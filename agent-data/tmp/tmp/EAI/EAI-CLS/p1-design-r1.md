# EAI-CLS P1 design r1 (architect pass, 2026-10-09) - native named classes

Status: design only; awaiting the user's answers to Q1-Q4 and /usage before slice S0. Corrects plan-r1 section 2 P1(a) and the explorer summary.

## Corrections
1. New format version is **4** (version 3 is already the V2 head file, SegV2Core.cpp:1422).
2. **Do not bump `SEGAI_GetVersion`** (EasyLabel rejects any other ABI number, NativeSegAiLibrary.cs:163). Only add exports plus `SEGAI_GetClassApiVersion()`.
3. One generalised V2 path with runtime class count K = N+1; K=3 proven bit-identical. No separate copied N-class path.
4. V1 (RTrees) and structural models stay 2-class only.
- Extra hard-coded 3s the explorer missed: SegV2Core.cpp:46 (Classes = 3), output-shape checks :148, :464, :1453-1455, AddHead mask :924, SegBackboneFineTune.cpp:81 and :192.
- The 100..199 / 200..255 label ranges exist only in V1 (SegTrainerCore.cpp:100-102).
- P0 baseline covers only V2 compact inference (2 models x 2 images); no training, multiscale, tiled, cleanup, winner or speed-scale coverage yet (slice S0 adds it).

## Invariants
- I1 versions 1, 2, 3 load and give byte-identical masks/confidence through the old exports; a trainer that never gets a class table writes a byte-identical v3 file.
- I2 old builds reject v4 and never misread it; new native gives a clear lastError for version > 4.
- I3 existing exports keep signature and behaviour; `SEGAI_GetVersion` stays 1; new exports use caller-owned buffers only.
- I4 background id 0, classes 1..N (N <= 32), at most one GOOD class, header numClasses = N+1.
- I5 live bin\Release and lib\native untouched; isolated builds only.

## Format v4
Same 72-byte header: version 4, featureFlags 0x02000000, featureCount 448, numClasses N+1 (2..33), `reservedA` (offset 52) = class-table byte size. Layout: [header 72][YAML payload, payloadSize, same crc32 rule][class table, reservedA bytes]; file size = 72 + payloadSize + reservedA. The table must NOT precede the YAML (managed code scans the first ~1 KB of the payload at offset 72 for native_v2 keys).
Class table (little-endian): "SCLS", uint32 tableVersion=1, uint32 N; per id 1..N: uint8 kind (0 DEFECT, 1 GOOD), uint8 flags (bit0 = enabled/had pixels), uint16 nameBytes (1..64), UTF-8 name; then uint32 CRC32 of the preceding table bytes. Max 2192 bytes. Valid UTF-8, no NUL/control chars, unique names (ordinal), <= 1 GOOD, >= 1 enabled class. v4 YAML omits `active_classes` (enabled flags live in the table). Legacy files get an in-memory table: 1 "NG"/DEFECT, 2 "OK"/GOOD. Resume from v4 requires same K and names in order, else fresh start ("class set changed"). Add static_asserts: sizeof 72, offsetof reservedA 52, payloadSize 56, crc32 64, kMaxClasses 32.

## ABI additions (additive, cdecl, int32)
- `SEGAI_GetClassApiVersion()` -> 1 (managed probes with GetExport).
- `SEGAI_TrainerSetClassTable(h, table, bytes)`: after ConfigureV2, before first AddSample, else -2; profiles 0/1 -2; bad table -3 with lastError; incoming enabled flags ignored.
- `SEGAI_InferGetClassTable(h, buf, capacity)`: returns required size; writes nothing if too small; -1 null handle; -4 no model.
- `SEGAI_InferPredictMulti(h, bgr, w, h, step, roiX, roiY, roiW, roiH, threshold, outLabel, labelStep, outScore(nullable), scoreStep, outClassFraction, classCapacity)`: full-image outputs, zeros outside ROI; label = class id of the owning component; score = that component's score; fraction[c-1] = accepted pixels of class c / ROI area; -4 if classCapacity < N; updates the same internal state as Predict. Regions are extracted in managed code.
- Old exports on an N-class model: InferPredict = union of DEFECT classes thresholded; GetConfidence = DEFECT score map; GetNormalConfidence = GOOD map or -2; HasNgClass = any enabled DEFECT; HasNormalClass = GOOD enabled; a v4 N=2 file (NG/DEFECT, OK/GOOD) equals its v3 sibling bit for bit; PredictDiagnosticV1 -5 for N>2.

