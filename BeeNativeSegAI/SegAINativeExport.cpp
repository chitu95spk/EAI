#include <cmath>
#include "pch.h"

#include "SegAINativeExport.h"

#include "SegInferCore.h"
#include "SegTrainerCore.h"
#include "SegStructureCore.h"
#include "SegAIFileFormat.h"
#include "SegV2Core.h"
#include "SegBackboneFineTune.h"

#include <opencv2/core/ocl.hpp>

#include <algorithm>
#include <chrono>
#include <cstring>
#include <limits>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace {

struct TrainerHandle {
    std::string lastError;
    BeeSegAI::FeatureConfig cfg;
    BeeSegAI::SegTrainer trainer;
    cv::Rect roi;
    int profile = 0;
    int detail = 12;
    bool lastSampleOk = false;       // EAI-POS P3: the last V2 AddSample succeeded ...
    cv::Rect lastSampleValid;        // ... and used this crop of the submitted image (the ROI clipped to the image)
    std::unique_ptr<BeeSegAI::SegV2Model> v2;
    BeeSegAI::SegStructureModel structure;
};

struct InferHandle {
    std::string lastError;
    BeeSegAI::SegInferer inferer;
    // V135 diagnostic: last SEGAI_InferPredict export-layer timings (ms), last call on this handle.
    double exportCopyMs = 0;
    double exportTotalMs = 0;
};

cv::Mat WrapBgr(const uint8_t* bgr, int w, int h, int step)
{
    if (!bgr || w <= 0 || h <= 0 || static_cast<int64_t>(step) < static_cast<int64_t>(w) * 3) {
        return {};
    }
    return cv::Mat(h, w, CV_8UC3, const_cast<uint8_t*>(bgr), static_cast<size_t>(step));
}

cv::Mat WrapMask(const uint8_t* mask, int w, int h, int step)
{
    if (!mask || w <= 0 || h <= 0 || step < w) {
        return {};
    }
    return cv::Mat(h, w, CV_8UC1, const_cast<uint8_t*>(mask), static_cast<size_t>(step));
}

int CopyError(const std::string& message, char* buffer, int capacity) {
    if (!buffer || capacity <= 0) return -1;
    const size_t count = std::min(message.size(), static_cast<size_t>(capacity - 1));
    std::memcpy(buffer, message.data(), count); buffer[count] = 0; return static_cast<int>(count);
}

static const size_t V158MaxMetadataFieldBytes = 8u * 1024u;
static const size_t V158MaxMetadataCombinedBytes = 12u * 1024u;

bool IsValidUtf8(const char* value, size_t length) {
    for (size_t index = 0; index < length;) {
        const unsigned char first = static_cast<unsigned char>(value[index++]);
        if (first <= 0x7f) continue;
        int remaining = 0;uint32_t codePoint = 0;
        if (first >= 0xc2 && first <= 0xdf) { remaining = 1;codePoint = first & 0x1f; }
        else if (first >= 0xe0 && first <= 0xef) { remaining = 2;codePoint = first & 0x0f; }
        else if (first >= 0xf0 && first <= 0xf4) { remaining = 3;codePoint = first & 0x07; }
        else return false;
        if (index + static_cast<size_t>(remaining) > length) return false;
        for (int offset = 0; offset < remaining; ++offset) {
            const unsigned char next = static_cast<unsigned char>(value[index++]);
            if ((next & 0xc0) != 0x80) return false;
            codePoint = (codePoint << 6) | (next & 0x3f);
        }
        if ((remaining == 1 && codePoint < 0x80) || (remaining == 2 && codePoint < 0x800) ||
            (remaining == 3 && codePoint < 0x10000) || (codePoint >= 0xd800 && codePoint <= 0xdfff) || codePoint > 0x10ffff)
            return false;
    }
    return true;
}

bool ScanBoundedUtf8(const char* source, size_t maximumBytes, size_t& length) {
    if (!source) return false;
    length = 0;
    while (length <= maximumBytes && source[length] != 0) ++length;
    if (length > maximumBytes) return false;
    return IsValidUtf8(source, length);
}

void WriteSize(cv::FileStorage& storage, const char* name, const cv::Size& size) {
    storage << name << "{" << "width" << size.width << "height" << size.height << "}";
}

void WriteRect(cv::FileStorage& storage, const char* name, const cv::Rect& rect) {
    storage << name << "{" << "x" << rect.x << "y" << rect.y
            << "width" << rect.width << "height" << rect.height << "}";
}

void WriteDiagnosticMatrix(cv::FileStorage& storage, const char* name, const cv::Mat& value) {
    // FileStorage JSON + BASE64 preserves the actual native matrix type and bytes.
    storage << name << value;
}

