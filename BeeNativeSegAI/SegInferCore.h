#pragma once

#include "SegFeatureCore.h"
#include "SegStructureCore.h"
#include "SegV2Core.h"

#include <opencv2/ml.hpp>

#include <cmath>
#include <cstdint>
#include <stdexcept>
#include <string>

namespace BeeSegAI {

class SegInferer {
public:
    bool LoadModel(const std::wstring& path);
    void SetV2Options(float foreground, float ratio, int morphology) { if (v2_) v2_->SetPostprocess(foreground, ratio, morphology); }
    void SetV2BoundaryMode(bool enabled) { if (!v2_) { if (enabled) throw std::runtime_error("V2 boundary mode requires a loaded V2 model"); return; } v2_->SetWinnerForeground(enabled); }
    void SetGpuEnabled(bool enabled) { useGpu_ = enabled; }
    // 0 = Intel OpenVINO (default, legacy meaning of "GPU"), 1 = NVIDIA CUDA via ONNX Runtime. Only matters when GPU is enabled.
    void SetGpuBackend(int backend) { gpuBackend_ = backend == 1 ? 1 : 0; backendNote_.clear(); if (v2_) v2_->SetGpuBackend(gpuBackend_); }
    int GpuBackend() const { return gpuBackend_; }
    // Non-empty when CUDA was selected but the last prediction ran on CPU: why (missing DLL, no NVIDIA GPU, ...).
    const std::string& BackendNote() const { return backendNote_; }
    void SetAssistMode(bool enabled) { assistMode_ = enabled; }
    void SetHighSpeed(bool enabled) { highSpeed_ = enabled; }
    void SetV2SpeedScale(float scale) { v2SpeedScale_ = scale; }
    void SetV2ShapeCleanup(float fraction) {
        if (!std::isfinite(fraction) || fraction < 0.f || fraction > .25f) throw std::invalid_argument("V2 shape cleanup fraction must be finite and within [0, 0.25]");
        v2ShapeCleanup_ = fraction;
        if (v2_) v2_->SetShapeCleanup(fraction);
    }
    void SetAngles(float step, float range) { angleStep_ = step; angleRange_ = range; }
    void SetObjectScale(float minimum, float maximum, bool multiScale) {
        minimumScale_ = minimum; maximumScale_ = maximum;
        if (v2_) v2_->SetObjectScale(minimum, maximum, multiScale);
    }
    const cv::Mat& NormalConfidence() const { return normalConfidence_; }
    bool LastUsedGpu() const { return lastUsedGpu_; }
    bool HasNormalClass() const { return !v2_ || v2_->HasNormalClass(); }
    bool HasNgClass() const { return !v2_ || v2_->HasNgClass(); }
    // EAI-CLS S1: class table of the loaded model (stored v4 table, or the synthesized "NG"/"OK" table of every
    // legacy file). False when no model is loaded.
    bool GetClassTable(SegClassTable& out) const {
        if (v2_) { out = v2_->GetClassTable(); return true; }
        if (!structural_ && !(model_ && !model_->empty())) return false;
        out = MakeLegacyClassTable();
        return true;
    }
    const std::string& LastError() const { return lastError_; }
    bool GpuEnabled() const { return useGpu_; }
    const cv::Mat& Confidence() const { return confidence_; }
    // V135 diagnostic: last-call timings of this engine (ms). Zero when the model is not V2.
    const SegV2Timings* V2Timings() const { return v2_ ? &v2_->LastTimings() : nullptr; }
    const std::vector<float>* V2InstanceTensor(int& channels, int& grid) const { return v2_ ? &v2_->LastInstanceTensor(channels, grid) : nullptr; }
    double LastTailMs() const { return lastTailMs_; }   // accepted-mask arithmetic + confidence/mask copies + score
    double LastTotalMs() const { return lastTotalMs_; } // whole V2 SegInferer::Predict

