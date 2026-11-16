#pragma once

#include <stdint.h>

#ifdef BEESEGAI_EXPORTS
#define SAPI __declspec(dllexport)
#elif defined(_WIN32)
#define SAPI __declspec(dllimport)
#else
#define SAPI
#endif

extern "C"
{
    // Additive UTF-8 diagnostics. Buffers are caller-owned; existing ABI stays unchanged.
    SAPI int32_t SEGAI_TrainerGetLastError(void* handle, char* buffer, int32_t capacity);
    SAPI int32_t SEGAI_InferGetLastError(void* handle, char* buffer, int32_t capacity);
    SAPI int32_t SEGAI_InferHasNormalClass(void* handle);
    SAPI int32_t SEGAI_InferHasNgClass(void* handle);
    SAPI void* SEGAI_TrainerCreate();
    SAPI void SEGAI_TrainerDestroy(void* handle);
    SAPI int32_t SEGAI_TrainerSetProfile(void* handle, int32_t profile, int32_t detail);
    SAPI int32_t SEGAI_TrainerSetROI(void* handle, int32_t x, int32_t y, int32_t w, int32_t h);
    SAPI int32_t SEGAI_TrainerAddSample(void* handle,
                                        const uint8_t* bgr,
                                        int32_t w,
                                        int32_t h,
                                        int32_t step,
                                        const uint8_t* mask,
                                        int32_t maskStep);
    SAPI void SEGAI_TrainerClearSamples(void* handle);
    SAPI int32_t SEGAI_TrainerTrain(void* handle,
                                    int32_t numTrees,
                                    int32_t maxDepth,
                                    int32_t minSampleCount,
                                    void(*progressCb)(int32_t, void*),
                                    void* userData,
                                    const volatile int32_t* cancelFlag);
    SAPI int32_t SEGAI_TrainerSave(void* handle,
                                   const wchar_t* path,
                                   float defectThreshold,
                                   uint32_t minDefectArea);
    SAPI int32_t SEGAI_TrainerSampleCount(void* handle, int32_t* outDefectPixels, int32_t* outNormalPixels);

    SAPI void* SEGAI_InferCreate();
    SAPI void SEGAI_InferDestroy(void* handle);
    SAPI int32_t SEGAI_InferLoad(void* handle, const wchar_t* path);
    SAPI int32_t SEGAI_InferSetGpu(void* handle, int32_t enable);
    SAPI int32_t SEGAI_InferGetGpuAvailable();
    // NVIDIA CUDA backend (ONNX Runtime). backend: 0 = Intel OpenVINO (default), 1 = NVIDIA CUDA; applies when SEGAI_InferSetGpu(1).
    SAPI int32_t SEGAI_InferSetGpuBackend(void* handle, int32_t backend);
    // 1 when a real CUDA session can be created here (ORT + CUDA runtime beside BeeNativeSegAI.dll + an NVIDIA GPU), else 0.
    SAPI int32_t SEGAI_InferGetCudaAvailable();
    // UTF-8 note: handle != NULL -> why the last Predict fell back to CPU (empty if it did not); handle == NULL -> why CUDA is unavailable.
    // Returns the byte length (without NUL), or -1 on a bad buffer.
    SAPI int32_t SEGAI_InferGetBackendNote(void* handle, char* buffer, int32_t capacity);
    SAPI int32_t SEGAI_InferGetLastBackend(void* handle);
    SAPI int32_t SEGAI_InferSetAssistMode(void* handle, int32_t enable);
    SAPI int32_t SEGAI_InferPredict(void* handle,
                                    const uint8_t* bgr,
                                    int32_t w,
                                    int32_t h,
                                    int32_t step,
                                    int32_t roiX,
                                    int32_t roiY,
                                    int32_t roiW,
                                    int32_t roiH,
                                    float threshold,
                                    uint8_t** outMaskPtr,
                                    int32_t* outMaskW,
                                    int32_t* outMaskH,
                                    float* outScore);

    SAPI int32_t SEGAI_InferGetConfidence(void* handle, uint8_t** outPtr, int32_t* outW, int32_t* outH);
    SAPI void SEGAI_FreeBuffer(void* ptr);

    SAPI int32_t SEGAI_InferSetAngles(void* handle, float step, float range);
    SAPI int32_t SEGAI_GetVersion();
    SAPI const char* SEGAI_GetBuildInfo();
    SAPI int32_t SEGAI_RunFeatureSelfTest();
SAPI int32_t SEGAI_InferSetSpeedMode(void* handle, int32_t mode);
// V2 HighSpeed (additive): Check scale in [0.25, 1] applied before V2 inference; 1 = off.
SAPI int32_t SEGAI_InferSetV2SpeedScale(void* handle, float scale);
SAPI int32_t SEGAI_InferGetNormalConfidence(void* handle, uint8_t** outPtr, int32_t* outW, int32_t* outH);
SAPI int SEGAI_TrainerConfigureV2(void* handle, const wchar_t* path);
SAPI int SEGAI_InferV2Options(void* handle, float foreground, float ratio, int morphology);
SAPI int SEGAI_InferSetObjectScale(void* handle, float minimum, float maximum, int multiScale);
// V159 additive: 0 preserves legacy sum foreground; 1 uses winner-class foreground for V2 compact single-scale only.
SAPI int32_t SEGAI_InferSetV2BoundaryMode(void* handle, int32_t mode);
// V2 compact per-component shape cleanup fraction in [0, 0.25] (closing then opening of the final NG/OK maps).
// additive; 0 = off; caller serializes per handle. Returns 0, -1 null handle, -2 invalid argument.
SAPI int32_t SEGAI_InferSetV2ShapeCleanup(void* handle, float fraction);
// Applies the same cleanup in place to caller-owned CV_8U maps (ok may be null).
// offline verification helper; stateless. Returns the changed-component count, -2 invalid argument, -3 native error.
SAPI int32_t SEGAI_V2ShapeCleanupApply(uint8_t* ng, uint8_t* ok, int32_t w, int32_t h, int32_t step, float fraction);
// V135 diagnostic (additive): last-call phase timings in ms as a double array (13 values, layout
// documented at the definition). Returns the number of values written or -1.
SAPI int32_t SEGAI_InferGetPhaseTimings(void* handle, double* outValues, int32_t capacity);
// EASYAI_SEGAI_SPLIT_TIMING diagnostic (additive; SEGAI_GetVersion stays 1). Split of the last V2 Predict:
// [0] backbone_ms, [1] head_ms derived = max(0, v2 infer - backbone), [2] infer_ms (full graph). Returns 3 when measured,
// 0 when the switch is off or the last call was not split-measured, -1 invalid arguments.
SAPI int32_t SEGAI_InferGetSplitTimings(void* handle, double* outValues, int32_t capacity);
// Diagnostic only (needs the environment variable EASYAI_SEGAI_INSTANCE_TENSOR set before the call being captured): copies the raw
// instance-head output of the last SEGAI_InferPredictInstances ({channels, grid, grid} floats, heat channels still logits) so
// OpenVINO CPU and CUDA can be compared per value. Returns the float count (0 = nothing captured), -1 invalid arguments,
// -2 when capacity is too small (outChannels/outGrid are still set).
SAPI int32_t SEGAI_InferGetLastInstanceTensor(void* handle, float* outValues, int32_t capacity, int32_t* outChannels, int32_t* outGrid);
// Split timing API revision (1); a DLL without this export is probed as 0 by the managed side.
SAPI int32_t SEGAI_GetSplitTimingApiVersion();
// V158 offline-only V2 compact/single-scale capture. callerProvenanceUtf8 is caller
// supplied and marked unverified; hostObservedFactsUtf8 is host-observed metadata.
// Each must be valid terminated UTF-8 within 8192 bytes; together they are limited
// to 12288 bytes before native string/JSON construction.
// The returned UTF-8 JSON is allocated with new[] and released with SEGAI_FreeBuffer.
// Calls on one handle are serialized by the caller; production SEGAI_InferPredict
// never enables capture. outByteCount excludes the terminating NUL.
SAPI int32_t SEGAI_InferPredictDiagnosticV1(void* handle,
                                            const uint8_t* bgr,
                                            int32_t w,
                                            int32_t h,
                                            int32_t step,
                                            int32_t roiX,
                                            int32_t roiY,
                                            int32_t roiW,
                                            int32_t roiH,
                                            float threshold,
                                            const char* callerProvenanceUtf8,
                                            const char* hostObservedFactsUtf8,
                                            uint8_t** outUtf8,
                                            uint64_t* outByteCount);
    // EAI-CLS S1 named classes (additive; SEGAI_GetVersion stays 1). Buffers are caller-owned; the table is the
    // binary class table described in SegClassTable.h (at most 2192 bytes).
    // Returns 1 for this class API revision; managed code probes for the export before using the functions below.
    SAPI int32_t SEGAI_GetClassApiVersion();
    // V2 trainer only: call after SEGAI_TrainerConfigureV2 and before the first AddSample. Returns 0, -1 null handle,
    // -2 wrong state (not a V2 trainer, or samples already added), -3 invalid table (reason in TrainerGetLastError).
    // The enabled flags of the supplied table are ignored: they are derived from the training data when saving.
    // A trainer that never receives a table keeps writing the version 3 file format.
    SAPI int32_t SEGAI_TrainerSetClassTable(void* handle, const uint8_t* table, int32_t bytes);
    // Copies the class table of the loaded model into buffer and returns its size in bytes. Nothing is written when
    // capacity is too small (so a null buffer with capacity 0 queries the size). Models without a stored table report
    // the synthesized legacy table (1 "NG" DEFECT, 2 "OK" GOOD). Returns -1 null handle, -3 stored table invalid, -4 no model loaded.
    SAPI int32_t SEGAI_InferGetClassTable(void* handle, uint8_t* buffer, int32_t capacity);
    // EAI-CLS S3 per-class prediction on a loaded V2 model; updates the same internal state as SEGAI_InferPredict.
    // outLabel (required) and outScore (nullable) are caller-owned w x h CV_8U planes (labelStep/scoreStep bytes per row)
    // filled for the whole image: zero outside the ROI and wherever the component score is not above the threshold cutoff.
    // label = class id (1..N, table order) of the owning component, score = that component's score (0..255).
    // outClassFraction[c-1] = accepted pixels of class c / ROI area for c = 1..N; classCapacity must be >= N (the class
    // count from SEGAI_InferGetClassTable), otherwise -4 and nothing is inferred. Legacy [DEFECT, GOOD] models map ng to
    // class 1 and ok to class 2 with NG precedence. Returns 0, the Predict error codes, -1 invalid argument or null handle,
    // -2 invalid image, -3 native error, -4 capacity too small, -9 no V2 model.
    SAPI int32_t SEGAI_InferPredictMulti(void* handle,
                                         const uint8_t* bgr,
                                         int32_t w,
                                         int32_t h,
                                         int32_t step,
                                         int32_t roiX,
                                         int32_t roiY,
                                         int32_t roiW,
                                         int32_t roiH,
                                         float threshold,
                                         uint8_t* outLabel,
                                         int32_t labelStep,
                                         uint8_t* outScore,
                                         int32_t scoreStep,
                                         float* outClassFraction,
                                         int32_t classCapacity);
    // ---- EAI-POS P3 per-instance head (additive; SEGAI_GetVersion stays 1, the .segai header stays version 3/4) ----
    // Returns 1 for this instance API revision; managed code probes for the export before using the functions below.
    SAPI int32_t SEGAI_GetInstanceApiVersion();
    // Trainer: settings keys (ConfigureV2 JSON) instanceHead = 0/1, angleSymmetry = one value 1|2|4 for every class or one value per
    // class (1 = full 360 degrees, 2 = 180, 4 = 90), optional instanceEpochs (default 60) and instanceLearningRate (default 0.002).
    // instanceHead=1 is rejected by ConfigureV2 (-8, reason in TrainerGetLastError) together with originalInput=1 (tiled / original
    // input), backboneEpochs>0 and Retrain (resumeFrom / trainSkip). The head has C = number of classes outputs (no background); a
    // trainer without a class table has the 2 classes NG (0) and OK (1).
    //
    // One labelled instance. 32 bytes, no padding. structSize = sizeof(SegInstanceV1) of the writer; a later revision appends fields and
    // enlarges structSize, this revision reads the first 32 bytes of every item. kind 0 = rotated box (w along TL->TR, h along TL->BL,
    // theta = atan2(TR - TL) in radians, image coordinates with y down, as LabelShape.Direction), kind 1 = circle (w = h = 2r, theta
    // ignored). Coordinates are in pixels of the image given to SEGAI_TrainerAddSample (full image, not the ROI crop). classIndex is
    // 0-based in the class table order.
    typedef struct SegInstanceV1 {
        int32_t structSize;
        int32_t kind;
        float cx;
        float cy;
        float w;
        float h;
        float theta;
        int32_t classIndex;
    } SegInstanceV1;
    // Attaches instance labels to the LAST successful SEGAI_TrainerAddSample (appends when called again for the same sample; a sample with
    // no call is a background-only sample). structSize is the stride in bytes between items (>= 32). Returns 0, -1 null handle or invalid
    // argument, -2 wrong state (not a V2 trainer, instanceHead not enabled, no sample added yet), -3 invalid item (non-finite value, size
    // <= 0, unknown kind, classIndex out of range; nothing is attached; reason in TrainerGetLastError).
    SAPI int32_t SEGAI_TrainerAddInstances(void* handle, const SegInstanceV1* items, int32_t count, int32_t structSize);
    // 1 when the loaded model carries an instance head, 0 when it does not (every legacy model), -1 null handle.
    SAPI int32_t SEGAI_InferHasInstanceHead(void* handle);
    // One detected instance. 40 bytes, no padding. Coordinates are pixels of the ORIGINAL input image (ROI offset applied). kind 1 =
    // circle with w = h = 2r. For a class with angle symmetry k > 1 theta is the representative in [-pi/k, pi/k]. refined is 0 in P3
    // (set to 1 by the P4 quality refinement when it succeeded).
    typedef struct SegInstanceResultV1 {
        int32_t structSize;
        int32_t kind;
        float cx;
        float cy;
        float w;
        float h;
        float theta;
        float score;
        int32_t classIndex;
        int32_t refined;
    } SegInstanceResultV1;
    // Runs the instance head on bgr (roi width/height <= 0 = whole image), threshold in [0, 1] on the centre score, quality 0 =
    // performance, 1 = quality (P4: each instance is refined, a failed refinement keeps the head result with refined = 0). Results are written to the caller-owned out[0..min(count, capacity)), sorted by
    // score descending; every written item has structSize = sizeof(SegInstanceResultV1). *outCount always receives the number of
    // instances found. Returns 0 ok, -1 null handle or invalid argument, -2 invalid image / threshold / quality / ROI, -3 native
    // error, -4 capacity too small (nothing is written, *outCount holds the required capacity), -8 inference failed, -9 no V2
    // model loaded, -11 the model has no instance head.
    SAPI int32_t SEGAI_InferPredictInstances(void* handle,
                                             const uint8_t* bgr,
                                             int32_t w,
                                             int32_t h,
                                             int32_t step,
                                             int32_t roiX,
                                             int32_t roiY,
                                             int32_t roiW,
                                             int32_t roiH,
                                             float threshold,
                                             int32_t quality,
                                             SegInstanceResultV1* out,
                                             int32_t capacity,
                                             int32_t* outCount);
    // SEG-LIFECYCLE-r11: libtorch backbone probe. Returns 0 ok, -1 not compiled in, -2 failed. report is UTF-8.
    SAPI int32_t SEGAI_TorchBackboneProbe(const wchar_t* torchScriptPath, int32_t threads, int32_t side, char* report, int32_t capacity);
    // P1b: forward+backward on a deterministic input; writes gradients to gradPath (+ ".names.txt"). 0 ok, -1 not compiled in, -2 failed.
    SAPI int32_t SEGAI_TorchBackboneGradProbe(const wchar_t* torchScriptPath, int32_t threads, int32_t side, const wchar_t* gradPath, char* report, int32_t capacity);
}