std::string SerializeDiagnostic(const BeeSegAI::SegV2DiagnosticCapture& capture,
                                const std::string& callerProvenance,
                                const std::string& hostObservedFacts) {
    cv::FileStorage storage(".json", cv::FileStorage::WRITE | cv::FileStorage::MEMORY |
        cv::FileStorage::FORMAT_JSON | cv::FileStorage::BASE64);
    storage << "schema_version" << 1;
    storage << "scope" << "native-only";
    storage << "provenance" << "captured at production boundaries; no reconstructed stages";
    storage << "identity_verification" << "no cryptographic model/input verification is performed by native V158";
    storage << "caller_supplied_provenance_unverified" << callerProvenance;
    storage << "host_observed_runtime_facts" << hostObservedFacts;
    storage << "managed_exclusions" << "["
            << "managed preprocessing/crop" << "label-255 exclusion" << "managed minimum-area"
            << "Best Object" << "score gate" << "box extraction" << "IoU matching/final managed filtering" << "]";
    storage << "same_handle_serialization" << "caller must serialize diagnostic and inference calls on this handle";
    storage << "timings" << "not-performance-evidence";
    storage << "units_and_coordinate_conventions" << "{"
            << "scope" << "native V2 compact/single-scale diagnostic payload only"
            << "axes_origin" << "matrix x is column, y is row; origin is the top-left pixel index of each recorded matrix"
            << "pixel_index_and_rectangle_convention" << "x/y and centers are pixel-index coordinates; width/height are counts of discrete pixel cells; component rectangles cover array indices [x,x+width) and [y,y+height). No continuous pixel-edge or inverse-resize mapping is claimed."
            << "payload_limits" << "{"
                << "captured_matrix_bytes" << static_cast<double>(67108864)
                << "serialized_packet_bytes" << static_cast<double>(134217728)
                << "meaning" << "diagnostic payload limits only; they are not process-memory limits"
            << "}"
            << "byte_counts" << "{"
                << "capture_matrix_bytes" << "sum of deep-copied captured cv::Mat storage bytes"
                << "capture_matrix_limit_bytes" << "maximum captured diagnostic matrix bytes"
                << "out_byte_count" << "serialized UTF-8 JSON bytes excluding its allocated terminating NUL"
            << "}"
            << "size_and_rectangle_scalars" << "{"
                << "sizes" << "width/height in pixels of the named recorded stage"
                << "requested_effective_roi" << "x/y origin plus width/height in caller-image pixel-index coordinates"
                << "frame_letterbox" << "left/top and content width/height in network-canvas pixel cells"
            << "}"
            << "settings_and_flags" << "{"
                << "requested_speed_scale_effective_speed_scale_x_effective_speed_scale_y_detection_scale_minimum_detection_scale_maximum" << "dimensionless scale factors"
                << "ng_class_enabled_ok_class_enabled_original_input_compact_postprocess_multiscale_detection_winner_foreground_boundary_mode_morphology_applied_speed_downscaled" << "boolean values encoded as 0 or 1; winner_foreground_boundary_mode=0 is the legacy sum boundary and 1 is the winner-class boundary"
                << "morphology_kernel" << "square morphology kernel side length in compact-grid cells; 0 means morphology disabled"
            << "}"
            << "matrix_families" << "{"
                << "network_bg_network_ng_network_ok" << "CV_32F V2 network output planes in network_output_grid coordinates; normalized class values expected in [0,1], recorded without diagnostic clamping"
                << "restored_compact_before_class_suppression" << "CV_32F planes resized from network_output_grid to compact_size before NG/OK class gates; normalized class values expected in [0,1]"
                << "enabled_ng_enabled_ok" << "CV_32F compact-grid planes after class gates; values remain normalized [0,1] or are zeroed when the corresponding class is disabled"
                << "foreground_pre_morph_foreground_after_close_foreground_after_open" << "CV_8U compact-grid binary masks with 0=background and 255=foreground; after_close/after_open are unchanged copies when morphology is disabled"
                << "component_ids" << "CV_32S compact-grid connected-component labels: 0=background, positive values=indexed component IDs"
                << "component_stats" << "CV_32S rows indexed by component ID with OpenCV columns LEFT, TOP, WIDTH, HEIGHT, AREA"
                << "component_centers" << "CV_64F rows indexed by component ID with OpenCV columns center_x, center_y in compact-grid pixel-index coordinates"
                << "filtered_ng_filtered_ok_score_maps" << "CV_8U compact-grid class score maps after component area/class selection; 0=no class score, retained scores are [1,255]"
                << "restored_ng_restored_ok" << "CV_8U score maps after nearest-neighbor resize to v2_input_size"
                << "inferer_ng_inferer_ok" << "CV_8U score maps in check_size after optional speed-resize restoration"
                << "accepted_ng_after_inferer_threshold" << "CV_8U binary NG mask in check_size after the inferer threshold: 0=rejected, 255=accepted"
                << "encoding" << "OpenCV FileStorage JSON/Base64 Mat encoding retains each recorded matrix dtype, rows, columns, and bytes"
            << "}"
            << "component_scalars" << "{"
                << "x_y_width_height_center_area_area_cutoff" << "all are compact-grid units: x/y and center_x/center_y use pixel-index coordinates; width/height are cell counts; area and area_cutoff are compact-grid pixel counts"
                << "ng_mean_ok_mean_foreground_threshold_inferer_threshold_minimum_ratio_minimum_fraction" << "normalized scalar values in [0,1] when supplied by the V2 path"
                << "score" << "U8 component score in [1,255] when score_evaluated=1; 0 means no score is available, not a retained score"
                << "selected_class" << "0=no class available, 1=NG, 2=OK; a rejected component may retain a diagnostic observation class while production still rejects it"
                << "accepted_rejection_score_evaluated" << "accepted and score_evaluated are 0/1; rejection names the production rejection stage/reason when not accepted"
            << "}"
            << "transformation_sequence" << "["
                << "caller image -> requested/effective ROI and check"
                << "optional rounded speed resize to speed_reduced_size"
                << "Prepare letterbox to network_canvas_size using recorded frame_left/frame_top/content bounds"
                << "network output grid"
                << "resize to compact_size"
                << "nearest-neighbor resize to V2 input/check stages"
                << "ROI placement into caller-image outputs when ROI is not full image"
                << "Recorded stages describe the executed path only; no exact inverse coordinate formula is asserted beyond these values."
            << "]"
        << "}";
    storage << "capture_matrix_bytes" << static_cast<double>(capture.matrixBytes);
    storage << "capture_matrix_limit_bytes" << static_cast<double>(BeeSegAI::SegV2DiagnosticCapture::MaxMatrixBytes);
    WriteSize(storage, "caller_image_size", capture.callerImageSize);
    WriteRect(storage, "requested_roi", capture.requestedRoi);
    WriteRect(storage, "effective_roi", capture.effectiveRoi);
    WriteSize(storage, "check_size", capture.checkSize);
    WriteSize(storage, "speed_reduced_size", capture.speedReducedSize);
    WriteSize(storage, "v2_input_size", capture.v2InputSize);
    WriteSize(storage, "network_canvas_size", capture.networkCanvasSize);
    WriteSize(storage, "model_input_size", capture.modelInputSize);
    WriteSize(storage, "network_output_grid", capture.networkOutputGrid);
    WriteSize(storage, "compact_size", capture.compactSize);
    storage << "frame_letterbox" << "{" << "left" << capture.frameLeft << "top" << capture.frameTop
            << "content_width" << capture.frameContentWidth << "content_height" << capture.frameContentHeight << "}";
    storage << "backend" << "{" << "requested" << capture.requestedBackend
            << "effective_device" << capture.effectiveDevice << "}";
    storage << "settings" << "{" << "requested_speed_scale" << capture.requestedSpeedScale
            << "speed_downscaled" << static_cast<int>(capture.speedDownscaled)
            << "effective_speed_scale_x" << capture.effectiveSpeedScaleX
            << "effective_speed_scale_y" << capture.effectiveSpeedScaleY
            << "foreground_threshold" << capture.foregroundThreshold
            << "inferer_threshold" << capture.infererThreshold
            << "minimum_ratio" << capture.minimumRatio
            << "minimum_fraction" << capture.minimumFraction
            << "minimum_area_cutoff_ceiling" << capture.areaCutoff
            << "detection_scale_minimum" << capture.detectionScaleMinimum
            << "detection_scale_maximum" << capture.detectionScaleMaximum
            << "ng_class_enabled" << static_cast<int>(capture.ngClassEnabled)
            << "ok_class_enabled" << static_cast<int>(capture.okClassEnabled)
            << "original_input" << static_cast<int>(capture.originalInput)
            << "compact_postprocess" << static_cast<int>(capture.compactPostprocess)
            << "multiscale_detection" << static_cast<int>(capture.multiScaleDetection)
            << "winner_foreground_boundary_mode" << static_cast<int>(capture.winnerForegroundBoundary)
            << "morphology_applied" << static_cast<int>(capture.morphologyApplied)
            << "morphology_kernel" << capture.morphologyKernel << "}";
    WriteDiagnosticMatrix(storage, "network_bg", capture.networkBg);
    WriteDiagnosticMatrix(storage, "network_ng", capture.networkNg);
    WriteDiagnosticMatrix(storage, "network_ok", capture.networkOk);
    WriteDiagnosticMatrix(storage, "restored_compact_bg_before_class_suppression", capture.restoredCompactBg);
    WriteDiagnosticMatrix(storage, "restored_compact_ng_before_class_suppression", capture.restoredCompactNg);
    WriteDiagnosticMatrix(storage, "restored_compact_ok_before_class_suppression", capture.restoredCompactOk);
    WriteDiagnosticMatrix(storage, "enabled_ng", capture.enabledNg);
    WriteDiagnosticMatrix(storage, "enabled_ok", capture.enabledOk);
    WriteDiagnosticMatrix(storage, "foreground_pre_morph", capture.foregroundPreMorph);
    WriteDiagnosticMatrix(storage, "foreground_after_close", capture.foregroundAfterClose);
    WriteDiagnosticMatrix(storage, "foreground_after_open", capture.foregroundAfterOpen);
    WriteDiagnosticMatrix(storage, "component_ids", capture.componentIds);
    WriteDiagnosticMatrix(storage, "component_stats", capture.componentStats);
    WriteDiagnosticMatrix(storage, "component_centers", capture.componentCenters);
    WriteDiagnosticMatrix(storage, "filtered_ng_score_map", capture.filteredNg);
    WriteDiagnosticMatrix(storage, "filtered_ok_score_map", capture.filteredOk);
    WriteDiagnosticMatrix(storage, "restored_ng", capture.restoredNg);
    WriteDiagnosticMatrix(storage, "restored_ok", capture.restoredOk);
    WriteDiagnosticMatrix(storage, "inferer_ng", capture.infererNg);
    WriteDiagnosticMatrix(storage, "inferer_ok", capture.infererOk);
    WriteDiagnosticMatrix(storage, "accepted_ng_after_inferer_threshold", capture.acceptedNg);
    storage << "components" << "[";
    for (const auto& component : capture.components) {
        storage << "{" << "id" << component.id << "x" << component.x << "y" << component.y
                << "width" << component.width << "height" << component.height << "area" << component.area
                << "center_x" << component.centerX << "center_y" << component.centerY
                << "ng_mean" << component.ngMean << "ok_mean" << component.okMean
                << "selected_class" << component.selectedClass << "score" << static_cast<int>(component.score)
                << "score_evaluated" << static_cast<int>(component.scoreEvaluated)
                << "area_cutoff" << component.areaCutoff << "accepted" << static_cast<int>(component.accepted)
                << "rejection" << component.rejection << "}";
    }
    storage << "]";
    std::string packet = storage.releaseAndGetString();
    static const size_t MaxPacketBytes = size_t(128) * 1024 * 1024;
    if (packet.size() > MaxPacketBytes || packet.size() > static_cast<size_t>(std::numeric_limits<int32_t>::max()))
        throw std::runtime_error("V158 diagnostic serialized-packet limit exceeded");
    return packet;
}
} // namespace

