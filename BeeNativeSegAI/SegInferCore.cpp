#include "pch.h"

#include "SegInferCore.h"

#include "SegAIFileFormat.h"

#include <opencv2/core/ocl.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <chrono>
#include <mutex>
#include <cmath>
#include <cstring>
#include <fstream>
#include <filesystem>

namespace BeeSegAI {
namespace {

std::mutex g_openCvRuntimeMutex;

struct ScopedOpenCvRuntimeLock
{
    std::unique_lock<std::mutex> lock;
    bool restoreOpenCl = false;
    bool oldOpenCl = false;

    void Lock()
    {
        if (!lock.owns_lock()) {
            lock = std::unique_lock<std::mutex>(g_openCvRuntimeMutex);
        }
    }

    void EnableOpenCl()
    {
        Lock();
        oldOpenCl = cv::ocl::useOpenCL();
        cv::ocl::setUseOpenCL(true);
        restoreOpenCl = true;
    }

    ~ScopedOpenCvRuntimeLock()
    {
        if (restoreOpenCl) {
            cv::ocl::setUseOpenCL(oldOpenCl);
        }
    }
};

cv::Rect EffectiveRoi(const cv::Rect& roi, const cv::Size& size)
{
    const cv::Rect imageRect(0, 0, size.width, size.height);
    if (roi.width <= 0 || roi.height <= 0) {
        return imageRect;
    }

    const cv::Rect clipped = roi & imageRect;
    return clipped;
}

bool CanUseGpu(bool enable)
{
    if (!enable) {
        return false;
    }

    try {
        return cv::ocl::haveOpenCL();
    } catch (...) {
        return false;
    }
}

cv::Mat PredictVotes(const cv::Ptr<cv::ml::RTrees>& model, const std::vector<cv::Mat>& planes)
{
    CV_Assert(!planes.empty() && planes.size() == static_cast<size_t>(model->getVarCount()));
    const cv::Size size = planes.front().size();
    for (const auto& plane : planes) {
        CV_Assert(plane.type() == CV_32F && plane.size() == size);
    }
    cv::Mat probability(size, CV_32F);
    // Bound the interleaved feature buffer; never allocate pixels x features twice.
    const int batchSize = 8192;
    const int total = static_cast<int>(probability.total());
    const auto& nodes = model->getNodes();
    const int treeCount = static_cast<int>(model->getRoots().size());
    CV_Assert(model->isClassifier() && treeCount > 0);
    const bool binaryLeaves = std::all_of(nodes.begin(), nodes.end(), [](const cv::ml::DTrees::Node& node) {
        return node.split >= 0 || node.value == 0.0 || node.value == 1.0;
    });
    // OpenCV PREDICT_SUM adds leaf values. For 0/1 classes this is the NG vote
    // count, without getVotes calling predictTrees separately for every tree.
    const int lanes = std::max(1, std::min(4, cv::getNumThreads()));
    cv::parallel_for_(cv::Range(0, lanes), [&](const cv::Range& range) {
        cv::Mat buffer(std::min(batchSize, total), static_cast<int>(planes.size()), CV_32F);
        for (int lane = range.start; lane < range.end; ++lane) {
            for (int start = lane * batchSize; start < total; start += lanes * batchSize) {
                const int count = std::min(batchSize, total - start);
                cv::Mat batch = buffer.rowRange(0, count);
                for (int i = 0; i < count; ++i) {
                    const int pixel = start + i;
                    const int y = pixel / size.width, x = pixel % size.width;
                    float* row = batch.ptr<float>(i);
                    for (size_t feature = 0; feature < planes.size(); ++feature)
                        row[feature] = planes[feature].ptr<float>(y)[x];
                }
                float* output = probability.ptr<float>() + start;
                cv::Mat votes;
                if (binaryLeaves) {
                    model->predict(batch, votes, cv::ml::DTrees::PREDICT_SUM);
                    const float* sums = votes.ptr<float>();
                    for (int i = 0; i < count; ++i) output[i] = sums[i] / treeCount;
                } else {
                    model->getVotes(batch, votes, cv::ml::DTrees::PREDICT_MAX_VOTE);
                    CV_Assert(votes.type() == CV_32S && votes.rows == count + 1);
                    int defectColumn = -1;
                    for (int column = 0; column < votes.cols; ++column)
                        if (votes.at<int>(0, column) == 1) defectColumn = column;
                    CV_Assert(defectColumn >= 0);
                    for (int i = 0; i < count; ++i) {
                        const int* row = votes.ptr<int>(i + 1);
                        int totalVotes = 0;
                        for (int column = 0; column < votes.cols; ++column) totalVotes += row[column];
                        output[i] = totalVotes > 0 ? static_cast<float>(row[defectColumn]) / totalVotes : 0.0f;
                    }
                }
            }
        }
    }, lanes);
    return probability;
}

void FilterConnectedComponents(const cv::Mat& roiMask, uint32_t minDefectArea, cv::Mat& filtered)
{
    cv::Mat labels;
    cv::Mat stats;
    cv::Mat centroids;
    const int componentCount = cv::connectedComponentsWithStats(roiMask, labels, stats, centroids, 8);

    filtered = cv::Mat::zeros(roiMask.size(), CV_8U);
    std::vector<uint8_t> keep(componentCount, 0);
    for (int label = 1; label < componentCount; ++label) {
        keep[label] = stats.at<int>(label, cv::CC_STAT_AREA) >= static_cast<int>(minDefectArea) ? 255 : 0;
    }
    for (int y = 0; y < labels.rows; ++y) {
        const int* source = labels.ptr<int>(y);
        uint8_t* target = filtered.ptr<uint8_t>(y);
        for (int x = 0; x < labels.cols; ++x) {
            target[x] = keep[source[x]];
        }
    }
}

} // namespace

bool SegInferer::LoadModel(const std::wstring& path)
{
    // A failed load must not leave a previous model available for inspection.
    model_.release(); normalConfidence_.release(); confidence_.release(); structural_ = false;
    structureModel_ = SegStructureModel();
    v2_.reset();lastError_.clear();
    std::ifstream headerFile(std::filesystem::path(path), std::ios::binary);
    SegAIHeader header = {}; headerFile.read(reinterpret_cast<char*>(&header), sizeof(header));
    if (headerFile && std::memcmp(header.magic, "SEGAI\0\0", 8) == 0 && header.version > kMaxSupportedSegaiVersion) {
        lastError_ = "Model file format version " + std::to_string(header.version) + " is newer than this runtime supports (up to " +
                     std::to_string(kMaxSupportedSegaiVersion) + "); update the application";
        return false;
    }
    if (headerFile && (header.version == 3 || header.version == 4)) {
        auto candidate = std::make_unique<SegV2Model>();
        if (!candidate->Load(path)) { lastError_=candidate->LastError();return false; }
        if (v2ShapeCleanup_ > 0.f) candidate->SetShapeCleanup(v2ShapeCleanup_);
        candidate->SetGpuBackend(gpuBackend_); backendNote_.clear();
        minDefectArea_ = header.minDefectArea; v2_ = std::move(candidate); return true;
    }
    SegStructureModel structure;
    uint32_t structureArea = 0;
    if (LoadStructuralSegai(path, structure, structureArea)) {
        model_.release(); structureModel_ = std::move(structure); minDefectArea_ = structureArea;
        structural_ = true; confidence_.release(); return true;
    }
    float threshold = 0.0f;
    uint32_t minArea = 0;
    cv::Ptr<cv::ml::RTrees> loaded;
    FeatureConfig loadedCfg;
    if (!LoadSegai(path, loaded, loadedCfg, threshold, minArea)) {
        return false;
    }

    model_ = loaded;
    structural_ = false;
    structureModel_ = SegStructureModel();
    cfg_ = loadedCfg;
    minDefectArea_ = minArea;
    extractor_.Configure(cfg_);
    return true;
}

int SegInferer::Predict(const cv::Mat& bgr,
                        const cv::Rect& roi,
                        float threshold,
                        cv::Mat& outMask,
                        float& outScore)
{
    lastUsedGpu_ = false;lastError_.clear();backendNote_.clear();
    lastTailMs_ = 0; lastTotalMs_ = 0;
    normalConfidence_.release();
    confidence_.release();
    if (!std::isfinite(threshold) || threshold < 0.0f || threshold > 1.0f) return -2;
    if (v2_) {
        if (bgr.empty() || bgr.type() != CV_8UC3) return -2;
        cv::Rect r = EffectiveRoi(roi, bgr.size()); if (r.area() <= 0) return -2;
        const auto timeStart = std::chrono::steady_clock::now();
        cv::Mat ng, ok; int rc;
        const cv::Mat check = bgr(r);
        if (v2SpeedScale_ < 1.f && v2_->OriginalInput()) {
            // V2 HighSpeed: infer on a reduced Check; region maps are restored to Check size.
            cv::Mat reduced;
            cv::resize(check, reduced, cv::Size(std::max(1, int(std::lround(check.cols * v2SpeedScale_))), std::max(1, int(std::lround(check.rows * v2SpeedScale_)))), 0, 0, cv::INTER_AREA);
            rc = v2_->Predict(reduced, useGpu_, ng, ok);
            // ok stays empty only for a generalised class table without a GOOD class.
            if (!rc) { cv::resize(ng, ng, check.size(), 0, 0, cv::INTER_NEAREST); if (!ok.empty()) cv::resize(ok, ok, check.size(), 0, 0, cv::INTER_NEAREST); }
        } else rc = v2_->Predict(check, useGpu_, ng, ok);
        if (rc) { lastError_=v2_->LastError();return rc; }
        const auto timeModel = std::chrono::steady_clock::now();
        // V135: one threshold pass instead of two compares plus an AND (three full-frame temporaries).
        // ng is CV_8U and the cutoff is an integer in [0,255], so (ng > 0) && (ng >= cutoff) is exactly
        // ng > max(0, cutoff - 1); THRESH_BINARY on 8U yields 255/0 like the comparison results did.
        cv::Mat accepted;
        cv::threshold(ng, accepted, std::max(0., std::nearbyint(threshold * 255.) - 1.), 255, cv::THRESH_BINARY);
        if (r == cv::Rect(0, 0, bgr.cols, bgr.rows)) {
            // The returned Mats own their storage; avoid allocating and copying the full Check again.
            confidence_ = ng; normalConfidence_ = ok; outMask = accepted;
        } else {
            confidence_ = cv::Mat::zeros(bgr.size(), CV_8U); ng.copyTo(confidence_(r));
            if (!ok.empty()) { normalConfidence_ = cv::Mat::zeros(bgr.size(), CV_8U); ok.copyTo(normalConfidence_(r)); }
            outMask = cv::Mat::zeros(bgr.size(), CV_8U); accepted.copyTo(outMask(r));
        }
        outScore = float(cv::countNonZero(accepted)) / r.area();
        const auto timeEnd = std::chrono::steady_clock::now();
        lastTailMs_ = std::chrono::duration<double, std::milli>(timeEnd - timeModel).count();
        lastTotalMs_ = std::chrono::duration<double, std::milli>(timeEnd - timeStart).count();
        lastUsedGpu_ = ReportUsedGpu(); return 0;
    }
    if (structural_) {
        if (bgr.empty() || bgr.type() != CV_8UC3) return -2;
        cv::Rect r = EffectiveRoi(roi, bgr.size());
        if (r.area() <= 0) return -2;
        cv::Mat patchConfidence, patchNormal;
        int rc = structureModel_.Predict(bgr(r), threshold, patchConfidence, patchNormal, highSpeed_, useGpu_, &lastUsedGpu_, assistMode_, angleStep_, angleRange_, minimumScale_, maximumScale_);
        // Recover difficult/ambiguous matches without publishing a provisional result.
        if (highSpeed_ && (rc == -6 || rc == -7))
            rc = structureModel_.Predict(bgr(r), threshold, patchConfidence, patchNormal, false, useGpu_, &lastUsedGpu_, assistMode_, angleStep_, angleRange_, minimumScale_, maximumScale_);
        if (rc != 0) return rc;
        confidence_ = cv::Mat::zeros(bgr.size(), CV_8U); patchConfidence.copyTo(confidence_(r));
        if (cv::countNonZero(patchNormal) > 0) {
            normalConfidence_ = cv::Mat::zeros(bgr.size(), CV_8U); patchNormal.copyTo(normalConfidence_(r));
        }
        cv::Mat raw, filtered;
        cv::threshold(patchConfidence, raw, std::max(0., std::nearbyint(threshold * 255.0) - 1.), 255, cv::THRESH_BINARY);
        FilterConnectedComponents(raw, minDefectArea_, filtered);
        outMask = cv::Mat::zeros(bgr.size(), CV_8U); filtered.copyTo(outMask(r));
        outScore = float(cv::countNonZero(filtered)) / float(r.area());
        return 0;
    }
    if (CanUseGpu(useGpu_)) {
        try {
            const int rc = PredictGpu(bgr, roi, threshold, outMask, outScore);
            if (rc == 0) { lastUsedGpu_ = cv::ocl::useOpenCL(); return 0; }
        } catch (const cv::Exception&) {
            normalConfidence_.release();
    confidence_.release();
        }
    }

    return PredictCpu(bgr, roi, threshold, outMask, outScore);
}

int SegInferer::PredictMulti(const cv::Mat& bgr,
                             const cv::Rect& roi,
                             float threshold,
                             int classCapacity,
                             cv::Mat& outLabel,
                             cv::Mat& outScore,
                             std::vector<float>& outFractions)
{
    outLabel.release(); outScore.release(); outFractions.clear();
    if (!v2_) { lastError_ = "PredictMulti requires a loaded V2 model"; return -9; }
    const int classCount = static_cast<int>(v2_->GetClassTable().classes.size());
    if (classCapacity < classCount) {
        lastError_ = "PredictMulti: class capacity " + std::to_string(classCapacity) + " is smaller than the model's " + std::to_string(classCount) + " classes";
        return -4;
    }
    cv::Mat mask; float score = 0.f;
    const int rc = Predict(bgr, roi, threshold, mask, score); // same internal state as the legacy export
    if (rc) return rc;
    const cv::Rect r = EffectiveRoi(roi, bgr.size());
    // The cutoff Predict applies to the DEFECT score map, applied here to every class.
    const double cutoff = std::max(0., std::nearbyint(threshold * 255.) - 1.);
    cv::Mat label = cv::Mat::zeros(r.size(), CV_8U), scoreMap = cv::Mat::zeros(r.size(), CV_8U), keep;
    cv::Mat classLabel, classScore;
    if (v2_->GetLastClassMaps(classLabel, classScore)) {
        if (classLabel.size() != r.size()) {
            cv::resize(classLabel, classLabel, r.size(), 0, 0, cv::INTER_NEAREST);
            cv::resize(classScore, classScore, r.size(), 0, 0, cv::INTER_NEAREST);
        }
        cv::threshold(classScore, keep, cutoff, 255, cv::THRESH_BINARY);
        classLabel.copyTo(label, keep);
        classScore.copyTo(scoreMap, keep);
    } else {
        // Legacy NG/OK layout: ng = class 1, ok = class 2, NG wins where both are accepted.
        if (!normalConfidence_.empty()) {
            const cv::Mat ok = normalConfidence_(r);
            cv::threshold(ok, keep, cutoff, 255, cv::THRESH_BINARY);
            label.setTo(2, keep);
            ok.copyTo(scoreMap, keep);
        }
        const cv::Mat ng = confidence_(r);
        cv::threshold(ng, keep, cutoff, 255, cv::THRESH_BINARY);
        label.setTo(1, keep);
        ng.copyTo(scoreMap, keep);
    }
    std::vector<double> counts(256, 0.);
    for (int y = 0; y < label.rows; ++y) {
        const unsigned char* row = label.ptr<unsigned char>(y);
        for (int x = 0; x < label.cols; ++x) counts[row[x]] += 1.;
    }
    outFractions.assign(static_cast<size_t>(classCount), 0.f);
    for (int c = 1; c <= classCount && c < 256; ++c) outFractions[c - 1] = static_cast<float>(counts[c] / static_cast<double>(r.area()));
    outLabel = cv::Mat::zeros(bgr.size(), CV_8U); label.copyTo(outLabel(r));
    outScore = cv::Mat::zeros(bgr.size(), CV_8U); scoreMap.copyTo(outScore(r));
    return 0;
}

int SegInferer::PredictInstances(const cv::Mat& bgr,
                                 const cv::Rect& roi,
                                 float threshold,
                                 int quality,
                                 std::vector<DecodedInstance>& out)
{
    const auto instanceBegin = std::chrono::steady_clock::now();
    lastTailMs_ = 0; lastTotalMs_ = 0;
    out.clear();
    lastError_.clear();
    backendNote_.clear(); lastUsedGpu_ = false; // like Predict/PredictDiagnostic: no stale note from an earlier call
    if (!v2_) { lastError_ = "PredictInstances requires a loaded V2 model"; return -9; }
    if (!v2_->HasInstanceHead()) { lastError_ = "PredictInstances: the loaded model has no instance head"; return -11; }
    if (!std::isfinite(threshold) || threshold < 0.0f || threshold > 1.0f || (quality != 0 && quality != 1)) { lastError_ = "PredictInstances: invalid threshold or quality"; return -2; }
    if (bgr.empty() || bgr.type() != CV_8UC3) { lastError_ = "PredictInstances: invalid BGR8 image"; return -2; }
    const cv::Rect r = EffectiveRoi(roi, bgr.size());
    if (r.area() <= 0) { lastError_ = "PredictInstances: the ROI has no valid pixels"; return -2; }
    const int rc = v2_->PredictInstances(bgr(r), useGpu_, threshold, quality, out);
    lastUsedGpu_ = ReportUsedGpu(); // CUDA now also serves the instance head; a CUDA failure reports CPU + BackendNote like Predict
    if (rc) { lastError_ = v2_->LastError(); out.clear(); return rc; }
    const auto instanceTailBegin = std::chrono::steady_clock::now();
    for (DecodedInstance& item : out) { item.cx += r.x; item.cy += r.y; }
    const auto instanceEnd = std::chrono::steady_clock::now();
    lastTailMs_ = std::chrono::duration<double, std::milli>(instanceEnd - instanceTailBegin).count();
    lastTotalMs_ = std::chrono::duration<double, std::milli>(instanceEnd - instanceBegin).count();
    return 0;
}

int SegInferer::PredictDiagnostic(const cv::Mat& bgr,
                                  const cv::Rect& roi,
                                  float threshold,
                                  cv::Mat& outMask,
                                  float& outScore,
                                  SegV2DiagnosticCapture& capture)
{
    lastUsedGpu_ = false;lastError_.clear();backendNote_.clear();lastTailMs_ = 0;lastTotalMs_ = 0;
    normalConfidence_.release();confidence_.release();outMask.release();outScore = 0.0f;
    if (!v2_) { lastError_ = "V158 diagnostic supports loaded V2 models only"; return -9; }
    if (!std::isfinite(threshold) || threshold < 0.0f || threshold > 1.0f || bgr.empty() || bgr.type() != CV_8UC3)
        return -2;
    const cv::Rect r = EffectiveRoi(roi, bgr.size());
    if (r.area() <= 0) return -2;
    try {
        capture.callerImageSize = bgr.size();capture.requestedRoi = roi;capture.effectiveRoi = r;
        capture.infererThreshold = threshold;capture.requestedBackend = useGpu_ ? (gpuBackend_ == 1 ? "CUDA" : "GPU") : "CPU";
        capture.requestedSpeedScale = v2SpeedScale_;
        const auto timeStart = std::chrono::steady_clock::now();
        cv::Mat ng, ok;int rc;
        const cv::Mat check = bgr(r);
        if (v2SpeedScale_ < 1.f && v2_->OriginalInput()) {
            cv::Mat reduced;
            cv::resize(check, reduced, cv::Size(std::max(1, int(std::lround(check.cols * v2SpeedScale_))), std::max(1, int(std::lround(check.rows * v2SpeedScale_)))), 0, 0, cv::INTER_AREA);
            capture.speedDownscaled = true;
            capture.speedReducedSize = reduced.size();
            capture.effectiveSpeedScaleX = float(reduced.cols) / float(check.cols);
            capture.effectiveSpeedScaleY = float(reduced.rows) / float(check.rows);
            rc = v2_->PredictDiagnostic(reduced, useGpu_, ng, ok, capture);
            if (!rc) { cv::resize(ng, ng, check.size(), 0, 0, cv::INTER_NEAREST); cv::resize(ok, ok, check.size(), 0, 0, cv::INTER_NEAREST); }
        } else {
            capture.speedDownscaled = false;
            capture.speedReducedSize = check.size();capture.effectiveSpeedScaleX = 1.f;capture.effectiveSpeedScaleY = 1.f;
            rc = v2_->PredictDiagnostic(check, useGpu_, ng, ok, capture);
        }
        if (rc) { lastError_ = v2_->LastError();return rc; }
        capture.checkSize = check.size();
        capture.Capture(ng, capture.infererNg);capture.Capture(ok, capture.infererOk);
        const auto timeModel = std::chrono::steady_clock::now();
        cv::Mat accepted;
        cv::threshold(ng, accepted, std::max(0., std::nearbyint(threshold * 255.) - 1.), 255, cv::THRESH_BINARY);
        capture.Capture(accepted, capture.acceptedNg);
        if (r == cv::Rect(0, 0, bgr.cols, bgr.rows)) {
            confidence_ = ng;normalConfidence_ = ok;outMask = accepted;
        } else {
            confidence_ = cv::Mat::zeros(bgr.size(), CV_8U);ng.copyTo(confidence_(r));
            normalConfidence_ = cv::Mat::zeros(bgr.size(), CV_8U);ok.copyTo(normalConfidence_(r));
            outMask = cv::Mat::zeros(bgr.size(), CV_8U);accepted.copyTo(outMask(r));
        }
        outScore = float(cv::countNonZero(accepted)) / r.area();
        const auto timeEnd = std::chrono::steady_clock::now();
        lastTailMs_ = std::chrono::duration<double, std::milli>(timeEnd - timeModel).count();
        lastTotalMs_ = std::chrono::duration<double, std::milli>(timeEnd - timeStart).count();
        lastUsedGpu_ = ReportUsedGpu();return 0;
    } catch (const std::exception& e) { lastError_ = std::string("V158 diagnostic: ") + e.what();return -8; }
    catch (...) { lastError_ = "V158 diagnostic: unknown native error";return -8; }
}

int SegInferer::PredictCpu(const cv::Mat& bgr,
                           const cv::Rect& roi,
                           float threshold,
                           cv::Mat& outMask,
                           float& outScore)
{
    outScore = 0.0f;
    if (!model_ || model_->empty()) {
        return -1;
    }
    if (bgr.empty() || bgr.type() != CV_8UC3) {
        return -2;
    }

    const cv::Rect r = EffectiveRoi(roi, bgr.size());
    if (r.area() <= 0) {
        return -2;
    }

    cv::Mat roiView = bgr(r);

    FeatureConfig predictCfg = cfg_;
    predictCfg.roi = cv::Rect(0, 0, roiView.cols, roiView.rows);
    extractor_.Configure(predictCfg);

    std::vector<cv::Mat> planes;
    extractor_.ExtractCPU(roiView, planes);

    cv::Mat roiPred = PredictVotes(model_, planes);
    confidence_ = cv::Mat::zeros(bgr.size(), CV_8U);
    roiPred.convertTo(confidence_(r), CV_8U, 255.0);
    cv::Mat roiMask;
    cv::threshold(confidence_(r), roiMask, std::max(0., std::nearbyint(threshold * 255.0) - 1.), 255, cv::THRESH_BINARY);
    roiMask.convertTo(roiMask, CV_8U);

    cv::Mat filtered;
    FilterConnectedComponents(roiMask, minDefectArea_, filtered);

    outMask = cv::Mat::zeros(bgr.size(), CV_8U);
    filtered.copyTo(outMask(r));
    outScore = static_cast<float>(cv::countNonZero(filtered)) / static_cast<float>(roiView.total());
    return 0;
}

int SegInferer::PredictGpu(const cv::Mat& bgr,
                           const cv::Rect& roi,
                           float threshold,
                           cv::Mat& outMask,
                           float& outScore)
{
    outScore = 0.0f;
    if (!model_ || model_->empty()) {
        return -1;
    }
    if (bgr.empty() || bgr.type() != CV_8UC3) {
        return -2;
    }

    const cv::Rect r = EffectiveRoi(roi, bgr.size());
    if (r.area() <= 0) {
        return -2;
    }

    ScopedOpenCvRuntimeLock runtimeLock;
    runtimeLock.EnableOpenCl();

    cv::Mat roiView = bgr(r);

    FeatureConfig predictCfg = cfg_;
    predictCfg.roi = cv::Rect(0, 0, roiView.cols, roiView.rows);
    extractor_.Configure(predictCfg);

    cv::UMat srcGpu;
    roiView.copyTo(srcGpu);

    std::vector<cv::UMat> planesGpu;
    extractor_.ExtractGpu(srcGpu, planesGpu);

    std::vector<cv::Mat> planes;
    planes.reserve(planesGpu.size());
    for (size_t i = 0; i < planesGpu.size(); ++i) {
        planes.push_back(planesGpu[i].getMat(cv::ACCESS_READ));
    }
    cv::Mat roiPred = PredictVotes(model_, planes);
    confidence_ = cv::Mat::zeros(bgr.size(), CV_8U);
    roiPred.convertTo(confidence_(r), CV_8U, 255.0);
    cv::Mat roiMask;
    cv::threshold(confidence_(r), roiMask, std::max(0., std::nearbyint(threshold * 255.0) - 1.), 255, cv::THRESH_BINARY);
    roiMask.convertTo(roiMask, CV_8U);

    cv::Mat filtered;
    FilterConnectedComponents(roiMask, minDefectArea_, filtered);

    outMask = cv::Mat::zeros(bgr.size(), CV_8U);
    filtered.copyTo(outMask(r));
    outScore = static_cast<float>(cv::countNonZero(filtered)) / static_cast<float>(roiView.total());
    return 0;
}

} // namespace BeeSegAI
