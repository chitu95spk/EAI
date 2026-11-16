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
#include <cctype>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <limits>
#include <sstream>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mutex>

namespace SegFineTune {

// torch_cpu.dll is delay-loaded, and the delay-load helper calls LoadLibrary("torch_cpu.dll") with the legacy search order
// (exe folder, System32, current dir, PATH). A host that loaded this DLL by full path (AddDllDirectory / explicit path) does
// not have the DLL's own folder on that list, so the first torch call failed with 0xC06D007E (ERROR_MOD_NOT_FOUND). Loading
// torch_cpu.dll by full path from this DLL's folder first makes the delay-load find it by module name. Must run before
// the first torch symbol is touched. When the file is not next to this DLL the default search is left untouched.
static bool EnsureTorchRuntimeLoaded(std::string& error) {
    static std::mutex lock;
    static int state = 0;               // 0 = not tried, 1 = loaded / nothing to do, -1 = failed
    static std::string cachedError;
    std::lock_guard<std::mutex> guard(lock);
    if (state == 0) {
        state = 1;
        if (!GetModuleHandleW(L"torch_cpu.dll")) {
            HMODULE self = nullptr;
            if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                   reinterpret_cast<LPCWSTR>(&EnsureTorchRuntimeLoaded), &self)) {
                std::wstring path(32768, L'\0');
                const DWORD length = GetModuleFileNameW(self, &path[0], static_cast<DWORD>(path.size()));
                if (length > 0 && length < path.size()) {
                    path.resize(length);
                    const size_t slash = path.find_last_of(L"\\/");
                    if (slash != std::wstring::npos) {
                        const std::wstring full = path.substr(0, slash + 1) + L"torch_cpu.dll";
                        if (GetFileAttributesW(full.c_str()) != INVALID_FILE_ATTRIBUTES) {
                            if (!LoadLibraryExW(full.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH)) {
                                state = -1;
                                cachedError = "cannot load torch_cpu.dll from the BeeNativeSegAI.dll folder (Win32 error " + std::to_string(GetLastError()) +
                                              "); a dependency of torch_cpu.dll is missing";
                            }
                        }
                    }
                }
            }
        }
    }
    if (state < 0) error = cachedError;
    return state >= 0;
}

// BEESEGAI_TRAIN_DEVICE = cpu | cuda | auto (default auto). With a CUDA libtorch build (torch_cuda.dll + c10_cuda.dll next to this
// DLL) the CUDA runtime is loaded by full path (same 0xC06D007E class of problem as torch_cpu.dll: torch_cuda.dll and its
// cuda/cudnn dependencies must be found in this DLL's folder). Nothing is linked against torch_cuda, so a CPU libtorch still works
// (the files are just absent -> CPU). Returns true when torch_cuda.dll is loaded; otherwise `why` says why CUDA is not used.
static std::string TrainDeviceMode() {
    char buffer[32] = {};
    const DWORD n = GetEnvironmentVariableA("BEESEGAI_TRAIN_DEVICE", buffer, sizeof(buffer));
    std::string mode = (n > 0 && n < sizeof(buffer)) ? std::string(buffer) : std::string("auto");
    for (auto& c : mode) c = char(std::tolower((unsigned char)c));
    return mode;
}