SAPI int32_t SEGAI_TrainerGetLastError(void* handle, char* buffer, int32_t capacity) {
    auto* trainer = static_cast<TrainerHandle*>(handle);
    if (!trainer) return -1;
    return CopyError(trainer->lastError, buffer, capacity);
}
SAPI int32_t SEGAI_InferGetLastError(void* handle, char* buffer, int32_t capacity) {
    auto* infer = static_cast<InferHandle*>(handle);
    if (!infer) return -1;
    return CopyError(infer->lastError, buffer, capacity);
}
SAPI int32_t SEGAI_InferHasNormalClass(void* handle) {
    auto* infer = static_cast<InferHandle*>(handle);
    return infer ? (infer->inferer.HasNormalClass() ? 1 : 0) : -1;
}
SAPI int32_t SEGAI_InferHasNgClass(void* handle) {
    auto* infer = static_cast<InferHandle*>(handle);
    return infer ? (infer->inferer.HasNgClass() ? 1 : 0) : -1;
}

SAPI void* SEGAI_TrainerCreate()
{
    try {
        std::unique_ptr<TrainerHandle> handle(new TrainerHandle());
        handle->cfg.roi = cv::Rect();
        handle->trainer.Configure(handle->cfg);
        return handle.release();
    } catch (...) {
        return nullptr;
    }
}

SAPI void SEGAI_TrainerDestroy(void* handle)
{
    delete static_cast<TrainerHandle*>(handle);
}

SAPI int SEGAI_TrainerSetROI(void* handle, int x, int y, int w, int h)
{
    TrainerHandle* trainer = static_cast<TrainerHandle*>(handle);
    if (!trainer || w <= 0 || h <= 0) {
        return -1;
    }

    trainer->roi = cv::Rect(x, y, w, h);
    trainer->cfg.roi = trainer->roi;
    trainer->trainer.Configure(trainer->cfg);
    return 0;
}

SAPI int32_t SEGAI_TrainerSetProfile(void* handle, int32_t profile, int32_t detail)
{
    auto* trainer = static_cast<TrainerHandle*>(handle);
    if (!trainer || (profile != 0 && profile != 1)) return -1;
    if (!trainer->trainer.Samples().empty() || trainer->structure.DefectPixels() || trainer->structure.NormalPixels()) return -2;
    trainer->profile = profile;
    trainer->detail = detail;
    trainer->structure = BeeSegAI::SegStructureModel();
    trainer->structure.Configure(detail);
    return 0;
}

SAPI int SEGAI_TrainerConfigureV2(void* handle, const wchar_t* path)
{
    auto* trainer = static_cast<TrainerHandle*>(handle);
    if (!trainer || !path) return -1;
    if (!trainer->trainer.Samples().empty() || trainer->structure.DefectPixels() || trainer->structure.NormalPixels() ||
        (trainer->v2 && (trainer->v2->DefectPixels() || trainer->v2->NormalPixels()))) return -2;
    trainer->lastError.clear();
    try {
        std::unique_ptr<BeeSegAI::SegV2Model> model(new BeeSegAI::SegV2Model());
        model->Configure(path); trainer->v2 = std::move(model); trainer->profile = 2; return 0;
    } catch (const std::exception& e) { trainer->lastError = std::string("V2 configure: ") + e.what(); return -8; }
    catch (...) { trainer->lastError = "V2 configure: unknown native error";return -8; }
}

