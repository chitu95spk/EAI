# Deferred work: EAI-CLS

## 2026-10-09 - plan approved, P0 done
- Approval: user approved plan-r1 (P0-P4) on 2026-10-09 and confirmed P3 (EasyVision ToolSegmentAI) stays in scope. P5 (live folders) needs a separate OK. Contract: .claude/task-contract.EAI-CLS.toml (edit/build/test/commit yes, push/deploy/hardware/benchmark no).
- P0 evidence (artifacts/EAI/EAI-CLS/p0/):
  - native-baseline-build.log: isolated build of HEAD BeeNativeSegAI (Release|x64, EasyVisionIsolatedVerification=true) 0 errors, output artifacts/verification/Release/x64/bin/BeeNativeSegAI/.
  - native-baseline-run/: runnable copy (isolated DLL + torch + OpenVINO/TBB/opencv_world455 copied from bin\Release; the isolated folder alone lacks OpenVINO and the loader then says "invalid model").
  - Baseline harness = existing opt-in test ParityDumpTests (EASYAI_PARITY_OUT, EASYAI_SEGAI_DIR, EASYAI_PARITY_MODEL). Run with `dotnet test ... --no-build --filter FullyQualifiedName~ParityDumpTests` after building the tests (never --no-build on stale binaries).
  - parity-v2 (baseline DLL) vs parity-live (bin\Release DLL): V2 NG-only and NG+OK models x tmp/EAI/EAI-3/fixture-src/{0,1}.png: every mask/conf/nmask/nconf/raw dump byte-identical. This is the 2-class regression baseline for P1.
  - parity-v1-*: V1 model outputs/AI_Defect_V109_Canvas_GPU/TCHI_LOA_Model.segai gives the same error on both DLLs ("No learned NG/OK region found inside Check"), so V1 has only error-path coverage. OWED: a V1 model with matching images.
- Facts: native numClasses is hard-coded (SegAIFileFormat.cpp:18 = 2 for V1; SegV2Core.cpp:1068/1422/1433 version 3, numClasses 3 = bg/NG/OK, featureCount 448). V2 pipeline carries separate Bg/Ng/Ok Mats (SegV2Core.h). One BeeNativeSegAI.dll is shared by EasyVision (BeeCore/Func/NativeSegAI.cs) and EasyLabel.
- Pitfall found: sln/`dotnet test --no-build` of EasyLabel used stale test binaries; always run `dotnet test` without --no-build (or build the test csproj with -p:Platform=x64).

## Next (P1 start)
1. Dispatch the architect pass (contract: architect_required, reviewer opus for native/ABI): native format v4 + class table, SEGAI_InferPredictMulti, N-channel training, dual-read, ABI version constant. Ask the user for /usage first (usage snapshot is stale).
2. Then P1 implement in isolated builds only.

## 2026-10-09 - P1 architect pass done
- Design saved: tmp/EAI/EAI-CLS/p1-design-r1.md (format v4 + class table trailer, no GetVersion bump, one generalised V2 path K=N+1, V1/structural stay 2-class, slices S0 baseline-ext, S1 format, S2a inference, S2b training, S3 PredictMulti; ~2.3-2.6M tokens).
- Waiting for the user: answers to Q1-Q4 (defaults proposed) and /usage. Do NOT start S0 before that.
- Next: S0 (extend native test exe + run against HEAD DLL), isolated builds only, one commit per slice.

## 2026-10-09 - S0 baseline extension done (user chose defaults Q1-Q4; S1+ wait for r11 native decision)
- Harness: BeeNativeSegAI/test/SegAITest.cpp `--baseline-ext <model> <imageDir> <outDir> [train [<backbone.onnx>]]`. Inference dumps: compact_default, multiscale_on, winner_boundary, shape_cleanup 0.1, speed_scale 0.5 (CPU). Unreachable via HEAD exports, so NOT baselined: postprocess_version 1 (YAML model property) and tiled. Training: one V2 head training per process (settings.json, 1 epoch, side 160, CPU), writes train-run.segai.
- Evidence (untracked, artifacts/EAI/EAI-CLS/p1/): inference run1==run2 byte-identical for NG-only and NG+OK models (baseline-ext/*-infer-run{1,2}); training in two fresh processes gives the same SHA256 fe8381b2fb5c64a7dddf048b5137ce6c080cf44f16281f0d3b2aaf1f0d2552ff (train6-a, train6-b); HEAD DLL SHA256 bdf0e85d24b2941f0ee4e7d6df3f6c8db8e1f649dc2c80f88eac29895f7cc33b; build-test7.log 0 errors.
- Gotchas: two trainings in ONE process differ only in network_xml node-name counters (weights identical) - compare across processes. V2 config is JSON (settings.json) with absolute forward-slash paths and a real backbone (bin\Release\Models\DefectV2\mobilenetv2-12.onnx). Mask needs class 0 pixels. Fine-tune not baselined (uncommitted r11 code).
- Owed before S1: decision on uncommitted SEG-LIFECYCLE-r11 edits in SegV2Core.cpp, SegAINativeExport.*, vcxproj, SegBackboneFineTune.*; then build the baseline DLL from a clean HEAD worktree if S1 edits those files.
- artifacts/EAI/EAI-CLS/p1/s0/run/ contains a tmp/ copy of other tasks' data (origin not explained by verifier); untracked, do not commit.
