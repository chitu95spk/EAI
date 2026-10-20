#pragma once

#include "SegClassTable.h"
#include "SegFeatureCore.h"

#include <opencv2/ml.hpp>

#include <cstddef>
#include <cstdint>
#include <string>

namespace BeeSegAI {
class SegStructureModel;
bool SaveStructuralSegai(const std::wstring& path, const SegStructureModel& model, float threshold, uint32_t minArea);
bool LoadStructuralSegai(const std::wstring& path, SegStructureModel& model, uint32_t& minArea);

#pragma pack(push, 1)
struct SegAIHeader {
    char magic[8];
    uint32_t version;
    uint32_t featureFlags;
    uint32_t numClasses;
    uint32_t featureCount;
    float defectThreshold;
    uint32_t minDefectArea;
    uint32_t lbpRadius;
    uint32_t hsvWindow;
    uint32_t gaborSize;
    float gaborSigma;
    float gaborLambda;
    uint32_t reservedA;
    uint64_t payloadSize;
    uint32_t crc32;
    uint32_t reservedB;
};
#pragma pack(pop)

static_assert(sizeof(SegAIHeader) == 72, "SegAIHeader must stay binary-compatible.");
// EAI-CLS S1: version 4 stores the class-table byte size in reservedA; keep these offsets frozen.
static_assert(offsetof(SegAIHeader, reservedA) == 52, "SegAIHeader::reservedA must stay at offset 52.");
static_assert(offsetof(SegAIHeader, payloadSize) == 56, "SegAIHeader::payloadSize must stay at offset 56.");
static_assert(offsetof(SegAIHeader, crc32) == 64, "SegAIHeader::crc32 must stay at offset 64.");

// Highest .segai header version this runtime reads (3 = V2 head, 4 = V2 head plus class table).
constexpr uint32_t kMaxSupportedSegaiVersion = 4;

bool SaveSegai(const std::wstring& path,
               const cv::Ptr<cv::ml::RTrees>& model,
               const FeatureConfig& cfg,
               float threshold,
               uint32_t minArea);

bool LoadSegai(const std::wstring& path,
               cv::Ptr<cv::ml::RTrees>& outModel,
               FeatureConfig& outCfg,
               float& outThreshold,
               uint32_t& outMinArea);

} // namespace BeeSegAI