SAPI int SEGAI_TrainerAddSample(void* handle,
                                const uint8_t* bgr,
                                int w,
                                int h,
                                int step,
                                const uint8_t* mask,
                                int maskStep)
{
    TrainerHandle* trainer = static_cast<TrainerHandle*>(handle);
    if (!trainer) {
        return -1;
    }

    trainer->lastError.clear();
    if (!bgr || !mask || w <= 0 || h <= 0 || static_cast<int64_t>(step) < static_cast<int64_t>(w)*3 || maskStep < w) { trainer->lastError="AddSample: invalid BGR8 image/mask dimensions or stride";return -2; }
    cv::Mat bgrMat = WrapBgr(bgr, w, h, step);
    cv::Mat maskMat = WrapMask(mask, w, h, maskStep);
    if (bgrMat.empty() || maskMat.empty()) {
        return -2;
    }

    const cv::Rect roi = trainer->roi.area() > 0 ? trainer->roi : cv::Rect(0, 0, w, h);
    if (trainer->profile == 2) {
        trainer->lastSampleOk = false;
        try {
            cv::Rect valid=roi & cv::Rect(0,0,w,h);
            if(valid.empty())throw std::runtime_error("Check has no valid pixels");
            const int rc=trainer->v2->AddSample(bgrMat(valid), maskMat(valid));
            trainer->lastSampleOk = rc == 0; trainer->lastSampleValid = valid; // EAI-POS P3: frame of the instance label coordinates
            if(rc)trainer->lastError="V2 AddSample: invalid image or mask";return rc;
        } catch (const std::exception& e) { trainer->lastError="V2 AddSample input="+std::to_string(w)+"x"+std::to_string(h)+": "+e.what();return -4; }
        catch (...) { trainer->lastError="V2 AddSample: unknown native error";return -4; }
    }
    if (trainer->profile == 1) {
        try {
            cv::Rect valid = roi & cv::Rect(0, 0, w, h);
            if (valid.area() <= 0) return -2;
            return trainer->structure.AddSample(bgrMat(valid), maskMat(valid));
        } catch (...) { return -4; }
    }
    return trainer->trainer.AddSample(bgrMat, maskMat, roi);
}

SAPI void SEGAI_TrainerClearSamples(void* handle)
{
    TrainerHandle* trainer = static_cast<TrainerHandle*>(handle);
    if (!trainer) {
        return;
    }

    if (trainer->v2) trainer->v2->ClearSamples();
    trainer->lastSampleOk = false;
    trainer->trainer = BeeSegAI::SegTrainer();
    trainer->trainer.Configure(trainer->cfg);
    trainer->structure = BeeSegAI::SegStructureModel();
    trainer->structure.Configure(trainer->detail);
}

SAPI int SEGAI_TrainerTrain(void* handle,
                            int numTrees,
                            int maxDepth,
                            int minSampleCount,
                            void(*progressCb)(int, void*),
                            void* userData,
                            const volatile int* cancelFlag)
{
    TrainerHandle* trainer = static_cast<TrainerHandle*>(handle);
    if (!trainer) {
        return -1;
    }

    trainer->lastError.clear();
    if (trainer->profile == 2) {
        try {
            const int rc=trainer->v2->Train([=](int p) { if (progressCb) progressCb(p, userData); }, cancelFlag);
            if(rc)trainer->lastError=trainer->v2->LastError();return rc;
        } catch (const std::exception& e) { trainer->lastError=std::string("V2 train: ")+e.what();return -8; }
        catch (...) { trainer->lastError="V2 train: unknown native error";return -8; }
    }
    if (trainer->profile == 1) {
        if (cancelFlag && *cancelFlag) return -10;
        if (!trainer->structure.Ready()) return -2;
        if (progressCb) progressCb(100, userData);
        return 0;
    }
    return trainer->trainer.Train(
        numTrees,
        maxDepth,
        minSampleCount,
        [progressCb, userData](int progress) {
            if (progressCb) {
                progressCb(progress, userData);
            }
        },
        cancelFlag);
}

SAPI int SEGAI_TrainerSave(void* handle, const wchar_t* path, float defectThreshold, uint32_t minDefectArea)
{
    TrainerHandle* trainer = static_cast<TrainerHandle*>(handle);
    if (!trainer || !path) {
        return -1;
    }

    trainer->lastError.clear();
    if (trainer->profile == 2) {
        const bool saved=trainer->v2->Save(path, defectThreshold, minDefectArea);
        if(!saved)trainer->lastError=trainer->v2->LastError();return saved?0:-5;
    }
    if (trainer->profile == 1)
        return BeeSegAI::SaveStructuralSegai(path, trainer->structure, defectThreshold, minDefectArea) ? 0 : -5;
    return trainer->trainer.SaveModel(path, defectThreshold, minDefectArea) ? 0 : -5;
}

SAPI int SEGAI_TrainerSampleCount(void* handle, int* outDefectPixels, int* outNormalPixels)
{
    TrainerHandle* trainer = static_cast<TrainerHandle*>(handle);
    if (!trainer || !outDefectPixels || !outNormalPixels) {
        return -1;
    }

    if (trainer->profile == 2) { *outDefectPixels = trainer->v2->DefectPixels(); *outNormalPixels = trainer->v2->NormalPixels(); return 0; }
    int defect = 0;
    int normal = 0;
    if (trainer->profile == 1) {
        *outDefectPixels = trainer->structure.DefectPixels(); *outNormalPixels = trainer->structure.NormalPixels();
        return 0;
    }
    for (const BeeSegAI::SegTrainingSample& sample : trainer->trainer.Samples()) {
        defect += static_cast<int>(sample.defectPixels.size());
        normal += static_cast<int>(sample.normalPixels.size());
    }

    *outDefectPixels = defect;
    *outNormalPixels = normal;
    return 0;
}

SAPI void* SEGAI_InferCreate()
{
    try {
        return new InferHandle();
    } catch (...) {
        return nullptr;
    }
}

SAPI void SEGAI_InferDestroy(void* handle)
{
    delete static_cast<InferHandle*>(handle);
}

SAPI int SEGAI_InferLoad(void* handle, const wchar_t* path)
{
    InferHandle* infer = static_cast<InferHandle*>(handle);
    if (!infer || !path) {
        return -1;
    }

    infer->lastError.clear();
    try {
        if(infer->inferer.LoadModel(path))return 0;
        infer->lastError=infer->inferer.LastError();
        if(infer->lastError.empty())infer->lastError="Model load: invalid, missing or incompatible model";
        return -5;
    } catch(const std::exception& e) { infer->lastError=std::string("Model load: ")+e.what();return -5; }
    catch(...) { infer->lastError="Model load: unknown native error";return -5; }
}

