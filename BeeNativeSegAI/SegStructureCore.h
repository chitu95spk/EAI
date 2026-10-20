#pragma once
#include <opencv2/core.hpp>
#include <opencv2/features2d.hpp>
#include <vector>
#include <memory>
namespace BeeSegAI {
class SegGpuMatcher;
bool StructuralGpuAvailable();
// Positive examples define inspection regions. Unlabeled pixels are never normal labels.
class SegStructureModel {
public:
    void Configure(int detail);
    int AddSample(const cv::Mat& bgr, const cv::Mat& labels);
    int Predict(const cv::Mat& bgr, float threshold, cv::Mat& confidence, cv::Mat& normal, bool highSpeed = false, bool useGpu = false, bool* usedGpu = nullptr, bool assist = false, float angleStep = 0, float angleRange = 180, float minimumScale = .6f, float maximumScale = 1.6f) const;
    void Write(cv::FileStorage& storage) const;
    bool Read(const cv::FileStorage& storage);
    bool Ready() const;
    int DefectPixels() const { return defectPixels_; }
    int NormalPixels() const { return normalPixels_; }
private:
    struct Example {
        int label = 0;
        cv::Mat gray, mask, descriptors;
        std::vector<cv::KeyPoint> points;
    };
    mutable std::shared_ptr<SegGpuMatcher> gpuMatcher_;
    int featureBudget_ = 2500;
    int defectPixels_ = 0, normalPixels_ = 0;
    std::vector<Example> examples_;
};
}
