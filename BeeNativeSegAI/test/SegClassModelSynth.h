#pragma once

// EAI-CLS S2a test helper (test project only, never part of the DLL).
// Builds a version 4 .segai with K = N + 1 output channels WITHOUT training: it takes a trained 3-channel V2 model,
// rewrites only the final 1x1 convolution + bias of its OpenVINO graph so that new channel k copies source channel
// sourceChannel (0 = background, 1 = NG, 2 = OK) plus an optional bias shift, and stores the supplied class table
// after the payload. Everything before that convolution (backbone, hidden layer) is left byte for byte as trained.

#include <filesystem>
#include <string>
#include <vector>

struct SynthChannel {
    int sourceChannel = 0;   // 0 background, 1 NG, 2 OK of the source model
    float biasShift = 0.f;   // added to the copied bias (a large negative value makes the class practically never win)
};

// channels[0] is the background channel, channels[k] the channel of class id k. tableBytes is an encoded class table with
// channels.size() - 1 classes. postprocessVersion is stored in the payload (1 or 2). Returns false with a reason in error.
bool SynthesizeClassModel(const std::filesystem::path& sourcePath, const std::filesystem::path& destinationPath,
                          const std::vector<SynthChannel>& channels, const std::vector<unsigned char>& tableBytes,
                          int postprocessVersion, std::string& error);
