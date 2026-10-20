#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "../SegAINativeExport.h"
#include "../SegClassTable.h"
#include "../SegInstanceTargets.h"
#include "../SegInstanceRefine.h"
#include "SegClassModelSynth.h"

#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <iterator>
#include <cerrno>
#include <climits>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iomanip>
#include <limits>
#include <memory>
#include <random>
#include <sstream>
#include <string>
#include <vector>

namespace {

struct InferHandleDeleter {
    void operator()(void* handle) const { if (handle) SEGAI_InferDestroy(handle); }
};
struct SegaiBufferDeleter {
    void operator()(uint8_t* buffer) const { if (buffer) SEGAI_FreeBuffer(buffer); }
};

bool ParseFloatArgument(const char* text, float& value)
{
    if (!text || !*text) return false;
    char* end = nullptr;errno = 0;const float parsed = std::strtof(text, &end);
    if (errno != 0 || end == text || *end != 0 || !std::isfinite(parsed)) return false;
    value = parsed;return true;
}

bool ParseIntArgument(const char* text, int& value)
{
    if (!text || !*text) return false;
    char* end = nullptr;errno = 0;const long parsed = std::strtol(text, &end, 10);
    if (errno != 0 || end == text || *end != 0 || parsed < INT_MIN || parsed > INT_MAX) return false;
    value = static_cast<int>(parsed);return true;
}

bool IsBoundedText(const char* text, size_t maximumBytes)
{
    if (!text || !*text) return false;
    size_t length = 0;
    while (length <= maximumBytes && text[length] != 0) ++length;
    return length <= maximumBytes;
}

bool BuildHostObservedFacts(const std::filesystem::path& modelPath,
                            const std::filesystem::path& inputPath,
                            const cv::Mat& image,
                            std::string& facts)
{
    std::error_code error;
    const auto modelBytes = std::filesystem::file_size(modelPath, error);
    if (error) return false;
    const auto modelMtime = std::filesystem::last_write_time(modelPath, error);
    if (error) return false;
    const auto inputBytes = std::filesystem::file_size(inputPath, error);
    if (error) return false;
    const auto inputMtime = std::filesystem::last_write_time(inputPath, error);
    if (error) return false;
    std::ostringstream value;
    value << "model_path=" << modelPath.u8string() << ";model_bytes=" << modelBytes
          << ";model_last_write_filesystem_ticks=" << modelMtime.time_since_epoch().count()
          << ";input_path=" << inputPath.u8string() << ";input_bytes=" << inputBytes
          << ";input_last_write_filesystem_ticks=" << inputMtime.time_since_epoch().count()
          << ";input_dimensions=" << image.cols << "x" << image.rows;
    facts = value.str();return true;
}

int RunV158DiagnosticMode(int argc, char** argv)
{
    // Dedicated offline handle. This mode never trains, changes a model, or overwrites an artifact.
    if (argc != 21 && argc != 22) {
        std::cerr << "Usage: --diagnose-v158 <model.segai> <input.png> <output.json> <threshold> <foreground> <minimumRatio> <morphology:0|3|5> <speedScale> <backend:cpu|gpu> <detectionScaleMin> <detectionScaleMax> <multiscale:0|1> <modelIdentity-unverified> <inputIdentity-unverified> <scoreGatePercent> <managedMinAreaPixels> <bestObject:0|1> <iouMatchThreshold> <labelExclusionNote> [winnerForegroundBoundaryMode:0|1]\n";
        return 40;
    }
    const std::filesystem::path modelPath = std::filesystem::u8path(argv[2]);
    const std::filesystem::path inputPath = std::filesystem::u8path(argv[3]);
    const std::filesystem::path outputPath = std::filesystem::u8path(argv[4]);
    float threshold = 0.0f, foreground = 0.0f, minimumRatio = 0.0f, speedScale = 0.0f;
    float detectionScaleMin = 0.0f, detectionScaleMax = 0.0f, scoreGatePercent = 0.0f, iouMatchThreshold = 0.0f;
    int morphology = 0, multiscale = 0, managedMinAreaPixels = 0, bestObject = 0, winnerForegroundBoundaryMode = 0;
    const bool gpu = std::strcmp(argv[10], "gpu") == 0;
    if (!ParseFloatArgument(argv[5], threshold) || !ParseFloatArgument(argv[6], foreground) ||
        !ParseFloatArgument(argv[7], minimumRatio) || !ParseIntArgument(argv[8], morphology) ||
        !ParseFloatArgument(argv[9], speedScale) || !ParseFloatArgument(argv[11], detectionScaleMin) ||
        !ParseFloatArgument(argv[12], detectionScaleMax) || !ParseIntArgument(argv[13], multiscale) ||
        !ParseFloatArgument(argv[16], scoreGatePercent) || !ParseIntArgument(argv[17], managedMinAreaPixels) ||
        !ParseIntArgument(argv[18], bestObject) || !ParseFloatArgument(argv[19], iouMatchThreshold) ||
        (argc == 22 && !ParseIntArgument(argv[21], winnerForegroundBoundaryMode)) ||
        (std::strcmp(argv[10], "cpu") != 0 && !gpu) || detectionScaleMin < .5f || detectionScaleMax > 2.f ||
        detectionScaleMin > detectionScaleMax || (multiscale != 0 && multiscale != 1) ||
        (winnerForegroundBoundaryMode != 0 && multiscale != 0) ||
        scoreGatePercent < 0.f || scoreGatePercent > 100.f || managedMinAreaPixels < 0 ||
        (bestObject != 0 && bestObject != 1) || (winnerForegroundBoundaryMode != 0 && winnerForegroundBoundaryMode != 1) ||
        iouMatchThreshold < 0.f || iouMatchThreshold > 1.f) {
        std::cerr << "Malformed V158 diagnostic settings\n";return 41;
    }
    for (int index = 14; index < 21; ++index) {
        if (!IsBoundedText(argv[index], index == 20 ? 1024u : 4096u)) { std::cerr << "Missing or oversized V158 diagnostic provenance value\n";return 41; }
    }
    if (!std::filesystem::is_regular_file(modelPath) || !std::filesystem::is_regular_file(inputPath) ||
        std::filesystem::exists(outputPath)) {
        std::cerr << "V158 diagnostic requires existing model/input and a new output path\n";return 42;
    }
    cv::Mat image = cv::imread(inputPath.u8string(), cv::IMREAD_COLOR);
    if (image.empty()) { std::cerr << "V158 diagnostic could not read input image\n";return 43; }
    std::ostringstream caller;
    caller << "model_identity_unverified=" << argv[14] << ";input_identity_unverified=" << argv[15]
           << ";score_gate_percent_caller_supplied_unverified=" << scoreGatePercent
           << ";managed_minimum_area_pixels_caller_supplied_unverified=" << managedMinAreaPixels
           << ";best_object_caller_supplied_unverified=" << bestObject
           << ";iou_match_threshold_caller_supplied_unverified=" << iouMatchThreshold
           << ";label_exclusion_note_caller_supplied_unverified=" << argv[20];
    const std::string callerProvenance = caller.str();
    std::string hostFacts;
    if (!BuildHostObservedFacts(modelPath, inputPath, image, hostFacts)) {
        std::cerr << "V158 diagnostic could not collect host-observed file facts\n";return 43;
    }
    std::unique_ptr<void, InferHandleDeleter> inferer(SEGAI_InferCreate());
    if (!inferer) { std::cerr << "V158 diagnostic could not create inferer\n";return 44; }
    if (SEGAI_InferLoad(inferer.get(), modelPath.wstring().c_str()) != 0 ||
        SEGAI_InferV2Options(inferer.get(), foreground, minimumRatio, morphology) != 0 ||
        SEGAI_InferSetV2SpeedScale(inferer.get(), speedScale) != 0 ||
        SEGAI_InferSetObjectScale(inferer.get(), detectionScaleMin, detectionScaleMax, multiscale) != 0 ||
        SEGAI_InferSetV2BoundaryMode(inferer.get(), winnerForegroundBoundaryMode) != 0 ||
        SEGAI_InferSetGpu(inferer.get(), gpu ? 1 : 0) != 0) {
        std::cerr << "V158 diagnostic setup failed\n";return 45;
    }
    uint8_t* rawPacket = nullptr;uint64_t packetBytes = 0;
    const int rc = SEGAI_InferPredictDiagnosticV1(inferer.get(), image.data, image.cols, image.rows,
        static_cast<int32_t>(image.step), 0, 0, image.cols, image.rows, threshold,
        callerProvenance.c_str(), hostFacts.c_str(), &rawPacket, &packetBytes);
    std::unique_ptr<uint8_t, SegaiBufferDeleter> packet(rawPacket);
    if (rc != 0 || !packet || packetBytes == 0 || packetBytes > static_cast<uint64_t>(std::numeric_limits<std::streamsize>::max())) { std::cerr << "V158 diagnostic capture failed: " << rc << "\n";return 46; }
    std::ofstream output(outputPath, std::ios::binary | std::ios::trunc);
    if (!output) { std::cerr << "V158 diagnostic could not create output\n";return 47; }
    output.write(reinterpret_cast<const char*>(packet.get()), static_cast<std::streamsize>(packetBytes));
    if (!output.good()) { std::cerr << "V158 diagnostic could not write output\n";return 48; }
    std::cout << "native_only_diagnostic=" << outputPath.u8string()
              << " timings=not-performance-evidence bytes=" << packetBytes << "\n";
    return 0;
}

cv::Rect BenchmarkRoi()
{
    return cv::Rect(384, 224, 512, 512);
}

void MakeSynthetic(cv::Mat& image, cv::Mat& mask)
{
    image = cv::Mat(960, 1280, CV_8UC3, cv::Scalar(240, 240, 240));
    mask = cv::Mat(960, 1280, CV_8UC1, cv::Scalar(2));

    for (int y = 0; y < image.rows; ++y) {
        for (int x = 0; x < image.cols; ++x) {
            if (((x / 32) + (y / 32)) % 2 == 0) {
                image.at<cv::Vec3b>(y, x) = cv::Vec3b(225, 225, 225);
            }
        }
    }

    const cv::Rect defect(512, 352, 256, 256);
    cv::rectangle(image, defect, cv::Scalar(20, 20, 20), cv::FILLED);
    cv::rectangle(mask, defect, cv::Scalar(1), cv::FILLED);
}

bool LoadOrSynthetic(int argc, char** argv, cv::Mat& image, cv::Mat& mask)
{
    if (argc >= 3) {
        image = cv::imread(argv[1], cv::IMREAD_COLOR);
        mask = cv::imread(argv[2], cv::IMREAD_GRAYSCALE);
        if (!image.empty() && !mask.empty() && image.size() == mask.size()) {
            return true;
        }
    }

    MakeSynthetic(image, mask);
    std::filesystem::create_directories("BeeNativeSegAI/test/data");
    cv::imwrite("BeeNativeSegAI/test/data/sample_defect_01.png", image);
    cv::imwrite("BeeNativeSegAI/test/data/sample_mask_01.png", mask);
    return true;
}

cv::Mat SolidMask(const cv::Mat& image, unsigned char label)
{
    return cv::Mat(image.size(), CV_8UC1, cv::Scalar(label));
}

cv::Rect ClampRect(const cv::Rect& roi, const cv::Size& size)
{
    return roi & cv::Rect(0, 0, size.width, size.height);
}

std::vector<cv::Rect> DefaultWeldRois(const cv::Mat& image)
{
    const int y = static_cast<int>(image.rows * 0.18);
    const int h = static_cast<int>(image.rows * 0.28);
    const int w = static_cast<int>(image.cols * 0.30);
    const int leftX = static_cast<int>(image.cols * 0.08);
    const int rightX = static_cast<int>(image.cols * 0.43);

    return {
        ClampRect(cv::Rect(leftX, y, w, h), image.size()),
        ClampRect(cv::Rect(rightX, y, w, h), image.size())
    };
}

cv::Rect FindTemplateRoi(const cv::Mat& image, const cv::Mat& templ)
{
    if (image.empty() || templ.empty() || templ.cols > image.cols || templ.rows > image.rows) {
        return {};
    }

    cv::Mat result;
    cv::matchTemplate(image, templ, result, cv::TM_CCOEFF_NORMED);

    double maxValue = 0.0;
    cv::Point maxLoc;
    cv::minMaxLoc(result, nullptr, &maxValue, nullptr, &maxLoc);
    if (maxValue < 0.55) {
        return {};
    }

    const int padX = std::max(12, templ.cols / 20);
    const int padY = std::max(12, templ.rows / 20);
    return ClampRect(
        cv::Rect(maxLoc.x - padX, maxLoc.y - padY, templ.cols + padX * 2, templ.rows + padY * 2),
        image.size());
}

std::vector<cv::Rect> TemplateWeldRois(const cv::Mat& image, const cv::Mat& ng, const cv::Mat& ok)
{
    cv::Rect left = FindTemplateRoi(image, ng);
    cv::Rect right = FindTemplateRoi(image, ok);
    if (left.area() <= 0 || right.area() <= 0) {
        return DefaultWeldRois(image);
    }
    return {left, right};
}

void WriteOverlay(const cv::Mat& image, const cv::Mat& mask, const std::string& path)
{
    cv::Mat overlay = image.clone();
    cv::Mat red(image.size(), image.type(), cv::Scalar(0, 0, 255));
    red.copyTo(overlay, mask > 0);
    cv::addWeighted(overlay, 0.35, image, 0.65, 0.0, overlay);
    cv::imwrite(path, overlay);
}

int RunCropSampleMode(const char* ngPath, const char* okPath, const char* testPath)
{
    cv::Mat ng = cv::imread(ngPath, cv::IMREAD_COLOR);
    cv::Mat ok = cv::imread(okPath, cv::IMREAD_COLOR);
    cv::Mat test = cv::imread(testPath, cv::IMREAD_COLOR);
    if (ng.empty() || ok.empty() || test.empty()) {
        std::cerr << "Failed to load NG/OK/test image inputs\n";
        return 10;
    }

    const std::wstring modelPath = L"BeeNativeSegAI/test/data/out_weld_model.segai";
    void* trainer = SEGAI_TrainerCreate();
    if (!trainer) {
        std::cerr << "SEGAI_TrainerCreate failed\n";
        return 11;
    }

    cv::Mat ngMask = SolidMask(ng, 1);
    cv::Mat okMask = SolidMask(ok, 2);

    int rc = SEGAI_TrainerSetROI(trainer, 0, 0, ng.cols, ng.rows);
    if (rc == 0) {
        rc = SEGAI_TrainerAddSample(trainer, ng.data, ng.cols, ng.rows, static_cast<int>(ng.step), ngMask.data, static_cast<int>(ngMask.step));
    }
    if (rc == 0) {
        rc = SEGAI_TrainerSetROI(trainer, 0, 0, ok.cols, ok.rows);
    }
    if (rc == 0) {
        rc = SEGAI_TrainerAddSample(trainer, ok.data, ok.cols, ok.rows, static_cast<int>(ok.step), okMask.data, static_cast<int>(okMask.step));
    }

    int defectCount = 0;
    int normalCount = 0;
    if (rc == 0) {
        rc = SEGAI_TrainerSampleCount(trainer, &defectCount, &normalCount);
    }
    if (rc == 0) {
        rc = SEGAI_TrainerTrain(trainer, 40, 8, 5, nullptr, nullptr, nullptr);
    }
    if (rc == 0) {
        rc = SEGAI_TrainerSave(trainer, modelPath.c_str(), 0.5f, 40);
    }
    SEGAI_TrainerDestroy(trainer);
    if (rc != 0) {
        std::cerr << "Crop trainer pipeline failed: " << rc << "\n";
        return 12;
    }

    void* inferer = SEGAI_InferCreate();
    if (!inferer) {
        std::cerr << "SEGAI_InferCreate failed\n";
        return 13;
    }
    rc = SEGAI_InferLoad(inferer, modelPath.c_str());
    if (rc != 0) {
        SEGAI_InferDestroy(inferer);
        std::cerr << "Infer load failed: " << rc << "\n";
        return 14;
    }

    cv::Mat combinedMask = cv::Mat::zeros(test.size(), CV_8UC1);
    std::vector<cv::Rect> rois = TemplateWeldRois(test, ng, ok);
    std::vector<float> scores;

    for (size_t i = 0; i < rois.size(); ++i) {
        uint8_t* outMask = nullptr;
        int outW = 0;
        int outH = 0;
        float score = 0.0f;
        const cv::Rect roi = rois[i];
        rc = SEGAI_InferPredict(
            inferer,
            test.data,
            test.cols,
            test.rows,
            static_cast<int>(test.step),
            roi.x,
            roi.y,
            roi.width,
            roi.height,
            0.5f,
            &outMask,
            &outW,
            &outH,
            &score);
        if (rc != 0 || !outMask) {
            SEGAI_InferDestroy(inferer);
            std::cerr << "Infer predict failed for ROI " << i << ": " << rc << "\n";
            return 15;
        }

        cv::Mat pred(outH, outW, CV_8UC1, outMask);
        cv::bitwise_or(combinedMask, pred, combinedMask);
        SEGAI_FreeBuffer(outMask);
        scores.push_back(score);
    }

    SEGAI_InferDestroy(inferer);

    cv::imwrite("BeeNativeSegAI/test/data/out_weld_pred.png", combinedMask);
    WriteOverlay(test, combinedMask, "BeeNativeSegAI/test/data/out_weld_overlay.png");

    std::cout << "trained_defect_pixels=" << defectCount
              << " trained_normal_pixels=" << normalCount
              << " left_roi=" << rois[0].x << "," << rois[0].y << "," << rois[0].width << "," << rois[0].height
              << " right_roi=" << rois[1].x << "," << rois[1].y << "," << rois[1].width << "," << rois[1].height
              << " left_score=" << scores[0]
              << " right_score=" << scores[1]
              << " ratio_left_over_right=" << (scores[1] > 0.0f ? scores[0] / scores[1] : 999.0f)
              << " pred=BeeNativeSegAI/test/data/out_weld_pred.png"
              << " overlay=BeeNativeSegAI/test/data/out_weld_overlay.png"
              << "\n";

    return scores[0] > scores[1] ? 0 : 16;
}

double ComputeIou(const cv::Mat& pred, const cv::Mat& label)
{
    cv::Mat predBin = pred > 0;
    cv::Mat labelBin = label == 1;
    cv::Mat intersection;
    cv::Mat unionMask;
    cv::bitwise_and(predBin, labelBin, intersection);
    cv::bitwise_or(predBin, labelBin, unionMask);
    const int unionCount = cv::countNonZero(unionMask);
    if (unionCount == 0) {
        return 0.0;
    }
    return static_cast<double>(cv::countNonZero(intersection)) / static_cast<double>(unionCount);
}

double ComputeAgreement(const cv::Mat& left, const cv::Mat& right)
{
    if (left.empty() || right.empty() || left.size() != right.size()) {
        return 0.0;
    }

    cv::Mat diff;
    cv::compare(left, right, diff, cv::CMP_NE);
    const double total = static_cast<double>(left.total());
    if (total <= 0.0) {
        return 1.0;
    }

    return 1.0 - (static_cast<double>(cv::countNonZero(diff)) / total);
}

// ---- SEG-LIFECYCLE-r8: V2 shape cleanup synthetic self-test ------------------------------------------------
// Offline only. Both exports are resolved from the already loaded BeeNativeSegAI.dll with GetProcAddress
// (the checked-in import library is stale and must not be linked for them).
typedef int32_t (*ShapeCleanupApplyFn)(uint8_t*, uint8_t*, int32_t, int32_t, int32_t, float);
typedef int32_t (*ShapeCleanupSetFn)(void*, float);

std::string JsonEscape(const std::string& text)
{
    std::string escaped;
    for (const char ch : text) {
        if (ch == '"' || ch == '\\') { escaped.push_back('\\'); escaped.push_back(ch); }
        else if (static_cast<unsigned char>(ch) < 0x20) escaped.push_back(' ');
        else escaped.push_back(ch);
    }
    return escaped;
}

bool IdenticalMaps(const cv::Mat& left, const cv::Mat& right)
{
    if (left.size() != right.size() || left.type() != right.type()) return false;
    cv::Mat diff;
    cv::compare(left, right, diff, cv::CMP_NE);
    return cv::countNonZero(diff) == 0;
}

// Area of the minimum-area rectangle of the foreground convex hull divided by referenceArea. The envelope is a
// rotated box, so its area does not depend on where it is centred. Points are pixel centres, so one pixel is
// added to each side: an axis-aligned w x h block measures exactly w * h.
double EnvelopeRatio(const cv::Mat& map, double referenceArea)
{
    std::vector<cv::Point> points;
    cv::findNonZero(map, points);
    if (points.size() < 3) return 0.0;
    std::vector<cv::Point> hull;
    cv::convexHull(points, hull);
    const cv::RotatedRect box = cv::minAreaRect(hull);
    return (box.size.width + 1.0) * (box.size.height + 1.0) / referenceArea;
}

// 300x240 rectangle at (left, top), 20x10 tail on its right edge and a 3x3 hole, all with one score value.
cv::Mat MakeTailedRectangle(cv::Size canvas, int left, int top, unsigned char value)
{
    cv::Mat map = cv::Mat::zeros(canvas, CV_8UC1);
    cv::rectangle(map, cv::Rect(left, top, 300, 240), cv::Scalar(value), cv::FILLED);
    cv::rectangle(map, cv::Rect(left + 300, top + 110, 20, 10), cv::Scalar(value), cv::FILLED);
    map(cv::Rect(left + 100, top + 100, 3, 3)).setTo(0);
    return map;
}

int32_t ApplyShapeCleanup(ShapeCleanupApplyFn apply, cv::Mat& ng, cv::Mat* ok, float fraction)
{
    return apply(ng.data, ok ? ok->data : nullptr, ng.cols, ng.rows, static_cast<int32_t>(ng.step[0]), fraction);
}

double MaskIou(const cv::Mat& left, const cv::Mat& right)
{
    const cv::Mat leftMask = left > 0, rightMask = right > 0;
    cv::Mat intersection, combined;
    cv::bitwise_and(leftMask, rightMask, intersection);
    cv::bitwise_or(leftMask, rightMask, combined);
    const int unionCount = cv::countNonZero(combined);
    return unionCount > 0 ? static_cast<double>(cv::countNonZero(intersection)) / unionCount : 0.0;
}

int RunV2ShapeCleanupSelfTest(int argc, char** argv)
{
    if (argc != 3) {
        std::cerr << "Usage: --v2-shape-cleanup-selftest <out.json>\n";
        return 2;
    }
    const HMODULE module = GetModuleHandleW(L"BeeNativeSegAI.dll");
    if (!module) {
        std::cerr << "BeeNativeSegAI.dll is not loaded in this process\n";
        return 3;
    }
    wchar_t modulePathBuffer[MAX_PATH * 2] = {};
    GetModuleFileNameW(module, modulePathBuffer, static_cast<DWORD>(sizeof(modulePathBuffer) / sizeof(modulePathBuffer[0])));
    const std::string modulePath = std::filesystem::path(modulePathBuffer).u8string();
    std::cout << "Loaded module: " << modulePath << "\n";
    const ShapeCleanupApplyFn apply = reinterpret_cast<ShapeCleanupApplyFn>(GetProcAddress(module, "SEGAI_V2ShapeCleanupApply"));
    const ShapeCleanupSetFn setCleanup = reinterpret_cast<ShapeCleanupSetFn>(GetProcAddress(module, "SEGAI_InferSetV2ShapeCleanup"));
    if (!apply || !setCleanup) {
        std::cerr << "Loaded module lacks SEGAI_V2ShapeCleanupApply / SEGAI_InferSetV2ShapeCleanup\n";
        std::ofstream missing(argv[2], std::ios::binary | std::ios::trunc);
        missing << "{\"module_path\":\"" << JsonEscape(modulePath) << "\",\"all_pass\":false,\"error\":\"missing exports\",\"cases\":[]}\n";
        return 4;
    }

    struct CaseResult { std::string name; bool pass; std::string detail; };
    std::vector<CaseResult> results;
    auto record = [&](const std::string& name, bool pass, const std::ostringstream& detail) {
        results.push_back(CaseResult{ name, pass, detail.str() });
        std::cout << name << ": " << (pass ? "PASS" : "FAIL") << "  " << detail.str() << "\n";
    };
    const float fraction = 0.12f;
    const cv::Size canvas(600, 500);
    const double referenceArea = 300.0 * 240.0;

    // T1: rectangle + tail + hole.
    {
        cv::Mat ng = MakeTailedRectangle(canvas, 100, 100, 200);
        cv::Mat ok = cv::Mat::zeros(canvas, CV_8UC1);
        const double before = EnvelopeRatio(ng, referenceArea);
        const int32_t rc = ApplyShapeCleanup(apply, ng, &ok, fraction);
        const double after = EnvelopeRatio(ng, referenceArea);
        const bool holeFilled = cv::countNonZero(ng(cv::Rect(200, 200, 3, 3)) == 200) == 9;
        const bool uniform = cv::countNonZero(ng > 0) == cv::countNonZero(ng == 200);
        const bool okEmpty = cv::countNonZero(ok) == 0;
        std::ostringstream detail;
        detail << std::fixed << std::setprecision(5) << "\"rc\":" << rc << ",\"envelope_before\":" << before
               << ",\"envelope_after\":" << after << ",\"hole_filled\":" << (holeFilled ? "true" : "false")
               << ",\"foreground_pixels\":" << cv::countNonZero(ng) << ",\"all_foreground_200\":" << (uniform ? "true" : "false");
        record("T1_tail_and_hole", rc >= 0 && before > 1.04 && after <= 1.02 && holeFilled && uniform && okEmpty, detail);
    }
    // T2: thin horizontal line (6 px tall) is untouched; also exercises a null ok map.
    {
        cv::Mat ng = cv::Mat::zeros(canvas, CV_8UC1);
        cv::rectangle(ng, cv::Rect(100, 200, 300, 6), cv::Scalar(200), cv::FILLED);
        const cv::Mat input = ng.clone();
        const int32_t rc = ApplyShapeCleanup(apply, ng, nullptr, fraction);
        const bool same = IdenticalMaps(ng, input);
        std::ostringstream detail;
        detail << "\"rc\":" << rc << ",\"identical\":" << (same ? "true" : "false");
        record("T2_thin_line_unchanged", rc == 0 && same, detail);
    }
    // T3: one compact pixel (4x4) alone, and 10 px away from the tailed rectangle.
    {
        cv::Mat alone = cv::Mat::zeros(canvas, CV_8UC1);
        cv::rectangle(alone, cv::Rect(100, 100, 4, 4), cv::Scalar(200), cv::FILLED);
        const cv::Mat aloneInput = alone.clone();
        cv::Mat okAlone = cv::Mat::zeros(canvas, CV_8UC1);
        const int32_t rcAlone = ApplyShapeCleanup(apply, alone, &okAlone, fraction);
        const bool aloneSame = IdenticalMaps(alone, aloneInput);

        cv::Mat withBlock = MakeTailedRectangle(canvas, 100, 100, 200); // (windows.h defines "near" as a macro)
        cv::rectangle(withBlock, cv::Rect(430, 150, 4, 4), cv::Scalar(200), cv::FILLED);
        const cv::Mat withBlockInput = withBlock.clone();
        cv::Mat okNear = cv::Mat::zeros(canvas, CV_8UC1);
        const int32_t rcNear = ApplyShapeCleanup(apply, withBlock, &okNear, fraction);
        const cv::Rect blockZone(422, 142, 20, 20); // the block inflated by 8 px; clear of the tail end at x = 419
        const bool nearSame = IdenticalMaps(withBlock(blockZone), withBlockInput(blockZone));
        std::ostringstream detail;
        detail << "\"rc_alone\":" << rcAlone << ",\"alone_identical\":" << (aloneSame ? "true" : "false")
               << ",\"rc_near\":" << rcNear << ",\"near_block_zone_identical\":" << (nearSame ? "true" : "false");
        record("T3_one_compact_pixel_unchanged", rcAlone == 0 && aloneSame && rcNear >= 0 && nearSame, detail);
    }
    // T4: 30 degree rotated 300x240 rectangle drawn at compact scale (1/4) and NEAREST upsampled x4.
    {
        const cv::Size fullSize(800, 640), compactSize(200, 160);
        const double angle = 30.0 * CV_PI / 180.0, cosine = std::cos(angle), sine = std::sin(angle);
        auto inside = [&](double px, double py) {
            const double dx = px - 400.0, dy = py - 320.0;
            return std::fabs(dx * cosine + dy * sine) <= 150.0 && std::fabs(-dx * sine + dy * cosine) <= 120.0;
        };
        cv::Mat compact = cv::Mat::zeros(compactSize, CV_8UC1), ideal = cv::Mat::zeros(fullSize, CV_8UC1);
        for (int y = 0; y < compactSize.height; ++y)
            for (int x = 0; x < compactSize.width; ++x)
                if (inside((x + 0.5) * 4.0, (y + 0.5) * 4.0)) compact.at<unsigned char>(y, x) = 200;
        for (int y = 0; y < fullSize.height; ++y)
            for (int x = 0; x < fullSize.width; ++x)
                if (inside(x + 0.5, y + 0.5)) ideal.at<unsigned char>(y, x) = 255;
        cv::Mat ng;
        cv::resize(compact, ng, fullSize, 0, 0, cv::INTER_NEAREST);
        cv::Mat ok = cv::Mat::zeros(fullSize, CV_8UC1);
        const double iouBefore = MaskIou(ng, ideal);
        const int32_t rc = ApplyShapeCleanup(apply, ng, &ok, fraction);
        const double iouAfter = MaskIou(ng, ideal);
        std::ostringstream detail;
        detail << std::fixed << std::setprecision(5) << "\"rc\":" << rc << ",\"iou_before\":" << iouBefore << ",\"iou_after\":" << iouAfter;
        record("T4_rotated_30_degrees_iou", rc >= 0 && iouAfter >= 0.97, detail);
    }
    // T5: fraction 0 is a no-op that returns 0.
    {
        cv::Mat ng = MakeTailedRectangle(canvas, 100, 100, 200);
        cv::Mat ok = cv::Mat::zeros(canvas, CV_8UC1);
        const cv::Mat ngInput = ng.clone(), okInput = ok.clone();
        const int32_t rc = ApplyShapeCleanup(apply, ng, &ok, 0.f);
        const bool same = IdenticalMaps(ng, ngInput) && IdenticalMaps(ok, okInput);
        std::ostringstream detail;
        detail << "\"rc\":" << rc << ",\"identical\":" << (same ? "true" : "false");
        record("T5_fraction_zero_identical", rc == 0 && same, detail);
    }
    // T6: OK component (with its own 6 px notch) inside the 72 px concavity of an NG U-shape. The U closing and the
    // OK closing both want the notch pixels: they are contested and must stay 0. OK pixels stay unchanged.
    {
        const cv::Size bigCanvas(1000, 900);
        cv::Mat ng = cv::Mat::zeros(bigCanvas, CV_8UC1), ok = cv::Mat::zeros(bigCanvas, CV_8UC1);
        cv::rectangle(ng, cv::Rect(100, 100, 700, 600), cv::Scalar(200), cv::FILLED);
        cv::rectangle(ng, cv::Rect(380, 100, 72, 400), cv::Scalar(0), cv::FILLED);
        cv::rectangle(ok, cv::Rect(381, 200, 70, 70), cv::Scalar(150), cv::FILLED);
        cv::rectangle(ok, cv::Rect(413, 200, 6, 30), cv::Scalar(0), cv::FILLED);
        const cv::Mat okInput = ok.clone();
        const cv::Rect contested(413, 200, 6, 30);
        const int32_t rc = ApplyShapeCleanup(apply, ng, &ok, fraction);
        const bool okSame = IdenticalMaps(ok, okInput);
        const int contestedNg = cv::countNonZero(ng(contested)), contestedOk = cv::countNonZero(ok(contested));
        const int slotFilled = cv::countNonZero(ng(cv::Rect(380, 100, 72, 400)));
        std::ostringstream detail;
        detail << "\"rc\":" << rc << ",\"ok_identical\":" << (okSame ? "true" : "false") << ",\"contested_ng_nonzero\":" << contestedNg
               << ",\"contested_ok_nonzero\":" << contestedOk << ",\"ng_slot_pixels_after\":" << slotFilled;
        record("T6_ok_inside_ng_concavity_contested", rc >= 0 && okSame && contestedNg == 0 && contestedOk == 0, detail);
    }
    // T7: 45 degree line 6 px thick; opening would erase it, so the empty-result fallback keeps it.
    {
        cv::Mat ng = cv::Mat::zeros(canvas, CV_8UC1);
        cv::line(ng, cv::Point(100, 100), cv::Point(312, 312), cv::Scalar(200), 6, cv::LINE_8);
        const cv::Mat input = ng.clone();
        cv::Mat ok = cv::Mat::zeros(canvas, CV_8UC1);
        const int32_t rc = ApplyShapeCleanup(apply, ng, &ok, fraction);
        const bool same = IdenticalMaps(ng, input);
        std::ostringstream detail;
        detail << "\"rc\":" << rc << ",\"identical\":" << (same ? "true" : "false") << ",\"foreground_pixels\":" << cv::countNonZero(ng);
        record("T7_diagonal_line_fallback", rc == 0 && same, detail);
    }
    // T8: tailed rectangle touching x = 0.
    {
        cv::Mat ng = MakeTailedRectangle(canvas, 0, 100, 200);
        cv::Mat ok = cv::Mat::zeros(canvas, CV_8UC1);
        const int32_t rc = ApplyShapeCleanup(apply, ng, &ok, fraction);
        const bool columnKept = cv::countNonZero(ng(cv::Rect(0, 100, 1, 240)) == 200) == 240;
        const int foreground = cv::countNonZero(ng);
        std::ostringstream detail;
        detail << "\"rc\":" << rc << ",\"x0_column_kept\":" << (columnKept ? "true" : "false") << ",\"foreground_pixels\":" << foreground;
        record("T8_touching_image_edge", rc >= 0 && columnKept && foreground == 300 * 240, detail);
    }
    // T9: invalid arguments return -2 and leave the buffers alone.
    {
        cv::Mat a = cv::Mat::zeros(64, 64, CV_8UC1), b = cv::Mat::zeros(64, 64, CV_8UC1);
        a.at<unsigned char>(10, 10) = 200;
        const cv::Mat aInput = a.clone();
        const int32_t rcNan = apply(a.data, b.data, 64, 64, 64, std::numeric_limits<float>::quiet_NaN());
        const int32_t rcLarge = apply(a.data, b.data, 64, 64, 64, 0.3f);
        const int32_t rcNull = apply(nullptr, b.data, 64, 64, 64, 0.12f);
        const int32_t rcStep = apply(a.data, b.data, 64, 64, 63, 0.12f);
        std::ostringstream detail;
        detail << "\"rc_nan\":" << rcNan << ",\"rc_0_3\":" << rcLarge << ",\"rc_null_ng\":" << rcNull << ",\"rc_step_lt_w\":" << rcStep;
        record("T9_invalid_arguments", rcNan == -2 && rcLarge == -2 && rcNull == -2 && rcStep == -2 && IdenticalMaps(a, aInput), detail);
    }
    // T10: the handle option export validates its argument (a fresh handle holds no model).
    {
        std::unique_ptr<void, InferHandleDeleter> handle(SEGAI_InferCreate());
        const int32_t rcNullHandle = setCleanup(nullptr, 0.12f);
        const int32_t rcOn = handle ? setCleanup(handle.get(), 0.12f) : 99;
        const int32_t rcTooLarge = handle ? setCleanup(handle.get(), 0.3f) : 99;
        const int32_t rcNan = handle ? setCleanup(handle.get(), std::numeric_limits<float>::quiet_NaN()) : 99;
        const int32_t rcOff = handle ? setCleanup(handle.get(), 0.f) : 99;
        std::ostringstream detail;
        detail << "\"rc_null_handle\":" << rcNullHandle << ",\"rc_0_12\":" << rcOn << ",\"rc_0_3\":" << rcTooLarge
               << ",\"rc_nan\":" << rcNan << ",\"rc_0\":" << rcOff;
        record("T10_set_export_validation", rcNullHandle == -1 && rcOn == 0 && rcTooLarge == -2 && rcNan == -2 && rcOff == 0, detail);
    }

    bool allPass = true;
    for (const CaseResult& result : results) allPass = allPass && result.pass;
    std::ofstream json(argv[2], std::ios::binary | std::ios::trunc);
    json << "{\"module_path\":\"" << JsonEscape(modulePath) << "\",\"fraction\":" << fraction
         << ",\"all_pass\":" << (allPass ? "true" : "false") << ",\"cases\":[";
    for (size_t i = 0; i < results.size(); ++i) {
        json << (i ? "," : "") << "\n  {\"name\":\"" << results[i].name << "\",\"pass\":" << (results[i].pass ? "true" : "false")
             << ",\"measured\":{" << results[i].detail << "}}";
    }
    json << "\n]}\n";
    json.flush();
    if (!json.good()) {
        std::cerr << "Could not write " << argv[2] << "\n";
        return 5;
    }
    return allPass ? 0 : 1;
}

// ---- Baseline-Ext deterministic dump harness (S0 test mode) -----------------------------------------------

namespace {
// Simple SHA256 implementation (public domain)
inline uint32_t rotr(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }
inline uint32_t ch(uint32_t x, uint32_t y, uint32_t z) { return (x & y) ^ (~x & z); }
inline uint32_t maj(uint32_t x, uint32_t y, uint32_t z) { return (x & y) ^ (x & z) ^ (y & z); }
inline uint32_t sigma0(uint32_t x) { return rotr(x, 2) ^ rotr(x, 13) ^ rotr(x, 22); }
inline uint32_t sigma1(uint32_t x) { return rotr(x, 6) ^ rotr(x, 11) ^ rotr(x, 25); }
inline uint32_t gamma0(uint32_t x) { return rotr(x, 7) ^ rotr(x, 18) ^ (x >> 3); }
inline uint32_t gamma1(uint32_t x) { return rotr(x, 17) ^ rotr(x, 19) ^ (x >> 10); }

struct Sha256 {
    uint32_t state[8] = {
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
        0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19
    };
    uint64_t count = 0;
    uint8_t buffer[64];