static bool EnsureTorchCudaLoaded(std::string& why) {
    static std::mutex lock;
    static int state = 0;               // 0 = not tried, 1 = loaded, -1 = not available
    static std::string cachedWhy;
    std::lock_guard<std::mutex> guard(lock);
    if (state == 0) {
        state = -1;
        if (GetModuleHandleW(L"torch_cuda.dll")) { state = 1; }
        else {
            HMODULE self = nullptr;
            if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                   reinterpret_cast<LPCWSTR>(&EnsureTorchCudaLoaded), &self)) {
                std::wstring path(32768, L'\0');
                const DWORD length = GetModuleFileNameW(self, &path[0], static_cast<DWORD>(path.size()));
                if (length > 0 && length < path.size()) {
                    path.resize(length);
                    const size_t slash = path.find_last_of(L"\\/");
                    const std::wstring folder = slash == std::wstring::npos ? std::wstring() : path.substr(0, slash + 1);
                    const std::wstring cuda = folder + L"torch_cuda.dll";
                    if (GetFileAttributesW(cuda.c_str()) == INVALID_FILE_ATTRIBUTES) cachedWhy = "torch_cuda.dll not found next to BeeNativeSegAI.dll (CPU libtorch)";
                    else {
                        // Dependencies (c10_cuda, cudart, cublas, cudnn, nvrtc...) are resolved from the folder of torch_cuda.dll.
                        HMODULE c10cuda = LoadLibraryExW((folder + L"c10_cuda.dll").c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
                        const DWORD e1 = c10cuda ? 0 : GetLastError();
                        // cuDNN 9 loads its sub-libraries (cudnn_graph64_9, cudnn_ops64_9, engines...) by bare name at run time with the
                        // default search order, which does not include this folder when the host is python.exe / a C# app in another
                        // folder -> fatal "Invalid handle. Cannot load symbol cudnnGetVersion". Pin them by full path first: a later
                        // LoadLibrary("cudnn_graph64_9.dll") then returns the already loaded module.
                        if (c10cuda) {
                            static const wchar_t* const cudnnParts[] = {
                                L"cudnn_graph64_9.dll", L"cudnn_ops64_9.dll", L"cudnn_cnn64_9.dll", L"cudnn_adv64_9.dll",
                                L"cudnn_heuristic64_9.dll", L"cudnn_engines_precompiled64_9.dll", L"cudnn_engines_runtime_compiled64_9.dll",
                                L"cudnn_engines_tensor_ir64_9.dll", L"cudnn_ext64_9.dll", L"cudnn64_9.dll" };
                            for (const wchar_t* part : cudnnParts) {
                                const std::wstring full = folder + part;
                                if (GetFileAttributesW(full.c_str()) != INVALID_FILE_ATTRIBUTES)
                                    LoadLibraryExW(full.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
                            }
                        }
                        HMODULE tc = c10cuda ? LoadLibraryExW(cuda.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH) : nullptr;
                        if (tc) state = 1;
                        else cachedWhy = c10cuda ? "cannot load torch_cuda.dll (Win32 error " + std::to_string(GetLastError()) + ")"
                                                 : "cannot load c10_cuda.dll (Win32 error " + std::to_string(e1) + ")";
                    }
                }
            }
            if (state < 0 && cachedWhy.empty()) cachedWhy = "cannot locate BeeNativeSegAI.dll folder";
        }
    }
    if (state < 0) why = cachedWhy;
    return state > 0;
}

// Chooses the training device. Never throws: any CUDA problem -> CPU with the reason in `note`.
static at::Device ChooseTrainDevice(std::string& note) {
    const std::string mode = TrainDeviceMode();
    if (mode == "cpu") { note = "device=cpu (forced by BEESEGAI_TRAIN_DEVICE=cpu)"; return at::Device(at::kCPU); }
    std::string why;
    try {
        if (EnsureTorchCudaLoaded(why) && torch::cuda::is_available() && torch::cuda::device_count() > 0) {
            note = "device=cuda:0";
            return at::Device(at::kCUDA, 0);
        }
        if (why.empty()) why = "torch::cuda::is_available() is false";
    } catch (const std::exception& e) { why = e.what(); }
    catch (...) { why = "unknown error probing CUDA"; }
    note = "device=cpu (" + why + ")";
    return at::Device(at::kCPU);
}

bool Compiled() { return true; }

bool Probe(const std::string& path, int threads, int side, std::string& report) {
    try {
        if (!EnsureTorchRuntimeLoaded(report)) return false;
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
        if (!EnsureTorchRuntimeLoaded(report)) return false;
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
    const at::Tensor values = tensor.detach().to(at::kCPU).contiguous().to(torch::kFloat32);
    cv::Mat mat(int(values.size(0)), int(values.dim() > 1 ? values.size(1) : 1), CV_32F);
    std::memcpy(mat.data, values.data_ptr<float>(), size_t(values.numel()) * sizeof(float));
    return mat;
}

at::Tensor Features(torch::jit::Module& module, const cv::Mat& image, const at::Device& device) {
    auto taps = module.forward({ ToInput(image).to(device) }).toList();
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
                                         std::mt19937& random, const at::Tensor& classWeight, const at::Device& device) {
    int gridWidth = 0, gridHeight = 0; std::vector<int64_t> index, labels;
    SelectRows(frame, options.classes, options.backgroundCap, options.foregroundCap, random, gridWidth, gridHeight, index, labels);
    if (index.empty()) return { at::Tensor(), 0. };
    const at::Tensor features = Features(module, frame.image, device);
    if (features.size(1) != Channels || features.size(2) != gridHeight || features.size(3) != gridWidth)
        throw std::runtime_error("Fine-tune feature shape mismatch");
    const at::Tensor idx = torch::tensor(index, torch::kInt64).to(device);
    const at::Tensor rows = features.reshape({ Channels, int64_t(gridHeight) * gridWidth }).index_select(1, idx).transpose(0, 1);
    const at::Tensor target = torch::tensor(labels, torch::kInt64).to(device);
    const at::Tensor logits = head.Logits(rows);
    const at::Tensor rowWeight = classWeight.index_select(0, target);
    const at::Tensor perRow = torch::nn::functional::cross_entropy(logits, target,
        torch::nn::functional::CrossEntropyFuncOptions().reduction(torch::enumtype::kNone{}));
    return { (perRow * rowWeight).sum(), rowWeight.sum().item<double>() };
}

}

