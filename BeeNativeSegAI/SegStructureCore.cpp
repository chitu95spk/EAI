#include "pch.h"
#include "SegStructureCore.h"
#include <opencv2/calib3d.hpp>
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>
#include <algorithm>
#include <cmath>
#include <limits>
#include <climits>
#include <windows.h>
#include "../ThirdParty/OpenCV455Contrib/src/opencv-4.5.5/3rdparty/include/opencl/1.2/CL/cl.h"

namespace BeeSegAI {

namespace {
struct ClApi {
    HMODULE module = nullptr;
#define CL_FN(name) decltype(&name) name = nullptr;
    CL_FN(clGetPlatformIDs) CL_FN(clGetDeviceIDs) CL_FN(clGetDeviceInfo)
    CL_FN(clCreateContext) CL_FN(clCreateCommandQueue) CL_FN(clCreateProgramWithSource)
    CL_FN(clBuildProgram) CL_FN(clCreateKernel) CL_FN(clCreateBuffer) CL_FN(clSetKernelArg)
    CL_FN(clEnqueueNDRangeKernel) CL_FN(clEnqueueReadBuffer) CL_FN(clFinish)
    CL_FN(clReleaseMemObject) CL_FN(clReleaseKernel) CL_FN(clReleaseProgram)
    CL_FN(clReleaseCommandQueue) CL_FN(clReleaseContext)
#undef CL_FN
    bool Load() {
        module = LoadLibraryExW(L"OpenCL.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) return false;
#define CL_LOAD(name) name = reinterpret_cast<decltype(name)>(GetProcAddress(module, #name)); if (!name) return false;
        CL_LOAD(clGetPlatformIDs) CL_LOAD(clGetDeviceIDs) CL_LOAD(clGetDeviceInfo)
        CL_LOAD(clCreateContext) CL_LOAD(clCreateCommandQueue) CL_LOAD(clCreateProgramWithSource)
        CL_LOAD(clBuildProgram) CL_LOAD(clCreateKernel) CL_LOAD(clCreateBuffer) CL_LOAD(clSetKernelArg)
        CL_LOAD(clEnqueueNDRangeKernel) CL_LOAD(clEnqueueReadBuffer) CL_LOAD(clFinish)
        CL_LOAD(clReleaseMemObject) CL_LOAD(clReleaseKernel) CL_LOAD(clReleaseProgram)
        CL_LOAD(clReleaseCommandQueue) CL_LOAD(clReleaseContext)
#undef CL_LOAD
        return true;
    }
    cl_device_id Device() {
        cl_uint count = 0;
        if (clGetPlatformIDs(0, nullptr, &count) != CL_SUCCESS || count > 64) return nullptr;
        std::vector<cl_platform_id> platforms(count);
        if (clGetPlatformIDs(count, platforms.data(), nullptr) != CL_SUCCESS) return nullptr;
        cl_device_id fallback = nullptr;
        for (auto platform : platforms) {
            cl_uint n = 0;
            if (clGetDeviceIDs(platform, CL_DEVICE_TYPE_GPU, 0, nullptr, &n) != CL_SUCCESS || n > 64) continue;
            std::vector<cl_device_id> devices(n);
            if (clGetDeviceIDs(platform, CL_DEVICE_TYPE_GPU, n, devices.data(), nullptr) != CL_SUCCESS) continue;
            for (auto device : devices) {
                cl_bool available = CL_FALSE, integrated = CL_TRUE;
                clGetDeviceInfo(device, CL_DEVICE_AVAILABLE, sizeof(available), &available, nullptr);
                clGetDeviceInfo(device, CL_DEVICE_HOST_UNIFIED_MEMORY, sizeof(integrated), &integrated, nullptr);
                if (!available) continue;
                if (!integrated) return device;
                fallback = device;
            }
        }
        return fallback;
    }
    ~ClApi() { if (module) FreeLibrary(module); }
};
}
class SegGpuMatcher {
    ClApi api;
    cl_context context = nullptr;
    cl_command_queue queue = nullptr;
    cl_program program = nullptr;
    cl_kernel kernel = nullptr;
public:
    SegGpuMatcher() {
        if (!api.Load()) return;
        cl_device_id device = api.Device(); if (!device) return;
        cl_int error = 0;
        context = api.clCreateContext(nullptr, 1, &device, nullptr, nullptr, &error);
        if (!context || error != CL_SUCCESS) return;
        queue = api.clCreateCommandQueue(context, device, 0, &error);
        if (!queue || error != CL_SUCCESS) return;
        const char* source = R"CLC(
    __kernel void segai_knn(__global const uint* q, __global const uint* t,
                            __global int* out, int nq, int nt) {
        int i = get_global_id(0); if (i >= nq) return;
        int a = -1, b = -1, da = 257, db = 257;
        for (int j = 0; j < nt; ++j) {
            int d = 0;
            for (int k = 0; k < 8; ++k) d += popcount(q[i*8+k] ^ t[j*8+k]);
            if (d < da) { b=a; db=da; a=j; da=d; }
            else if (d < db) { b=j; db=d; }
        }
        out[i*4]=a; out[i*4+1]=b; out[i*4+2]=da; out[i*4+3]=db;
    })CLC";
        program = api.clCreateProgramWithSource(context, 1, &source, nullptr, &error);
        if (!program || error != CL_SUCCESS || api.clBuildProgram(program, 1, &device, nullptr, nullptr, nullptr) != CL_SUCCESS) return;
        kernel = api.clCreateKernel(program, "segai_knn", &error);
        if (error != CL_SUCCESS) kernel = nullptr;
    }
    ~SegGpuMatcher() {
        if (queue) api.clFinish(queue);
        if (kernel) api.clReleaseKernel(kernel);
        if (program) api.clReleaseProgram(program);
        if (queue) api.clReleaseCommandQueue(queue);
        if (context) api.clReleaseContext(context);
    }
    bool Match(const cv::Mat& query, const cv::Mat& train, std::vector<std::vector<cv::DMatch>>& matches) {
        if (!kernel || !query.isContinuous() || !train.isContinuous()) return false;
        cl_int error = 0;
        cl_mem q = nullptr, t = nullptr, out = nullptr;
        auto release = [&]() { if(q)api.clReleaseMemObject(q); if(t)api.clReleaseMemObject(t); if(out)api.clReleaseMemObject(out); };
        q = api.clCreateBuffer(context, CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR, query.total(), query.data, &error);
        if (!q || error) { release(); return false; }
        t = api.clCreateBuffer(context, CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR, train.total(), train.data, &error);
        if (!t || error) { release(); return false; }
        out = api.clCreateBuffer(context, CL_MEM_WRITE_ONLY, query.rows * 4 * sizeof(int), nullptr, &error);
        if (!out || error) { release(); return false; }
        error = api.clSetKernelArg(kernel, 0, sizeof(q), &q) | api.clSetKernelArg(kernel, 1, sizeof(t), &t) |
            api.clSetKernelArg(kernel, 2, sizeof(out), &out) | api.clSetKernelArg(kernel, 3, sizeof(int), &query.rows) |
            api.clSetKernelArg(kernel, 4, sizeof(int), &train.rows);
        size_t global = size_t(query.rows);
        if (error || api.clEnqueueNDRangeKernel(queue, kernel, 1, nullptr, &global, nullptr, 0, nullptr, nullptr) != CL_SUCCESS) { release(); return false; }
        std::vector<int> result(size_t(query.rows) * 4);
        error = api.clEnqueueReadBuffer(queue, out, CL_TRUE, 0, result.size() * sizeof(int), result.data(), 0, nullptr, nullptr);
        release(); if (error != CL_SUCCESS) return false;
        matches.clear(); matches.resize(query.rows);
        for (int i = 0; i < query.rows; ++i) {
            const int* row = result.data() + i * 4;
            if (row[0] >= 0) matches[i].emplace_back(i, row[0], float(row[2]));
            if (row[1] >= 0) matches[i].emplace_back(i, row[1], float(row[3]));
        }
        return true;
    }
};
bool StructuralGpuAvailable() { ClApi api; return api.Load() && api.Device() != nullptr; }

namespace {
constexpr int kWorkSide = 800;
constexpr int kMaxExamples = 256;
cv::Mat Prepare(const cv::Mat& bgr) {
    cv::Mat small, gray;
    double scale = std::min(1., double(kWorkSide) / std::max(bgr.cols, bgr.rows));
    cv::resize(bgr, small, cv::Size(), scale, scale, cv::INTER_AREA);
    cv::cvtColor(small, gray, cv::COLOR_BGR2GRAY);
    cv::createCLAHE(2., cv::Size(8, 8))->apply(gray, gray);
    return gray;
}
cv::Ptr<cv::ORB> Detector(int features) {
    return cv::ORB::create(features, 1.2f, 8, 15, 0, 2, cv::ORB::HARRIS_SCORE, 31, 8);
}


double Overlap(const cv::Mat& a, const cv::Mat& b) {
    cv::Mat both; cv::bitwise_and(a, b, both);
    return double(cv::countNonZero(both)) / std::max(1, std::min(cv::countNonZero(a), cv::countNonZero(b)));
}
}
void SegStructureModel::Configure(int detail) {
    featureBudget_ = std::clamp(detail * 210, 1000, 4000);
}
bool SegStructureModel::Ready() const {
    bool hasDefect = false, hasNormal = false;
    for (const auto& example : examples_) {
        hasDefect |= example.label == 1; hasNormal |= example.label == 2;
    }
    return defectPixels_ > 0 && normalPixels_ > 0 && hasDefect && hasNormal;
}
int SegStructureModel::AddSample(const cv::Mat& bgr, const cv::Mat& labels) {
    if (bgr.empty() || bgr.type() != CV_8UC3 || labels.type() != CV_8UC1 || labels.size() != bgr.size()) return -2;
    cv::Mat gray = Prepare(bgr), smallLabels, normalized = cv::Mat::zeros(labels.size(), CV_8U);
    normalized.setTo(1, (labels == 1) | (labels >= 200));
    normalized.setTo(2, (labels == 2) | ((labels >= 100) & (labels < 200)));
    cv::resize(normalized, smallLabels, gray.size(), 0, 0, cv::INTER_NEAREST);
    std::vector<Example> pending;
    int counts[3] = {};
    auto detector = Detector(featureBudget_);
    for (int label = 1; label <= 2; ++label) {
        cv::Mat binary = smallLabels == label, components, stats, centroids;
        int total = cv::connectedComponentsWithStats(binary, components, stats, centroids, 8);
        for (int component = 1; component < total; ++component) {
            if (stats.at<int>(component, cv::CC_STAT_AREA) < 16) continue;
            Example example; example.label = label;
            cv::Mat mask = components == component, context;
            cv::dilate(mask, context, cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(21, 21)));
            detector->detectAndCompute(gray, context, example.points, example.descriptors);
            if (example.points.size() < 5) return -8;
            cv::Rect box = cv::boundingRect(context);
            box = cv::Rect(box.x - 16, box.y - 16, box.width + 32, box.height + 32) & cv::Rect(0, 0, gray.cols, gray.rows);
            example.gray = gray(box).clone(); example.mask = mask(box).clone();
            for (auto& point : example.points) point.pt -= cv::Point2f(float(box.x), float(box.y));
            pending.push_back(std::move(example));
        }
        counts[label] = cv::countNonZero(normalized == label);
    }
    if (pending.empty()) return -3;
    if (examples_.size() + pending.size() > kMaxExamples) return -9;
    examples_.insert(examples_.end(), pending.begin(), pending.end());
    defectPixels_ = int(std::min<int64_t>(INT_MAX, int64_t(defectPixels_) + counts[1]));
    normalPixels_ = int(std::min<int64_t>(INT_MAX, int64_t(normalPixels_) + counts[2]));
    return 0;
}
int SegStructureModel::Predict(const cv::Mat& bgr, float threshold, cv::Mat& confidence, cv::Mat& normal, bool highSpeed, bool useGpu, bool* usedGpu, bool assist, float angleStep, float angleRange, float minimumScale, float maximumScale) const {
    confidence.release(); normal.release();
    if (!Ready()) return -1;
    cv::Mat gray = Prepare(bgr), descriptors;
    std::vector<cv::KeyPoint> points;
    Detector(highSpeed ? std::min(3500, featureBudget_ + 500) : std::min(6000, featureBudget_ * 2))->detectAndCompute(gray, cv::noArray(), points, descriptors);
    if (descriptors.empty()) return -6;
    struct Candidate { int label; double score; cv::Mat mask; };
    std::vector<Candidate> candidates;
    auto matcher = cv::BFMatcher::create(cv::NORM_HAMMING);
    if (useGpu && !gpuMatcher_) gpuMatcher_ = std::make_shared<SegGpuMatcher>();
    for (const auto& example : examples_) {
        // Remove each discovered target from the scene to support repeated instances.
        cv::Mat available = descriptors.clone();
        std::vector<int> pointIds(points.size());
        for (size_t i = 0; i < points.size(); ++i) pointIds[i] = int(i);
        for (int instance = 0; available.rows >= 5; ++instance) {
            if (instance >= 64) return -9;
            std::vector<std::vector<cv::DMatch>> matches;
            if (useGpu && gpuMatcher_->Match(example.descriptors, available, matches)) { if (usedGpu) *usedGpu = true; }
            else matcher->knnMatch(example.descriptors, available, matches, 2);
            std::vector<cv::Point2f> from, to;
            for (const auto& match : matches) {
                if (match.size() == 2 && match[0].distance < 65 &&
                    (match[0].distance < 12 || match[0].distance < .78f * match[1].distance)) {
                    from.push_back(example.points[match[0].queryIdx].pt);
                    to.push_back(points[pointIds[match[0].trainIdx]].pt);
                }
            }
            if (from.size() < 5) break;
            cv::Mat inliers;
            cv::Mat transform = cv::estimateAffinePartial2D(from, to, inliers, cv::RANSAC, 3., 2000, .995);
            int count = inliers.empty() ? 0 : cv::countNonZero(inliers);
            // With repeated targets, valid correspondences are split across instances.
            // The photometric check below still validates the aligned region itself.
            if (transform.empty() || count < 5 || count < int(from.size() * .1)) break;
            double scale = std::hypot(transform.at<double>(0, 0), transform.at<double>(1, 0));
            // A unity range requests rigid matching: features localize the center,
            // then the photometric check scores the original-size template.
            const bool fixedScale = minimumScale == 1.f && maximumScale == 1.f;
            const bool allowedScale = fixedScale || (scale >= double(minimumScale) - 1e-6 && scale <= double(maximumScale) + 1e-6);
            // Keep legacy defaults unchanged; custom ranges can skip an oversized
            // instance and continue looking for another instance in the scene.
            if (!allowedScale && minimumScale == .6f && maximumScale == 1.6f) break;
            cv::Mat mask;
            cv::warpAffine(example.mask, mask, transform, gray.size(), cv::INTER_NEAREST);
            if (cv::countNonZero(mask) < 100) break;
            const double angle = std::atan2(transform.at<double>(1, 0), transform.at<double>(0, 0)) * 180. / CV_PI;
            double score = -1;
            cv::Mat bestMask;
            // The invariant feature match localizes the target first. Evaluate a bounded
            // local rotation grid instead of scanning every angle over the whole image.
            if (allowedScale && std::abs(angle) <= angleRange + .25) {
                std::vector<double> angles;
                if (angleStep <= 0) angles.push_back(std::clamp(angle, -double(angleRange), double(angleRange)));
                else {
                    const double nearest = std::round(angle / angleStep) * angleStep;
                    for (int offset = -1; offset <= 1; ++offset) {
                        double trial = nearest + offset * angleStep;
                        if (std::abs(trial) <= angleRange + 1e-6) angles.push_back(trial);
                    }
                    if (angles.empty()) angles.push_back(0);
                }
                const double cx = example.gray.cols * .5, cy = example.gray.rows * .5;
                const double targetX = transform.at<double>(0, 0)*cx + transform.at<double>(0, 1)*cy + transform.at<double>(0, 2);
                const double targetY = transform.at<double>(1, 0)*cx + transform.at<double>(1, 1)*cy + transform.at<double>(1, 2);
                for (double trial : angles) {
                    cv::Mat pose = transform.clone();
                    if (fixedScale || std::abs(trial - angle) > 1e-8) {
                        const double poseScale = fixedScale ? 1. : scale;
                        const double c = poseScale * std::cos(trial * CV_PI / 180.), sn = poseScale * std::sin(trial * CV_PI / 180.);
                        pose = (cv::Mat_<double>(2, 3) << c, -sn, targetX-c*cx+sn*cy, sn, c, targetY-sn*cx-c*cy);
                    }
                    cv::Mat warped, trialMask;
                    cv::warpAffine(example.gray, warped, pose, gray.size());
                    cv::warpAffine(example.mask, trialMask, pose, gray.size(), cv::INTER_NEAREST);
                    if (cv::countNonZero(trialMask) < 100) continue;
                    cv::Scalar meanA, stdA, meanB, stdB;
                    cv::meanStdDev(warped, meanA, stdA, trialMask); cv::meanStdDev(gray, meanB, stdB, trialMask);
                    cv::Mat a, b; warped.convertTo(a, CV_32F); gray.convertTo(b, CV_32F);
                    a -= meanA[0]; b -= meanB[0];
                    double similarity = cv::mean(a.mul(b), trialMask)[0] / std::max(1., stdA[0] * stdB[0]);
                    double value = std::clamp(similarity, 0., 1.) * std::min(1., count / 15.);
                    if (value > score) { score = value; bestMask = trialMask; }
                }
                if (score < std::max(assist ? .35 : .55, double(threshold))) break;
                if (instance >= 64 || candidates.size() >= 512) return -9;
                mask = bestMask; candidates.push_back({ example.label, score, mask });
            }
            cv::Mat excluded; cv::dilate(mask, excluded, cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(31, 31)));
            cv::Mat remaining; std::vector<int> remainingIds;
            for (int row = 0; row < available.rows; ++row) {
                const auto& pt = points[pointIds[row]].pt;
                if (excluded.at<unsigned char>(std::clamp(int(pt.y), 0, gray.rows - 1), std::clamp(int(pt.x), 0, gray.cols - 1)) == 0) {
                    remaining.push_back(available.row(row)); remainingIds.push_back(pointIds[row]);
                }
            }
            available = remaining; pointIds.swap(remainingIds);
        }
    }
    if (candidates.empty()) return -6;
    std::sort(candidates.begin(), candidates.end(), [](const Candidate& a, const Candidate& b) { return a.score > b.score; });
    cv::Mat occupied = cv::Mat::zeros(gray.size(), CV_8U), result = cv::Mat::zeros(gray.size(), CV_8U);
    cv::Mat normalResult = cv::Mat::zeros(gray.size(), CV_8U);
    int targets = 0;
    for (size_t i = 0; i < candidates.size(); ++i) {
        const auto& candidate = candidates[i];
        if (Overlap(candidate.mask, occupied) > .25) continue;
        for (size_t j = i + 1; j < candidates.size(); ++j)
            if (candidates[j].label != candidate.label && candidate.score - candidates[j].score < .08 &&
                Overlap(candidate.mask, candidates[j].mask) > .5) return -7;
        cv::bitwise_or(occupied, candidate.mask, occupied); ++targets;
        if (candidate.label == 1) result.setTo(cv::Scalar(std::nearbyint(candidate.score * 255.)), candidate.mask);
        else normalResult.setTo(cv::Scalar(std::nearbyint(candidate.score * 255.)), candidate.mask);
        if (targets > 64) return -9;
    }
    cv::resize(result, confidence, bgr.size(), 0, 0, cv::INTER_NEAREST);
    cv::resize(normalResult, normal, bgr.size(), 0, 0, cv::INTER_NEAREST);
    return 0;
}
void SegStructureModel::Write(cv::FileStorage& storage) const {
    storage << "structure_version" << 1 << "features" << featureBudget_ << "ng_pixels" << defectPixels_ << "ok_pixels" << normalPixels_;
    storage << "examples" << "[";
    for (const auto& e : examples_) {
        std::vector<unsigned char> imageBytes, maskBytes;
        cv::imencode(".png", e.gray, imageBytes); cv::imencode(".png", e.mask, maskBytes);
        cv::Mat positions(int(e.points.size()), 2, CV_32F);
        for (int i = 0; i < positions.rows; ++i) { positions.at<float>(i, 0) = e.points[i].pt.x; positions.at<float>(i, 1) = e.points[i].pt.y; }
        storage << "{" << "label" << e.label << "gray" << cv::Mat(imageBytes) << "mask" << cv::Mat(maskBytes) << "points" << positions << "descriptors" << e.descriptors << "}";
    }
    storage << "]";
}
bool SegStructureModel::Read(const cv::FileStorage& storage) {
    if (int(storage["structure_version"]) != 1) return false;
    SegStructureModel loaded;
    loaded.featureBudget_ = int(storage["features"]); loaded.defectPixels_ = int(storage["ng_pixels"]); loaded.normalPixels_ = int(storage["ok_pixels"]);
    auto entries = storage["examples"];
    if (!entries.isSeq() || entries.size() > kMaxExamples || loaded.featureBudget_ < 1000 || loaded.featureBudget_ > 4000) return false;
    for (const auto& node : entries) {
        Example e; e.label = int(node["label"]); cv::Mat imageBytes, maskBytes, positions;
        node["gray"] >> imageBytes; node["mask"] >> maskBytes; node["points"] >> positions; node["descriptors"] >> e.descriptors;
        e.gray = cv::imdecode(imageBytes, cv::IMREAD_GRAYSCALE); e.mask = cv::imdecode(maskBytes, cv::IMREAD_GRAYSCALE);
        if ((e.label != 1 && e.label != 2) || e.gray.empty() || e.gray.cols > kWorkSide || e.gray.rows > kWorkSide ||
            e.mask.size() != e.gray.size() || cv::countNonZero(e.mask) < 16 || positions.type() != CV_32F || positions.cols != 2 || positions.rows < 5 || positions.rows > 5000 ||
            e.descriptors.type() != CV_8U || e.descriptors.cols != 32 || e.descriptors.rows != positions.rows || !cv::checkRange(positions)) return false;
        for (int i = 0; i < positions.rows; ++i) e.points.emplace_back(positions.at<float>(i, 0), positions.at<float>(i, 1), 31.f);
        loaded.examples_.push_back(std::move(e));
    }
    if (!loaded.Ready()) return false;
    *this = std::move(loaded); return true;
}
}
