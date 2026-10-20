#pragma once
#include <opencv2/core.hpp>
#include <functional>
#include <random>
#include <string>
#include <utility>
#include <vector>

// SEG-LIFECYCLE-r11: optional libtorch-backed backbone fine-tune for Segment AI V2.
// Everything here is compiled only when BEESEGAI_TORCH=1 (Release|x64 with ThirdParty\libtorch present); otherwise
// the functions report "not available". torch_cpu.dll is delay-loaded, so a machine that only runs inference
// never loads it. The torch headers stay inside SegBackboneFineTune.cpp.
namespace SegFineTune {

// True when this build contains libtorch support.
bool Compiled();

// Loads the TorchScript backbone (outputs: taps Add_32, Add_72, Conv_94), applies the thread count and runs one
// forward at side x side. Returns true on success and fills `report`; otherwise `report` holds the error text.
bool Probe(const std::string& torchScriptUtf8Path, int threads, int side, std::string& report);

// P1b gradient check: deterministic input x[i] = 0.5 + 0.5*sin(0.001*i), loss = sum over the three taps of mean(tap^2),
// backward, then writes every parameter gradient (float32, named_parameters order) to gradPath and the names with element
// counts to gradPath + ".names.txt". `report` gets loss, parameter count and gradient L2 norm.
bool GradientProbe(const std::string& torchScriptUtf8Path, int threads, int side, const std::string& gradUtf8Path, std::string& report);

// P1c joint fine-tune (stage 2). The head layout matches SegV2Core: hiddenWeights Channels x Hidden, weights Hidden x Classes,
// mean/sd 1 x Channels, all CV_32F.
struct Head { cv::Mat weights, bias, hiddenWeights, hiddenBias, mean, sd; };
struct Frame { cv::Mat image, labels; int left = 0, top = 0, width = 0, height = 0; };  // image: letterboxed BGR8, labels: CV_8U (255 = ignore)
struct Options {
    std::string modulePath;          // TorchScript backbone returning the three taps
    int epochs = 0, threads = 2, side = 640;
    float backboneLr = 1e-4f, headLr = 1e-3f, regularization = 0.f;
    // classes = K = N + 1 head outputs (3 for the NG/OK layout, at most 33); classWeights[c] is used for c < classes.
    int classes = 3;
    bool useClassWeights = false; float classWeights[33] = { 1.f, 1.f, 1.f };
    int backgroundCap = 2000, foregroundCap = 450;   // per-image row caps, same as SegV2Core::Gather
    size_t trainCount = 0, validationCount = 0;
    std::function<Frame(size_t, int, std::mt19937&)> trainFrame;  // (train index, epoch, rng) -> frame (augmented when wanted)
    std::function<Frame(size_t)> validationFrame;
    std::function<Frame()> parityFrame;               // optional: frame whose final features are returned in Result::parityRows
    std::function<void(int)> progress;                // 0..100
    const volatile int* cancel = nullptr;
    std::string logPath;                              // epoch csv
};
struct Result {
    Head head;                                        // best (lowest validation loss, else last) head
    std::vector<std::pair<std::string, std::vector<float>>> backbone;  // parameter name -> values (same names as the OpenVINO constants)
    int bestEpoch = 0; double bestLoss = 0;
    cv::Mat parityRows;                               // (grid*grid) x Channels features of the best weights (eval forward), same layout as SegV2Core::FeatureRows
};
// Returns true on success; otherwise `error` is filled. Throws nothing.
bool Run(const Options& options, const Head& start, Result& result, std::string& error);

}