// `thrown` is set when the failure was a torch/C++ exception (as opposed to bad options, cancel, non-finite loss), so the caller
// can retry on CPU.
static bool RunOn(const at::Device& device, const Options& options, const Head& start, Result& result, std::string& error, bool& thrown) {
    thrown = false;
    try {
        if (options.epochs < 1 || options.trainCount == 0 || !options.trainFrame) { error = "invalid fine-tune options"; return false; }
        if (options.threads < 1 || options.threads > 64) { error = "invalid fine-tune thread count"; return false; }
        at::set_num_threads(options.threads);
        auto module = torch::jit::load(options.modulePath);
        // Move the backbone BEFORE the parameters are flagged requires_grad, so they stay leaf tensors for the optimizer.
        if (device.is_cuda()) module.to(device);
        module.train();
        std::vector<at::Tensor> backbone; std::vector<std::string> names;
        for (const auto& item : module.named_parameters()) { item.value.set_requires_grad(true); backbone.push_back(item.value); names.push_back(item.name); }
        if (backbone.empty()) { error = "backbone has no trainable parameters"; return false; }
        TorchHead head;
        head.hiddenWeights = ToTensor(start.hiddenWeights).to(device).set_requires_grad(true);
        head.hiddenBias = ToTensor(start.hiddenBias).reshape({ Hidden }).to(device).set_requires_grad(true);
        head.weights = ToTensor(start.weights).to(device).set_requires_grad(true);
        const int classes = options.classes;
        if (classes < 2 || classes > MaxClasses) { error = "invalid fine-tune class count"; return false; }
        head.bias = ToTensor(start.bias).reshape({ classes }).to(device).set_requires_grad(true);
        head.mean = ToTensor(start.mean).reshape({ 1, Channels }).to(device);
        head.sd = ToTensor(start.sd).reshape({ 1, Channels }).to(device);
        if (head.hiddenWeights.size(0) != Channels || head.hiddenWeights.size(1) != Hidden || head.weights.size(0) != Hidden || head.weights.size(1) != classes) {
            error = "unexpected head shape"; return false;
        }
        at::Tensor classWeight = torch::ones({ classes });
        if (options.useClassWeights) classWeight = torch::tensor(std::vector<float>(options.classWeights, options.classWeights + classes));
        classWeight = classWeight.to(device);

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
                const at::Tensor values = backbone[i].detach().to(at::kCPU).contiguous().to(torch::kFloat32);
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
                auto step = FrameLoss(module, head, options, frame, random, classWeight, device);
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
                    auto step = FrameLoss(module, head, options, frame, random, classWeight, device);
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
            const at::Tensor features = Features(module, frame.image, device);
            result.parityRows = ToMat(features.reshape({ Channels, features.size(2) * features.size(3) }).transpose(0, 1).contiguous());
        }
        return true;
    } catch (const std::exception& problem) { error = problem.what(); thrown = true; return false; }
    catch (...) { error = "unknown torch error"; thrown = true; return false; }
}

bool Run(const Options& options, const Head& start, Result& result, std::string& error) {
    try {
        if (!EnsureTorchRuntimeLoaded(error)) return false;
        std::string note;
        const at::Device device = ChooseTrainDevice(note);
        bool thrown = false;
        const auto begin = std::chrono::steady_clock::now();
        bool ok = RunOn(device, options, start, result, error, thrown);
        if (!ok && thrown && device.is_cuda()) {
            // CUDA failed at run time (out of memory, unsupported arch, kernel error...): redo the whole run on CPU.
            note = "device=cpu (CUDA run failed, fell back: " + error + ")";
            result = Result();
            error.clear();
            ok = RunOn(at::Device(at::kCPU), options, start, result, error, thrown);
        }
        const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - begin).count();
        result.device = note;
        std::fprintf(stderr, "[SegFineTune] %s train_seconds=%.2f ok=%d\n", note.c_str(), seconds, ok ? 1 : 0);
        return ok;
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
