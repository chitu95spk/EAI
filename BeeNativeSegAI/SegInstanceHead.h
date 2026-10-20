#pragma once

// EAI-POS P3: CenterNet-style instance branch trained on the frozen 448 channel feature grid of the V2 backbone.
// Plain C++ plus cv::Mat (no OpenVINO) so the trainer can be unit-tested and the maths stays separate from SegV2Core.
//
// Network: x(448) -> 1x1 conv 64 + ReLU -> 1x1 conv (C + 7) with C = number of classes (no background).
//   outputs 0..C-1   heat logits (sigmoid at inference), one plane per class
//   outputs C..C+6   regression in the SegInstanceTargets layout (offset x, offset y, log w/8, log h/8, sin(k theta),
//                    cos(k theta), circle flag)
// Standardisation of the input is folded into the first layer after training (same as the semantic head), so the weights in
// InstanceHeadWeights apply to raw backbone features.
//
// Loss (per training batch): CenterNet focal loss (alpha 2, beta 4) summed over the heat planes and normalised by the number of
// positive cells (target == 1) in the batch, plus an L1 loss on the regression channels at regMask cells normalised by the
// number of masked cells; the two angle channels carry no loss for circles. The branch has its own RNG and its own Adam, so
// the semantic head is never touched.

#include "SegInstanceTargets.h"

#include <opencv2/core.hpp>

#include <functional>
#include <random>
#include <string>
#include <vector>

namespace BeeSegAI {

constexpr int kInstanceFeatureChannels = 448;
constexpr int kInstanceHidden = 128;
constexpr int kInstanceMaxRowsPerJob = 512;
constexpr int kInstanceHeadVersion = 1;

struct InstanceHeadWeights {
    int classes = 0;
    cv::Mat w1, b1;            // 448 x 128 and 1 x 128 (CV_32F), standardisation folded in
    cv::Mat w2, b2;            // 128 x (classes + 7) and 1 x (classes + 7)
    std::vector<int> symmetry; // per class k (1, 2 or 4), size == classes
    int Outputs() const { return classes + kInstanceRegChannels; }
    // Shapes, types and finite values; the symmetry vector must hold classes values from {1, 2, 4}.
    bool Valid(std::string* reason = nullptr) const;
};

// Training rows of one image variant (or the concatenation of many). features are RAW backbone features.
struct InstanceTrainRows {
    cv::Mat features;   // N x 448, CV_32F
    cv::Mat heat;       // N x C, CV_32F target heat (1.0 exactly at a centre cell)
    cv::Mat reg;        // N x 7, CV_32F
    cv::Mat regMask;    // N x 1, CV_32F (1 where reg is valid)
    cv::Mat weight;     // N x 1, CV_32F loss weight of the heat targets (1 for the object rows, >= 1 for sampled background)
    double positives = 0; // number of (class, cell) peaks of the images that produced these rows
    int dropped = 0;      // labels the target renderer dropped (centre outside the grid, bad class, centre cell shared)
    int Rows() const { return features.rows; }
};

// Selects at most maxRows cells of one rendered target set: every centre / regression cell, the Gaussian halo cells
// (heat >= 0.05) and a random sample of the remaining background cells, which carries weight (cells / sampled) so the
// sampled background loss is an unbiased estimate of the dense one. featureRows is grid*grid x 448 (row = gy * grid + gx).
void GatherInstanceRows(const cv::Mat& featureRows, const InstanceTargets& targets, std::mt19937& random, int maxRows,
                        InstanceTrainRows& out);

// Concatenates parts in order into one set; each part is released as soon as it has been copied (peak memory = result + one part).
void ConcatInstanceRows(std::vector<InstanceTrainRows>& parts, InstanceTrainRows& out);

struct InstanceTrainOptions {
    int epochs = 60;
    float learningRate = 2e-3f;
    int batchRows = 4096;
    unsigned seed = 41;
    float inputDropout = 0.3f;   // P3b regularisation against memorising the training scenes (training only, folded out at inference)
    float hiddenDropout = 0.2f;
    float weightDecay = 0.05f;   // decoupled (AdamW style) on the two weight matrices
};

struct InstanceTrainReport {
    int rows = 0;
    double positives = 0;
    int epochs = 0;
    int steps = 0;
    double firstLoss = 0;   // mean batch loss of epoch 1
    double lastLoss = 0;    // mean batch loss of the last epoch
    double focalLast = 0;   // focal part of lastLoss
    double regLast = 0;     // regression part of lastLoss
};

// Standardises data.features IN PLACE, trains, folds the standardisation into out and returns true. progress(epoch) is called after
// every epoch; cancelled() is polled once per step. On failure returns false with a reason in error. symmetry must have `classes`
// entries (only used to fill out.symmetry).
bool TrainInstanceHead(InstanceTrainRows& data, int classes, const std::vector<int>& symmetry, const InstanceTrainOptions& options,
                       const std::function<bool()>& cancelled, const std::function<void(int)>& progress,
                       InstanceHeadWeights& out, InstanceTrainReport& report, std::string& error);

// Reference CPU evaluation of one feature row (raw features) into heat logits + regression, for tests and diagnostics.
void EvaluateInstanceHeadRow(const InstanceHeadWeights& weights, const float* features, float* outputs);

} // namespace BeeSegAI
