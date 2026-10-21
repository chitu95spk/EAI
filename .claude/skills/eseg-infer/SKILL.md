---
name: eseg-infer
description: Validate an EasyVision Segment AI model through the actual managed-to-native production inference path. Use for staged .segai load/predict checks, CPU/GPU consistency, preprocessing and postprocessing verification, repeated-run stability, or production latency evidence. Do not train, quantize, activate, or deploy models.
---

# EasyVision Segment Inference Validation

Prove behavior through EasyVision's real managed-to-native inference boundary, not a framework-only approximation.

When entered directly, invoke the `eroute` skill once (Skill tool). When invoked by `eseg-flow` or another Segment lifecycle stage, reuse its validated Task Contract and approved plan revision; do not redispatch. Running inference, tests, benchmarks, GPU/hardware work, copying fixtures, and writing result artifacts require the corresponding current-task authorization.

## Preconditions

- The candidate returned `EXPORTED_PARITY_ACCEPTED`, or the task explicitly targets a frozen current-production baseline.
- Model, `BeeNativeSegAI.dll`, managed assembly, OpenVINO runtime, device/driver, fixture, evaluation code, and every inference option have immutable identities.
- The model and DLL are copied to an isolated verification area. Never replace or treat live `bin/Release` output as the oracle.

## Exercise the production contract

Use the path represented by `NativeSegAIInferer` and the `SEGAI_InferLoad` / `SEGAI_InferPredict` native exports. Match production BGR layout, stride, Check/ROI coordinates, resize and normalization, tensor layout, output interpretation, threshold, minimum area, morphology, object scale, speed scale, multiscale, boundary mode, and managed region analysis.

Prefer immutable image fixtures. Camera or UI execution needs explicit hardware/runtime authorization and must preserve Start → Read → Stop, avoid blocking the WinForms dispatch thread, serialize calls on one native handle where required, and free every returned native buffer through the owning API.

Validate:

- Clean load, corrupt/truncated/incompatible rejection, and actionable error propagation.
- Cold initialization, first result, warm repeated inference, model reload, cancellation/disposal, and deterministic repeated outputs.
- Representative image sizes, ROI positions, empty masks, tiny and boundary-touching defects, multiple components, low contrast, and threshold-near cases.
- CPU behavior and GPU behavior only when that backend is available and authorized; record the backend actually used rather than the requested backend.
- Full-resolution pixel, component/box, small-defect, and final OK/NG gates from the frozen baseline.

Measure median, p95, maximum, cold-start time, end-to-end time, and peak memory only when benchmarking is authorized. Separate native network time from preparation, tensor creation, upsampling, connected components, managed copying, and final postprocessing.

## Result

Return `INFERENCE_VALIDATED_NOT_DEPLOYED`, `INFERENCE_REJECTED`, or `INFERENCE_INCONCLUSIVE`. Include exact hashes, commands, backend/device evidence, per-sample failures, quality and authorized performance results, leak/stability observations, and unverified risks. Never activate or deploy the candidate.

Structural reference: [Geti inference skill](https://github.com/open-edge-platform/geti/blob/develop/skills/library/getitune-running-inference/SKILL.md). EasyVision's C# ↔ C++ ABI and native postprocessing remain authoritative.