SAPI int SEGAI_InferV2Options(void* handle, float foreground, float ratio, int morphology)
{
    if (!handle) return -1;
    try { static_cast<InferHandle*>(handle)->inferer.SetV2Options(foreground, ratio, morphology); return 0; } catch (...) { return -2; }
}

SAPI int SEGAI_InferSetObjectScale(void* handle, float minimum, float maximum, int multiScale)
{
    if (!handle || !std::isfinite(minimum) || !std::isfinite(maximum) || minimum < .5f || maximum > 2.f || minimum > maximum || (multiScale != 0 && multiScale != 1)) return -2;
    try { static_cast<InferHandle*>(handle)->inferer.SetObjectScale(minimum, maximum, multiScale != 0); return 0; }
    catch (const std::exception& e) { static_cast<InferHandle*>(handle)->lastError = std::string("Object scale: ") + e.what(); return -2; }
    catch (...) { return -2; }
}

SAPI int32_t SEGAI_InferSetV2BoundaryMode(void* handle, int32_t mode)
{
    InferHandle* infer = static_cast<InferHandle*>(handle);
    if (!infer || (mode != 0 && mode != 1)) return -2;
    try {
        infer->inferer.SetV2BoundaryMode(mode != 0);
        return 0;
    } catch (const std::exception& e) {
        infer->lastError = std::string("V2 boundary mode: ") + e.what();
        return -2;
    } catch (...) {
        infer->lastError = "V2 boundary mode: unknown native error";
        return -2;
    }
}

SAPI int32_t SEGAI_InferSetV2ShapeCleanup(void* handle, float fraction)
{
    InferHandle* infer = static_cast<InferHandle*>(handle);
    if (!infer) return -1;
    if (!std::isfinite(fraction) || fraction < 0.f || fraction > .25f) return -2;
    try {
        infer->inferer.SetV2ShapeCleanup(fraction);
        return 0;
    } catch (const std::exception& e) {
        infer->lastError = std::string("V2 shape cleanup: ") + e.what();
        return -2;
    } catch (...) {
        infer->lastError = "V2 shape cleanup: unknown native error";
        return -2;
    }
}

SAPI int32_t SEGAI_V2ShapeCleanupApply(uint8_t* ng, uint8_t* ok, int32_t w, int32_t h, int32_t step, float fraction)
{
    if (!ng || w <= 0 || h <= 0 || step < w || !std::isfinite(fraction) || fraction < 0.f || fraction > .25f) return -2;
    try {
        cv::Mat ngMap(h, w, CV_8U, ng, static_cast<size_t>(step));
        if (ok) {
            cv::Mat okMap(h, w, CV_8U, ok, static_cast<size_t>(step));
            return BeeSegAI::ApplyV2ShapeCleanup(ngMap, okMap, fraction);
        }
        cv::Mat zeroOk = cv::Mat::zeros(h, w, CV_8U); // temporary; never written back
        return BeeSegAI::ApplyV2ShapeCleanup(ngMap, zeroOk, fraction);
    } catch (...) {
        return -3;
    }
}

SAPI int SEGAI_InferSetGpu(void* handle, int enable)
{
    InferHandle* infer = static_cast<InferHandle*>(handle);
    if (!infer) {
        return -1;
    }

    infer->inferer.SetGpuEnabled(enable != 0);
    return 0;
}