## Training
Runtime K member, fixed-capacity std::array<..., 33>; loops c=0..K-1 in the same order so K=3 RNG draws, gemm shapes and float op order are unchanged. Head hidden x K + bias 1 x K; masked-softmax cross-entropy; caps 2000 (background) / 450 (each foreground class); one pooled foreground prior weight (no per-class balancing: new algorithm, out of scope). Disabled class: zero column, bias -1e9. Fine-tune (SegBackboneFineTune) generalised the same way. Training raster value = class id 0..N, > N ignored (recommend 255 = ignore in P2). Save writes v3 when no table was set, v4 when one was.

## Inference
One shared foreground threshold and morphology. Sum mode: sum of enabled p_c >= threshold accumulated in order AND OR_c(p_c > p0); winner mode: max over classes. Shared components; class = argmax of component mean, lowest id wins ties; score formula unchanged. K=3 keeps the exact ng/ok planes and existing cleanup; K>3 uses INTER_NEAREST resize of label/score maps and class-indexed shape cleanup. Tiled path checks {1,K,..}. Per-class thresholds/area filters in managed code. For N>2 P1 rejects multiscale and postprocess_version 1 with a clear error. Verdict (P2/P3): NG when any DEFECT class has an accepted region passing the managed filters.

## Slices (one commit each, token estimates incl. reviewer)
- S0 baseline extension (~250k): test-only modes in BeeNativeSegAI/test/SegAITest.cpp; inference dumps (compact, postprocess 1, multiscale, winner, cleanup 0.1, speed scale 0.5, tiled) and a deterministic tiny 2-class CPU training run (with/without fine-tune) twice; store in artifacts/EAI/EAI-CLS/p1/baseline-ext/.
- S1 format + class table (~450k): codec/validation, v4 load/save K=3 only, dispatch accepts 3 or 4, legacy table synthesis, version > 4 message, exports GetClassApiVersion/TrainerSetClassTable/InferGetClassTable; tests T1-T3.
- S2a generalised inference runtime K (~550k): gate P0 + S0 inference dumps identical.
- S2b generalised training + fine-tune (~600k): gate S0 training .segai SHA256 identical.
- S3 PredictMulti + legacy mapping + 3-class end-to-end (~450k): tests T4-T6.
- Total about 2.3-2.6M tokens (native only; P2 managed, P3 EasyVision tool, P4 verification come after).

## Gate before every commit
Isolated build of BeeNativeSegAI.vcxproj and test exe (Release|x64, -p:EasyVisionIsolatedVerification=true) 0 errors, warnings <= baseline; runnable copy under artifacts/EAI/EAI-CLS/p1/<slice>/run/ (deps as in deferred.md); ParityDumpTests binary compare vs parity-v2 = 0 differences; S0 dumps identical (S2b onward training SHA256 identical); SEGAI_RunFeatureSelfTest = 0; dumpbin /exports shows additions only and GetVersion = 1; Opus reviewer on S1 and S3. Stop and do not commit if any K=3 byte differs or the two S0 baseline training runs differ. Rollback = revert the slice commit.

## Risks
Float/RNG order drift (gates catch it); OpenVINO IR serialisation must stay identical for v3; libtorch fine-tune may be non-deterministic (K=3 fine-tune claim then unverified); shape cleanup may leave NG/OK overlapping (PredictMulti gives NG precedence for K=3, legacy exports stay exact); old native leaves lastError empty for v4; hardware/production evidence owed in P4.

## Questions for the user (defaults proposed by the architect)
- Q1 a project whose classes are exactly NG/DEFECT and OK/GOOD keeps saving version 3 so old EasyVision can still load it (default yes).
- Q2 for N>2 in P1: no multiscale or postprocess_version 1, PredictDiagnosticV1 2-class only, shape cleanup generalised (default yes).
- Q3 fine-tune with N>2: generalise it and add a fine-tune baseline (+~150k tokens) or reject N>2 with fine-tune in P1 (default generalise).
- Q4 per-class foreground/morphology are not native; per-class filters run in managed code (default yes).
