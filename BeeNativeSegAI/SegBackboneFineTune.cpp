#include "SegBackboneFineTune.h"

#if defined(BEESEGAI_TORCH) && BEESEGAI_TORCH
#pragma warning(push)
#pragma warning(disable : 4251 4244 4267 4275 4018 4624 4101 4996)
#include <torch/script.h>
#include <torch/torch.h>
#pragma warning(pop)
#include <opencv2/imgproc.hpp>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <fstream>
#include <limits>
#include <sstream>

namespace SegFineTune {

bool Compiled() { return true; }

bool Probe(const std::string& path, int threads, int side, std::string& report) {
    try {
        if (threads < 1 || threads > 64 || side < 160 || side > 2560 || side % 32) { report = "invalid threads or side"; return false; }
        at::set_num_threads(threads);
        torch::NoGradGuard noGrad;
        auto module = torch::jit::load(path);
        module.eval();
        auto taps = module.forward({ torch::zeros({ 1, 3, side, side }) }).toList();
        std::ostringstream text;
        text << "threads=" << at::get_num_threads() << " taps=" << taps.size();
        for (size_t i = 0; i < taps.size(); ++i) {
            const auto shape = taps.get(i).toTensor().sizes();
            text << " [" << shape[1] << "x" << shape[2] << "x" << shape[3] << "]";
        }
        report = text.str();
        return taps.size() == 3;
    } catch (const std::exception& error) {
        report = error.what();
        return false;
    } catch (...) {
        report = "unknown torch error";
        return false;
    }
}

bool GradientProbe(const std::string& path, int threads, int side, const std::string& gradPath, std::string& report) {
    try {
        if (threads < 1 || threads > 64 || side < 160 || side > 2560 || side % 32) { report = "invalid threads or side"; return false; }
        at::set_num_threads(threads);
        auto module = torch::jit::load(path);
        module.train();
        std::vector<at::Tensor> parameters; std::vector<std::string> names;
        for (const auto& item : module.named_parameters()) { item.value.set_requires_grad(true); parameters.push_back(item.value); names.push_back(item.name); }
        auto input = torch::empty({ 1, 3, side, side }, torch::kFloat32);
        float* data = input.data_ptr<float>();
        for (int64_t i = 0; i < input.numel(); ++i) data[i] = float(0.5 + 0.5 * std::sin(0.001 * double(i)));
        auto taps = module.forward({ input }).toList();
        at::Tensor loss = torch::zeros({}, torch::kFloat32);
        for (size_t i = 0; i < taps.size(); ++i) { auto tap = taps.get(i).toTensor(); loss = loss + (tap * tap).mean(); }
        loss.backward();
        std::ofstream binary(gradPath, std::ios::binary), list(gradPath + ".names.txt");
        double norm = 0;
        for (size_t i = 0; i < parameters.size(); ++i) {
            auto gradient = parameters[i].grad();
            if (!gradient.defined()) { report = "missing gradient for " + names[i]; return false; }
            gradient = gradient.contiguous().to(torch::kFloat32);
            binary.write(reinterpret_cast<const char*>(gradient.data_ptr<float>()), std::streamsize(gradient.numel() * 4));
            list << names[i] << ' ' << gradient.numel() << std::endl;
            norm += (gradient * gradient).sum().item<double>();
        }
        std::ostringstream text; text.precision(9);
        text << "loss=" << loss.item<float>() << " params=" << parameters.size() << " gradNorm=" << std::sqrt(norm);
        report = text.str();
        return binary.good() && list.good();
    } catch (const std::exception& error) { report = error.what(); return false; }
    catch (...) { report = "unknown torch error"; return false; }
}

namespace {

constexpr int Channels = 448, Hidden = 32, MaxClasses = 33;

// Same preprocessing as SegV2Core::Input: planar RGB, (v / 255 - mean) / sd.
at::Tensor ToInput(const cv::Mat& bgr) {
    at::Tensor t = torch::from_blob(const_cast<unsigned char*>(bgr.data), { bgr.rows, bgr.cols, 3 }, torch::kUInt8).clone();
    t = t.to(torch::kFloat32).div(255.f).flip({ 2 }).permute({ 2, 0, 1 });
    const at::Tensor mean = torch::tensor({ .485f, .456f, .406f }).reshape({ 3, 1, 1 });
    const at::Tensor sd = torch::tensor({ .229f, .224f, .225f }).reshape({ 3, 1, 1 });
    return ((t - mean) / sd).unsqueeze(0).contiguous();
}

// Same selection as SegV2Core::Gather but returning grid indices (row = y * gridWidth + x) and labels.
void SelectRows(const Frame& frame, int classes, int bgCap, int fgCap, std::mt19937& random, int& gridWidth, int& gridHeight,
                std::vector<int64_t>& index, std::vector<int64_t>& labels) {
    gridWidth = frame.image.cols / 8; gridHeight = frame.image.rows / 8;
    cv::Mat reduced;
    cv::resize(frame.labels, reduced, cv::Size(gridWidth, gridHeight), 0, 0, cv::INTER_NEAREST_EXACT);
    std::vector<int> byClass[MaxClasses];
    for (int y = 0; y < gridHeight; ++y) for (int x = 0; x < gridWidth; ++x) {
        const int sx = x * 8 + 3, sy = y * 8 + 3;
        if (sx < frame.left || sy < frame.top || sx >= frame.left + frame.width || sy >= frame.top + frame.height) continue;
        const int label = reduced.at<unsigned char>(y, x);
        if (label >= 0 && label < classes) byClass[label].push_back(y * gridWidth + x);
    }
    index.clear(); labels.clear();
    for (int c = 0; c < classes; ++c) {
        std::shuffle(byClass[c].begin(), byClass[c].end(), random);
        const size_t limit = size_t(c == 0 ? bgCap : fgCap);
        if (byClass[c].size() > limit) byClass[c].resize(limit);
        for (int value : byClass[c]) { index.push_back(value); labels.push_back(c); }
    }
}

struct TorchHead {
    at::Tensor hiddenWeights, hiddenBias, weights, bias, mean, sd;
    std::vector<at::Tensor> Trainable() const { return { hiddenWeights, hiddenBias, weights, bias }; }
    at::Tensor Logits(const at::Tensor& rows) const {
        const at::Tensor x = (rows - mean) / sd;
        return torch::relu(x.matmul(hiddenWeights) + hiddenBias).matmul(weights) + bias;
    }
};

at::Tensor ToTensor(const cv::Mat& mat) {
    cv::Mat copy = mat.clone();
    return torch::from_blob(copy.data, { copy.rows, copy.cols }, torch::kFloat32).clone();
}

cv::Mat ToMat(const at::Tensor& tensor) {
    const at::Tensor values = tensor.detach().contiguous().to(torch::kFloat32);
    cv::Mat mat(int(values.size(0)), int(values.dim() > 1 ? values.size(1) : 1), CV_32F);
    std::memcpy(mat.data, values.data_ptr<float>(), size_t(values.numel()) * sizeof(float));
    return mat;
}

at::Tensor Features(torch::jit::Module& module, const cv::Mat& image) {
    auto taps = module.forward({ ToInput(image) }).toList();
    std::vector<at::Tensor> levels;
    int64_t height = 0, width = 0;
    for (size_t i = 0; i < taps.size(); ++i) {
        const at::Tensor tap = taps.get(i).toTensor();
        if (i == 0) { height = tap.size(2); width = tap.size(3); }
        levels.push_back(tap.size(2) == height && tap.size(3) == width ? tap
            : torch::nn::functional::interpolate(tap, torch::nn::functional::InterpolateFuncOptions()
                .size(std::vector<int64_t>{ height, width }).mode(torch::enumtype::kBilinear{}).align_corners(false)));
    }
    return torch::cat(levels, 1);
}

// Weighted cross entropy over the sampled rows of one frame. Returns the weighted loss sum and the weight sum so
// that epochs can be pooled exactly like SegV2Core's HeadPass (sum(w * ce) / sum(w)).
std::pair<at::Tensor, double> FrameLoss(torch::jit::Module& module, const TorchHead& head, const Options& options, const Frame& frame,
                                         std::mt19937& random, const at::Tensor& classWeight) {
    int gridWidth = 0, gridHeight = 0; std::vector<int64_t> index, labels;
    SelectRows(frame, options.classes, options.backgroundCap, options.foregroundCap, random, gridWidth, gridHeight, index, labels);
    if (index.empty()) return { at::Tensor(), 0. };
    const at::Tensor features = Features(module, frame.image);
    if (features.size(1) != Channels || features.size(2) != gridHeight || features.size(3) != gridWidth)
        throw std::runtime_error("Fine-tune feature shape mismatch");
    const at::Tensor idx = torch::tensor(index, torch::kInt64);
    const at::Tensor rows = features.reshape({ Channels, int64_t(gridHeight) * gridWidth }).index_select(1, idx).transpose(0, 1);
    const at::Tensor target = torch::tensor(labels, torch::kInt64);
    const at::Tensor logits = head.Logits(rows);
    const at::Tensor rowWeight = classWeight.index_select(0, target);
    const at::Tensor perRow = torch::nn::functional::cross_entropy(logits, target,
        torch::nn::functional::CrossEntropyFuncOptions().reduction(torch::enumtype::kNone{}));
    return { (perRow * rowWeight).sum(), rowWeight.sum().item<double>() };
}

}

bool Run(const Options& options, const Head& start, Result& result, std::string& error) {
    try {
        if (options.epochs < 1 || options.trainCount == 0 || !options.trainFrame) { error = "invalid fine-tune options"; return false; }
        if (options.threads < 1 || options.threads > 64) { error = "invalid fine-tune thread count"; return false; }
        at::set_num_threads(options.threads);
        auto module = torch::jit::load(options.modulePath);
        module.train();
        std::vector<at::Tensor> backbone; std::vector<std::string> names;
        for (const auto& item : module.named_parameters()) { item.value.set_requires_grad(true); backbone.push_back(item.value); names.push_back(item.name); }
        if (backbone.empty()) { error = "backbone has no trainable parameters"; return false; }
        TorchHead head;
        head.hiddenWeights = ToTensor(start.hiddenWeights).set_requires_grad(true);
        head.hiddenBias = ToTensor(start.hiddenBias).reshape({ Hidden }).set_requires_grad(true);
        head.weights = ToTensor(start.weights).set_requires_grad(true);
        const int classes = options.classes;
        if (classes < 2 || classes > MaxClasses) { error = "invalid fine-tune class count"; return false; }
        head.bias = ToTensor(start.bias).reshape({ classes }).set_requires_grad(true);
        head.mean = ToTensor(start.mean).reshape({ 1, Channels });
        head.sd = ToTensor(start.sd).reshape({ 1, Channels });
        if (head.hiddenWeights.size(0) != Channels || head.hiddenWeights.size(1) != Hidden || head.weights.size(0) != Hidden || head.weights.size(1) != classes) {
            error = "unexpected head shape"; return false;
        }
        at::Tensor classWeight = torch::ones({ classes });
        if (options.useClassWeights) classWeight = torch::tensor(std::vector<float>(options.classWeights, options.classWeights + classes));

        std::vector<torch::optim::OptimizerParamGroup> groups;
        groups.emplace_back(backbone, std::make_unique<torch::optim::AdamOptions>(options.backboneLr));
        groups.emplace_back(head.Trainable(), std::make_unique<torch::optim::AdamOptions>(options.headLr));
        torch::optim::Adam optimizer(groups, torch::optim::AdamOptions(options.headLr));
        const double baseRates[2] = { options.backboneLr, options.headLr };

        std::ofstream log;
        if (!options.logPath.empty()) { log.open(options.logPath); log << "epoch,time,train/cls_loss,val/cls_loss,lr_backbone" << std::endl; }
        const auto begin = std::chrono::steady_clock::now();
        std::mt19937 order(12345u);
        const bool hasValidation = options.validationCount > 0 && options.validationFrame;
        double best = std::numeric_limits<double>::infinity();
        auto snapshot = [&]() {
            result.backbone.clear();
            for (size_t i = 0; i < backbone.size(); ++i) {
                const at::Tensor values = backbone[i].detach().contiguous().to(torch::kFloat32);
                result.backbone.emplace_back(names[i], std::vector<float>(values.data_ptr<float>(), values.data_ptr<float>() + values.numel()));
            }
            result.head.hiddenWeights = ToMat(head.hiddenWeights); result.head.hiddenBias = ToMat(head.hiddenBias).reshape(1, 1).clone();
            result.head.weights = ToMat(head.weights); result.head.bias = ToMat(head.bias).reshape(1, 1).clone();
            result.head.mean = start.mean.clone(); result.head.sd = start.sd.clone();
        };
        for (int epoch = 1; epoch <= options.epochs; ++epoch) {
            const double scale = .1 + .9 * (1. + std::cos(epoch * 3.14159265358979323846 / options.epochs)) / 2.;
            for (size_t g = 0; g < 2; ++g) static_cast<torch::optim::AdamOptions&>(optimizer.param_groups()[g].options()).lr(baseRates[g] * scale);
            std::vector<size_t> sequence(options.trainCount);
            for (size_t i = 0; i < sequence.size(); ++i) sequence[i] = i;
            std::shuffle(sequence.begin(), sequence.end(), order);
            double lossSum = 0, weightSum = 0;
            for (size_t item : sequence) {
                if (options.cancel && *options.cancel) { error = "Training cancelled"; return false; }
                std::seed_seq seed{ 101u, unsigned(item), unsigned(epoch) }; std::mt19937 random(seed);
                const Frame frame = options.trainFrame(item, epoch, random);
                auto step = FrameLoss(module, head, options, frame, random, classWeight);
                if (!step.first.defined() || step.second <= 0) continue;
                at::Tensor loss = step.first / step.second;
                if (options.regularization > 0.f)
                    loss = loss + .5 * options.regularization * ((head.hiddenWeights * head.hiddenWeights).sum() + (head.weights * head.weights).sum());
                optimizer.zero_grad();
                loss.backward();
                optimizer.step();
                lossSum += step.first.item<double>(); weightSum += step.second;
            }
            if (weightSum <= 0 || !std::isfinite(lossSum)) { error = "fine-tune produced a non-finite loss"; return false; }
            const double trainLoss = lossSum / weightSum;
            double validationLoss = trainLoss;
            if (hasValidation) {
                torch::NoGradGuard noGrad;
                double sum = 0, weights = 0;
                for (size_t item = 0; item < options.validationCount; ++item) {
                    std::seed_seq seed{ 29u, unsigned(item) }; std::mt19937 random(seed);
                    const Frame frame = options.validationFrame(item);
                    auto step = FrameLoss(module, head, options, frame, random, classWeight);
                    if (step.first.defined() && step.second > 0) { sum += step.first.item<double>(); weights += step.second; }
                }
                if (weights > 0) validationLoss = sum / weights;
            }
            if (!std::isfinite(validationLoss)) { error = "fine-tune produced a non-finite validation loss"; return false; }
            const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - begin).count();
            if (log.is_open()) log << epoch << ',' << seconds << ',' << trainLoss << ',' << validationLoss << ',' << options.backboneLr * scale << std::endl;
            if (!hasValidation || validationLoss < best) {
                best = validationLoss; result.bestEpoch = epoch; result.bestLoss = validationLoss;
                snapshot();
            }
            if (options.progress) options.progress(epoch * 100 / options.epochs);
        }
        if (options.parityFrame) {
            // Restore the best snapshot into the module and compute its features, for the OpenVINO parity check.
            torch::NoGradGuard noGrad;
            size_t i = 0;
            for (const auto& item : module.named_parameters()) {
                const auto& saved = result.backbone[i++].second;
                item.value.copy_(torch::from_blob(const_cast<float*>(saved.data()), item.value.sizes(), torch::kFloat32));
            }
            module.eval();
            const Frame frame = options.parityFrame();
            const at::Tensor features = Features(module, frame.image);
            result.parityRows = ToMat(features.reshape({ Channels, features.size(2) * features.size(3) }).transpose(0, 1).contiguous());
        }
        return true;
    } catch (const std::exception& problem) { error = problem.what(); return false; }
    catch (...) { error = "unknown torch error"; return false; }
}

}
#else
namespace SegFineTune {
bool Compiled() { return false; }
bool Probe(const std::string&, int, int, std::string& report) { report = "libtorch support is not compiled into this build"; return false; }
bool GradientProbe(const std::string&, int, int, const std::string&, std::string& report) { report = "libtorch support is not compiled into this build"; return false; }
bool Run(const Options&, const Head&, Result&, std::string& error) { error = "libtorch support is not compiled into this build"; return false; }
}
#endif