SAPI int SEGAI_InferGetGpuAvailable()
{
    try {
        return BeeSegAI::StructuralGpuAvailable() ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

SAPI int SEGAI_InferSetAssistMode(void* handle, int32_t enable)
{
    auto* infer = static_cast<InferHandle*>(handle);
    if (!infer) return -1;
    infer->inferer.SetAssistMode(enable != 0);
    return 0;
}

SAPI int SEGAI_InferSetAngles(void* handle, float step, float range)
{
    if (!handle || !std::isfinite(step) || !std::isfinite(range) || step < 0 || step > 45 || range < 0 || range > 180) return -1;
    static_cast<InferHandle*>(handle)->inferer.SetAngles(step, range); return 0;
}
SAPI int SEGAI_InferGetLastBackend(void* handle)
{
    auto* infer = static_cast<InferHandle*>(handle);
    return infer ? (infer->inferer.LastUsedGpu() ? 1 : 0) : -1;
}

SAPI int SEGAI_InferPredict(void* handle,
                            const uint8_t* bgr,
                            int w,
                            int h,
                            int step,
                            int roiX,
                            int roiY,
                            int roiW,
                            int roiH,
                            float threshold,
                            uint8_t** outMaskPtr,
                            int* outMaskW,
                            int* outMaskH,
                            float* outScore)
{
    InferHandle* infer = static_cast<InferHandle*>(handle);
    if (!infer || !outMaskPtr || !outMaskW || !outMaskH || !outScore) {
        return -1;
    }

    infer->lastError.clear();
    *outMaskPtr = nullptr;
    *outMaskW = 0;
    *outMaskH = 0;
    *outScore = 0.0f;
    infer->exportCopyMs = 0;
    infer->exportTotalMs = 0;
    const auto timeStart = std::chrono::steady_clock::now();

    try {
        cv::Mat bgrMat = WrapBgr(bgr, w, h, step);
        if (bgrMat.empty()) { infer->lastError="Predict: invalid BGR8 image dimensions or stride";return -2; }
        cv::Mat mask;
        float score = 0.0f;
        const int rc = infer->inferer.Predict(bgrMat, cv::Rect(roiX, roiY, roiW, roiH), threshold, mask, score);
        if (rc != 0) {
            infer->lastError=infer->inferer.LastError();
            if(infer->lastError.empty())infer->lastError="Predict backend="+std::string(infer->inferer.GpuEnabled()?"GPU":"CPU")+" input="+std::to_string(w)+"x"+std::to_string(h)+" failed ("+std::to_string(rc)+")";
            return rc;
        }
        const auto timeCopy = std::chrono::steady_clock::now();
        const size_t byteCount = mask.total();
        uint8_t* buffer = new uint8_t[byteCount];
        std::memcpy(buffer, mask.data, byteCount);
        *outMaskPtr = buffer;
        *outMaskW = mask.cols;
        *outMaskH = mask.rows;
        *outScore = score;
        const auto timeEnd = std::chrono::steady_clock::now();
        infer->exportCopyMs = std::chrono::duration<double, std::milli>(timeEnd - timeCopy).count();
        infer->exportTotalMs = std::chrono::duration<double, std::milli>(timeEnd - timeStart).count();
        return 0;
    } catch (const std::exception& e) {
        infer->lastError="Predict backend="+std::string(infer->inferer.GpuEnabled()?"GPU":"CPU")+" input="+std::to_string(w)+"x"+std::to_string(h)+": "+e.what();return -3;
    } catch (...) {
        infer->lastError="Predict: unknown native error";return -3;
    }
}

SAPI int32_t SEGAI_InferPredictDiagnosticV1(void* handle,
                                             const uint8_t* bgr,
                                             int32_t w,
                                             int32_t h,
                                             int32_t step,
                                             int32_t roiX,
                                             int32_t roiY,
                                             int32_t roiW,
                                             int32_t roiH,
                                             float threshold,
                                             const char* callerProvenanceUtf8,
                                             const char* hostObservedFactsUtf8,
                                             uint8_t** outUtf8,
                                             uint64_t* outByteCount)
{
    if (outUtf8) *outUtf8 = nullptr;
    if (outByteCount) *outByteCount = 0;
    if (!outUtf8 || !outByteCount || !callerProvenanceUtf8 || !hostObservedFactsUtf8) return -1;
    InferHandle* infer = static_cast<InferHandle*>(handle);
    if (!infer) return -1;
    infer->lastError.clear();
    try {
        size_t callerLength = 0, hostLength = 0;
        if (!ScanBoundedUtf8(callerProvenanceUtf8, V158MaxMetadataFieldBytes, callerLength)) {
            infer->lastError = "V158 diagnostic: caller provenance must be terminated valid UTF-8 within 8192 bytes";return -2;
        }
        if (!ScanBoundedUtf8(hostObservedFactsUtf8, V158MaxMetadataFieldBytes, hostLength) ||
            callerLength > V158MaxMetadataCombinedBytes - hostLength) {
            infer->lastError = "V158 diagnostic: host metadata must be terminated valid UTF-8 and combined metadata must not exceed 12288 bytes";return -2;
        }
        const std::string callerProvenance(callerProvenanceUtf8, callerLength);
        const std::string hostObservedFacts(hostObservedFactsUtf8, hostLength);
        cv::Mat bgrMat = WrapBgr(bgr, w, h, step);
        if (bgrMat.empty()) { infer->lastError = "V158 diagnostic: invalid BGR8 image dimensions or stride"; return -2; }
        BeeSegAI::SegV2DiagnosticCapture capture;
        cv::Mat mask;float score = 0.0f;
        const int rc = infer->inferer.PredictDiagnostic(bgrMat, cv::Rect(roiX, roiY, roiW, roiH), threshold, mask, score, capture);
        if (rc != 0) {
            infer->lastError = infer->inferer.LastError();
            if (infer->lastError.empty()) infer->lastError = "V158 diagnostic inference failed (" + std::to_string(rc) + ")";
            return rc;
        }
        std::string packet = SerializeDiagnostic(capture, callerProvenance, hostObservedFacts);
        std::unique_ptr<uint8_t[]> buffer(new uint8_t[packet.size() + 1]);
        std::memcpy(buffer.get(), packet.data(), packet.size());buffer[packet.size()] = 0;
        *outByteCount = static_cast<uint64_t>(packet.size());
        *outUtf8 = buffer.release();
        return 0;
    } catch (const std::exception& e) {
        infer->lastError = std::string("V158 diagnostic: ") + e.what();return -8;
    } catch (...) {
        infer->lastError = "V158 diagnostic: unknown native error";return -8;
    }
}

// V135 diagnostic getter. Fills up to `capacity` doubles (milliseconds) and returns the number
// written (13 when capacity >= 13), or -1 for invalid arguments. Values describe the last
// SEGAI_InferPredict call on this handle only; they are not synchronized, so read them on the
// same thread that called Predict. Non-V2 models report zeros for the V2 slots.
// Index layout (keep in sync with NativeSegAIInferer.PhaseTimingNames):
//  0 v2_device   1 v2_prepare   2 v2_tensor   3 v2_infer   4 v2_upsample   5 v2_compile
//  6 v2_ccscore  7 v2_nearest   8 v2_total    9 inferer_tail  10 inferer_total
// 11 export_copy 12 export_total
SAPI int32_t SEGAI_InferGetPhaseTimings(void* handle, double* outValues, int32_t capacity)
{
    if (!handle || !outValues || capacity <= 0) return -1;
    const auto* infer = static_cast<InferHandle*>(handle);
    double values[13] = {};
    const BeeSegAI::SegV2Timings* v2 = infer->inferer.V2Timings();
    if (v2) {
        values[0] = v2->deviceMs; values[1] = v2->prepareMs; values[2] = v2->tensorMs;
        values[3] = v2->inferMs; values[4] = v2->upsampleMs; values[5] = v2->compileMs;
        values[6] = v2->ccScoreMs; values[7] = v2->nearestMs; values[8] = v2->totalMs;
    }
    values[9] = infer->inferer.LastTailMs();
    values[10] = infer->inferer.LastTotalMs();
    values[11] = infer->exportCopyMs;
    values[12] = infer->exportTotalMs;
    const int32_t count = capacity < 13 ? capacity : 13;
    for (int32_t i = 0; i < count; ++i) outValues[i] = values[i];
    return count;
}

// EASYAI_SEGAI_SPLIT_TIMING diagnostic getter; layout documented in SegAINativeExport.h. Reads the same handle-owned
// timings as SEGAI_InferGetPhaseTimings, on the same thread as Predict.
SAPI int32_t SEGAI_InferGetSplitTimings(void* handle, double* outValues, int32_t capacity)
{
    if (!handle || !outValues || capacity <= 0) return -1;
    const auto* infer = static_cast<InferHandle*>(handle);
    const BeeSegAI::SegV2Timings* v2 = infer->inferer.V2Timings();
    if (!v2 || !v2->backboneMeasured) return 0;
    const double head = v2->inferMs - v2->backboneMs;
    const double values[3] = { v2->backboneMs, head > 0 ? head : 0.0, v2->inferMs };
    const int32_t count = capacity < 3 ? capacity : 3;
    for (int32_t i = 0; i < count; ++i) outValues[i] = values[i];
    return count;
}
SAPI int32_t SEGAI_GetSplitTimingApiVersion()
{
    return 1;
}

SAPI int32_t SEGAI_InferGetConfidence(void* handle, uint8_t** outPtr, int32_t* outW, int32_t* outH)
{
    if (!outPtr || !outW || !outH) return -1;
    *outPtr = nullptr; *outW = 0; *outH = 0;
    if (!handle) return -1;
    try {
        const cv::Mat& confidence = static_cast<InferHandle*>(handle)->inferer.Confidence();
        if (confidence.empty() || !confidence.isContinuous()) return -2;
        uint8_t* buffer = new uint8_t[confidence.total()];
        std::memcpy(buffer, confidence.data, confidence.total());
        *outPtr = buffer; *outW = confidence.cols; *outH = confidence.rows;
        return 0;
    } catch (...) { return -3; }
}
SAPI int32_t SEGAI_InferGetNormalConfidence(void* handle, uint8_t** outPtr, int32_t* outW, int32_t* outH)
{
    if (!outPtr || !outW || !outH) return -1;
    *outPtr = nullptr; *outW = 0; *outH = 0;
    if (!handle) return -1;
    try {
        const cv::Mat& confidence = static_cast<InferHandle*>(handle)->inferer.NormalConfidence();
        if (confidence.empty() || !confidence.isContinuous()) return -2;
        uint8_t* buffer = new uint8_t[confidence.total()];
        std::memcpy(buffer, confidence.data, confidence.total());
        *outPtr = buffer; *outW = confidence.cols; *outH = confidence.rows;
        return 0;
    } catch (...) { return -3; }
}
SAPI int32_t SEGAI_InferSetSpeedMode(void* handle, int32_t mode)
{
    if (!handle || (mode != 0 && mode != 1)) return -1;
    static_cast<InferHandle*>(handle)->inferer.SetHighSpeed(mode == 1);
    return 0;
}
SAPI int32_t SEGAI_InferSetV2SpeedScale(void* handle, float scale)
{
    if (!handle || !std::isfinite(scale) || scale < .25f || scale > 1.f) return -1;
    static_cast<InferHandle*>(handle)->inferer.SetV2SpeedScale(scale);
    return 0;
}
SAPI void SEGAI_FreeBuffer(void* ptr)
{
    delete[] static_cast<uint8_t*>(ptr);
}
SAPI int32_t SEGAI_GetClassApiVersion()
{
    return 1;
}
SAPI int32_t SEGAI_TrainerSetClassTable(void* handle, const uint8_t* table, int32_t bytes)
{
    auto* trainer = static_cast<TrainerHandle*>(handle);
    if (!trainer) return -1;
    trainer->lastError.clear();
    if (trainer->profile != 2 || !trainer->v2) {
        trainer->lastError = "SetClassTable: requires a V2 trainer (call SEGAI_TrainerConfigureV2 first)";
        return -2;
    }
    try {
        std::string error;
        const int rc = trainer->v2->SetClassTable(table, bytes > 0 ? static_cast<size_t>(bytes) : 0u, error);
        if (rc != 0) trainer->lastError = "SetClassTable: " + error;
        return rc;
    } catch (const std::exception& e) {
        trainer->lastError = std::string("SetClassTable: ") + e.what();
        return -3;
    } catch (...) {
        trainer->lastError = "SetClassTable: unknown native error";
        return -3;
    }
}
SAPI int32_t SEGAI_InferGetClassTable(void* handle, uint8_t* buffer, int32_t capacity)
{
    auto* infer = static_cast<InferHandle*>(handle);
    if (!infer) return -1;
    try {
        BeeSegAI::SegClassTable table;
        if (!infer->inferer.GetClassTable(table)) return -4;
        std::vector<unsigned char> bytes;
        std::string error;
        if (!BeeSegAI::EncodeClassTable(table, bytes, error)) return -3;
        const int32_t required = static_cast<int32_t>(bytes.size());
        if (buffer && capacity >= required) std::memcpy(buffer, bytes.data(), bytes.size());
        return required;
    } catch (...) {
        return -3;
    }
}
SAPI int32_t SEGAI_InferPredictMulti(void* handle,
                                     const uint8_t* bgr,
                                     int32_t w,
                                     int32_t h,
                                     int32_t step,
                                     int32_t roiX,
                                     int32_t roiY,
                                     int32_t roiW,
                                     int32_t roiH,
                                     float threshold,
                                     uint8_t* outLabel,
                                     int32_t labelStep,
                                     uint8_t* outScore,
                                     int32_t scoreStep,
                                     float* outClassFraction,
                                     int32_t classCapacity)
{
    auto* infer = static_cast<InferHandle*>(handle);
    if (!infer) return -1;
    infer->lastError.clear();
    if (!outLabel || !outClassFraction || classCapacity < 0 || w <= 0 || h <= 0 || labelStep < w || (outScore && scoreStep < w)) {
        infer->lastError = "PredictMulti: invalid output buffer, stride or capacity argument";
        return -1;
    }
    infer->exportCopyMs = 0;
    infer->exportTotalMs = 0;
    try {
        cv::Mat bgrMat = WrapBgr(bgr, w, h, step);
        if (bgrMat.empty()) { infer->lastError = "PredictMulti: invalid BGR8 image dimensions or stride"; return -2; }
        cv::Mat label, score;
        std::vector<float> fractions;
        const int rc = infer->inferer.PredictMulti(bgrMat, cv::Rect(roiX, roiY, roiW, roiH), threshold, classCapacity, label, score, fractions);
        if (rc != 0) {
            infer->lastError = infer->inferer.LastError();
            if (infer->lastError.empty()) infer->lastError = "PredictMulti failed (" + std::to_string(rc) + ")";
            return rc;
        }
        for (int y = 0; y < h; ++y) std::memcpy(outLabel + static_cast<size_t>(y) * labelStep, label.ptr<uint8_t>(y), static_cast<size_t>(w));
        if (outScore) for (int y = 0; y < h; ++y) std::memcpy(outScore + static_cast<size_t>(y) * scoreStep, score.ptr<uint8_t>(y), static_cast<size_t>(w));
        for (size_t i = 0; i < fractions.size(); ++i) outClassFraction[i] = fractions[i];
        return 0;
    } catch (const std::exception& e) {
        infer->lastError = std::string("PredictMulti: ") + e.what();
        return -3;
    } catch (...) {
        infer->lastError = "PredictMulti: unknown native error";
        return -3;
    }
}
SAPI int32_t SEGAI_GetInstanceApiVersion()
{
    return 1;
}
SAPI int32_t SEGAI_TrainerAddInstances(void* handle, const SegInstanceV1* items, int32_t count, int32_t structSize)
{
    auto* trainer = static_cast<TrainerHandle*>(handle);
    if (!trainer) return -1;
    trainer->lastError.clear();
    if (count < 0 || (count > 0 && (!items || structSize < static_cast<int32_t>(sizeof(SegInstanceV1))))) {
        trainer->lastError = "AddInstances: invalid item array, count or structSize (the stride must be at least 32 bytes)";
        return -1;
    }
    if (trainer->profile != 2 || !trainer->v2) {
        trainer->lastError = "AddInstances: requires a V2 trainer (call SEGAI_TrainerConfigureV2 first)";
        return -2;
    }
    if (!trainer->lastSampleOk) {
        trainer->lastError = "AddInstances: add a sample with SEGAI_TrainerAddSample first";
        return -2;
    }
    try {
        // Label coordinates are relative to the submitted image; the trainer stored the ROI crop, so shift them into the crop.
        const float shiftX = static_cast<float>(trainer->lastSampleValid.x), shiftY = static_cast<float>(trainer->lastSampleValid.y);
        std::vector<BeeSegAI::InstanceLabel> labels;
        labels.reserve(static_cast<size_t>(count));
        for (int32_t i = 0; i < count; ++i) {
            const SegInstanceV1& item = *reinterpret_cast<const SegInstanceV1*>(reinterpret_cast<const uint8_t*>(items) + static_cast<size_t>(i) * static_cast<size_t>(structSize));
            if (item.kind != 0 && item.kind != 1) {
                trainer->lastError = "AddInstances: item " + std::to_string(i) + " has an unknown kind (0 = rotated box, 1 = circle); nothing was attached";
                return -3;
            }
            BeeSegAI::InstanceLabel label;
            label.kind = item.kind == 1 ? BeeSegAI::InstanceKind::Circle : BeeSegAI::InstanceKind::RotatedBox;
            label.cx = static_cast<double>(item.cx - shiftX);
            label.cy = static_cast<double>(item.cy - shiftY);
            label.w = item.w;
            label.h = item.h;
            label.theta = item.theta;
            label.classIndex = item.classIndex;
            labels.push_back(label);
        }
        const int rc = trainer->v2->AddInstances(labels.data(), static_cast<int>(labels.size()));
        if (rc != 0) trainer->lastError = trainer->v2->LastError();
        return rc;
    } catch (const std::exception& e) {
        trainer->lastError = std::string("AddInstances: ") + e.what();
        return -3;
    } catch (...) {
        trainer->lastError = "AddInstances: unknown native error";
        return -3;
    }
}
SAPI int32_t SEGAI_InferHasInstanceHead(void* handle)
{
    auto* infer = static_cast<InferHandle*>(handle);
    return infer ? (infer->inferer.HasInstanceHead() ? 1 : 0) : -1;
}
SAPI int32_t SEGAI_InferPredictInstances(void* handle,
                                         const uint8_t* bgr,
                                         int32_t w,
                                         int32_t h,
                                         int32_t step,
                                         int32_t roiX,
                                         int32_t roiY,
                                         int32_t roiW,
                                         int32_t roiH,
                                         float threshold,
                                         int32_t quality,
                                         SegInstanceResultV1* out,
                                         int32_t capacity,
                                         int32_t* outCount)
{
    auto* infer = static_cast<InferHandle*>(handle);
    if (!infer) return -1;
    infer->lastError.clear();
    if (!outCount || capacity < 0 || (capacity > 0 && !out)) {
        infer->lastError = "PredictInstances: invalid output buffer, capacity or count pointer";
        return -1;
    }
    *outCount = 0;
    try {
        cv::Mat bgrMat = WrapBgr(bgr, w, h, step);
        if (bgrMat.empty()) { infer->lastError = "PredictInstances: invalid BGR8 image dimensions or stride"; return -2; }
        std::vector<BeeSegAI::DecodedInstance> items;
        const int rc = infer->inferer.PredictInstances(bgrMat, cv::Rect(roiX, roiY, roiW, roiH), threshold, quality, items);
        if (rc != 0) {
            infer->lastError = infer->inferer.LastError();
            if (infer->lastError.empty()) infer->lastError = "PredictInstances failed (" + std::to_string(rc) + ")";
            return rc;
        }
        *outCount = static_cast<int32_t>(items.size());
        if (static_cast<int64_t>(items.size()) > static_cast<int64_t>(capacity)) {
            infer->lastError = "PredictInstances: capacity " + std::to_string(capacity) + " is smaller than the " + std::to_string(items.size()) + " instances found";
            return -4;
        }
        for (size_t i = 0; i < items.size(); ++i) {
            const BeeSegAI::DecodedInstance& d = items[i];
            SegInstanceResultV1& r = out[i];
            const bool circle = d.kind == BeeSegAI::InstanceKind::Circle;
            r.structSize = static_cast<int32_t>(sizeof(SegInstanceResultV1));
            r.kind = circle ? 1 : 0;
            r.cx = static_cast<float>(d.cx);
            r.cy = static_cast<float>(d.cy);
            r.w = static_cast<float>(circle ? 0.5 * (d.w + d.h) : d.w);
            r.h = static_cast<float>(circle ? 0.5 * (d.w + d.h) : d.h);
            r.theta = static_cast<float>(circle ? 0.0 : d.theta);
            r.score = d.score;
            r.classIndex = d.classIndex;
            r.refined = d.refined != 0 ? 1 : 0;
        }
        return 0;
    } catch (const std::exception& e) {
        infer->lastError = std::string("PredictInstances: ") + e.what();
        return -3;
    } catch (...) {
        infer->lastError = "PredictInstances: unknown native error";
        return -3;
    }
}
SAPI int32_t SEGAI_TorchBackboneProbe(const wchar_t* torchScriptPath, int32_t threads, int32_t side, char* report, int32_t capacity)
{
    if (!SegFineTune::Compiled()) return -1;
    std::string text;
    int32_t result = -2;
    if (torchScriptPath && *torchScriptPath) {
        const int bytes = WideCharToMultiByte(CP_UTF8, 0, torchScriptPath, -1, nullptr, 0, nullptr, nullptr);
        std::string path(bytes > 1 ? size_t(bytes) - 1 : 0, '\0');
        if (bytes > 1) WideCharToMultiByte(CP_UTF8, 0, torchScriptPath, -1, &path[0], bytes, nullptr, nullptr);
        result = SegFineTune::Probe(path, threads, side, text) ? 0 : -2;
    } else text = "empty path";
    if (report && capacity > 0) {
        const size_t count = std::min<size_t>(text.size(), size_t(capacity) - 1);
        std::memcpy(report, text.data(), count);
        report[count] = '\0';
    }
    return result;
}
static std::string WideToUtf8(const wchar_t* text)
{
    if (!text || !*text) return std::string();
    const int bytes = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
    std::string result(bytes > 1 ? size_t(bytes) - 1 : 0, '\0');
    if (bytes > 1) WideCharToMultiByte(CP_UTF8, 0, text, -1, &result[0], bytes, nullptr, nullptr);
    return result;
}
SAPI int32_t SEGAI_TorchBackboneGradProbe(const wchar_t* torchScriptPath, int32_t threads, int32_t side, const wchar_t* gradPath, char* report, int32_t capacity)
{
    if (!SegFineTune::Compiled()) return -1;
    std::string text;
    const int32_t result = SegFineTune::GradientProbe(WideToUtf8(torchScriptPath), threads, side, WideToUtf8(gradPath), text) ? 0 : -2;
    if (report && capacity > 0) {
        const size_t count = std::min<size_t>(text.size(), size_t(capacity) - 1);
        std::memcpy(report, text.data(), count);
        report[count] = '\0';
    }
    return result;
}