    int Predict(const cv::Mat& bgr,
                const cv::Rect& roi,
                float threshold,
                cv::Mat& outMask,
                float& outScore);
    // EAI-CLS S3: per-class result of a V2 model. Runs Predict (same internal state, confidence maps and last error) and
    // then derives full-image outputs: label = class id (1..N) of the owning component, score = that component's score,
    // both zero outside the ROI and wherever the component score is not above the threshold cutoff of Predict;
    // fractions[c-1] = accepted pixels of class c / ROI area. Models with the legacy NG/OK table map ng to class 1 and ok
    // to class 2 with NG precedence. Returns -4 when classCapacity < N (nothing is inferred), -9 for a non-V2 model.
    int PredictMulti(const cv::Mat& bgr,
                     const cv::Rect& roi,
                     float threshold,
                     int classCapacity,
                     cv::Mat& outLabel,
                     cv::Mat& outScore,
                     std::vector<float>& outFractions);
    // EAI-POS P3 instance head. HasInstanceHead is false for every legacy / structural / RTrees model. PredictInstances runs the
    // instance branch on bgr(roi) (roi.width/height <= 0 = whole image) and returns the instances in the pixel coordinates of bgr (ROI
    // offset applied). Returns 0, -2 invalid image / threshold / quality / ROI, -9 no V2 model loaded, -11 the model has no instance
    // head, or the V2 Predict error codes (-8 native error with LastError). quality 1 refines each instance (P4); a failed refinement keeps the head result.
    bool HasInstanceHead() const { return v2_ && v2_->HasInstanceHead(); }
    int InstanceClassCount() const { return v2_ ? v2_->InstanceClassCount() : 0; }
    int PredictInstances(const cv::Mat& bgr,
                         const cv::Rect& roi,
                         float threshold,
                         int quality,
                         std::vector<DecodedInstance>& out);
    // Offline-only.  The caller must serialize access to this handle; normal Predict
    // has no capture object and performs no diagnostic copying.
    int PredictDiagnostic(const cv::Mat& bgr,
                          const cv::Rect& roi,
                          float threshold,
                          cv::Mat& outMask,
                          float& outScore,
                          SegV2DiagnosticCapture& capture);

private:
    int PredictCpu(const cv::Mat& bgr,
                   const cv::Rect& roi,
                   float threshold,
                   cv::Mat& outMask,
                   float& outScore);

    int PredictGpu(const cv::Mat& bgr,
                   const cv::Rect& roi,
                   float threshold,
                   cv::Mat& outMask,
                   float& outScore);

    std::unique_ptr<SegV2Model> v2_;
    std::string lastError_;
    FeatureConfig cfg_;
    SegFeatureExtractor extractor_;
    cv::Ptr<cv::ml::RTrees> model_;
    uint32_t minDefectArea_ = 50;
    bool useGpu_ = false;
    int gpuBackend_ = 0;
    std::string backendNote_;
    bool lastUsedGpu_ = false;
    // GPU was requested and actually used: false when CUDA was selected but the V2 model fell back to CPU (visible in the result).
    bool ReportUsedGpu() {
        if (useGpu_ && gpuBackend_ == 1 && v2_ && v2_->LastGpuFellBack()) { backendNote_ = v2_->LastGpuNote(); return false; }
        return useGpu_;
    }
    double lastTailMs_ = 0, lastTotalMs_ = 0;
    cv::Mat confidence_;
    bool structural_ = false;
    bool highSpeed_ = false;
    float v2SpeedScale_ = 1.f; // V2 HighSpeed: Check downscale before inference (1 = off)
    float v2ShapeCleanup_ = 0.f; // V2 compact per-component shape cleanup fraction (0 = off); reapplied on LoadModel
    bool assistMode_ = false;
    float angleStep_ = 0, angleRange_ = 180;
    float minimumScale_ = .6f, maximumScale_ = 1.6f;
    cv::Mat normalConfidence_;
    SegStructureModel structureModel_;
};

} // namespace BeeSegAI