    static const uint32_t k[64];

    void transform(const uint8_t* data) {
        uint32_t w[64];
        for (int i = 0; i < 16; ++i) {
            w[i] = (static_cast<uint32_t>(data[i*4]) << 24) |
                   (static_cast<uint32_t>(data[i*4+1]) << 16) |
                   (static_cast<uint32_t>(data[i*4+2]) << 8) |
                   (static_cast<uint32_t>(data[i*4+3]));
        }
        for (int i = 16; i < 64; ++i) {
            w[i] = gamma1(w[i-2]) + w[i-7] + gamma0(w[i-15]) + w[i-16];
        }
        uint32_t a = state[0], b = state[1], c = state[2], d = state[3];
        uint32_t e = state[4], f = state[5], g = state[6], h = state[7];
        for (int i = 0; i < 64; ++i) {
            const uint32_t t1 = h + sigma1(e) + ch(e,f,g) + k[i] + w[i];
            const uint32_t t2 = sigma0(a) + maj(a,b,c);
            h = g; g = f; f = e; e = d + t1;
            d = c; c = b; b = a; a = t1 + t2;
        }
        state[0] += a; state[1] += b; state[2] += c; state[3] += d;
        state[4] += e; state[5] += f; state[6] += g; state[7] += h;
    }

    void update(const uint8_t* data, size_t len) {
        for (size_t i = 0; i < len; ++i) {
            const size_t idx = (count / 8) % 64;
            buffer[idx] = data[i];
            count += 8;
            if (idx == 63) transform(buffer);
        }
    }

