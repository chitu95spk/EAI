#pragma once
#include <opencv2/core.hpp>
#include <functional>
#include <memory>
#include <string>
#include <vector>

#include "SegClassTable.h"
#include "SegInstanceTargets.h"

namespace BeeSegAI {
// V135 diagnostic: last-call phase timings (milliseconds) of SegV2Model::Predict on the
// compact path. Values are overwritten per call and owned by the model instance
// (last call on this engine; not synchronized, same threading contract as Predict itself).
struct SegV2Timings {
    double deviceMs = 0;    // Impl::Device() lookup
    double prepareMs = 0;   // Prepare(): resize + letterbox canvas
    double tensorMs = 0;    // Input(): float tensor build
    double inferMs = 0;     // set_input_tensor + request.infer()
    double upsampleMs = 0;  // get_output_tensor + per-class upsample/crop/restore (Classify tail)
    double compileMs = 0;   // compile_model when device/size changed (normally 0)
    double ccScoreMs = 0;   // foreground, morphology, connected components, scoring, small maps
    double nearestMs = 0;   // two INTER_NEAREST resizes back to full ROI size
    double totalMs = 0;     // whole compact Predict
    // EASYAI_SEGAI_SPLIT_TIMING diagnostic only (default off): backbone-only request run on the same input tensor.
    double backboneMs = 0;
    bool backboneMeasured = false;
};

// V158 offline-only capture.  It is allocated by the caller, is not retained by a
// model, and records only the V2 compact/single-scale production path.
struct SegV2DiagnosticComponent {
    int id = 0;
    int x = 0, y = 0, width = 0, height = 0, area = 0;
    double centerX = 0, centerY = 0;
    double ngMean = 0, okMean = 0;
    int selectedClass = 0; // 1 NG, 2 OK
    unsigned char score = 0;
    bool scoreEvaluated = false;
    int areaCutoff = 0;
    bool accepted = false;
    std::string rejection;
};

struct SegV2DiagnosticCapture {
    static constexpr size_t MaxMatrixBytes = size_t(64) * 1024 * 1024;
    size_t matrixBytes = 0;
    cv::Size callerImageSize, checkSize, speedReducedSize, v2InputSize, networkCanvasSize, modelInputSize, networkOutputGrid, compactSize;
    cv::Rect requestedRoi, effectiveRoi;
    int frameLeft = 0, frameTop = 0, frameContentWidth = 0, frameContentHeight = 0;
    float requestedSpeedScale = 1.f;
    float effectiveSpeedScaleX = 1.f, effectiveSpeedScaleY = 1.f;
    bool speedDownscaled = false;
    bool morphologyApplied = false;
    bool winnerForegroundBoundary = false;
    bool ngClassEnabled = false, okClassEnabled = false;
    bool originalInput = false, compactPostprocess = false, multiScaleDetection = false;
    int morphologyKernel = 0;
    float foregroundThreshold = 0;
    float infererThreshold = 0;
    float minimumRatio = 0;
    double minimumFraction = 0;
    float detectionScaleMinimum = 1.f, detectionScaleMaximum = 1.f;
    int areaCutoff = 0;
    std::string requestedBackend, effectiveDevice;
    // Deep copies at the real production boundaries; never reconstructed later.
    cv::Mat networkBg, networkNg, networkOk;
    cv::Mat restoredCompactBg, restoredCompactNg, restoredCompactOk;
    cv::Mat enabledNg, enabledOk;
    cv::Mat foregroundPreMorph, foregroundAfterClose, foregroundAfterOpen;
    cv::Mat componentIds, componentStats, componentCenters;
    cv::Mat filteredNg, filteredOk, restoredNg, restoredOk;
    cv::Mat infererNg, infererOk, acceptedNg;
    std::vector<SegV2DiagnosticComponent> components;
    bool Capture(const cv::Mat& source, cv::Mat& target);
};

// Optional per-component shape cleanup (close then open, kernel = round(fraction * min(component bbox side)))
// of the final full-resolution CV_8UC1 NG/OK score maps, in place on the caller's Mats.
// Returns the number of components changed, 0 when fraction == 0, or -1 for invalid input.
int ApplyV2ShapeCleanup(cv::Mat& ng, cv::Mat& ok, float fraction);

// OpenVINO types stay private so constructing a V1 handle does not initialize V2.
class SegV2Model {
public:
    SegV2Model();
    ~SegV2Model();
    void Configure(const std::wstring& configuration);
    int AddSample(const cv::Mat& image, const cv::Mat& labels);
    void ClearSamples();
    int Train(const std::function<void(int)>& progress, const volatile int* cancel);
    bool Save(const std::wstring& path, float threshold, unsigned minimumArea);
    bool Load(const std::wstring& path);
    // ng = score map of the DEFECT classes, ok = score map of the GOOD class (CV_8U, image size). A loaded model whose
    // class table has no GOOD class returns ok empty. Models with a non-NG/OK class table (N != 2, or other kinds) use
    // the generalised class-count path (EAI-CLS S2a); the NG/OK layout keeps the original K=3 path.
    int Predict(const cv::Mat& image, bool gpu, cv::Mat& ng, cv::Mat& ok);
    int PredictDiagnostic(const cv::Mat& image, bool gpu, cv::Mat& ng, cv::Mat& ok,
                          SegV2DiagnosticCapture& capture);
    void SetPostprocess(float foreground, float minimumRatio, int morphology);
    void SetWinnerForeground(bool enabled);
    void SetObjectScale(float minimum, float maximum, bool multiScale);
    void SetShapeCleanup(float fraction); // [0, 0.25]; 0 = off (default, bit-identical to legacy output)
    int DefectPixels() const;
    int NormalPixels() const;
    bool HasNormalClass() const;
    bool OriginalInput() const; // Input size OFF: the network runs on Check pixels
    bool HasNgClass() const;
    // EAI-CLS S1 named classes. SetClassTable is valid after Configure and before the first AddSample (0 ok,
    // -2 wrong state, -3 invalid table with a reason in error); the incoming enabled flags are ignored.
    // GetClassTable returns the table of the trained/loaded model (legacy "NG"/"OK" when none was stored)
    // with the enabled flags of the model.
    int SetClassTable(const unsigned char* data, size_t bytes, std::string& error);
    SegClassTable GetClassTable() const;
    const std::string& LastError() const;
    const SegV2Timings& LastTimings() const;
    // EAI-CLS S3: class id (0 = none) and component score maps, at the size of the last Predict input, of the last Predict
    // on the generalised class path. False after a K=3 NG/OK Predict or a failed one (use the ng/ok outputs then).
    // The Mats share storage with the model; the caller must not modify them.
    bool GetLastClassMaps(cv::Mat& label, cv::Mat& score) const;
    // EAI-POS P3 instance head (CenterNet branch on the frozen feature grid; see SegInstanceHead.h). Trainer side: the settings key
    // instanceHead = 1 enables it (Configure rejects it with originalInput, backbone fine-tune and resume); AddInstances attaches
    // labels to the LAST AddSample, in the pixel coordinates of that sample image. Returns 0, -2 wrong state (instance head not enabled
    // or no sample yet), -3 invalid label (nothing is attached; reason in LastError).
    int AddInstances(const InstanceLabel* labels, int count);
    // True when the model (trained in this process or loaded) carries an instance head; InstanceClassCount = its class count C.
    bool HasInstanceHead() const;
    int InstanceClassCount() const;
    // Instances of the loaded/trained model in the pixel coordinates of `image` (no ROI offset). quality 1 refines each instance (SegInstanceRefine.h; a failed refinement keeps the head result)
    // refinement and behaves like 0. Returns 0, -1 no model, -11 the model has no instance head, -8 native error (LastError).
    int PredictInstances(const cv::Mat& image, bool gpu, float threshold, int quality, std::vector<DecodedInstance>& out);
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
}
