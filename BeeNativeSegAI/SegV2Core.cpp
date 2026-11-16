#include "../BuildSupport/ModelCacheDirectory.h"
#include "SegV2Core.h"
#include "SegAIFileFormat.h"
#include "SegBackboneFineTune.h"
#include "SegInstanceHead.h"
#include "SegInstanceRefine.h"
#include <openvino/core/graph_util.hpp>
#include <openvino/openvino.hpp>
#include <openvino/opsets/opset13.hpp>
#include <openvino/pass/serialize.hpp>
#include <opencv2/opencv.hpp>
#include <fstream>
#include <sstream>
#include <random>
#include <chrono>
#include <algorithm>
#include <numeric>
#include <filesystem>
#include <iostream>
#include <set>
#include <cmath>
#include <limits>
#include <atomic>
#include <exception>
#include <map>
#include <mutex>
#include <thread>
#include <immintrin.h>
#ifdef SEGAI_WITH_CUDA
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "OrtCudaBackend.h" // the only TU that sees the ONNX Runtime headers (ORT_API_MANUAL_INIT)
#endif

namespace BeeSegAI {
namespace fs = std::filesystem;
using Clock = std::chrono::steady_clock;
static inline double ElapsedMs(Clock::time_point from, Clock::time_point to) {
    return std::chrono::duration<double, std::milli>(to - from).count();
}

bool SegV2DiagnosticCapture::Capture(const cv::Mat& source, cv::Mat& target) {
    if (source.empty()) { target.release(); return true; }
    const size_t elementBytes = source.elemSize();
    if (matrixBytes > MaxMatrixBytes || elementBytes == 0 || source.total() > (MaxMatrixBytes - matrixBytes) / elementBytes)
        throw std::runtime_error("V158 diagnostic captured-matrix limit exceeded");
    cv::Mat copy = source.clone();
    matrixBytes += copy.total() * copy.elemSize();
    target = std::move(copy);
    return true;
}

namespace {
// EAI-CLS: true when the table is the legacy NG/OK layout (class 1 DEFECT, class 2 GOOD). Such a model keeps the exact
// K=3 compact inference path; every other table (N != 2, or other kinds) uses the generalised class-count path.
bool IsLegacyClassLayout(const SegClassTable& table) {
    return table.classes.size() == 2 && table.classes[0].kind == kClassKindDefect && table.classes[1].kind == kClassKindGood;
}
// EAI-CLS S2b: the trainer works with K = N + 1 head outputs for any codec-valid table (N = 1..32 classes, names and kinds as
// ValidateClassTable defines them: at most one GOOD, any number of DEFECT, any order). A table supplied to the trainer, or
// stored in a resume source, only has to pass that check; enabled flags are not required.
bool ValidateTrainerClassTable(const SegClassTable& table, std::string& error) {
    if (table.classes.empty() || table.classes.size() > static_cast<size_t>(kMaxClasses)) {
        error = "table has " + std::to_string(table.classes.size()) + " named classes; the trainer supports 1 to " + std::to_string(kMaxClasses);
        return false;
    }
    return ValidateClassTable(table, false, error);
}
// Class-indexed form of ApplyV2ShapeCleanup for the generalised inference path. label holds the class id (0 = none) of
// every pixel and score the owning component's score; classes are processed in id order with the same close/open,
// "never delete a component" and claim rules as the NG/OK function. Returns the number of components changed, 0 when
// fraction == 0, or -1 for invalid input.
int CleanupClassMaps(cv::Mat& label, cv::Mat& score, int classCount, float fraction) {
    if (label.empty() || score.empty() || label.type() != CV_8UC1 || score.type() != CV_8UC1 || label.size() != score.size() ||
        !std::isfinite(fraction) || fraction < 0.f || fraction > .25f) return -1;
    if (fraction == 0.f) return 0;
    const cv::Mat label0 = label.clone(), score0 = score.clone();
    const cv::Mat occupied = label0 > 0;
    cv::Mat claim = cv::Mat::zeros(label.size(), CV_32S); // 0 empty, >0 claim key (1-based), -1 contested
    struct Claim { int cls; unsigned char score; bool changed; };
    std::vector<Claim> claims;
    std::vector<bool> present(256, false);
    for (int y = 0; y < label0.rows; ++y) {
        const unsigned char* row = label0.ptr<unsigned char>(y);
        for (int x = 0; x < label0.cols; ++x) present[row[x]] = true;
    }
    for (int cls = 1; cls < classCount && cls < 256; ++cls) { // fixed order: class id ascending
        if (!present[cls]) continue;
        const cv::Mat binary = label0 == cls;
        cv::Mat labels, stats, centroids;
        const int count = cv::connectedComponentsWithStats(binary, labels, stats, centroids, 8, CV_32S);
        for (int id = 1; id < count; ++id) {
            const int x = stats.at<int>(id, cv::CC_STAT_LEFT), y = stats.at<int>(id, cv::CC_STAT_TOP);
            const int w = stats.at<int>(id, cv::CC_STAT_WIDTH), h = stats.at<int>(id, cv::CC_STAT_HEIGHT);
            int k = static_cast<int>(std::lround(double(fraction) * std::min(w, h)));
            if (k < 8) continue; // small components stay unchanged
            k |= 1;
            const int pad = k + 2;
            const cv::Rect box(x, y, w, h);
            const cv::Mat member = labels(box) == id;
            cv::Mat crop = cv::Mat::zeros(h + 2 * pad, w + 2 * pad, CV_8UC1);
            member.copyTo(crop(cv::Rect(pad, pad, w, h)));
            const cv::Mat kernel = cv::Mat::ones(k, k, CV_8UC1);
            cv::Mat closed, opened;
            cv::morphologyEx(crop, closed, cv::MORPH_CLOSE, kernel);
            cv::morphologyEx(closed, opened, cv::MORPH_OPEN, kernel);
            const cv::Mat result = opened(cv::Rect(pad, pad, w, h));
            if (cv::countNonZero(result) == 0) continue; // never delete a component
            double peak = 0;
            cv::minMaxLoc(score0(box), nullptr, &peak, nullptr, nullptr, member);
            claims.push_back(Claim{ cls, static_cast<unsigned char>(peak), false });
            const int key = static_cast<int>(claims.size());
            for (int row = 0; row < h; ++row) {
                const unsigned char* inside = member.ptr<unsigned char>(row);
                const unsigned char* kept = result.ptr<unsigned char>(row);
                const unsigned char* busy = occupied.ptr<unsigned char>(y + row) + x;
                unsigned char* outLabel = label.ptr<unsigned char>(y + row) + x;
                unsigned char* outScore = score.ptr<unsigned char>(y + row) + x;
                int* claimed = claim.ptr<int>(y + row) + x;
                for (int col = 0; col < w; ++col) {
                    if (inside[col]) {
                        if (!kept[col]) { outLabel[col] = 0; outScore[col] = 0; claims[key - 1].changed = true; }
                    } else if (kept[col] && !busy[col]) {
                        if (claimed[col] == 0) claimed[col] = key;
                        else if (claimed[col] != key) claimed[col] = -1;
                    }
                }
            }
        }
    }
    for (int y = 0; y < label.rows; ++y) {
        const int* claimed = claim.ptr<int>(y);
        unsigned char* l = label.ptr<unsigned char>(y);
        unsigned char* s = score.ptr<unsigned char>(y);
        for (int x = 0; x < label.cols; ++x) {
            if (claimed[x] <= 0) continue;
            Claim& owner = claims[claimed[x] - 1];
            l[x] = static_cast<unsigned char>(owner.cls);
            s[x] = owner.score;
            owner.changed = true;
        }
    }
    int changed = 0;
    for (const Claim& entry : claims) if (entry.changed) ++changed;
    return changed;
}
}

struct SegV2Model::Impl {
    // Classes = 3 is the K of every model trained without a class table (background, NG, OK); MaxClasses = 33 is the
    // fixed capacity of every per-class array (background + the 32 classes a table may name). trainClasses (below) is the
    // K actually used by the trainer.
    static constexpr int Channels = 448, Classes = 3, MaxClasses = 33;
    // V147: native original-input GPU execution is bounded to a 512px output core plus
    // the 320px receptive-field context required around it.  Both values are /32 so
    // every crop retains the exported ONNX feature-grid and HALF_PIXEL phase.
    static constexpr int TileCore = 512, TileHalo = 320, TileMaximum = TileCore + TileHalo * 2;
    int Side = 640, Grid = 80, epochs = 200;
    float learningRate = .015f, regularization = .001f;
    // Early stop: end when the best validation loss improved by less than earlyStopMinGain
    // (relative) over the last max(20, epochs/10) epochs; the best head is kept.
    bool earlyStop = true; float earlyStopMinGain = .02f;
    // V150: optional explicit early-stop window in epochs (settings key earlyStopWindow). 0 = legacy max(20, epochs/10).
    int earlyStopWindow = 0;
    float rotation = 15.f, scaleVariation = .15f, brightness = .15f;
    float trainingScaleMin = .85f, trainingScaleMax = 1.15f;
    float detectionScaleMin = 1.f, detectionScaleMax = 1.f;
    bool multiScaleDetection = false;
    bool augment = true, trainGpu = true, originalInput = false;
    // SEG-LIFECYCLE-r11 stage 2: optional joint backbone + head fine-tune with libtorch after the head is trained.
    // backboneEpochs = 0 (default, key absent) keeps the previous behaviour exactly.
    int backboneEpochs = 0, backboneThreads = 2; float backboneLr = 1e-4f, backboneHeadLr = 1e-3f; std::string backboneTorch; int backboneAugment = -1; // -1 follows `augment`; 0/1 sets the fine-tune stage only
    float lastClassWeights[MaxClasses] = { 1.f, 1.f, 1.f }; bool lastClassWeightsValid = false;
    // EAI-CLS S2b training class layout. trainClasses = K = N + 1 (3 without a table, N + 1 once SetClassTable ran);
    // classPixels[c] = labelled pixels of class c over all samples; classDisabled[c] = class c has no pixels (set by Train and
    // Load; background is never disabled). ngEnabled / normalEnabled keep their legacy meaning: any enabled DEFECT class /
    // the enabled GOOD class (for K = 3 without a table: class 1 / class 2, as before).
    int trainClasses = Classes; int classPixels[MaxClasses] = {}; bool classDisabled[MaxClasses] = {};
    bool ClassEnabled(int c) const { return c == 0 || !classDisabled[c]; }
    // Kind of class c (1..K-1): the table's kind, or NG = DEFECT / OK = GOOD without a table.
    unsigned char ClassKind(int c) const { return classTableSet ? classTable.classes[size_t(c) - 1].kind : (c == 2 ? kClassKindGood : kClassKindDefect); }
    void SyncLegacyEnabledFlags() {
        ngEnabled = false; normalEnabled = false;
        for (int c = 1; c < trainClasses; ++c) if (ClassEnabled(c)) { if (ClassKind(c) == kClassKindGood) normalEnabled = true; else ngEnabled = true; }
    }
    bool normalEnabled = true, ngEnabled = true, compactPostprocess = true, winnerForegroundBoundary = false;
    std::string lastError, stage;
    SegV2Timings timings; // V135 diagnostic: last compact Predict phase timings
    // Per-handle scratch for the synchronous instance path only (same threading contract as PredictInstances).
    ov::Tensor instanceInputTensor;
    cv::Mat instanceInputBgr[3];
    cv::Size lastInput;
    void Error(const std::string& operation, bool gpu, const std::string& reason) {
        lastError = ("V2 " + operation + " backend=" + (gpu ? (gpuBackend == 1 ? "NVIDIA CUDA" : "Intel GPU") : "CPU") +
            " input=" + std::to_string(lastInput.width) + "x" + std::to_string(lastInput.height) + ": " + reason).substr(0, 4000);
    }
    float foregroundThreshold = .5f, minimumRatio = .25f;
    int morphology = 3, ngPixels = 0, okPixels = 0;
    float shapeCleanupFraction = 0.f; // optional per-component shape cleanup of the final maps (0 = off)
    double minimumFraction = 1.;
    std::string backbone;
    fs::path logFolder;
    std::set<int> validationIndices;
    std::vector<std::pair<cv::Mat,cv::Mat>> images;
    std::unique_ptr<ov::Core> core;
    std::shared_ptr<ov::Model> network;
    ov::CompiledModel compiled;
    ov::InferRequest request;
    ov::InferRequest backboneRequest; // EASYAI_SEGAI_SPLIT_TIMING diagnostic only; empty unless backboneReady
    bool backboneReady = false;
    cv::Mat weightsBytes;
    std::string compiledDevice;
    cv::Size compiledSize;
    const volatile int* cancelFlag = nullptr;
    std::function<void(int)> progress;
    struct Frame { cv::Mat image, labels; int width=0,height=0,left=0,top=0; };
    // V161: natural[c] = per-class row count of the image BEFORE the per-image caps below (prior-balance bookkeeping only).
    struct Sample { cv::Mat rows, labels; double natural[MaxClasses] = {}; };
    // EAI-PLUS S5 step 0: wall-clock stage timers (measurement only; thread-summed for the parallel feature stages).
    struct StageStats { std::mutex lock; std::map<std::string, std::pair<double, long long>> values; std::vector<std::string> order; };
    std::shared_ptr<StageStats> stageStats = std::make_shared<StageStats>();
    void AddStage(const char* name, double seconds) {
        std::lock_guard<std::mutex> guard(stageStats->lock);
        auto found = stageStats->values.find(name);
        if (found == stageStats->values.end()) { stageStats->order.push_back(name); found = stageStats->values.emplace(name, std::make_pair(0., 0LL)).first; }
        found->second.first += seconds; found->second.second += 1;
    }
    struct StageTimer {
        Impl& owner; const char* name; Clock::time_point begin; bool done = false;
        StageTimer(Impl& o, const char* n) : owner(o), name(n), begin(Clock::now()) {}
        void Stop() { if (done) return; done = true; owner.AddStage(name, std::chrono::duration<double>(Clock::now() - begin).count()); }
        ~StageTimer() { try { Stop(); } catch (...) {} }
    };
    void WriteStageTimes() {
        try {
            std::lock_guard<std::mutex> guard(stageStats->lock);
            if (logFolder.empty()) return;
            std::ofstream csv(logFolder / "stage_times.csv", std::ios::trunc); csv << "stage,seconds,calls\n";
            std::ostringstream line; line << "StageTimes";
            for (const auto& name : stageStats->order) {
                const auto& v = stageStats->values[name];
                csv << name << ',' << v.first << ',' << v.second << '\n'; line << ' ' << name << '=' << v.first << "s/" << v.second;
            }
            std::cout << line.str() << std::endl;
        } catch (...) {}
    }
    void CheckCancel() const { if (cancelFlag && *cancelFlag) throw std::runtime_error("Training cancelled"); }
    ov::Core& Core() { if (!core) core.reset(new ov::Core()); return *core; }
    // NVIDIA CUDA backend (ONNX Runtime). gpuBackend: 0 = Intel OpenVINO (legacy default), 1 = NVIDIA CUDA.
    // CUDA only serves the inference/eval network (cudaCapable == true). Training, the instance head and every OpenVINO-only
    // path ask for Device(gpu) and get "CPU" when CUDA is selected (documented scope of this step). cudaFailed is sticky until
    // SetGpuBackend/Load so a missing CUDA runtime is reported, never retried per frame.
    int gpuBackend = 0; bool cudaFailed = false; std::string cudaNote;
#ifdef SEGAI_WITH_CUDA
    std::unique_ptr<segai_cuda::OrtCudaBackend> cuda;
    std::unique_ptr<segai_cuda::OrtCudaBackend> instanceCuda; // instance head (backbone + 2 dense 1x1) on ONNX Runtime CUDA; same sticky-fallback rules
#endif
    // Diagnostic capture of the raw instance output (see LastInstanceTensor); off unless EASYAI_SEGAI_INSTANCE_TENSOR is set.
    const bool captureInstanceTensor = std::getenv("EASYAI_SEGAI_INSTANCE_TENSOR") != nullptr;
    std::vector<float> lastInstanceTensor; int lastInstanceChannels = 0, lastInstanceGrid = 0;
    std::string Device(bool gpu, bool cudaCapable = false) {
        if (!gpu) return "CPU";
        if (gpuBackend == 1) return (cudaCapable && !cudaFailed) ? "CUDA" : "CPU";
        for (const auto& device : Core().get_available_devices())
            if (device.find("GPU") == 0 && Core().get_property(device, ov::device::full_name).find("Intel") != std::string::npos) return device;
        throw std::runtime_error("Intel OpenVINO GPU unavailable. Select CPU or check the Intel driver.");
    }
Frame Prepare(const cv::Mat& image, const cv::Mat& labels, bool recordInput = true) {
    const bool labelled = !labels.empty();
    if (image.empty() || image.type() != CV_8UC3 || (labelled && (labels.type() != CV_8U || image.size() != labels.size())))
        throw std::runtime_error("Expected nonempty BGR8 image and matching label mask");
    if (recordInput) lastInput = image.size(); // parallel training workers must not race on this diagnostic
    Frame frame;
    const double scale = originalInput ? 1. : double(Side) / std::max(image.cols, image.rows);
    frame.width = std::max(1, int(std::round(image.cols * scale))); frame.height = std::max(1, int(std::round(image.rows * scale)));
    const int width = originalInput ? ((frame.width + 31) / 32) * 32 : Side;
    const int height = originalInput ? ((frame.height + 31) / 32) * 32 : Side;
    frame.left = (width - frame.width) / 2; frame.top = (height - frame.height) / 2;
    frame.image = cv::Mat(height, width, CV_8UC3, cv::Scalar(114, 114, 114));
    if (labelled) frame.labels = cv::Mat(height, width, CV_8U, cv::Scalar(255));
    cv::Mat resized, resizedLabels;
    if (originalInput) { resized = image; resizedLabels = labels; }
    else {
        cv::resize(image, resized, cv::Size(frame.width, frame.height));
        if (labelled) cv::resize(labels, resizedLabels, resized.size(), 0, 0, cv::INTER_NEAREST);
    }
    resized.copyTo(frame.image(cv::Rect(frame.left, frame.top, frame.width, frame.height)));
    if (labelled) resizedLabels.copyTo(frame.labels(cv::Rect(frame.left, frame.top, frame.width, frame.height)));
    return frame;
}
ov::Tensor Input(const cv::Mat& image, bool reuseInstanceBuffer = false) {
    const int width = image.cols, height = image.rows;
    ov::Tensor tensor;
    cv::Mat temporaryBgr[3]; cv::Mat* bgr = temporaryBgr;
    if (reuseInstanceBuffer) {
        const ov::Shape shape{1, 3, size_t(height), size_t(width)};
        if (!instanceInputTensor || instanceInputTensor.get_shape() != shape)
            instanceInputTensor = ov::Tensor(ov::element::f32, shape);
        tensor = instanceInputTensor; bgr = instanceInputBgr;
    } else tensor = ov::Tensor(ov::element::f32, {1, 3, size_t(height), size_t(width)});
    float* data = tensor.data<float>();
    const float mean[3] = {.485f, .456f, .406f}, sd[3] = {.229f, .224f, .225f};
    // Planar RGB written straight into the tensor; scale/offset fold (v / 255 - mean) / sd.
    cv::split(image, bgr);
    for (int c = 0; c < 3; ++c) {
        cv::Mat plane(height, width, CV_32F, data + size_t(c) * height * width);
        bgr[2 - c].convertTo(plane, CV_32F, 1. / (255. * sd[c]), -mean[c] / sd[c]);
    }
    return tensor;
}
std::vector<cv::Mat> Classify(const Frame& frame, cv::Size restoredSize, SegV2DiagnosticCapture* diagnostic = nullptr) {
    const auto timeStart = Clock::now();
    ov::Tensor inputTensor = Input(frame.image);
    const auto timeTensor = Clock::now();
    const int width = frame.image.cols / 8, height = frame.image.rows / 8;
    std::vector<float> cudaOutput; const float* outputData = nullptr; ov::Tensor output;
#ifdef SEGAI_WITH_CUDA
    if (compiledDevice == "CUDA" && cuda) {
        cudaOutput = cuda->Run(inputTensor.data<float>());
        if (cudaOutput.size() != size_t(inferClasses) * size_t(height) * size_t(width)) throw std::runtime_error("V2 CUDA output shape mismatch");
        outputData = cudaOutput.data();
    } else
#endif
    {
        request.set_input_tensor(inputTensor); request.infer();
        output = request.get_output_tensor();
        if (output.get_element_type() != ov::element::f32) throw std::runtime_error("Expected f32 V2 output");
        if (output.get_shape() != ov::Shape{1, size_t(inferClasses), size_t(height), size_t(width)}) throw std::runtime_error("V2 output shape mismatch");
        outputData = output.data<float>();
    }
    const auto timeInfer = Clock::now();
    std::vector<cv::Mat> classes;
    if (diagnostic) diagnostic->networkOutputGrid = cv::Size(width, height);
    for (int c = 0; c < inferClasses; ++c) {
        cv::Mat plane(height, width, CV_32F, const_cast<float*>(outputData) + size_t(c) * height * width), big, restored;
        if (diagnostic) {
            if (c == 0) diagnostic->Capture(plane, diagnostic->networkBg);
            else if (c == 1) diagnostic->Capture(plane, diagnostic->networkNg);
            else if (c == 2) diagnostic->Capture(plane, diagnostic->networkOk);
        }
        cv::resize(plane, big, frame.image.size());
        cv::resize(big(cv::Rect(frame.left, frame.top, frame.width, frame.height)), restored, restoredSize);
        if (diagnostic) {
            if (c == 0) diagnostic->Capture(restored, diagnostic->restoredCompactBg);
            else if (c == 1) diagnostic->Capture(restored, diagnostic->restoredCompactNg);
            else if (c == 2) diagnostic->Capture(restored, diagnostic->restoredCompactOk);
        }
        classes.push_back(restored);
    }
    timings.tensorMs += ElapsedMs(timeStart, timeTensor);
    timings.inferMs += ElapsedMs(timeTensor, timeInfer);
    timings.upsampleMs += ElapsedMs(timeInfer, Clock::now());
    if (backboneReady) { // EASYAI_SEGAI_SPLIT_TIMING: backbone-only run on the same tensor, after the full-graph phases above
        const auto timeBackbone = Clock::now();
        backboneRequest.set_input_tensor(inputTensor); backboneRequest.infer();
        timings.backboneMs += ElapsedMs(timeBackbone, Clock::now()); timings.backboneMeasured = true;
    }
    return classes;
}
// EAI-CLS S2a: turns the per-pixel class id / component score maps of the generalised path into the legacy outputs:
// ng = score of every pixel whose class is DEFECT, ok = score of every pixel whose class is GOOD (released when the table
// has no GOOD class). Both maps have the size of the label map.
void ClassMapsToDefectGood(const cv::Mat& labelMap, const cv::Mat& scoreMap, cv::Mat& ng, cv::Mat& ok) const {
    unsigned char defectTable[256]={},goodTable[256]={};
    bool hasGood=false;
    for(int c=1;c<inferClasses;++c) {
        if(classTable.classes[c-1].kind==kClassKindGood){goodTable[c]=255;hasGood=true;}
        else defectTable[c]=255;
    }
    ng=cv::Mat::zeros(labelMap.size(),CV_8U);
    cv::Mat selected;
    cv::LUT(labelMap,cv::Mat(1,256,CV_8U,defectTable),selected);
    scoreMap.copyTo(ng,selected);
    if(hasGood) {
        ok=cv::Mat::zeros(labelMap.size(),CV_8U);
        cv::LUT(labelMap,cv::Mat(1,256,CV_8U,goodTable),selected);
        scoreMap.copyTo(ok,selected);
    } else ok.release();
}
// EAI-CLS S2a: compact inference for a model whose class table is not the legacy NG/OK layout (any N in 1..32, any kinds).
// Same stages as PredictCompact: shared foreground threshold and morphology, shared connected components, one class per
// component (argmax of the component mean over the enabled classes, lowest class id wins ties), the legacy score formula,
// INTER_NEAREST restore. ng receives the score of every component whose class is DEFECT, ok the score of every component whose
// class is GOOD (left empty when the table has no GOOD class). Disabled classes contribute zero.
int PredictCompactClasses(const cv::Mat& image, bool gpu, cv::Mat& ng, cv::Mat& ok) {
    const float shapeCleanup = shapeCleanupFraction;
    const auto timeStart=Clock::now();
    const std::string device=Device(gpu,true);
    const auto timeDevice=Clock::now();
    const double scale=originalInput?.25:double(Grid*2)/std::max(image.cols,image.rows);
    const cv::Size size(std::max(1,int(std::round(image.cols*scale))),std::max(1,int(std::round(image.rows*scale))));
    auto frame=Prepare(image,cv::Mat());
    const auto timePrepare=Clock::now();
    timings.deviceMs=ElapsedMs(timeStart,timeDevice);
    timings.prepareMs=ElapsedMs(timeDevice,timePrepare);
    auto classes=PredictFrame(frame,size,device,nullptr);
    const auto timeClassified=Clock::now();
    const int foregroundClasses=inferClasses-1;
    if (foregroundClasses<1 || int(classes.size())!=inferClasses || int(classTable.classes.size())!=foregroundClasses) throw std::runtime_error("V2 class count mismatch");
    for (int c=1;c<inferClasses;++c) if (!classTable.classes[c-1].enabled) classes[c].setTo(0);
    cv::Mat foreground;
    if (!winnerForegroundBoundary) {
        // Sum of the enabled class probabilities in class order, and at least one class above the background.
        cv::Mat sum=classes[1].clone(), above=classes[1]>classes[0];
        for (int c=2;c<inferClasses;++c) {
            cv::add(sum,classes[c],sum);
            cv::Mat higher=classes[c]>classes[0];
            cv::bitwise_or(above,higher,above);
        }
        foreground=(sum>=foregroundThreshold)&above;
    } else {
        cv::Mat winner=classes[1].clone();
        for (int c=2;c<inferClasses;++c) cv::max(winner,classes[c],winner);
        foreground=(winner>=foregroundThreshold)&(winner>classes[0]);
    }
    if(morphology) {
        auto kernel=cv::Mat::ones(morphology,morphology,CV_8U);
        cv::morphologyEx(foreground,foreground,cv::MORPH_CLOSE,kernel);
        cv::morphologyEx(foreground,foreground,cv::MORPH_OPEN,kernel);
    }
    cv::Mat ids,stats,centers;int count=cv::connectedComponentsWithStats(foreground,ids,stats,centers,8);
    std::vector<std::vector<double>> sums(foregroundClasses,std::vector<double>(count,0.));
    {
        std::vector<const float*> rows(foregroundClasses);
        for(int y=0;y<size.height;++y) {
            const int* labels=ids.ptr<int>(y);
            for(int c=0;c<foregroundClasses;++c) rows[c]=classes[c+1].ptr<float>(y);
            for(int x=0;x<size.width;++x) for(int c=0;c<foregroundClasses;++c) sums[c][labels[x]]+=rows[c][x];
        }
    }
    std::vector<unsigned char> componentClass(count,0),componentScore(count,0);
    const double cutoff=std::max(1.,minimumFraction*minimumRatio*size.area());
    for(int id=1;id<count;++id) {
        const int area=stats.at<int>(id,cv::CC_STAT_AREA);
        if(area<cutoff) continue;
        int best=-1;double bestMean=0;
        for(int c=0;c<foregroundClasses;++c) {
            if(!classTable.classes[c].enabled) continue;
            const double mean=sums[c][id]/area;
            if(best<0||mean>bestMean){best=c;bestMean=mean;} // strict ">" keeps the lowest class id on a tie
        }
        if(best<0) continue;
        componentClass[id]=static_cast<unsigned char>(best+1);
        componentScore[id]=cv::saturate_cast<unsigned char>(std::max(1.,std::nearbyint(bestMean*255)));
    }
    cv::Mat smallLabel(size,CV_8U),smallScore(size,CV_8U);
    for(int y=0;y<size.height;++y) {
        const int* labels=ids.ptr<int>(y);unsigned char* l=smallLabel.ptr<unsigned char>(y);unsigned char* s=smallScore.ptr<unsigned char>(y);
        for(int x=0;x<size.width;++x){l[x]=componentClass[labels[x]];s[x]=componentScore[labels[x]];}
    }
    const auto timeScored=Clock::now();
    cv::Mat labelMap,scoreMap;
    cv::resize(smallLabel,labelMap,image.size(),0,0,cv::INTER_NEAREST);
    cv::resize(smallScore,scoreMap,image.size(),0,0,cv::INTER_NEAREST);
    if (shapeCleanup > 0.f) CleanupClassMaps(labelMap,scoreMap,inferClasses,shapeCleanup);
    ClassMapsToDefectGood(labelMap,scoreMap,ng,ok);
    lastLabelMap=labelMap;lastScoreMap=scoreMap;
    timings.ccScoreMs=ElapsedMs(timeClassified,timeScored);
    timings.nearestMs=ElapsedMs(timeScored,Clock::now());
    return 0;
}
// EAI-CLS S2c: multiscale detection for a non-NG/OK class table. This generalises the NG/OK multiscale code in
// SegV2Model::Predict step for step: the same scale list, view geometry, Prepare/PredictFrame calls and coordinate
// restore, the same grid resize, foreground threshold on the class sum, morphology, connected components and
// minimum-area rule (minimum scale squared). Only the two class planes become K-1 classes.
//
// Fusion rule (the generalisation of "n>bg && n>=o -> ng=max(ng,n); o>bg && o>n -> ok=max(ok,o)" and of the final
// "if(ng>=ok)ok=0;else ng=0"): at every scale a pixel is won by the enabled class with the highest probability
// (lowest class id on an exact tie) when that probability beats the background. Over all scales, per class c,
// best_c = max of those winning probabilities; then each pixel keeps only the class with the highest best_c
// (lowest class id on a tie) and every other class is zero there. Because a pixel contributes to a single class at each
// scale, the maximum over all (scale, class) winning pairs is the same pixel state, so one value plane and one class
// plane are kept instead of K-1 full-size planes. Disabled classes contribute nothing. The shape cleanup fraction is
// not applied here, as in the NG/OK multiscale path.
int PredictMultiScaleClasses(const cv::Mat& image, bool gpu, cv::Mat& ng, cv::Mat& ok) {
    const int foregroundClasses=inferClasses-1;
    if (foregroundClasses<1 || int(classTable.classes.size())!=foregroundClasses) throw std::runtime_error("V2 class count mismatch");
    const std::string device=Device(gpu,true);
    cv::Mat bestValue=cv::Mat::zeros(image.size(),CV_32F);
    cv::Mat bestClass=cv::Mat::zeros(image.size(),CV_8U);
    auto accumulate = [&](const std::vector<cv::Mat>& classes, const cv::Rect& target) {
        if (int(classes.size())!=inferClasses) throw std::runtime_error("V2 class count mismatch");
        std::vector<const float*> rows(inferClasses);
        for (int y=0; y<target.height; ++y) {
            for (int c=0;c<inferClasses;++c) rows[c]=classes[c].ptr<float>(y);
            float* values=bestValue.ptr<float>(y+target.y)+target.x;
            unsigned char* owners=bestClass.ptr<unsigned char>(y+target.y)+target.x;
            for (int x=0; x<target.width; ++x) {
                int winner=0;float value=0.f;
                for (int c=1;c<inferClasses;++c) {
                    const float probability=classTable.classes[c-1].enabled?rows[c][x]:0.f;
                    if (winner==0||probability>value) {winner=c;value=probability;} // strict ">" keeps the lowest class id on a tie
                }
                if (winner==0||!(value>rows[0][x])) continue;
                if (owners[x]==0||value>values[x]||(value==values[x]&&winner<owners[x])) {values[x]=value;owners[x]=static_cast<unsigned char>(winner);}
            }
        }
    };
    std::vector<double> scales{1.};
    if (multiScaleDetection) {
        scales = {detectionScaleMin, detectionScaleMax,
            detectionScaleMin <= 1 && detectionScaleMax >= 1 ? 1. : std::sqrt(double(detectionScaleMin)*detectionScaleMax)};
        std::sort(scales.begin(),scales.end());
        scales.erase(std::unique(scales.begin(),scales.end(),[](double x,double y){return std::abs(x-y)<1e-5;}),scales.end());
    }
    for (double scale : scales) {
        if (scale < 1.-1e-5) {
            // A small target is enlarged through overlapping views. Cover the
            // complete Check ROI instead of zooming only at the image centre.
            const int width=std::max(1,int(std::round(image.cols*scale))),height=std::max(1,int(std::round(image.rows*scale)));
            const int nx=std::max(1,int(std::ceil(2.*(image.cols-width)/width))+1),ny=std::max(1,int(std::ceil(2.*(image.rows-height)/height))+1);
            for(int iy=0;iy<ny;++iy) for(int ix=0;ix<nx;++ix) {
                cv::Rect target(nx==1?0:int(std::round(double(ix)*(image.cols-width)/(nx-1))),ny==1?0:int(std::round(double(iy)*(image.rows-height)/(ny-1))),width,height);
                cv::Mat view=image(target);
                // Original-input mode needs an explicit zoom; cropping alone no longer resizes it.
                if (originalInput) cv::resize(image(target),view,image.size());
                auto frame=Prepare(view,cv::Mat::zeros(view.size(),CV_8U));
                accumulate(PredictFrame(frame,target.size(),device),target);
            }
        } else {
            auto frame=Prepare(image,cv::Mat::zeros(image.size(),CV_8U));
            if (scale > 1.+1e-5) {
                // Shrink content within the fixed network input. Resizing the
                // source then letterboxing again would cancel the object zoom.
                frame.width=std::max(1,int(std::round(frame.width/scale)));frame.height=std::max(1,int(std::round(frame.height/scale)));
                frame.left=(frame.image.cols-frame.width)/2;frame.top=(frame.image.rows-frame.height)/2;
                frame.image.setTo(cv::Scalar(114,114,114));cv::Mat resized;
                cv::resize(image,resized,cv::Size(frame.width,frame.height));resized.copyTo(frame.image(cv::Rect(frame.left,frame.top,frame.width,frame.height)));
            }
            accumulate(PredictFrame(frame,image.size(),device),cv::Rect(0,0,image.cols,image.rows));
        }
    }
    const cv::Size grid = originalInput
        ? cv::Size(std::max(1,(image.cols+7)/8),std::max(1,(image.rows+7)/8))
        : cv::Size(Grid,std::max(1,int(std::round(image.rows*double(Grid)/image.cols))));
    // One grid-size plane per class, built from the pixels that class kept (classes without a pixel stay zero).
    std::vector<bool> present(256,false);
    for (int y=0;y<bestClass.rows;++y) {
        const unsigned char* owners=bestClass.ptr<unsigned char>(y);
        for (int x=0;x<bestClass.cols;++x) present[owners[x]]=true;
    }
    std::vector<cv::Mat> gridPlanes(foregroundClasses);
    for (int c=0;c<foregroundClasses;++c) {
        if (!present[c+1]) {gridPlanes[c]=cv::Mat::zeros(grid,CV_32F);continue;}
        cv::Mat plane=cv::Mat::zeros(image.size(),CV_32F);
        const cv::Mat kept=bestClass==(c+1);
        bestValue.copyTo(plane,kept);
        cv::resize(plane,gridPlanes[c],grid,0,0,cv::INTER_AREA);
    }
    cv::Mat sum=gridPlanes[0].clone();
    for (int c=1;c<foregroundClasses;++c) cv::add(sum,gridPlanes[c],sum);
    cv::Mat foreground,ids,stats,centers;cv::compare(sum,foregroundThreshold,foreground,cv::CMP_GE);
    if(morphology) {
        auto kernel=cv::Mat::ones(morphology,morphology,CV_8U);
        cv::morphologyEx(foreground,foreground,cv::MORPH_CLOSE,kernel,cv::Point(-1,-1),1,cv::BORDER_CONSTANT,cv::Scalar(0));
        cv::morphologyEx(foreground,foreground,cv::MORPH_OPEN,kernel,cv::Point(-1,-1),1,cv::BORDER_CONSTANT,cv::Scalar(0));
    }
    const int count=cv::connectedComponentsWithStats(foreground,ids,stats,centers,8);
    // The area rule counts restored pixels. Restoring the id map once with INTER_NEAREST selects exactly the pixels
    // that restoring each component's mask separately would.
    cv::Mat restoredIds;
    cv::resize(ids,restoredIds,image.size(),0,0,cv::INTER_NEAREST);
    std::vector<int64_t> restoredArea(count,0);
    for (int y=0;y<restoredIds.rows;++y) {
        const int* row=restoredIds.ptr<int>(y);
        for (int x=0;x<restoredIds.cols;++x) ++restoredArea[row[x]];
    }
    std::vector<std::vector<double>> sums(foregroundClasses,std::vector<double>(count,0.));
    for (int c=0;c<foregroundClasses;++c) for (int y=0;y<grid.height;++y) {
        const int* labels=ids.ptr<int>(y);const float* values=gridPlanes[c].ptr<float>(y);
        for (int x=0;x<grid.width;++x) sums[c][labels[x]]+=values[x];
    }
    const double areaScale=multiScaleDetection?std::pow(double(detectionScaleMin),2):1.;
    const double cutoff=std::max(1.,minimumFraction*minimumRatio*double(image.total())*areaScale);
    std::vector<unsigned char> componentClass(count,0),componentScore(count,0);
    for (int id=1;id<count;++id) {
        if (double(restoredArea[id])<cutoff) continue;
        const int area=stats.at<int>(id,cv::CC_STAT_AREA);
        int best=-1;double bestMean=0;
        for (int c=0;c<foregroundClasses;++c) {
            if (!classTable.classes[c].enabled) continue;
            const double mean=sums[c][id]/area;
            if (best<0||mean>bestMean) {best=c;bestMean=mean;} // strict ">" keeps the lowest class id on a tie
        }
        if (best<0) continue;
        componentClass[id]=static_cast<unsigned char>(best+1);
        componentScore[id]=cv::saturate_cast<unsigned char>(std::max(1.,std::nearbyint(bestMean*255)));
    }
    cv::Mat labelMap(image.size(),CV_8U),scoreMap(image.size(),CV_8U);
    for (int y=0;y<image.rows;++y) {
        const int* row=restoredIds.ptr<int>(y);unsigned char* l=labelMap.ptr<unsigned char>(y);unsigned char* s=scoreMap.ptr<unsigned char>(y);
        for (int x=0;x<image.cols;++x) {l[x]=componentClass[row[x]];s[x]=componentScore[row[x]];}
    }
    ClassMapsToDefectGood(labelMap,scoreMap,ng,ok);
    lastLabelMap=labelMap;lastScoreMap=scoreMap;
    return 0;
}
int PredictCompact(const cv::Mat& image, bool gpu, cv::Mat& ng, cv::Mat& ok,
                   SegV2DiagnosticCapture* diagnostic) {
    if (genericClasses) return PredictCompactClasses(image, gpu, ng, ok);
    const float shapeCleanup = shapeCleanupFraction;
    const auto timeStart=Clock::now();
    const std::string device=Device(gpu,true);
    const auto timeDevice=Clock::now();
    const double scale=originalInput?.25:double(Grid*2)/std::max(image.cols,image.rows);
    const cv::Size size(std::max(1,int(std::round(image.cols*scale))),std::max(1,int(std::round(image.rows*scale))));
    auto frame=Prepare(image,cv::Mat());
    if (diagnostic) {
        diagnostic->v2InputSize = image.size();
        diagnostic->networkCanvasSize = frame.image.size();
        diagnostic->modelInputSize = frame.image.size();
        diagnostic->frameLeft = frame.left;diagnostic->frameTop = frame.top;
        diagnostic->frameContentWidth = frame.width;diagnostic->frameContentHeight = frame.height;
        diagnostic->compactSize = size;
        diagnostic->effectiveDevice = device;
        diagnostic->foregroundThreshold = foregroundThreshold;
        diagnostic->minimumRatio = minimumRatio;
        diagnostic->minimumFraction = minimumFraction;
        diagnostic->detectionScaleMinimum = detectionScaleMin;
        diagnostic->detectionScaleMaximum = detectionScaleMax;
        diagnostic->ngClassEnabled = ngEnabled;diagnostic->okClassEnabled = normalEnabled;
        diagnostic->originalInput = originalInput;diagnostic->compactPostprocess = compactPostprocess;
        diagnostic->multiScaleDetection = multiScaleDetection;
        diagnostic->winnerForegroundBoundary = winnerForegroundBoundary;
        diagnostic->areaCutoff = static_cast<int>(std::ceil(std::max(1., minimumFraction * minimumRatio * size.area())));
        diagnostic->morphologyApplied = morphology != 0;
        diagnostic->morphologyKernel = morphology;
    }
    const auto timePrepare=Clock::now();
    timings.deviceMs=ElapsedMs(timeStart,timeDevice);
    timings.prepareMs=ElapsedMs(timeDevice,timePrepare);
    auto classes=PredictFrame(frame,size,device,diagnostic);
    const auto timeClassified=Clock::now();
    cv::Mat a=classes[1],b=classes[2];
    if(!ngEnabled)a.setTo(0);
    if(!normalEnabled)b.setTo(0);
    if (diagnostic) {
        diagnostic->Capture(a, diagnostic->enabledNg);
        diagnostic->Capture(b, diagnostic->enabledOk);
    }
    cv::Mat foreground;
    if (!winnerForegroundBoundary) foreground=(a+b>=foregroundThreshold)&((a>classes[0])|(b>classes[0]));
    else {
        cv::Mat winner;
        cv::max(a,b,winner);
        foreground=(winner>=foregroundThreshold)&(winner>classes[0]);
    }
    if (diagnostic) diagnostic->Capture(foreground, diagnostic->foregroundPreMorph);
    if(morphology) {
        auto kernel=cv::Mat::ones(morphology,morphology,CV_8U);
        cv::morphologyEx(foreground,foreground,cv::MORPH_CLOSE,kernel);
        if (diagnostic) diagnostic->Capture(foreground, diagnostic->foregroundAfterClose);
        cv::morphologyEx(foreground,foreground,cv::MORPH_OPEN,kernel);
        if (diagnostic) diagnostic->Capture(foreground, diagnostic->foregroundAfterOpen);
    } else if (diagnostic) {
        // These are copies of the unchanged real production mask at both skipped boundaries.
        diagnostic->Capture(foreground, diagnostic->foregroundAfterClose);
        diagnostic->Capture(foreground, diagnostic->foregroundAfterOpen);
    }
    cv::Mat ids,stats,centers;int count=cv::connectedComponentsWithStats(foreground,ids,stats,centers,8);
    if (diagnostic) {
        diagnostic->Capture(ids, diagnostic->componentIds);
        diagnostic->Capture(stats, diagnostic->componentStats);
        diagnostic->Capture(centers, diagnostic->componentCenters);
    }
    std::vector<unsigned char> ngScore(count,0),okScore(count,0);
    std::vector<double> ngSum(count,0),okSum(count,0);
    for(int y=0;y<size.height;++y) {
        const int* labels=ids.ptr<int>(y);const float* n=a.ptr<float>(y);const float* o=b.ptr<float>(y);
        for(int x=0;x<size.width;++x){ngSum[labels[x]]+=n[x];okSum[labels[x]]+=o[x];}
    }
    for(int id=1;id<count;++id) {
        int area=stats.at<int>(id,cv::CC_STAT_AREA);
        if(area<std::max(1.,minimumFraction*minimumRatio*size.area())) {
            if (diagnostic) {
                const double n=ngSum[id]/area,o=okSum[id]/area;
                const unsigned char score=cv::saturate_cast<unsigned char>(std::max(1.,std::nearbyint(std::max(n,o)*255)));
                SegV2DiagnosticComponent component;
                component.id=id;component.x=stats.at<int>(id,cv::CC_STAT_LEFT);component.y=stats.at<int>(id,cv::CC_STAT_TOP);
                component.width=stats.at<int>(id,cv::CC_STAT_WIDTH);component.height=stats.at<int>(id,cv::CC_STAT_HEIGHT);component.area=area;
                component.centerX=centers.at<double>(id,0);component.centerY=centers.at<double>(id,1);
                component.ngMean=n;component.okMean=o;component.selectedClass=n>=o?1:2;component.score=score;
                component.scoreEvaluated=true;
                component.areaCutoff=static_cast<int>(std::ceil(std::max(1.,minimumFraction*minimumRatio*size.area())));
                component.rejection="minimum_area";diagnostic->components.push_back(component);
            }
            continue;
        }
        double n=ngSum[id]/area,o=okSum[id]/area;
        unsigned char score=cv::saturate_cast<unsigned char>(std::max(1.,std::nearbyint(std::max(n,o)*255)));
        (n>=o?ngScore:okScore)[id]=score;
        if (diagnostic) {
            SegV2DiagnosticComponent component;
            component.id=id;component.x=stats.at<int>(id,cv::CC_STAT_LEFT);component.y=stats.at<int>(id,cv::CC_STAT_TOP);
            component.width=stats.at<int>(id,cv::CC_STAT_WIDTH);component.height=stats.at<int>(id,cv::CC_STAT_HEIGHT);component.area=area;
            component.centerX=centers.at<double>(id,0);component.centerY=centers.at<double>(id,1);
            component.ngMean=n;component.okMean=o;component.selectedClass=n>=o?1:2;component.score=score;
            component.scoreEvaluated=true;
            component.areaCutoff=static_cast<int>(std::ceil(std::max(1.,minimumFraction*minimumRatio*size.area())));
            component.accepted=true;component.rejection="";diagnostic->components.push_back(component);
        }
    }
    cv::Mat smallNg(size,CV_8U),smallOk(size,CV_8U);
    for(int y=0;y<size.height;++y) {
        const int* labels=ids.ptr<int>(y);unsigned char* n=smallNg.ptr<unsigned char>(y);unsigned char* o=smallOk.ptr<unsigned char>(y);
        for(int x=0;x<size.width;++x){n[x]=ngScore[labels[x]];o[x]=okScore[labels[x]];}
    }
    if (diagnostic) {
        diagnostic->Capture(smallNg, diagnostic->filteredNg);
        diagnostic->Capture(smallOk, diagnostic->filteredOk);
    }
    const auto timeScored=Clock::now();
    cv::resize(smallNg,ng,image.size(),0,0,cv::INTER_NEAREST);
    cv::resize(smallOk,ok,image.size(),0,0,cv::INTER_NEAREST);
    if (shapeCleanup > 0.f) ApplyV2ShapeCleanup(ng, ok, shapeCleanup);
    if (diagnostic) {
        diagnostic->Capture(ng, diagnostic->restoredNg);
        diagnostic->Capture(ok, diagnostic->restoredOk);
    }
    timings.ccScoreMs=ElapsedMs(timeClassified,timeScored);
    timings.nearestMs=ElapsedMs(timeScored,Clock::now());
    return 0;
}
std::shared_ptr<ov::Node> Upsample(const ov::Output<ov::Node>& input, const ov::Output<ov::Node>& size) {
    ov::op::v4::Interpolate::InterpolateAttrs attributes;
    attributes.mode = ov::op::v4::Interpolate::InterpolateMode::LINEAR_ONNX;
    attributes.shape_calculation_mode = ov::op::v4::Interpolate::ShapeCalcMode::SIZES;
    attributes.coordinate_transformation_mode = ov::op::v4::Interpolate::CoordinateTransformMode::HALF_PIXEL;
    return std::make_shared<ov::op::v4::Interpolate>(input,
        size,
        ov::opset13::Constant::create(ov::element::f32, {2}, {1.f, 1.f}),
        ov::opset13::Constant::create(ov::element::i64, {2}, {2, 3}), attributes);
}
std::shared_ptr<ov::Model> FeatureModel(ov::Core& core, const std::string& path) {
    auto imported = core.read_model(path);
    std::vector<std::string> names = {"Add_32", "Add_72", "Conv_94"};
    ov::OutputVector outputs;
    for (const auto& name : names) {
        bool found = false;
        for (const auto& node : imported->get_ordered_ops()) if (node->get_friendly_name() == name) {
            outputs.push_back(node->output(0)); found = true; break;
        }
        if (!found) throw std::runtime_error("Missing frozen feature node: " + name);
    }
    auto features = std::make_shared<ov::Model>(outputs, imported->get_parameters());
    features->reshape(originalInput ? ov::PartialShape{1, 3, -1, -1} : ov::PartialShape{1, 3, Side, Side});
    ov::Output<ov::Node> gridSize = ov::opset13::Constant::create(ov::element::i64, {2}, {Grid, Grid});
    if (originalInput) {
        auto shape = std::make_shared<ov::opset13::ShapeOf>(features->get_parameters().at(0), ov::element::i64);
        auto spatial = std::make_shared<ov::opset13::Gather>(shape,
            ov::opset13::Constant::create(ov::element::i64, {2}, {2, 3}),
            ov::opset13::Constant::create(ov::element::i64, {}, {0}));
        gridSize = std::make_shared<ov::opset13::Divide>(spatial, ov::opset13::Constant::create(ov::element::i64, {2}, {8, 8}));
    }
    ov::OutputVector levels;
    for (const auto& output : outputs) levels.push_back(Upsample(output, gridSize)->output(0));
    auto concat = std::make_shared<ov::opset13::Concat>(levels, 1);
    auto result = std::make_shared<ov::Model>(ov::OutputVector{concat}, features->get_parameters(), "NativeDefectV2_Features");
    if (result->output().get_partial_shape() != (originalInput ? ov::PartialShape{1, Channels, -1, -1} : ov::PartialShape{1, Channels, Grid, Grid})) throw std::runtime_error("Feature shape mismatch");
    return result;
}
ov::CompiledModel Compile(const std::shared_ptr<ov::Model>& model, cv::Size size, const std::string& device,
                          ov::hint::PerformanceMode mode = ov::hint::PerformanceMode::LATENCY) {
    // Compile concrete dimensions once per Check size, including on older Intel GPUs.
    auto shaped = originalInput ? model->clone() : model;
    if (originalInput) shaped->reshape(ov::PartialShape{1, 3, size.height, size.width});
    // In-memory IR graphs can share the same XML while their trained weights differ.
    // Partition disk caches by both serialized graph and weights, not the graph name.
    std::ostringstream xml, bin; ov::pass::Serialize serializer(xml, bin); serializer.run_on_model(shaped);
    uint64_t fingerprint = 14695981039346656037ull;
    auto hash = [&](const std::string& bytes) { for (unsigned char value : bytes) { fingerprint ^= value; fingerprint *= 1099511628211ull; } };
    hash(xml.str()); hash(bin.str());
    std::string cache = EasyVisionModelCacheDirectory("OpenVINO-Seg-v2");
    if (!cache.empty()) {
        try { fs::path folder = fs::u8path(cache) / std::to_string(fingerprint); fs::create_directories(folder); cache = folder.u8string(); }
        catch (...) { cache.clear(); } // An unwritable optional cache must not prevent inference.
    }
    ov::AnyMap config{ov::hint::performance_mode(mode), ov::cache_dir(cache)};
    // Optional, reproducible CPU tuning; absent/invalid preserves the OpenVINO latency-hint defaults.
#pragma warning(suppress : 4996)
    const char* threadsEnv = std::getenv("EASYAI_SEGAI_CPU_THREADS");
    if (device == "CPU" && threadsEnv && *threadsEnv) {
        char* end = nullptr; const long threads = std::strtol(threadsEnv, &end, 10);
        if (end != threadsEnv && *end == '\0' && threads > 0 && threads <= 256)
            config[ov::inference_num_threads.name()] = int(threads);
    }
    return Core().compile_model(shaped, device, config);
}
// EASYAI_SEGAI_SPLIT_TIMING diagnostic (read once; off = no backbone compile, no extra inference, no extra cost).
static bool SplitTimingRequested() {
#pragma warning(suppress : 4996)
    static const bool requested = [] { const char* value = std::getenv("EASYAI_SEGAI_SPLIT_TIMING"); return value && value[0] == '1' && value[1] == '\0'; }();
    return requested;
}
// The backbone-only graph is the concat that feeds AddHead's first 1x1 conv. Walks the output chain
// Softmax [Multiply] -> Add -> Convolution -> Relu(hidden) -> Add -> Convolution(features) and returns the features output.
std::shared_ptr<ov::Model> SplitBackbone(const std::shared_ptr<ov::Model>& head) {
    if (!head || head->get_results().empty()) return nullptr;
    std::shared_ptr<ov::Node> node = head->get_results().at(0)->get_input_node_shared_ptr(0);
    int depth = 0;
    while (node && !std::dynamic_pointer_cast<ov::opset13::Relu>(node) && depth++ < 8) node = node->get_input_node_shared_ptr(0);
    if (!std::dynamic_pointer_cast<ov::opset13::Relu>(node)) return nullptr;
    std::shared_ptr<ov::Node> bias = node->get_input_node_shared_ptr(0); // Add(conv, bias) of the hidden dense
    std::shared_ptr<ov::Node> conv = bias ? bias->get_input_node_shared_ptr(0) : nullptr;
    if (!std::dynamic_pointer_cast<ov::opset13::Convolution>(conv)) return nullptr;
    ov::Output<ov::Node> features = conv->input_value(0);
    return std::make_shared<ov::Model>(ov::OutputVector{features}, head->get_parameters(), "NativeDefectV2_Backbone");
}
// Called after every full compile of the V2 request; a failed backbone compile only disables the split timing.
void PrepareBackbone(const std::string& device, cv::Size size) {
    backboneReady = false; backboneRequest = ov::InferRequest();
    if (!SplitTimingRequested()) return;
    try {
        auto backbone = SplitBackbone(network);
        if (!backbone) return;
        backboneRequest = Compile(backbone, size, device).create_infer_request();
        backboneReady = true;
    } catch (const std::exception&) {
        backboneReady = false; backboneRequest = ov::InferRequest();
    }
}
// Builds the ONNX Runtime CUDA session for this frame size. Returns false (and sets cudaFailed + cudaNote) when CUDA cannot be
// used; the caller then continues on the OpenVINO CPU device, so a missing driver/runtime never yields silent wrong output.
bool CompileCuda(const cv::Size& size) {
#ifdef SEGAI_WITH_CUDA
    try {
        auto shaped = originalInput ? network->clone() : network;
        if (originalInput) shaped->reshape(ov::PartialShape{1, 3, size.height, size.width});
        cuda.reset(); // free the previous size first
        cuda.reset(new segai_cuda::OrtCudaBackend(shaped, 0, false));
        compiled = ov::CompiledModel(); request = ov::InferRequest(); backboneReady = false; backboneRequest = ov::InferRequest();
        return true;
    } catch (const std::exception& e) { cudaNote = e.what(); }
    catch (...) { cudaNote = "unknown CUDA error"; }
    cuda.reset();
#else
    cudaNote = "BeeNativeSegAI.dll was built without CUDA support";
#endif
    cudaFailed = true;
    std::cerr << "SegAI CUDA unavailable, falling back to CPU: " << cudaNote << std::endl;
    return false;
}
std::vector<cv::Mat> PredictFrame(const Frame& frame, cv::Size restoredSize, const std::string& deviceRequested,
                                  SegV2DiagnosticCapture* diagnostic = nullptr) {
    std::string device = deviceRequested;
    if (device == "CUDA") {
        if (compiledDevice == "CUDA" && compiledSize == frame.image.size()) return Classify(frame, restoredSize, diagnostic);
        const auto timeCompile = Clock::now();
        if (CompileCuda(frame.image.size())) {
            compiledDevice = "CUDA"; compiledSize = frame.image.size();
            timings.compileMs += ElapsedMs(timeCompile, Clock::now());
            return Classify(frame, restoredSize, diagnostic);
        }
        device = "CPU"; // sticky fallback; Device() now returns "CPU" until SetGpuBackend/Load resets it
    }
    if (UseTiledOriginalInput(frame, device)) return ClassifyTiled(frame, restoredSize, device, diagnostic);
    if (compiledDevice != device || compiledSize != frame.image.size()) {
        const auto timeCompile = Clock::now();
        compiled = Compile(network, frame.image.size(), device);
        request = compiled.create_infer_request(); compiledDevice = device; compiledSize = frame.image.size();
        PrepareBackbone(device, frame.image.size());
        timings.compileMs += ElapsedMs(timeCompile, Clock::now());
    }
    return Classify(frame, restoredSize, diagnostic);
}
cv::Mat FeatureRows(ov::InferRequest& request, const Frame& frame) {
    request.set_input_tensor(Input(frame.image)); request.infer();
    auto tensor = request.get_output_tensor();
    if (tensor.get_element_type() != ov::element::f32) throw std::runtime_error("Expected f32 feature output");
    const int width = frame.image.cols / 8, height = frame.image.rows / 8;
    if (tensor.get_shape() != ov::Shape{1, Channels, size_t(height), size_t(width)}) throw std::runtime_error("Feature output shape mismatch");
    cv::Mat planar(Channels, width * height, CV_32F, tensor.data<float>()), rows;
    cv::transpose(planar, rows);
    return rows;
}
bool UseTiledOriginalInput(const Frame& frame, const std::string& device) const {
    // CPU keeps its established full-frame path.  Narrow images use their real clipped
    // edge tiles too, so no synthetic outer pixels alter convolution padding.
    return originalInput && device.find("GPU") == 0 && (frame.image.cols > TileMaximum || frame.image.rows > TileMaximum);
}
template <typename Action>
void ForEachTiledCore(const Frame& frame, Action action) const {
    // The prepared original-input canvas is already /32.  Core and halo are /32 too,
    // so crop origins and dimensions remain aligned for the /8, /16 and /32 features.
    for (int top = 0; top < frame.image.rows; top += TileCore) for (int left = 0; left < frame.image.cols; left += TileCore) {
        const int right = std::min(frame.image.cols, left + TileCore);
        const int bottom = std::min(frame.image.rows, top + TileCore);
        const cv::Rect core(left, top, right - left, bottom - top);
        const cv::Rect tile(std::max(0, left - TileHalo), std::max(0, top - TileHalo),
                            std::min(frame.image.cols, right + TileHalo) - std::max(0, left - TileHalo),
                            std::min(frame.image.rows, bottom + TileHalo) - std::max(0, top - TileHalo));
        action(core, tile);
    }
}
void BindTiledRequest(const std::shared_ptr<ov::Model>& model, const cv::Size& size, const std::string& device,
                      ov::CompiledModel& tiledCompiled, ov::InferRequest& tiledRequest, cv::Size& tiledSize) {
    if (tiledSize == size) return;
    // Replacing the request before the compiled model drops the prior tile allocation.
    tiledRequest = ov::InferRequest(); tiledCompiled = ov::CompiledModel();
    const auto compileStart = Clock::now();
    tiledCompiled = Compile(model, size, device, ov::hint::PerformanceMode::LATENCY);
    tiledRequest = tiledCompiled.create_infer_request(); tiledSize = size;
    timings.compileMs += ElapsedMs(compileStart, Clock::now());
}
Sample GatherTiled(ov::CompiledModel& tiledCompiled, ov::InferRequest& tiledRequest, cv::Size& tiledSize,
                   const std::shared_ptr<ov::Model>& model, const Frame& frame, const std::string& device,
                   std::mt19937& random) {
    cv::Mat reduced;
    const int gridWidth = frame.image.cols / 8, gridHeight = frame.image.rows / 8;
    cv::resize(frame.labels, reduced, cv::Size(gridWidth, gridHeight), 0, 0, cv::INTER_NEAREST_EXACT);
    std::vector<int> indices[MaxClasses];
    for (int y = 0; y < gridHeight; ++y) for (int x = 0; x < gridWidth; ++x) {
        const int sx = x * 8 + 3, sy = y * 8 + 3;
        if (sx < frame.left || sy < frame.top || sx >= frame.left + frame.width || sy >= frame.top + frame.height) continue;
        const int label = reduced.at<unsigned char>(y, x);
        if (label >= 0 && label < trainClasses) indices[label].push_back(y * gridWidth + x); // raster values above N (255 = ignore) are skipped
    }
    Sample sample; std::vector<int> selected;
    for (int c = 0; c < trainClasses; ++c) {
        sample.natural[c] = double(indices[c].size());
        std::shuffle(indices[c].begin(), indices[c].end(), random);
        const int limit = c == 0 ? 2000 : 450;
        if (indices[c].size() > size_t(limit)) indices[c].resize(limit);
        for (int index : indices[c]) { selected.push_back(index); sample.labels.push_back(c); }
    }
    if (selected.empty()) return sample;
    sample.rows.create(int(selected.size()), Channels, CV_32F);
    std::vector<int> destination(size_t(gridWidth) * gridHeight, -1);
    for (size_t row = 0; row < selected.size(); ++row) destination[selected[row]] = int(row);
    ForEachTiledCore(frame, [&](const cv::Rect& core, const cv::Rect& tile) {
        CheckCancel();
        BindTiledRequest(model, tile.size(), device, tiledCompiled, tiledRequest, tiledSize);
        tiledRequest.set_input_tensor(Input(frame.image(tile))); tiledRequest.infer();
        ov::Tensor tensor = tiledRequest.get_output_tensor(); // non-const: const Tensor::data<T>() is deprecated in OpenVINO 2025.4 (C4996)
        const int tileWidth = tile.width / 8, tileHeight = tile.height / 8;
        if (tensor.get_element_type() != ov::element::f32 ||
            tensor.get_shape() != ov::Shape{1, Channels, size_t(tileHeight), size_t(tileWidth)})
            throw std::runtime_error("Tiled feature output shape mismatch");
        const float* values = tensor.data<const float>();
        for (int y = core.y; y < core.y + core.height; y += 8) for (int x = core.x; x < core.x + core.width; x += 8) {
            const int row = destination[(y / 8) * gridWidth + x / 8];
            if (row < 0) continue;
            const int local = ((y - tile.y) / 8) * tileWidth + (x - tile.x) / 8;
            float* target = sample.rows.ptr<float>(row);
            for (int channel = 0; channel < Channels; ++channel) target[channel] = values[size_t(channel) * tileWidth * tileHeight + local];
        }
    });
    return sample;
}
std::vector<cv::Mat> ClassifyTiled(const Frame& frame, cv::Size restoredSize, const std::string& device,
                                   SegV2DiagnosticCapture* diagnostic) {
    // Do not leave a failed full-frame GPU graph resident while changing to bounded tiles.
    request = ov::InferRequest(); compiled = ov::CompiledModel(); compiledDevice.clear(); compiledSize = cv::Size();
    ov::CompiledModel tiledCompiled; ov::InferRequest tiledRequest; cv::Size tiledSize;
    const int gridWidth = frame.image.cols / 8, gridHeight = frame.image.rows / 8;
    std::vector<cv::Mat> grids;
    for (int channel = 0; channel < inferClasses; ++channel) grids.push_back(cv::Mat::zeros(gridHeight, gridWidth, CV_32F));
    ForEachTiledCore(frame, [&](const cv::Rect& core, const cv::Rect& tile) {
        BindTiledRequest(network, tile.size(), device, tiledCompiled, tiledRequest, tiledSize);
        const auto tensorStart = Clock::now();
        tiledRequest.set_input_tensor(Input(frame.image(tile)));
        const auto inferStart = Clock::now(); tiledRequest.infer();
        const auto inferEnd = Clock::now();
        ov::Tensor output = tiledRequest.get_output_tensor(); // non-const: see C4996 note above
        const int tileWidth = tile.width / 8, tileHeight = tile.height / 8;
        if (output.get_element_type() != ov::element::f32 ||
            output.get_shape() != ov::Shape{1, size_t(inferClasses), size_t(tileHeight), size_t(tileWidth)})
            throw std::runtime_error("Tiled V2 output shape mismatch");
        const float* values = output.data<const float>();
        const int copyWidth = core.width / 8, copyHeight = core.height / 8;
        const int localLeft = (core.x - tile.x) / 8, localTop = (core.y - tile.y) / 8;
        for (int channel = 0; channel < inferClasses; ++channel) for (int row = 0; row < copyHeight; ++row)
            std::memcpy(grids[channel].ptr<float>(core.y / 8 + row) + core.x / 8,
                        values + size_t(channel) * tileWidth * tileHeight + size_t(localTop + row) * tileWidth + localLeft,
                        size_t(copyWidth) * sizeof(float));
        timings.tensorMs += ElapsedMs(tensorStart, inferStart); timings.inferMs += ElapsedMs(inferStart, inferEnd);
    });
    if (diagnostic) {
        diagnostic->networkOutputGrid = cv::Size(gridWidth, gridHeight);
        diagnostic->Capture(grids[0], diagnostic->networkBg);
        if (grids.size() > 1) diagnostic->Capture(grids[1], diagnostic->networkNg);
        if (grids.size() > 2) diagnostic->Capture(grids[2], diagnostic->networkOk);
    }
    const auto restoreStart = Clock::now(); std::vector<cv::Mat> classes;
    for (int channel = 0; channel < inferClasses; ++channel) {
        cv::Mat big, restored;
        cv::resize(grids[channel], big, frame.image.size());
        cv::resize(big(cv::Rect(frame.left, frame.top, frame.width, frame.height)), restored, restoredSize);
        if (diagnostic) {
            if (channel == 0) diagnostic->Capture(restored, diagnostic->restoredCompactBg);
            else if (channel == 1) diagnostic->Capture(restored, diagnostic->restoredCompactNg);
            else if (channel == 2) diagnostic->Capture(restored, diagnostic->restoredCompactOk);
        }
        classes.push_back(restored);
    }
    timings.upsampleMs += ElapsedMs(restoreStart, Clock::now());
    return classes;
}
Sample Gather(const cv::Mat& rows, const Frame& frame, std::mt19937& random) {
    cv::Mat reduced;
    const int width = frame.image.cols / 8, height = frame.image.rows / 8;
    cv::resize(frame.labels, reduced, cv::Size(width, height), 0, 0, cv::INTER_NEAREST_EXACT);
    std::vector<int> indices[MaxClasses];
    for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
        // This prototype assumes all target objects in each pilot image are annotated.
        // Outside-target samples are background, never the OK class. Exclude letterbox padding.
        // INTER_NEAREST_EXACT selects the lower center pixel for an 8:1 reduction.
        const int sx = x * 8 + 3, sy = y * 8 + 3;
        if (sx < frame.left || sy < frame.top || sx >= frame.left + frame.width || sy >= frame.top + frame.height) continue;
        int label = reduced.at<unsigned char>(y,x);
        if (label >= 0 && label < trainClasses) indices[label].push_back(y * width + x); // raster values above N (255 = ignore) are skipped
    }
    Sample sample;
    for (int c = 0; c < trainClasses; ++c) sample.natural[c] = double(indices[c].size()); // V161: uncapped counts, no RNG use
    for (int c = 0; c < trainClasses; ++c) {
        std::shuffle(indices[c].begin(), indices[c].end(), random);
        // Cover more background appearances instead of repeatedly over-weighting scarce foreground pixels.
        const int limit = c == 0 ? 2000 : 450;
        if (indices[c].size() > size_t(limit)) indices[c].resize(limit);
        for (int index : indices[c]) { sample.rows.push_back(rows.row(index)); sample.labels.push_back(c); }
    }
    return sample;
}
// EAI-POS P3: instance training rows of one job. Label coordinates (pixels of the image given to AddSample) go through the exact
// geometry of the job: resize to the stored content (Side / max(W, H), no padding), the variant's warpAffine matrix M (index
// coordinates, hence the half pixel conjugation) and the letterbox of Prepare(). Returns false when the labels cannot follow the
// transform (anisotropic scale beyond 1%): the variant then contributes no instance rows. Own RNG stream {41, image, variant}; the
// semantic stream {17|29, image, variant} and Gather() are not involved.
bool GatherInstances(const cv::Mat& featureRows, const cv::Mat& transform, const cv::Size& viewSize, size_t image, int variant, InstanceTrainRows& out) {
    const cv::Size source = instanceSourceSize[image];
    std::vector<InstanceLabel> labels = instanceLabels[image];
    Affine2x3 chain = MakeLetterboxAffine(source.width, source.height, Side);
    chain.m[2] = 0; chain.m[5] = 0; // stored content has no letterbox padding
    if (!transform.empty()) {
        Affine2x3 m;
        for (int i = 0; i < 6; ++i) m.m[i] = transform.at<double>(i / 3, i % 3);
        Affine2x3 shiftIn, shiftOut; shiftIn.m[2] = -.5; shiftIn.m[5] = -.5; shiftOut.m[2] = .5; shiftOut.m[5] = .5;
        chain = ComposeAffine(chain, ComposeAffine(ComposeAffine(shiftIn, m), shiftOut));
    }
    chain = ComposeAffine(chain, MakeLetterboxAffine(viewSize.width, viewSize.height, Side));
    if (!ApplyAffineToLabels(chain, labels)) return false;
    InstanceTargets targets;
    RenderInstanceTargets(labels, trainClasses - 1, instanceSymmetryConfig, targets, Grid, 8);
    std::seed_seq seed{41u, unsigned(image), unsigned(variant)}; std::mt19937 random(seed);
    GatherInstanceRows(featureRows, targets, random, kInstanceMaxRowsPerJob, out);
    out.dropped = targets.droppedLabels;
    return true;
}
struct Head { cv::Mat weights, bias, hiddenWeights, hiddenBias, mean, sd; };
static constexpr int HeadHidden = 32, HeadBlockRows = 4096;
// U570: tp/fp/fn are per-class argmax counts (report-only telemetry, never used by optimization or best-epoch selection).
struct HeadGrad { cv::Mat weights, bias, hiddenWeights, hiddenBias; double loss = 0, weight = 0; double tp[MaxClasses] = {}, fp[MaxClasses] = {}, fn[MaxClasses] = {}; };
// V145 Retrain: raw (pre-fold, pre-mask) head snapshots and the resume request state carried from Configure
// into Train/Save. resumeHead is the head loaded from a previous model's .segai file (when requested and
// compatible); savedHead is this run's raw head, written on every Save so any future run can resume from it.
Head resumeHead, savedHead; bool resumeRequested=false, resumeLoaded=false, hasSavedHead=false;
float resumeRateScale=1.f; std::string resumeNote; unsigned long long resumeActive=0; int resumeSide=0, resumeNativeV2=0;
bool ReadResumeHead(const fs::path& path, std::string& reason);
// EAI-CLS S1 named classes: classTableSet = a caller supplied table (Save then writes format v4, otherwise the byte-identical
// v3); Load sets it for a v4 file. resumeTable = class table of the resume source (legacy NG/OK for a v3 source).
// ReconcileResumeClasses drops the resume (fresh start, "class set changed") when the class names differ.
SegClassTable classTable, resumeTable; bool classTableSet=false;
void ReconcileResumeClasses();
// EAI-CLS S2a inference-side class layout, set by Load only (the trainer uses trainClasses): inferClasses = K =
// N + 1 channels of the network output; genericClasses = the table is not the legacy NG/OK layout, so inference uses
// PredictCompactClasses instead of the K=3 path.
int inferClasses=3; bool genericClasses=false;
// EAI-CLS S3: class id / component score maps (size of the last Predict input) of the last generalised-path Predict; empty
// after a K=3 NG/OK Predict or a failed one. Shares storage with the maps the legacy ng/ok outputs are derived from.
cv::Mat lastLabelMap, lastScoreMap;
// EAI-POS P3 instance head. Trainer: instanceHead (settings key) + the labels attached by AddInstances, parallel to `images`
// (instanceSourceSize = size of the image given to AddSample, the frame of the label coordinates). The trained / loaded branch
// is instanceWeights (instanceReady); inference compiles it together with the backbone into instanceCompiled on demand.
// It never touches the semantic head, `network` or the semantic training RNG.
bool instanceHead=false; int instanceEpochs=60; float instanceLearningRate=2e-3f; std::vector<int> instanceSymmetryConfig;
std::vector<std::vector<InstanceLabel>> instanceLabels; std::vector<cv::Size> instanceSourceSize;
InstanceHeadWeights instanceWeights; bool instanceReady=false;
ov::CompiledModel instanceCompiled; ov::InferRequest instanceRequest; std::string instanceDevice;
// EAI-POS P4: class templates for the quality refinement (empty = no template, boxes fall back to the head). instanceTemplates is
// trained or loaded; the sum/count pair is the trainer accumulator filled by AddInstances from the RotatedBox labels of each sample.
InstanceTemplateSet instanceTemplates; cv::Mat instanceTemplateSum; std::vector<int> instanceTemplateCount;
std::shared_ptr<ov::Node> FindFeatureConcat(const std::shared_ptr<ov::Model>& model) const {
    for (const auto& node : model->get_ordered_ops()) {
        if (std::string(node->get_type_name()) != "Concat") continue;
        const ov::PartialShape shape = node->get_output_partial_shape(0);
        if (shape.rank().is_static() && shape.rank().get_length() == 4 && shape[1].is_static() && shape[1].get_length() == Channels) return node;
    }
    return nullptr;
}
// 1x1 convolution + bias on weights stored as in Head (rows = inputs, cols = outputs).
static ov::Output<ov::Node> Dense1x1(const ov::Output<ov::Node>& input, const cv::Mat& weights, const cv::Mat& bias) {
    std::vector<float> w(size_t(weights.cols)*weights.rows), b(weights.cols);
    for (int c=0;c<weights.cols;++c) {
        b[c]=bias.at<float>(0,c);
        for(int j=0;j<weights.rows;++j) w[size_t(c)*weights.rows+j]=weights.at<float>(j,c);
    }
    auto kernel=ov::opset13::Constant::create(ov::element::f32,{size_t(weights.cols),size_t(weights.rows),1,1},w);
    auto conv=std::make_shared<ov::opset13::Convolution>(input,kernel,ov::Strides{1,1},ov::CoordinateDiff{0,0},ov::CoordinateDiff{0,0},ov::Strides{1,1});
    return std::make_shared<ov::opset13::Add>(conv,ov::opset13::Constant::create(ov::element::f32,{1,size_t(weights.cols),1,1},b));
}
// Backbone + instance branch: one output of C + 7 planes (heat logits, then regression) on the Grid x Grid feature grid. Built from a clone of
// the semantic network so the semantic graph (and the saved IR) is untouched.
std::shared_ptr<ov::Model> BuildInstanceNetwork() const {
    auto clone = network->clone();
    auto concat = FindFeatureConcat(clone);
    if (!concat) throw std::runtime_error("Feature concat of the instance head was not found in the network");
    auto hidden = std::make_shared<ov::opset13::Relu>(Dense1x1(concat->output(0), instanceWeights.w1, instanceWeights.b1));
    ov::Output<ov::Node> result = Dense1x1(hidden->output(0), instanceWeights.w2, instanceWeights.b2);
    return std::make_shared<ov::Model>(ov::OutputVector{result}, clone->get_parameters(), "NativeDefectV2_Instance");
}
// SEG-LIFECYCLE-r11: trains backbone + head jointly (libtorch), rewrites the OpenVINO backbone constants of `features` in place
// and replaces `head` with the fine-tuned head. Throws on any failure (the caller then fails the whole training).
void FineTuneBackbone(const std::shared_ptr<ov::Model>& features, Head& head);
void WriteResumeStatus(const std::string& text);
// V146 Retrain (changed images only): indices (into images/validationIndices space) the managed side asked to
// skip from feature extraction, honored only when a resume actually succeeded and a validation split exists.
std::set<int> trainSkipIndices; bool deltaRequested=false, deltaActive=false; float deltaTolerance=.05f;
void WriteDeltaStatus(const std::string& text);
// V161 prior-balanced class weights: priorBalanceRequested = settings key priorBalance==1; resumePriorBalanced = the
// resumed model's own prior_balanced payload key; priorBalanced = effective mode (a resumed head keeps its own mode so
// the loss scale of the loaded head never mixes). Only a balanced model writes prior_balanced into its payload.
bool priorBalanceRequested=false, priorBalanced=false, resumePriorBalanced=false, priorKeyPresent=false; std::string priorHeader;
void WritePriorStatus(const std::string& text);
// One row block of the head: forward, softmax cross-entropy and (optionally) the backward pass.
// Blocks are independent so full-batch epochs spread over all cores; gradients stay unnormalized here.
// V161: classWeight (nullptr = legacy, every row weight 1 via the original statements) holds one per-class row weight;
// the weighted loss, its normaliser and the logit gradient all use it, so train and validation share one objective.
// EAI-PLUS S5: the fast path below fuses forward/softmax/backward over L2-sized row chunks with hand-written AVX2/FMA
// kernels for the two 448x32 products (cv::gemm with GEMM_1_T and 32 columns was the bottleneck). The reference path is
// kept as the fallback (no AVX2/FMA, unexpected shapes) and can be forced with EASYAI_HEAD_REFERENCE=1 for A/B checks.
static bool HeadFastKernels() {
#pragma warning(suppress : 4996)
    static const bool ok = cv::checkHardwareSupport(CV_CPU_AVX2) && cv::checkHardwareSupport(CV_CPU_FMA3) && !std::getenv("EASYAI_HEAD_REFERENCE");
    return ok;
}
// h0/h1 = relu(bias + x0/x1 * W) for two rows at once (W is cols x 32, row-major); sequential sum over k per lane.
static void HeadForwardPair(const float* x0, const float* x1, int cols, const float* W, const float* bias, float* h0, float* h1) {
    __m256 a0 = _mm256_setzero_ps(), a1 = a0, a2 = a0, a3 = a0, b0 = a0, b1 = a0, b2 = a0, b3 = a0;
    for (int k = 0; k < cols; ++k) {
        const float* w = W + k * HeadHidden;
        const __m256 w0 = _mm256_loadu_ps(w), w1 = _mm256_loadu_ps(w + 8), w2 = _mm256_loadu_ps(w + 16), w3 = _mm256_loadu_ps(w + 24);
        const __m256 s = _mm256_set1_ps(x0[k]), t = _mm256_set1_ps(x1[k]);
        a0 = _mm256_fmadd_ps(s, w0, a0); a1 = _mm256_fmadd_ps(s, w1, a1); a2 = _mm256_fmadd_ps(s, w2, a2); a3 = _mm256_fmadd_ps(s, w3, a3);
        b0 = _mm256_fmadd_ps(t, w0, b0); b1 = _mm256_fmadd_ps(t, w1, b1); b2 = _mm256_fmadd_ps(t, w2, b2); b3 = _mm256_fmadd_ps(t, w3, b3);
    }
    const __m256 zero = _mm256_setzero_ps();
    const __m256 c0 = _mm256_loadu_ps(bias), c1 = _mm256_loadu_ps(bias + 8), c2 = _mm256_loadu_ps(bias + 16), c3 = _mm256_loadu_ps(bias + 24);
    _mm256_storeu_ps(h0, _mm256_max_ps(zero, _mm256_add_ps(a0, c0))); _mm256_storeu_ps(h0 + 8, _mm256_max_ps(zero, _mm256_add_ps(a1, c1)));
    _mm256_storeu_ps(h0 + 16, _mm256_max_ps(zero, _mm256_add_ps(a2, c2))); _mm256_storeu_ps(h0 + 24, _mm256_max_ps(zero, _mm256_add_ps(a3, c3)));
    _mm256_storeu_ps(h1, _mm256_max_ps(zero, _mm256_add_ps(b0, c0))); _mm256_storeu_ps(h1 + 8, _mm256_max_ps(zero, _mm256_add_ps(b1, c1)));
    _mm256_storeu_ps(h1 + 16, _mm256_max_ps(zero, _mm256_add_ps(b2, c2))); _mm256_storeu_ps(h1 + 24, _mm256_max_ps(zero, _mm256_add_ps(b3, c3)));
}
// G (cols x 32) += x0^T * d0 + x1^T * d1 (outer products of two rows).
static void HeadAccumulatePair(const float* x0, const float* x1, int cols, const float* d0, const float* d1, float* G) {
    const __m256 p0 = _mm256_loadu_ps(d0), p1 = _mm256_loadu_ps(d0 + 8), p2 = _mm256_loadu_ps(d0 + 16), p3 = _mm256_loadu_ps(d0 + 24);
    const __m256 q0 = _mm256_loadu_ps(d1), q1 = _mm256_loadu_ps(d1 + 8), q2 = _mm256_loadu_ps(d1 + 16), q3 = _mm256_loadu_ps(d1 + 24);
    for (int k = 0; k < cols; ++k) {
        float* g = G + k * HeadHidden;
        const __m256 s = _mm256_set1_ps(x0[k]), t = _mm256_set1_ps(x1[k]);
        __m256 g0 = _mm256_loadu_ps(g), g1 = _mm256_loadu_ps(g + 8), g2 = _mm256_loadu_ps(g + 16), g3 = _mm256_loadu_ps(g + 24);
        g0 = _mm256_fmadd_ps(s, p0, g0); g1 = _mm256_fmadd_ps(s, p1, g1); g2 = _mm256_fmadd_ps(s, p2, g2); g3 = _mm256_fmadd_ps(s, p3, g3);
        g0 = _mm256_fmadd_ps(t, q0, g0); g1 = _mm256_fmadd_ps(t, q1, g1); g2 = _mm256_fmadd_ps(t, q2, g2); g3 = _mm256_fmadd_ps(t, q3, g3);
        _mm256_storeu_ps(g, g0); _mm256_storeu_ps(g + 8, g1); _mm256_storeu_ps(g + 16, g2); _mm256_storeu_ps(g + 24, g3);
    }
}
void HeadBlock(const cv::Mat& x, const cv::Mat& labels, int begin, int end, const Head& head, bool gradient, HeadGrad& out, const float* classWeight) const {
    const int K = trainClasses;
    const bool shapesOk = x.type() == CV_32F && x.cols > 0 && head.hiddenWeights.type() == CV_32F && head.hiddenWeights.isContinuous()
        && head.hiddenWeights.rows == x.cols && head.hiddenWeights.cols == HeadHidden && head.hiddenBias.type() == CV_32F && head.hiddenBias.isContinuous()
        && head.hiddenBias.total() == size_t(HeadHidden) && head.weights.type() == CV_32F && head.weights.isContinuous()
        && head.weights.rows == HeadHidden && head.weights.cols == K && head.bias.type() == CV_32F && head.bias.isContinuous()
        && head.bias.total() == size_t(K) && K > 0 && K <= MaxClasses && labels.type() == CV_32S;
    if (!shapesOk || !HeadFastKernels()) { HeadBlockReference(x, labels, begin, end, head, gradient, out, classWeight); return; }
    constexpr int Chunk = 256;
    bool enabled[MaxClasses]; for (int c = 0; c < K; ++c) enabled[c] = ClassEnabled(c);
    const int cols = x.cols;
    const float* W1 = head.hiddenWeights.ptr<float>(); const float* hiddenBias = head.hiddenBias.ptr<float>();
    const float* W2 = head.weights.ptr<float>(); const float* bias = head.bias.ptr<float>();
    thread_local std::vector<float> hiddenBuffer, gradBuffer;
    if (hiddenBuffer.size() < size_t(Chunk) * HeadHidden) { hiddenBuffer.resize(size_t(Chunk) * HeadHidden); gradBuffer.resize(size_t(Chunk) * HeadHidden); }
    double gW2[HeadHidden * MaxClasses] = {}, gB[MaxClasses] = {}, gHB[HeadHidden] = {};
    float* gW1 = nullptr;
    if (gradient) { out.hiddenWeights = cv::Mat::zeros(cols, HeadHidden, CV_32F); gW1 = out.hiddenWeights.ptr<float>(); }
    const float zeroRow[HeadHidden] = {}; float spare[HeadHidden];
    for (int c0 = begin; c0 < end; c0 += Chunk) {
        const int n = std::min(Chunk, end - c0);
        float* hid = hiddenBuffer.data(); float* dh = gradBuffer.data();
        for (int r = 0; r < n; r += 2) {
            const float* x0 = x.ptr<float>(c0 + r);
            if (r + 1 < n) HeadForwardPair(x0, x.ptr<float>(c0 + r + 1), cols, W1, hiddenBias, hid + r * HeadHidden, hid + (r + 1) * HeadHidden);
            else HeadForwardPair(x0, x0, cols, W1, hiddenBias, hid + r * HeadHidden, spare);
        }
        for (int r = 0; r < n; ++r) {
            const float* h = hid + r * HeadHidden; const int label = labels.at<int>(c0 + r);
            float p[MaxClasses];
            float peak = -std::numeric_limits<float>::infinity();
            for (int c = 0; c < K; ++c) {
                float s = 0.f; for (int j = 0; j < HeadHidden; ++j) s += h[j] * W2[j * K + c];
                p[c] = s + bias[c]; if (enabled[c]) peak = std::max(peak, p[c]);
            }
            float sum = 0; for (int c = 0; c < K; ++c) { p[c] = enabled[c] ? std::exp(p[c] - peak) : 0.f; sum += p[c]; }
            for (int c = 0; c < K; ++c) p[c] /= sum;
            if (classWeight) { const float w = classWeight[label]; out.loss -= double(w) * std::log(std::max(p[label], 1e-8f)); out.weight += w; }
            else { out.loss -= std::log(std::max(p[label], 1e-8f)); out.weight += 1; }
            int predicted = 0;
            for (int c = 1; c < K; ++c) if (enabled[c] && p[c] > p[predicted]) predicted = c;
            if (predicted == label) out.tp[label] += 1; else { out.fp[predicted] += 1; out.fn[label] += 1; }
            if (!gradient) continue;
            p[label] -= 1.f;
            if (classWeight) { const float w = classWeight[label]; for (int c = 0; c < K; ++c) p[c] *= w; }
            float* d = dh + r * HeadHidden;
            for (int c = 0; c < K; ++c) gB[c] += p[c];
            for (int j = 0; j < HeadHidden; ++j) {
                const float hj = h[j];
                if (hj <= 0) { d[j] = 0.f; continue; }
                float s = 0.f;
                for (int c = 0; c < K; ++c) { s += p[c] * W2[j * K + c]; gW2[j * K + c] += double(hj) * p[c]; }
                d[j] = s; gHB[j] += s;
            }
        }
        if (!gradient) continue;
        for (int r = 0; r < n; r += 2) {
            const float* x0 = x.ptr<float>(c0 + r);
            if (r + 1 < n) HeadAccumulatePair(x0, x.ptr<float>(c0 + r + 1), cols, dh + r * HeadHidden, dh + (r + 1) * HeadHidden, gW1);
            else HeadAccumulatePair(x0, x0, cols, dh + r * HeadHidden, zeroRow, gW1);
        }
    }
    if (!gradient) return;
    out.weights = cv::Mat(HeadHidden, K, CV_32F); out.bias = cv::Mat(1, K, CV_32F); out.hiddenBias = cv::Mat(1, HeadHidden, CV_32F);
    for (int i = 0; i < HeadHidden * K; ++i) out.weights.ptr<float>()[i] = float(gW2[i]);
    for (int c = 0; c < K; ++c) out.bias.ptr<float>()[c] = float(gB[c]);
    for (int j = 0; j < HeadHidden; ++j) out.hiddenBias.ptr<float>()[j] = float(gHB[j]);
}
void HeadBlockReference(const cv::Mat& x, const cv::Mat& labels, int begin, int end, const Head& head, bool gradient, HeadGrad& out, const float* classWeight) const {
    const int K = trainClasses;
    bool enabled[MaxClasses]; for (int c = 0; c < K; ++c) enabled[c] = ClassEnabled(c);
    const cv::Mat block = x.rowRange(begin, end);
    cv::Mat hidden; cv::gemm(block, head.hiddenWeights, 1, cv::noArray(), 0, hidden);
    const float* hiddenBias = head.hiddenBias.ptr<float>();
    for (int r = 0; r < hidden.rows; ++r) { float* h = hidden.ptr<float>(r); for (int c = 0; c < HeadHidden; ++c) h[c] = std::max(0.f, h[c] + hiddenBias[c]); }
    cv::Mat logits; cv::gemm(hidden, head.weights, 1, cv::noArray(), 0, logits);
    const float* bias = head.bias.ptr<float>();
    for (int row = 0; row < logits.rows; ++row) {
        float* p = logits.ptr<float>(row); const int label = labels.at<int>(begin + row);
        float peak = -std::numeric_limits<float>::infinity();
        for (int c = 0; c < K; ++c) { p[c] += bias[c]; if (enabled[c]) peak = std::max(peak, p[c]); }
        float sum = 0; for (int c = 0; c < K; ++c) { p[c] = enabled[c] ? std::exp(p[c] - peak) : 0.f; sum += p[c]; }
        for (int c = 0; c < K; ++c) p[c] /= sum;
        if (classWeight) { const float w = classWeight[label]; out.loss -= double(w) * std::log(std::max(p[label], 1e-8f)); out.weight += w; }
        else { out.loss -= std::log(std::max(p[label], 1e-8f)); out.weight += 1; }
        // U570: argmax over enabled classes (disabled classes have probability 0) for per-epoch IoU telemetry.
        int predicted = 0;
        for (int c = 1; c < K; ++c) if (enabled[c] && p[c] > p[predicted]) predicted = c;
        if (predicted == label) out.tp[label] += 1; else { out.fp[predicted] += 1; out.fn[label] += 1; }
        if (gradient) {
            p[label] -= 1.f;
            if (classWeight) { const float w = classWeight[label]; for (int c = 0; c < K; ++c) p[c] *= w; }
        }
    }
    if (!gradient) return;
    cv::gemm(hidden, logits, 1, cv::noArray(), 0, out.weights, cv::GEMM_1_T);
    cv::reduce(logits, out.bias, 0, cv::REDUCE_SUM);
    cv::Mat hiddenGradient; cv::gemm(logits, head.weights, 1, cv::noArray(), 0, hiddenGradient, cv::GEMM_2_T);
    for (int r = 0; r < hidden.rows; ++r) {
        const float* h = hidden.ptr<float>(r); float* g = hiddenGradient.ptr<float>(r);
        for (int c = 0; c < HeadHidden; ++c) if (h[c] <= 0) g[c] = 0;
    }
    cv::gemm(block, hiddenGradient, 1, cv::noArray(), 0, out.hiddenWeights, cv::GEMM_1_T);
    cv::reduce(hiddenGradient, out.hiddenBias, 0, cv::REDUCE_SUM);
}
// Mean loss over all rows; with gradient, fills the loss-normalized gradient (no regularization).
// U570: optional meanIou receives the mean foreground (NG/OK) IoU of this pass's argmax predictions (nan when no
// enabled foreground class has any truth or prediction). It never influences the returned loss or the gradient.
double HeadPass(const cv::Mat& x, const cv::Mat& labels, const Head& head, bool gradient, HeadGrad* total, double* meanIou = nullptr, const float* classWeight = nullptr) const {
    const int blocks = (x.rows + HeadBlockRows - 1) / HeadBlockRows;
    std::vector<HeadGrad> parts(blocks);
    cv::parallel_for_(cv::Range(0, blocks), [&](const cv::Range& range) {
        for (int b = range.start; b < range.end; ++b)
            HeadBlock(x, labels, b * HeadBlockRows, std::min(x.rows, (b + 1) * HeadBlockRows), head, gradient, parts[b], classWeight);
    });
    // Summing in block order keeps every epoch independent of thread scheduling.
    double loss = 0, weight = 0, tp[MaxClasses] = {}, fp[MaxClasses] = {}, fn[MaxClasses] = {};
    for (int b = 0; b < blocks; ++b) {
        loss += parts[b].loss; weight += parts[b].weight;
        for (int c = 0; c < trainClasses; ++c) { tp[c] += parts[b].tp[c]; fp[c] += parts[b].fp[c]; fn[c] += parts[b].fn[c]; }
        if (!gradient) continue;
        if (!b) { total->weights = parts[b].weights; total->bias = parts[b].bias; total->hiddenWeights = parts[b].hiddenWeights; total->hiddenBias = parts[b].hiddenBias; }
        else { total->weights += parts[b].weights; total->bias += parts[b].bias; total->hiddenWeights += parts[b].hiddenWeights; total->hiddenBias += parts[b].hiddenBias; }
    }
    if (gradient) {
        const double scale = 1. / std::max(1., weight);
        total->weights *= scale; total->bias *= scale; total->hiddenWeights *= scale; total->hiddenBias *= scale;
    }
    if (meanIou) {
        double sum = 0; int used = 0;
        for (int c = 1; c < trainClasses; ++c) {
            const double union_ = tp[c] + fp[c] + fn[c];
            if (!ClassEnabled(c) || union_ <= 0) continue;
            sum += tp[c] / union_; ++used;
        }
        *meanIou = used ? sum / used : std::numeric_limits<double>::quiet_NaN();
    }
    return loss / std::max(1., weight);
}
static cv::Mat ColumnSum(const cv::Mat& rows, bool squares) {
    const int blocks = (rows.rows + HeadBlockRows - 1) / HeadBlockRows;
    std::vector<cv::Mat> parts(blocks);
    cv::parallel_for_(cv::Range(0, blocks), [&](const cv::Range& range) {
        for (int b = range.start; b < range.end; ++b) {
            const cv::Mat block = rows.rowRange(b * HeadBlockRows, std::min(rows.rows, (b + 1) * HeadBlockRows));
            cv::reduce(squares ? cv::Mat(block.mul(block)) : block, parts[b], 0, cv::REDUCE_SUM, CV_64F);
        }
    });
    cv::Mat sum = cv::Mat::zeros(1, rows.cols, CV_64F);
    for (const auto& part : parts) sum += part;
    return sum;
}
static void Standardize(cv::Mat& rows, const cv::Mat& mean, const cv::Mat& sd) {
    cv::parallel_for_(cv::Range(0, rows.rows), [&](const cv::Range& range) {
        const float* m = mean.ptr<float>(); const float* s = sd.ptr<float>();
        for (int r = range.start; r < range.end; ++r) { float* p = rows.ptr<float>(r); for (int c = 0; c < rows.cols; ++c) p[c] = (p[c] - m[c]) / s[c]; }
    });
}
// V149: samples.rows / validation.rows are consumed and standardised IN PLACE (no clone, to avoid duplicating the
// multi-GB feature matrix). The caller must not reuse or read these Sample buffers as raw features afterwards.
Head Train(Sample& samples, Sample& validation, const fs::path& out) {
    if (samples.rows.empty()) throw std::runtime_error("No usable pixels after ROI alignment and feature sampling");
    Head head;
    cv::Mat x = samples.rows;
    std::unique_ptr<StageTimer> standardizeTimer(new StageTimer(*this, "head_standardize"));
    // Double column sums keep E[x^2] - mean^2 accurate for float features.
    cv::Mat mean64 = ColumnSum(x, false) / double(x.rows), variance = ColumnSum(x, true) / double(x.rows) - mean64.mul(mean64);
    cv::max(variance, 0., variance); mean64.convertTo(head.mean, CV_32F); variance.convertTo(variance, CV_32F);
    cv::sqrt(variance, head.sd); cv::max(head.sd, .1f, head.sd);
    Standardize(x, head.mean, head.sd);
    const bool hasValidation = !validation.rows.empty();
    // Recheck mode (no validation split): vx aliases the already-standardised x, so it must not be standardised again.
    cv::Mat vx = hasValidation ? validation.rows : x;
    if (hasValidation) Standardize(vx, head.mean, head.sd);
    standardizeTimer.reset();
    const cv::Mat& validationLabels = hasValidation ? validation.labels : samples.labels;
    const int hidden = HeadHidden;
    // V145 Retrain: resumed starts mutable so a failed self-check (below) can fall back to the fresh path;
    // resumeLoaded itself (set in Configure, structural/shape checks already passed) never changes here.
    bool resumed = resumeLoaded;
    bool rebaseCheckFailed = false;
    double rebaseMaxAbs = std::numeric_limits<double>::quiet_NaN();
    if (resumed) {
        head.hiddenWeights = cv::Mat(Channels, hidden, CV_32F); head.hiddenBias = cv::Mat(1, hidden, CV_32F);
        head.weights = resumeHead.weights.clone(); head.bias = resumeHead.bias.clone();
        // Rebase the previous head into this run's standardization basis so the epoch-0 output is
        // algebraically unchanged from the previous model: hiddenWeights'=hiddenWeights_old*sd_new/sd_old,
        // hiddenBias'=hiddenBias_old + sum_f hiddenWeights_old[f]*(mean_new[f]-mean_old[f])/sd_old[f].
        for (int c = 0; c < hidden; ++c) {
            double bias = double(resumeHead.hiddenBias.at<float>(c));
            for (int f = 0; f < Channels; ++f) {
                const float so = resumeHead.sd.at<float>(f), sn = head.sd.at<float>(f);
                const float wOld = resumeHead.hiddenWeights.at<float>(f, c);
                head.hiddenWeights.at<float>(f, c) = wOld * sn / so;
                bias += double(wOld) * (double(head.mean.at<float>(f)) - double(resumeHead.mean.at<float>(f))) / double(so);
            }
            head.hiddenBias.at<float>(c) = float(bias);
        }
        // Self-check: compare old-path vs rebased hidden pre-activations on up to 64 sample rows.
        double maxAbsDiff = 0.;
        const int checkRows = std::min(x.rows, 64);
        for (int r = 0; r < checkRows; ++r) {
            for (int c = 0; c < hidden; ++c) {
                double oldPre = double(resumeHead.hiddenBias.at<float>(c));
                double newPre = double(head.hiddenBias.at<float>(c));
                for (int f = 0; f < Channels; ++f) {
                    // x is already standardized with head.mean/head.sd (the new basis); recover the
                    // un-standardized feature value to evaluate both the old and the rebased formula.
                    const double raw = double(x.at<float>(r, f)) * double(head.sd.at<float>(f)) + double(head.mean.at<float>(f));
                    oldPre += (raw - double(resumeHead.mean.at<float>(f))) / double(resumeHead.sd.at<float>(f)) * double(resumeHead.hiddenWeights.at<float>(f, c));
                    newPre += (raw - double(head.mean.at<float>(f))) / double(head.sd.at<float>(f)) * double(head.hiddenWeights.at<float>(f, c));
                }
                const double d = std::abs(newPre - oldPre);
                if (!std::isfinite(d)) maxAbsDiff = std::numeric_limits<double>::infinity(); else maxAbsDiff = std::max(maxAbsDiff, d);
            }
        }
        if (!cv::checkRange(head.hiddenWeights) || !cv::checkRange(head.hiddenBias)) maxAbsDiff = std::numeric_limits<double>::infinity();
        rebaseMaxAbs = maxAbsDiff;
        if (!std::isfinite(maxAbsDiff)) {
            resumed = false; rebaseCheckFailed = true;
            cv::RNG random(17);
            head.hiddenWeights = cv::Mat(Channels, hidden, CV_32F); head.hiddenBias = cv::Mat::zeros(1, hidden, CV_32F);
            head.weights = cv::Mat(hidden, trainClasses, CV_32F); head.bias = cv::Mat::zeros(1, trainClasses, CV_32F);
            random.fill(head.hiddenWeights, cv::RNG::UNIFORM, -1.f/std::sqrt(float(Channels)), 1.f/std::sqrt(float(Channels)));
            random.fill(head.weights, cv::RNG::UNIFORM, -1.f/std::sqrt(float(hidden)), 1.f/std::sqrt(float(hidden)));
        }
    } else {
        cv::RNG random(17);
        head.hiddenWeights = cv::Mat(Channels, hidden, CV_32F); head.hiddenBias = cv::Mat::zeros(1, hidden, CV_32F);
        head.weights = cv::Mat(hidden, trainClasses, CV_32F); head.bias = cv::Mat::zeros(1, trainClasses, CV_32F);
        random.fill(head.hiddenWeights, cv::RNG::UNIFORM, -1.f/std::sqrt(float(Channels)), 1.f/std::sqrt(float(Channels)));
        random.fill(head.weights, cv::RNG::UNIFORM, -1.f/std::sqrt(float(hidden)), 1.f/std::sqrt(float(hidden)));
    }
    // V146 Retrain (changed images only): deltaActive requires resumeLoaded (set in Configure); if the rebase
    // self-check above just flipped `resumed` back to false, a changed-only run can no longer proceed safely
    // (the unchanged/skipped images would get zero gradient exposure from a random-init head) — reject it so the
    // managed side reruns on the full dataset (D2) instead of silently producing a bad model.
    if (deltaActive && !resumed) throw std::runtime_error(std::string("[retrain-delta-rejected] rebase check failed"));
    const float baseRate = resumed ? learningRate * resumeRateScale : learningRate;
    cv::Mat firstW = cv::Mat::zeros(head.weights.size(), CV_32F), secondW = firstW.clone();
    cv::Mat firstB = cv::Mat::zeros(head.bias.size(), CV_32F), secondB = firstB.clone();
    cv::Mat firstH = cv::Mat::zeros(head.hiddenWeights.size(), CV_32F), secondH = firstH.clone();
    cv::Mat firstHB = cv::Mat::zeros(head.hiddenBias.size(), CV_32F), secondHB = firstHB.clone();
    std::vector<float> counts(trainClasses, 0);
    for (int r = 0; r < samples.labels.rows; ++r) ++counts[samples.labels.at<int>(r)];
    // Sampling already caps each foreground class, so every row keeps weight 1 (no inverse-frequency
    // weighting: it makes sparse OK pixels compete with the entire background and inflates false positives).
    // V161: unless priorBalanced, which restores the NATURAL bg:fg prior instead (see the weights block below).
    if (!counts[0]) throw std::runtime_error("No usable background pixels after ROI alignment and feature sampling");
    { // no foreground class (NG or OK without a table, any named class with one) kept a sampled row
        bool anyForeground = false; for (int c = 1; c < trainClasses; ++c) if (counts[c]) anyForeground = true;
        if (!anyForeground) throw std::runtime_error(classTableSet ? "No usable class pixels after ROI alignment and feature sampling; increase input size or label coverage"
            : "No usable NG or OK pixels after ROI alignment and feature sampling; increase input size or label coverage");
    }
    // V146 Retrain (changed images only): a delta-active run's TRAINING split is only the changed+rehearsal
    // subset, which may legitimately lack a class that the full dataset (AddSample, unconditional — I2) already
    // enabled; the &&!deltaActive guards below let that subset through instead of failing spuriously.
    for (int c = 1; c < trainClasses; ++c) {
        if (classPixels[c] > 0 && !counts[c] && !deltaActive) {
            const std::string className = classTableSet ? classTable.classes[size_t(c) - 1].name : (c == 1 ? "NG" : "OK");
            throw std::runtime_error("No usable " + className + " pixels in the training split after feature sampling" + (c == 1 ? "; increase input size or label coverage" : ""));
        }
    }
    // A class with no labelled pixel at all is disabled; background is always enabled.
    for (int c = 1; c < trainClasses; ++c) classDisabled[c] = classPixels[c] <= 0;
    SyncLegacyEnabledFlags();
    // Validate labels once here; the parallel blocks must not throw.
    const cv::Mat& trainLabels = samples.labels;
    for (const cv::Mat* labels : { &trainLabels, &validationLabels })
        for (int r = 0; r < labels->rows; ++r) { const int label = labels->at<int>(r); if (label < 0 || label >= trainClasses || !ClassEnabled(label)) throw std::runtime_error("Validation contains a class absent from training"); }
    // V161: prior-balanced class weights. The per-image caps (bg 2000 / fg 450 per class) train at ~2.2:1 bg:fg while the
    // natural ratio is ~33:1, so the head over-predicts foreground and blobs over-extend. One pooled weight per side
    // (fg shared by NG and OK) restores the natural prior: w_c = (N_c/N)/(S_c/S) with N = pre-cap training rows and
    // S = sampled rows. bg/fg odds are capped at 32 and renormalised so the mean row weight stays 1 (Adam step scale
    // and the loss magnitude stay comparable). The SAME array feeds train, validation, resume-start and recheck passes.
    float priorWeights[MaxClasses]; for (int c = 0; c < MaxClasses; ++c) priorWeights[c] = 1.f;
    const float* classWeight = nullptr;
    lastClassWeightsValid = false;
    if (priorBalanced) {
        // One pooled foreground weight: the natural and sampled foreground counts add up over classes 1..K-1 in id order
        // (for K = 3 exactly natural[1] + natural[2] and counts[1] + counts[2] as before).
        double nFg = samples.natural[1], sFg = double(counts[1]);
        for (int c = 2; c < trainClasses; ++c) { nFg += samples.natural[c]; sFg += double(counts[c]); }
        const double nBg = samples.natural[0];
        const double sBg = double(counts[0]);
        const double n = nBg + nFg, s = sBg + sFg;
        if (nBg > 0 && nFg > 0 && sBg > 0 && sFg > 0 && std::isfinite(n) && std::isfinite(s)) {
            double wBg = (nBg / n) / (sBg / s), wFg = (nFg / n) / (sFg / s);
            bool capped = false;
            if (wBg / wFg > 32.) {
                wFg = wBg / 32.;
                const double meanWeight = (sBg * wBg + sFg * wFg) / s;
                wBg /= meanWeight; wFg /= meanWeight; capped = true;
            }
            priorWeights[0] = float(wBg); for (int c = 1; c < trainClasses; ++c) priorWeights[c] = float(wFg);
            classWeight = priorWeights;
            for (int c = 0; c < trainClasses; ++c) lastClassWeights[c] = priorWeights[c];
            lastClassWeightsValid = true;
            std::ostringstream status; status.precision(9);
            status << priorHeader << '\n' << long long(nBg) << ' ' << long long(nFg) << ' ' << long long(sBg) << ' ' << long long(sFg)
                   << ' ' << wBg << ' ' << wFg << ' ' << (capped ? 1 : 0);
            WritePriorStatus(status.str());
        } else {
            priorBalanced = false; // degenerate counts: stay legacy rather than invent weights
            WritePriorStatus("legacy; requested=" + std::string(priorBalanceRequested ? "1" : "0") + "; fallback: unusable natural/sampled row counts");
        }
    }
    // V145 Retrain: epoch-0 loss under the rebased head, for status text only (never written to epochs.csv
    // and never used for best-epoch selection, which stays unseeded and starts at epoch 1 below).
    double startValLoss = std::numeric_limits<double>::quiet_NaN();
    if (resumed) startValLoss = HeadPass(vx, validationLabels, head, false, nullptr, nullptr, classWeight);
    // V146 Retrain (changed images only): a delta-active run needs a finite starting point to bound how far
    // validation loss (measured only on unchanged/held-out images) may rise before the run is rejected.
    if (deltaActive && !std::isfinite(startValLoss)) throw std::runtime_error("[retrain-delta-rejected] start validation loss is not finite");
    const double deltaLimit = deltaActive ? startValLoss * (1.0 + double(deltaTolerance)) : 0.;
    Head deltaHead; int deltaEpoch = 0; double deltaValLoss = 0.;
    std::ofstream log(out / "epochs.csv"); log << "epoch,time,train/cls_loss,val/cls_loss,lr,train_rows,train/iou,val/iou\n";
    // V150: explicit earlyStopWindow (clamped 5..200) replaces the legacy epoch-scaled window; minimumEpochs keeps its shape.
    // epoch >= minimumEpochs >= 2*window guarantees bestHistory holds at least window+1 entries when it is read.
    const int window = earlyStopWindow > 0 ? std::min(200, std::max(5, earlyStopWindow)) : std::max(20, epochs / 10), minimumEpochs = std::min(epochs, std::max(40, 2 * window));
    std::vector<double> bestHistory; double best = std::numeric_limits<double>::infinity();
    Head bestHead; int bestEpoch = 0;
    const auto start = Clock::now();
    // Per-epoch bookkeeping (finite check, delta snapshot, epochs.csv row, progress, console, best head, early stop).
    // Returns true when training must stop. It clones/snapshots `head`, so it must only run while `head` holds the
    // post-update parameters P_epoch of the epoch being finished.
    // V149 recheck equivalence: with no validation split, vx aliases x and validationLabels == samples.labels, so the
    // validation forward after epoch e's Adam update (params P_e) is bit-identical to epoch e+1's train forward (params
    // P_e, same rows/labels/block order; HeadBlock computes loss and tp/fp/fn before the gradient step and independent
    // of `gradient`). Recheck mode therefore defers finish(e) until that forward has produced the values, and runs one
    // trailing forward after the last epoch. Do not "fix" this back into a second pass per epoch.
    auto finish = [&](int epoch, double loss, double validationLoss, double trainIou, double validationIou, float rate) -> bool {
        if (!std::isfinite(loss) || !std::isfinite(validationLoss)) throw std::runtime_error("Nonfinite V2 head loss; reduce learning rate or check the training input");
        // V146 Retrain (changed images only): keep the LATEST epoch whose validation loss (unchanged/held-out
        // images only) stays within tolerance of the pre-training start — most learning of the changed images
        // without exceeding the regression limit on images this run never trains on.
        if (deltaActive && validationLoss <= deltaLimit) {
            deltaEpoch = epoch; deltaValLoss = validationLoss;
            deltaHead.weights = head.weights.clone(); deltaHead.bias = head.bias.clone();
            deltaHead.hiddenWeights = head.hiddenWeights.clone(); deltaHead.hiddenBias = head.hiddenBias.clone();
        }
        const double seconds = std::chrono::duration<double>(Clock::now() - start).count();
        log << epoch << ',' << seconds << ',' << loss << ',' << validationLoss << ',' << rate << ',' << x.rows << ',' << trainIou << ',' << validationIou << '\n'; log.flush();
        if (progress) progress(45 + epoch * 50 / epochs);
        if (epoch == 1 || epoch % 20 == 0) std::cout << "Epoch " << epoch << " train=" << loss << " validation=" << validationLoss << std::endl;
        if (validationLoss < best) {
            best = validationLoss; bestEpoch = epoch;
            bestHead.weights = head.weights.clone(); bestHead.bias = head.bias.clone();
            bestHead.hiddenWeights = head.hiddenWeights.clone(); bestHead.hiddenBias = head.hiddenBias.clone();
        }
        bestHistory.push_back(best);
        if (earlyStop && epoch >= minimumEpochs && epoch < epochs) {
            const double before = bestHistory[bestHistory.size() - 1 - window];
            if (before - best < earlyStopMinGain * before) {
                std::cout << "Early stop at epoch " << epoch << " (best " << bestEpoch << ")" << std::endl;
                if (progress) progress(95);
                return true;
            }
        }
        return false;
    };
    const bool recheck = !hasValidation;
    struct PreviousEpoch { bool has = false; int epoch = 0; double loss = 0, trainIou = std::numeric_limits<double>::quiet_NaN(); float rate = 0; } prev;
    bool stopped = false;
    for (int epoch = 1; epoch <= epochs; ++epoch) {
        CheckCancel();
        HeadGrad grad;
        double trainIou = std::numeric_limits<double>::quiet_NaN(), validationIou = std::numeric_limits<double>::quiet_NaN();
        double loss;
        { StageTimer timer(*this, "headpass_train"); loss = HeadPass(x, samples.labels, head, true, &grad, &trainIou, classWeight); }
        StageTimer adamTimer(*this, "epoch_rest_incl_validation_and_csv");
        // Recheck: this forward used P_(epoch-1) == the params epoch-1's validation would have used; `head` is still
        // P_(epoch-1) because this epoch's Adam update has not been applied yet.
        if (recheck && prev.has && finish(prev.epoch, prev.loss, loss, prev.trainIou, trainIou, prev.rate)) { stopped = true; break; }
        cv::Mat gradW = grad.weights + regularization * head.weights, gradB = grad.bias;
        cv::Mat gradH = grad.hiddenWeights + regularization * head.hiddenWeights, gradHB = grad.hiddenBias;
        float rate = baseRate * (.1f + .9f * float(1 + std::cos(epoch * CV_PI / epochs)) / 2.f);
        auto update = [&](cv::Mat& weights, cv::Mat& m, cv::Mat& v, const cv::Mat& gradient) {
            m = .9f * m + .1f * gradient; v = .999f * v + .001f * gradient.mul(gradient);
            cv::Mat denominator; cv::sqrt(v / (1 - std::pow(.999f, epoch)), denominator); denominator += 1e-8;
            cv::Mat step; cv::divide(m / (1 - std::pow(.9f, epoch)), denominator, step); weights -= rate * step;
        };
        update(head.weights, firstW, secondW, gradW); update(head.bias, firstB, secondB, gradB);
        update(head.hiddenWeights, firstH, secondH, gradH); update(head.hiddenBias, firstHB, secondHB, gradHB);
        if (recheck) {
            // V149: no separate validation pass here. Epoch e's validation values are produced by epoch e+1's train
            // forward (same rows, labels, block order and parameters P_e); see the comment above `finish`.
            prev.has = true; prev.epoch = epoch; prev.loss = loss; prev.trainIou = trainIou; prev.rate = rate;
        } else {
            double validationLoss;
            { StageTimer timer(*this, "headpass_validation"); validationLoss = HeadPass(vx, validationLabels, head, false, nullptr, &validationIou, classWeight); }
            if (finish(epoch, loss, validationLoss, trainIou, validationIou, rate)) break;
        }
    }
    if (recheck && !stopped && prev.has) {
        // V149: trailing validation forward for the last epoch (head == P_last here), exactly what the old loop ran.
        double validationIou = std::numeric_limits<double>::quiet_NaN();
        const double validationLoss = HeadPass(x, samples.labels, head, false, nullptr, &validationIou, classWeight);
        finish(prev.epoch, prev.loss, validationLoss, prev.trainIou, validationIou, prev.rate);
    }
    if (deltaActive) {
        // V146 Retrain (changed images only): a rejection here means Train returns an error code and no model
        // gets saved/activated — the managed side's TrainConfigured wrapper catches the marked message and
        // reruns on the full dataset (D2).
        if (!deltaEpoch) throw std::runtime_error("[retrain-delta-rejected] every epoch raised validation loss on unchanged images by more than " + std::to_string(int(deltaTolerance * 100 + .5f)) + "%");
        head.weights = deltaHead.weights; head.bias = deltaHead.bias; head.hiddenWeights = deltaHead.hiddenWeights; head.hiddenBias = deltaHead.hiddenBias;
    } else if (bestEpoch) { head.weights = bestHead.weights; head.bias = bestHead.bias; head.hiddenWeights = bestHead.hiddenWeights; head.hiddenBias = bestHead.hiddenBias; }
    // V145 Retrain: snapshot the raw (pre-fold, pre-mask) head so a future run can resume from it. Runs on
    // both fresh and resumed runs, so every newly trained V2 model becomes resumable.
    savedHead.weights = head.weights.clone(); savedHead.bias = head.bias.clone();
    savedHead.hiddenWeights = head.hiddenWeights.clone(); savedHead.hiddenBias = head.hiddenBias.clone();
    savedHead.mean = head.mean.clone(); savedHead.sd = head.sd.clone();
    hasSavedHead = true;
    // Fold training-only standardization into a 1x1 convolution for native GPU inference.
    for (int feature = 0; feature < Channels; ++feature) for (int c = 0; c < hidden; ++c) {
        head.hiddenWeights.at<float>(feature,c) /= head.sd.at<float>(feature);
        head.hiddenBias.at<float>(c) -= head.mean.at<float>(feature) * head.hiddenWeights.at<float>(feature,c);
    }
    for (int c = 1; c < trainClasses; ++c) if (!ClassEnabled(c)) { head.weights.col(c).setTo(0); head.bias.at<float>(c) = -1e9f; }
    cv::FileStorage storage((out/"head.yml").u8string(), cv::FileStorage::WRITE);
    storage << "weights" << head.weights << "bias" << head.bias << "hidden_weights" << head.hiddenWeights << "hidden_bias" << head.hiddenBias << "train_mean" << head.mean << "train_std" << head.sd;
    if (resumeRequested) {
        if (resumed) {
            std::ostringstream note;
            note << "resumed; start_val_loss=" << startValLoss << "; best_epoch=" << bestEpoch
                 << "; best_val_loss=" << best << "; lr_scale=" << resumeRateScale
                 << "; rebase_max_abs=" << rebaseMaxAbs;
            unsigned long long currentActive = 1; // bit c = class c is enabled (bit 0 = background); equals the legacy active_classes mask for K = 3
            for (int c = 1; c < trainClasses; ++c) if (ClassEnabled(c)) currentActive |= 1ull << c;
            if (resumeActive && resumeActive != currentActive)
                note << "; note: class set changed (old active=" << resumeActive << ", now " << currentActive << ")";
            if (resumeSide && resumeSide != Side)
                note << "; note: input size changed";
            // V146 Retrain (changed images only): append which epoch's head was kept, and write the dedicated
            // delta.txt status the managed side reads for the report's RetrainScope text.
            if (deltaActive) {
                note << "; delta_epoch=" << deltaEpoch;
                std::ostringstream deltaNote;
                deltaNote << "delta; epoch=" << deltaEpoch << "; val_loss=" << deltaValLoss
                    << "; start_val_loss=" << startValLoss << "; tolerance=" << deltaTolerance;
                WriteDeltaStatus(deltaNote.str());
            }
            WriteResumeStatus(note.str());
        } else if (rebaseCheckFailed) {
            WriteResumeStatus("fresh; rebase check failed");
        }
        // else: resumeLoaded was already false when Configure ran; its "fresh; <reason>" status stands.
    }
    return head;
}
std::shared_ptr<ov::Model> AddHead(const std::shared_ptr<ov::Model>& features, const Head& head) {
    auto dense = [](const ov::Output<ov::Node>& input, const cv::Mat& weights, const cv::Mat& bias) {
        std::vector<float> w(size_t(weights.cols)*weights.rows), b(weights.cols);
        for (int c=0;c<weights.cols;++c) {
            b[c]=bias.at<float>(c);
            for(int j=0;j<weights.rows;++j) w[size_t(c)*weights.rows+j]=weights.at<float>(j,c);
        }
        auto kernel=ov::opset13::Constant::create(ov::element::f32,{size_t(weights.cols),size_t(weights.rows),1,1},w);
        auto conv=std::make_shared<ov::opset13::Convolution>(input,kernel,ov::Strides{1,1},ov::CoordinateDiff{0,0},ov::CoordinateDiff{0,0},ov::Strides{1,1});
        return std::make_shared<ov::opset13::Add>(conv,ov::opset13::Constant::create(ov::element::f32,{1,size_t(weights.cols),1,1},b));
    };
    auto hidden=std::make_shared<ov::opset13::Relu>(dense(features->get_results().at(0)->input_value(0),head.hiddenWeights,head.hiddenBias));
    auto add=dense(hidden,head.weights,head.bias);
    ov::Output<ov::Node> probabilities = std::make_shared<ov::opset13::Softmax>(add, 1);
    // A class without labelled pixels is masked to probability 0 (for K = 3 this is exactly the {1, ng, ok} constant).
    bool anyDisabled = false; std::vector<float> classMask(size_t(trainClasses), 1.f);
    for (int c = 1; c < trainClasses; ++c) if (!ClassEnabled(c)) { anyDisabled = true; classMask[size_t(c)] = 0.f; }
    if (anyDisabled) probabilities = std::make_shared<ov::opset13::Multiply>(probabilities,
        ov::opset13::Constant::create(ov::element::f32, ov::Shape{1, size_t(trainClasses), 1, 1}, classMask));
    return std::make_shared<ov::Model>(ov::OutputVector{probabilities}, features->get_parameters(), "NativeDefectV2_Prototype");
}

};
namespace {
unsigned V2Crc(const std::string& bytes) {
    unsigned crc=0xffffffff;
    for (unsigned char b : bytes) { crc ^= b; for(int i=0;i<8;++i) crc=(crc&1)?(crc>>1)^0xedb88320:(crc>>1); }
    return ~crc;
}
}
void SegV2Model::Impl::WriteResumeStatus(const std::string& text) {
    // V145 Retrain: best-effort status for the managed side; must never throw or fail training.
    try { std::ofstream(logFolder/"resume.txt", std::ios::trunc) << text << '\n'; } catch (...) {}
}
void SegV2Model::Impl::WriteDeltaStatus(const std::string& text) {
    // V146 Retrain (changed images only): best-effort status for the managed side; must never throw or fail training.
    try { std::ofstream(logFolder/"delta.txt", std::ios::trunc) << text << '\n'; } catch (...) {}
}
void SegV2Model::Impl::FineTuneBackbone(const std::shared_ptr<ov::Model>& features, Head& head) {
    if (originalInput) throw std::runtime_error("Backbone fine-tune supports only the fixed Side input");
    if (deltaActive) throw std::runtime_error("Backbone fine-tune is not supported together with Retrain (changed images only)");
    if (!SegFineTune::Compiled()) throw std::runtime_error("Backbone fine-tune needs a build with libtorch");
    fs::path module = fs::u8path(backbone);
    if (backboneTorch.empty()) { module.replace_extension(); module += ".taps.pt"; } else module = fs::u8path(backboneTorch);
    if (!fs::exists(module)) throw std::runtime_error("Missing TorchScript backbone: " + module.u8string());
    std::vector<size_t> trainImages, validationImages;
    for (size_t i = 0; i < images.size(); ++i) (validationIndices.count(int(i)) ? validationImages : trainImages).push_back(i);
    if (trainImages.empty()) throw std::runtime_error("Backbone fine-tune has no training images");
    // Same augmentation transform as the per-job feature extraction in Train(), applied lazily per step.
    auto makeFrame = [&](size_t image, int variant) {
        const auto& pair = images[image]; cv::Mat view = pair.first, labels = pair.second;
        if (variant) {
            const double angles[] = { 0., -rotation, rotation, -90., 90., 180. };
            const double angle = variant < 3 ? angles[variant] : (rotation >= 90 ? angles[variant] : angles[variant % 3]);
            const double objectScale = variant % 2 ? trainingScaleMin : trainingScaleMax;
            auto transform = cv::getRotationMatrix2D(cv::Point2f(float(view.cols) / 2, float(view.rows) / 2), angle, objectScale);
            cv::Mat warped, warpedLabels;
            cv::warpAffine(pair.first, warped, transform, pair.first.size(), cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(114, 114, 114));
            cv::warpAffine(pair.second, warpedLabels, transform, pair.second.size(), cv::INTER_NEAREST, cv::BORDER_CONSTANT, cv::Scalar(255));
            warped.convertTo(warped, CV_8U, 1. + (variant % 2 ? -brightness : brightness));
            view = warped; labels = warpedLabels;
        }
        const auto frame = Prepare(view, labels, false);
        SegFineTune::Frame result; result.image = frame.image; result.labels = frame.labels;
        result.left = frame.left; result.top = frame.top; result.width = frame.width; result.height = frame.height;
        return result;
    };
    SegFineTune::Options options;
    options.modulePath = module.u8string(); options.epochs = backboneEpochs; options.threads = backboneThreads; options.side = Side;
    options.backboneLr = backboneLr; options.headLr = backboneHeadLr; options.regularization = regularization;
    options.useClassWeights = lastClassWeightsValid;
    options.classes = trainClasses;
    for (int c = 0; c < trainClasses; ++c) options.classWeights[c] = lastClassWeights[c];
    options.trainCount = trainImages.size(); options.validationCount = validationImages.size();
    options.trainFrame = [&](size_t item, int, std::mt19937& random) { return makeFrame(trainImages[item], (backboneAugment < 0 ? augment : backboneAugment != 0) ? int(random() % 6) : 0); };
    options.validationFrame = [&](size_t item) { return makeFrame(validationImages[item], 0); };
    options.progress = [&](int percent) { if (progress) progress(95 + percent / 20); };
    options.parityFrame = [&]() { return makeFrame(trainImages[0], 0); };
    options.cancel = cancelFlag; options.logPath = (logFolder / "finetune_epochs.csv").u8string();
    // Train() already folded mean/sd into hiddenWeights/hiddenBias (inference form). The fine-tune works in the standardised
    // basis (like the head trainer), so undo the fold here and redo it after the run:
    //   fold:   W_f = W / sd,  b_f = b - sum(mean * W_f)      unfold: b = b_f + sum(mean * W_f),  W = W_f * sd
    SegFineTune::Head start; start.weights = head.weights; start.bias = head.bias; start.mean = head.mean; start.sd = head.sd;
    start.hiddenWeights = head.hiddenWeights.clone(); start.hiddenBias = head.hiddenBias.clone();
    for (int c = 0; c < start.hiddenWeights.cols; ++c) {
        double bias = head.hiddenBias.at<float>(c);
        for (int f = 0; f < start.hiddenWeights.rows; ++f) {
            bias += double(head.mean.at<float>(f)) * double(head.hiddenWeights.at<float>(f, c));
            start.hiddenWeights.at<float>(f, c) = head.hiddenWeights.at<float>(f, c) * head.sd.at<float>(f);
        }
        start.hiddenBias.at<float>(c) = float(bias);
    }
    SegFineTune::Result result; std::string error;
    if (!SegFineTune::Run(options, start, result, error)) throw std::runtime_error("Backbone fine-tune failed: " + error);
    // Write the fine-tuned backbone weights into the OpenVINO graph (constants share the torch parameter names).
    // Optional "<torch name> <initializer name>" map next to the TorchScript file (the zoo ONNX names its initializers by number).
    std::map<std::string, std::string> alias;
    { fs::path mapPath = module; mapPath.replace_extension(".map.txt"); std::ifstream in(mapPath); std::string torchName, initializer; while (in >> torchName >> initializer) alias[torchName] = initializer; }
    std::map<std::string, const std::vector<float>*> updated;
    for (const auto& item : result.backbone) { const auto renamed = alias.find(item.first); updated[renamed == alias.end() ? item.first : renamed->second] = &item.second; }
    size_t replaced = 0;
    for (const auto& node : features->get_ordered_ops()) {
        auto constant = std::dynamic_pointer_cast<ov::op::v0::Constant>(node);
        if (!constant) continue;
        const auto found = updated.find(constant->get_friendly_name());
        if (found == updated.end()) continue;
        if (constant->get_element_type() != ov::element::f32 || ov::shape_size(constant->get_shape()) != found->second->size())
            throw std::runtime_error("Backbone fine-tune parameter mismatch: " + constant->get_friendly_name());
        auto fresh = ov::opset13::Constant::create(ov::element::f32, constant->get_shape(), *found->second);
        fresh->set_friendly_name(constant->get_friendly_name());
        ov::replace_node(constant, fresh); ++replaced;
    }
    if (replaced != result.backbone.size()) {
        std::string sample;
        for (const auto& node : features->get_ordered_ops()) {
            auto constant = std::dynamic_pointer_cast<ov::op::v0::Constant>(node);
            if (constant && ov::shape_size(constant->get_shape()) > 1 && sample.size() < 240) sample += constant->get_friendly_name() + ";";
        }
        throw std::runtime_error("Backbone fine-tune replaced " + std::to_string(replaced) + " of " + std::to_string(result.backbone.size()) +
            " parameters; first torch name " + result.backbone.front().first + "; OpenVINO constants " + sample);
    }
    features->validate_nodes_and_infer_types();
    // P2 parity: OpenVINO features of the rewritten graph must equal the torch features of the same weights.
    double parityMax = 0, parityRef = 0;
    {
        const auto frame = makeFrame(trainImages[0], 0);
        Frame prepared; prepared.image = frame.image;
        auto compiledFeatures = Compile(features, frame.image.size(), "CPU");
        auto parityRequest = compiledFeatures.create_infer_request();
        const cv::Mat openvino = FeatureRows(parityRequest, prepared);
        if (openvino.size() != result.parityRows.size()) throw std::runtime_error("Backbone fine-tune parity shape mismatch");
        parityMax = cv::norm(openvino, result.parityRows, cv::NORM_INF); parityRef = cv::norm(result.parityRows, cv::NORM_INF);
        if (!std::isfinite(parityMax) || parityMax > 1e-3 * std::max(1., parityRef))
            throw std::runtime_error("Backbone fine-tune parity failed: max|diff|=" + std::to_string(parityMax) + " ref max=" + std::to_string(parityRef));
    }
    head.weights = result.head.weights; head.bias = result.head.bias;
    head.hiddenWeights = result.head.hiddenWeights.clone(); head.hiddenBias = result.head.hiddenBias.clone();
    for (int c = 0; c < head.hiddenWeights.cols; ++c) {
        double bias = result.head.hiddenBias.at<float>(c);
        for (int f = 0; f < head.hiddenWeights.rows; ++f) {
            const float folded = result.head.hiddenWeights.at<float>(f, c) / head.sd.at<float>(f);
            head.hiddenWeights.at<float>(f, c) = folded;
            bias -= double(head.mean.at<float>(f)) * double(folded);
        }
        head.hiddenBias.at<float>(c) = float(bias);
    }
    // The raw resume snapshot belongs to the pre-fine-tune head and the original backbone; a fine-tuned model must not be resumable from it.
    hasSavedHead = false;
    try { std::ofstream status(logFolder / "finetune.txt", std::ios::trunc); status << "epochs=" << backboneEpochs << " best_epoch=" << result.bestEpoch << " best_loss=" << result.bestLoss << " parameters=" << replaced << " parity_max_diff=" << parityMax << " parity_ref_max=" << parityRef << std::endl; } catch (...) {}
}
void SegV2Model::Impl::WritePriorStatus(const std::string& text) {
    // V161: best-effort prior-balance status (line 1 mode, line 2 counts/weights); must never throw or fail training.
    try { std::ofstream(logFolder/"prior.txt", std::ios::trunc) << text << '\n'; } catch (...) {}
}
// V145 Retrain: loads the raw pre-fold head from a previous .segai file for this run to resume from.
// Defined after V2Crc (needs it for the CRC check) and out-of-line (needs the complete Impl definition).
// Best-effort: any failure sets `reason` and returns false; never touches lastError, never throws out.
bool SegV2Model::Impl::ReadResumeHead(const fs::path& path, std::string& reason) {
    try {
        std::ifstream file(path, std::ios::binary | std::ios::ate);
        if (!file) { reason = "cannot open resume source"; return false; }
        const auto size = file.tellg(); file.seekg(0);
        SegAIHeader header = {}; file.read(reinterpret_cast<char*>(&header), sizeof(header));
        // Version 4 appends the class table after the payload (reservedA bytes); version 3 has none.
        const bool v4 = file && header.version == 4;
        const uint64_t tableBytes = v4 ? header.reservedA : 0;
        if (!file || std::memcmp(header.magic, "SEGAI\0\0", 8) || (header.version != 3 && !v4) ||
            (v4 ? (header.numClasses < 2 || header.numClasses > uint32_t(MaxClasses)) : header.numClasses != 3) ||
            header.featureCount != 448 || header.featureFlags != 0x02000000 || !header.payloadSize ||
            header.payloadSize > 256ull * 1024 * 1024 || (v4 && (tableBytes == 0 || tableBytes > kMaxClassTableBytes)) ||
            uint64_t(size) != 72 + header.payloadSize + tableBytes) {
            reason = "resume source has an invalid or incompatible header"; return false;
        }
        // Read fully into memory (no open handle kept afterward), so a later File.Replace on the same
        // path (ActivateTrainedCandidate) never hits a sharing violation because of this read.
        std::string payload(size_t(header.payloadSize), '\0'); file.read(&payload[0], payload.size());
        if (!file || V2Crc(payload) != header.crc32) { reason = "resume source failed CRC check"; return false; }
        SegClassTable sourceTable = MakeLegacyClassTable();
        if (v4) {
            std::vector<unsigned char> tableBuffer(static_cast<size_t>(tableBytes)); file.read(reinterpret_cast<char*>(tableBuffer.data()), std::streamsize(tableBuffer.size()));
            std::string tableError;
            if (!file || !DecodeClassTable(tableBuffer.data(), tableBuffer.size(), sourceTable, tableError)) {
                reason = "resume source has an invalid class table"; return false;
            }
            if (!ValidateTrainerClassTable(sourceTable, tableError)) {
                reason = "resume source has a class table the trainer cannot resume from: " + tableError; return false;
            }
            if (header.numClasses != sourceTable.classes.size() + 1) {
                reason = "resume source header class count does not match its class table"; return false;
            }
        }
        const int sourceClasses = int(sourceTable.classes.size()) + 1; // K of the resume source (3 for version 3)
        file.close();
        cv::FileStorage storage(payload, cv::FileStorage::READ | cv::FileStorage::MEMORY);
        if (storage["resume_head"].empty()) { reason = "model has no Retrain data (trained before Retrain support)"; return false; }
        int resumeFlag = 0; storage["resume_head"] >> resumeFlag;
        if (resumeFlag != 1) { reason = "model has no Retrain data (trained before Retrain support)"; return false; }
        cv::Mat hiddenWeights, hiddenBias, weights, bias, mean, sd;
        storage["resume_hidden_weights"] >> hiddenWeights; storage["resume_hidden_bias"] >> hiddenBias;
        storage["resume_weights"] >> weights; storage["resume_bias"] >> bias;
        storage["resume_mean"] >> mean; storage["resume_std"] >> sd;
        auto validShape = [](const cv::Mat& m, int rows, int cols) {
            return !m.empty() && m.type() == CV_32F && m.rows == rows && m.cols == cols;
        };
        if (!validShape(hiddenWeights, Channels, HeadHidden) || !validShape(hiddenBias, 1, HeadHidden) ||
            !validShape(weights, HeadHidden, sourceClasses) || !validShape(bias, 1, sourceClasses) ||
            !validShape(mean, 1, Channels) || !validShape(sd, 1, Channels)) {
            reason = "incompatible head shape/values"; return false;
        }
        double sdMin = 0, sdMax = 0; cv::minMaxLoc(sd, &sdMin, &sdMax);
        bool valuesOk = cv::checkRange(hiddenWeights) && cv::checkRange(hiddenBias) && cv::checkRange(weights) &&
            cv::checkRange(bias) && cv::checkRange(mean) && cv::checkRange(sd) && sdMin >= .1 - 1e-6;
        if (!valuesOk) { reason = "incompatible head shape/values"; return false; }
        std::string resumeBackbone, resumeBackboneBytes;
        storage["resume_backbone"] >> resumeBackbone; storage["resume_backbone_bytes"] >> resumeBackboneBytes;
        std::string currentName = fs::u8path(backbone).filename().u8string();
        std::error_code ec; uint64_t currentSize = fs::file_size(fs::u8path(backbone), ec);
        if (ec || resumeBackbone != currentName || resumeBackboneBytes != std::to_string(currentSize)) {
            reason = "backbone changed"; return false;
        }
        // Notes only (not validation gates): reported in the final Train() status when they differ.
        { int legacyActive = 7; if (!storage["active_classes"].empty()) storage["active_classes"] >> legacyActive; resumeActive = static_cast<unsigned long long>(legacyActive); }
        if (v4) { // v4 keeps the flags in the table; bit c = class c enabled
            resumeActive = 1;
            for (size_t c = 0; c < sourceTable.classes.size(); ++c) if (sourceTable.classes[c].enabled) resumeActive |= 1ull << (c + 1);
        }
        resumeTable = sourceTable;
        resumeSide = 0; if (!storage["side"].empty()) storage["side"] >> resumeSide;
        resumeNativeV2 = 0; if (!storage["native_v2"].empty()) storage["native_v2"] >> resumeNativeV2;
        // V161: the resumed head was fitted under this mode; the new run keeps it (never rejects, missing = legacy).
        resumePriorBalanced = false; if (!storage["prior_balanced"].empty()) { int v = 0; storage["prior_balanced"] >> v; resumePriorBalanced = v == 1; }
        resumeHead.hiddenWeights = hiddenWeights.clone(); resumeHead.hiddenBias = hiddenBias.clone();
        resumeHead.weights = weights.clone(); resumeHead.bias = bias.clone();
        resumeHead.mean = mean.clone(); resumeHead.sd = sd.clone();
        return true;
    } catch (const std::exception& e) {
        reason = e.what(); return false;
    } catch (...) {
        reason = "unknown error reading resume data"; return false;
    }
}
// EAI-CLS S1: a resume is only valid when the class names (and therefore K) match the table this run will save; otherwise
// the run starts fresh like any other unusable resume source. Called once at the start of Train, after SetClassTable.
void SegV2Model::Impl::ReconcileResumeClasses() {
    if (!resumeLoaded) return;
    const SegClassTable current = classTableSet ? classTable : MakeLegacyClassTable();
    bool same = current.classes.size() == resumeTable.classes.size();
    for (size_t i = 0; same && i < current.classes.size(); ++i)
        same = current.classes[i].name == resumeTable.classes[i].name && current.classes[i].kind == resumeTable.classes[i].kind;
    if (same) return;
    resumeLoaded = false; resumeHead = Head();
    WriteResumeStatus("fresh; class set changed");
    // Configure derived the prior mode and the delta plan from the resume that was just dropped; redo both for a fresh start.
    priorBalanced = priorBalanceRequested;
    { std::error_code priorEc; fs::remove(logFolder/"prior.txt", priorEc); }
    if (priorKeyPresent || priorBalanced) {
        priorHeader = priorBalanced ? "balanced" : "legacy";
        WritePriorStatus(priorHeader);
    }
    if (deltaActive) { deltaActive = false; WriteDeltaStatus("full; previous model could not be resumed"); }
}
SegV2Model::SegV2Model() : impl_(new Impl()) {}
SegV2Model::~SegV2Model() = default;
void SegV2Model::Configure(const std::wstring& configuration) {
    auto& p=*impl_;
    p.winnerForegroundBoundary=false;
    p.resumeRequested=false; p.resumeLoaded=false; p.resumeRateScale=1.f; p.resumeNote.clear(); p.resumeHead=Impl::Head();
    // V146 Retrain (changed images only): reset every run, same as the resume state above.
    p.trainSkipIndices.clear(); p.deltaRequested=false; p.deltaActive=false; p.deltaTolerance=.05f;
    // V161: reset every run like the resume state; absent key = legacy weights.
    p.priorBalanceRequested=false; p.priorBalanced=false; p.resumePriorBalanced=false; p.priorKeyPresent=false; p.priorHeader.clear();
    cv::FileStorage cfg(fs::path(configuration).u8string(),cv::FileStorage::READ);
    if(!cfg.isOpened()) throw std::runtime_error("Cannot read V2 training settings");
    cfg["backbone"] >> p.backbone; std::string logs; cfg["logs"] >> logs; p.logFolder=fs::u8path(logs);
    cfg["epochs"] >> p.epochs; cfg["side"] >> p.Side; cfg["learningRate"] >> p.learningRate;
    cfg["regularization"] >> p.regularization; cfg["rotation"] >> p.rotation;
    cfg["scaleVariation"] >> p.scaleVariation; cfg["brightness"] >> p.brightness;
    p.trainingScaleMin = 1 - p.scaleVariation; p.trainingScaleMax = 1 + p.scaleVariation;
    if (!cfg["objectScaleMin"].empty()) cfg["objectScaleMin"] >> p.trainingScaleMin;
    if (!cfg["objectScaleMax"].empty()) cfg["objectScaleMax"] >> p.trainingScaleMax;
    int augment=1,gpu=1,original=0; cfg["augment"]>>augment;cfg["gpu"]>>gpu;
    if (!cfg["earlyStop"].empty()) { int stop=1; cfg["earlyStop"]>>stop; p.earlyStop=stop!=0; }
    if (!cfg["earlyStopMinGain"].empty()) cfg["earlyStopMinGain"]>>p.earlyStopMinGain;
    // V150: reset every run like the resume state; absent or <=0 keeps the legacy window formula.
    p.earlyStopWindow=0;
    if (!cfg["earlyStopWindow"].empty()) { int stopWindow=0; cfg["earlyStopWindow"]>>stopWindow; p.earlyStopWindow=stopWindow>0?std::min(200,std::max(5,stopWindow)):0; }
    // V161: optional prior-balanced class weights (0 = legacy, 1 = balanced); anything else is rejected like the other keys.
    if (!cfg["priorBalance"].empty()) {
        int prior=0; cfg["priorBalance"]>>prior;
        if (prior!=0 && prior!=1) throw std::runtime_error("Invalid V2 training settings");
        p.priorKeyPresent=true; p.priorBalanceRequested=prior==1;
    }
    // SEG-LIFECYCLE-r11: optional backbone fine-tune keys, reset every run. Absent backboneEpochs = legacy (head only).
    p.backboneEpochs=0; p.backboneThreads=2; p.backboneLr=1e-4f; p.backboneHeadLr=1e-3f; p.backboneTorch.clear(); p.backboneAugment=-1;
    if (!cfg["backboneEpochs"].empty()) cfg["backboneEpochs"] >> p.backboneEpochs;
    if (!cfg["backboneThreads"].empty()) cfg["backboneThreads"] >> p.backboneThreads;
    if (!cfg["backboneLearningRate"].empty()) cfg["backboneLearningRate"] >> p.backboneLr;
    if (!cfg["backboneHeadLearningRate"].empty()) cfg["backboneHeadLearningRate"] >> p.backboneHeadLr;
    if (!cfg["backboneTorch"].empty()) cfg["backboneTorch"] >> p.backboneTorch;
    if (!cfg["backboneAugment"].empty()) { cfg["backboneAugment"] >> p.backboneAugment; if (p.backboneAugment!=0 && p.backboneAugment!=1) throw std::runtime_error("Invalid V2 backbone fine-tune settings"); }
    if (p.backboneEpochs<0 || p.backboneEpochs>200 || p.backboneThreads<1 || p.backboneThreads>64 ||
        !std::isfinite(p.backboneLr) || p.backboneLr<=0 || p.backboneLr>.01f ||
        !std::isfinite(p.backboneHeadLr) || p.backboneHeadLr<=0 || p.backboneHeadLr>.1f)
        throw std::runtime_error("Invalid V2 backbone fine-tune settings");
    if (!cfg["originalInput"].empty()) cfg["originalInput"] >> original;
    if (original != 0 && original != 1) throw std::runtime_error("Invalid V2 input size mode");
    if (p.backboneEpochs>0 && original!=0) throw std::runtime_error("Backbone fine-tune supports only the fixed Side input");
    p.augment=augment!=0;p.trainGpu=gpu!=0;p.originalInput=original!=0;
    for (const auto& node : cfg["validation"]) p.validationIndices.insert(int(node));
    if(p.epochs<1 || p.epochs>2000 || p.Side<160 || p.Side>2560 || p.Side%32 ||
       !std::isfinite(p.learningRate) || p.learningRate<=0 || p.learningRate>1 ||
       !std::isfinite(p.regularization) || p.regularization<0 || p.regularization>1 ||
       !std::isfinite(p.rotation) || p.rotation<0 || p.rotation>180 ||
       !std::isfinite(p.scaleVariation) || p.scaleVariation<0 || p.scaleVariation>.5 ||
       !std::isfinite(p.trainingScaleMin) || !std::isfinite(p.trainingScaleMax) || p.trainingScaleMin < .5f || p.trainingScaleMax > 2.f || p.trainingScaleMin > p.trainingScaleMax ||
       !std::isfinite(p.brightness) || p.brightness<0 || p.brightness>.5 ||
       !std::isfinite(p.earlyStopMinGain) || p.earlyStopMinGain<0 || p.earlyStopMinGain>.5)
        throw std::runtime_error("Invalid V2 training settings");
    p.Grid=p.Side/8;p.compactPostprocess=true;fs::create_directories(p.logFolder);
    // EAI-POS P3 instance head: optional additive keys, reset every run. Absent instanceHead = no behavior change at all.
    // instanceHead (0/1), angleSymmetry (one value 1|2|4 for every class, or one value per class), instanceEpochs, instanceLearningRate.
    // Rejected up front (r1 scope): original-input models, tiled input, Retrain / resume / changed-only training.
    p.instanceHead=false;p.instanceEpochs=60;p.instanceLearningRate=2e-3f;p.instanceSymmetryConfig.clear();
    if (!cfg["instanceHead"].empty()) {
        int enabled=0; cfg["instanceHead"]>>enabled;
        if (enabled!=0&&enabled!=1) throw std::runtime_error("Invalid V2 instance head settings: instanceHead must be 0 or 1");
        p.instanceHead=enabled==1;
    }
    if (p.instanceHead) {
        if (p.originalInput) throw std::runtime_error("Instance head (instanceHead=1) is not supported with originalInput=1 (original / tiled input); use the fixed Side input");
        // EAI-LOGO: instanceHead + backboneEpochs is allowed. The instance rows are then gathered in a second pass, from the features of
        // the fine-tuned backbone (see deferInstance in Train), so the instance head always learns from the backbone it will run on.
        std::string resumeSource; if (!cfg["resumeFrom"].empty()) cfg["resumeFrom"]>>resumeSource;
        if (!resumeSource.empty() || !cfg["trainSkip"].empty()) throw std::runtime_error("Instance head (instanceHead=1) cannot be combined with Retrain / resume (resumeFrom, trainSkip)");
        if (!cfg["instanceEpochs"].empty()) cfg["instanceEpochs"]>>p.instanceEpochs;
        if (!cfg["instanceLearningRate"].empty()) cfg["instanceLearningRate"]>>p.instanceLearningRate;
        if (p.instanceEpochs<1||p.instanceEpochs>1000||!std::isfinite(p.instanceLearningRate)||p.instanceLearningRate<=0||p.instanceLearningRate>.1f)
            throw std::runtime_error("Invalid V2 instance head settings: instanceEpochs 1..1000, instanceLearningRate (0, 0.1]");
        const cv::FileNode symmetry=cfg["angleSymmetry"];
        if (!symmetry.empty()) {
            if (symmetry.isSeq()) { for (const auto& item : symmetry) p.instanceSymmetryConfig.push_back(int(item)); }
            else p.instanceSymmetryConfig.push_back(int(symmetry));
            if (p.instanceSymmetryConfig.size()>32) throw std::runtime_error("Invalid V2 instance head settings: angleSymmetry has more than 32 values");
            for (int k : p.instanceSymmetryConfig) if (k!=1&&k!=2&&k!=4) throw std::runtime_error("Invalid V2 instance head settings: angleSymmetry values must be 1, 2 or 4");
        }
    }
    // V145 Retrain: optional additive key. Absent (every pre-existing caller): no behavior change at all.
    if (!cfg["resumeFrom"].empty()) {
        std::string resumeFrom; cfg["resumeFrom"] >> resumeFrom;
        float scale = 1.f; if (!cfg["resumeLearningRateScale"].empty()) cfg["resumeLearningRateScale"] >> scale;
        if (!std::isfinite(scale) || scale <= 0 || scale > 1) throw std::runtime_error("Invalid V2 training settings");
        if (!resumeFrom.empty()) {
            p.resumeRequested = true;
            std::string reason;
            p.resumeLoaded = p.ReadResumeHead(fs::u8path(resumeFrom), reason);
            p.resumeRateScale = scale;
            p.WriteResumeStatus(p.resumeLoaded ? std::string("loaded") : ("fresh; " + reason));
        }
    }
    // V161: effective mode. A successfully resumed head keeps its own mode (a legacy head fine-tuned with balanced weights
    // would mix loss scales); otherwise the request decides. prior.txt is written only when the key was sent or the mode
    // is balanced, so a legacy run without the key produces no new file. A stale file from an earlier run is removed first.
    { std::error_code priorEc; fs::remove(p.logFolder/"prior.txt", priorEc); }
    p.priorBalanced = p.resumeLoaded ? p.resumePriorBalanced : p.priorBalanceRequested;
    if (p.priorKeyPresent || p.priorBalanced) {
        p.priorHeader = p.priorBalanced ? "balanced" : "legacy";
        if (p.priorBalanceRequested != p.priorBalanced)
            p.priorHeader += std::string("; requested=") + (p.priorBalanceRequested ? "1" : "0") + "; kept resumed model mode";
        p.WritePriorStatus(p.priorHeader);
    }
    // V146 Retrain (changed images only): optional additive key, honored only when a resume actually succeeded
    // and there is a non-empty validation split (validationIndices is already populated above). Absent (every
    // pre-existing caller, and every plain Train/Retrain without a delta plan): no behavior change at all.
    if (!cfg["trainSkip"].empty()) {
        for (const auto& node : cfg["trainSkip"]) p.trainSkipIndices.insert(int(node));
        if (!cfg["deltaValidationTolerance"].empty()) cfg["deltaValidationTolerance"] >> p.deltaTolerance;
        if (!std::isfinite(p.deltaTolerance) || p.deltaTolerance < 0 || p.deltaTolerance > .5f) throw std::runtime_error("Invalid V2 training settings");
        for (int i : p.trainSkipIndices) if (i < 0) throw std::runtime_error("Invalid V2 training settings");
        p.deltaRequested = !p.trainSkipIndices.empty();
        p.deltaActive = p.deltaRequested && p.resumeLoaded && !p.validationIndices.empty();
        if (p.deltaRequested) p.WriteDeltaStatus(p.deltaActive ? "delta; pending" : "full; previous model could not be resumed");
    }
}
int SegV2Model::AddSample(const cv::Mat& image,const cv::Mat& labels) {
    if(image.empty() || labels.empty() || image.type()!=CV_8UC3 || labels.type()!=CV_8U || image.size()!=labels.size()) return -2;
    auto& p=*impl_;p.lastError.clear();auto frame=p.Prepare(image,labels);
    // Preserve the aspect ratio; preparation is repeated consistently for augmentation.
    cv::Rect content(frame.left,frame.top,frame.width,frame.height);
    p.images.emplace_back(frame.image(content).clone(),frame.labels(content).clone());
    p.instanceLabels.emplace_back();p.instanceSourceSize.push_back(image.size()); // EAI-POS P3: kept parallel to images; AddInstances fills the labels
    // Labelled pixels per class 1..K-1 (K = 3 without a table: NG = 1, OK = 2). Values above N, e.g. 255 = ignore, count nowhere.
    // ngPixels / okPixels aggregate the DEFECT / GOOD classes for the legacy DefectPixels / NormalPixels counters.
    for(int c=1;c<p.trainClasses;++c) {
        const int count=cv::countNonZero(labels==c);
        p.classPixels[c]+=count;
        if(p.ClassKind(c)==kClassKindGood)p.okPixels+=count;else p.ngPixels+=count;
    }
    return 0;
}
int SegV2Model::AddInstances(const InstanceLabel* labels,int count) {
    auto& p=*impl_;p.lastError.clear();
    if(!p.instanceHead){p.lastError="AddInstances: the instance head is not enabled (settings key instanceHead=1)";return -2;}
    if(p.images.empty()||p.instanceLabels.size()!=p.images.size()){p.lastError="AddInstances: add a sample first";return -2;}
    if(count<0||(count>0&&!labels)){p.lastError="AddInstances: invalid label array";return -3;}
    const int classes=p.trainClasses-1;
    for(int i=0;i<count;++i) {
        const InstanceLabel& l=labels[i];
        const bool finite=std::isfinite(l.cx)&&std::isfinite(l.cy)&&std::isfinite(l.w)&&std::isfinite(l.h)&&std::isfinite(l.theta);
        if(!finite||!(l.w>0)||!(l.h>0)||l.classIndex<0||l.classIndex>=classes||(l.kind!=InstanceKind::RotatedBox&&l.kind!=InstanceKind::Circle)) {
            p.lastError="AddInstances: label "+std::to_string(i)+" is invalid (non-finite value, size <= 0, unknown kind or classIndex outside 0.."+std::to_string(classes-1)+"); nothing was attached";
            return -3;
        }
    }
    auto& target=p.instanceLabels.back();
    const size_t firstNew=target.size();
    for(int i=0;i<count;++i) {
        InstanceLabel l=labels[i];
        if(l.kind==InstanceKind::Circle){const double d=.5*(l.w+l.h);l.w=d;l.h=d;l.theta=0;}else l.theta=WrapAngle(l.theta);
        target.push_back(l);
    }
    // EAI-POS P4: class templates. The labels are in source pixels; the stored sample is the letterboxed content (same scale per axis
    // up to rounding), so the labels are mapped with the content / source size ratio before the square patch is sampled.
    if(count>0&&classes>0) {
        const cv::Mat& content=p.images.back().first;
        const cv::Size source=p.instanceSourceSize.back();
        if(p.instanceTemplateSum.empty()){p.instanceTemplateSum=cv::Mat::zeros(classes,kInstanceTemplateSize*kInstanceTemplateSize,CV_32FC1);p.instanceTemplateCount.assign(size_t(classes),0);}
        cv::Mat gray8,gray;
        cv::cvtColor(content,gray8,cv::COLOR_BGR2GRAY);gray8.convertTo(gray,CV_32FC1);
        const double sx=double(content.cols)/source.width,sy=double(content.rows)/source.height,scale=.5*(sx+sy);
        for(size_t i=firstNew;i<target.size();++i) {
            InstanceLabel l=target[i];
            l.cx*=sx;l.cy*=sy;l.w*=scale;l.h*=scale;
            // The class symmetry as the trainer resolves it (empty = 1, one value = all classes, else per class).
            const auto& symmetryConfig=p.instanceSymmetryConfig;
            const int symmetry=symmetryConfig.empty()?1:(symmetryConfig.size()==1?symmetryConfig[0]:(size_t(l.classIndex)<symmetryConfig.size()?symmetryConfig[size_t(l.classIndex)]:1));
            AccumulateInstanceTemplate(gray,l,kInstanceTemplateSize,symmetry,p.instanceTemplateSum,p.instanceTemplateCount);
        }
    }
    return 0;
}
bool SegV2Model::HasInstanceHead() const { return impl_->instanceReady; }
int SegV2Model::InstanceClassCount() const { return impl_->instanceReady?impl_->instanceWeights.classes:0; }
int SegV2Model::PredictInstances(const cv::Mat& image,bool gpu,float threshold,int quality,std::vector<DecodedInstance>& out) {
    auto& p=*impl_;p.lastError.clear();p.lastInput=image.size();out.clear();
    if(!p.network){p.Error("predict instances",gpu,"Model is not loaded");return -1;}
    if(!p.instanceReady){p.Error("predict instances",gpu,"the model has no instance head");return -11;}
    try {
        if(image.empty()||image.type()!=CV_8UC3)throw std::runtime_error("Expected nonempty BGR8 image");
        if(p.originalInput)throw std::runtime_error("Instance prediction needs a fixed-size (non original-input) model");
        using PhaseClock=std::chrono::steady_clock;
        const auto phaseBegin=PhaseClock::now();
        p.timings=SegV2Timings();
        const std::string device=p.Device(gpu,true);
        auto frame=p.Prepare(image,cv::Mat());
        const auto phasePrepared=PhaseClock::now();
        const int classes=p.instanceWeights.classes,grid=p.Grid,cells=grid*grid;
        std::vector<float> cudaOutput;ov::Tensor output;const float* data=nullptr;
        double compileMs=0,cudaCompileMs=0;
        std::string runDevice=device;
#ifdef SEGAI_WITH_CUDA
        if(runDevice=="CUDA") {
            // Instance net = clone of the semantic network cut at the feature concat + the two dense 1x1 layers (all Conv/Add/Relu,
            // already covered by OvToOnnx). Built once per loaded model / backend selection; failure is sticky and falls back to CPU.
            if(!p.instanceCuda) {
                const auto compileStart=PhaseClock::now();
                try {
                    auto model=p.BuildInstanceNetwork();
                    p.instanceCuda.reset(new segai_cuda::OrtCudaBackend(model,0,false));
                    p.instanceDevice="CUDA";
                } catch (const std::exception& e) { p.cudaNote=std::string("instance head: ")+e.what(); }
                catch (...) { p.cudaNote="instance head: unknown CUDA error"; }
                if(!p.instanceCuda) { p.cudaFailed=true; std::cerr<<"SegAI CUDA unavailable for the instance head, falling back to CPU: "<<p.cudaNote<<std::endl; }
                cudaCompileMs=std::chrono::duration<double,std::milli>(PhaseClock::now()-compileStart).count();
            }
            if(!p.instanceCuda) runDevice="CPU";
        }
#else
        if(runDevice=="CUDA") runDevice="CPU";
#endif
        // Instance input buffer is owned by the handle and reused across calls (Lane A perf: no per-call allocation).
        ov::Tensor inputTensor=p.Input(frame.image, true);
        const auto phaseTensor=PhaseClock::now();
#ifdef SEGAI_WITH_CUDA
        if(runDevice=="CUDA") {
            // A CUDA Run failure (driver reset, out of memory, ...) must not fail the call: fall back to CPU for THIS call, drop the
            // broken session, and make the failure sticky + visible (LastGpuFellBack/LastGpuNote) like a compile failure.
            try {
#ifdef SEGAI_TEST_INSTANCE_CUDA_RUN_FAILURE
                // Dedicated fault-injection build only. This code and string are absent from production binaries.
                throw std::runtime_error("SEGAI_TEST_INSTANCE_CUDA_RUN_FAILURE");
#endif
                cudaOutput=p.instanceCuda->Run(inputTensor.data<float>());
                if(cudaOutput.size()!=size_t(classes+kInstanceRegChannels)*size_t(cells))throw std::runtime_error("Instance output shape mismatch");
                data=cudaOutput.data();
            } catch (const std::exception& e) { p.cudaNote=std::string("instance head: CUDA Run failed: ")+e.what(); }
            catch (...) { p.cudaNote="instance head: CUDA Run failed: unknown error"; }
            if(!data) {
                p.instanceCuda.reset();p.cudaFailed=true;cudaOutput.clear();runDevice="CPU";
                std::cerr<<"SegAI CUDA failed while running the instance head, falling back to CPU for this call: "<<p.cudaNote<<std::endl;
            }
        }
#endif
        if(!data) {
            if(p.instanceDevice!=runDevice) {
                const auto compileStart=PhaseClock::now();
                auto model=p.BuildInstanceNetwork();
                p.instanceCompiled=p.Compile(model,frame.image.size(),runDevice);
                p.instanceRequest=p.instanceCompiled.create_infer_request();p.instanceDevice=runDevice;
                compileMs+=std::chrono::duration<double,std::milli>(PhaseClock::now()-compileStart).count();
            }
            p.instanceRequest.set_input_tensor(inputTensor);p.instanceRequest.infer();
            output=p.instanceRequest.get_output_tensor();
            if(output.get_element_type()!=ov::element::f32||output.get_shape()!=ov::Shape{1,size_t(classes+kInstanceRegChannels),size_t(grid),size_t(grid)})
                throw std::runtime_error("Instance output shape mismatch");
            data=output.data<float>();
        }
        const auto phaseInferred=PhaseClock::now();
        if(p.captureInstanceTensor){p.lastInstanceChannels=classes+kInstanceRegChannels;p.lastInstanceGrid=grid;p.lastInstanceTensor.assign(data,data+size_t(classes+kInstanceRegChannels)*cells);}
        std::vector<float> heat(size_t(classes)*cells);
        for(size_t i=0;i<heat.size();++i)heat[i]=1.f/(1.f+std::exp(-data[i]));
        DecodeOptions options;options.scoreThreshold=threshold;options.nmsIou=.5f;options.maxInstances=1000;options.grid=grid;options.stride=8;
        std::vector<DecodedInstance> decoded;
        DecodeInstances(heat.data(),data+size_t(classes)*cells,classes,p.instanceWeights.symmetry,options,decoded);
        // EAI-POS P4 quality: refine on the network input frame (the templates are scale free, so the frame and the source sample
        // share the square sampling). A refinement that fails keeps the head result with refined = 0.
        if(quality==1&&!decoded.empty()) {
            cv::Mat gray8,gray;cv::cvtColor(frame.image,gray8,cv::COLOR_BGR2GRAY);gray8.convertTo(gray,CV_32FC1);
            for(DecodedInstance& d:decoded) {
                const int k=(d.classIndex>=0&&d.classIndex<int(p.instanceWeights.symmetry.size()))?p.instanceWeights.symmetry[size_t(d.classIndex)]:1;
                RefineInstance(gray,p.instanceTemplates,k,d);
            }
        }
        // Network input pixels -> pixels of `image`: undo the letterbox (resize to frame.width x frame.height at left/top).
        const double sx=double(frame.width)/image.cols,sy=double(frame.height)/image.rows,scale=.5*(sx+sy);
        for(DecodedInstance d:decoded) {
            d.cx=(d.cx-frame.left)/sx;d.cy=(d.cy-frame.top)/sy;d.w/=scale;d.h/=scale;d.radius/=scale;
            if(d.cx<0||d.cy<0||d.cx>=image.cols||d.cy>=image.rows)continue; // a peak inside the gray padding is not an object
            out.push_back(d);
        }
        const auto phaseEnd=PhaseClock::now();
        auto ms=[](PhaseClock::time_point a,PhaseClock::time_point b){return std::chrono::duration<double,std::milli>(b-a).count();};
        // Reuses SegV2Timings for SEGAI_InferGetPhaseTimings: v2_ccscore holds the instance decode phase
        // (sigmoid + DecodeInstances + optional refine + un-letterbox); v2_infer includes the host<->device copies on CUDA.
        p.timings.prepareMs=ms(phaseBegin,phasePrepared);p.timings.tensorMs=ms(phasePrepared,phaseTensor)-cudaCompileMs;
        p.timings.inferMs=ms(phaseTensor,phaseInferred)-compileMs;p.timings.compileMs=compileMs+cudaCompileMs;
        p.timings.ccScoreMs=ms(phaseInferred,phaseEnd);p.timings.totalMs=ms(phaseBegin,phaseEnd);
        return 0;
    }catch(const std::exception& e){p.Error("predict instances",gpu,e.what());out.clear();return -8;}
}
int SegV2Model::DefectPixels() const { return impl_->ngPixels; }
int SegV2Model::NormalPixels() const { return impl_->okPixels; }
bool SegV2Model::HasNormalClass() const { return impl_->normalEnabled; }
bool SegV2Model::OriginalInput() const { return impl_->originalInput; }
bool SegV2Model::HasNgClass() const { return impl_->ngEnabled; }
int SegV2Model::SetClassTable(const unsigned char* data, size_t bytes, std::string& error) {
    auto& p=*impl_;
    if(!p.images.empty()||p.ngPixels>0||p.okPixels>0||p.network) { error="the class table must be set after ConfigureV2 and before the first AddSample"; return -2; }
    SegClassTable table;
    // Enabled flags are derived from the training data at save time, so the incoming ones are neither required nor kept.
    if(!DecodeClassTable(data,bytes,table,error,false)) return -3;
    for(auto& entry:table.classes) entry.enabled=true;
    if(!ValidateTrainerClassTable(table,error)) return -3;
    // Any codec-valid table (1..32 classes, at most one GOOD) is trainable since S2b; the trainer then works with K = N + 1.
    p.classTable=table;p.classTableSet=true;p.trainClasses=int(table.classes.size())+1;return 0;
}
SegClassTable SegV2Model::GetClassTable() const {
    const auto& p=*impl_;
    if(p.genericClasses) return p.classTable; // loaded model: the stored table, flags included
    if(p.classTableSet) { // trained or loaded with a table: the flags are the per-class enabled state
        SegClassTable table=p.classTable;
        for(size_t c=0;c<table.classes.size();++c) table.classes[c].enabled=p.ClassEnabled(int(c)+1);
        return table;
    }
    SegClassTable table=MakeLegacyClassTable();
    table.classes[0].enabled=p.ngEnabled;table.classes[1].enabled=p.normalEnabled;
    return table;
}
const std::string& SegV2Model::LastError() const { return impl_->lastError; }
void SegV2Model::SetGpuBackend(int backend) {
    auto& p = *impl_;
    const int normalized = backend == 1 ? 1 : 0;
    // ConfigureInferer is called for every image by the managed Counter predictor. Selecting the same healthy backend
    // must not discard a live ORT session (and re-convert the graph / re-run cuDNN search on the next frame).
    // A failed backend may still be explicitly reselected to retry after the runtime has recovered.
    if (p.gpuBackend == normalized && !p.cudaFailed) return;
    if (p.gpuBackend != normalized) { p.compiledDevice.clear(); p.compiledSize = cv::Size(); }
    p.gpuBackend = normalized; p.cudaFailed = false; p.cudaNote.clear();
#ifdef SEGAI_WITH_CUDA
    p.cuda.reset();
    p.instanceCuda.reset(); if (p.instanceDevice == "CUDA") p.instanceDevice.clear();
#endif
}
int SegV2Model::GpuBackend() const { return impl_->gpuBackend; }
bool SegV2Model::LastGpuFellBack() const { return impl_->gpuBackend == 1 && impl_->cudaFailed; }
const std::string& SegV2Model::LastGpuNote() const { return impl_->cudaNote; }
bool CudaBackendAvailable(std::string& reason) {
#ifdef SEGAI_WITH_CUDA
    return segai_cuda::ProbeCuda(reason);
#else
    reason = "BeeNativeSegAI.dll was built without CUDA support";
    return false;
#endif
}
const SegV2Timings& SegV2Model::LastTimings() const { return impl_->timings; }
const std::vector<float>& SegV2Model::LastInstanceTensor(int& channels, int& grid) const {
    channels = impl_->lastInstanceChannels; grid = impl_->lastInstanceGrid; return impl_->lastInstanceTensor;
}
bool SegV2Model::GetLastClassMaps(cv::Mat& label, cv::Mat& score) const {
    const auto& p=*impl_;
    if(p.lastLabelMap.empty()||p.lastScoreMap.empty()) return false;
    label=p.lastLabelMap;score=p.lastScoreMap;return true;
}
void SegV2Model::ClearSamples() {
    impl_->images.clear(); impl_->ngPixels = 0; impl_->okPixels = 0;
    impl_->instanceLabels.clear(); impl_->instanceSourceSize.clear();
    impl_->instanceReady = false; impl_->instanceWeights = InstanceHeadWeights(); impl_->instanceCompiled = ov::CompiledModel(); impl_->instanceRequest = ov::InferRequest(); impl_->instanceDevice.clear();
#ifdef SEGAI_WITH_CUDA
    impl_->instanceCuda.reset();
#endif
    impl_->instanceTemplates = InstanceTemplateSet(); impl_->instanceTemplateSum = cv::Mat(); impl_->instanceTemplateCount.clear();
    for (int c = 0; c < Impl::MaxClasses; ++c) { impl_->classPixels[c] = 0; impl_->classDisabled[c] = false; }
    impl_->minimumFraction = 1.; impl_->network.reset(); impl_->lastError.clear(); impl_->normalEnabled = true; impl_->ngEnabled = true; impl_->winnerForegroundBoundary = false;
    impl_->compiledDevice.clear();
    // V145 Retrain: the saved-for-resume snapshot belongs to a specific trained network; resume* config-side
    // state (resumeRequested/resumeLoaded/...) comes from Configure, not from samples, and stays untouched.
    impl_->savedHead = Impl::Head(); impl_->hasSavedHead = false;
}
int SegV2Model::Train(const std::function<void(int)>& progress,const volatile int* cancel) {
    auto& p=*impl_;p.cancelFlag=cancel;p.progress=progress;p.lastError.clear();p.minimumFraction=1.;
    try {
        p.CheckCancel();
        p.ReconcileResumeClasses();
        if (p.images.empty()) throw std::runtime_error("No training samples");
        p.stage="load backbone";
        Impl::StageTimer totalTimer(p,"train_total");
        std::unique_ptr<Impl::StageTimer> phase(new Impl::StageTimer(p,"load_backbone"));
        auto features=p.FeatureModel(p.Core(),p.backbone);
        const std::string device=p.Device(p.trainGpu);
        struct Job { size_t image; int variant; bool held; };
        std::vector<Job> jobs;
        for(size_t i=0;i<p.images.size();++i) {
            p.CheckCancel();bool held=p.validationIndices.count(int(i))!=0;
            // V146 Retrain (changed images only): `skip` only ever excludes a frame from the jobs pushed below —
            // minimumFraction (I2) stays computed exactly as before, unconditional on skip.
            const bool skip = p.deltaActive && !held && p.trainSkipIndices.count(int(i)) != 0;
            const auto& pair=p.images[i];const int variants=(!held && p.augment)?6:1;
            if(!held) for(int label=1;label<p.trainClasses;++label) {
                cv::Mat ids,stats,centers;int count=cv::connectedComponentsWithStats(pair.second==label,ids,stats,centers,8);
                for(int id=1;id<count;++id) p.minimumFraction=std::min(p.minimumFraction,double(stats.at<int>(id,cv::CC_STAT_AREA))/pair.second.total());
            }
            if(!skip) for(int variant=0;variant<variants;++variant) jobs.push_back(Job{i,variant,held});
        }
        if (jobs.empty()) throw std::runtime_error("No training samples");
        // Regular feature extraction runs as parallel jobs.  Tiled GPU extraction stays serial,
        // with one bounded request, while each worker still owns its regular infer requests.
        std::mutex compileLock;std::map<std::pair<int,int>,ov::CompiledModel> compiledBySize;
        auto requestFor=[&](cv::Size size)->ov::InferRequest {
            std::lock_guard<std::mutex> lock(compileLock);
            auto key=std::make_pair(size.width,size.height);auto found=compiledBySize.find(key);
            if(found==compiledBySize.end()) found=compiledBySize.emplace(key,p.Compile(features,size,device,ov::hint::PerformanceMode::THROUGHPUT)).first;
            return found->second.create_infer_request();
        };
        // EAI-LOGO: with a backbone fine-tune the instance rows wait for the fine-tuned features (second pass below).
        const bool deferInstance=p.instanceHead&&p.backboneEpochs>0;
        // EAI-POS P3: per-job instance rows (non-held jobs only), in job order; counters for the status file.
        std::atomic<int> instanceTrained{0},instanceSkipped{0},instanceDropped{0};
        std::vector<InstanceTrainRows> instanceResults;
        if(p.instanceHead) {
            const int classes=p.trainClasses-1;
            if(p.instanceSymmetryConfig.empty()) p.instanceSymmetryConfig.assign(size_t(classes),1);
            else if(p.instanceSymmetryConfig.size()==1) p.instanceSymmetryConfig.assign(size_t(classes),p.instanceSymmetryConfig[0]);
            else if(int(p.instanceSymmetryConfig.size())!=classes) throw std::runtime_error("angleSymmetry has "+std::to_string(p.instanceSymmetryConfig.size())+" values but the model has "+std::to_string(classes)+" classes");
            if(p.instanceLabels.size()!=p.images.size()) throw std::runtime_error("Instance labels are out of step with the samples");
            instanceResults.resize(jobs.size());
        }
        auto runJob=[&](const Job& job,std::map<std::pair<int,int>,ov::InferRequest>& requests,
                        ov::CompiledModel& tiledCompiled,ov::InferRequest& tiledRequest,cv::Size& tiledSize,
                        InstanceTrainRows* instanceOut,bool instanceOnly)->Impl::Sample {
            const auto& pair=p.images[job.image];cv::Mat view=pair.first,labels=pair.second;
            cv::Mat transform; // EAI-POS: the variant's warpAffine matrix (empty for variant 0), reused for the instance labels
            if(job.variant) {
                const double angles[]={0.,-p.rotation,p.rotation,-90.,90.,180.};
                const double angle=job.variant<3?angles[job.variant]:(p.rotation>=90?angles[job.variant]:angles[job.variant%3]);
                const double objectScale = job.variant % 2 ? p.trainingScaleMin : p.trainingScaleMax;
                transform=cv::getRotationMatrix2D(cv::Point2f(float(view.cols)/2,float(view.rows)/2),angle,objectScale);
                cv::Mat warped,warpedLabels;
                cv::warpAffine(pair.first,warped,transform,pair.first.size(),cv::INTER_LINEAR,cv::BORDER_CONSTANT,cv::Scalar(114,114,114));
                cv::warpAffine(pair.second,warpedLabels,transform,pair.second.size(),cv::INTER_NEAREST,cv::BORDER_CONSTANT,cv::Scalar(255));
                warped.convertTo(warped,CV_8U,1.+(job.variant%2?-p.brightness:p.brightness));
                view=warped;labels=warpedLabels;
            }
            Impl::Frame frame;
            { Impl::StageTimer timer(p,"prepare_frame_thread_sum");frame=p.Prepare(view,labels,false); }
            // V147: 20MP original-input GPU feature extraction remains serial and bounded.
            // Selection is global before the tiles run, so RNG/order/caps retain Gather's semantics.
            std::seed_seq seed{job.held?29u:17u,unsigned(job.image),unsigned(job.variant)};std::mt19937 random(seed);
            if (p.UseTiledOriginalInput(frame,device)) {
                Impl::StageTimer timer(p,"gather_tiled_thread_sum");
                return p.GatherTiled(tiledCompiled,tiledRequest,tiledSize,features,frame,device,random);
            }
            auto key=std::make_pair(frame.image.cols,frame.image.rows);auto found=requests.find(key);
            if(found==requests.end()) { Impl::StageTimer timer(p,"compile_request_thread_sum");found=requests.emplace(key,requestFor(frame.image.size())).first; }
            // Seed per image/variant so sampling does not depend on which worker ran the job;
            // validation labels still never change which training pixels are sampled.
            cv::Mat featureRows;
            { Impl::StageTimer timer(p,"backbone_features_thread_sum");featureRows=p.FeatureRows(found->second,frame); }
            Impl::StageTimer gatherTimer(p,"gather_rows_thread_sum");
            Impl::Sample sampled;
            if(!instanceOnly) sampled=p.Gather(featureRows,frame,random);
            // EAI-POS P3: after the semantic gather, from the same (read-only) feature rows, with its own RNG.
            if(instanceOut) {
                if(p.GatherInstances(featureRows,transform,pair.first.size(),job.image,job.variant,*instanceOut)) instanceTrained+=1; else instanceSkipped+=1;
                instanceDropped+=instanceOut->dropped;
            }
            return sampled;
        };
        phase.reset(new Impl::StageTimer(p,"compile_features_first"));
        p.stage="compile features";p.lastInput=p.images[jobs[0].image].first.size();
        unsigned workers=1;
        bool hasTiledOriginalInput=false;
        if (p.originalInput && p.trainGpu) for (const auto& job : jobs) {
            const cv::Size size=p.images[job.image].first.size();
            if (size.width>Impl::TileMaximum || size.height>Impl::TileMaximum) { hasTiledOriginalInput=true;break; }
        }
        if (hasTiledOriginalInput) {
            // A prior Predict may have cached a full-frame GPU graph.  Training must not
            // retain it alongside the bounded feature request.
            p.request=ov::InferRequest();p.compiled=ov::CompiledModel();p.compiledDevice.clear();p.compiledSize=cv::Size();
        }
        if (!hasTiledOriginalInput) {
            auto first=p.Prepare(p.images[jobs[0].image].first,cv::Mat(),false);requestFor(first.image.size());
            std::lock_guard<std::mutex> lock(compileLock);
            try { workers=compiledBySize.begin()->second.get_property(ov::optimal_number_of_infer_requests); } catch (...) { workers=1; }
        }
        // Each worker holds full-size image copies, so cap the pool to bound memory.
        workers=std::max(1u,std::min({workers,4u,unsigned(jobs.size())}));
        phase.reset(new Impl::StageTimer(p,"sample_features_wall"));
        p.stage="sample features";
        std::vector<Impl::Sample> results(jobs.size());
        std::atomic<size_t> next{0},done{0};std::atomic<bool> stop{false};
        std::exception_ptr failure;std::mutex failureLock;
        std::vector<std::thread> threads;
        auto stopAndJoin=[&]{ stop=true; for(auto& thread:threads) if(thread.joinable()) thread.join(); };
        try {
            for(unsigned w=0;w<workers;++w) threads.emplace_back([&]{
                std::map<std::pair<int,int>,ov::InferRequest> requests;
                ov::CompiledModel tiledCompiled;ov::InferRequest tiledRequest;cv::Size tiledSize;
                try {
                    for(;;) {
                        if(stop || (p.cancelFlag && *p.cancelFlag)) break;
                        const size_t index=next++;if(index>=jobs.size()) break;
                        results[index]=runJob(jobs[index],requests,tiledCompiled,tiledRequest,tiledSize,p.instanceHead&&!deferInstance&&!jobs[index].held?&instanceResults[index]:nullptr,false);++done;
                    }
                } catch(...) {
                    std::lock_guard<std::mutex> lock(failureLock);if(!failure) failure=std::current_exception();stop=true;
                }
            });
            // Progress and cancel are polled here so managed callbacks stay on the training thread.
            while(done<jobs.size() && !stop) {
                if(p.cancelFlag && *p.cancelFlag) break;
                if(progress) progress(int(done*40/jobs.size()));
                std::this_thread::sleep_for(std::chrono::milliseconds(50));
            }
        } catch(...) { stopAndJoin(); throw; }
        stopAndJoin();
        if(failure) std::rethrow_exception(failure);
        p.CheckCancel();
        if(progress) progress(40);
        phase.reset(new Impl::StageTimer(p,"concat_rows"));
        Impl::Sample training,validation;
        // V149: exact-size concat (no cv::Mat::push_back capacity doubling). Pass 1 sums the rows per destination and
        // checks shape/type consistency; pass 2 allocates once and copies in ascending job order (same row order as
        // before), freeing each result as it is consumed. A destination with no rows stays default-constructed (empty)
        // so Train reports the same "No usable ..." error and hasValidation stays false.
        {
            int totalRows[2]={0,0},columns[2]={0,0},elementType[2]={0,0};bool seen[2]={false,false};
            for(size_t index=0;index<jobs.size();++index) {
                const auto& item=results[index];if(item.rows.empty()) continue;
                const int slot=jobs[index].held?1:0;
                CV_Assert(item.rows.dims==2 && item.labels.type()==CV_32S && item.labels.cols==1 && item.labels.rows==item.rows.rows);
                if(!seen[slot]) { seen[slot]=true;columns[slot]=item.rows.cols;elementType[slot]=item.rows.type(); }
                else { CV_Assert(item.rows.cols==columns[slot] && item.rows.type()==elementType[slot]); }
                totalRows[slot]+=item.rows.rows;
            }
            Impl::Sample* targets[2]={&training,&validation};int offset[2]={0,0};
            for(int slot=0;slot<2;++slot) if(totalRows[slot]>0) {
                targets[slot]->rows.create(totalRows[slot],columns[slot],elementType[slot]);
                targets[slot]->labels.create(totalRows[slot],1,CV_32S);
            }
            for(size_t index=0;index<jobs.size();++index) {
                auto& item=results[index];
                // V161: pre-cap class counts of the TRAINING slot only, summed in job order on this thread (deterministic).
                if(!jobs[index].held) for(int c=0;c<p.trainClasses;++c) training.natural[c]+=item.natural[c];
                if(!item.rows.empty()) {
                    const int slot=jobs[index].held?1:0,count=item.rows.rows;
                    cv::Mat rowsDestination=targets[slot]->rows.rowRange(offset[slot],offset[slot]+count);
                    cv::Mat labelsDestination=targets[slot]->labels.rowRange(offset[slot],offset[slot]+count);
                    item.rows.copyTo(rowsDestination);item.labels.copyTo(labelsDestination);
                    offset[slot]+=count;
                }
                results[index]=Impl::Sample();
            }
        }
        phase.reset(new Impl::StageTimer(p,"head_train_total"));
        p.stage="train head";auto head=p.Train(training,validation,p.logFolder);
        phase.reset();
        training=Impl::Sample();validation=Impl::Sample(); // V150: free the concatenated feature matrices before AddHead/save
        if(deferInstance) {
            // EAI-LOGO: fine-tune the backbone first, then gather the instance rows again from the fine-tuned features.
            { Impl::StageTimer timer(p,"finetune_backbone");p.stage="fine-tune backbone";p.FineTuneBackbone(features,head); }
            compiledBySize.clear();
            p.stage="sample instance features";
            std::map<std::pair<int,int>,ov::InferRequest> secondRequests;ov::CompiledModel secondTiledCompiled;ov::InferRequest secondTiledRequest;cv::Size secondTiledSize;
            Impl::StageTimer secondTimer(p,"instance_features_second_pass");
            for(size_t index=0;index<jobs.size();++index) {
                if(jobs[index].held) continue;
                p.CheckCancel();
                runJob(jobs[index],secondRequests,secondTiledCompiled,secondTiledRequest,secondTiledSize,&instanceResults[index],true);
            }
        }
        // EAI-POS P3: the instance branch is trained after (and independently of) the semantic head: own rows, RNG, Adam. A failure
        // here fails the whole training before p.network is replaced, so no half-built model can be saved.
        p.instanceReady=false;p.instanceWeights=InstanceHeadWeights();p.instanceCompiled=ov::CompiledModel();p.instanceRequest=ov::InferRequest();p.instanceDevice.clear();
#ifdef SEGAI_WITH_CUDA
        p.instanceCuda.reset();
#endif
        p.instanceTemplates=InstanceTemplateSet();
        if(p.instanceHead) {
            Impl::StageTimer timer(p,"instance_head_train");p.stage="train instance head";
            InstanceTrainRows instanceRows;ConcatInstanceRows(instanceResults,instanceRows);instanceResults.clear();instanceResults.shrink_to_fit();
            InstanceTrainOptions options;options.epochs=p.instanceEpochs;options.learningRate=p.instanceLearningRate;            InstanceTrainReport report;std::string instanceError;
            const int instanceRowCount=instanceRows.Rows();const double instancePositives=instanceRows.positives;
            const bool trained=TrainInstanceHead(instanceRows,p.trainClasses-1,p.instanceSymmetryConfig,options,
                [&]()->bool{return p.cancelFlag&&*p.cancelFlag;},
                [&](int epoch){ if(progress) progress(95+epoch*4/options.epochs); },
                p.instanceWeights,report,instanceError);
            p.CheckCancel();
            std::ostringstream status;status.precision(9);
            status<<"instance_head; rows="<<instanceRowCount<<"; positives="<<instancePositives<<"; variants_used="<<instanceTrained.load()
                  <<"; variants_skipped_affine="<<instanceSkipped.load()<<"; labels_dropped="<<instanceDropped.load()<<"; epochs="<<report.epochs
                  <<"; first_loss="<<report.firstLoss<<"; last_loss="<<report.lastLoss<<"; focal="<<report.focalLast<<"; reg="<<report.regLast;
            try { std::ofstream file(p.logFolder/"instance.txt",std::ios::trunc);file<<status.str()<<'\n'; } catch (...) {}
            if(!trained) throw std::runtime_error(instanceError+" ["+status.str()+"]");
            p.instanceReady=true;
            FinalizeInstanceTemplates(p.instanceTemplateSum,p.instanceTemplateCount,p.instanceTemplates); // EAI-POS P4
        }
        if(p.backboneEpochs>0&&!deferInstance) { Impl::StageTimer timer(p,"finetune_backbone");p.stage="fine-tune backbone";p.FineTuneBackbone(features,head); }
        { Impl::StageTimer timer(p,"add_head_network");p.network=p.AddHead(features,head); }
        // EAI-CLS S2b: a model that was just trained predicts with its own class layout (identical to the Load defaults for K = 3).
        if(p.classTableSet) for(size_t c=0;c<p.classTable.classes.size();++c) p.classTable.classes[c].enabled=p.ClassEnabled(int(c)+1);
        p.inferClasses=p.trainClasses;p.genericClasses=p.classTableSet&&!IsLegacyClassLayout(p.classTable);
        p.compiledDevice.clear();p.compiledSize=cv::Size();
        totalTimer.Stop();p.WriteStageTimes();
        p.CheckCancel();p.images.clear();p.instanceLabels.clear();p.instanceSourceSize.clear();p.instanceTemplateSum=cv::Mat();p.instanceTemplateCount.clear();p.cancelFlag=nullptr;p.progress=nullptr;return 0;
    } catch(const std::exception& e) {
        p.Error(p.stage,p.trainGpu,e.what());
        try { std::ofstream error(p.logFolder/"error.txt");error<<p.lastError; } catch (...) {}
        p.cancelFlag=nullptr;p.progress=nullptr;return cancel&&*cancel?-10:-8;
    }
}
bool SegV2Model::Save(const std::wstring& path,float threshold,unsigned minimumArea) {
    auto& p=*impl_;p.Error("save",p.trainGpu,"No trained network or cannot write model file");if(!p.network)return false;
    // EAI-PLUS S5 step 0: time the save and rewrite stage_times.csv when this model was just trained (measurement only).
    Impl::StageTimer saveTimer(p,"save_model");
    struct SaveGuard { Impl& owner; Impl::StageTimer& timer; ~SaveGuard() { timer.Stop(); bool trained; { std::lock_guard<std::mutex> g(owner.stageStats->lock); trained = owner.stageStats->values.count("train_total") != 0; } if (trained) owner.WriteStageTimes(); } } saveGuard{p,saveTimer};
    try {
        // EAI-CLS S1: a trainer that was given a class table writes format v4; without one the file stays the v3 it always was.
        const bool writeTable=p.classTableSet;
        std::vector<unsigned char> tableBytes;
        uint32_t classCount=3; // K = N + 1 background and class channels; 3 for every version 3 file
        if(writeTable) {
            std::string tableError;
            const SegClassTable tableToSave=GetClassTable();
            if(!EncodeClassTable(tableToSave,tableBytes,tableError)) throw std::runtime_error("class table: "+tableError);
            classCount=uint32_t(tableToSave.classes.size()+1);
        }
        std::ostringstream xml,bin;ov::pass::Serialize serializer(xml,bin);serializer.run_on_model(p.network);
        std::string x=xml.str(),b=bin.str();
        cv::FileStorage storage("v2.yml",cv::FileStorage::WRITE|cv::FileStorage::MEMORY|cv::FileStorage::BASE64);
        // V145 Retrain: a backbone that cannot be stat'd must not fail an ordinary (non-Retrain) Save — use
        // the non-throwing overload and only claim resume data (resume_head) when the size lookup succeeded.
        std::error_code backboneError;
        const std::uintmax_t backboneBytes = p.hasSavedHead ? fs::file_size(fs::u8path(p.backbone), backboneError) : 0;
        const bool writeResume = p.hasSavedHead && !backboneError;
        // Older runtimes reject original-input models instead of silently resizing them.
        storage<<"native_v2"<<(p.originalInput?2:1)<<"side"<<p.Side<<"minimum_fraction"<<p.minimumFraction;
        // v4 keeps the enabled flags in the class table, so its payload has no active_classes key (v3 payload bytes unchanged).
        if(!writeTable) storage<<"active_classes"<<(1|(p.ngEnabled?2:0)|(p.normalEnabled?4:0));
        storage<<"postprocess_version"<<(p.compactPostprocess?2:1);
        // V161: only a prior-balanced model carries the key (legacy payload bytes stay identical); the first ~1KB scan window is kept.
        if (p.priorBalanced) storage << "prior_balanced" << 1;
        // resume_head is conditional so old-shaped payloads (no Retrain data) are unchanged.
        if (writeResume) storage << "resume_head" << 1;
        storage<<"network_xml"<<cv::Mat(1,int(x.size()),CV_8U,const_cast<char*>(x.data()));
        storage<<"network_bin"<<cv::Mat(1,int(b.size()),CV_8U,const_cast<char*>(b.data()));
        if (writeResume) {
            // Written after the big network_xml/network_bin blobs so the early-payload scan window used by
            // managed code (native_v2/postprocess_version/active_classes, first ~1KB) is undisturbed.
            storage << "resume_hidden_weights" << p.savedHead.hiddenWeights << "resume_hidden_bias" << p.savedHead.hiddenBias
                    << "resume_weights" << p.savedHead.weights << "resume_bias" << p.savedHead.bias
                    << "resume_mean" << p.savedHead.mean << "resume_std" << p.savedHead.sd;
            storage << "resume_backbone" << fs::u8path(p.backbone).filename().u8string()
                    << "resume_backbone_bytes" << std::to_string(backboneBytes);
        }
        // EAI-POS P3: the instance head is additive payload keys written LAST (after network_xml/network_bin and the resume block), so
        // the bytes before them, the ~1KB managed scan window and the header (version 3/4, featureCount, numClasses) are exactly what a
        // model without the head would have. An older runtime ignores the unknown keys and loads the semantic part only.
        if (p.instanceReady) {
            std::string instanceReason;
            if (!p.instanceWeights.Valid(&instanceReason)) throw std::runtime_error("instance head: "+instanceReason);
            const cv::Mat symmetry(1,int(p.instanceWeights.symmetry.size()),CV_32S,const_cast<int*>(p.instanceWeights.symmetry.data()));
            storage << "instance_version" << int(kInstanceHeadVersion) << "instance_head" << 1 << "instance_classes" << p.instanceWeights.classes
                    << "instance_symmetry" << symmetry
                    << "instance_w1" << p.instanceWeights.w1 << "instance_b1" << p.instanceWeights.b1
                    << "instance_w2" << p.instanceWeights.w2 << "instance_b2" << p.instanceWeights.b2;
            // EAI-POS P4: optional additive template keys (absent = no refinement templates; older readers ignore them).
            if(!p.instanceTemplates.Empty()) {
                std::string templateReason;
                if(!p.instanceTemplates.Valid(p.instanceWeights.classes,&templateReason)) throw std::runtime_error("instance templates: "+templateReason);
                storage << "instance_template_size" << p.instanceTemplates.size << "instance_templates" << p.instanceTemplates.data;
            }
        }
        std::string payload=storage.releaseAndGetString();SegAIHeader header={};
        std::memcpy(header.magic,"SEGAI",5);header.version=writeTable?4:3;header.featureFlags=0x02000000;header.numClasses=classCount;header.featureCount=448;
        header.defectThreshold=threshold;header.minDefectArea=minimumArea;header.payloadSize=payload.size();header.crc32=V2Crc(payload);
        header.reservedA=uint32_t(tableBytes.size()); // 0 for v3; class-table byte size for v4
        std::ofstream file(fs::path(path),std::ios::binary|std::ios::trunc);
        file.write(reinterpret_cast<const char*>(&header),sizeof(header));file.write(payload.data(),payload.size());
        if(writeTable) file.write(reinterpret_cast<const char*>(tableBytes.data()),std::streamsize(tableBytes.size()));
        if(!file.good())return false;p.lastError.clear();return true;
    } catch(const std::exception& e) {p.Error("save",p.trainGpu,e.what());return false;}
}
bool SegV2Model::Load(const std::wstring& path) {
    auto& p=*impl_;p.winnerForegroundBoundary=false;p.Error("load",false,"Invalid, corrupt or incompatible V2 model");
    try {
        std::ifstream file(fs::path(path),std::ios::binary|std::ios::ate);if(!file)return false;
        const auto size=file.tellg();file.seekg(0);SegAIHeader header={};file.read(reinterpret_cast<char*>(&header),sizeof(header));
        if(file && !std::memcmp(header.magic,"SEGAI\0\0",8) && header.version>kMaxSupportedSegaiVersion) {
            p.Error("load",false,"model file format version "+std::to_string(header.version)+" is newer than this runtime supports (up to "+std::to_string(kMaxSupportedSegaiVersion)+"); update the application");
            return false;
        }
        // Version 4 appends the class table after the payload (reservedA bytes); version 3 has none.
        const bool v4=file && header.version==4;
        const uint64_t tableBytes=v4?header.reservedA:0;
        if(!file || std::memcmp(header.magic,"SEGAI\0\0",8) || (header.version!=3 && !v4) || header.featureCount!=448 ||
           header.featureFlags!=0x02000000 || !header.payloadSize || header.payloadSize>256ull*1024*1024 ||
           (v4 && (tableBytes==0 || tableBytes>kMaxClassTableBytes)) || uint64_t(size)!=72+header.payloadSize+tableBytes)return false;
        if(!v4 && header.numClasses!=3)return false;
        std::string payload(size_t(header.payloadSize),'\0');file.read(&payload[0],payload.size());if(!file||V2Crc(payload)!=header.crc32)return false;
        SegClassTable table;
        if(v4) {
            std::vector<unsigned char> tableBuffer(static_cast<size_t>(tableBytes));file.read(reinterpret_cast<char*>(tableBuffer.data()),std::streamsize(tableBuffer.size()));
            std::string tableError;
            if(!file){p.Error("load",false,"class table is truncated");return false;}
            if(!DecodeClassTable(tableBuffer.data(),tableBuffer.size(),table,tableError)){p.Error("load",false,"invalid class table: "+tableError);return false;}
            if(header.numClasses!=table.classes.size()+1){p.Error("load",false,"header class count "+std::to_string(header.numClasses)+" does not match the class table ("+std::to_string(table.classes.size())+" classes)");return false;}
        }
        cv::FileStorage storage(payload,cv::FileStorage::READ|cv::FileStorage::MEMORY);
        int version=0;storage["native_v2"]>>version;storage["side"]>>p.Side;storage["minimum_fraction"]>>p.minimumFraction;
        if((version!=1&&version!=2)||p.Side<160||p.Side>2560||p.Side%32||!std::isfinite(p.minimumFraction)||p.minimumFraction<=0||p.minimumFraction>1)return false;
        if(v4) {
            // The enabled flags live in the class table (at least one is set; the payload has no active_classes key).
            // ngEnabled = any enabled DEFECT class, normalEnabled = the GOOD class is enabled (the legacy exports' meaning).
            p.ngEnabled=false;p.normalEnabled=false;
            for(const SegClassEntry& entry:table.classes) if(entry.enabled) { if(entry.kind==kClassKindGood)p.normalEnabled=true; else p.ngEnabled=true; }
            p.classTable=table;p.classTableSet=true;
            for(size_t c=0;c<table.classes.size();++c) p.classDisabled[c+1]=!table.classes[c].enabled;
            p.inferClasses=int(table.classes.size())+1;p.genericClasses=!IsLegacyClassLayout(table);
        } else {
            int active=7;if(!storage["active_classes"].empty())storage["active_classes"]>>active;
            if(active!=3&&active!=5&&active!=7)return false;p.ngEnabled=(active&2)!=0;p.normalEnabled=(active&4)!=0;
            p.classTable=SegClassTable();p.classTableSet=false;
            p.inferClasses=3;p.genericClasses=false;
        }
        int postprocess=1;if(!storage["postprocess_version"].empty())storage["postprocess_version"]>>postprocess;
        if(postprocess!=1&&postprocess!=2)return false;p.compactPostprocess=postprocess==2;
        if(p.genericClasses&&!p.compactPostprocess){p.Error("load",false,"postprocess_version 1 supports only the NG/OK class layout, not this model's class table of "+std::to_string(table.classes.size())+" classes");return false;}
        // V161: informational only (managed code reads the payload key itself); missing = legacy model.
        p.priorBalanced=false;if(!storage["prior_balanced"].empty()){int prior=0;storage["prior_balanced"]>>prior;p.priorBalanced=prior==1;}
        p.originalInput=version==2;p.compiledDevice.clear();p.compiledSize=cv::Size();
#ifdef SEGAI_WITH_CUDA
        p.cuda.reset();p.instanceCuda.reset();p.cudaFailed=false;p.cudaNote.clear(); // a new network needs a fresh ONNX session (and a fresh chance for CUDA)
#endif
        p.Grid=p.Side/8;cv::Mat xml;storage["network_xml"]>>xml;storage["network_bin"]>>p.weightsBytes;
        if(xml.type()!=CV_8U||p.weightsBytes.type()!=CV_8U||xml.empty()||p.weightsBytes.empty())return false;
        ov::Tensor weights(ov::element::u8,{p.weightsBytes.total()},p.weightsBytes.data);
        p.network=p.Core().read_model(std::string(reinterpret_cast<char*>(xml.data),xml.total()),weights);
        const auto outputShape=p.network->output().get_partial_shape();
        // Interpolate may infer positive dynamic bounds (1..) after IR reload.
        const bool dynamicOutput=outputShape.rank().is_static()&&outputShape.rank().get_length()==4&&
            outputShape[0]==1&&outputShape[1]==p.inferClasses&&outputShape[2].is_dynamic()&&outputShape[3].is_dynamic();
        const bool valid = p.network->input().get_partial_shape()==(p.originalInput?ov::PartialShape{1,3,-1,-1}:ov::PartialShape{1,3,p.Side,p.Side}) &&
            (p.originalInput?dynamicOutput:outputShape==ov::PartialShape{1,p.inferClasses,p.Grid,p.Grid});
        // EAI-POS P3: optional additive instance head (keys written last by Save). Absent in every older file = no head; a head of a newer
        // instance_version is ignored (semantic part only); a head of this version that is inconsistent fails the load.
        p.instanceReady=false;p.instanceWeights=InstanceHeadWeights();p.instanceCompiled=ov::CompiledModel();p.instanceRequest=ov::InferRequest();p.instanceDevice.clear();
        p.instanceTemplates=InstanceTemplateSet();p.instanceTemplateSum=cv::Mat();p.instanceTemplateCount.clear();
        if(valid&&!storage["instance_head"].empty()) {
            int instanceFlag=0,instanceVersion=0;storage["instance_head"]>>instanceFlag;
            if(!storage["instance_version"].empty())storage["instance_version"]>>instanceVersion;
            if(instanceFlag==1&&instanceVersion==kInstanceHeadVersion) {
                InstanceHeadWeights loaded;cv::Mat symmetry;
                storage["instance_classes"]>>loaded.classes;storage["instance_symmetry"]>>symmetry;
                storage["instance_w1"]>>loaded.w1;storage["instance_b1"]>>loaded.b1;storage["instance_w2"]>>loaded.w2;storage["instance_b2"]>>loaded.b2;
                if(symmetry.type()==CV_32S&&symmetry.isContinuous()&&int(symmetry.total())==loaded.classes&&loaded.classes>0&&loaded.classes<=32)
                    loaded.symmetry.assign(symmetry.ptr<int>(),symmetry.ptr<int>()+loaded.classes);
                std::string reason;
                if(loaded.classes!=p.inferClasses-1||!loaded.Valid(&reason)){p.Error("load",false,"invalid instance head block"+(reason.empty()?std::string(" (class count differs from the network)"):": "+reason));return false;}
                p.instanceWeights=loaded;p.instanceReady=true;
                if(!storage["instance_templates"].empty()) {
                    InstanceTemplateSet templates;cv::Mat templateData;
                    storage["instance_template_size"]>>templates.size;storage["instance_templates"]>>templateData;
                    templates.data=templateData;
                    std::string templateReason;
                    if(!templates.Valid(loaded.classes,&templateReason)){p.Error("load",false,"invalid instance template block: "+templateReason);return false;}
                    p.instanceTemplates=templates;
                }
            } else if(!(instanceFlag==1&&instanceVersion>kInstanceHeadVersion)){p.Error("load",false,"invalid instance head header");return false;}
        }
        if(valid)p.lastError.clear();return valid;
    }catch(const std::exception& e){p.Error("load",false,e.what());return false;}
}
void SegV2Model::SetPostprocess(float foreground,float ratio,int morphology) {
    if(!std::isfinite(foreground)||foreground<.01f||foreground>1||!std::isfinite(ratio)||ratio<0||ratio>1||(morphology!=0&&morphology!=3&&morphology!=5))throw std::runtime_error("Invalid V2 postprocessing");
    impl_->foregroundThreshold=foreground;impl_->minimumRatio=ratio;impl_->morphology=morphology;
}
void SegV2Model::SetWinnerForeground(bool enabled) {
    if (enabled && impl_->multiScaleDetection) throw std::runtime_error("V2 winner foreground does not support multiscale detection");
    impl_->winnerForegroundBoundary=enabled;
}
void SegV2Model::SetObjectScale(float minimum, float maximum, bool multiScale) {
    if (!std::isfinite(minimum) || !std::isfinite(maximum) || minimum < .5f || maximum > 2.f || minimum > maximum)
        throw std::runtime_error("Invalid object scale range");
    if (multiScale && impl_->winnerForegroundBoundary) throw std::runtime_error("V2 winner foreground does not support multiscale detection");
    impl_->detectionScaleMin = minimum; impl_->detectionScaleMax = maximum; impl_->multiScaleDetection = multiScale;
}
void SegV2Model::SetShapeCleanup(float fraction) {
    if (!std::isfinite(fraction) || fraction < 0.f || fraction > .25f) throw std::runtime_error("Invalid V2 shape cleanup fraction");
    impl_->shapeCleanupFraction = fraction;
}
int ApplyV2ShapeCleanup(cv::Mat& ng, cv::Mat& ok, float fraction) {
    if (ng.empty() || ok.empty() || ng.type() != CV_8UC1 || ok.type() != CV_8UC1 || ng.size() != ok.size() ||
        !std::isfinite(fraction) || fraction < 0.f || fraction > .25f) return -1;
    if (fraction == 0.f) return 0;
    // Components are read from untouched copies; every write goes in place into the caller's Mats.
    const cv::Mat ng0 = ng.clone(), ok0 = ok.clone();
    const cv::Mat occupied = (ng0 > 0) | (ok0 > 0);
    cv::Mat claim = cv::Mat::zeros(ng.size(), CV_32S); // 0 empty, >0 claim key (1-based), -1 contested
    struct Claim { int cls; unsigned char score; bool changed; };
    std::vector<Claim> claims;
    const cv::Mat* sources[2] = { &ng0, &ok0 };
    cv::Mat* targets[2] = { &ng, &ok };
    for (int cls = 0; cls < 2; ++cls) { // fixed order: NG, then OK
        const cv::Mat& source = *sources[cls];
        cv::Mat& target = *targets[cls];
        const cv::Mat binary = source > 0;
        cv::Mat labels, stats, centroids;
        const int count = cv::connectedComponentsWithStats(binary, labels, stats, centroids, 8, CV_32S);
        for (int id = 1; id < count; ++id) {
            const int x = stats.at<int>(id, cv::CC_STAT_LEFT), y = stats.at<int>(id, cv::CC_STAT_TOP);
            const int w = stats.at<int>(id, cv::CC_STAT_WIDTH), h = stats.at<int>(id, cv::CC_STAT_HEIGHT);
            int k = static_cast<int>(std::lround(double(fraction) * std::min(w, h)));
            if (k < 8) continue; // small components stay unchanged
            k |= 1;
            const int pad = k + 2;
            const cv::Rect box(x, y, w, h);
            const cv::Mat member = labels(box) == id;
            cv::Mat crop = cv::Mat::zeros(h + 2 * pad, w + 2 * pad, CV_8UC1);
            member.copyTo(crop(cv::Rect(pad, pad, w, h)));
            const cv::Mat kernel = cv::Mat::ones(k, k, CV_8UC1);
            cv::Mat closed, opened;
            cv::morphologyEx(crop, closed, cv::MORPH_CLOSE, kernel);
            cv::morphologyEx(closed, opened, cv::MORPH_OPEN, kernel);
            const cv::Mat result = opened(cv::Rect(pad, pad, w, h));
            if (cv::countNonZero(result) == 0) continue; // never delete a component
            double peak = 0;
            cv::minMaxLoc(source(box), nullptr, &peak, nullptr, nullptr, member);
            claims.push_back(Claim{ cls, static_cast<unsigned char>(peak), false });
            const int key = static_cast<int>(claims.size());
            for (int row = 0; row < h; ++row) {
                const unsigned char* inside = member.ptr<unsigned char>(row);
                const unsigned char* kept = result.ptr<unsigned char>(row);
                const unsigned char* busy = occupied.ptr<unsigned char>(y + row) + x;
                unsigned char* out = target.ptr<unsigned char>(y + row) + x;
                int* claimed = claim.ptr<int>(y + row) + x;
                for (int col = 0; col < w; ++col) {
                    if (inside[col]) {
                        if (!kept[col]) { out[col] = 0; claims[key - 1].changed = true; }
                    } else if (kept[col] && !busy[col]) {
                        if (claimed[col] == 0) claimed[col] = key;
                        else if (claimed[col] != key) claimed[col] = -1;
                    }
                }
            }
        }
    }
    for (int y = 0; y < ng.rows; ++y) {
        const int* claimed = claim.ptr<int>(y);
        unsigned char* n = ng.ptr<unsigned char>(y);
        unsigned char* o = ok.ptr<unsigned char>(y);
        for (int x = 0; x < ng.cols; ++x) {
            if (claimed[x] <= 0) continue;
            Claim& owner = claims[claimed[x] - 1];
            (owner.cls == 0 ? n : o)[x] = owner.score;
            owner.changed = true;
        }
    }
    int changed = 0;
    for (const Claim& entry : claims) if (entry.changed) ++changed;
    return changed;
}
int SegV2Model::Predict(const cv::Mat& image,bool gpu,cv::Mat& ng,cv::Mat& ok) {
    auto& p=*impl_;p.lastError.clear();p.lastInput=image.size();p.timings=SegV2Timings();
    p.lastLabelMap.release();p.lastScoreMap.release();
    if(!p.network){p.Error("predict",gpu,"Model is not loaded");return -1;}
    try {
        if(image.empty()||image.type()!=CV_8UC3)throw std::runtime_error("Expected nonempty BGR8 image");
        if(p.genericClasses&&!p.compactPostprocess)throw std::runtime_error("postprocess_version 1 supports only the NG/OK class layout, not this model's class table");
        if(p.genericClasses&&p.multiScaleDetection)return p.PredictMultiScaleClasses(image,gpu,ng,ok); // EAI-CLS S2c
        if(p.compactPostprocess&&!p.multiScaleDetection) {
            const auto compactStart=Clock::now();
            const int compactRc=p.PredictCompact(image,gpu,ng,ok,nullptr);
            p.timings.totalMs=ElapsedMs(compactStart,Clock::now());
            return compactRc;
        }
        const std::string device=p.Device(gpu,true);
        cv::Mat a=cv::Mat::zeros(image.size(),CV_32F),b=a.clone();
        auto accumulate = [&](const std::vector<cv::Mat>& classes, const cv::Rect& target) {
            for (int y=0; y<target.height; ++y) for (int x=0; x<target.width; ++x) {
                float bg=classes[0].at<float>(y,x),n=p.ngEnabled?classes[1].at<float>(y,x):0.f,o=p.normalEnabled?classes[2].at<float>(y,x):0.f;
                float& ng = a.at<float>(y+target.y,x+target.x); float& ok = b.at<float>(y+target.y,x+target.x);
                if(n>bg && n>=o) ng=std::max(ng,n); else if(o>bg && o>n) ok=std::max(ok,o);
            }
        };
        std::vector<double> scales{1.};
        if (p.multiScaleDetection) {
            scales = {p.detectionScaleMin, p.detectionScaleMax,
                p.detectionScaleMin <= 1 && p.detectionScaleMax >= 1 ? 1. : std::sqrt(double(p.detectionScaleMin)*p.detectionScaleMax)};
            std::sort(scales.begin(),scales.end());
            scales.erase(std::unique(scales.begin(),scales.end(),[](double x,double y){return std::abs(x-y)<1e-5;}),scales.end());
        }
        for (double scale : scales) {
            if (scale < 1.-1e-5) {
                // A small target is enlarged through overlapping views. Cover the
                // complete Check ROI instead of zooming only at the image centre.
                const int width=std::max(1,int(std::round(image.cols*scale))),height=std::max(1,int(std::round(image.rows*scale)));
                const int nx=std::max(1,int(std::ceil(2.*(image.cols-width)/width))+1),ny=std::max(1,int(std::ceil(2.*(image.rows-height)/height))+1);
                for(int iy=0;iy<ny;++iy) for(int ix=0;ix<nx;++ix) {
                    cv::Rect target(nx==1?0:int(std::round(double(ix)*(image.cols-width)/(nx-1))),ny==1?0:int(std::round(double(iy)*(image.rows-height)/(ny-1))),width,height);
                    cv::Mat view=image(target);
                    // Original-input mode needs an explicit zoom; cropping alone no longer resizes it.
                    if (p.originalInput) cv::resize(image(target),view,image.size());
                    auto frame=p.Prepare(view,cv::Mat::zeros(view.size(),CV_8U));
                    accumulate(p.PredictFrame(frame,target.size(),device),target);
                }
            } else {
                auto frame=p.Prepare(image,cv::Mat::zeros(image.size(),CV_8U));
                if (scale > 1.+1e-5) {
                    // Shrink content within the fixed network input. Resizing the
                    // source then letterboxing again would cancel the object zoom.
                    frame.width=std::max(1,int(std::round(frame.width/scale)));frame.height=std::max(1,int(std::round(frame.height/scale)));
                    frame.left=(frame.image.cols-frame.width)/2;frame.top=(frame.image.rows-frame.height)/2;
                    frame.image.setTo(cv::Scalar(114,114,114));cv::Mat resized;
                    cv::resize(image,resized,cv::Size(frame.width,frame.height));resized.copyTo(frame.image(cv::Rect(frame.left,frame.top,frame.width,frame.height)));
                }
                accumulate(p.PredictFrame(frame,image.size(),device),cv::Rect(0,0,image.cols,image.rows));
            }
        }
        if (p.multiScaleDetection) {
            // Fuse before connected components so the same region is counted once.
            for(int y=0;y<image.rows;++y)for(int x=0;x<image.cols;++x) {
                float& ng=a.at<float>(y,x);float& ok=b.at<float>(y,x);if(ng>=ok)ok=0;else ng=0;
            }
        }
        const cv::Size grid = p.originalInput
            ? cv::Size(std::max(1,(image.cols+7)/8),std::max(1,(image.rows+7)/8))
            : cv::Size(p.Grid,std::max(1,int(std::round(image.rows*double(p.Grid)/image.cols))));
        cv::resize(a,a,grid,0,0,cv::INTER_AREA);cv::resize(b,b,grid,0,0,cv::INTER_AREA);
        cv::Mat foreground,ids,stats,centers;cv::compare(a+b,p.foregroundThreshold,foreground,cv::CMP_GE);
        if(p.morphology) {
            auto kernel=cv::Mat::ones(p.morphology,p.morphology,CV_8U);
            cv::morphologyEx(foreground,foreground,cv::MORPH_CLOSE,kernel,cv::Point(-1,-1),1,cv::BORDER_CONSTANT,cv::Scalar(0));
            cv::morphologyEx(foreground,foreground,cv::MORPH_OPEN,kernel,cv::Point(-1,-1),1,cv::BORDER_CONSTANT,cv::Scalar(0));
        }
        int count=cv::connectedComponentsWithStats(foreground,ids,stats,centers,8);ng=cv::Mat::zeros(image.size(),CV_8U);ok=ng.clone();
        for(int id=1;id<count;++id) {
            cv::Mat selected=ids==id,restored;cv::resize(selected,restored,image.size(),0,0,cv::INTER_NEAREST);
            const double areaScale=p.multiScaleDetection?std::pow(double(p.detectionScaleMin),2):1.;
            if(cv::countNonZero(restored)<std::max(1.,p.minimumFraction*p.minimumRatio*image.total()*areaScale))continue;
            // Evidence for the opposite class must not inflate the chosen class score.
            const double ngScore=cv::mean(a,selected)[0],okScore=cv::mean(b,selected)[0];
            const double score=std::max(ngScore,okScore);
            (ngScore>=okScore?ng:ok).setTo(cv::Scalar(std::max(1.,std::nearbyint(score*255))),restored);
        }
        return 0;
    }catch(const std::exception& e){p.Error("predict",gpu,e.what());ng.release();ok.release();return -8;}
}
int SegV2Model::PredictDiagnostic(const cv::Mat& image, bool gpu, cv::Mat& ng, cv::Mat& ok,
                                  SegV2DiagnosticCapture& capture) {
    auto& p=*impl_;p.lastError.clear();p.lastInput=image.size();p.timings=SegV2Timings();
    if(!p.network){p.Error("diagnostic predict",gpu,"Model is not loaded");return -1;}
    if (p.genericClasses) {
        p.Error("diagnostic predict",gpu,"V158 diagnostics support the NG/OK class layout only, not this model's class table");return -5;
    }
    if (!p.compactPostprocess || p.multiScaleDetection) {
        p.Error("diagnostic predict",gpu,"V158 supports V2 compact single-scale inference only");return -9;
    }
    try {
        if(image.empty()||image.type()!=CV_8UC3)throw std::runtime_error("Expected nonempty BGR8 image");
        const auto compactStart=Clock::now();
        const int compactRc=p.PredictCompact(image,gpu,ng,ok,&capture);
        p.timings.totalMs=ElapsedMs(compactStart,Clock::now());
        return compactRc;
    }catch(const std::exception& e){p.Error("diagnostic predict",gpu,e.what());ng.release();ok.release();return -8;}
    catch(...){p.Error("diagnostic predict",gpu,"unknown native error");ng.release();ok.release();return -8;}
}
}