    void finalize(uint8_t hash[32]) {
        const uint64_t bits = count;
        const uint8_t pad = 0x80;
        update(&pad, 1);
        while (((count / 8) % 64) != 56) {
            const uint8_t zero = 0;
            update(&zero, 1);
        }
        uint8_t len_buf[8];
        for (int i = 0; i < 8; ++i) len_buf[7-i] = static_cast<uint8_t>(bits >> (i*8));
        update(len_buf, 8);
        for (int i = 0; i < 8; ++i) {
            hash[i*4+0] = static_cast<uint8_t>(state[i] >> 24);
            hash[i*4+1] = static_cast<uint8_t>(state[i] >> 16);
            hash[i*4+2] = static_cast<uint8_t>(state[i] >> 8);
            hash[i*4+3] = static_cast<uint8_t>(state[i]);
        }
    }
};

const uint32_t Sha256::k[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
};

std::string ComputeSha256(const std::vector<uint8_t>& data) {
    Sha256 sha;
    sha.update(data.data(), data.size());
    uint8_t hash[32];
    sha.finalize(hash);
    std::ostringstream ss;
    for (int i = 0; i < 32; ++i) {
        ss << std::hex << std::setw(2) << std::setfill('0') << static_cast<int>(hash[i]);
    }
    return ss.str();
}

std::vector<std::string> LoadImagesFromDir(const std::filesystem::path& dir) {
    std::vector<std::string> images;
    std::error_code ec;
    for (const auto& entry : std::filesystem::directory_iterator(dir, ec)) {
        const auto ext = entry.path().extension().u8string();
        if (entry.is_regular_file() && (ext == ".png" || ext == ".PNG" || ext == ".bmp" || ext == ".BMP")) {
            images.push_back(entry.path().filename().u8string());
        }
    }
    std::sort(images.begin(), images.end());
    return images;
}

int RunBaselineExtMode(int argc, char** argv) {
    if (argc < 5) {
        std::cerr << "Usage: --baseline-ext <modelDir-or-model.segai> <imageDir> <outDir> [train [<backbone.onnx>]]\n";
        return 100;
    }

    const std::filesystem::path modelPath = std::filesystem::u8path(argv[2]);
    const std::filesystem::path imageDir = std::filesystem::u8path(argv[3]);
    const std::filesystem::path outDir = std::filesystem::u8path(argv[4]);
    const bool doTrain = (argc > 5 && std::strcmp(argv[5], "train") == 0);

    // Optional backbone path (comes after 'train' keyword, or use default)
    std::filesystem::path backbonePath;
    if (doTrain) {
        if (argc > 6) {
            backbonePath = std::filesystem::u8path(argv[6]);
        } else {
            // Default fallback: Models/DefectV2/mobilenetv2-12.onnx next to the executable
            // Get executable directory by using the module path
            wchar_t exePath[MAX_PATH] = {};
            GetModuleFileNameW(nullptr, exePath, MAX_PATH);
            backbonePath = std::filesystem::path(exePath).parent_path() / "Models" / "DefectV2" / "mobilenetv2-12.onnx";
        }

        // Check that backbone file exists
        if (!std::filesystem::exists(backbonePath)) {
            std::cerr << "V2 backbone is missing: " << backbonePath.u8string() << "\n";
            std::cerr << "Expected Models\\DefectV2\\mobilenetv2-12.onnx next to BeeNativeSegAI.dll\n";
            return 102;
        }
        backbonePath = std::filesystem::absolute(backbonePath);
    }

    std::error_code ec;
    if (!std::filesystem::is_directory(imageDir, ec)) {
        std::cerr << "imageDir does not exist or is not a directory\n";
        return 101;
    }

    std::filesystem::create_directories(outDir, ec);
    if (ec) {
        std::cerr << "Failed to create outDir\n";
        return 102;
    }

    // Load images
    const auto imageNames = LoadImagesFromDir(imageDir);
    if (imageNames.empty()) {
        std::cerr << "No PNG/BMP images found in imageDir\n";
        return 103;
    }

    // Create inferer and load model
    std::unique_ptr<void, InferHandleDeleter> inferer(SEGAI_InferCreate());
    if (!inferer) {
        std::cerr << "SEGAI_InferCreate failed\n";
        return 104;
    }

    const std::wstring modelWide = modelPath.wstring();
    if (SEGAI_InferLoad(inferer.get(), modelWide.c_str()) != 0) {
        std::cerr << "SEGAI_InferLoad failed for " << modelPath.u8string() << "\n";
        return 105;
    }

    // Force CPU
    if (SEGAI_InferSetGpu(inferer.get(), 0) != 0) {
        std::cerr << "SEGAI_InferSetGpu(0) failed\n";
        return 106;
    }

    // Helper to write inference results for a config
    auto runConfig = [&](const std::string& configName, const std::function<void(void*)>& setupFn,
                         const cv::Mat& image, const std::string& baseName, const std::filesystem::path& outDir) {
        SEGAI_InferSetGpu(inferer.get(), 0);
        if (setupFn) setupFn(inferer.get());

        uint8_t* maskPtr = nullptr;
        int outW = 0, outH = 0;
        float score = 0.0f;
        const int rc = SEGAI_InferPredict(inferer.get(), image.data, image.cols, image.rows,
            static_cast<int>(image.step), 0, 0, image.cols, image.rows, 0.5f, &maskPtr, &outW, &outH, &score);

        if (rc == 0 && maskPtr) {
            std::ofstream maskFile(outDir / (baseName + "__" + configName + "__mask.bin"), std::ios::binary);
            maskFile.write(reinterpret_cast<const char*>(maskPtr), outW * outH);
            maskFile.close();

            uint8_t* confPtr = nullptr;
            int confW = 0, confH = 0;
            if (SEGAI_InferGetConfidence(inferer.get(), &confPtr, &confW, &confH) == 0 && confPtr) {
                std::ofstream confFile(outDir / (baseName + "__" + configName + "__confidence.bin"), std::ios::binary);
                confFile.write(reinterpret_cast<const char*>(confPtr), confW * confH);
                confFile.close();
                SEGAI_FreeBuffer(confPtr);
            }

            uint8_t* normPtr = nullptr;
            int normW = 0, normH = 0;
            if (SEGAI_InferGetNormalConfidence(inferer.get(), &normPtr, &normW, &normH) == 0 && normPtr) {
                std::ofstream normFile(outDir / (baseName + "__" + configName + "__normal_confidence.bin"), std::ios::binary);
                normFile.write(reinterpret_cast<const char*>(normPtr), normW * normH);
                normFile.close();
                SEGAI_FreeBuffer(normPtr);
            }
            SEGAI_FreeBuffer(maskPtr);
        }

        std::ofstream metaFile(outDir / (baseName + "__" + configName + "__metadata.txt"));
        metaFile << "return_code=" << rc << "\n"
                 << "score=" << score << "\n"
                 << "has_ng=" << (SEGAI_InferHasNgClass(inferer.get()) != 0 ? "1" : "0") << "\n"
                 << "has_normal=" << (SEGAI_InferHasNormalClass(inferer.get()) != 0 ? "1" : "0") << "\n"
                 << "image_width=" << image.cols << "\n"
                 << "image_height=" << image.rows << "\n";
        char errBuf[1024] = {};
        SEGAI_InferGetLastError(inferer.get(), errBuf, sizeof(errBuf));
        if (errBuf[0]) metaFile << "last_error=" << errBuf << "\n";
        metaFile.close();
    };

    // Write config.txt with the exact calls for each configuration
    std::ofstream configFile(outDir / "config.txt");
    configFile << "Config: compact_default\n"
               << "  SEGAI_InferSetGpu(0, 0)\n"
               << "  SEGAI_InferSetObjectScale(0.5, 2.0, 0)\n"
               << "Config: postprocess_v1\n"
               << "  UNREACHABLE: postprocess_version is a model property (YAML key stored at train-time),\n"
               << "  loaded from model at inference load. Cannot be changed at runtime via exports.\n"
               << "  V2 always uses postprocess_version=2 (compact). HEAD exports provide no setter.\n"
               << "Config: multiscale_on\n"
               << "  SEGAI_InferSetObjectScale(0.5, 2.0, 1)\n"
               << "Config: winner_boundary\n"
               << "  SEGAI_InferSetV2BoundaryMode(1)\n"
               << "Config: shape_cleanup\n"
               << "  SEGAI_InferSetV2ShapeCleanup(0.1)\n"
               << "Config: speed_scale\n"
               << "  SEGAI_InferSetV2SpeedScale(0.5)\n"
               << "Config: tiled\n"
               << "  UNREACHABLE: No tiled inference export in HEAD SegAINativeExport.h.\n";
    configFile.close();

    // Process each image with all configurations
    for (const auto& imgName : imageNames) {
        const std::filesystem::path imagePath = imageDir / imgName;
        const cv::Mat image = cv::imread(imagePath.u8string(), cv::IMREAD_COLOR);
        if (image.empty()) {
            std::cerr << "Could not load image: " << imagePath.u8string() << "\n";
            continue;
        }

        const std::string baseName = std::filesystem::path(imgName).stem().u8string();

        // Config 1: compact_default
        runConfig("compact_default", [](void* h) { SEGAI_InferSetObjectScale(h, 0.5f, 2.0f, 0); }, image, baseName, outDir);

        // Config 2: postprocess_v1 (UNREACHABLE - documented in config.txt)
        // postprocess_version is a model property, cannot be changed at runtime

        // Config 3: multiscale_on
        runConfig("multiscale_on", [](void* h) { SEGAI_InferSetObjectScale(h, 0.5f, 2.0f, 1); }, image, baseName, outDir);

        // Config 4: winner_boundary
        runConfig("winner_boundary", [](void* h) { SEGAI_InferSetV2BoundaryMode(h, 1); }, image, baseName, outDir);

        // Config 5: shape_cleanup
        runConfig("shape_cleanup", [](void* h) { SEGAI_InferSetV2ShapeCleanup(h, 0.1f); }, image, baseName, outDir);

        // Config 6: speed_scale
        runConfig("speed_scale", [](void* h) { SEGAI_InferSetV2SpeedScale(h, 0.5f); }, image, baseName, outDir);

        // Config 7: tiled (UNREACHABLE - no export in HEAD)
    }

    // Optional training
    if (doTrain) {
        const std::filesystem::path fixtureDir = std::filesystem::u8path("tmp/EAI/EAI-3/fixture-src");
        std::vector<std::pair<std::string, cv::Mat>> fixtures;
        std::vector<std::pair<std::string, cv::Mat>> masks;

        // Load fixture images
        for (int i = 0; i < 2; ++i) {
            const std::string fname = std::to_string(i) + ".png";
            const std::filesystem::path fpath = fixtureDir / fname;
            cv::Mat img = cv::imread(fpath.u8string(), cv::IMREAD_COLOR);
            if (!img.empty()) {
                fixtures.push_back({fname, img});
            }
        }

        if (!fixtures.empty()) {
            // Synthesize masks deterministically if not present
            // Mask raster convention for V2 training: 0=background, 1=NG (defect), 2=OK (normal)
            // Ensure enough pixels for each class: background majority, NG and OK as separated blobs ~5-10% each
            for (const auto& [name, img] : fixtures) {
                const int w = img.cols, h = img.rows;

                // Start with all background (class 0)
                cv::Mat mask = cv::Mat(img.size(), CV_8UC1, cv::Scalar(0)); // 0 = background

                // Add NG blob 1: top-left region, size ~10% of image width/height
                const int ng_size1 = std::max(20, (w + h) / 30); // ~3% per dimension
                cv::Rect ng_blob1(w / 10, h / 10, ng_size1, ng_size1);
                ng_blob1 = ng_blob1 & cv::Rect(0, 0, w, h); // clamp to image
                cv::rectangle(mask, ng_blob1, cv::Scalar(1), cv::FILLED); // 1 = NG class

                // Add NG blob 2: bottom-right region, different size
                const int ng_size2 = std::max(15, (w + h) / 40); // ~2.5% per dimension
                cv::Rect ng_blob2(w - ng_size2 - w/8, h - ng_size2 - h/8, ng_size2, ng_size2);
                ng_blob2 = ng_blob2 & cv::Rect(0, 0, w, h); // clamp to image
                cv::rectangle(mask, ng_blob2, cv::Scalar(1), cv::FILLED); // 1 = NG class

                // Add OK blob: center region (only for models with OK class, but always add for simplicity)
                // This represents the normal/OK region
                const int ok_size = std::max(25, (w + h) / 25); // ~4% per dimension
                cv::Rect ok_blob(w/2 - ok_size/2, h/2 - ok_size/2, ok_size, ok_size);
                ok_blob = ok_blob & cv::Rect(0, 0, w, h); // clamp to image
                cv::rectangle(mask, ok_blob, cv::Scalar(2), cv::FILLED); // 2 = OK class

                masks.push_back({name, mask});
            }

            // Create V2 config JSON for deterministic training (profile 2)
            // Smallest settings that complete in ~1 minute: epochs=1, side=160, minimal augmentation
            // Match the format written by SegAiTrainingPipeline.cs BuildV2SettingsJson
            const std::filesystem::path v2LogsDir = outDir / "v2_logs";
            const std::filesystem::path v2ConfigPath = outDir / "settings.json";
            std::error_code ec;
            std::filesystem::create_directories(v2LogsDir, ec);

            std::string v2LogsAbsolute = std::filesystem::absolute(v2LogsDir).u8string();
            std::string backboneAbsolute = std::filesystem::absolute(backbonePath).u8string();
            // Replace backslashes with forward slashes for JSON
            for (auto& ch : v2LogsAbsolute) if (ch == '\\') ch = '/';
            for (auto& ch : backboneAbsolute) if (ch == '\\') ch = '/';

            {
                // Write JSON config matching EasyLabel.SegAI/Training/SegAiTrainingPipeline.cs
                std::ofstream v2cfg(v2ConfigPath, std::ios::binary);
                std::string content = "{"
                    "\"backbone\":\"" + backboneAbsolute + "\","
                    "\"logs\":\"" + v2LogsAbsolute + "\","
                    "\"epochs\":1,"
                    "\"side\":160,"
                    "\"learningRate\":0.1,"
                    "\"regularization\":0.0,"
                    "\"rotation\":0.0,"
                    "\"scaleVariation\":0.0,"
                    "\"brightness\":0.0,"
                    "\"augment\":0,"
                    "\"gpu\":0,"
                    "\"validation\":[]"
                    "}";
                // Write as UTF-8 without BOM (like .NET UTF8Encoding(false))
                v2cfg.write(content.c_str(), content.size());
                v2cfg.close();
            }

            // Train once with V2 trainer (profile 2)
            // Verifier will launch exe twice in separate processes and compare SHA256
            void* trainer = SEGAI_TrainerCreate();
            if (!trainer) {
                std::cerr << "SEGAI_TrainerCreate failed\n";
                return 110;
            }

            // Configure V2 trainer (sets profile to 2 internally)
            const std::wstring configWide = v2ConfigPath.wstring();
            const int cfgRc = SEGAI_TrainerConfigureV2(trainer, configWide.c_str());
            if (cfgRc != 0) {
                char errBuf[1024] = {};
                SEGAI_TrainerGetLastError(trainer, errBuf, sizeof(errBuf));
                std::cerr << "SEGAI_TrainerConfigureV2 failed: rc=" << cfgRc << " error=" << errBuf << "\n";
                SEGAI_TrainerDestroy(trainer);
                return 111;
            }

            // Add samples with V2 mask convention: 0=bg, 1=NG, 2=OK
            int sampleRc = 0;
            for (size_t i = 0; i < fixtures.size() && i < masks.size(); ++i) {
                const auto& img = fixtures[i].second;
                const auto& mask = masks[i].second;
                sampleRc = SEGAI_TrainerSetROI(trainer, 0, 0, img.cols, img.rows);
                if (sampleRc == 0) {
                    sampleRc = SEGAI_TrainerAddSample(trainer, img.data, img.cols, img.rows, static_cast<int>(img.step),
                        mask.data, static_cast<int>(mask.step));
                }
            }
            if (sampleRc != 0) {
                char errBuf[1024] = {};
                SEGAI_TrainerGetLastError(trainer, errBuf, sizeof(errBuf));
                std::cerr << "SEGAI_TrainerAddSample failed: rc=" << sampleRc << " error=" << errBuf << "\n";
                SEGAI_TrainerDestroy(trainer);
                return 112;
            }

            // Train with V2 (epochs already set to 1 in config for ~1 minute runtime)
            const int trainRc = SEGAI_TrainerTrain(trainer, 0, 0, 0, nullptr, nullptr, nullptr);
            if (trainRc != 0) {
                char errBuf[1024] = {};
                SEGAI_TrainerGetLastError(trainer, errBuf, sizeof(errBuf));
                std::cerr << "SEGAI_TrainerTrain failed: rc=" << trainRc << " error=" << errBuf << "\n";
                SEGAI_TrainerDestroy(trainer);
                return 113;
            }

            // Save to train-run.segai
            const std::wstring outModel = (outDir / "train-run.segai").wstring();
            const int saveRc = SEGAI_TrainerSave(trainer, outModel.c_str(), 0.5f, 40);
            SEGAI_TrainerDestroy(trainer);

            if (saveRc != 0) {
                std::cerr << "SEGAI_TrainerSave failed: rc=" << saveRc << "\n";
                return 114;
            }

            // Report file size and SHA256
            std::ifstream file(outModel, std::ios::binary);
            std::vector<uint8_t> fileData((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
            if (!fileData.empty()) {
                std::string sha = ComputeSha256(fileData);
                std::cout << "train_run_size=" << fileData.size() << "\n";
                std::cout << "train_run_sha256=" << sha << "\n";
            } else {
                std::cerr << "train-run.segai is empty\n";
                return 115;
            }
        }
    }

    return 0;
}

// ---- EAI-CLS S1 named-class tests T1-T3 --------------------------------------------------------------------
//   T1  --class-table-selftest                                      class-table codec + new exports on bare handles
//   T2  --class-table-model <outDir> [<backbone.onnx>|-] [<fixtureDir>|-] [<expectedV3Sha256>]
//                                                                   tiny V2 training with and without a class table
//   T3  --class-table-negative <v3.segai> <v4.segai> <outDir>        version 5, truncated/corrupt table, v3 legacy load
// Each mode prints one line per failed check and a summary; the exit code is 0 only when every check passed.

struct TestTally {
    int checks = 0;
    int failures = 0;
    void Check(bool condition, const std::string& what)
    {
        ++checks;
        if (!condition) {
            ++failures;
            std::cerr << "FAIL: " << what << "\n";
        }
    }
    int Finish(const char* name) const
    {
        std::cout << name << ": " << (checks - failures) << "/" << checks << " checks passed\n";
        return failures == 0 ? 0 : 1;
    }
};

BeeSegAI::SegClassTable MakeClassTable(const std::vector<std::pair<std::string, uint8_t>>& items, bool enabled = true)
{
    BeeSegAI::SegClassTable table;
    for (const auto& item : items) {
        BeeSegAI::SegClassEntry entry;
        entry.name = item.first;
        entry.kind = item.second;
        entry.enabled = enabled;
        table.classes.push_back(entry);
    }
    return table;
}

bool SameClassTable(const BeeSegAI::SegClassTable& left, const BeeSegAI::SegClassTable& right)
{
    if (left.classes.size() != right.classes.size()) return false;
    for (size_t index = 0; index < left.classes.size(); ++index) {
        if (left.classes[index].name != right.classes[index].name || left.classes[index].kind != right.classes[index].kind ||
            left.classes[index].enabled != right.classes[index].enabled) return false;
    }
    return true;
}

std::string TrainerErrorText(void* trainer)
{
    char buffer[1024] = {};
    SEGAI_TrainerGetLastError(trainer, buffer, static_cast<int32_t>(sizeof(buffer)));
    return std::string(buffer);
}

std::string InferErrorText(void* infer)
{
    char buffer[1024] = {};
    SEGAI_InferGetLastError(infer, buffer, static_cast<int32_t>(sizeof(buffer)));
    return std::string(buffer);
}

bool Contains(const std::string& text, const char* needle)
{
    return text.find(needle) != std::string::npos;
}

std::vector<unsigned char> ReadFileBytes(const std::filesystem::path& path)
{
    std::ifstream stream(path, std::ios::binary);
    return std::vector<unsigned char>((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
}

bool WriteFileBytes(const std::filesystem::path& path, const std::vector<unsigned char>& bytes)
{
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream) return false;
    if (!bytes.empty()) stream.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    return stream.good();
}

uint32_t GetU32(const std::vector<unsigned char>& bytes, size_t offset)
{
    return static_cast<uint32_t>(bytes[offset]) | (static_cast<uint32_t>(bytes[offset + 1]) << 8) |
           (static_cast<uint32_t>(bytes[offset + 2]) << 16) | (static_cast<uint32_t>(bytes[offset + 3]) << 24);
}

uint64_t GetU64(const std::vector<unsigned char>& bytes, size_t offset)
{
    return static_cast<uint64_t>(GetU32(bytes, offset)) | (static_cast<uint64_t>(GetU32(bytes, offset + 4)) << 32);
}

void PutU32(std::vector<unsigned char>& bytes, size_t offset, uint32_t value)
{
    for (size_t index = 0; index < 4; ++index) bytes[offset + index] = static_cast<unsigned char>((value >> (8 * index)) & 0xFFu);
}

void FlipBits(unsigned char& byte, unsigned int mask)
{
    byte = static_cast<unsigned char>(byte ^ mask);
}

bool ContainsText(const std::vector<unsigned char>& bytes, size_t begin, size_t end, const std::string& needle)
{
    if (end > bytes.size() || begin > end) return false;
    const auto first = bytes.begin() + static_cast<std::ptrdiff_t>(begin);
    const auto last = bytes.begin() + static_cast<std::ptrdiff_t>(end);
    return std::search(first, last, needle.begin(), needle.end()) != last;
}

// .segai header offsets (SegAIHeader is 72 bytes, packed).
constexpr size_t kSegaiHeaderBytes = 72;
constexpr size_t kOffsetVersion = 8;
constexpr size_t kOffsetNumClasses = 16;
constexpr size_t kOffsetReservedA = 52;
constexpr size_t kOffsetPayloadSize = 56;

struct SegaiLayout {
    bool valid = false;
    uint32_t version = 0, featureFlags = 0, numClasses = 0, featureCount = 0, reservedA = 0, payloadCrc = 0;
    uint64_t payloadSize = 0;
};

SegaiLayout ReadSegaiLayout(const std::vector<unsigned char>& file)
{
    SegaiLayout layout;
    if (file.size() < kSegaiHeaderBytes || std::memcmp(file.data(), "SEGAI\0\0", 8) != 0) return layout;
    layout.valid = true;
    layout.version = GetU32(file, kOffsetVersion);
    layout.featureFlags = GetU32(file, 12);
    layout.numClasses = GetU32(file, kOffsetNumClasses);
    layout.featureCount = GetU32(file, 20);
    layout.reservedA = GetU32(file, kOffsetReservedA);
    layout.payloadSize = GetU64(file, kOffsetPayloadSize);
    layout.payloadCrc = GetU32(file, 64);
    return layout;
}

// expectedTable == nullptr: a version 3 file; otherwise a version 4 file whose stored table bytes must equal *expectedTable.
void CheckSegaiLayout(TestTally& tally, const std::string& label, const std::vector<unsigned char>& file,
                      const std::vector<unsigned char>* expectedTable)
{
    const SegaiLayout layout = ReadSegaiLayout(file);
    tally.Check(layout.valid, label + ": header magic");
    if (!layout.valid) return;
    const uint32_t expectedVersion = expectedTable ? 4u : 3u;
    const uint64_t tableBytes = expectedTable ? expectedTable->size() : 0u;
    tally.Check(layout.version == expectedVersion, label + ": header version is " + std::to_string(expectedVersion) + " (was " + std::to_string(layout.version) + ")");
    tally.Check(layout.featureFlags == 0x02000000u && layout.featureCount == 448u && layout.numClasses == 3u,
                label + ": featureFlags 0x02000000, featureCount 448, numClasses 3");
    tally.Check(layout.reservedA == tableBytes, label + ": reservedA is the class-table size " + std::to_string(tableBytes));
    tally.Check(file.size() == kSegaiHeaderBytes + layout.payloadSize + layout.reservedA, label + ": file size = 72 + payloadSize + reservedA");
    if (file.size() < kSegaiHeaderBytes + layout.payloadSize) return;
    const size_t payloadEnd = kSegaiHeaderBytes + static_cast<size_t>(layout.payloadSize);
    tally.Check(BeeSegAI::ClassTableCrc32(file.data() + kSegaiHeaderBytes, static_cast<size_t>(layout.payloadSize)) == layout.payloadCrc,
                label + ": payload CRC32");
    tally.Check(ContainsText(file, kSegaiHeaderBytes, payloadEnd, "native_v2"), label + ": payload has native_v2");
    const bool hasActiveClasses = ContainsText(file, kSegaiHeaderBytes, payloadEnd, "active_classes");
    tally.Check(expectedTable ? !hasActiveClasses : hasActiveClasses,
                label + (expectedTable ? ": v4 payload omits active_classes" : ": v3 payload keeps active_classes"));
    if (expectedTable) {
        tally.Check(file.size() == payloadEnd + expectedTable->size() &&
                    std::equal(expectedTable->begin(), expectedTable->end(), file.begin() + static_cast<std::ptrdiff_t>(payloadEnd)),
                    label + ": stored class table bytes");
    }
}

// Returns SEGAI_InferGetClassTable's size and fills bytes with the table when the call reports one.
int32_t QueryClassTable(void* infer, std::vector<unsigned char>& bytes)
{
    bytes.clear();
    const int32_t required = SEGAI_InferGetClassTable(infer, nullptr, 0);
    if (required > 0) {
        bytes.assign(static_cast<size_t>(required), 0xAAu);
        if (SEGAI_InferGetClassTable(infer, bytes.data(), required) != required) bytes.clear();
    }
    return required;
}

// ---- T1 ----------------------------------------------------------------------------------------------------

int RunClassTableSelfTest()
{
    using namespace BeeSegAI;
    TestTally tally;
    std::string error;

    {
        const unsigned char digits[9] = { '1', '2', '3', '4', '5', '6', '7', '8', '9' };
        tally.Check(ClassTableCrc32(digits, 9) == 0xCBF43926u, "CRC-32 check value of \"123456789\"");
    }
    tally.Check(kMaxClasses == 32 && kMaxClassTableBytes == 2192, "table limits are 32 classes and 2192 bytes");

    // Wire layout of the legacy table: "SCLS", version 1, N 2, two entries, little-endian CRC32.
    const SegClassTable legacy = MakeLegacyClassTable();
    std::vector<unsigned char> legacyBytes;
    tally.Check(EncodeClassTable(legacy, legacyBytes, error), "legacy table encodes: " + error);
    {
        const unsigned char expected[24] = { 'S', 'C', 'L', 'S', 1, 0, 0, 0, 2, 0, 0, 0,
                                             0, 1, 2, 0, 'N', 'G', 1, 1, 2, 0, 'O', 'K' };
        tally.Check(legacyBytes.size() == 28, "legacy table is 28 bytes");
        tally.Check(legacyBytes.size() == 28 && std::memcmp(legacyBytes.data(), expected, 24) == 0, "legacy table wire bytes");
        const uint32_t crc = ClassTableCrc32(expected, 24);
        tally.Check(legacyBytes.size() == 28 && GetU32(legacyBytes, 24) == crc, "legacy table trailing CRC32 (little-endian)");
    }

    // Round trips.
    auto roundTrip = [&](const SegClassTable& table, const std::string& label) -> std::vector<unsigned char> {
        std::vector<unsigned char> bytes;
        std::string reason;
        const bool encoded = EncodeClassTable(table, bytes, reason);
        tally.Check(encoded, label + " encodes: " + reason);
        if (!encoded) return bytes;
        tally.Check(bytes.size() <= kMaxClassTableBytes, label + " fits in 2192 bytes");
        SegClassTable decoded;
        const bool ok = DecodeClassTable(bytes.data(), bytes.size(), decoded, reason);
        tally.Check(ok, label + " decodes: " + reason);
        tally.Check(ok && SameClassTable(table, decoded), label + " round trips");
        return bytes;
    };
    roundTrip(legacy, "legacy NG/OK table");
    roundTrip(MakeClassTable({ { "NG", kClassKindDefect } }), "single DEFECT class");
    roundTrip(MakeClassTable({ { "OK", kClassKindGood } }), "single GOOD class");
    {
        SegClassTable mixed = MakeLegacyClassTable(false, true);
        roundTrip(mixed, "table with a disabled class");
    }
    roundTrip(MakeClassTable({ { std::string("Tr\xE1\xBA\xA7" "y x\xC6\xB0\xE1\xBB\x9B" "c"), kClassKindDefect },
                               { std::string("\xC2\xA0"), kClassKindDefect },
                               { std::string("\xEF\xBF\xBF"), kClassKindDefect },
                               { std::string("\xF4\x8F\xBF\xBF"), kClassKindDefect },
                               { std::string("\xF0\x9F\x98\x80"), kClassKindDefect },
                               { std::string("ng"), kClassKindDefect },
                               { std::string("NG"), kClassKindDefect },
                               { std::string(64, 'a'), kClassKindGood } }),
              "UTF-8 names including the 64-byte limit");
    {
        SegClassTable maximum;
        for (int index = 0; index < kMaxClasses; ++index) {
            SegClassEntry entry;
            entry.name = std::string("C") + std::to_string(100 + index).substr(1) + std::string(61, 'x'); // exactly 64 bytes
            entry.kind = index == 4 ? kClassKindGood : kClassKindDefect;
            entry.enabled = index % 3 != 0;
            maximum.classes.push_back(entry);
        }
        const std::vector<unsigned char> bytes = roundTrip(maximum, "32 classes with 64-byte names");
        tally.Check(bytes.size() == kMaxClassTableBytes, "worst-case table is exactly 2192 bytes");
    }

    // Encode-side validation rejections: each must fail, explain why, and leave the output empty.
    auto expectInvalid = [&](const SegClassTable& table, const std::string& label) {
        std::vector<unsigned char> bytes(3, 7);
        std::string reason;
        const bool encoded = EncodeClassTable(table, bytes, reason);
        tally.Check(!encoded, label + " is rejected");
        tally.Check(!encoded && !reason.empty() && bytes.empty(), label + " reports a reason and clears the output");
    };
    expectInvalid(SegClassTable(), "empty table");
    {
        std::vector<std::pair<std::string, uint8_t>> many;
        for (int index = 0; index < kMaxClasses + 1; ++index) many.push_back({ "N" + std::to_string(index), kClassKindDefect });
        expectInvalid(MakeClassTable(many), "33 classes");
    }
    const std::vector<std::pair<std::string, std::string>> badNames = {
        { "empty name", std::string() },
        { "65-byte name", std::string(65, 'a') },
        { "embedded NUL", std::string("A\0B", 3) },
        { "newline", std::string("A\nB") },
        { "DEL", std::string("A\x7F") },
        { "C1 control U+0085", std::string("A\xC2\x85") },
        { "C1 control U+0080", std::string("\xC2\x80") },
        { "C1 control U+009F", std::string("\xC2\x9F") },
        { "lone continuation byte", std::string("\x80") },
        { "invalid lead byte 0xFF", std::string("\xFF") },
        { "overlong two-byte C0 80", std::string("A\xC0\x80") },
        { "overlong three-byte E0 80 80", std::string("\xE0\x80\x80") },
        { "truncated sequence", std::string("A\xE1\xBA") },
        { "UTF-16 surrogate", std::string("\xED\xA0\x80") },
        { "above U+10FFFF", std::string("\xF4\x90\x80\x80") },
    };
    for (const auto& badName : badNames) {
        expectInvalid(MakeClassTable({ { badName.second, kClassKindDefect } }), "name: " + badName.first);
    }
    expectInvalid(MakeClassTable({ { "A", kClassKindDefect }, { "A", kClassKindDefect } }), "duplicate names");
    expectInvalid(MakeClassTable({ { "A", 2 } }), "kind 2");
    expectInvalid(MakeClassTable({ { "A", kClassKindGood }, { "B", kClassKindGood } }), "two GOOD classes");
    expectInvalid(MakeClassTable({ { "A", kClassKindDefect } }, false), "no enabled class");

    // Decode-side rejections, starting from the valid legacy bytes (layout: magic 0-3, version 4-7, N 8-11,
    // class 1 at 12-17, class 2 at 18-23, CRC 24-27).
    auto fixCrc = [](std::vector<unsigned char>& bytes) {
        PutU32(bytes, bytes.size() - 4, ClassTableCrc32(bytes.data(), bytes.size() - 4));
    };
    auto expectDecodeFails = [&](const std::vector<unsigned char>& bytes, const std::string& label, const char* reasonText) {
        SegClassTable decoded = legacy; // must be cleared on failure
        std::string reason;
        const bool ok = DecodeClassTable(bytes.empty() ? nullptr : bytes.data(), bytes.size(), decoded, reason);
        tally.Check(!ok, label + " is rejected");
        tally.Check(ok || (!reason.empty() && decoded.classes.empty()), label + " reports a reason and clears the output");
        if (!ok && reasonText) tally.Check(Contains(reason, reasonText), label + " reason mentions \"" + reasonText + "\" (was: " + reason + ")");
    };
    struct Mutation {
        const char* label;
        const char* reasonText;
        bool recomputeCrc;
        std::function<void(std::vector<unsigned char>&)> apply;
    };
    const std::vector<Mutation> mutations = {
        { "bad signature", "signature", true, [](std::vector<unsigned char>& m) { m[0] = 'X'; } },
        { "table version 2", "version", true, [](std::vector<unsigned char>& m) { m[4] = 2; } },
        { "class count 0", "class count", true, [](std::vector<unsigned char>& m) { m[8] = 0; } },
        { "class count 33", "class count", true, [](std::vector<unsigned char>& m) { m[8] = 33; } },
        { "class count larger than the entries present", "truncated", true, [](std::vector<unsigned char>& m) { m[8] = 3; } },
        { "reserved flag bit", "reserved flag", true, [](std::vector<unsigned char>& m) { m[13] = 3; } },
        { "name length 0", "name length", true, [](std::vector<unsigned char>& m) { m[14] = 0; } },
        { "name length 65", "name length", true, [](std::vector<unsigned char>& m) { m[14] = 65; } },
        { "kind 2", "kind", true, [](std::vector<unsigned char>& m) { m[12] = 2; } },
        { "two GOOD classes", "GOOD", true, [](std::vector<unsigned char>& m) { m[12] = 1; } },
        { "duplicate names", "duplicate", true, [](std::vector<unsigned char>& m) { m[22] = 'N'; m[23] = 'G'; } },
        { "NUL in a name", "control", true, [](std::vector<unsigned char>& m) { m[16] = 0; } },
        { "no enabled class", "enabled", true, [](std::vector<unsigned char>& m) { m[13] = 0; m[19] = 0; } },
        { "flipped name byte (CRC mismatch)", "CRC", false, [](std::vector<unsigned char>& m) { FlipBits(m[16], 1); } },
        { "flipped CRC byte", "CRC", false, [](std::vector<unsigned char>& m) { FlipBits(m[27], 0x80); } },
        { "one byte missing", nullptr, false, [](std::vector<unsigned char>& m) { m.pop_back(); } },
        { "CRC missing", "truncated", false, [](std::vector<unsigned char>& m) { m.resize(24); } },
        { "15 bytes", "truncated", false, [](std::vector<unsigned char>& m) { m.resize(15); } },
        { "trailing byte", "trailing", false, [](std::vector<unsigned char>& m) { m.push_back(0); } },
        { "larger than 2192 bytes", "larger", false, [](std::vector<unsigned char>& m) { m.assign(2193, 0); } },
        { "empty input", "truncated", false, [](std::vector<unsigned char>& m) { m.clear(); } },
    };
    for (const Mutation& mutation : mutations) {
        std::vector<unsigned char> bytes = legacyBytes;
        mutation.apply(bytes);
        if (mutation.recomputeCrc) fixCrc(bytes);
        expectDecodeFails(bytes, std::string("table with ") + mutation.label, mutation.reasonText);
    }
    {
        // Enabled flags may be absent from a caller-supplied table: relaxed decoding accepts them, strict decoding does not.
        std::vector<unsigned char> bytes = legacyBytes;
        bytes[13] = 0;
        bytes[19] = 0;
        fixCrc(bytes);
        SegClassTable relaxed;
        tally.Check(DecodeClassTable(bytes.data(), bytes.size(), relaxed, error, false) && relaxed.classes.size() == 2 &&
                    !relaxed.classes[0].enabled && !relaxed.classes[1].enabled, "relaxed decode accepts a table with no enabled class");
    }

    // New exports on bare handles: no model, wrong profile, null handle.
    tally.Check(SEGAI_GetClassApiVersion() == 1, "SEGAI_GetClassApiVersion is 1");
    tally.Check(SEGAI_GetVersion() == 1, "SEGAI_GetVersion stays 1");
    tally.Check(SEGAI_TrainerSetClassTable(nullptr, legacyBytes.data(), static_cast<int32_t>(legacyBytes.size())) == -1, "SetClassTable(null handle) is -1");
    {
        uint8_t scratch[64] = {};
        tally.Check(SEGAI_InferGetClassTable(nullptr, scratch, 64) == -1, "InferGetClassTable(null handle) is -1");
    }
    {
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        tally.Check(infer != nullptr, "InferCreate");
        if (infer) {
            uint8_t scratch[64] = {};
            tally.Check(SEGAI_InferGetClassTable(infer.get(), nullptr, 0) == -4, "InferGetClassTable without a model is -4 (size query)");
            tally.Check(SEGAI_InferGetClassTable(infer.get(), scratch, 64) == -4, "InferGetClassTable without a model is -4 (buffer)");
        }
    }
    {
        void* trainer = SEGAI_TrainerCreate();
        tally.Check(trainer != nullptr, "TrainerCreate");
        if (trainer) {
            const int32_t size = static_cast<int32_t>(legacyBytes.size());
            tally.Check(SEGAI_TrainerSetClassTable(trainer, legacyBytes.data(), size) == -2, "SetClassTable on a profile 0 trainer is -2");
            tally.Check(SEGAI_TrainerSetProfile(trainer, 1, 12) == 0, "TrainerSetProfile(1)");
            tally.Check(SEGAI_TrainerSetClassTable(trainer, legacyBytes.data(), size) == -2, "SetClassTable on a profile 1 trainer is -2");
            SEGAI_TrainerDestroy(trainer);
        }
    }
    return tally.Finish("class-table-selftest (T1)");
}

// ---- EAI-POS P2: instance targets (--inst-targets-selftest, no model files) ---------------------------------

BeeSegAI::InstanceLabel MakeInstBox(double cx, double cy, double w, double h, double theta, int classIndex = 0)
{
    BeeSegAI::InstanceLabel label;
    label.kind = BeeSegAI::InstanceKind::RotatedBox;
    label.cx = cx; label.cy = cy; label.w = w; label.h = h; label.theta = theta; label.classIndex = classIndex;
    return label;
}

BeeSegAI::InstanceLabel MakeInstCircle(double cx, double cy, double radius, int classIndex = 0)
{
    BeeSegAI::InstanceLabel label;
    label.kind = BeeSegAI::InstanceKind::Circle;
    label.cx = cx; label.cy = cy; label.w = 2 * radius; label.h = 2 * radius; label.theta = 0; label.classIndex = classIndex;
    return label;
}

// Nearest decoded instance (same class) to a label centre; -1 when none.
int FindInstMatch(const std::vector<BeeSegAI::DecodedInstance>& decoded, const BeeSegAI::InstanceLabel& label, std::vector<char>& used)
{
    int best = -1;
    double bestDistance = 1e300;
    for (size_t i = 0; i < decoded.size(); ++i) {
        if (used[i] || decoded[i].classIndex != label.classIndex) continue;
        const double distance = std::hypot(decoded[i].cx - label.cx, decoded[i].cy - label.cy);
        if (distance < bestDistance) { bestDistance = distance; best = static_cast<int>(i); }
    }
    if (best >= 0) used[static_cast<size_t>(best)] = 1;
    return best;
}

void RunInstAffineRoundTrip(TestTally& tally)
{
    using namespace BeeSegAI;
    const double pi = kInstancePi;
    const std::vector<InstanceLabel> original = {
        MakeInstBox(310.5, 220.25, 271, 262, 0.35), MakeInstBox(900, 700, 120, 80, -2.9), MakeInstBox(500, 1200, 90, 90, pi),
        MakeInstBox(1500, 300, 60, 140, -pi + 0.01), MakeInstCircle(1200, 1000, 45) };

    struct Case { const char* name; Affine2x3 affine; };
    const Case cases[] = {
        { "rotate37-scale0.7", MakeRotationAffine(1224, 1024, 37, 0.7) },
        { "rotate-120-scale1.3", MakeRotationAffine(100, 50, -120, 1.3) },
        { "flipX", MakeFlipXAffine(2448) },
        { "flipX-then-rotate90", ComposeAffine(MakeFlipXAffine(2448), MakeRotationAffine(1224, 1024, 90, 1.0)) },
        { "rotate-then-flipX-then-letterbox-scale", ComposeAffine(ComposeAffine(MakeRotationAffine(1224, 1024, 15, 1.1), MakeFlipXAffine(2448)),
                                                                   MakeRotationAffine(0, 0, 0, 0.26)) },
    };
    for (const Case& c : cases) {
        bool invertible = false;
        const Affine2x3 inverse = InvertAffine(c.affine, &invertible);
        tally.Check(invertible, std::string(c.name) + ": invertible");
        std::vector<InstanceLabel> labels = original;
        tally.Check(ApplyAffineToLabels(c.affine, labels), std::string(c.name) + ": forward similarity accepted");
        tally.Check(ApplyAffineToLabels(inverse, labels), std::string(c.name) + ": inverse similarity accepted");
        for (size_t i = 0; i < labels.size(); ++i) {
            const std::string tag = std::string(c.name) + " label " + std::to_string(i);
            tally.Check(std::hypot(labels[i].cx - original[i].cx, labels[i].cy - original[i].cy) < 1e-3, tag + ": centre error < 1e-3 px");
            if (original[i].kind == InstanceKind::RotatedBox)
                tally.Check(std::fabs(WrapAngle(labels[i].theta - original[i].theta)) < 1e-4, tag + ": angle error < 1e-4 rad");
            tally.Check(std::fabs(labels[i].w - original[i].w) < 1e-4 * original[i].w && std::fabs(labels[i].h - original[i].h) < 1e-4 * original[i].h,
                        tag + ": size restored");
            tally.Check(labels[i].kind == original[i].kind && labels[i].classIndex == original[i].classIndex, tag + ": kind and class kept");
        }
    }

    // Closed-form expectations: cv rotation by +a degrees is counter-clockwise on screen, so theta drops by a; a mirror reflects theta.
    {
        std::vector<InstanceLabel> labels = { MakeInstBox(100, 100, 40, 20, 0.5) };
        tally.Check(ApplyAffineToLabels(MakeRotationAffine(100, 100, 30, 1.0), labels), "rotate: accepted");
        tally.Check(std::fabs(WrapAngle(labels[0].theta - (0.5 - 30 * pi / 180))) < 1e-9, "rotate +30 deg lowers theta by 30 deg");
        labels = { MakeInstBox(100, 100, 40, 20, 0.5) };
        tally.Check(ApplyAffineToLabels(MakeFlipXAffine(640), labels), "flip: accepted");
        tally.Check(std::fabs(WrapAngle(labels[0].theta - (pi - 0.5))) < 1e-9 && std::fabs(labels[0].cx - 540) < 1e-9, "flipX maps theta to pi - theta and cx to W - cx");
    }
    // Anisotropic maps are refused and leave the labels alone.
    {
        Affine2x3 stretch;
        stretch.m[0] = 2.0;
        std::vector<InstanceLabel> labels = { MakeInstBox(100, 100, 40, 20, 0.5) };
        tally.Check(!ApplyAffineToLabels(stretch, labels) && labels[0].cx == 100 && labels[0].w == 40, "anisotropic scale rejected, labels untouched");
    }
    // Letterbox of a 2448x2048 frame into 640: scale 640/2448, frame 640x535, top offset 52.
    {
        const Affine2x3 lb = MakeLetterboxAffine(2448, 2048, 640);
        tally.Check(lb.m[2] == 0 && lb.m[5] == 52, "letterbox 2448x2048 -> 640: left 0, top 52");
        std::vector<InstanceLabel> labels = { MakeInstBox(1224, 1024, 262, 262, 0) };
        tally.Check(ApplyAffineToLabels(lb, labels), "letterbox accepted");
        tally.Check(std::fabs(labels[0].cx - 320) < 1e-6 && std::fabs(labels[0].cy - (52 + 535 / 2.0)) < 0.5 && std::fabs(labels[0].w - 262 * 640.0 / 2448) < 0.1,
                    "letterbox maps centre and size");
    }
}

void RunInstSymmetryEncode(TestTally& tally)
{
    using namespace BeeSegAI;
    const double pi = kInstancePi;
    const double thetas[] = { 0.0, 0.4, -1.2, 1.9, 2.8, -2.9, pi, -pi + 1e-3, 0.7853981 };
    for (int k : { 1, 2, 4 }) {
        for (double theta : thetas) {
            InstanceTargets targets;
            RenderInstanceTargets({ MakeInstBox(324.0, 252.0, 100, 90, theta) }, 1, { k }, targets);
            std::vector<DecodedInstance> decoded;
            DecodeInstances(targets.heat.data(), targets.reg.data(), 1, { k }, DecodeOptions(), decoded);
            const std::string tag = "k=" + std::to_string(k) + " theta=" + std::to_string(theta);
            tally.Check(decoded.size() == 1, tag + ": one instance");
            if (decoded.size() != 1) continue;
            tally.Check(AngleDifferenceModuloSymmetry(decoded[0].theta, theta, k) < 1e-5, tag + ": orientation preserved modulo 360/k");
            tally.Check(std::fabs(decoded[0].theta) <= pi / k + 1e-6, tag + ": canonical representative within [-pi/k, pi/k]");
            tally.Check(std::fabs(decoded[0].cx - 324.0) < 1e-3 && std::fabs(decoded[0].w - 100) < 1e-3 && std::fabs(decoded[0].h - 90) < 1e-3, tag + ": centre and size exact");
            if (k == 1) tally.Check(std::fabs(WrapAngle(decoded[0].theta - theta)) < 1e-5, tag + ": k=1 keeps the full direction");
            // Equivalent orientations (theta + 2pi/k) encode to the same decoded angle.
            InstanceTargets shifted;
            RenderInstanceTargets({ MakeInstBox(324.0, 252.0, 100, 90, theta + 2 * pi / k) }, 1, { k }, shifted);
            std::vector<DecodedInstance> again;
            DecodeInstances(shifted.heat.data(), shifted.reg.data(), 1, { k }, DecodeOptions(), again);
            tally.Check(again.size() == 1 && AngleDifferenceModuloSymmetry(again[0].theta, decoded[0].theta, k) < 1e-5, tag + ": theta + 2pi/k decodes to the same orientation");
        }
    }
    // Circle: radius comes back as (w + h) / 4 and the angle is ignored.
    {
        InstanceTargets targets;
        RenderInstanceTargets({ MakeInstCircle(100.0, 100.0, 30) }, 1, {}, targets);
        std::vector<DecodedInstance> decoded;
        DecodeInstances(targets.heat.data(), targets.reg.data(), 1, {}, DecodeOptions(), decoded);
        tally.Check(decoded.size() == 1 && decoded[0].kind == InstanceKind::Circle && std::fabs(decoded[0].radius - 30) < 1e-3, "circle radius = (w+h)/4");
    }
}

void RunInstDecodeTouching(TestTally& tally)
{
    using namespace BeeSegAI;
    const double pi = kInstancePi;
    // Class 0: two near-square rotated boxes overlapping by about 20% of one box. Class 1: two circles that overlap slightly.
    const std::vector<InstanceLabel> labels = {
        MakeInstBox(200, 200, 100, 96, 0.20, 0), MakeInstBox(280, 205, 98, 100, -0.15, 0),
        MakeInstCircle(450, 450, 40, 1), MakeInstCircle(520, 450, 40, 1) };
    InstanceTargets targets;
    RenderInstanceTargets(labels, 2, {}, targets);
    tally.Check(targets.droppedLabels == 0, "touching: no label dropped");
    std::vector<DecodedInstance> decoded;
    DecodeInstances(targets.heat.data(), targets.reg.data(), 2, {}, DecodeOptions(), decoded);
    tally.Check(decoded.size() == labels.size(), "touching: exactly 4 instances (got " + std::to_string(decoded.size()) + ")");
    std::vector<char> used(decoded.size(), 0);
    for (size_t i = 0; i < labels.size(); ++i) {
        const int m = FindInstMatch(decoded, labels[i], used);
        tally.Check(m >= 0, "touching: label " + std::to_string(i) + " recovered");
        if (m < 0) continue;
        const DecodedInstance& d = decoded[static_cast<size_t>(m)];
        tally.Check(std::hypot(d.cx - labels[i].cx, d.cy - labels[i].cy) <= 4.0, "touching: label " + std::to_string(i) + " centre within 0.5 cell");
        tally.Check(d.kind == labels[i].kind, "touching: label " + std::to_string(i) + " kind");
        if (labels[i].kind == InstanceKind::RotatedBox) {
            tally.Check(std::fabs(WrapAngle(d.theta - labels[i].theta)) * 180 / pi <= 2.0, "touching: label " + std::to_string(i) + " angle within 2 deg");
            tally.Check(std::fabs(d.w - labels[i].w) <= 0.1 * labels[i].w && std::fabs(d.h - labels[i].h) <= 0.1 * labels[i].h, "touching: label " + std::to_string(i) + " size within 10%");
        } else {
            tally.Check(std::fabs(d.radius - labels[i].w / 2) <= 0.1 * labels[i].w / 2, "touching: label " + std::to_string(i) + " radius within 10%");
        }
    }
    // The pair of boxes really overlaps (guards the fixture): IoU well above 0 and below the NMS threshold.
    {
        DecodedInstance a, b;
        a.cx = labels[0].cx; a.cy = labels[0].cy; a.w = labels[0].w; a.h = labels[0].h; a.theta = labels[0].theta;
        b.cx = labels[1].cx; b.cy = labels[1].cy; b.w = labels[1].w; b.h = labels[1].h; b.theta = labels[1].theta;
        const double iou = InstanceIou(a, b);
        tally.Check(iou > 0.05 && iou < 0.5, "touching: fixture box IoU is in (0.05, 0.5): " + std::to_string(iou));
        tally.Check(std::fabs(InstanceIou(a, a) - 1.0) < 1e-9, "rotated IoU of a box with itself is 1");
        DecodedInstance half = a; half.cx += a.w / 2; half.theta = 0; a.theta = 0;
        tally.Check(std::fabs(InstanceIou(a, half) - 1.0 / 3.0) < 1e-9, "rotated IoU of half-shifted axis aligned boxes is 1/3");
    }
    // Duplicate peaks of one object (two close high cells) are merged by the rotated NMS.
    {
        std::vector<float> heat(static_cast<size_t>(kInstanceGrid) * kInstanceGrid, 0.f);
        std::vector<float> reg(static_cast<size_t>(kInstanceRegChannels) * kInstanceGrid * kInstanceGrid, 0.f);
        const size_t g2 = heat.size();
        for (int x : { 20, 24 }) {
            const size_t cell = static_cast<size_t>(30) * kInstanceGrid + x;
            heat[cell] = x == 20 ? 0.9f : 0.8f;
            reg[2 * g2 + cell] = std::log(12.f); reg[3 * g2 + cell] = std::log(12.f); reg[5 * g2 + cell] = 1.f;
            reg[0 * g2 + cell] = x == 20 ? 4.5f : 0.5f;   // both decode to cx = 24.5 * stride-ish, strongly overlapping
        }
        std::vector<DecodedInstance> decoded2;
        DecodeInstances(heat.data(), reg.data(), 1, {}, DecodeOptions(), decoded2);
        tally.Check(decoded2.size() == 1 && decoded2[0].score == 0.9f, "NMS keeps the stronger of two overlapping peaks");
    }
}

void RunInstLayoutRoundTrip(TestTally& tally)
{
    using namespace BeeSegAI;
    const double pi = kInstancePi;
    // Five ~270 px near-square objects in a 2448x2048 image (E:\EAI_TEST-like), two of them touching.
    const std::vector<InstanceLabel> original = {
        MakeInstBox(500, 500, 271, 262, 0.10), MakeInstBox(785, 520, 268, 264, -0.20), MakeInstBox(1300, 600, 270, 262, 1.00),
        MakeInstBox(1620, 640, 272, 260, 2.50), MakeInstBox(1000, 1400, 269, 263, -3.00) };
    const Affine2x3 letterbox = MakeLetterboxAffine(2448, 2048, 640);
    std::vector<InstanceLabel> net = original;
    tally.Check(ApplyAffineToLabels(letterbox, net), "layout: letterbox accepted");
    InstanceTargets targets;
    RenderInstanceTargets(net, 1, {}, targets);
    tally.Check(targets.droppedLabels == 0, "layout: nothing dropped");
    std::vector<DecodedInstance> decoded;
    DecodeInstances(targets.heat.data(), targets.reg.data(), 1, {}, DecodeOptions(), decoded);
    tally.Check(decoded.size() == original.size(), "layout: exactly 5 instances (got " + std::to_string(decoded.size()) + ")");

    // Back to the original image through the inverse letterbox.
    std::vector<InstanceLabel> back;
    for (const DecodedInstance& d : decoded) back.push_back(MakeInstBox(d.cx, d.cy, d.w, d.h, d.theta));
    tally.Check(ApplyAffineToLabels(InvertAffine(letterbox), back), "layout: inverse letterbox accepted");
    std::vector<char> used(back.size(), 0);
    std::vector<DecodedInstance> backDecoded;
    for (const InstanceLabel& b : back) { DecodedInstance d; d.cx = b.cx; d.cy = b.cy; d.w = b.w; d.h = b.h; d.theta = b.theta; backDecoded.push_back(d); }
    for (size_t i = 0; i < original.size(); ++i) {
        const int m = FindInstMatch(backDecoded, original[i], used);
        tally.Check(m >= 0, "layout: object " + std::to_string(i) + " recovered");
        if (m < 0) continue;
        const DecodedInstance& d = backDecoded[static_cast<size_t>(m)];
        tally.Check(std::hypot(d.cx - original[i].cx, d.cy - original[i].cy) <= 0.5, "layout: object " + std::to_string(i) + " centre within 0.5 px");
        tally.Check(std::fabs(WrapAngle(d.theta - original[i].theta)) * 180 / pi <= 0.5, "layout: object " + std::to_string(i) + " angle within 0.5 deg");
        tally.Check(std::fabs(d.w - original[i].w) <= 0.01 * original[i].w && std::fabs(d.h - original[i].h) <= 0.01 * original[i].h, "layout: object " + std::to_string(i) + " size within 1%");
    }
}

int RunInstTargetsSelfTest()
{
    TestTally tally;
    std::cout << "InstTargets_AffineRoundTrip\n";
    RunInstAffineRoundTrip(tally);
    std::cout << "InstTargets_SymmetryEncode_k1k2k4\n";
    RunInstSymmetryEncode(tally);
    std::cout << "InstDecode_TouchingPeaksSeparated\n";
    RunInstDecodeTouching(tally);
    std::cout << "InstDecode_LayoutRoundTrip (2448x2048, 5 objects, 640 letterbox)\n";
    RunInstLayoutRoundTrip(tally);
    return tally.Finish("inst-targets-selftest (EAI-POS P2)");
}

// ---- EAI-POS P4: quality refinement (--inst-refine-selftest, no model files) ---------------------------------------

// Anti-aliased synthetic object on the OpenCV pixel-centre convention: a rotated box (w x h, theta) or a disc (w = diameter),
// intensity 200 on background 40, 8 x 8 supersampling per pixel.
cv::Mat RenderInstRefineObject(int width, int height, double cx, double cy, double w, double h, double theta, bool disc)
{
    cv::Mat gray(height, width, CV_32FC1, cv::Scalar(40.0));
    const double ca = std::cos(theta);
    const double sa = std::sin(theta);
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            int inside = 0;
            for (int sy = 0; sy < 8; ++sy) {
                for (int sx = 0; sx < 8; ++sx) {
                    const double px = x + (sx + 0.5) / 8.0 - 0.5 - cx;
                    const double py = y + (sy + 0.5) / 8.0 - 0.5 - cy;
                    bool hit = false;
                    if (disc) {
                        hit = std::hypot(px, py) <= 0.5 * w;
                    } else {
                        const double bx = px * ca + py * sa;
                        const double by = -px * sa + py * ca;
                        hit = std::fabs(bx) <= 0.5 * w && std::fabs(by) <= 0.5 * h;
                    }
                    if (hit) ++inside;
                }
            }
            gray.at<float>(y, x) = static_cast<float>(40.0 + 160.0 * inside / 64.0);
        }
    }
    return gray;
}

// Class template of a box class captured from one canonical label (w x h at theta 0) on a 256 x 256 canvas, through the same
// AccumulateInstanceTemplate / FinalizeInstanceTemplates path the trainer uses.
BeeSegAI::InstanceTemplateSet BuildInstRefineTemplate(double w, double h, int symmetry = 1)
{
    using namespace BeeSegAI;
    const cv::Mat canonical = RenderInstRefineObject(256, 256, 128.0, 128.0, w, h, 0.0, false);
    cv::Mat sum = cv::Mat::zeros(1, kInstanceTemplateSize * kInstanceTemplateSize, CV_32FC1);
    std::vector<int> counts(1, 0);
    AccumulateInstanceTemplate(canonical, MakeInstBox(128.0, 128.0, w, h, 0.0), kInstanceTemplateSize, symmetry, sum, counts);
    InstanceTemplateSet set;
    FinalizeInstanceTemplates(sum, counts, set);
    return set;
}

// Intensity centroid of an S x S patch (patch pixels, weights = value - minimum). Used only for the P4 diagnostic.
cv::Point2d InstRefineCentroid(const cv::Mat& patch)
{
    double minV = 0.0;
    double maxV = 0.0;
    cv::minMaxLoc(patch, &minV, &maxV);
    const cv::Mat weights = patch - cv::Scalar::all(minV);
    const cv::Moments mo = cv::moments(weights, false);
    if (!(mo.m00 > 0.0)) return cv::Point2d(-1.0, -1.0);
    return cv::Point2d(mo.m10 / mo.m00, mo.m01 / mo.m00);
}

void RunInstRefineSynthSubPixel(TestTally& tally)
{
    using namespace BeeSegAI;
    const double pi = kInstancePi;
    const double centreOfPatch = 0.5 * (kInstanceTemplateSize - 1);
    // Truth = object pose in the synthetic image; the head estimate is off by (ex, ey) px and edeg degrees, plus phase degrees
    // (a 90 deg phase is the symmetry ambiguity of a k = 4 class, resolved by the symmetry NCC).
    struct BoxCase { double w, h, tx, ty, tdeg, ex, ey, edeg, phase; int k; const char* name; };
    const BoxCase cases[] = {
        { 60.0, 44.0, 128.37, 127.79, 1.3, 1.2, -0.9, 3.5, 0.0, 1, "box 60x44 k=1" },
        { 48.0, 48.0, 120.60, 133.20, -4.0, -1.5, 1.1, 2.5, 0.0, 1, "square 48 k=1" },
        { 48.0, 48.0, 130.25, 124.70, 2.0, -1.0, 0.8, 1.5, 90.0, 4, "square 48 k=4 head 90 deg off" },
        { 60.0, 44.0, 128.37, 127.79, 1.3, 1.2, -0.9, 3.5, 180.0, 2, "box 60x44 k=2 head 180 deg off" },
    };
    for (const BoxCase& c : cases) {
        const InstanceTemplateSet set = BuildInstRefineTemplate(c.w, c.h, c.k);
        tally.Check(set.size == kInstanceTemplateSize && set.Valid(1), std::string(c.name) + ": template set is valid");
        const double truth = c.tdeg * pi / 180.0;
        const cv::Mat target = RenderInstRefineObject(256, 256, c.tx, c.ty, c.w, c.h, truth, false);
        DecodedInstance d;
        d.kind = InstanceKind::RotatedBox;
        d.cx = c.tx + c.ex;
        d.cy = c.ty + c.ey;
        d.w = c.w;
        d.h = c.h;
        d.theta = truth + (c.edeg + c.phase) * pi / 180.0;
        d.classIndex = 0;
        d.score = 0.9f;
        const double initialError = std::hypot(d.cx - c.tx, d.cy - c.ty);
        const double initialAngle = AngleDifferenceModuloSymmetry(d.theta, truth, c.k) * 180.0 / pi;
        // Diagnostic: centroid of the template row and of the target patch at the truth pose, offsets from the patch centre
        // (patch pixels). A constant offset here would bias every refinement by the same amount.
        cv::Mat truthPatch;
        const bool truthSampled = SampleInstancePatch(target, c.tx, c.ty, InstancePatchSide(c.w, c.h), truth, kInstanceTemplateSize, truthPatch) && NormalizePatch(truthPatch);
        const cv::Point2d templateCentroid = InstRefineCentroid(set.data.row(0).clone().reshape(1, kInstanceTemplateSize));
        const cv::Point2d targetCentroid = truthSampled ? InstRefineCentroid(truthPatch) : cv::Point2d(-1.0, -1.0);
        std::vector<std::string> trace;
        const bool ok = RefineInstanceTraced(target, set, c.k, d, trace);
        const double refinedError = std::hypot(d.cx - c.tx, d.cy - c.ty);
        const double refinedAngle = AngleDifferenceModuloSymmetry(d.theta, truth, c.k) * 180.0 / pi;
        std::cout << "DIAG " << c.name << ": initial centre err " << initialError << " px, angle err " << initialAngle
                  << " deg; refined " << (ok ? "yes" : "no") << " centre err " << refinedError << " px, angle err " << refinedAngle
                  << " deg; template centroid offset (" << templateCentroid.x - centreOfPatch << "," << templateCentroid.y - centreOfPatch
                  << ") patch px; target centroid offset (" << targetCentroid.x - centreOfPatch << "," << targetCentroid.y - centreOfPatch
                  << ") patch px; patch scale " << InstancePatchSide(c.w, c.h) / kInstanceTemplateSize << " px/patch px\n";
        for (const std::string& line : trace) std::cout << "DIAG   " << line << "\n";
        tally.Check(ok && d.refined == 1, std::string(c.name) + ": refinement succeeds");
        if (!ok) continue;
        const double centreError = refinedError;
        const double angleError = refinedAngle;
        tally.Check(centreError < 0.3, std::string(c.name) + ": centre error " + std::to_string(centreError) + " px < 0.3");
        tally.Check(angleError < 0.2, std::string(c.name) + ": angle error " + std::to_string(angleError) + " deg < 0.2");
        tally.Check(d.w == c.w && d.h == c.h, std::string(c.name) + ": box size kept");
    }
    // Disc: centre and radius from the caliper, head 3.7 px off in the centre and 2 px short in the radius.
    {
        const cv::Mat target = RenderInstRefineObject(256, 256, 100.4, 90.6, 60.0, 60.0, 0.0, true);
        DecodedInstance d;
        d.kind = InstanceKind::Circle;
        d.cx = 103.0;
        d.cy = 88.0;
        d.w = 56.0;
        d.h = 56.0;
        d.radius = 14.0;
        d.classIndex = 0;
        d.score = 0.9f;
        const bool ok = RefineInstance(target, InstanceTemplateSet(), 1, d);
        tally.Check(ok && d.refined == 1, "circle d=60: refinement succeeds without templates");
        if (ok) {
            const double centreError = std::hypot(d.cx - 100.4, d.cy - 90.6);
            tally.Check(centreError < 0.3, "circle d=60: centre error " + std::to_string(centreError) + " px < 0.3");
            tally.Check(std::fabs(d.radius - 30.0) < 0.3 && std::fabs(d.w - 60.0) < 0.6 && std::fabs(d.h - 60.0) < 0.6,
                        "circle d=60: radius error " + std::to_string(std::fabs(d.radius - 30.0)) + " px < 0.3");
        }
    }
}

void RunInstRefineFallback(TestTally& tally)
{
    using namespace BeeSegAI;
    const InstanceTemplateSet set = BuildInstRefineTemplate(60.0, 44.0);
    const cv::Mat object = RenderInstRefineObject(256, 256, 128.0, 128.0, 60.0, 44.0, 0.0, false);
    const cv::Mat flat(256, 256, CV_32FC1, cv::Scalar(90.0));
    const auto makeBox = []() {
        DecodedInstance d;
        d.kind = InstanceKind::RotatedBox;
        d.cx = 128.5;
        d.cy = 127.5;
        d.w = 60.0;
        d.h = 44.0;
        d.theta = 0.02;
        d.classIndex = 0;
        d.score = 0.8f;
        return d;
    };
    const auto unchanged = [](const DecodedInstance& d) {
        return d.cx == 128.5 && d.cy == 127.5 && d.w == 60.0 && d.h == 44.0 && d.theta == 0.02 && d.refined == 0;
    };
    // A: no templates at all (legacy or head without templates): the box keeps the head result.
    {
        DecodedInstance d = makeBox();
        tally.Check(!RefineInstance(object, InstanceTemplateSet(), 1, d) && unchanged(d), "fallback: no templates keep the head box, refined = 0");
    }
    // B: the square leaves the image (head centre 5 px from the border).
    {
        DecodedInstance d = makeBox();
        d.cx = 5.0;
        d.cy = 5.0;
        tally.Check(!RefineInstance(object, set, 1, d) && d.refined == 0 && d.cx == 5.0 && d.cy == 5.0, "fallback: square outside the image keeps the head box");
    }
    // C: flat image (no structure to correlate or to align).
    {
        DecodedInstance d = makeBox();
        tally.Check(!RefineInstance(flat, set, 1, d) && unchanged(d), "fallback: flat image keeps the head box");
    }
    // D: class without a template row (classIndex outside the set, or an all-zero row).
    {
        DecodedInstance d = makeBox();
        d.classIndex = 1;
        tally.Check(!RefineInstance(object, set, 1, d) && d.refined == 0 && d.theta == 0.02, "fallback: class index without template row");
        InstanceTemplateSet zero;
        zero.size = kInstanceTemplateSize;
        zero.data = cv::Mat::zeros(1, kInstanceTemplateSize * kInstanceTemplateSize, CV_32FC1);
        DecodedInstance e = makeBox();
        tally.Check(!RefineInstance(object, zero, 1, e) && unchanged(e), "fallback: all-zero template row keeps the head box");
    }
    // E: circle on a flat image has no edges to caliper.
    {
        DecodedInstance d = makeBox();
        d.kind = InstanceKind::Circle;
        d.w = 60.0;
        d.h = 60.0;
        // Compare with the values before the call (the circle sets w = h = 60; unchanged() is for the box makeBox() and must not be used here).
        const DecodedInstance before = d;
        const bool failed = !RefineInstance(flat, InstanceTemplateSet(), 1, d);
        const bool same = d.cx == before.cx && d.cy == before.cy && d.w == before.w && d.h == before.h && d.theta == before.theta && d.refined == 0;
        tally.Check(failed && same,
                    "fallback: circle without edges keeps the head circle (before cx " + std::to_string(before.cx) + " cy " + std::to_string(before.cy) +
                        " w " + std::to_string(before.w) + " h " + std::to_string(before.h) + " theta " + std::to_string(before.theta) +
                        "; after refined " + std::to_string(d.refined) + " cx " + std::to_string(d.cx) + " cy " + std::to_string(d.cy) +
                        " w " + std::to_string(d.w) + " h " + std::to_string(d.h) + " theta " + std::to_string(d.theta) + ")");
    }
    // F: a stale refined = 1 from the caller is cleared when the refinement fails.
    {
        DecodedInstance d = makeBox();
        d.refined = 1;
        tally.Check(!RefineInstance(flat, set, 1, d) && d.refined == 0, "fallback: refined flag is reset to 0 on failure");
    }
    // G: the template validator rejects a wrong width.
    {
        InstanceTemplateSet bad = set;
        bad.size = 16;
        tally.Check(!bad.Valid(1) && set.Valid(1), "template set: wrong size is rejected");
    }
}

// A head that is already exact must not be worsened: either refined with a pose within 0.05 px / 0.05 deg, or left untouched.
void RunInstRefineExactHead(TestTally& tally)
{
    using namespace BeeSegAI;
    const double pi = kInstancePi;
    const double truth = 1.3 * pi / 180.0;
    const InstanceTemplateSet set = BuildInstRefineTemplate(60.0, 44.0, 1);
    const cv::Mat target = RenderInstRefineObject(256, 256, 128.37, 127.79, 60.0, 44.0, truth, false);
    DecodedInstance d;
    d.kind = InstanceKind::RotatedBox;
    d.cx = 128.37;
    d.cy = 127.79;
    d.w = 60.0;
    d.h = 44.0;
    d.theta = truth;
    d.classIndex = 0;
    d.score = 0.9f;
    const DecodedInstance before = d;
    const bool ok = RefineInstance(target, set, 1, d);
    const double centreError = std::hypot(d.cx - 128.37, d.cy - 127.79);
    const double angleError = std::fabs(WrapAngle(d.theta - truth)) * 180.0 / pi;
    std::cout << "DIAG exact head: refined " << (ok ? "yes" : "no") << " centre err " << centreError << " px, angle err " << angleError << " deg\n";
    tally.Check(!ok || (centreError <= 0.05 && angleError <= 0.05),
                "exact head: refinement must not worsen it (centre " + std::to_string(centreError) + " px, angle " + std::to_string(angleError) + " deg)");
    tally.Check(ok || (d.cx == before.cx && d.cy == before.cy && d.theta == before.theta && d.refined == 0), "exact head: a failed refinement leaves the head unchanged");
}

// Noisy object (Gaussian noise, sigma 8 gray levels) with heads of the right and of an off size. refined = 1 must come with a moved
// pose, a failure must leave the head and the box size unchanged.
void RunInstRefineNoisyOffSize(TestTally& tally)
{
    using namespace BeeSegAI;
    const double pi = kInstancePi;
    const double truth = 1.3 * pi / 180.0;
    const InstanceTemplateSet set = BuildInstRefineTemplate(60.0, 44.0, 1);
    cv::Mat target = RenderInstRefineObject(256, 256, 128.37, 127.79, 60.0, 44.0, truth, false);
    std::mt19937 random(7);
    std::normal_distribution<float> noise(0.0f, 8.0f);
    for (int y = 0; y < target.rows; ++y) {
        for (int x = 0; x < target.cols; ++x) target.at<float>(y, x) += noise(random);
    }
    struct Head { double dx, dy, dthetaDeg, w, h; const char* name; };
    const Head heads[] = {
        { 1.0, -0.7, 2.0, 60.0, 44.0, "noisy, exact size" },
        { 1.0, -0.7, 2.0, 51.0, 35.2, "noisy, off-size 0.85 x 0.8" },
        { -2.5, 1.5, -6.0, 66.0, 40.0, "noisy, off-size 1.1 x 0.9, far head" },
    };
    for (const Head& hd : heads) {
        DecodedInstance d;
        d.kind = InstanceKind::RotatedBox;
        d.cx = 128.37 + hd.dx;
        d.cy = 127.79 + hd.dy;
        d.w = hd.w;
        d.h = hd.h;
        d.theta = truth + hd.dthetaDeg * pi / 180.0;
        d.classIndex = 0;
        d.score = 0.9f;
        const DecodedInstance before = d;
        const bool ok = RefineInstance(target, set, 1, d);
        const bool moved = d.cx != before.cx || d.cy != before.cy || d.theta != before.theta;
        std::cout << "DIAG " << hd.name << ": refined " << (ok ? "yes" : "no") << " pose (" << d.cx << "," << d.cy << "," << d.theta * 180.0 / pi
                  << " deg) centre err " << std::hypot(d.cx - 128.37, d.cy - 127.79) << " px\n";
        tally.Check(!(ok && d.refined == 1 && !moved), std::string(hd.name) + ": refined = 1 must come with a moved pose");
        tally.Check(ok || (d.refined == 0 && !moved), std::string(hd.name) + ": a failure leaves the head unchanged");
        tally.Check(d.w == before.w && d.h == before.h, std::string(hd.name) + ": w and h are not changed by the refinement");
    }
}

// Template keys: the names and types used by SegV2Model Save/Load round-trip through a FileStorage (YAML), and a set without the keys
// (old model) reads back as empty.
void RunInstRefineTemplateKeys(TestTally& tally)
{
    using namespace BeeSegAI;
    const InstanceTemplateSet set = BuildInstRefineTemplate(60.0, 44.0, 1);
    cv::FileStorage writer(".yml", cv::FileStorage::WRITE | cv::FileStorage::MEMORY);
    writer << "instance_template_size" << set.size << "instance_templates" << set.data;
    const std::string text = writer.releaseAndGetString();
    cv::FileStorage reader(text, cv::FileStorage::READ | cv::FileStorage::MEMORY);
    InstanceTemplateSet loaded;
    reader["instance_template_size"] >> loaded.size;
    reader["instance_templates"] >> loaded.data;
    tally.Check(loaded.size == set.size && !loaded.data.empty(), "template keys: size and matrix read back");
    tally.Check(loaded.data.size() == set.data.size() && cv::norm(loaded.data - set.data, cv::NORM_INF) < 1e-5,
                "template keys: values round-trip within 1e-5");
    tally.Check(loaded.Valid(1), "template keys: loaded set is valid");
    cv::FileStorage old(".yml", cv::FileStorage::WRITE | cv::FileStorage::MEMORY);
    old << "instance_head" << 1;
    const std::string oldText = old.releaseAndGetString();
    cv::FileStorage oldReader(oldText, cv::FileStorage::READ | cv::FileStorage::MEMORY);
    InstanceTemplateSet absent;
    if (!oldReader["instance_templates"].empty()) oldReader["instance_templates"] >> absent.data;
    tally.Check(absent.Empty(), "template keys: a file without the keys has no templates (refinement falls back)");
}

// An old model (no instance head, no template keys) still loads and the instance API reports no head.
void RunInstRefineOldModel(TestTally& tally)
{
    const std::filesystem::path modelPath = std::filesystem::path(__FILE__).parent_path() / "data" / "out_model.segai";
    if (!std::filesystem::exists(modelPath)) {
        std::cout << "SKIP old model load: " << modelPath.string() << " not found\n";
        return;
    }
    void* infer = SEGAI_InferCreate();
    tally.Check(infer != nullptr, "old model: InferCreate");
    if (!infer) return;
    const int32_t rc = SEGAI_InferLoad(infer, modelPath.wstring().c_str());
    tally.Check(rc == 0, "old model: loads (rc " + std::to_string(rc) + ")");
    if (rc == 0) {
        tally.Check(SEGAI_InferHasInstanceHead(infer) == 0, "old model: no instance head");
        cv::Mat bgr(8, 8, CV_8UC3, cv::Scalar(0, 0, 0));
        SegInstanceResultV1 item{};
        int32_t count = -1;
        const int32_t pr = SEGAI_InferPredictInstances(infer, bgr.data, 8, 8, static_cast<int32_t>(bgr.step), 0, 0, 0, 0, 0.3f, 1, &item, 1, &count);
        tally.Check((pr == -11 || pr == -9) && count == 0, "old model: instance predict (quality 1) reports no head (rc " + std::to_string(pr) + ")");
    }
    SEGAI_InferDestroy(infer);
}

int RunInstRefineSelfTest()
{
    TestTally tally;
    std::cout << "InstRefine_SynthSubPixel\n";
    RunInstRefineSynthSubPixel(tally);
    std::cout << "InstRefine_FallbackFlag\n";
    RunInstRefineFallback(tally);
    std::cout << "InstRefine_ExactHead\n";
    RunInstRefineExactHead(tally);
    std::cout << "InstRefine_NoisyOffSize\n";
    RunInstRefineNoisyOffSize(tally);
    std::cout << "InstRefine_TemplateKeys\n";
    RunInstRefineTemplateKeys(tally);
    std::cout << "InstRefine_OldModelLoad\n";
    RunInstRefineOldModel(tally);
    return tally.Finish("inst-refine-selftest (EAI-POS P4)");
}

// ---- T2 ----------------------------------------------------------------------------------------------------

// Same synthetic masks as --baseline-ext training: 0 background, 1 NG blobs, 2 OK blob.
cv::Mat MakeTinyMask(const cv::Mat& img)
{
    const int w = img.cols, h = img.rows;
    cv::Mat mask = cv::Mat(img.size(), CV_8UC1, cv::Scalar(0));
    const int ngSize1 = std::max(20, (w + h) / 30);
    cv::Rect ngBlob1(w / 10, h / 10, ngSize1, ngSize1);
    ngBlob1 = ngBlob1 & cv::Rect(0, 0, w, h);
    cv::rectangle(mask, ngBlob1, cv::Scalar(1), cv::FILLED);
    const int ngSize2 = std::max(15, (w + h) / 40);
    cv::Rect ngBlob2(w - ngSize2 - w / 8, h - ngSize2 - h / 8, ngSize2, ngSize2);
    ngBlob2 = ngBlob2 & cv::Rect(0, 0, w, h);
    cv::rectangle(mask, ngBlob2, cv::Scalar(1), cv::FILLED);
    const int okSize = std::max(25, (w + h) / 25);
    cv::Rect okBlob(w / 2 - okSize / 2, h / 2 - okSize / 2, okSize, okSize);
    okBlob = okBlob & cv::Rect(0, 0, w, h);
    cv::rectangle(mask, okBlob, cv::Scalar(2), cv::FILLED);
    return mask;
}

// Trains the tiny deterministic V2 model of --baseline-ext (epochs 1, side 160) and saves it to modelPath.
// tableBytes != nullptr: the trainer is also exercised with bad class tables and then given *tableBytes.
bool TrainTinyV2Model(TestTally& tally, const std::string& label, const std::filesystem::path& runDir,
                      const std::filesystem::path& backbonePath, const std::filesystem::path& fixtureDir,
                      const std::vector<unsigned char>* tableBytes, const std::filesystem::path& modelPath,
                      const std::filesystem::path& resumeFrom = std::filesystem::path())
{
    std::vector<cv::Mat> images, masks;
    for (int index = 0; index < 2; ++index) {
        cv::Mat image = cv::imread((fixtureDir / (std::to_string(index) + ".png")).u8string(), cv::IMREAD_COLOR);
        if (!image.empty()) images.push_back(image);
    }
    if (images.empty()) {
        tally.Check(false, label + ": fixture images 0.png/1.png found in " + fixtureDir.u8string());
        return false;
    }
    for (const cv::Mat& image : images) masks.push_back(MakeTinyMask(image));

    std::error_code ec;
    const std::filesystem::path logsDir = runDir / "v2_logs";
    std::filesystem::create_directories(logsDir, ec);
    std::string logsText = std::filesystem::absolute(logsDir).u8string();
    std::string backboneText = std::filesystem::absolute(backbonePath).u8string();
    for (char& c : logsText) if (c == '\\') c = '/';
    for (char& c : backboneText) if (c == '\\') c = '/';
    const std::filesystem::path settingsPath = runDir / "settings.json";
    {
        std::string resumeText; // optional Retrain source (settings key resumeFrom)
        if (!resumeFrom.empty()) {
            std::string resumeSource = std::filesystem::absolute(resumeFrom).u8string();
            for (char& c : resumeSource) if (c == '\\') c = '/';
            resumeText = ",\"resumeFrom\":\"" + resumeSource + "\"";
        }
        const std::string content = "{\"backbone\":\"" + backboneText + "\",\"logs\":\"" + logsText + "\","
            "\"epochs\":1,\"side\":160,\"learningRate\":0.1,\"regularization\":0.0,\"rotation\":0.0,"
            "\"scaleVariation\":0.0,\"brightness\":0.0,\"augment\":0,\"gpu\":0,\"validation\":[]" + resumeText + "}";
        std::ofstream settings(settingsPath, std::ios::binary);
        settings.write(content.c_str(), static_cast<std::streamsize>(content.size()));
    }

    void* trainer = SEGAI_TrainerCreate();
    if (!trainer) {
        tally.Check(false, label + ": TrainerCreate");
        return false;
    }
    bool ok = false;
    do {
        const std::wstring settingsWide = settingsPath.wstring();
        const int configureRc = SEGAI_TrainerConfigureV2(trainer, settingsWide.c_str());
        tally.Check(configureRc == 0, label + ": ConfigureV2 rc=" + std::to_string(configureRc) + " " + TrainerErrorText(trainer));
        if (configureRc != 0) break;

        const int32_t tableSize = tableBytes ? static_cast<int32_t>(tableBytes->size()) : 0;
        if (tableBytes) {
            std::vector<unsigned char> garbage(10, 0x5A);
            tally.Check(SEGAI_TrainerSetClassTable(trainer, garbage.data(), 10) == -3 && !TrainerErrorText(trainer).empty(),
                        label + ": garbage table is -3 with a lastError");
            tally.Check(SEGAI_TrainerSetClassTable(trainer, nullptr, tableSize) == -3, label + ": null table is -3");
            tally.Check(SEGAI_TrainerSetClassTable(trainer, tableBytes->data(), 0) == -3, label + ": zero length table is -3");
            std::vector<unsigned char> corrupt = *tableBytes;
            FlipBits(corrupt.back(), 0x01);
            tally.Check(SEGAI_TrainerSetClassTable(trainer, corrupt.data(), tableSize) == -3 && Contains(TrainerErrorText(trainer), "CRC"),
                        label + ": corrupt CRC table is -3 mentioning CRC");
            std::vector<unsigned char> unsupported;
            std::string reason;
            BeeSegAI::EncodeClassTable(MakeClassTable({ { "A", BeeSegAI::kClassKindDefect }, { "B", BeeSegAI::kClassKindDefect },
                                                        { "C", BeeSegAI::kClassKindGood } }), unsupported, reason);
            // EAI-CLS S2b: every codec-valid table is trainable now (any N 1..32, any kind order); the last accepted one is replaced below.
            tally.Check(SEGAI_TrainerSetClassTable(trainer, unsupported.data(), static_cast<int32_t>(unsupported.size())) == 0,
                        label + ": 3-class table is accepted since S2b");
            BeeSegAI::EncodeClassTable(MakeClassTable({ { "A", BeeSegAI::kClassKindDefect }, { "B", BeeSegAI::kClassKindDefect } }), unsupported, reason);
            tally.Check(SEGAI_TrainerSetClassTable(trainer, unsupported.data(), static_cast<int32_t>(unsupported.size())) == 0,
                        label + ": DEFECT+DEFECT table is accepted since S2b");
            BeeSegAI::EncodeClassTable(MakeClassTable({ { "A", BeeSegAI::kClassKindGood }, { "B", BeeSegAI::kClassKindDefect } }), unsupported, reason);
            tally.Check(SEGAI_TrainerSetClassTable(trainer, unsupported.data(), static_cast<int32_t>(unsupported.size())) == 0,
                        label + ": GOOD+DEFECT table is accepted since S2b");
            tally.Check(SEGAI_TrainerSetClassTable(trainer, tableBytes->data(), tableSize) == 0 && TrainerErrorText(trainer).empty(),
                        label + ": valid table is accepted");
        }

        int sampleRc = 0;
        for (size_t index = 0; index < images.size() && sampleRc == 0; ++index) {
            sampleRc = SEGAI_TrainerSetROI(trainer, 0, 0, images[index].cols, images[index].rows);
            if (sampleRc == 0) {
                sampleRc = SEGAI_TrainerAddSample(trainer, images[index].data, images[index].cols, images[index].rows,
                                                  static_cast<int>(images[index].step), masks[index].data, static_cast<int>(masks[index].step));
            }
        }
        tally.Check(sampleRc == 0, label + ": AddSample rc=" + std::to_string(sampleRc) + " " + TrainerErrorText(trainer));
        if (sampleRc != 0) break;

        // The table can no longer be set (or replaced) once samples exist.
        std::vector<unsigned char> late;
        std::string reason;
        BeeSegAI::EncodeClassTable(BeeSegAI::MakeLegacyClassTable(), late, reason);
        tally.Check(SEGAI_TrainerSetClassTable(trainer, late.data(), static_cast<int32_t>(late.size())) == -2,
                    label + ": SetClassTable after AddSample is -2");

        const int trainRc = SEGAI_TrainerTrain(trainer, 0, 0, 0, nullptr, nullptr, nullptr);
        tally.Check(trainRc == 0, label + ": Train rc=" + std::to_string(trainRc) + " " + TrainerErrorText(trainer));
        if (trainRc != 0) break;
        const std::wstring modelWide = modelPath.wstring();
        const int saveRc = SEGAI_TrainerSave(trainer, modelWide.c_str(), 0.5f, 40);
        tally.Check(saveRc == 0, label + ": Save rc=" + std::to_string(saveRc) + " " + TrainerErrorText(trainer));
        ok = saveRc == 0;
    } while (false);
    SEGAI_TrainerDestroy(trainer);
    return ok;
}

bool PredictFullImage(const std::filesystem::path& modelPath, const cv::Mat& image, std::vector<unsigned char>& mask,
                      std::vector<unsigned char>& confidence, float& score, std::string& error)
{
    std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
    if (!infer) {
        error = "InferCreate failed";
        return false;
    }
    const std::wstring wide = modelPath.wstring();
    if (SEGAI_InferLoad(infer.get(), wide.c_str()) != 0) {
        error = "InferLoad: " + InferErrorText(infer.get());
        return false;
    }
    SEGAI_InferSetGpu(infer.get(), 0);
    uint8_t* maskPtr = nullptr;
    int32_t width = 0, height = 0;
    score = 0.f;
    const int rc = SEGAI_InferPredict(infer.get(), image.data, image.cols, image.rows, static_cast<int>(image.step), 0, 0,
                                      image.cols, image.rows, 0.5f, &maskPtr, &width, &height, &score);
    std::unique_ptr<uint8_t, SegaiBufferDeleter> maskOwner(maskPtr);
    if (rc != 0 || !maskPtr) {
        error = "InferPredict rc=" + std::to_string(rc) + " " + InferErrorText(infer.get());
        return false;
    }
    mask.assign(maskPtr, maskPtr + static_cast<size_t>(width) * static_cast<size_t>(height));
    uint8_t* confidencePtr = nullptr;
    int32_t confidenceWidth = 0, confidenceHeight = 0;
    const int confidenceRc = SEGAI_InferGetConfidence(infer.get(), &confidencePtr, &confidenceWidth, &confidenceHeight);
    std::unique_ptr<uint8_t, SegaiBufferDeleter> confidenceOwner(confidencePtr);
    confidence.clear();
    if (confidenceRc == 0 && confidencePtr) {
        confidence.assign(confidencePtr, confidencePtr + static_cast<size_t>(confidenceWidth) * static_cast<size_t>(confidenceHeight));
    }
    return true;
}

int RunClassTableModelMode(int argc, char** argv)
{
    if (argc < 3) {
        std::cerr << "Usage: --class-table-model <outDir> [<backbone.onnx>|-] [<fixtureDir>|-] [<expectedV3Sha256>]\n";
        return 100;
    }
    const std::filesystem::path outDir = std::filesystem::u8path(argv[2]);
    std::filesystem::path backbonePath;
    if (argc > 3 && std::strcmp(argv[3], "-") != 0) {
        backbonePath = std::filesystem::u8path(argv[3]);
    } else {
        wchar_t exePath[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        backbonePath = std::filesystem::path(exePath).parent_path() / "Models" / "DefectV2" / "mobilenetv2-12.onnx";
    }
    const std::filesystem::path fixtureDir = (argc > 4 && std::strcmp(argv[4], "-") != 0)
        ? std::filesystem::u8path(argv[4]) : std::filesystem::u8path("tmp/EAI/EAI-3/fixture-src");
    const std::string expectedSha = argc > 5 ? std::string(argv[5]) : std::string();
    if (!std::filesystem::exists(backbonePath)) {
        std::cerr << "V2 backbone is missing: " << backbonePath.u8string() << "\n";
        return 102;
    }
    std::error_code ec;
    std::filesystem::create_directories(outDir, ec);
    if (ec) {
        std::cerr << "Failed to create outDir\n";
        return 102;
    }

    using namespace BeeSegAI;
    TestTally tally;

    // The caller-supplied table names the classes; its enabled flags must be ignored (class 1 is passed as disabled).
    SegClassTable supplied = MakeClassTable({ { "NG", kClassKindDefect }, { "OK", kClassKindGood } });
    supplied.classes[0].enabled = false;
    std::vector<unsigned char> suppliedBytes;
    std::string error;
    tally.Check(EncodeClassTable(supplied, suppliedBytes, error), "supplied table encodes: " + error);
    SegClassTable expectedStored = MakeClassTable({ { "NG", kClassKindDefect }, { "OK", kClassKindGood } }); // both flags set from the training data
    std::vector<unsigned char> expectedStoredBytes;
    tally.Check(EncodeClassTable(expectedStored, expectedStoredBytes, error), "expected stored table encodes: " + error);
    if (suppliedBytes.empty() || expectedStoredBytes.empty()) return tally.Finish("class-table-model (T2)");

    const std::filesystem::path plainModel = outDir / "no-table.segai";
    const std::filesystem::path tableModel = outDir / "with-table.segai";
    const bool plainTrained = TrainTinyV2Model(tally, "no table", outDir / "no-table", backbonePath, fixtureDir, nullptr, plainModel);
    const bool tableTrained = TrainTinyV2Model(tally, "with table", outDir / "with-table", backbonePath, fixtureDir, &suppliedBytes, tableModel);
    if (!plainTrained || !tableTrained) return tally.Finish("class-table-model (T2)");

    // A trainer that never receives a table writes the unchanged version 3 file.
    const std::vector<unsigned char> plainFile = ReadFileBytes(plainModel);
    CheckSegaiLayout(tally, "no-table model", plainFile, nullptr);
    {
        const std::string plainSha = ComputeSha256(plainFile);
        std::cout << "no_table_size=" << plainFile.size() << "\nno_table_sha256=" << plainSha << "\n";
        // Byte identity with the recorded v3 baseline is NOT checked here: the network_xml node-name counter differs
        // between processes that ran inference before training and this one (training first), so only the layout,
        // the load round trip and the identical-inference checks below are asserted. v3 bit identity is proven by the
        // fresh-process --baseline-ext train gate (SHA256 compare of train-run.segai).
        if (!expectedSha.empty()) std::cout << "no_table_matches_expected_sha=" << (plainSha == expectedSha ? "true" : "false") << " (informational)\n";
    }

    // A trainer that received a table writes version 4 with the table after the payload.
    const std::vector<unsigned char> tableFile = ReadFileBytes(tableModel);
    CheckSegaiLayout(tally, "with-table model", tableFile, &expectedStoredBytes);
    std::cout << "with_table_size=" << tableFile.size() << "\n";

    // Reload both and read the tables back through the new export.
    {
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        const std::wstring wide = tableModel.wstring();
        tally.Check(infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0, "v4 model loads: " + (infer ? InferErrorText(infer.get()) : std::string()));
        if (infer) {
            std::vector<unsigned char> bytes;
            const int32_t required = QueryClassTable(infer.get(), bytes);
            tally.Check(required == static_cast<int32_t>(expectedStoredBytes.size()), "InferGetClassTable reports the stored table size");
            tally.Check(bytes == expectedStoredBytes, "InferGetClassTable returns the stored table bytes");
            SegClassTable decoded;
            tally.Check(!bytes.empty() && DecodeClassTable(bytes.data(), bytes.size(), decoded, error) && SameClassTable(decoded, expectedStored),
                        "returned table decodes to NG (DEFECT) / OK (GOOD) with both classes enabled: " + error);
            if (required > 1) {
                std::vector<unsigned char> small(static_cast<size_t>(required), 0xAAu);
                tally.Check(SEGAI_InferGetClassTable(infer.get(), small.data(), required - 1) == required, "too-small capacity still returns the required size");
                tally.Check(std::all_of(small.begin(), small.end(), [](unsigned char value) { return value == 0xAAu; }), "too-small capacity writes nothing");
            }
            tally.Check(SEGAI_InferHasNgClass(infer.get()) == 1 && SEGAI_InferHasNormalClass(infer.get()) == 1, "v4 model reports both NG and OK classes");
        }
    }
    {
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        const std::wstring wide = plainModel.wstring();
        tally.Check(infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0, "v3 model loads: " + (infer ? InferErrorText(infer.get()) : std::string()));
        if (infer) {
            std::vector<unsigned char> bytes;
            QueryClassTable(infer.get(), bytes);
            SegClassTable decoded;
            tally.Check(!bytes.empty() && DecodeClassTable(bytes.data(), bytes.size(), decoded, error) &&
                        SameClassTable(decoded, MakeLegacyClassTable(SEGAI_InferHasNgClass(infer.get()) == 1, SEGAI_InferHasNormalClass(infer.get()) == 1)),
                        "v3 model reports the synthesized NG (DEFECT) / OK (GOOD) table: " + error);
        }
    }

    // The class table must not change what the network predicts.
    {
        const cv::Mat image = cv::imread((fixtureDir / "0.png").u8string(), cv::IMREAD_COLOR);
        std::vector<unsigned char> plainMask, plainConfidence, tableMask, tableConfidence;
        float plainScore = 0.f, tableScore = 0.f;
        std::string plainError, tableError;
        const bool plainPredicted = !image.empty() && PredictFullImage(plainModel, image, plainMask, plainConfidence, plainScore, plainError);
        const bool tablePredicted = !image.empty() && PredictFullImage(tableModel, image, tableMask, tableConfidence, tableScore, tableError);
        tally.Check(plainPredicted && tablePredicted, "both models predict fixture 0.png: " + plainError + " " + tableError);
        if (plainPredicted && tablePredicted) {
            tally.Check(plainMask == tableMask && plainConfidence == tableConfidence && plainScore == tableScore,
                        "v3 and v4 sibling models give identical mask, confidence and score");
        }
    }
    return tally.Finish("class-table-model (T2)");
}

// ---- T3 ----------------------------------------------------------------------------------------------------

int RunClassTableNegativeMode(int argc, char** argv)
{
    if (argc < 5) {
        std::cerr << "Usage: --class-table-negative <v3.segai> <v4.segai> <outDir>\n";
        return 100;
    }
    const std::filesystem::path v3Path = std::filesystem::u8path(argv[2]);
    const std::filesystem::path v4Path = std::filesystem::u8path(argv[3]);
    const std::filesystem::path outDir = std::filesystem::u8path(argv[4]);
    std::error_code ec;
    std::filesystem::create_directories(outDir, ec);
    if (ec) {
        std::cerr << "Failed to create outDir\n";
        return 102;
    }

    using namespace BeeSegAI;
    TestTally tally;
    std::string error;
    const std::vector<unsigned char> v3File = ReadFileBytes(v3Path);
    const std::vector<unsigned char> v4File = ReadFileBytes(v4Path);
    const SegaiLayout v3Layout = ReadSegaiLayout(v3File);
    const SegaiLayout v4Layout = ReadSegaiLayout(v4File);
    tally.Check(v3Layout.valid && v3Layout.version == 3 && v3Layout.reservedA == 0, "input v3 model is a version 3 file");
    tally.Check(v4Layout.valid && v4Layout.version == 4 && v4Layout.reservedA > 0 &&
                v4File.size() == kSegaiHeaderBytes + v4Layout.payloadSize + v4Layout.reservedA, "input v4 model is a well-formed version 4 file");
    if (!v3Layout.valid || v3Layout.version != 3 || !v4Layout.valid || v4Layout.version != 4 || v4Layout.reservedA == 0 ||
        v4File.size() != kSegaiHeaderBytes + v4Layout.payloadSize + v4Layout.reservedA) {
        return tally.Finish("class-table-negative (T3)");
    }
    const size_t payloadEnd = kSegaiHeaderBytes + static_cast<size_t>(v4Layout.payloadSize);
    const std::vector<unsigned char> v4Table(v4File.begin() + static_cast<std::ptrdiff_t>(payloadEnd), v4File.end());

    // A v3 file still loads and reports the synthesized legacy table.
    {
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        const std::wstring wide = v3Path.wstring();
        tally.Check(infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0, "v3 file loads: " + (infer ? InferErrorText(infer.get()) : std::string()));
        if (infer) {
            std::vector<unsigned char> bytes;
            QueryClassTable(infer.get(), bytes);
            SegClassTable decoded;
            tally.Check(!bytes.empty() && DecodeClassTable(bytes.data(), bytes.size(), decoded, error) &&
                        SameClassTable(decoded, MakeLegacyClassTable(SEGAI_InferHasNgClass(infer.get()) == 1, SEGAI_InferHasNormalClass(infer.get()) == 1)),
                        "v3 file reports the synthesized NG (DEFECT) / OK (GOOD) table: " + error);
        }
    }
    // The unmodified v4 file loads and reports its stored table.
    {
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        const std::wstring wide = v4Path.wstring();
        tally.Check(infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0, "v4 file loads: " + (infer ? InferErrorText(infer.get()) : std::string()));
        if (infer) {
            std::vector<unsigned char> bytes;
            QueryClassTable(infer.get(), bytes);
            tally.Check(bytes == v4Table, "v4 file reports its stored table bytes");
        }
    }

    const std::filesystem::path mutatedPath = outDir / "mutated.segai";
    auto expectLoadFails = [&](const std::string& label, const std::vector<unsigned char>& bytes, const char* reasonText) {
        tally.Check(WriteFileBytes(mutatedPath, bytes), label + ": mutated file written");
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        if (!infer) {
            tally.Check(false, label + ": InferCreate");
            return;
        }
        const std::wstring wide = mutatedPath.wstring();
        const int rc = SEGAI_InferLoad(infer.get(), wide.c_str());
        const std::string reason = InferErrorText(infer.get());
        tally.Check(rc == -5, label + ": InferLoad is rejected with -5 (was " + std::to_string(rc) + ")");
        tally.Check(!reason.empty(), label + ": a lastError is reported");
        if (reasonText) tally.Check(Contains(reason, reasonText), label + ": lastError mentions \"" + reasonText + "\" (was: " + reason + ")");
        tally.Check(SEGAI_InferGetClassTable(infer.get(), nullptr, 0) == -4, label + ": no model stays loaded (InferGetClassTable is -4)");
    };

    {
        std::vector<unsigned char> bytes = v4File;
        PutU32(bytes, kOffsetVersion, 5);
        expectLoadFails("format version 5", bytes, "newer");
        PutU32(bytes, kOffsetVersion, 0x7FFFFFFFu);
        expectLoadFails("format version 2147483647", bytes, "newer");
    }
    {
        std::vector<unsigned char> bytes = v4File;
        PutU32(bytes, kOffsetReservedA, 0);
        expectLoadFails("v4 with reservedA 0", bytes, nullptr);
    }
    {
        std::vector<unsigned char> bytes = v4File;
        PutU32(bytes, kOffsetReservedA, v4Layout.reservedA + 1);
        expectLoadFails("v4 with reservedA larger than the table", bytes, nullptr);
    }
    {
        std::vector<unsigned char> bytes = v4File;
        bytes.pop_back();
        expectLoadFails("v4 with the last table byte missing", bytes, nullptr);
    }
    {
        std::vector<unsigned char> bytes(v4File.begin(), v4File.begin() + static_cast<std::ptrdiff_t>(payloadEnd));
        expectLoadFails("v4 without the table", bytes, nullptr);
    }
    {
        std::vector<unsigned char> bytes = v4File;
        bytes.push_back(0);
        expectLoadFails("v4 with a trailing byte", bytes, nullptr);
    }
    {
        std::vector<unsigned char> bytes = v4File;
        FlipBits(bytes.back(), 0x80);
        expectLoadFails("v4 with a corrupted table CRC", bytes, "class table");
    }
    {
        std::vector<unsigned char> bytes = v4File;
        FlipBits(bytes[payloadEnd + 16], 0x01); // first byte of the first class name
        expectLoadFails("v4 with a corrupted class name", bytes, "CRC");
    }
    {
        std::vector<unsigned char> bytes = v4File;
        FlipBits(bytes[kSegaiHeaderBytes + static_cast<size_t>(v4Layout.payloadSize / 2)], 0x01);
        expectLoadFails("v4 with a corrupted payload", bytes, nullptr);
    }
    {
        std::vector<unsigned char> bytes = v4File;
        PutU32(bytes, kOffsetNumClasses, 4);
        expectLoadFails("v4 whose header class count disagrees with the table", bytes, "class count");
    }
    auto withTable = [&](const SegClassTable& table, uint32_t headerNumClasses) {
        std::vector<unsigned char> tableBytes;
        std::string reason;
        const bool encoded = EncodeClassTable(table, tableBytes, reason);
        tally.Check(encoded, "mutation table encodes: " + reason);
        std::vector<unsigned char> bytes(v4File.begin(), v4File.begin() + static_cast<std::ptrdiff_t>(payloadEnd));
        bytes.insert(bytes.end(), tableBytes.begin(), tableBytes.end());
        PutU32(bytes, kOffsetReservedA, static_cast<uint32_t>(tableBytes.size()));
        PutU32(bytes, kOffsetNumClasses, headerNumClasses);
        return bytes;
    };
    // Since S2a the class count is no longer capped at two; the network must simply have N + 1 output channels.
    // The network of the input model has 3, so tables with another class count fail on the shape check.
    expectLoadFails("v4 with three named classes but a 3-channel network",
                    withTable(MakeClassTable({ { "A", kClassKindDefect }, { "B", kClassKindDefect }, { "C", kClassKindGood } }), 4), "incompatible");
    expectLoadFails("v4 with one named class but a 3-channel network", withTable(MakeClassTable({ { "A", kClassKindDefect } }), 2), "incompatible");
    auto expectGenericLoads = [&](const std::string& label, const SegClassTable& table, int expectedHasNg, int expectedHasNormal) {
        std::vector<unsigned char> tableBytes;
        tally.Check(EncodeClassTable(table, tableBytes, error), label + ": table encodes");
        tally.Check(WriteFileBytes(mutatedPath, withTable(table, static_cast<uint32_t>(table.classes.size() + 1))), label + ": file written");
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        const std::wstring wide = mutatedPath.wstring();
        tally.Check(infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0, label + ": loads: " + (infer ? InferErrorText(infer.get()) : std::string()));
        if (infer) {
            std::vector<unsigned char> returned;
            QueryClassTable(infer.get(), returned);
            tally.Check(returned == tableBytes, label + ": InferGetClassTable returns the stored table");
            tally.Check(SEGAI_InferHasNgClass(infer.get()) == expectedHasNg && SEGAI_InferHasNormalClass(infer.get()) == expectedHasNormal,
                        label + ": HasNgClass / HasNormalClass follow the table kinds");
        }
    };
    expectGenericLoads("v4 with two DEFECT classes", MakeClassTable({ { "A", kClassKindDefect }, { "B", kClassKindDefect } }), 1, 0);
    expectGenericLoads("v4 with GOOD before DEFECT", MakeClassTable({ { "A", kClassKindGood }, { "B", kClassKindDefect } }), 1, 1);
    {
        // Control: the same rebuilt file with the real table loads, so the failures above come from the tables only.
        std::vector<unsigned char> bytes = withTable(MakeClassTable({ { "Scratch", kClassKindDefect }, { "Fine", kClassKindGood } }), 3);
        tally.Check(WriteFileBytes(mutatedPath, bytes), "control file written");
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        const std::wstring wide = mutatedPath.wstring();
        tally.Check(infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0, "v4 with a renamed two-class table loads: " + (infer ? InferErrorText(infer.get()) : std::string()));
        if (infer) {
            std::vector<unsigned char> returned;
            QueryClassTable(infer.get(), returned);
            SegClassTable decoded;
            tally.Check(!returned.empty() && DecodeClassTable(returned.data(), returned.size(), decoded, error) && decoded.classes.size() == 2 &&
                        decoded.classes[0].name == "Scratch" && decoded.classes[1].name == "Fine", "renamed classes are reported");
        }
    }
    std::filesystem::remove(mutatedPath, ec);
    return tally.Finish("class-table-negative (T3)");
}

// ---- T4 (S2a) resume across class tables -------------------------------------------------------------------
//   --class-table-resume <outDir> [<backbone.onnx>|-] [<fixtureDir>|-]
// A Retrain run resumes the head of a previous model only when the class names and kinds match; otherwise it starts
// fresh and says so in the run's resume.txt ("fresh; class set changed").

std::string ReadTrimmedText(const std::filesystem::path& path)
{
    std::ifstream stream(path, std::ios::binary);
    std::string text((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
    while (!text.empty() && (text.back() == '\n' || text.back() == '\r' || text.back() == ' ')) text.pop_back();
    return text;
}

int RunClassTableResumeMode(int argc, char** argv)
{
    if (argc < 3) {
        std::cerr << "Usage: --class-table-resume <outDir> [<backbone.onnx>|-] [<fixtureDir>|-]\n";
        return 100;
    }
    const std::filesystem::path outDir = std::filesystem::u8path(argv[2]);
    std::filesystem::path backbonePath;
    if (argc > 3 && std::strcmp(argv[3], "-") != 0) {
        backbonePath = std::filesystem::u8path(argv[3]);
    } else {
        wchar_t exePath[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        backbonePath = std::filesystem::path(exePath).parent_path() / "Models" / "DefectV2" / "mobilenetv2-12.onnx";
    }
    const std::filesystem::path fixtureDir = (argc > 4 && std::strcmp(argv[4], "-") != 0)
        ? std::filesystem::u8path(argv[4]) : std::filesystem::u8path("tmp/EAI/EAI-3/fixture-src");
    if (!std::filesystem::exists(backbonePath)) {
        std::cerr << "V2 backbone is missing: " << backbonePath.u8string() << "\n";
        return 102;
    }
    std::error_code ec;
    std::filesystem::create_directories(outDir, ec);
    if (ec) {
        std::cerr << "Failed to create outDir\n";
        return 102;
    }

    using namespace BeeSegAI;
    TestTally tally;
    std::string error;
    std::vector<unsigned char> scratchFine, ngOk;
    tally.Check(EncodeClassTable(MakeClassTable({ { "Scratch", kClassKindDefect }, { "Fine", kClassKindGood } }), scratchFine, error), "Scratch/Fine table encodes");
    tally.Check(EncodeClassTable(MakeClassTable({ { "NG", kClassKindDefect }, { "OK", kClassKindGood } }), ngOk, error), "NG/OK table encodes");
    if (scratchFine.empty() || ngOk.empty()) return tally.Finish("class-table-resume (T4)");

    // Three resume sources: version 3, version 4 with other names, version 4 named NG/OK.
    const std::filesystem::path sourceV3 = outDir / "source-v3.segai", sourceV4Scratch = outDir / "source-v4-scratch.segai", sourceV4NgOk = outDir / "source-v4-ngok.segai";
    const bool sourcesReady =
        TrainTinyV2Model(tally, "source v3", outDir / "source-v3", backbonePath, fixtureDir, nullptr, sourceV3) &&
        TrainTinyV2Model(tally, "source v4 Scratch/Fine", outDir / "source-v4-scratch", backbonePath, fixtureDir, &scratchFine, sourceV4Scratch) &&
        TrainTinyV2Model(tally, "source v4 NG/OK", outDir / "source-v4-ngok", backbonePath, fixtureDir, &ngOk, sourceV4NgOk);
    if (!sourcesReady) return tally.Finish("class-table-resume (T4)");

    struct ResumeCase {
        const char* label;
        const std::filesystem::path* source;
        const std::vector<unsigned char>* table;
        bool expectResumed;
    };
    const ResumeCase cases[] = {
        { "v3 source, table Scratch/Fine set", &sourceV3, &scratchFine, false },
        { "v4 Scratch/Fine source, no table", &sourceV4Scratch, nullptr, false },
        { "v4 NG/OK source, no table", &sourceV4NgOk, nullptr, true },
        { "v3 source, table NG/OK set", &sourceV3, &ngOk, true },
        { "v4 Scratch/Fine source, table Scratch/Fine set", &sourceV4Scratch, &scratchFine, true },
    };
    int index = 0;
    for (const ResumeCase& resumeCase : cases) {
        const std::filesystem::path runDir = outDir / ("resume-run-" + std::to_string(index++));
        const std::filesystem::path model = runDir.string() + ".segai";
        const std::string label = std::string("resume: ") + resumeCase.label;
        if (!TrainTinyV2Model(tally, label, runDir, backbonePath, fixtureDir, resumeCase.table, model, *resumeCase.source)) continue;
        const std::string status = ReadTrimmedText(runDir / "v2_logs" / "resume.txt");
        std::cout << label << " -> " << status << "\n";
        if (resumeCase.expectResumed) {
            tally.Check(status.compare(0, 7, "resumed") == 0, label + ": the head is resumed (status was: " + status + ")");
        } else {
            tally.Check(status == "fresh; class set changed", label + ": fresh start because the class set changed (status was: " + status + ")");
        }
    }
    return tally.Finish("class-table-resume (T4)");
}

// ---- T5 (S2a) generalised class count inference ------------------------------------------------------------
//   --class-model-synth <outDir> <source.segai> [<fixtureDir>|-]
// Models with N = 1, 2 and 4 classes are synthesised WITHOUT training from <source.segai> (a trained 2-class V2 model,
// e.g. no-table.segai written by --class-table-model) by SynthesizeClassModel and checked against the source model.

struct ClassModelResult {
    int loadRc = 0;
    std::string loadError;
    int predictRc = 0;
    std::string predictError;
    int32_t tableRc = 0;
    std::vector<unsigned char> tableBytes;
    int hasNg = -1, hasNormal = -1;
    int normalRc = 0;
    cv::Mat ng, ok; // ok stays empty when GetNormalConfidence reports no GOOD class
    std::vector<unsigned char> mask;
};

ClassModelResult RunClassModel(const std::filesystem::path& model, const cv::Mat& image, const std::function<void(void*)>& setup)
{
    ClassModelResult result;
    std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
    if (!infer) {
        result.loadRc = -99;
        result.loadError = "InferCreate failed";
        return result;
    }
    const std::wstring wide = model.wstring();
    result.loadRc = SEGAI_InferLoad(infer.get(), wide.c_str());
    result.loadError = InferErrorText(infer.get());
    if (result.loadRc != 0) return result;
    SEGAI_InferSetGpu(infer.get(), 0);
    result.tableRc = QueryClassTable(infer.get(), result.tableBytes);
    result.hasNg = SEGAI_InferHasNgClass(infer.get());
    result.hasNormal = SEGAI_InferHasNormalClass(infer.get());
    if (setup) setup(infer.get());
    uint8_t* maskPtr = nullptr;
    int32_t width = 0, height = 0;
    float score = 0.f;
    result.predictRc = SEGAI_InferPredict(infer.get(), image.data, image.cols, image.rows, static_cast<int>(image.step), 0, 0,
                                          image.cols, image.rows, 0.5f, &maskPtr, &width, &height, &score);
    std::unique_ptr<uint8_t, SegaiBufferDeleter> maskOwner(maskPtr);
    result.predictError = InferErrorText(infer.get());
    if (result.predictRc != 0 || !maskPtr) return result;
    result.mask.assign(maskPtr, maskPtr + static_cast<size_t>(width) * static_cast<size_t>(height));
    uint8_t* ngPtr = nullptr;
    int32_t ngWidth = 0, ngHeight = 0;
    if (SEGAI_InferGetConfidence(infer.get(), &ngPtr, &ngWidth, &ngHeight) == 0 && ngPtr) {
        result.ng = cv::Mat(ngHeight, ngWidth, CV_8U, ngPtr).clone();
        SEGAI_FreeBuffer(ngPtr);
    }
    uint8_t* okPtr = nullptr;
    int32_t okWidth = 0, okHeight = 0;
    result.normalRc = SEGAI_InferGetNormalConfidence(infer.get(), &okPtr, &okWidth, &okHeight);
    if (result.normalRc == 0 && okPtr) {
        result.ok = cv::Mat(okHeight, okWidth, CV_8U, okPtr).clone();
        SEGAI_FreeBuffer(okPtr);
    }
    return result;
}

// Same size, same non-zero support, every pixel within tolerance.
bool MapsClose(const cv::Mat& left, const cv::Mat& right, int tolerance, std::string& why)
{
    if (left.empty() || right.empty() || left.size() != right.size() || left.type() != right.type()) {
        why = "size or type differs or a map is empty";
        return false;
    }
    cv::Mat difference;
    cv::absdiff(left, right, difference);
    double maximum = 0;
    cv::minMaxLoc(difference, nullptr, &maximum);
    if (maximum > tolerance) {
        why = "max difference " + std::to_string(maximum);
        return false;
    }
    cv::Mat leftSupport = left > 0, rightSupport = right > 0, mismatch;
    cv::bitwise_xor(leftSupport, rightSupport, mismatch);
    if (cv::countNonZero(mismatch) != 0) {
        why = std::to_string(cv::countNonZero(mismatch)) + " pixels differ in support";
        return false;
    }
    return true;
}

int RunClassModelSynthMode(int argc, char** argv)
{
    if (argc < 4) {
        std::cerr << "Usage: --class-model-synth <outDir> <source.segai> [<fixtureDir>|-]\n";
        return 100;
    }
    const std::filesystem::path outDir = std::filesystem::u8path(argv[2]);
    const std::filesystem::path sourceModel = std::filesystem::u8path(argv[3]);
    const std::filesystem::path fixtureDir = (argc > 4 && std::strcmp(argv[4], "-") != 0)
        ? std::filesystem::u8path(argv[4]) : std::filesystem::u8path("tmp/EAI/EAI-3/fixture-src");
    std::error_code ec;
    std::filesystem::create_directories(outDir, ec);
    if (ec) {
        std::cerr << "Failed to create outDir\n";
        return 102;
    }
    std::vector<cv::Mat> images;
    for (int index = 0; index < 2; ++index) {
        cv::Mat image = cv::imread((fixtureDir / (std::to_string(index) + ".png")).u8string(), cv::IMREAD_COLOR);
        if (!image.empty()) images.push_back(image);
    }
    if (images.empty() || !std::filesystem::exists(sourceModel)) {
        std::cerr << "Need <source.segai> and fixture images 0.png/1.png in " << fixtureDir.u8string() << "\n";
        return 101;
    }

    using namespace BeeSegAI;
    TestTally tally;
    std::string error;
    const float kNegligible = -30.f; // bias shift that keeps a copied class from ever winning

    // Builds <name>.segai with the given channels and table; returns its path.
    auto build = [&](const std::string& name, const std::vector<SynthChannel>& channels, const SegClassTable& table, int postprocessVersion) {
        std::vector<unsigned char> tableBytes;
        std::string reason;
        tally.Check(EncodeClassTable(table, tableBytes, reason), name + ": table encodes: " + reason);
        const std::filesystem::path path = outDir / (name + ".segai");
        tally.Check(SynthesizeClassModel(sourceModel, path, channels, tableBytes, postprocessVersion, reason), name + ": model synthesised: " + reason);
        return path;
    };
    auto tableOf = [](const SegClassTable& table) {
        std::vector<unsigned char> bytes;
        std::string reason;
        EncodeClassTable(table, bytes, reason);
        return bytes;
    };

    // Class order is chosen so the expected result is known: channel 1 of the source is NG, channel 2 is OK.
    const SegClassTable identityTable = MakeClassTable({ { "NG", kClassKindDefect }, { "OK", kClassKindGood } });
    const SegClassTable permutedTable = MakeClassTable({ { "Fine", kClassKindGood }, { "Scratch", kClassKindDefect } });
    const SegClassTable fourTable = MakeClassTable({ { "A", kClassKindDefect }, { "B", kClassKindDefect }, { "G", kClassKindGood }, { "C", kClassKindDefect } });
    SegClassTable disabledTable = MakeClassTable({ { "A", kClassKindDefect }, { "G", kClassKindGood }, { "B", kClassKindDefect } });
    disabledTable.classes[1].enabled = false;
    SegClassTable disabledLegacyTable = MakeClassTable({ { "A", kClassKindDefect }, { "G", kClassKindGood } });
    disabledLegacyTable.classes[1].enabled = false;
    SegClassTable disabledDefectTable = MakeClassTable({ { "A", kClassKindDefect }, { "G", kClassKindGood }, { "B", kClassKindDefect } });
    disabledDefectTable.classes[0].enabled = false;

    const std::filesystem::path identity = build("identity", { { 0, 0.f }, { 1, 0.f }, { 2, 0.f } }, identityTable, 2);
    const std::filesystem::path permuted = build("permuted", { { 0, 0.f }, { 2, 0.f }, { 1, 0.f } }, permutedTable, 2);
    const std::filesystem::path four = build("four-classes", { { 0, 0.f }, { 1, 0.f }, { 1, kNegligible }, { 2, 0.f }, { 2, kNegligible } }, fourTable, 2);
    const std::filesystem::path disabled = build("disabled-good", { { 0, 0.f }, { 1, 0.f }, { 2, 0.f }, { 1, kNegligible } }, disabledTable, 2);
    const std::filesystem::path disabledLegacy = build("disabled-good-legacy", { { 0, 0.f }, { 1, 0.f }, { 2, 0.f } }, disabledLegacyTable, 2);
    const std::filesystem::path disabledDefect = build("disabled-defect", { { 0, 0.f }, { 1, 0.f }, { 2, 0.f }, { 1, kNegligible } }, disabledDefectTable, 2);
    const std::filesystem::path oneDefect = build("one-defect", { { 0, 0.f }, { 1, 0.f } }, MakeClassTable({ { "Only", kClassKindDefect } }), 2);
    const std::filesystem::path oneGood = build("one-good", { { 0, 0.f }, { 2, 0.f } }, MakeClassTable({ { "Only", kClassKindGood } }), 2);
    const std::filesystem::path fourPostprocess1 = build("four-classes-pp1", { { 0, 0.f }, { 1, 0.f }, { 1, kNegligible }, { 2, 0.f }, { 2, kNegligible } }, fourTable, 1);
    const std::filesystem::path permutedPostprocess1 = build("permuted-pp1", { { 0, 0.f }, { 2, 0.f }, { 1, 0.f } }, permutedTable, 1);
    const std::filesystem::path identityPostprocess1 = build("identity-pp1", { { 0, 0.f }, { 1, 0.f }, { 2, 0.f } }, identityTable, 1);

    // Two foreground settings: the stored defaults and a permissive one that yields many components on a weak model.
    struct Variant { const char* name; std::function<void(void*)> apply; };
    const std::vector<Variant> variants = {
        { "default options", nullptr },
        { "foreground 0.05, no morphology", [](void* handle) { SEGAI_InferV2Options(handle, 0.05f, 0.f, 0); } },
    };
    int nonEmptyReference = 0, referenceRuns = 0;

    // Runs `reference` and `candidate` over every image and variant and compares the DEFECT map (and the GOOD map).
    auto compareModels = [&](const std::string& label, const std::filesystem::path& reference, const std::filesystem::path& candidate,
                             const std::function<void(void*)>& extraReference, const std::function<void(void*)>& extraCandidate,
                             int tolerance, bool compareGood, bool countReference) {
        for (size_t imageIndex = 0; imageIndex < images.size(); ++imageIndex) {
            for (const Variant& variant : variants) {
                const std::string where = label + " [image " + std::to_string(imageIndex) + ", " + variant.name + "]";
                auto withExtra = [&](const std::function<void(void*)>& extra) {
                    return std::function<void(void*)>([&variant, &extra](void* handle) {
                        if (variant.apply) variant.apply(handle);
                        if (extra) extra(handle);
                    });
                };
                const ClassModelResult expected = RunClassModel(reference, images[imageIndex], withExtra(extraReference));
                const ClassModelResult actual = RunClassModel(candidate, images[imageIndex], withExtra(extraCandidate));
                tally.Check(expected.loadRc == 0 && expected.predictRc == 0, where + ": reference predicts (" + expected.loadError + expected.predictError + ")");
                tally.Check(actual.loadRc == 0 && actual.predictRc == 0, where + ": candidate predicts (" + actual.loadError + actual.predictError + ")");
                if (expected.predictRc != 0 || actual.predictRc != 0 || expected.loadRc != 0 || actual.loadRc != 0) continue;
                std::string why;
                tally.Check(MapsClose(expected.ng, actual.ng, tolerance, why), where + ": DEFECT map matches the reference: " + why);
                if (compareGood) {
                    why.clear();
                    tally.Check(MapsClose(expected.ok, actual.ok, tolerance, why), where + ": GOOD map matches the reference: " + why);
                }
                if (countReference) {
                    ++referenceRuns;
                    if (cv::countNonZero(expected.ng) > 0 || (!expected.ok.empty() && cv::countNonZero(expected.ok) > 0)) ++nonEmptyReference;
                }
            }
        }
    };
    const std::function<void(void*)> noExtra;
    const std::function<void(void*)> cleanup = [](void* handle) { SEGAI_InferSetV2ShapeCleanup(handle, 0.1f); };
    const std::function<void(void*)> winner = [](void* handle) { SEGAI_InferSetV2BoundaryMode(handle, 1); };

    // The identity rewrite carries the same weights: the legacy NG/OK layout must reproduce the source bit for bit.
    compareModels("identity v4 N=2 vs source", sourceModel, identity, noExtra, noExtra, 0, true, true);
    // Swapped class order (GOOD first): generalised path, DEFECT/GOOD maps must still equal the source's NG/OK maps.
    compareModels("permuted classes (GOOD, DEFECT) vs source", sourceModel, permuted, noExtra, noExtra, 1, true, false);
    // Four classes, two of them practically impossible copies: K=5 path equals the source.
    compareModels("N=4 vs source", sourceModel, four, noExtra, noExtra, 1, true, false);
    compareModels("N=4 vs source, winner foreground", sourceModel, four, winner, winner, 1, true, false);
    compareModels("N=4 vs source, shape cleanup 0.1", sourceModel, four, cleanup, cleanup, 1, true, false);
    // A disabled GOOD class contributes nothing: generalised N=3 equals the legacy-layout N=2 with the same flag.
    compareModels("N=3 with disabled GOOD vs legacy layout with disabled GOOD", disabledLegacy, disabled, noExtra, noExtra, 1, false, false);

    std::cout << "non_empty_reference_runs=" << nonEmptyReference << "/" << referenceRuns << "\n";
    if (nonEmptyReference == 0) std::cout << "NOTE: the source model predicted no foreground on these images, so the map comparisons above are vacuous.\n";
    tally.Check(nonEmptyReference > 0, "the source model predicts foreground on at least one image/variant (the map comparisons are not vacuous)");

    // Per-model properties: table round trip, legacy export mapping, disabled classes.
    auto inspect = [&](const std::string& label, const std::filesystem::path& path, const SegClassTable& table, int expectedHasNg, int expectedHasNormal,
                       bool expectGoodMap) {
        const ClassModelResult result = RunClassModel(path, images[0], std::function<void(void*)>());
        tally.Check(result.loadRc == 0, label + ": loads (" + result.loadError + ")");
        if (result.loadRc != 0) return result;
        tally.Check(result.tableBytes == tableOf(table), label + ": InferGetClassTable returns the stored table, flags included");
        tally.Check(result.hasNg == expectedHasNg && result.hasNormal == expectedHasNormal,
                    label + ": HasNgClass " + std::to_string(result.hasNg) + " / HasNormalClass " + std::to_string(result.hasNormal) + " follow the table");
        tally.Check(result.predictRc == 0, label + ": predicts (" + result.predictError + ")");
        tally.Check(result.predictRc != 0 || (!result.ng.empty() && result.ng.size() == images[0].size()), label + ": DEFECT map has the image size");
        if (expectGoodMap) tally.Check(result.predictRc != 0 || (result.normalRc == 0 && result.ok.size() == images[0].size()), label + ": GOOD map is returned");
        else tally.Check(result.predictRc != 0 || result.normalRc == -2, label + ": GetNormalConfidence is -2 without a GOOD class");
        return result;
    };
    inspect("identity", identity, identityTable, 1, 1, true);
    inspect("permuted", permuted, permutedTable, 1, 1, true);
    inspect("N=4", four, fourTable, 1, 1, true);
    inspect("one DEFECT class", oneDefect, MakeClassTable({ { "Only", kClassKindDefect } }), 1, 0, false);
    {
        const ClassModelResult result = inspect("one GOOD class", oneGood, MakeClassTable({ { "Only", kClassKindGood } }), 0, 1, true);
        tally.Check(result.predictRc != 0 || (!result.ng.empty() && cv::countNonZero(result.ng) == 0), "one GOOD class: the DEFECT map is empty");
    }
    {
        const ClassModelResult result = inspect("disabled GOOD", disabled, disabledTable, 1, 0, true);
        tally.Check(result.predictRc != 0 || (!result.ok.empty() && cv::countNonZero(result.ok) == 0), "disabled GOOD: the GOOD map has no pixels");
        const ClassModelResult legacy = inspect("disabled GOOD (legacy layout)", disabledLegacy, disabledLegacyTable, 1, 0, true);
        tally.Check(legacy.predictRc != 0 || (!legacy.ok.empty() && cv::countNonZero(legacy.ok) == 0), "disabled GOOD (legacy layout): the GOOD map has no pixels");
    }
    {
        // Class A (the NG copy) is disabled; B is a negligible NG copy and G the OK class, so no pixel can be assigned a DEFECT class.
        for (size_t imageIndex = 0; imageIndex < images.size(); ++imageIndex) {
            for (const Variant& variant : variants) {
                const ClassModelResult result = RunClassModel(disabledDefect, images[imageIndex], variant.apply);
                const std::string where = std::string("disabled DEFECT [image ") + std::to_string(imageIndex) + ", " + variant.name + "]";
                tally.Check(result.loadRc == 0 && result.predictRc == 0, where + ": predicts (" + result.loadError + result.predictError + ")");
                tally.Check(result.predictRc != 0 || (!result.ng.empty() && cv::countNonZero(result.ng) == 0), where + ": the DEFECT map has no pixels");
            }
        }
    }

    // Since S2c multiscale works for every class layout; winner foreground still excludes it, as for the NG/OK layout.
    for (const auto& generic : std::vector<std::pair<std::string, std::filesystem::path>>{ { "N=4", four }, { "one DEFECT class", oneDefect }, { "permuted", permuted } }) {
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        const std::wstring wide = generic.second.wstring();
        tally.Check(infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0, generic.first + ": loads for the multiscale check");
        if (!infer) continue;
        tally.Check(SEGAI_InferSetObjectScale(infer.get(), 0.6f, 1.6f, 1) == 0, generic.first + ": multiscale is accepted (" + InferErrorText(infer.get()) + ")");
        tally.Check(SEGAI_InferSetV2BoundaryMode(infer.get(), 1) == -2, generic.first + ": winner foreground is rejected while multiscale is on");
        tally.Check(SEGAI_InferSetObjectScale(infer.get(), 0.6f, 1.6f, 0) == 0, generic.first + ": single-scale object scale is accepted");
    }
    {
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        const std::wstring wide = identity.wstring();
        tally.Check(infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0 && SEGAI_InferSetObjectScale(infer.get(), 0.6f, 1.6f, 1) == 0,
                    "identity (NG/OK layout): multiscale is still accepted");
    }
    for (const auto& rejected : std::vector<std::pair<std::string, std::filesystem::path>>{ { "N=4 postprocess_version 1", fourPostprocess1 }, { "permuted postprocess_version 1", permutedPostprocess1 } }) {
        const ClassModelResult result = RunClassModel(rejected.second, images[0], std::function<void(void*)>());
        tally.Check(result.loadRc == -5 && Contains(result.loadError, "postprocess_version"), rejected.first + ": load is rejected with a lastError (rc " + std::to_string(result.loadRc) + ": " + result.loadError + ")");
    }
    {
        const ClassModelResult result = RunClassModel(identityPostprocess1, images[0], std::function<void(void*)>());
        tally.Check(result.loadRc == 0 && result.predictRc == 0, "identity postprocess_version 1 (NG/OK layout) still loads and predicts");
    }

    // The V158 diagnostic export knows only the NG/OK layout.
    {
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        const std::wstring wide = four.wstring();
        tally.Check(infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0, "N=4: loads for the diagnostic check");
        if (infer) {
            SEGAI_InferSetGpu(infer.get(), 0);
            uint8_t* packet = nullptr;
            uint64_t packetBytes = 0;
            const int32_t rc = SEGAI_InferPredictDiagnosticV1(infer.get(), images[0].data, images[0].cols, images[0].rows, static_cast<int32_t>(images[0].step),
                                                              0, 0, images[0].cols, images[0].rows, 0.5f, "test", "test", &packet, &packetBytes);
            std::unique_ptr<uint8_t, SegaiBufferDeleter> packetOwner(packet);
            tally.Check(rc == -5, "N=4: SEGAI_InferPredictDiagnosticV1 is -5 (was " + std::to_string(rc) + ")");
        }
    }
    return tally.Finish("class-model-synth (T5)");
}

// ---- T6 (S2c) multiscale detection on every class layout ---------------------------------------------------
//   --class-model-multiscale <outDir> <source.segai> [<fixtureDir>|-]
// Same synthesised models as T5, now with SEGAI_InferSetObjectScale(min, max, 1) for the ranges 0.85..1.15 (scales below
// and above 1 plus 1.0) and 0.7..1.0 (overlapping crops below 1). The references are the source model (legacy
// multiscale path) or a legacy-layout model; the candidates run the generalised multiscale path.

struct SetupVariant {
    const char* name;
    std::function<void(void*)> apply;
};

std::function<void(void*)> ComposeSetup(const std::function<void(void*)>& first, const std::function<void(void*)>& second)
{
    return std::function<void(void*)>([first, second](void* handle) {
        if (first) first(handle);
        if (second) second(handle);
    });
}

bool HasPixels(const cv::Mat& map)
{
    return !map.empty() && cv::countNonZero(map) > 0;
}

int RunClassModelMultiscaleMode(int argc, char** argv)
{
    if (argc < 4) {
        std::cerr << "Usage: --class-model-multiscale <outDir> <source.segai> [<fixtureDir>|-]\n";
        return 100;
    }
    const std::filesystem::path outDir = std::filesystem::u8path(argv[2]);
    const std::filesystem::path sourceModel = std::filesystem::u8path(argv[3]);
    const std::filesystem::path fixtureDir = (argc > 4 && std::strcmp(argv[4], "-") != 0)
        ? std::filesystem::u8path(argv[4]) : std::filesystem::u8path("tmp/EAI/EAI-3/fixture-src");
    std::error_code ec;
    std::filesystem::create_directories(outDir, ec);
    if (ec) {
        std::cerr << "Failed to create outDir\n";
        return 102;
    }
    std::vector<cv::Mat> images;
    for (int index = 0; index < 2; ++index) {
        cv::Mat image = cv::imread((fixtureDir / (std::to_string(index) + ".png")).u8string(), cv::IMREAD_COLOR);
        if (!image.empty()) images.push_back(image);
    }
    if (images.empty() || !std::filesystem::exists(sourceModel)) {
        std::cerr << "Need <source.segai> and fixture images 0.png/1.png in " << fixtureDir.u8string() << "\n";
        return 101;
    }

    using namespace BeeSegAI;
    TestTally tally;
    const float kNegligible = -30.f;
    auto build = [&](const std::string& name, const std::vector<SynthChannel>& channels, const SegClassTable& table, int postprocessVersion) {
        std::vector<unsigned char> tableBytes;
        std::string reason;
        tally.Check(EncodeClassTable(table, tableBytes, reason), name + ": table encodes: " + reason);
        const std::filesystem::path path = outDir / (name + ".segai");
        tally.Check(SynthesizeClassModel(sourceModel, path, channels, tableBytes, postprocessVersion, reason), name + ": model synthesised: " + reason);
        return path;
    };

    const SegClassTable identityTable = MakeClassTable({ { "NG", kClassKindDefect }, { "OK", kClassKindGood } });
    const SegClassTable permutedTable = MakeClassTable({ { "Fine", kClassKindGood }, { "Scratch", kClassKindDefect } });
    const SegClassTable fourTable = MakeClassTable({ { "A", kClassKindDefect }, { "B", kClassKindDefect }, { "G", kClassKindGood }, { "C", kClassKindDefect } });
    SegClassTable disabledTable = MakeClassTable({ { "A", kClassKindDefect }, { "G", kClassKindGood }, { "B", kClassKindDefect } });
    disabledTable.classes[1].enabled = false;
    SegClassTable disabledLegacyTable = MakeClassTable({ { "A", kClassKindDefect }, { "G", kClassKindGood } });
    disabledLegacyTable.classes[1].enabled = false;

    const std::filesystem::path identity = build("identity", { { 0, 0.f }, { 1, 0.f }, { 2, 0.f } }, identityTable, 2);
    const std::filesystem::path permuted = build("permuted", { { 0, 0.f }, { 2, 0.f }, { 1, 0.f } }, permutedTable, 2);
    const std::filesystem::path four = build("four-classes", { { 0, 0.f }, { 1, 0.f }, { 1, kNegligible }, { 2, 0.f }, { 2, kNegligible } }, fourTable, 2);
    const std::filesystem::path oneDefect = build("one-defect", { { 0, 0.f }, { 1, 0.f } }, MakeClassTable({ { "Only", kClassKindDefect } }), 2);
    // Legacy layout whose OK channel can never win: its NG map is the reference of the one-class model (same softmax up to e^-30).
    const std::filesystem::path negligibleOk = build("legacy-negligible-ok", { { 0, 0.f }, { 1, 0.f }, { 2, kNegligible } }, identityTable, 2);
    const std::filesystem::path disabled = build("disabled-good", { { 0, 0.f }, { 1, 0.f }, { 2, 0.f }, { 1, kNegligible } }, disabledTable, 2);
    const std::filesystem::path disabledLegacy = build("disabled-good-legacy", { { 0, 0.f }, { 1, 0.f }, { 2, 0.f } }, disabledLegacyTable, 2);
    // Exact ties: two classes with the very same channel; the lowest class id must win every component.
    const std::filesystem::path tieDefectFirst = build("tie-defect-first", { { 0, 0.f }, { 1, 0.f }, { 1, 0.f } },
                                                       MakeClassTable({ { "A", kClassKindDefect }, { "B", kClassKindGood } }), 2);
    const std::filesystem::path tieGoodFirst = build("tie-good-first", { { 0, 0.f }, { 1, 0.f }, { 1, 0.f } },
                                                     MakeClassTable({ { "A", kClassKindGood }, { "B", kClassKindDefect } }), 2);
    // Two DEFECT classes with the real NG and OK channels: InferPredict is the union of both.
    const std::filesystem::path twoDefect = build("two-defect", { { 0, 0.f }, { 1, 0.f }, { 2, 0.f } },
                                                  MakeClassTable({ { "A", kClassKindDefect }, { "B", kClassKindDefect } }), 2);
    const std::filesystem::path fourPostprocess1 = build("four-classes-pp1", { { 0, 0.f }, { 1, 0.f }, { 1, kNegligible }, { 2, 0.f }, { 2, kNegligible } }, fourTable, 1);
    const std::filesystem::path permutedPostprocess1 = build("permuted-pp1", { { 0, 0.f }, { 2, 0.f }, { 1, 0.f } }, permutedTable, 1);

    const std::vector<SetupVariant> variants = {
        { "default options", nullptr },
        { "foreground 0.05, no morphology", [](void* handle) { SEGAI_InferV2Options(handle, 0.05f, 0.f, 0); } },
    };
    auto scaleOn = [&tally](float low, float high) {
        return std::function<void(void*)>([&tally, low, high](void* handle) {
            tally.Check(SEGAI_InferSetObjectScale(handle, low, high, 1) == 0,
                        "SEGAI_InferSetObjectScale(" + std::to_string(low) + ", " + std::to_string(high) + ", 1) is accepted");
        });
    };
    const std::function<void(void*)> noExtra;
    const std::pair<float, float> ranges[] = { { 0.85f, 1.15f }, { 0.7f, 1.0f } };

    // Runs reference and candidate over every image and foreground variant and compares the maps.
    int candidateRuns = 0, candidateNonEmpty = 0;
    auto compare = [&](const std::string& label, const std::filesystem::path& reference, const std::filesystem::path& candidate,
                       const std::function<void(void*)>& referenceExtra, const std::function<void(void*)>& candidateExtra,
                       int tolerance, bool compareGood) {
        int labelRuns = 0, labelNonEmpty = 0;
        struct LabelReport {
            const std::string& label; int& runs; int& nonEmpty; TestTally& tally;
            ~LabelReport() {
                std::cout << "multiscale_compare_non_empty: " << label << ": " << nonEmpty << "/" << runs << "\n";
                tally.Check(nonEmpty >= 1, label + ": at least one run has a non-empty candidate map (not vacuous)");
            }
        } labelReport{ label, labelRuns, labelNonEmpty, tally };
        for (size_t imageIndex = 0; imageIndex < images.size(); ++imageIndex) {
            for (const SetupVariant& variant : variants) {
                const std::string where = label + " [image " + std::to_string(imageIndex) + ", " + variant.name + "]";
                const ClassModelResult expected = RunClassModel(reference, images[imageIndex], ComposeSetup(variant.apply, referenceExtra));
                const ClassModelResult actual = RunClassModel(candidate, images[imageIndex], ComposeSetup(variant.apply, candidateExtra));
                tally.Check(expected.loadRc == 0 && expected.predictRc == 0, where + ": reference predicts (" + expected.loadError + expected.predictError + ")");
                tally.Check(actual.loadRc == 0 && actual.predictRc == 0, where + ": candidate predicts (" + actual.loadError + actual.predictError + ")");
                if (expected.loadRc != 0 || expected.predictRc != 0 || actual.loadRc != 0 || actual.predictRc != 0) continue;
                std::string why;
                tally.Check(MapsClose(expected.ng, actual.ng, tolerance, why), where + ": DEFECT map matches the reference: " + why);
                if (compareGood) {
                    why.clear();
                    tally.Check(MapsClose(expected.ok, actual.ok, tolerance, why), where + ": GOOD map matches the reference: " + why);
                }
                ++candidateRuns;
                ++labelRuns;
                if (HasPixels(actual.ng) || HasPixels(actual.ok)) { ++candidateNonEmpty; ++labelNonEmpty; }
            }
        }
    };

    for (const auto& range : ranges) {
        const std::string rangeText = " multiscale " + std::to_string(range.first).substr(0, 4) + ".." + std::to_string(range.second).substr(0, 4);
        const std::function<void(void*)> on = scaleOn(range.first, range.second);
        // The legacy layout keeps the original multiscale code: the v4 identity file must equal the v3 source exactly.
        compare("identity v4 N=2 vs source," + rangeText, sourceModel, identity, on, on, 0, true);
        compare("permuted classes (GOOD, DEFECT) vs source," + rangeText, sourceModel, permuted, on, on, 1, true);
        compare("N=4 vs source," + rangeText, sourceModel, four, on, on, 1, true);
        // One DEFECT class has a K=2 softmax, so its reference is the legacy-layout model whose OK channel cannot win.
        compare("N=1 DEFECT vs legacy layout with a negligible OK," + rangeText, negligibleOk, oneDefect, on, on, 1, false);
        compare("N=3 with disabled GOOD vs legacy layout with disabled GOOD," + rangeText, disabledLegacy, disabled, on, on, 1, false);
    }
    // Multiscale off is unchanged (single-scale generalised path) and matches the source.
    compare("N=4 vs source, multiscale off", sourceModel, four, noExtra, noExtra, 1, true);

    // Multiscale ON versus OFF on the N=4 model must differ exactly where the source model's ON and OFF differ, and neither is empty.
    {
        const std::function<void(void*)> on = scaleOn(ranges[0].first, ranges[0].second);
        int sourceDiffers = 0, candidateDiffers = 0, onNonEmpty = 0, offNonEmpty = 0, runs = 0;
        for (size_t imageIndex = 0; imageIndex < images.size(); ++imageIndex) {
            for (const SetupVariant& variant : variants) {
                const ClassModelResult sourceOn = RunClassModel(sourceModel, images[imageIndex], ComposeSetup(variant.apply, on));
                const ClassModelResult sourceOff = RunClassModel(sourceModel, images[imageIndex], variant.apply);
                const ClassModelResult fourOn = RunClassModel(four, images[imageIndex], ComposeSetup(variant.apply, on));
                const ClassModelResult fourOff = RunClassModel(four, images[imageIndex], variant.apply);
                if (sourceOn.predictRc != 0 || sourceOff.predictRc != 0 || fourOn.predictRc != 0 || fourOff.predictRc != 0) {
                    tally.Check(false, std::string("ON/OFF comparison: all four runs predict [image ") + std::to_string(imageIndex) + ", " + variant.name + "]");
                    continue;
                }
                ++runs;
                std::string why;
                const bool sourceSame = MapsClose(sourceOn.ng, sourceOff.ng, 0, why) && MapsClose(sourceOn.ok, sourceOff.ok, 0, why);
                const bool candidateSame = MapsClose(fourOn.ng, fourOff.ng, 1, why) && MapsClose(fourOn.ok, fourOff.ok, 1, why);
                if (!sourceSame) ++sourceDiffers;
                if (!candidateSame) ++candidateDiffers;
                tally.Check(sourceSame == candidateSame || (!sourceSame && !candidateSame),
                            std::string("N=4 multiscale ON vs OFF differ exactly when the source's do [image ") + std::to_string(imageIndex) + ", " + variant.name + "]");
                if (HasPixels(fourOn.ng) || HasPixels(fourOn.ok)) ++onNonEmpty;
                if (HasPixels(fourOff.ng) || HasPixels(fourOff.ok)) ++offNonEmpty;
            }
        }
        std::cout << "multiscale_on_off: runs=" << runs << " source_differs=" << sourceDiffers << " n4_differs=" << candidateDiffers
                  << " n4_on_non_empty=" << onNonEmpty << " n4_off_non_empty=" << offNonEmpty << "\n";
        tally.Check(onNonEmpty > 0 && offNonEmpty > 0, "N=4 predicts foreground with multiscale on and with multiscale off (at least one run each)");
    }

    // Exact ties: the lowest class id owns every component, so the other kind's map is empty and both models agree.
    for (size_t imageIndex = 0; imageIndex < images.size(); ++imageIndex) {
        for (const SetupVariant& variant : variants) {
            for (int multiscale = 0; multiscale < 2; ++multiscale) {
                const std::function<void(void*)> extra = multiscale ? scaleOn(ranges[0].first, ranges[0].second) : noExtra;
                const std::string where = std::string("tie [image ") + std::to_string(imageIndex) + ", " + variant.name + (multiscale ? ", multiscale" : ", single scale") + "]";
                const ClassModelResult defectFirst = RunClassModel(tieDefectFirst, images[imageIndex], ComposeSetup(variant.apply, extra));
                const ClassModelResult goodFirst = RunClassModel(tieGoodFirst, images[imageIndex], ComposeSetup(variant.apply, extra));
                tally.Check(defectFirst.predictRc == 0 && goodFirst.predictRc == 0, where + ": both tie models predict");
                if (defectFirst.predictRc != 0 || goodFirst.predictRc != 0) continue;
                tally.Check(!defectFirst.ok.empty() && !HasPixels(defectFirst.ok), where + ": DEFECT id 1 beats GOOD id 2 on an exact tie (GOOD map is empty)");
                tally.Check(!goodFirst.ng.empty() && !HasPixels(goodFirst.ng), where + ": GOOD id 1 beats DEFECT id 2 on an exact tie (DEFECT map is empty)");
                std::string why;
                tally.Check(MapsClose(defectFirst.ng, goodFirst.ok, 0, why), where + ": both tie models give the same winner map: " + why);
            }
        }
    }
    {
        int tieNonEmpty = 0;
        for (size_t imageIndex = 0; imageIndex < images.size(); ++imageIndex) {
            for (const SetupVariant& variant : variants) {
                if (HasPixels(RunClassModel(tieDefectFirst, images[imageIndex], variant.apply).ng)) ++tieNonEmpty;
            }
        }
        tally.Check(tieNonEmpty > 0, "the tie models predict foreground on at least one image/variant (the tie checks are not vacuous)");
    }

    // Two DEFECT classes: DEFECT map = union of both source maps; InferPredict is that map thresholded at 0.5.
    for (size_t imageIndex = 0; imageIndex < images.size(); ++imageIndex) {
        for (const SetupVariant& variant : variants) {
            for (int multiscale = 0; multiscale < 2; ++multiscale) {
                const std::function<void(void*)> extra = multiscale ? scaleOn(ranges[0].first, ranges[0].second) : noExtra;
                const std::string where = std::string("two DEFECT classes [image ") + std::to_string(imageIndex) + ", " + variant.name + (multiscale ? ", multiscale" : ", single scale") + "]";
                const ClassModelResult reference = RunClassModel(sourceModel, images[imageIndex], ComposeSetup(variant.apply, extra));
                const ClassModelResult actual = RunClassModel(twoDefect, images[imageIndex], ComposeSetup(variant.apply, extra));
                tally.Check(reference.predictRc == 0 && actual.predictRc == 0, where + ": both predict");
                if (reference.predictRc != 0 || actual.predictRc != 0) continue;
                tally.Check(actual.normalRc == -2 && actual.hasNormal == 0 && actual.hasNg == 1, where + ": no GOOD class (GetNormalConfidence -2, HasNormalClass 0)");
                cv::Mat unionMap;
                cv::max(reference.ng, reference.ok, unionMap); // NG and OK components are disjoint
                std::string why;
                tally.Check(MapsClose(unionMap, actual.ng, 1, why), where + ": DEFECT map is the union of the source's NG and OK maps: " + why);
                cv::Mat expectedMask;
                cv::threshold(actual.ng, expectedMask, std::max(0., std::nearbyint(0.5 * 255.) - 1.), 255, cv::THRESH_BINARY);
                tally.Check(actual.mask.size() == expectedMask.total() &&
                            std::memcmp(actual.mask.data(), expectedMask.data, actual.mask.size()) == 0, where + ": InferPredict mask is the thresholded union");
            }
        }
    }

    // postprocess_version 1, the V158 diagnostic and winner foreground stay rejected for non-legacy layouts.
    for (const auto& rejected : std::vector<std::pair<std::string, std::filesystem::path>>{ { "N=4 postprocess_version 1", fourPostprocess1 }, { "permuted postprocess_version 1", permutedPostprocess1 } }) {
        const ClassModelResult result = RunClassModel(rejected.second, images[0], std::function<void(void*)>());
        tally.Check(result.loadRc == -5 && Contains(result.loadError, "postprocess_version"), rejected.first + ": load is rejected (rc " + std::to_string(result.loadRc) + ": " + result.loadError + ")");
    }
    {
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        const std::wstring wide = four.wstring();
        tally.Check(infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0, "N=4: loads for the diagnostic check");
        if (infer) {
            SEGAI_InferSetGpu(infer.get(), 0);
            tally.Check(SEGAI_InferSetObjectScale(infer.get(), 0.85f, 1.15f, 1) == 0, "N=4: multiscale on before the diagnostic call");
            uint8_t* packet = nullptr;
            uint64_t packetBytes = 0;
            const int32_t rc = SEGAI_InferPredictDiagnosticV1(infer.get(), images[0].data, images[0].cols, images[0].rows, static_cast<int32_t>(images[0].step),
                                                              0, 0, images[0].cols, images[0].rows, 0.5f, "test", "test", &packet, &packetBytes);
            std::unique_ptr<uint8_t, SegaiBufferDeleter> packetOwner(packet);
            tally.Check(rc == -5, "N=4: SEGAI_InferPredictDiagnosticV1 is -5 (was " + std::to_string(rc) + ")");
            tally.Check(SEGAI_InferSetV2BoundaryMode(infer.get(), 1) == -2, "N=4: winner foreground is rejected while multiscale is on");
        }
    }

    std::cout << "multiscale_candidate_non_empty_runs=" << candidateNonEmpty << "/" << candidateRuns << "\n";
    tally.Check(candidateNonEmpty > 0, "the generalised multiscale candidates predict foreground on at least one image/variant (not vacuous)");
    return tally.Finish("class-model-multiscale (T6)");
}

// ---- T7 (S2b) generalised class-count training -------------------------------------------------------------
//   --class-train <outDir> [<backbone.onnx>|-] [<fixtureDir>|-] [<epochs>]
//       Trains tiny V2 models with N = 3 and 4 classes, two DEFECT classes and no GOOD class, GOOD in the middle and a class
//       without pixels; masks are the fixture rectangles (as in --class-table-model) remapped to the class ids of each case,
//       with ignore patches (class id N + 1 and 255). Structure is checked first (file layout, stored table, load, predict
//       output shapes, sample counters, resume rules); the quality bar (class confidence inside the painted regions against the
//       background, per-region class labels and fractions of SEGAI_InferPredictMulti) is a hard check since S3.
//       Also N = 1 (one DEFECT class, K = 2), a name-only change on resume, a v3 source resumed by a v4 NG/OK table and a
//       disabled class (zero head column, bias -1e9, zero output channel).
//   --class-train-finetune <outDir> <backbone.onnx> <taps.pt> [<fixtureDir>|-] [n3]
//       Fine-tune hook: trains the standard 2-class tiny model with a one epoch backbone fine-tune, writes the .segai and prints
//       its SHA256 (finetune_sha256=). Run it in a fresh process against the HEAD DLL and the new DLL and compare. With "n3" it
//       also fine-tunes an N=3 model (structure checks only).

// SEGAI_InferPredictMulti through caller-owned planes; label/score are pre-filled with a sentinel so unwritten bytes are caught.
struct MultiResult {
    int rc = 0;
    std::string error;
    cv::Mat label, score; // score stays empty when not requested
    std::vector<float> fractions; // classCapacity entries, -1 = untouched
};

MultiResult RunPredictMulti(void* infer, const cv::Mat& image, const cv::Rect& roi, float threshold, bool wantScore, int32_t classCapacity)
{
    MultiResult result;
    result.label = cv::Mat(image.size(), CV_8U, cv::Scalar(77));
    if (wantScore) result.score = cv::Mat(image.size(), CV_8U, cv::Scalar(77));
    result.fractions.assign(static_cast<size_t>(std::max<int32_t>(classCapacity, 1)), -1.f);
    result.rc = SEGAI_InferPredictMulti(infer, image.data, image.cols, image.rows, static_cast<int32_t>(image.step),
                                        roi.x, roi.y, roi.width, roi.height, threshold,
                                        result.label.data, static_cast<int32_t>(result.label.step),
                                        wantScore ? result.score.data : nullptr, wantScore ? static_cast<int32_t>(result.score.step) : 0,
                                        result.fractions.data(), classCapacity);
    result.error = InferErrorText(infer);
    return result;
}

struct ClassTrainLayout {
    int blob1 = 0, blob2 = 0, center = 0, extra = 0, ignoreA = 0, ignoreB = 0; // class id painted into each region (0 = not painted)
};

ClassTrainLayout MakeClassTrainLayout(int blob1, int blob2, int center, int extra, int ignoreA, int ignoreB)
{
    ClassTrainLayout layout;
    layout.blob1 = blob1;
    layout.blob2 = blob2;
    layout.center = center;
    layout.extra = extra;
    layout.ignoreA = ignoreA;
    layout.ignoreB = ignoreB;
    return layout;
}

struct ClassTrainRegions {
    cv::Rect blob1, blob2, center, extra, ignoreA, ignoreB;
};

// S3: the regions are a fixed fraction of the image (about a fifth of each side) around the anchors of MakeTinyMask. The side-160
// blobs of S2b were smaller than the 3x3 open of the compact path (about 40 px on the long side) and were erased.
ClassTrainRegions MakeClassTrainRegions(const cv::Size& size)
{
    const int w = size.width, h = size.height;
    const cv::Rect frame(0, 0, w, h);
    const int rw = std::max(24, w / 5), rh = std::max(24, h / 5);
    ClassTrainRegions regions;
    regions.blob1 = cv::Rect(w / 12, h / 12, rw, rh) & frame;
    regions.extra = cv::Rect(w - rw - w / 12, h / 12, rw, rh) & frame;
    regions.center = cv::Rect(w / 2 - rw / 2, h / 2 - rh / 2, rw, rh) & frame;
    regions.blob2 = cv::Rect(w - rw - w / 12, h - rh - h / 12, rw, rh) & frame;
    regions.ignoreA = cv::Rect(w / 12, h - rh - h / 12, rw / 2, rh / 2) & frame;
    regions.ignoreB = cv::Rect(w / 12 + rw / 2 + 6, h - rh - h / 12, rw / 2, rh / 2) & frame;
    return regions;
}

cv::Mat MakeClassTrainMask(const cv::Size& size, const ClassTrainLayout& layout)
{
    const ClassTrainRegions regions = MakeClassTrainRegions(size);
    cv::Mat mask(size, CV_8UC1, cv::Scalar(0));
    const auto paint = [&mask](const cv::Rect& rect, int value) {
        if (value > 0 && !rect.empty()) cv::rectangle(mask, rect, cv::Scalar(value), cv::FILLED);
    };
    paint(regions.blob1, layout.blob1);
    paint(regions.blob2, layout.blob2);
    paint(regions.center, layout.center);
    paint(regions.extra, layout.extra);
    paint(regions.ignoreA, layout.ignoreA);
    paint(regions.ignoreB, layout.ignoreB);
    return mask;
}

std::string JsonPathText(const std::filesystem::path& path)
{
    std::string text = std::filesystem::absolute(path).u8string();
    for (char& c : text) if (c == '\\') c = '/';
    return text;
}

bool LoadTrainFixtureImages(const std::filesystem::path& fixtureDir, std::vector<cv::Mat>& images)
{
    images.clear();
    for (int index = 0; index < 2; ++index) {
        cv::Mat image = cv::imread((fixtureDir / (std::to_string(index) + ".png")).u8string(), cv::IMREAD_COLOR);
        if (!image.empty()) images.push_back(image);
    }
    return !images.empty();
}

// Expected pixel counters of a table over the masks: perClass[c] for ids 1..N, defect/normal summed by class kind.
void CountExpectedPixels(const std::vector<cv::Mat>& masks, const BeeSegAI::SegClassTable& table,
                         int& defect, int& normal, std::vector<int>& perClass)
{
    defect = 0;
    normal = 0;
    perClass.assign(table.classes.size() + 1, 0);
    for (const cv::Mat& mask : masks) {
        for (size_t index = 0; index < table.classes.size(); ++index) {
            const int count = cv::countNonZero(mask == static_cast<int>(index + 1));
            perClass[index + 1] += count;
            if (table.classes[index].kind == BeeSegAI::kClassKindGood) normal += count;
            else defect += count;
        }
    }
}

// Trains one tiny V2 model (side 160, CPU, no augmentation) and saves it. tableBytes == nullptr trains without a table (version 3).
// expectedDefect / expectedNormal >= 0: the trainer's pixel counters are compared with them after AddSample.
bool TrainMaskedModel(TestTally& tally, const std::string& label, const std::filesystem::path& runDir,
                      const std::filesystem::path& backbonePath, const std::vector<cv::Mat>& images, const std::vector<cv::Mat>& masks,
                      const std::vector<unsigned char>* tableBytes, int epochs, const std::string& extraSettings,
                      const std::filesystem::path& modelPath, int expectedDefect, int expectedNormal)
{
    std::error_code ec;
    const std::filesystem::path logsDir = runDir / "v2_logs";
    std::filesystem::create_directories(logsDir, ec);
    const std::filesystem::path settingsPath = runDir / "settings.json";
    {
        const std::string content = "{\"backbone\":\"" + JsonPathText(backbonePath) + "\",\"logs\":\"" + JsonPathText(logsDir) + "\","
            "\"epochs\":" + std::to_string(epochs) + ",\"side\":160,\"learningRate\":0.1,\"regularization\":0.0,\"rotation\":0.0,"
            "\"scaleVariation\":0.0,\"brightness\":0.0,\"augment\":0,\"gpu\":0,\"validation\":[]" + extraSettings + "}";
        std::ofstream settings(settingsPath, std::ios::binary);
        settings.write(content.c_str(), static_cast<std::streamsize>(content.size()));
    }

    void* trainer = SEGAI_TrainerCreate();
    if (!trainer) {
        tally.Check(false, label + ": TrainerCreate");
        return false;
    }
    bool ok = false;
    do {
        const std::wstring settingsWide = settingsPath.wstring();
        const int configureRc = SEGAI_TrainerConfigureV2(trainer, settingsWide.c_str());
        tally.Check(configureRc == 0, label + ": ConfigureV2 rc=" + std::to_string(configureRc) + " " + TrainerErrorText(trainer));
        if (configureRc != 0) break;
        if (tableBytes) {
            const int tableRc = SEGAI_TrainerSetClassTable(trainer, tableBytes->data(), static_cast<int32_t>(tableBytes->size()));
            tally.Check(tableRc == 0, label + ": SetClassTable rc=" + std::to_string(tableRc) + " " + TrainerErrorText(trainer));
            if (tableRc != 0) break;
        }
        int sampleRc = 0;
        for (size_t index = 0; index < images.size() && sampleRc == 0; ++index) {
            sampleRc = SEGAI_TrainerSetROI(trainer, 0, 0, images[index].cols, images[index].rows);
            if (sampleRc == 0) {
                sampleRc = SEGAI_TrainerAddSample(trainer, images[index].data, images[index].cols, images[index].rows,
                                                  static_cast<int>(images[index].step), masks[index].data, static_cast<int>(masks[index].step));
            }
        }
        tally.Check(sampleRc == 0, label + ": AddSample rc=" + std::to_string(sampleRc) + " " + TrainerErrorText(trainer));
        if (sampleRc != 0) break;
        if (expectedDefect >= 0 && expectedNormal >= 0) {
            int32_t defect = -1, normal = -1;
            const int countRc = SEGAI_TrainerSampleCount(trainer, &defect, &normal);
            tally.Check(countRc == 0 && defect == expectedDefect && normal == expectedNormal,
                        label + ": sample counters defect/normal are " + std::to_string(defect) + "/" + std::to_string(normal) +
                        " (expected " + std::to_string(expectedDefect) + "/" + std::to_string(expectedNormal) + ")");
        }
        const int trainRc = SEGAI_TrainerTrain(trainer, 0, 0, 0, nullptr, nullptr, nullptr);
        tally.Check(trainRc == 0, label + ": Train rc=" + std::to_string(trainRc) + " " + TrainerErrorText(trainer));
        if (trainRc != 0) break;
        const std::wstring modelWide = modelPath.wstring();
        const int saveRc = SEGAI_TrainerSave(trainer, modelWide.c_str(), 0.5f, 40);
        tally.Check(saveRc == 0, label + ": Save rc=" + std::to_string(saveRc) + " " + TrainerErrorText(trainer));
        ok = saveRc == 0;
    } while (false);
    SEGAI_TrainerDestroy(trainer);
    return ok;
}

double RegionMean(const cv::Mat& map, const cv::Rect& rect)
{
    const cv::Rect clipped = rect & cv::Rect(0, 0, map.cols, map.rows);
    if (map.empty() || clipped.empty()) return -1.0;
    return cv::mean(map(clipped))[0];
}

double OutsideMean(const cv::Mat& map, const std::vector<cv::Rect>& painted)
{
    if (map.empty()) return -1.0;
    cv::Mat keep(map.size(), CV_8UC1, cv::Scalar(255));
    for (const cv::Rect& rect : painted) {
        const cv::Rect grown = cv::Rect(rect.x - 8, rect.y - 8, rect.width + 16, rect.height + 16) & cv::Rect(0, 0, map.cols, map.rows);
        if (!grown.empty()) keep(grown).setTo(0);
    }
    return cv::mean(map, keep)[0];
}

struct ClassTrainCase {
    std::string label;
    std::vector<std::pair<std::string, uint8_t>> items;
    ClassTrainLayout layout;
};

// Trains the case (version 4 file), then checks the stored table, the file layout, loading and prediction.
bool RunClassTrainCase(TestTally& tally, const ClassTrainCase& testCase, const std::filesystem::path& runDir,
                       const std::filesystem::path& backbonePath, const std::vector<cv::Mat>& images, int epochs,
                       const std::string& extraSettings, const std::filesystem::path& modelPath, bool qualityIsHard = true)
{
    const std::string& label = testCase.label;
    const BeeSegAI::SegClassTable supplied = MakeClassTable(testCase.items);
    const size_t classCount = supplied.classes.size();
    std::vector<unsigned char> suppliedBytes;
    std::string error;
    tally.Check(BeeSegAI::EncodeClassTable(supplied, suppliedBytes, error), label + ": supplied table encodes: " + error);
    if (suppliedBytes.empty()) return false;

    std::vector<cv::Mat> masks;
    for (const cv::Mat& image : images) masks.push_back(MakeClassTrainMask(image.size(), testCase.layout));
    int expectedDefect = 0, expectedNormal = 0;
    std::vector<int> perClass;
    CountExpectedPixels(masks, supplied, expectedDefect, expectedNormal, perClass);

    // The stored table has the trainer-derived flags: a class without labelled pixels is disabled.
    BeeSegAI::SegClassTable expectedStored = supplied;
    bool anyDefectEnabled = false, goodEnabled = false;
    for (size_t index = 0; index < classCount; ++index) {
        expectedStored.classes[index].enabled = perClass[index + 1] > 0;
        if (expectedStored.classes[index].enabled) {
            if (expectedStored.classes[index].kind == BeeSegAI::kClassKindGood) goodEnabled = true;
            else anyDefectEnabled = true;
        }
    }
    std::vector<unsigned char> expectedStoredBytes;
    tally.Check(BeeSegAI::EncodeClassTable(expectedStored, expectedStoredBytes, error), label + ": expected stored table encodes: " + error);
    if (expectedStoredBytes.empty()) return false;

    if (!TrainMaskedModel(tally, label, runDir, backbonePath, images, masks, &suppliedBytes, epochs, extraSettings, modelPath,
                          expectedDefect, expectedNormal)) return false;

    // File layout: version 4, K = N + 1 classes, the table after the payload, no active_classes key.
    const std::vector<unsigned char> file = ReadFileBytes(modelPath);
    const SegaiLayout layout = ReadSegaiLayout(file);
    tally.Check(layout.valid && layout.version == 4u, label + ": header version is 4");
    tally.Check(layout.valid && layout.featureFlags == 0x02000000u && layout.featureCount == 448u && layout.numClasses == static_cast<uint32_t>(classCount + 1),
                label + ": numClasses is N + 1 = " + std::to_string(classCount + 1) + " (was " + std::to_string(layout.numClasses) + ")");
    tally.Check(layout.valid && layout.reservedA == expectedStoredBytes.size(), label + ": reservedA is the class-table size");
    tally.Check(layout.valid && file.size() == kSegaiHeaderBytes + layout.payloadSize + layout.reservedA, label + ": file size = 72 + payloadSize + reservedA");
    if (layout.valid && file.size() >= kSegaiHeaderBytes + layout.payloadSize + layout.reservedA) {
        const size_t payloadEnd = kSegaiHeaderBytes + static_cast<size_t>(layout.payloadSize);
        tally.Check(BeeSegAI::ClassTableCrc32(file.data() + kSegaiHeaderBytes, static_cast<size_t>(layout.payloadSize)) == layout.payloadCrc, label + ": payload CRC32");
        tally.Check(!ContainsText(file, kSegaiHeaderBytes, payloadEnd, "active_classes"), label + ": payload omits active_classes");
        tally.Check(file.size() == payloadEnd + expectedStoredBytes.size() &&
                    std::equal(expectedStoredBytes.begin(), expectedStoredBytes.end(), file.begin() + static_cast<std::ptrdiff_t>(payloadEnd)),
                    label + ": stored class table bytes (enabled = has pixels)");
    }

    // The saved head has K = N + 1 output columns; a disabled class has a zero column and bias -1e9 (so its output channel is zero).
    {
        cv::Mat headWeights, headBias;
        cv::FileStorage headFile((runDir / "v2_logs" / "head.yml").u8string(), cv::FileStorage::READ);
        if (headFile.isOpened()) {
            headFile["weights"] >> headWeights;
            headFile["bias"] >> headBias;
        }
        const bool readable = headWeights.type() == CV_32F && headWeights.cols == static_cast<int>(classCount + 1) &&
                              headBias.type() == CV_32F && headBias.total() == classCount + 1;
        tally.Check(readable, label + ": head.yml has K = N + 1 = " + std::to_string(classCount + 1) + " output columns");
        if (readable) {
            for (size_t index = 0; index < classCount; ++index) {
                const int column = static_cast<int>(index) + 1;
                if (expectedStored.classes[index].enabled) {
                    tally.Check(cv::countNonZero(headWeights.col(column)) > 0, label + ": enabled class " + std::to_string(column) + " has a trained head column");
                } else {
                    tally.Check(cv::countNonZero(headWeights.col(column)) == 0, label + ": disabled class " + std::to_string(column) + " has a zero head column");
                    tally.Check(headBias.ptr<float>()[column] <= -9.9e8f, label + ": disabled class " + std::to_string(column) + " has bias -1e9");
                }
            }
        }
    }

    // Load the model, read the table back and predict.
    const ClassModelResult result = RunClassModel(modelPath, images[0], std::function<void(void*)>());
    tally.Check(result.loadRc == 0, label + ": model loads: " + result.loadError);
    if (result.loadRc != 0) return false;
    tally.Check(result.tableRc == static_cast<int32_t>(expectedStoredBytes.size()) && result.tableBytes == expectedStoredBytes,
                label + ": InferGetClassTable returns the stored table");
    tally.Check(result.hasNg == (anyDefectEnabled ? 1 : 0), label + ": HasNgClass is " + std::to_string(anyDefectEnabled ? 1 : 0) + " (was " + std::to_string(result.hasNg) + ")");
    tally.Check(result.hasNormal == (goodEnabled ? 1 : 0), label + ": HasNormalClass is " + std::to_string(goodEnabled ? 1 : 0) + " (was " + std::to_string(result.hasNormal) + ")");
    tally.Check(result.predictRc == 0, label + ": InferPredict rc=" + std::to_string(result.predictRc) + " " + result.predictError);
    if (result.predictRc != 0) return false;
    tally.Check(result.mask.size() == images[0].total(), label + ": predicted mask covers the image");
    tally.Check(!result.ng.empty() && result.ng.size() == images[0].size(), label + ": DEFECT confidence map has the image size");
    if (goodEnabled) tally.Check(result.normalRc == 0 && !result.ok.empty() && result.ok.size() == images[0].size(), label + ": GOOD confidence map has the image size");
    else tally.Check(result.ok.empty(), label + ": no GOOD confidence map without an enabled GOOD class");

    // Quality bar (hard check): confidence inside the DEFECT regions against the rest of the image, the same for GOOD, then
    // the class labels and fractions of SEGAI_InferPredictMulti per painted region.
    const ClassTrainRegions regions = MakeClassTrainRegions(images[0].size());
    struct RegionClass { cv::Rect rect; int id; };
    const RegionClass painted[] = { { regions.blob1, testCase.layout.blob1 }, { regions.blob2, testCase.layout.blob2 },
                                    { regions.center, testCase.layout.center }, { regions.extra, testCase.layout.extra } };
    std::vector<cv::Rect> allPainted = { regions.ignoreA, regions.ignoreB };
    double defectInside = 0, goodInside = 0;
    int defectRegions = 0, goodRegions = 0;
    for (const RegionClass& region : painted) {
        if (region.id <= 0 || region.id > static_cast<int>(classCount)) continue;
        allPainted.push_back(region.rect);
        if (!expectedStored.classes[static_cast<size_t>(region.id) - 1].enabled) continue;
        if (expectedStored.classes[static_cast<size_t>(region.id) - 1].kind == BeeSegAI::kClassKindGood) {
            goodInside += RegionMean(result.ok, region.rect);
            ++goodRegions;
        } else {
            defectInside += RegionMean(result.ng, region.rect);
            ++defectRegions;
        }
    }
    const double defectOutside = OutsideMean(result.ng, allPainted);
    const double goodOutside = OutsideMean(result.ok, allPainted);
    std::ostringstream quality;
    quality << std::fixed << std::setprecision(1) << label << ": quality";
    bool defectBar = true, goodBar = true;
    if (defectRegions > 0) {
        defectInside /= defectRegions;
        quality << " defect_conf_inside=" << defectInside << " defect_conf_outside=" << defectOutside;
        defectBar = defectInside > defectOutside + 30.0;
    }
    if (goodRegions > 0) {
        goodInside /= goodRegions;
        quality << " good_conf_inside=" << goodInside << " good_conf_outside=" << goodOutside;
        goodBar = goodInside > goodOutside + 30.0;
    }
    quality << " bar(inside > outside + 30)=" << ((defectBar && goodBar) ? "met" : "below");
    std::cout << quality.str() << "\n";
    // qualityIsHard == false (one fine-tune epoch, or a fresh 20 epoch start inside a resume rule test): printed only.
    if (qualityIsHard) {
        tally.Check(defectBar, label + ": DEFECT confidence inside the DEFECT regions is above the rest of the image + 30");
        tally.Check(goodBar, label + ": GOOD confidence inside the GOOD regions is above the rest of the image + 30");
    }

    // SEGAI_InferPredictMulti on the trained model: labels, fractions, and the legacy mapping against InferPredict.
    std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
    const std::wstring modelWide = modelPath.wstring();
    const bool loaded = infer && SEGAI_InferLoad(infer.get(), modelWide.c_str()) == 0;
    tally.Check(loaded, label + ": model loads for PredictMulti");
    if (!loaded) return true;
    SEGAI_InferSetGpu(infer.get(), 0);
    const cv::Rect fullImage(0, 0, images[0].cols, images[0].rows);
    const MultiResult multi = RunPredictMulti(infer.get(), images[0], fullImage, 0.5f, true, static_cast<int32_t>(classCount));
    tally.Check(multi.rc == 0, label + ": PredictMulti rc=" + std::to_string(multi.rc) + " " + multi.error);
    if (multi.rc != 0) return true;
    unsigned char defectLut[256] = {};
    for (size_t index = 0; index < classCount; ++index) {
        if (supplied.classes[index].kind != BeeSegAI::kClassKindGood) defectLut[index + 1] = 255;
    }
    cv::Mat defectUnion;
    cv::LUT(multi.label, cv::Mat(1, 256, CV_8U, defectLut), defectUnion);
    tally.Check(result.mask.size() == defectUnion.total() && std::memcmp(result.mask.data(), defectUnion.data, result.mask.size()) == 0,
                label + ": union of the DEFECT labels equals the InferPredict mask");
    std::ostringstream regionText;
    regionText << std::fixed << std::setprecision(2) << label << ": PredictMulti region ratios";
    for (const RegionClass& region : painted) {
        if (region.id <= 0 || region.id > static_cast<int>(classCount) || region.rect.empty()) continue;
        const bool enabled = expectedStored.classes[static_cast<size_t>(region.id) - 1].enabled;
        const double ratio = static_cast<double>(cv::countNonZero(multi.label(region.rect) == region.id)) / static_cast<double>(region.rect.area());
        regionText << " [class " << region.id << (enabled ? "" : " disabled") << "]=" << ratio;
        if (enabled) { if (qualityIsHard) tally.Check(ratio >= 0.5, label + ": PredictMulti labels at least half of a class " + std::to_string(region.id) + " region as that class (ratio " + std::to_string(ratio) + ")"); }
        else tally.Check(ratio == 0.0, label + ": PredictMulti never labels a disabled class " + std::to_string(region.id));
    }
    std::cout << regionText.str() << "\n";
    const double roiArea = static_cast<double>(images[0].total());
    for (size_t index = 0; index < classCount; ++index) {
        const double expectedFraction = static_cast<double>(cv::countNonZero(multi.label == static_cast<int>(index + 1))) / roiArea;
        const float reported = multi.fractions[index];
        tally.Check(std::abs(static_cast<double>(reported) - expectedFraction) < 1e-6, label + ": class " + std::to_string(index + 1) + " fraction matches the label plane");
        if (!expectedStored.classes[index].enabled) tally.Check(reported == 0.f, label + ": disabled class " + std::to_string(index + 1) + " has fraction 0");
    }
    return true;
}

int RunClassTrainMode(int argc, char** argv)
{
    if (argc < 3) {
        std::cerr << "Usage: --class-train <outDir> [<backbone.onnx>|-] [<fixtureDir>|-] [<epochs>]\n";
        return 100;
    }
    const std::filesystem::path outDir = std::filesystem::u8path(argv[2]);
    std::filesystem::path backbonePath;
    if (argc > 3 && std::strcmp(argv[3], "-") != 0) {
        backbonePath = std::filesystem::u8path(argv[3]);
    } else {
        wchar_t exePath[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        backbonePath = std::filesystem::path(exePath).parent_path() / "Models" / "DefectV2" / "mobilenetv2-12.onnx";
    }
    const std::filesystem::path fixtureDir = (argc > 4 && std::strcmp(argv[4], "-") != 0)
        ? std::filesystem::u8path(argv[4]) : std::filesystem::u8path("tmp/EAI/EAI-3/fixture-src");
    int epochs = 80;
    if (argc > 5 && (!ParseIntArgument(argv[5], epochs) || epochs < 1 || epochs > 2000)) {
        std::cerr << "Invalid epochs\n";
        return 100;
    }
    if (!std::filesystem::exists(backbonePath)) {
        std::cerr << "V2 backbone is missing: " << backbonePath.u8string() << "\n";
        return 102;
    }
    std::error_code ec;
    std::filesystem::create_directories(outDir, ec);
    if (ec) {
        std::cerr << "Failed to create outDir\n";
        return 102;
    }
    std::vector<cv::Mat> images;
    if (!LoadTrainFixtureImages(fixtureDir, images)) {
        std::cerr << "Fixture images 0.png/1.png not found in " << fixtureDir.u8string() << "\n";
        return 102;
    }

    TestTally tally;
    const uint8_t defect = BeeSegAI::kClassKindDefect, good = BeeSegAI::kClassKindGood;
    std::vector<ClassTrainCase> cases;
    {
        ClassTrainCase item;
        item.label = "n3";
        item.items = { { "Scratch", defect }, { "Dent", defect }, { "Fine", good } };
        item.layout = MakeClassTrainLayout(1, 2, 3, 0, 4, 255); // class 4 = N + 1 and 255 are ignored
        cases.push_back(item);
        item = ClassTrainCase();
        item.label = "n4";
        item.items = { { "Scratch", defect }, { "Dent", defect }, { "Stain", defect }, { "Fine", good } };
        item.layout = MakeClassTrainLayout(1, 2, 4, 3, 5, 255);
        cases.push_back(item);
        item = ClassTrainCase();
        item.label = "two-defect-no-good";
        item.items = { { "Scratch", defect }, { "Dent", defect } };
        item.layout = MakeClassTrainLayout(1, 2, 0, 0, 3, 255);
        cases.push_back(item);
        item = ClassTrainCase();
        item.label = "good-in-the-middle";
        item.items = { { "Scratch", defect }, { "Fine", good }, { "Dent", defect } };
        item.layout = MakeClassTrainLayout(1, 3, 2, 0, 4, 255);
        cases.push_back(item);
        item = ClassTrainCase();
        item.label = "class-without-pixels";
        item.items = { { "Scratch", defect }, { "Dent", defect }, { "Stain", defect } }; // class 3 never appears: disabled
        item.layout = MakeClassTrainLayout(1, 2, 0, 0, 4, 255);
        cases.push_back(item);
        item = ClassTrainCase();
        item.label = "n1";
        item.items = { { "Scratch", defect } }; // one DEFECT class, K = 2
        item.layout = MakeClassTrainLayout(1, 1, 0, 1, 2, 255);
        cases.push_back(item);
        item = ClassTrainCase();
        item.label = "disabled-middle";
        item.items = { { "Scratch", defect }, { "Dent", defect }, { "Fine", good } }; // class 2 never appears: disabled between two enabled classes
        item.layout = MakeClassTrainLayout(1, 3, 0, 3, 4, 255);
        cases.push_back(item);
    }
    std::filesystem::path n3Model;
    for (const ClassTrainCase& testCase : cases) {
        const std::filesystem::path model = outDir / (testCase.label + ".segai");
        const bool trained = RunClassTrainCase(tally, testCase, outDir / testCase.label, backbonePath, images, epochs, std::string(), model);
        if (trained && testCase.label == "n3") n3Model = model;
    }

    // Resume rules with N = 3: the same table resumes; a changed kind, or no table at all (K = 3), starts fresh.
    if (!n3Model.empty()) {
        const std::string resumeSetting = ",\"resumeFrom\":\"" + JsonPathText(n3Model) + "\"";
        struct ResumeCase { const char* label; std::vector<std::pair<std::string, uint8_t>> items; bool expectResumed; };
        const ResumeCase resumeCases[] = {
            { "resume-same-table", { { "Scratch", defect }, { "Dent", defect }, { "Fine", good } }, true },
            { "resume-changed-kind", { { "Scratch", defect }, { "Dent", good }, { "Fine", defect } }, false },
            // Same kinds in the same order, other names: the class set is different, so the resume is dropped.
            { "resume-names-only", { { "Alpha", defect }, { "Beta", defect }, { "Gamma", good } }, false },
        };
        for (const ResumeCase& resumeCase : resumeCases) {
            ClassTrainCase item;
            item.label = resumeCase.label;
            item.items = resumeCase.items;
            item.layout = MakeClassTrainLayout(1, 2, 3, 0, 4, 255);
            const std::filesystem::path runDir = outDir / item.label;
            // Only a resumed head carries the quality bar over; a fresh 20 epoch start is checked for structure.
            if (!RunClassTrainCase(tally, item, runDir, backbonePath, images, 20, resumeSetting, outDir / (item.label + ".segai"), resumeCase.expectResumed)) continue;
            const std::string status = ReadTrimmedText(runDir / "v2_logs" / "resume.txt");
            std::cout << item.label << " -> " << status << "\n";
            if (resumeCase.expectResumed) tally.Check(status.compare(0, 7, "resumed") == 0, item.label + ": the head is resumed (status was: " + status + ")");
            else tally.Check(status == "fresh; class set changed", item.label + ": fresh start because the class kinds changed (status was: " + status + ")");
        }
        {
            // No table: the trainer is the K = 3 NG/OK trainer and cannot resume an N = 3 head.
            std::vector<cv::Mat> masks;
            for (const cv::Mat& image : images) masks.push_back(MakeTinyMask(image));
            const std::filesystem::path runDir = outDir / "resume-no-table";
            if (TrainMaskedModel(tally, "resume-no-table", runDir, backbonePath, images, masks, nullptr, 20, resumeSetting, outDir / "resume-no-table.segai", -1, -1)) {
                const std::string status = ReadTrimmedText(runDir / "v2_logs" / "resume.txt");
                std::cout << "resume-no-table -> " << status << "\n";
                tally.Check(status == "fresh; class set changed", "resume-no-table: fresh start because the class set changed (status was: " + status + ")");
            }
        }
    }

    // A version 3 source (trained without a table, legacy "NG"/"OK") resumed by a v4 run: the NG/OK DEFECT/GOOD table resumes,
    // any other names start fresh.
    {
        std::vector<cv::Mat> masks;
        for (const cv::Mat& image : images) masks.push_back(MakeTinyMask(image));
        const std::filesystem::path v3Model = outDir / "v3-source.segai";
        if (TrainMaskedModel(tally, "v3-source", outDir / "v3-source", backbonePath, images, masks, nullptr, 20, std::string(), v3Model, -1, -1)) {
            const std::vector<unsigned char> v3File = ReadFileBytes(v3Model);
            const SegaiLayout v3Layout = ReadSegaiLayout(v3File);
            tally.Check(v3Layout.valid && v3Layout.version == 3u, "v3-source: the table-less trainer writes a version 3 file");
            const std::string resumeSetting = ",\"resumeFrom\":\"" + JsonPathText(v3Model) + "\"";
            struct V3ResumeCase { const char* label; std::vector<std::pair<std::string, uint8_t>> items; bool expectResumed; };
            const V3ResumeCase v3Cases[] = {
                { "resume-v3-by-ngok-table", { { "NG", defect }, { "OK", good } }, true },
                { "resume-v3-by-renamed-table", { { "Scratch", defect }, { "Fine", good } }, false },
            };
            for (const V3ResumeCase& v3Case : v3Cases) {
                ClassTrainCase item;
                item.label = v3Case.label;
                item.items = v3Case.items;
                item.layout = MakeClassTrainLayout(1, 1, 2, 0, 3, 255);
                const std::filesystem::path runDir = outDir / item.label;
                if (!RunClassTrainCase(tally, item, runDir, backbonePath, images, 20, resumeSetting, outDir / (item.label + ".segai"), false)) continue; // structure only: both heads are 20 epoch models
                const std::string status = ReadTrimmedText(runDir / "v2_logs" / "resume.txt");
                std::cout << item.label << " -> " << status << "\n";
                if (v3Case.expectResumed) tally.Check(status.compare(0, 7, "resumed") == 0, item.label + ": the v3 head is resumed by the NG/OK v4 table (status was: " + status + ")");
                else tally.Check(status == "fresh; class set changed", item.label + ": fresh start because the names differ from NG/OK (status was: " + status + ")");
            }
        }
    }
    return tally.Finish("class-train (T7)");
}

int RunClassTrainFineTuneMode(int argc, char** argv)
{
    if (argc < 5) {
        std::cerr << "Usage: --class-train-finetune <outDir> <backbone.onnx> <taps.pt> [<fixtureDir>|-] [n3]\n";
        return 100;
    }
    const std::filesystem::path outDir = std::filesystem::u8path(argv[2]);
    const std::filesystem::path backbonePath = std::filesystem::u8path(argv[3]);
    const std::filesystem::path tapsPath = std::filesystem::u8path(argv[4]);
    const std::filesystem::path fixtureDir = (argc > 5 && std::strcmp(argv[5], "-") != 0)
        ? std::filesystem::u8path(argv[5]) : std::filesystem::u8path("tmp/EAI/EAI-3/fixture-src");
    const bool withN3 = argc > 6 && std::strcmp(argv[6], "n3") == 0;
    if (!std::filesystem::exists(backbonePath) || !std::filesystem::exists(tapsPath)) {
        std::cerr << "V2 backbone or TorchScript taps file is missing\n";
        return 102;
    }
    std::error_code ec;
    std::filesystem::create_directories(outDir, ec);
    if (ec) {
        std::cerr << "Failed to create outDir\n";
        return 102;
    }
    std::vector<cv::Mat> images;
    if (!LoadTrainFixtureImages(fixtureDir, images)) {
        std::cerr << "Fixture images 0.png/1.png not found in " << fixtureDir.u8string() << "\n";
        return 102;
    }

    TestTally tally;
    const std::string fineTune = ",\"backboneEpochs\":1,\"backboneThreads\":2,\"backboneTorch\":\"" + JsonPathText(tapsPath) + "\"";
    std::vector<cv::Mat> masks;
    for (const cv::Mat& image : images) masks.push_back(MakeTinyMask(image));
    const std::filesystem::path model = outDir / "finetune-2class.segai";
    const bool trained = TrainMaskedModel(tally, "2-class fine-tune", outDir / "finetune-2class", backbonePath, images, masks, nullptr, 1, fineTune, model, -1, -1);
    if (!trained) {
        const int exitCode = tally.Finish("class-train-finetune (T7b)");
        return exitCode != 0 ? 103 : 0;
    }
    const std::vector<unsigned char> file = ReadFileBytes(model);
    CheckSegaiLayout(tally, "2-class fine-tune model", file, nullptr);
    std::cout << "finetune_size=" << file.size() << "\nfinetune_sha256=" << ComputeSha256(file) << "\n";
    std::cout << "finetune_status=" << ReadTrimmedText(outDir / "finetune-2class" / "v2_logs" / "finetune.txt") << "\n";

    if (withN3) {
        ClassTrainCase item;
        item.label = "finetune-n3";
        item.items = { { "Scratch", BeeSegAI::kClassKindDefect }, { "Dent", BeeSegAI::kClassKindDefect }, { "Fine", BeeSegAI::kClassKindGood } };
        item.layout = MakeClassTrainLayout(1, 2, 3, 0, 4, 255);
        RunClassTrainCase(tally, item, outDir / item.label, backbonePath, images, 1, fineTune, outDir / "finetune-n3.segai", false);
        std::cout << "finetune_n3_status=" << ReadTrimmedText(outDir / item.label / "v2_logs" / "finetune.txt") << "\n";
    }
    return tally.Finish("class-train-finetune (T7b)");
}

// ---- T8 (S3) SEGAI_InferPredictMulti -------------------------------------------------------------------------
//   --class-multi <outDir> [<backbone.onnx>|-] [<fixtureDir>|-] [<epochs>]
//       Trains a legacy table-less (version 3, NG/OK) model and a 3-class model. Checks the ABI (null handle, no model -9, bad
//       arguments -1, class capacity -4 without inference, capacity larger than N leaves the tail untouched), full-image outputs
//       with zeros outside a sub-ROI, the nullable score plane, that PredictMulti updates the Predict state (GetConfidence), the
//       legacy [DEFECT, GOOD] mapping (label 1/2 with NG precedence equals the InferPredict mask and confidence maps) and, for
//       the 3-class model, train -> save -> load -> PredictMulti (class labels per painted region, fractions).

int RunClassMultiMode(int argc, char** argv)
{
    if (argc < 3) {
        std::cerr << "Usage: --class-multi <outDir> [<backbone.onnx>|-] [<fixtureDir>|-] [<epochs>]\n";
        return 100;
    }
    const std::filesystem::path outDir = std::filesystem::u8path(argv[2]);
    std::filesystem::path backbonePath;
    if (argc > 3 && std::strcmp(argv[3], "-") != 0) {
        backbonePath = std::filesystem::u8path(argv[3]);
    } else {
        wchar_t exePath[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        backbonePath = std::filesystem::path(exePath).parent_path() / "Models" / "DefectV2" / "mobilenetv2-12.onnx";
    }
    const std::filesystem::path fixtureDir = (argc > 4 && std::strcmp(argv[4], "-") != 0)
        ? std::filesystem::u8path(argv[4]) : std::filesystem::u8path("tmp/EAI/EAI-3/fixture-src");
    int epochs = 80;
    if (argc > 5 && (!ParseIntArgument(argv[5], epochs) || epochs < 1 || epochs > 2000)) {
        std::cerr << "Invalid epochs\n";
        return 100;
    }
    if (!std::filesystem::exists(backbonePath)) {
        std::cerr << "V2 backbone is missing: " << backbonePath.u8string() << "\n";
        return 102;
    }
    std::error_code ec;
    std::filesystem::create_directories(outDir, ec);
    if (ec) {
        std::cerr << "Failed to create outDir\n";
        return 102;
    }
    std::vector<cv::Mat> images;
    if (!LoadTrainFixtureImages(fixtureDir, images)) {
        std::cerr << "Fixture images 0.png/1.png not found in " << fixtureDir.u8string() << "\n";
        return 102;
    }
    const cv::Mat& image = images[0];
    const cv::Rect fullImage(0, 0, image.cols, image.rows);

    TestTally tally;
    const float threshold = 0.5f;
    const double cutoff = std::max(0., std::nearbyint(threshold * 255.) - 1.); // the cutoff of InferPredict
    const double fullArea = static_cast<double>(image.total());

    // Handles without a model.
    {
        const MultiResult noHandle = RunPredictMulti(nullptr, image, fullImage, threshold, true, 4);
        tally.Check(noHandle.rc == -1, "PredictMulti(null handle) is -1 (was " + std::to_string(noHandle.rc) + ")");
        std::unique_ptr<void, InferHandleDeleter> empty(SEGAI_InferCreate());
        tally.Check(static_cast<bool>(empty), "InferCreate for the no-model check");
        if (empty) {
            const MultiResult noModel = RunPredictMulti(empty.get(), image, fullImage, threshold, true, 4);
            tally.Check(noModel.rc == -9 && !noModel.error.empty(), "PredictMulti without a model is -9 with a message (was " + std::to_string(noModel.rc) + ": " + noModel.error + ")");
        }
    }

    // ---- legacy table-less model (version 3, NG/OK) ----
    std::vector<cv::Mat> legacyMasks;
    for (const cv::Mat& item : images) legacyMasks.push_back(MakeTinyMask(item));
    const std::filesystem::path legacyModel = outDir / "legacy-v3.segai";
    const bool legacyTrained = TrainMaskedModel(tally, "legacy", outDir / "legacy", backbonePath, images, legacyMasks, nullptr, 20, std::string(), legacyModel, -1, -1);
    if (legacyTrained) {
        std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
        const std::wstring wide = legacyModel.wstring();
        const bool loaded = infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0;
        tally.Check(loaded, "legacy: model loads");
        if (loaded) {
            SEGAI_InferSetGpu(infer.get(), 0);
            void* handle = infer.get();
            // Argument checks.
            {
                MultiResult tooSmall = RunPredictMulti(handle, image, fullImage, threshold, true, 1);
                tally.Check(tooSmall.rc == -4 && !tooSmall.error.empty(), "legacy: class capacity 1 < N = 2 is -4 with a message (was " + std::to_string(tooSmall.rc) + ")");
                tally.Check(cv::countNonZero(tooSmall.label != 77) == 0 && tooSmall.fractions[0] == -1.f, "legacy: -4 writes nothing");
                float fractions[8] = {};
                cv::Mat label(image.size(), CV_8U);
                const int32_t nullLabel = SEGAI_InferPredictMulti(handle, image.data, image.cols, image.rows, static_cast<int32_t>(image.step), 0, 0, image.cols, image.rows,
                                                                  threshold, nullptr, image.cols, nullptr, 0, fractions, 8);
                tally.Check(nullLabel == -1, "legacy: null label plane is -1 (was " + std::to_string(nullLabel) + ")");
                const int32_t nullFractions = SEGAI_InferPredictMulti(handle, image.data, image.cols, image.rows, static_cast<int32_t>(image.step), 0, 0, image.cols, image.rows,
                                                                      threshold, label.data, static_cast<int32_t>(label.step), nullptr, 0, nullptr, 8);
                tally.Check(nullFractions == -1, "legacy: null fraction buffer is -1 (was " + std::to_string(nullFractions) + ")");
                const int32_t shortStride = SEGAI_InferPredictMulti(handle, image.data, image.cols, image.rows, static_cast<int32_t>(image.step), 0, 0, image.cols, image.rows,
                                                                    threshold, label.data, image.cols - 1, nullptr, 0, fractions, 8);
                tally.Check(shortStride == -1, "legacy: label step smaller than the width is -1 (was " + std::to_string(shortStride) + ")");
                const MultiResult badThreshold = RunPredictMulti(handle, image, fullImage, 2.f, true, 2);
                tally.Check(badThreshold.rc == -2, "legacy: threshold 2.0 is -2 like InferPredict (was " + std::to_string(badThreshold.rc) + ")");
            }
            // Full image with score: compare with InferPredict and the confidence maps of the same handle.
            const MultiResult full = RunPredictMulti(handle, image, fullImage, threshold, true, 2);
            tally.Check(full.rc == 0, "legacy: PredictMulti rc=" + std::to_string(full.rc) + " " + full.error);
            if (full.rc == 0) {
                uint8_t* confidencePtr = nullptr;
                int32_t confidenceW = 0, confidenceH = 0;
                const int32_t confidenceRc = SEGAI_InferGetConfidence(handle, &confidencePtr, &confidenceW, &confidenceH);
                std::unique_ptr<uint8_t, SegaiBufferDeleter> confidenceOwner(confidencePtr);
                tally.Check(confidenceRc == 0 && confidenceW == image.cols && confidenceH == image.rows, "legacy: PredictMulti updates the Predict state (GetConfidence rc " + std::to_string(confidenceRc) + ")");
                // InferPredict on the same handle replaces the maps with the same values (deterministic CPU run).
                uint8_t* maskPtr = nullptr;
                int32_t maskW = 0, maskH = 0;
                float maskScore = 0.f;
                const int32_t predictRc = SEGAI_InferPredict(handle, image.data, image.cols, image.rows, static_cast<int32_t>(image.step), 0, 0, image.cols, image.rows, threshold, &maskPtr, &maskW, &maskH, &maskScore);
                std::unique_ptr<uint8_t, SegaiBufferDeleter> maskOwner(maskPtr);
                tally.Check(predictRc == 0 && maskPtr && maskW == image.cols && maskH == image.rows, "legacy: InferPredict rc=" + std::to_string(predictRc));
                uint8_t* ngPtr = nullptr;
                uint8_t* okPtr = nullptr;
                int32_t mapW = 0, mapH = 0;
                const int32_t ngRc = SEGAI_InferGetConfidence(handle, &ngPtr, &mapW, &mapH);
                std::unique_ptr<uint8_t, SegaiBufferDeleter> ngOwner(ngPtr);
                const int32_t okRc = SEGAI_InferGetNormalConfidence(handle, &okPtr, &mapW, &mapH);
                std::unique_ptr<uint8_t, SegaiBufferDeleter> okOwner(okPtr);
                if (predictRc == 0 && maskPtr && ngRc == 0 && okRc == 0) {
                    const cv::Mat ng(image.size(), CV_8U, ngPtr), ok(image.size(), CV_8U, okPtr), mask(image.size(), CV_8U, maskPtr);
                    cv::Mat ngAccepted, okAccepted, expectedLabel = cv::Mat::zeros(image.size(), CV_8U), expectedScore = cv::Mat::zeros(image.size(), CV_8U);
                    cv::threshold(ng, ngAccepted, cutoff, 255, cv::THRESH_BINARY);
                    cv::threshold(ok, okAccepted, cutoff, 255, cv::THRESH_BINARY);
                    expectedLabel.setTo(2, okAccepted);
                    ok.copyTo(expectedScore, okAccepted);
                    expectedLabel.setTo(1, ngAccepted); // NG precedence
                    ng.copyTo(expectedScore, ngAccepted);
                    tally.Check(cv::countNonZero(full.label != expectedLabel) == 0, "legacy: label plane = NG (1) over OK (2) of the accepted confidence maps");
                    tally.Check(cv::countNonZero(full.score != expectedScore) == 0, "legacy: score plane = the confidence of the owning class");
                    tally.Check(cv::countNonZero((full.label == 1) != (mask != 0)) == 0, "legacy: label 1 equals the InferPredict mask");
                    for (int classId = 1; classId <= 2; ++classId) {
                        const double expected = static_cast<double>(cv::countNonZero(full.label == classId)) / fullArea;
                        tally.Check(std::abs(static_cast<double>(full.fractions[static_cast<size_t>(classId) - 1]) - expected) < 1e-6, "legacy: fraction of class " + std::to_string(classId) + " matches the label plane");
                    }
                    std::cout << "legacy fractions: NG=" << full.fractions[0] << " OK=" << full.fractions[1] << " mask_score=" << maskScore << "\n";
                    tally.Check(std::abs(static_cast<double>(full.fractions[0]) - static_cast<double>(maskScore)) < 1e-6, "legacy: NG fraction equals the InferPredict score");
                    tally.Check(cv::countNonZero(full.label == 1) + cv::countNonZero(full.label == 2) > 0, "legacy: the labels are not all empty (not vacuous)");
                }
            }
            // Nullable score plane: the same label plane without it.
            {
                const MultiResult noScore = RunPredictMulti(handle, image, fullImage, threshold, false, 2);
                tally.Check(noScore.rc == 0, "legacy: PredictMulti with a null score plane rc=" + std::to_string(noScore.rc));
                if (noScore.rc == 0 && full.rc == 0) tally.Check(cv::countNonZero(noScore.label != full.label) == 0, "legacy: a null score plane does not change the label plane");
            }
            // Sub-ROI: zeros outside, fractions over the ROI area.
            {
                const cv::Rect roi(image.cols / 4, image.rows / 4, image.cols / 2, image.rows / 2);
                const MultiResult sub = RunPredictMulti(handle, image, roi, threshold, true, 4);
                tally.Check(sub.rc == 0, "legacy: PredictMulti with a ROI rc=" + std::to_string(sub.rc) + " " + sub.error);
                if (sub.rc == 0) {
                    cv::Mat outside(image.size(), CV_8U, cv::Scalar(255));
                    outside(roi).setTo(0);
                    tally.Check(cv::countNonZero(sub.label & outside) == 0 && cv::countNonZero(sub.score & outside) == 0, "legacy: label and score are zero outside the ROI");
                    for (int classId = 1; classId <= 2; ++classId) {
                        const double expected = static_cast<double>(cv::countNonZero(sub.label(roi) == classId)) / static_cast<double>(roi.area());
                        tally.Check(std::abs(static_cast<double>(sub.fractions[static_cast<size_t>(classId) - 1]) - expected) < 1e-6, "legacy: ROI fraction of class " + std::to_string(classId) + " is relative to the ROI area");
                    }
                    tally.Check(sub.fractions[2] == -1.f && sub.fractions[3] == -1.f, "legacy: fraction entries beyond N are untouched");
                }
            }
        }
    }

    // ---- 3-class model: train -> save -> load -> PredictMulti (RunClassTrainCase runs the hard quality bar and the legacy-union check) ----
    {
        const BeeSegAI::SegClassTable table = MakeClassTable({ { "Scratch", BeeSegAI::kClassKindDefect }, { "Dent", BeeSegAI::kClassKindDefect }, { "Fine", BeeSegAI::kClassKindGood } });
        ClassTrainCase item;
        item.label = "multi-n3";
        for (const BeeSegAI::SegClassEntry& entry : table.classes) item.items.push_back(std::make_pair(entry.name, entry.kind));
        item.layout = MakeClassTrainLayout(1, 2, 3, 0, 4, 255);
        const std::filesystem::path model = outDir / "multi-n3.segai";
        const bool trained = RunClassTrainCase(tally, item, outDir / item.label, backbonePath, images, epochs, std::string(), model, true);
        if (trained) {
            std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
            const std::wstring wide = model.wstring();
            const bool loaded = infer && SEGAI_InferLoad(infer.get(), wide.c_str()) == 0;
            tally.Check(loaded, "multi-n3: model loads");
            if (loaded) {
                SEGAI_InferSetGpu(infer.get(), 0);
                const MultiResult tooSmall = RunPredictMulti(infer.get(), image, fullImage, threshold, true, 2);
                tally.Check(tooSmall.rc == -4 && cv::countNonZero(tooSmall.label != 77) == 0, "multi-n3: class capacity 2 < N = 3 is -4 and writes nothing (was " + std::to_string(tooSmall.rc) + ")");
                const MultiResult wide32 = RunPredictMulti(infer.get(), image, fullImage, threshold, true, 32);
                tally.Check(wide32.rc == 0, "multi-n3: capacity 32 rc=" + std::to_string(wide32.rc) + " " + wide32.error);
                if (wide32.rc == 0) {
                    tally.Check(wide32.fractions[3] == -1.f && wide32.fractions[31] == -1.f, "multi-n3: fraction entries beyond N are untouched");
                    double maximumLabel = 0;
                    cv::minMaxLoc(wide32.label, nullptr, &maximumLabel);
                    tally.Check(maximumLabel <= 3.0, "multi-n3: labels are class ids 0..N");
                    cv::Mat labelled = wide32.label != 0, scored = wide32.score != 0;
                    cv::Mat mismatch;
                    cv::bitwise_xor(labelled, scored, mismatch);
                    tally.Check(cv::countNonZero(mismatch) == 0, "multi-n3: a pixel has a score exactly where it has a label");
                    cv::Mat belowCutoff;
                    cv::threshold(wide32.score, belowCutoff, cutoff, 255, cv::THRESH_BINARY_INV);
                    tally.Check(cv::countNonZero(belowCutoff & labelled) == 0, "multi-n3: every labelled pixel has a score above the threshold cutoff");
                    double sum = 0;
                    for (int index = 0; index < 3; ++index) sum += wide32.fractions[static_cast<size_t>(index)];
                    tally.Check(sum <= 1.0 + 1e-6, "multi-n3: the class fractions sum to at most 1");
                    std::cout << "multi-n3 fractions: " << wide32.fractions[0] << " " << wide32.fractions[1] << " " << wide32.fractions[2] << "\n";
                }
            }
        }
    }
    return tally.Finish("class-multi (T8)");
}

// ---- EAI-POS P3: instance head -----------------------------------------------------------------------------
//   --inst-head-selftest <outDir> [<backbone.onnx>|-] [<instanceEpochs>]
// Needs the real MobileNetV2 backbone (the same file as --class-train; the default is Models\DefectV2 next to the exe).
// Checks, in order: InstApi_Probe (versions, struct sizes, null/invalid arguments, AddInstances state codes, the rejected setting
// combinations, DecodeInstances with NaN regression), then trains two models on the same 12 synthetic scenes (touching / overlapping
// discs = class 0 and rotated squares = class 1, 640 px): one with instanceHead=0 and one with instanceHead=1 and checks
// DetectBitIdentical_InstanceOnOff, InstanceKeys_PayloadScanWindowUnchanged, LegacyV3V4_Load_NoInstance (the off model as v3 and as a
// v4 table model), InstSynth_TouchingDiscs on 8 unseen scenes (recall >= .98, centre error <= 8 px = 1 grid cell, no spurious
// instances), the capacity contract (-4 with *outCount set, nothing written) and the ROI offset. Prints the measured numbers.

static_assert(sizeof(SegInstanceV1) == 32, "SegInstanceV1 is 32 bytes");
static_assert(sizeof(SegInstanceResultV1) == 40, "SegInstanceResultV1 is 40 bytes");

struct SynthObject {
    int cls = 0;          // 0 disc, 1 rotated square / rectangle
    bool circle = false;
    double cx = 0, cy = 0, w = 0, h = 0, theta = 0;
};

constexpr int kInstSide = 640;
constexpr double kInstCentreLimit = 8.0; // one grid cell at the 640 input

void SynthCorners(const SynthObject& o, cv::Point2f out[4])
{
    const double ux = std::cos(o.theta), uy = std::sin(o.theta), vx = -uy, vy = ux;
    const double sx[4] = { -1, 1, 1, -1 }, sy[4] = { -1, -1, 1, 1 }; // TL, TR, BR, BL
    for (int i = 0; i < 4; ++i) {
        out[i].x = static_cast<float>(o.cx + ux * o.w * 0.5 * sx[i] + vx * o.h * 0.5 * sy[i]);
        out[i].y = static_cast<float>(o.cy + uy * o.w * 0.5 * sx[i] + vy * o.h * 0.5 * sy[i]);
    }
}

// One 640x640 scene. Touching and overlapping objects are the point: 65% of the objects are placed against an earlier one.
void MakeInstanceScene(unsigned seed, cv::Mat& image, cv::Mat& mask, std::vector<SynthObject>& objects)
{
    cv::RNG rng(seed);
    image = cv::Mat(kInstSide, kInstSide, CV_8UC3);
    rng.fill(image, cv::RNG::NORMAL, cv::Scalar::all(96), cv::Scalar::all(5));
    mask = cv::Mat::zeros(kInstSide, kInstSide, CV_8U);
    objects.clear();
    const int wanted = 6 + rng.uniform(0, 3);
    auto inscribed = [](const SynthObject& o) { return 0.5 * std::min(o.w, o.h); };
    for (int attempt = 0; attempt < 600 && static_cast<int>(objects.size()) < wanted; ++attempt) {
        SynthObject o;
        o.circle = rng.uniform(0, 2) == 0;
        o.cls = o.circle ? 0 : 1;
        if (o.circle) { o.w = o.h = 2.0 * rng.uniform(26., 38.); }
        else { o.w = rng.uniform(52., 76.); o.h = rng.uniform(52., 76.); o.theta = rng.uniform(-CV_PI, CV_PI); }
        const double extent = o.circle ? o.w * 0.5 : 0.5 * std::hypot(o.w, o.h);
        if (!objects.empty() && rng.uniform(0., 1.) < 0.65) {
            const SynthObject& anchor = objects[static_cast<size_t>(rng.uniform(0, static_cast<int>(objects.size())))];
            const double distance = rng.uniform(0.8, 1.05) * (inscribed(anchor) + inscribed(o)); // overlapping .. just touching
            const double direction = rng.uniform(0., 2 * CV_PI);
            o.cx = anchor.cx + distance * std::cos(direction);
            o.cy = anchor.cy + distance * std::sin(direction);
        } else {
            o.cx = rng.uniform(extent + 4, kInstSide - extent - 4);
            o.cy = rng.uniform(extent + 4, kInstSide - extent - 4);
        }
        if (o.cx < extent + 4 || o.cy < extent + 4 || o.cx > kInstSide - extent - 4 || o.cy > kInstSide - extent - 4) continue;
        bool crowded = false;
        for (const SynthObject& other : objects) if (std::hypot(other.cx - o.cx, other.cy - o.cy) < 24.0) crowded = true;
        if (crowded) continue;
        objects.push_back(o);
    }
    for (const SynthObject& o : objects) {
        const cv::Scalar fill = o.circle ? cv::Scalar(60 + rng.uniform(-14, 15), 170 + rng.uniform(-14, 15), 230 + rng.uniform(-14, 15))
                                         : cv::Scalar(70 + rng.uniform(-14, 15), 200 + rng.uniform(-14, 15), 90 + rng.uniform(-14, 15));
        const cv::Scalar edge = fill * 0.45;
        if (o.circle) {
            const cv::Point centre(static_cast<int>(std::lround(o.cx)), static_cast<int>(std::lround(o.cy)));
            const int radius = static_cast<int>(std::lround(o.w * 0.5));
            cv::circle(image, centre, radius, fill, cv::FILLED, cv::LINE_AA);
            cv::circle(image, centre, radius, edge, 2, cv::LINE_AA);
            cv::circle(mask, centre, radius, cv::Scalar(1), cv::FILLED);
        } else {
            cv::Point2f corners[4];
            SynthCorners(o, corners);
            std::vector<cv::Point> polygon;
            for (const cv::Point2f& c : corners) polygon.emplace_back(static_cast<int>(std::lround(c.x)), static_cast<int>(std::lround(c.y)));
            cv::fillConvexPoly(image, polygon, fill, cv::LINE_AA);
            cv::polylines(image, polygon, true, edge, 2, cv::LINE_AA);
            cv::fillConvexPoly(mask, polygon, cv::Scalar(2));
        }
    }
}

std::filesystem::path WriteInstSettings(const std::filesystem::path& runDir, const std::filesystem::path& backbone, int side, int epochs, const std::string& extra)
{
    std::error_code ec;
    const std::filesystem::path logsDir = runDir / "v2_logs";
    std::filesystem::create_directories(logsDir, ec);
    const std::filesystem::path settingsPath = runDir / "settings.json";
    const std::string content = "{\"backbone\":\"" + JsonPathText(backbone) + "\",\"logs\":\"" + JsonPathText(logsDir) + "\","
        "\"epochs\":" + std::to_string(epochs) + ",\"side\":" + std::to_string(side) + ",\"learningRate\":0.05,\"regularization\":0.0,\"rotation\":20.0,"
        "\"scaleVariation\":0.1,\"brightness\":0.1,\"augment\":1,\"gpu\":0,\"validation\":[]" + extra + "}";
    std::ofstream settings(settingsPath, std::ios::binary);
    settings.write(content.c_str(), static_cast<std::streamsize>(content.size()));
    return settingsPath;
}

SegInstanceV1 MakeInstanceItem(const SynthObject& o)
{
    SegInstanceV1 item = {};
    item.structSize = static_cast<int32_t>(sizeof(SegInstanceV1));
    item.kind = o.circle ? 1 : 0;
    item.cx = static_cast<float>(o.cx);
    item.cy = static_cast<float>(o.cy);
    item.w = static_cast<float>(o.w);
    item.h = static_cast<float>(o.h);
    item.theta = static_cast<float>(o.theta);
    item.classIndex = o.cls;
    return item;
}

// Trains one model on the scenes (instanceHead = true also attaches the labels) and saves it.
bool TrainInstScenes(TestTally& tally, const std::string& label, const std::filesystem::path& runDir, const std::filesystem::path& backbone,
                     const std::vector<cv::Mat>& images, const std::vector<cv::Mat>& masks, const std::vector<std::vector<SynthObject>>& objects,
                     bool instanceHead, int semanticEpochs, int instanceEpochs, const std::filesystem::path& modelPath)
{
    const std::string extra = instanceHead ? ",\"instanceHead\":1,\"angleSymmetry\":[1,4],\"instanceEpochs\":" + std::to_string(instanceEpochs) : std::string();
    const std::filesystem::path settingsPath = WriteInstSettings(runDir, backbone, kInstSide, semanticEpochs, extra);
    void* trainer = SEGAI_TrainerCreate();
    if (!trainer) { tally.Check(false, label + ": TrainerCreate"); return false; }
    bool ok = false;
    const auto begin = std::chrono::steady_clock::now();
    do {
        const std::wstring settingsWide = settingsPath.wstring();
        const int configureRc = SEGAI_TrainerConfigureV2(trainer, settingsWide.c_str());
        tally.Check(configureRc == 0, label + ": ConfigureV2 rc=" + std::to_string(configureRc) + " " + TrainerErrorText(trainer));
        if (configureRc != 0) break;
        int rc = 0;
        for (size_t index = 0; index < images.size() && rc == 0; ++index) {
            rc = SEGAI_TrainerSetROI(trainer, 0, 0, images[index].cols, images[index].rows);
            if (rc == 0) rc = SEGAI_TrainerAddSample(trainer, images[index].data, images[index].cols, images[index].rows, static_cast<int>(images[index].step),
                                                     masks[index].data, static_cast<int>(masks[index].step));
            if (rc == 0 && instanceHead) {
                std::vector<SegInstanceV1> items;
                for (const SynthObject& o : objects[index]) items.push_back(MakeInstanceItem(o));
                rc = SEGAI_TrainerAddInstances(trainer, items.data(), static_cast<int32_t>(items.size()), static_cast<int32_t>(sizeof(SegInstanceV1)));
            }
        }
        tally.Check(rc == 0, label + ": AddSample/AddInstances rc=" + std::to_string(rc) + " " + TrainerErrorText(trainer));
        if (rc != 0) break;
        const int trainRc = SEGAI_TrainerTrain(trainer, 0, 0, 0, nullptr, nullptr, nullptr);
        tally.Check(trainRc == 0, label + ": Train rc=" + std::to_string(trainRc) + " " + TrainerErrorText(trainer));
        if (trainRc != 0) break;
        const std::wstring modelWide = modelPath.wstring();
        const int saveRc = SEGAI_TrainerSave(trainer, modelWide.c_str(), 0.5f, 40);
        tally.Check(saveRc == 0, label + ": Save rc=" + std::to_string(saveRc) + " " + TrainerErrorText(trainer));
        ok = saveRc == 0;
    } while (false);
    SEGAI_TrainerDestroy(trainer);
    std::cout << label << ": trained in " << std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - begin).count() << " s\n";
    if (instanceHead) {
        const std::vector<unsigned char> status = ReadFileBytes(runDir / "v2_logs" / "instance.txt");
        std::cout << label << ": instance.txt: " << std::string(status.begin(), status.end());
    }
    return ok;
}

// Runs PredictInstances with a big enough buffer. Returns the native rc; found is filled on 0.
int PredictScene(void* infer, const cv::Mat& image, const cv::Rect& roi, float threshold, int quality, std::vector<SegInstanceResultV1>& found)
{
    std::vector<SegInstanceResultV1> buffer(256);
    int32_t count = -1;
    const int rc = SEGAI_InferPredictInstances(infer, image.data, image.cols, image.rows, static_cast<int32_t>(image.step), roi.x, roi.y, roi.width, roi.height,
                                               threshold, quality, buffer.data(), static_cast<int32_t>(buffer.size()), &count);
    found.clear();
    if (rc == 0 && count >= 0 && count <= static_cast<int32_t>(buffer.size())) found.assign(buffer.begin(), buffer.begin() + count);
    return rc;
}

struct InstScore {
    int truth = 0, matched = 0, found = 0, angleCount = 0;
    double centreSum = 0, centreWorst = 0, angleSum = 0, angleWorst = 0;
};

// Greedy one-to-one matching, same class only; a match needs the centre within `limit` pixels. onlyLeftOf >= 0 restricts the truth to cx < onlyLeftOf.
void ScoreInstances(const std::vector<SynthObject>& truth, const std::vector<SegInstanceResultV1>& found, double limit, InstScore& score, double onlyLeftOf = -1.0)
{
    std::vector<char> used(found.size(), 0);
    score.found += static_cast<int>(found.size());
    for (const SynthObject& t : truth) {
        if (onlyLeftOf >= 0 && t.cx >= onlyLeftOf) continue;
        ++score.truth;
        int best = -1;
        double bestDistance = 1e30;
        for (size_t i = 0; i < found.size(); ++i) {
            if (used[i] || found[i].classIndex != t.cls) continue;
            const double d = std::hypot(static_cast<double>(found[i].cx) - t.cx, static_cast<double>(found[i].cy) - t.cy);
            if (d < bestDistance) { bestDistance = d; best = static_cast<int>(i); }
        }
        if (best < 0 || bestDistance > limit) continue;
        used[static_cast<size_t>(best)] = 1;
        ++score.matched;
        score.centreSum += bestDistance;
        score.centreWorst = std::max(score.centreWorst, bestDistance);
        if (!t.circle) {
            const double degrees = BeeSegAI::AngleDifferenceModuloSymmetry(found[static_cast<size_t>(best)].theta, t.theta, 4) * 180.0 / CV_PI;
            ++score.angleCount;
            score.angleSum += degrees;
            score.angleWorst = std::max(score.angleWorst, degrees);
        }
    }
}

int RunInstHeadSelfTest(int argc, char** argv)
{
    if (argc < 3) {
        std::cerr << "Usage: --inst-head-selftest <outDir> [<backbone.onnx>|-] [<instanceEpochs>]\n";
        return 100;
    }
    namespace fs = std::filesystem;
    const fs::path outDir = fs::u8path(argv[2]);
    fs::path backbonePath;
    if (argc > 3 && std::strcmp(argv[3], "-") != 0) {
        backbonePath = fs::u8path(argv[3]);
    } else {
        wchar_t exePath[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        backbonePath = fs::path(exePath).parent_path() / "Models" / "DefectV2" / "mobilenetv2-12.onnx";
    }
    int instanceEpochs = 120;
    if (argc > 4 && (!ParseIntArgument(argv[4], instanceEpochs) || instanceEpochs < 1 || instanceEpochs > 1000)) {
        std::cerr << "Invalid instanceEpochs\n";
        return 100;
    }
    int trainScenes = 12;
    if (argc > 5 && (!ParseIntArgument(argv[5], trainScenes) || trainScenes < 1 || trainScenes > 200)) {
        std::cerr << "Invalid trainScenes\n";
        return 100;
    }
    if (!fs::exists(backbonePath)) {
        std::cerr << "V2 backbone is missing: " << backbonePath.u8string() << "\n";
        return 102;
    }
    std::error_code ec;
    fs::create_directories(outDir, ec);
    if (ec) {
        std::cerr << "Failed to create outDir\n";
        return 102;
    }
    TestTally tally;

    // ---- InstApi_Probe -------------------------------------------------------------------------------------------
    std::cout << "InstApi_Probe\n";
    tally.Check(SEGAI_GetInstanceApiVersion() == 1, "GetInstanceApiVersion is 1");
    tally.Check(SEGAI_GetVersion() == 1, "SEGAI_GetVersion stays 1");
    tally.Check(SEGAI_InferHasInstanceHead(nullptr) == -1, "HasInstanceHead(null) = -1");
    {
        SegInstanceV1 item = MakeInstanceItem(SynthObject());
        tally.Check(SEGAI_TrainerAddInstances(nullptr, &item, 1, 32) == -1, "AddInstances(null handle) = -1");
        int32_t count = 7;
        tally.Check(SEGAI_InferPredictInstances(nullptr, nullptr, 0, 0, 0, 0, 0, 0, 0, 0.3f, 0, nullptr, 0, &count) == -1, "PredictInstances(null handle) = -1");
        std::unique_ptr<void, InferHandleDeleter> bare(SEGAI_InferCreate());
        tally.Check(SEGAI_InferHasInstanceHead(bare.get()) == 0, "HasInstanceHead without a model = 0");
        cv::Mat tiny(8, 8, CV_8UC3, cv::Scalar(0, 0, 0));
        count = 7;
        tally.Check(SEGAI_InferPredictInstances(bare.get(), tiny.data, 8, 8, static_cast<int32_t>(tiny.step), 0, 0, 0, 0, 0.3f, 0, nullptr, 0, &count) == -9 && count == 0,
                    "PredictInstances without a model = -9 and outCount = 0");
        tally.Check(SEGAI_InferPredictInstances(bare.get(), tiny.data, 8, 8, static_cast<int32_t>(tiny.step), 0, 0, 0, 0, 0.3f, 0, nullptr, 0, nullptr) == -1, "PredictInstances(outCount null) = -1");
        void* unconfigured = SEGAI_TrainerCreate();
        tally.Check(SEGAI_TrainerAddInstances(unconfigured, &item, 1, 32) == -2 && Contains(TrainerErrorText(unconfigured), "V2 trainer"), "AddInstances on a trainer without ConfigureV2 = -2");
        SEGAI_TrainerDestroy(unconfigured);
    }
    // Rejected / invalid settings (Configure reads no backbone file, so these run without model work).
    auto configureCase = [&](const std::string& name, const std::string& extra, int expectedRc, const char* expectedText) {
        const fs::path dir = outDir / ("settings-" + name);
        const fs::path settings = WriteInstSettings(dir, backbonePath, 160, 5, extra);
        void* trainer = SEGAI_TrainerCreate();
        const std::wstring wide = settings.wstring();
        const int rc = SEGAI_TrainerConfigureV2(trainer, wide.c_str());
        const std::string text = TrainerErrorText(trainer);
        tally.Check(rc == expectedRc && (!expectedText || Contains(text, expectedText)), name + ": ConfigureV2 rc=" + std::to_string(rc) + " '" + text + "'");
        SEGAI_TrainerDestroy(trainer);
    };
    configureCase("head-on", ",\"instanceHead\":1,\"angleSymmetry\":2", 0, nullptr);
    configureCase("head-off-ignores-keys", ",\"instanceHead\":0,\"angleSymmetry\":3", 0, nullptr);
    configureCase("original-input", ",\"instanceHead\":1,\"originalInput\":1", -8, "originalInput");
    configureCase("backbone-finetune", ",\"instanceHead\":1,\"backboneEpochs\":1", -8, "backbone fine-tune");
    configureCase("retrain", ",\"instanceHead\":1,\"resumeFrom\":\"missing.segai\"", -8, "Retrain");
    configureCase("bad-symmetry", ",\"instanceHead\":1,\"angleSymmetry\":[1,3]", -8, "angleSymmetry");
    configureCase("bad-flag", ",\"instanceHead\":2", -8, "instanceHead must be 0 or 1");
    // AddInstances state and argument codes on a configured trainer.
    {
        const fs::path settings = WriteInstSettings(outDir / "settings-add", backbonePath, 160, 5, ",\"instanceHead\":1");
        void* trainer = SEGAI_TrainerCreate();
        const std::wstring wide = settings.wstring();
        tally.Check(SEGAI_TrainerConfigureV2(trainer, wide.c_str()) == 0, "AddInstances trainer: ConfigureV2");
        SynthObject good;
        good.cx = 30; good.cy = 30; good.w = good.h = 20; good.circle = true;
        SegInstanceV1 item = MakeInstanceItem(good);
        tally.Check(SEGAI_TrainerAddInstances(trainer, &item, 1, 32) == -2 && Contains(TrainerErrorText(trainer), "sample"), "AddInstances before AddSample = -2");
        cv::Mat image(64, 64, CV_8UC3, cv::Scalar(90, 90, 90)), mask = cv::Mat::zeros(64, 64, CV_8U);
        cv::circle(mask, cv::Point(30, 30), 10, cv::Scalar(1), cv::FILLED);
        tally.Check(SEGAI_TrainerAddSample(trainer, image.data, 64, 64, static_cast<int32_t>(image.step), mask.data, static_cast<int32_t>(mask.step)) == 0, "AddInstances trainer: AddSample");
        tally.Check(SEGAI_TrainerAddInstances(trainer, &item, 1, 32) == 0, "AddInstances valid item = 0");
        tally.Check(SEGAI_TrainerAddInstances(trainer, nullptr, 0, 32) == 0, "AddInstances count 0 = 0");
        tally.Check(SEGAI_TrainerAddInstances(trainer, nullptr, 1, 32) == -1, "AddInstances null items = -1");
        tally.Check(SEGAI_TrainerAddInstances(trainer, &item, -1, 32) == -1, "AddInstances negative count = -1");
        tally.Check(SEGAI_TrainerAddInstances(trainer, &item, 1, 16) == -1, "AddInstances structSize 16 = -1");
        SegInstanceV1 badClass = item; badClass.classIndex = 9;
        tally.Check(SEGAI_TrainerAddInstances(trainer, &badClass, 1, 32) == -3 && !TrainerErrorText(trainer).empty(), "AddInstances classIndex out of range = -3");
        SegInstanceV1 badKind = item; badKind.kind = 5;
        tally.Check(SEGAI_TrainerAddInstances(trainer, &badKind, 1, 32) == -3, "AddInstances unknown kind = -3");
        SegInstanceV1 badSize = item; badSize.w = 0.f;
        tally.Check(SEGAI_TrainerAddInstances(trainer, &badSize, 1, 32) == -3, "AddInstances zero width = -3");
        SegInstanceV1 badNan = item; badNan.cx = std::numeric_limits<float>::quiet_NaN();
        tally.Check(SEGAI_TrainerAddInstances(trainer, &badNan, 1, 32) == -3, "AddInstances NaN = -3");
        SEGAI_TrainerDestroy(trainer);
        const fs::path offSettings = WriteInstSettings(outDir / "settings-add-off", backbonePath, 160, 5, std::string());
        trainer = SEGAI_TrainerCreate();
        const std::wstring offWide = offSettings.wstring();
        SEGAI_TrainerConfigureV2(trainer, offWide.c_str());
        SEGAI_TrainerAddSample(trainer, image.data, 64, 64, static_cast<int32_t>(image.step), mask.data, static_cast<int32_t>(mask.step));
        tally.Check(SEGAI_TrainerAddInstances(trainer, &item, 1, 32) == -2 && Contains(TrainerErrorText(trainer), "not enabled"), "AddInstances with instanceHead=0 = -2");
        SEGAI_TrainerDestroy(trainer);
    }
    // P2 minor: a NaN regression channel must not produce an instance.
    {
        using namespace BeeSegAI;
        const int grid = 8;
        const size_t g2 = static_cast<size_t>(grid) * grid;
        std::vector<float> heat(g2, 0.f), reg(static_cast<size_t>(kInstanceRegChannels) * g2, 0.f);
        const size_t cell = 3 * grid + 3;
        heat[cell] = 0.9f;
        reg[2 * g2 + cell] = std::log(4.f); reg[3 * g2 + cell] = std::log(4.f); reg[5 * g2 + cell] = 1.f;
        DecodeOptions options;
        options.grid = grid;
        std::vector<DecodedInstance> decoded;
        DecodeInstances(heat.data(), reg.data(), 1, std::vector<SymmetryK>(), options, decoded);
        tally.Check(decoded.size() == 1, "DecodeInstances finite regression gives one instance");
        reg[0 * g2 + cell] = std::numeric_limits<float>::quiet_NaN();
        DecodeInstances(heat.data(), reg.data(), 1, std::vector<SymmetryK>(), options, decoded);
        tally.Check(decoded.empty(), "DecodeInstances skips a NaN regression cell");
    }

    // ---- scenes ----------------------------------------------------------------------------------------------------
    std::vector<cv::Mat> trainImages, trainMasks, testImages, testMasks;
    std::vector<std::vector<SynthObject>> trainObjects, testObjects;
    for (unsigned i = 0; i < static_cast<unsigned>(trainScenes); ++i) {
        cv::Mat image, mask;
        std::vector<SynthObject> objects;
        MakeInstanceScene(1000 + i, image, mask, objects);
        trainImages.push_back(image); trainMasks.push_back(mask); trainObjects.push_back(objects);
    }
    for (unsigned i = 0; i < 8; ++i) {
        cv::Mat image, mask;
        std::vector<SynthObject> objects;
        MakeInstanceScene(5000 + i, image, mask, objects);
        testImages.push_back(image); testMasks.push_back(mask); testObjects.push_back(objects);
    }
    if (!testImages.empty()) cv::imwrite((outDir / "scene-test-0.png").u8string(), testImages[0]);

    const fs::path offModel = outDir / "instance-off.segai";
    const fs::path onModel = outDir / "instance-on.segai";
    const bool offTrained = TrainInstScenes(tally, "instance off", outDir / "run-off", backbonePath, trainImages, trainMasks, trainObjects, false, 20, instanceEpochs, offModel);
    const bool onTrained = TrainInstScenes(tally, "instance on", outDir / "run-on", backbonePath, trainImages, trainMasks, trainObjects, true, 20, instanceEpochs, onModel);
    if (!offTrained || !onTrained) return tally.Finish("inst-head-selftest (EAI-POS P3)");

    // ---- DetectBitIdentical_InstanceOnOff ----------------------------------------------------------------------------
    std::cout << "DetectBitIdentical_InstanceOnOff\n";
    const std::vector<unsigned char> offFile = ReadFileBytes(offModel), onFile = ReadFileBytes(onModel);
    const SegaiLayout offLayout = ReadSegaiLayout(offFile), onLayout = ReadSegaiLayout(onFile);
    tally.Check(offLayout.valid && onLayout.valid && offLayout.version == 3u && onLayout.version == 3u, "both models are format version 3 (no header bump)");
    tally.Check(offLayout.featureFlags == onLayout.featureFlags && offLayout.numClasses == onLayout.numClasses && offLayout.featureCount == onLayout.featureCount &&
                offLayout.reservedA == onLayout.reservedA, "header fields other than size and CRC are identical");
    tally.Check(onLayout.payloadSize > offLayout.payloadSize, "the instance model carries extra payload");
    if (offLayout.valid && onLayout.valid && offFile.size() >= kSegaiHeaderBytes + offLayout.payloadSize && onFile.size() >= kSegaiHeaderBytes + onLayout.payloadSize) {
        const std::string offPayload(offFile.begin() + kSegaiHeaderBytes, offFile.begin() + kSegaiHeaderBytes + static_cast<std::ptrdiff_t>(offLayout.payloadSize));
        const std::string onPayload(onFile.begin() + kSegaiHeaderBytes, onFile.begin() + kSegaiHeaderBytes + static_cast<std::ptrdiff_t>(onLayout.payloadSize));
        const size_t instanceAt = onPayload.find("instance_version");
        const size_t networkBinAt = onPayload.find("network_bin");
        tally.Check(instanceAt != std::string::npos && networkBinAt != std::string::npos && instanceAt > networkBinAt, "instance keys are written after network_bin");
        tally.Check(offPayload.find("instance_") == std::string::npos, "the off model has no instance_ key");
        // P3b determinism control (--inst-determinism-selftest): the saved network_xml / network_bin of two identical semantic trainings are
        // NOT byte-reproducible (the IR serialisation differs run to run, even with the instance head off), while Predict is bit-identical.
        // So the invariant is checked on what is deterministic: the YAML key prefix up to network_xml (the managed scan window content), the
        // sequence of top-level payload keys, and the Predict outputs below.
        auto prefixBeforeXml = [](const std::string& payload) { const size_t at = payload.find("network_xml"); return at == std::string::npos ? payload : payload.substr(0, at); };
        auto topLevelKeys = [](const std::string& payload, size_t limit) {
            std::string keys;
            size_t pos = 0;
            while (pos < limit) {
                const size_t eol = payload.find('\n', pos);
                const size_t end = eol == std::string::npos ? payload.size() : eol;
                if (end > pos && payload[pos] != ' ') { const size_t colon = payload.find(':', pos); if (colon != std::string::npos && colon < end) keys += payload.substr(pos, colon - pos) + ","; }
                pos = end + 1;
            }
            return keys;
        };
        std::cout << "semantic payload: off " << offPayload.size() << " bytes, on " << onPayload.size() << " bytes, instance keys at " << instanceAt << "\n";
        tally.Check(instanceAt != std::string::npos && prefixBeforeXml(offPayload) == prefixBeforeXml(onPayload), "payload YAML prefix before network_xml is identical with the instance head on");
        tally.Check(instanceAt != std::string::npos && topLevelKeys(offPayload, offPayload.size()) == topLevelKeys(onPayload, instanceAt),
                    "the top-level payload keys before the instance keys are the same sequence with the instance head on");
        // InstanceKeys_PayloadScanWindowUnchanged: the managed side reads the 1024 bytes after the header (SegAiTrainingPipeline line-wise
        // "native_v2: 2", SegAiModelFile ReadYamlInt native_v2 / postprocess_version): the same lines must be present and nothing instance_.
        std::cout << "InstanceKeys_PayloadScanWindowUnchanged\n";
        const size_t window = std::min<size_t>(1024, std::min(offPayload.size(), onPayload.size()));
        const std::string offWindow = offPayload.substr(0, window), onWindow = onPayload.substr(0, window);
        tally.Check(prefixBeforeXml(offWindow) == prefixBeforeXml(onWindow) && prefixBeforeXml(onWindow).find("native_v2: 1") != std::string::npos,
                    "the managed scan window holds the same native_v2 / postprocess_version / side lines (only the network_xml size digits may differ)");
        tally.Check(onPayload.find("instance_") > 1024, "no instance_ key inside the first 1024 payload bytes");
        tally.Check(onPayload.find("native_v2") < 1024 && onPayload.find("postprocess_version") < 1024, "native_v2 and postprocess_version stay inside the scan window");
    }
    {
        std::vector<unsigned char> offMask, offConfidence, onMask, onConfidence;
        for (size_t i = 0; i < 2; ++i) {
            float offScore = 0.f, onScore = 0.f;
            std::string error;
            const bool a = PredictFullImage(offModel, testImages[i], offMask, offConfidence, offScore, error);
            const bool b = PredictFullImage(onModel, testImages[i], onMask, onConfidence, onScore, error);
            tally.Check(a && b, "semantic Predict on test scene " + std::to_string(i) + ": " + error);
            tally.Check(a && b && offMask == onMask && offConfidence == onConfidence && offScore == onScore,
                        "semantic mask, confidence and score of scene " + std::to_string(i) + " are bit-identical with instance head on/off");
        }
    }

    // ---- LegacyV3V4_Load_NoInstance ----------------------------------------------------------------------------------
    std::cout << "LegacyV3V4_Load_NoInstance\n";
    {
        std::vector<unsigned char> tableBytes;
        std::string reason;
        BeeSegAI::EncodeClassTable(MakeClassTable({ { "NG", BeeSegAI::kClassKindDefect }, { "OK", BeeSegAI::kClassKindGood } }), tableBytes, reason);
        const fs::path v4Model = outDir / "legacy-v4.segai";
        tally.Check(SynthesizeClassModel(offModel, v4Model, { { 0, 0.f }, { 1, 0.f }, { 2, 0.f } }, tableBytes, 2, reason), "v4 legacy model synthesised: " + reason);
        for (const fs::path& path : { offModel, v4Model }) {
            std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
            const std::wstring wide = path.wstring();
            tally.Check(SEGAI_InferLoad(infer.get(), wide.c_str()) == 0, path.filename().u8string() + ": loads");
            tally.Check(SEGAI_InferHasInstanceHead(infer.get()) == 0, path.filename().u8string() + ": HasInstanceHead = 0");
            std::vector<SegInstanceResultV1> found;
            int32_t count = 5;
            std::vector<SegInstanceResultV1> buffer(4);
            const int rc = SEGAI_InferPredictInstances(infer.get(), testImages[0].data, testImages[0].cols, testImages[0].rows, static_cast<int32_t>(testImages[0].step),
                                                       0, 0, 0, 0, 0.3f, 0, buffer.data(), 4, &count);
            tally.Check(rc == -11 && count == 0 && Contains(InferErrorText(infer.get()), "no instance head"),
                        path.filename().u8string() + ": PredictInstances = -11 with a 'no instance head' message (rc=" + std::to_string(rc) + ")");
            SEGAI_InferSetGpu(infer.get(), 0);
            uint8_t* maskPtr = nullptr; int32_t maskW = 0, maskH = 0; float score = 0.f;
            const int predictRc = SEGAI_InferPredict(infer.get(), testImages[0].data, testImages[0].cols, testImages[0].rows, static_cast<int32_t>(testImages[0].step),
                                                     0, 0, testImages[0].cols, testImages[0].rows, 0.5f, &maskPtr, &maskW, &maskH, &score);
            std::unique_ptr<uint8_t, SegaiBufferDeleter> maskOwner(maskPtr);
            tally.Check(predictRc == 0 && maskW == testImages[0].cols, path.filename().u8string() + ": semantic Predict still works");
        }
    }

    // ---- InstSynth_TouchingDiscs ---------------------------------------------------------------------------------------
    std::cout << "InstSynth_TouchingDiscs\n";
    std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
    {
        const std::wstring wide = onModel.wstring();
        tally.Check(SEGAI_InferLoad(infer.get(), wide.c_str()) == 0, "instance model loads: " + InferErrorText(infer.get()));
        SEGAI_InferSetGpu(infer.get(), 0);
    }
    tally.Check(SEGAI_InferHasInstanceHead(infer.get()) == 1, "instance model: HasInstanceHead = 1");
    InstScore total;
    double predictMs = 0;
    int firstCount = 0;
    std::vector<SegInstanceResultV1> firstFound;
    for (size_t i = 0; i < testImages.size(); ++i) {
        std::vector<SegInstanceResultV1> found;
        const auto begin = std::chrono::steady_clock::now();
        const int rc = PredictScene(infer.get(), testImages[i], cv::Rect(), 0.3f, 0, found);
        predictMs += std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - begin).count();
        tally.Check(rc == 0, "PredictInstances scene " + std::to_string(i) + " rc=" + std::to_string(rc) + " " + InferErrorText(infer.get()));
        InstScore scene;
        ScoreInstances(testObjects[i], found, kInstCentreLimit, scene);
        std::cout << "  scene " << i << ": truth " << scene.truth << ", found " << scene.found << ", matched " << scene.matched << "\n";
        total.truth += scene.truth; total.matched += scene.matched; total.found += scene.found;
        total.centreSum += scene.centreSum; total.centreWorst = std::max(total.centreWorst, scene.centreWorst);
        total.angleSum += scene.angleSum; total.angleCount += scene.angleCount; total.angleWorst = std::max(total.angleWorst, scene.angleWorst);
        if (i == 0) { firstFound = found; firstCount = static_cast<int>(found.size()); }
    }
    const double recall = total.truth ? double(total.matched) / total.truth : 0.0;
    const double precision = total.found ? double(total.matched) / total.found : 0.0;
    std::cout << std::fixed << std::setprecision(3) << "  recall " << recall << " (" << total.matched << "/" << total.truth << "), precision " << precision
              << ", centre error mean " << (total.matched ? total.centreSum / total.matched : 0.0) << " px, worst " << total.centreWorst
              << " px, square angle error mean " << (total.angleCount ? total.angleSum / total.angleCount : 0.0) << " deg, worst " << total.angleWorst
              << " deg, " << predictMs / std::max<size_t>(1, testImages.size()) << " ms per image (CPU, includes the first compile)\n";
    tally.Check(recall >= 0.98, "recall >= 0.98 (touching / overlapping objects are separated), measured " + std::to_string(recall));
    tally.Check(total.centreWorst <= kInstCentreLimit, "every matched centre within 1 grid cell (8 px), worst " + std::to_string(total.centreWorst));
    tally.Check(precision >= 0.95, "no merged or spurious instances: precision >= 0.95, measured " + std::to_string(precision));

    // ---- capacity contract, argument errors, quality parameter, ROI ------------------------------------------------------------
    std::cout << "InstApi_Capacity\n";
    if (firstCount >= 2) {
        const cv::Mat& scene = testImages[0];
        const int32_t stride = static_cast<int32_t>(scene.step);
        SegInstanceResultV1 sentinel = {};
        sentinel.structSize = -7;
        std::vector<SegInstanceResultV1> buffer(static_cast<size_t>(firstCount) + 2, sentinel);
        int32_t count = -1;
        int rc = SEGAI_InferPredictInstances(infer.get(), scene.data, scene.cols, scene.rows, stride, 0, 0, 0, 0, 0.3f, 0, buffer.data(), firstCount - 1, &count);
        tally.Check(rc == -4 && count == firstCount && buffer[0].structSize == -7, "capacity N-1: -4, outCount = N, nothing written");
        count = -1;
        rc = SEGAI_InferPredictInstances(infer.get(), scene.data, scene.cols, scene.rows, stride, 0, 0, 0, 0, 0.3f, 0, nullptr, 0, &count);
        tally.Check(rc == -4 && count == firstCount, "capacity 0 and no buffer: -4, outCount = N");
        count = -1;
        rc = SEGAI_InferPredictInstances(infer.get(), scene.data, scene.cols, scene.rows, stride, 0, 0, 0, 0, 0.3f, 0, buffer.data(), firstCount, &count);
        bool shapeOk = rc == 0 && count == firstCount && buffer[static_cast<size_t>(firstCount)].structSize == -7;
        for (int i = 0; i < count && shapeOk; ++i) {
            const SegInstanceResultV1& r = buffer[static_cast<size_t>(i)];
            shapeOk = r.structSize == 40 && (r.kind == 0 || r.kind == 1) && r.score >= 0.3f && r.score <= 1.f && r.refined == 0 && std::isfinite(r.cx) && std::isfinite(r.theta) &&
                      (i == 0 || buffer[static_cast<size_t>(i) - 1].score >= r.score);
        }
        tally.Check(shapeOk, "capacity N: 0, structSize 40, kind/score/refined sane, score descending, one past the end untouched");
        tally.Check(SEGAI_InferPredictInstances(infer.get(), scene.data, scene.cols, scene.rows, stride, 0, 0, 0, 0, 0.3f, 0, buffer.data(), -1, &count) == -1, "negative capacity = -1");
        tally.Check(SEGAI_InferPredictInstances(infer.get(), scene.data, scene.cols, scene.rows, stride, 0, 0, 0, 0, 0.3f, 0, nullptr, 5, &count) == -1, "null buffer with capacity > 0 = -1");
        tally.Check(SEGAI_InferPredictInstances(infer.get(), scene.data, scene.cols, scene.rows, stride, 0, 0, 0, 0, 0.3f, 2, buffer.data(), firstCount, &count) == -2, "quality 2 = -2");
        tally.Check(SEGAI_InferPredictInstances(infer.get(), scene.data, scene.cols, scene.rows, stride, 0, 0, 0, 0, 1.5f, 0, buffer.data(), firstCount, &count) == -2, "threshold 1.5 = -2");
        tally.Check(SEGAI_InferPredictInstances(infer.get(), scene.data, scene.cols, scene.rows, stride, 0, 0, 0, 0, std::numeric_limits<float>::quiet_NaN(), 0, buffer.data(), firstCount, &count) == -2, "NaN threshold = -2");
        tally.Check(SEGAI_InferPredictInstances(infer.get(), scene.data, scene.cols, scene.rows, stride, 5000, 5000, 10, 10, 0.3f, 0, buffer.data(), firstCount, &count) == -2, "ROI outside the image = -2");
        std::vector<SegInstanceResultV1> quality;
        tally.Check(PredictScene(infer.get(), scene, cv::Rect(), 0.3f, 1, quality) == 0 && static_cast<int>(quality.size()) == firstCount, "quality 1 keeps the instance count (P4 refinement does not add or drop instances)");
    } else {
        tally.Check(false, "scene 0 produced fewer than 2 instances, the capacity contract could not be exercised");
    }
    std::cout << "InstApi_Roi\n";
    {
        const cv::Rect left(0, 0, 320, kInstSide);
        InstScore roiScore;
        for (size_t i = 0; i < testImages.size(); ++i) {
            std::vector<SegInstanceResultV1> found;
            const int rc = PredictScene(infer.get(), testImages[i], left, 0.3f, 0, found);
            tally.Check(rc == 0, "PredictInstances ROI scene " + std::to_string(i) + " rc=" + std::to_string(rc));
            bool inside = true;
            for (const SegInstanceResultV1& r : found) if (r.cx < 0 || r.cx >= 320) inside = false;
            tally.Check(inside, "ROI scene " + std::to_string(i) + ": every instance lies inside the ROI (original image coordinates)");
            ScoreInstances(testObjects[i], found, 12.0, roiScore, 260.0);
        }
        const double roiRecall = roiScore.truth ? double(roiScore.matched) / roiScore.truth : 0.0;
        std::cout << "  ROI recall " << roiRecall << " (" << roiScore.matched << "/" << roiScore.truth << ") within 12 px for objects with cx < 260\n";
        tally.Check(roiScore.truth > 0 && roiRecall >= 0.8, "ROI keeps the original image coordinates: recall >= 0.8 within 12 px, measured " + std::to_string(roiRecall));
    }
    return tally.Finish("inst-head-selftest (EAI-POS P3)");
}

// P3b determinism control: --inst-determinism-selftest <outDir> <backbone.onnx> [<instanceEpochs>]
// Trains the semantic model twice with the instance head OFF and once ON on the same scenes and reports where the saved payloads and
// the Predict outputs differ.
int RunInstDeterminismSelfTest(int argc, char** argv)
{
    namespace fs = std::filesystem;
    if (argc < 4) return 100;
    const fs::path outDir = fs::u8path(argv[2]), backbonePath = fs::u8path(argv[3]);
    int instanceEpochs = 3;
    if (argc > 4) ParseIntArgument(argv[4], instanceEpochs);
    std::error_code ec;
    fs::create_directories(outDir, ec);
    TestTally tally;
    std::vector<cv::Mat> trainImages, trainMasks, testImages, testMasks;
    std::vector<std::vector<SynthObject>> trainObjects, testObjects;
    for (unsigned i = 0; i < 12; ++i) {
        cv::Mat image, mask; std::vector<SynthObject> objects;
        MakeInstanceScene(1000 + i, image, mask, objects);
        trainImages.push_back(image); trainMasks.push_back(mask); trainObjects.push_back(objects);
    }
    for (unsigned i = 0; i < 2; ++i) {
        cv::Mat image, mask; std::vector<SynthObject> objects;
        MakeInstanceScene(5000 + i, image, mask, objects);
        testImages.push_back(image); testMasks.push_back(mask); testObjects.push_back(objects);
    }
    const fs::path models[3] = { outDir / "off-a.segai", outDir / "off-b.segai", outDir / "on.segai" };
    TrainInstScenes(tally, "off A", outDir / "run-off-a", backbonePath, trainImages, trainMasks, trainObjects, false, 20, instanceEpochs, models[0]);
    TrainInstScenes(tally, "off B", outDir / "run-off-b", backbonePath, trainImages, trainMasks, trainObjects, false, 20, instanceEpochs, models[1]);
    TrainInstScenes(tally, "on", outDir / "run-on", backbonePath, trainImages, trainMasks, trainObjects, true, 20, instanceEpochs, models[2]);
    std::string payloads[3];
    for (int m = 0; m < 3; ++m) {
        const std::vector<unsigned char> file = ReadFileBytes(models[m]);
        const SegaiLayout layout = ReadSegaiLayout(file);
        if (layout.valid && file.size() >= kSegaiHeaderBytes + layout.payloadSize)
            payloads[m].assign(file.begin() + kSegaiHeaderBytes, file.begin() + kSegaiHeaderBytes + static_cast<std::ptrdiff_t>(layout.payloadSize));
        std::cout << "payload " << m << ": " << payloads[m].size() << " bytes\n";
    }
    auto firstDiff = [](const std::string& a, const std::string& b) {
        return static_cast<size_t>(std::mismatch(a.begin(), a.end(), b.begin(), b.end()).first - a.begin());
    };
    const size_t ab = firstDiff(payloads[0], payloads[1]), ac = firstDiff(payloads[0], payloads[2]);
    std::cout << "off-a vs off-b: first difference at byte " << ab << " (sizes " << payloads[0].size() << " / " << payloads[1].size() << ")\n";
    std::cout << "off-a vs on: first difference at byte " << ac << "\n";
    for (int pair = 0; pair < 2; ++pair) {
        const std::string& a = payloads[0]; const std::string& b = payloads[pair == 0 ? 1 : 2];
        const size_t at = pair == 0 ? ab : ac;
        if (at < a.size() && at < b.size()) {
            const size_t from = at > 80 ? at - 80 : 0;
            std::cout << "  context A: " << a.substr(from, 200) << "\n  context B: " << b.substr(from, 200) << "\n";
        }
    }
    size_t diffBytes = 0; const size_t common = std::min(payloads[0].size(), payloads[1].size());
    for (size_t i = 0; i < common; ++i) if (payloads[0][i] != payloads[1][i]) ++diffBytes;
    std::cout << "off-a vs off-b: " << diffBytes << " differing bytes of " << common << "\n";
    for (size_t i = 0; i < 2; ++i) {
        std::vector<unsigned char> m[3], c[3]; float s[3] = {}; std::string error;
        for (int k = 0; k < 3; ++k) PredictFullImage(models[k], testImages[i], m[k], c[k], s[k], error);
        auto confDiff = [&](int a, int b) { double mx = 0; size_t n = std::min(c[a].size(), c[b].size()); for (size_t j = 0; j < n; ++j) mx = std::max(mx, std::abs(double(c[a][j]) - double(c[b][j]))); return mx; };
        auto maskDiff = [&](int a, int b) { size_t d = 0; size_t n = std::min(m[a].size(), m[b].size()); for (size_t j = 0; j < n; ++j) if (m[a][j] != m[b][j]) ++d; return d; };
        std::cout << "scene " << i << ": off-a vs off-b mask diff " << maskDiff(0, 1) << " px, conf max diff " << confDiff(0, 1) << ", score " << s[0] << " vs " << s[1]
                  << "; off-a vs on mask diff " << maskDiff(0, 2) << " px, conf max diff " << confDiff(0, 2) << ", score " << s[2] << "\n";
    }
    return tally.Finish("inst-determinism-selftest");
}

// P3b diagnosis: --inst-diag <model.segai> [train|test]  (no training; loads a saved instance model and explains each miss)
int RunInstDiag(int argc, char** argv)
{
    namespace fs = std::filesystem;
    if (argc < 3) return 100;
    const bool useTrain = argc > 3 && std::strcmp(argv[3], "train") == 0;
    std::unique_ptr<void, InferHandleDeleter> infer(SEGAI_InferCreate());
    const std::wstring wide = fs::u8path(argv[2]).wstring();
    if (SEGAI_InferLoad(infer.get(), wide.c_str()) != 0) { std::cout << "load failed\n"; return 101; }
    SEGAI_InferSetGpu(infer.get(), 0);
    InstScore total;
    int touching = 0, touchingMiss = 0, small = 0, smallMiss = 0, edge = 0, edgeMiss = 0;
    const int sceneCount = useTrain ? 12 : 8;
    for (int i = 0; i < sceneCount; ++i) {
        cv::Mat image, mask; std::vector<SynthObject> truth;
        MakeInstanceScene((useTrain ? 1000u : 5000u) + i, image, mask, truth);
        std::vector<SegInstanceResultV1> loose, strict;
        PredictScene(infer.get(), image, cv::Rect(), 0.02f, 0, loose);
        PredictScene(infer.get(), image, cv::Rect(), 0.3f, 0, strict);
        InstScore scene;
        ScoreInstances(truth, strict, kInstCentreLimit, scene);
        total.truth += scene.truth; total.matched += scene.matched; total.found += scene.found;
        total.angleSum += scene.angleSum; total.angleCount += scene.angleCount; total.angleWorst = std::max(total.angleWorst, scene.angleWorst);
        std::cout << "scene " << i << ": truth " << scene.truth << " strict-found " << scene.found << " matched " << scene.matched << " loose-found " << loose.size() << "\n";
        for (const SynthObject& t : truth) {
            double nearestTruth = 1e9;
            for (const SynthObject& o : truth) if (&o != &t) nearestTruth = std::min(nearestTruth, std::hypot(o.cx - t.cx, o.cy - t.cy));
            const double radius = t.circle ? t.w * 0.5 : 0.5 * std::min(t.w, t.h);
            const double edgeDistance = std::min(std::min(t.cx, t.cy), std::min(kInstSide - t.cx, kInstSide - t.cy));
            const bool touch = nearestTruth < 1.1 * (radius + 26.0);
            double bestStrict = 1e9, bestLoose = 1e9, looseScore = 0, strictScore = 0;
            for (const auto& f : strict) if (f.classIndex == t.cls) { const double d = std::hypot(f.cx - t.cx, f.cy - t.cy); if (d < bestStrict) { bestStrict = d; strictScore = f.score; } }
            for (const auto& f : loose) if (f.classIndex == t.cls) { const double d = std::hypot(f.cx - t.cx, f.cy - t.cy); if (d < bestLoose) { bestLoose = d; looseScore = f.score; } }
            const bool hit = bestStrict <= kInstCentreLimit;
            if (touch) { ++touching; if (!hit) ++touchingMiss; }
            if (radius < 30) { ++small; if (!hit) ++smallMiss; }
            if (edgeDistance < 60) { ++edge; if (!hit) ++edgeMiss; }
            if (!hit) {
                std::cout << "  MISS cls " << t.cls << (t.circle ? " disc" : " box") << " c=(" << std::fixed << std::setprecision(1) << t.cx << "," << t.cy << ") w=" << t.w << " h=" << t.h
                          << " th=" << std::setprecision(0) << t.theta * 180 / CV_PI << " nearest-truth " << nearestTruth << " edge " << edgeDistance
                          << " | strict peak d=" << std::setprecision(1) << bestStrict << " s=" << std::setprecision(2) << strictScore
                          << " | loose peak d=" << std::setprecision(1) << bestLoose << " s=" << std::setprecision(2) << looseScore << "\n";
            }
        }
        for (const auto& f : strict) {
            double best = 1e9; int cls = -1;
            for (const SynthObject& t : truth) { const double d = std::hypot(f.cx - t.cx, f.cy - t.cy); if (d < best) { best = d; cls = t.cls; } }
            if (best > kInstCentreLimit || cls != f.classIndex)
                std::cout << "  FP cls " << f.classIndex << " c=(" << std::setprecision(1) << f.cx << "," << f.cy << ") s=" << std::setprecision(2) << f.score << " nearest-truth " << std::setprecision(1) << best << " (cls " << cls << ")\n";
        }
    }
    std::cout << std::setprecision(3) << (useTrain ? "TRAIN" : "TEST") << " recall " << double(total.matched) / std::max(1, total.truth) << " (" << total.matched << "/" << total.truth << ") precision "
              << double(total.matched) / std::max(1, total.found) << " angle mean " << (total.angleCount ? total.angleSum / total.angleCount : 0.0) << " worst " << total.angleWorst << "\n";
    std::cout << "touching objects: " << touching << " missed " << touchingMiss << "; small(radius<30): " << small << " missed " << smallMiss << "; near-edge(<60px): " << edge << " missed " << edgeMiss << "\n";
    return 0;
}

} // namespace

} // namespace

int main(int argc, char** argv)
{
    if (argc > 1 && std::strcmp(argv[1], "--baseline-ext") == 0) {
        return RunBaselineExtMode(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--class-table-selftest") == 0) {
        return RunClassTableSelfTest();
    }
    if (argc > 1 && std::strcmp(argv[1], "--inst-targets-selftest") == 0) {
        return RunInstTargetsSelfTest();
    }
    if (argc > 1 && std::strcmp(argv[1], "--inst-refine-selftest") == 0) {
        return RunInstRefineSelfTest();
    }
    if (argc > 1 && std::strcmp(argv[1], "--inst-diag") == 0) {
        return RunInstDiag(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--inst-determinism-selftest") == 0) {
        return RunInstDeterminismSelfTest(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--inst-head-selftest") == 0) {
        return RunInstHeadSelfTest(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--class-table-model") == 0) {
        return RunClassTableModelMode(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--class-table-negative") == 0) {
        return RunClassTableNegativeMode(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--class-table-resume") == 0) {
        return RunClassTableResumeMode(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--class-model-synth") == 0) {
        return RunClassModelSynthMode(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--class-model-multiscale") == 0) {
        return RunClassModelMultiscaleMode(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--class-train") == 0) {
        return RunClassTrainMode(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--class-train-finetune") == 0) {
        return RunClassTrainFineTuneMode(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--class-multi") == 0) {
        return RunClassMultiMode(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--v2-shape-cleanup-selftest") == 0) {
        return RunV2ShapeCleanupSelfTest(argc, argv);
    }
    if (argc > 1 && std::strcmp(argv[1], "--diagnose-v158") == 0) {
        return RunV158DiagnosticMode(argc, argv);
    }
    if (argc >= 4) {
        return RunCropSampleMode(argv[1], argv[2], argv[3]);
    }

    cv::Mat image;
    cv::Mat mask;
    LoadOrSynthetic(argc, argv, image, mask);

    const std::wstring modelPath = L"BeeNativeSegAI/test/data/out_model.segai";
    const std::string predPath = "BeeNativeSegAI/test/data/out_pred.png";

    void* trainer = SEGAI_TrainerCreate();
    if (!trainer) {
        std::cerr << "SEGAI_TrainerCreate failed\n";
        return 1;
    }

    const cv::Rect roi = BenchmarkRoi();

    int rc = SEGAI_TrainerSetROI(trainer, roi.x, roi.y, roi.width, roi.height);
    if (rc == 0) {
        rc = SEGAI_TrainerAddSample(trainer, image.data, image.cols, image.rows, static_cast<int>(image.step), mask.data, static_cast<int>(mask.step));
    }
    if (rc == 0) {
        rc = SEGAI_TrainerTrain(trainer, 12, 6, 2, nullptr, nullptr, nullptr);
    }
    if (rc == 0) {
        rc = SEGAI_TrainerSave(trainer, modelPath.c_str(), 0.5f, 20);
    }
    SEGAI_TrainerDestroy(trainer);

    if (rc != 0) {
        std::cerr << "Trainer pipeline failed: " << rc << "\n";
        return 2;
    }

    void* inferer = SEGAI_InferCreate();
    if (!inferer) {
        std::cerr << "SEGAI_InferCreate failed\n";
        return 3;
    }

    uint8_t* outMask = nullptr;
    int outW = 0;
    int outH = 0;
    float score = 0.0f;

    rc = SEGAI_InferLoad(inferer, modelPath.c_str());
    if (rc != 0) {
        SEGAI_InferDestroy(inferer);
        std::cerr << "Infer pipeline failed: " << rc << "\n";
        return 4;
    }

    cv::Mat predCopy;
    double cpuMs = 0.0;
    double gpuMs = 0.0;
    double agreement = 1.0;
    float gpuScore = 0.0f;

    {
        cv::TickMeter meter;
        meter.start();
        SEGAI_InferSetGpu(inferer, 0);
        rc = SEGAI_InferPredict(
            inferer,
            image.data,
            image.cols,
            image.rows,
            static_cast<int>(image.step),
            roi.x,
            roi.y,
            roi.width,
            roi.height,
            0.5f,
            &outMask,
            &outW,
            &outH,
            &score);
        meter.stop();
        cpuMs = meter.getTimeMilli();
    }

    if (rc != 0 || !outMask) {
        SEGAI_InferDestroy(inferer);
        std::cerr << "CPU infer pipeline failed: " << rc << "\n";
        return 4;
    }

    cv::Mat pred(outH, outW, CV_8UC1, outMask);
    predCopy = pred.clone();
    SEGAI_FreeBuffer(outMask);
    outMask = nullptr;

    if (SEGAI_InferGetGpuAvailable() != 0) {
        cv::TickMeter meter;
        meter.start();
        SEGAI_InferSetGpu(inferer, 1);
        rc = SEGAI_InferPredict(
            inferer,
            image.data,
            image.cols,
            image.rows,
            static_cast<int>(image.step),
            roi.x,
            roi.y,
            roi.width,
            roi.height,
            0.5f,
            &outMask,
            &outW,
            &outH,
            &gpuScore);
        meter.stop();
        gpuMs = meter.getTimeMilli();

        if (rc != 0 || !outMask) {
            SEGAI_InferDestroy(inferer);
            std::cerr << "GPU infer pipeline failed: " << rc << "\n";
            return 6;
        }

        cv::Mat gpuPred(outH, outW, CV_8UC1, outMask);
        agreement = ComputeAgreement(predCopy, gpuPred);
        SEGAI_FreeBuffer(outMask);
        outMask = nullptr;
    }

    SEGAI_InferDestroy(inferer);

    cv::imwrite(predPath, predCopy);

    const double iou = ComputeIou(predCopy(roi), mask(roi));
    std::cout << std::fixed << std::setprecision(6)
              << "score=" << score
              << " iou=" << iou
              << " cpu_ms=" << cpuMs;
    if (gpuMs > 0.0) {
        std::cout << " gpu_ms=" << gpuMs
                  << " gpu_speedup=" << (cpuMs / gpuMs)
                  << " gpu_agreement=" << agreement;
    } else {
        std::cout << " gpu_ms=unavailable gpu_speedup=unavailable gpu_agreement=unavailable";
    }
    std::cout << " pred=" << predPath << "\n";
    return iou >= 0.50 && (gpuMs <= 0.0 || agreement >= 0.99) ? 0 : 5;
}
