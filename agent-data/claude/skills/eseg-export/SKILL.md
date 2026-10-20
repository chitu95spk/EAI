---
name: eseg-export
description: Package and validate an EasyVision Segment AI candidate as a staged .segai artifact. Use for export/load round trips, training-versus-native parity, format compatibility, metadata preservation, or deciding whether an artifact may proceed to production-path inference. Do not activate or deploy the candidate.
---

# EasyVision Segment Export and Parity

Produce an immutable, loadable `.segai` candidate and prove that serialization and the native runtime preserve the approved model behavior.

When entered directly, invoke the `eroute` skill once (Skill tool). When invoked by `eseg-flow` or another Segment lifecycle stage, reuse its validated Task Contract and approved plan revision; do not redispatch. Export execution, artifact writes, native loading, build/test, benchmark, hardware use, activation, and deployment retain separate current-task authorization gates.

## Preconditions

- `eseg-train` returned `TRAINED_NOT_EXPORTED`, or an external candidate has an immutable source identity and an explicitly supported EasyVision conversion path.
- The dataset/split manifest, frozen baseline, acceptance gates, source checkpoint, exporter code, native DLL, OpenVINO runtime, precision, and target device are identified by version and hash.
- The output directory is isolated from every live or program-owned model path.

An ONNX, OpenVINO IR, PyTorch, or other file does not become an EasyVision model by renaming or copying it to `.segai`. If no supported converter and packager exist, return `EXPORT_BLOCKED_UNSUPPORTED_FORMAT` instead of inventing one.

## Preserve the EasyVision contract

EasyVision V2 `.segai` stores a validated header and CRC-protected payload containing the OpenVINO graph and weights plus runtime and training metadata. Preserve every field that changes loading, inference, or future retraining, including input-shape mode, active classes, postprocess version, prior-balanced semantics, threshold/minimum area, training report and outline, and compatible resume/retrain data.

Write to a uniquely named staging artifact. Never overwrite the source checkpoint, the accepted baseline, or a live `model.segai`. Record the artifact SHA-256, size, schema identity, exporter revision, source identity, and all appended metadata identities.

## Export and load-back parity

1. Freeze a representative parity fixture and the exact preprocessing, ROI, threshold, morphology, object-scale, speed-scale, boundary mode, and postprocessing options.
2. Export or package the candidate into staging, then validate header bounds, payload checksum, required metadata, graph inputs/outputs, element types, and dynamic/static shape expectations.
3. Load the staged artifact through the same native loader used by EasyVision. A successful load is necessary but is not parity evidence.
4. Run the reference model and the load-back native model on identical inputs. Compare raw score/confidence surfaces when available, thresholded masks, component identities, region scores, and final OK/NG decisions. Evaluate masks at original resolution.
5. Use tolerances approved before the comparison and appropriate to FP32, FP16, or INT8. Report threshold-near disagreements separately; never hide them inside aggregate IoU.

Parity evidence must include per-sample mismatch identities, maximum/percentile numeric error, mask disagreement pixels, component/box differences, final-decision differences, and the frozen full-resolution quality gates from `eseg-baseline`.

## Result

Return `EXPORTED_PARITY_ACCEPTED`, `EXPORTED_PARITY_REJECTED`, `EXPORT_INCONCLUSIVE`, or `EXPORT_BLOCKED_UNSUPPORTED_FORMAT`. Include hashes, commands, runtime identities, tolerances, raw-result locations, exclusions, and residual risks. Acceptance permits only `eseg-infer`; it does not authorize model activation or deployment.

Structural reference: [Geti model export skill](https://github.com/open-edge-platform/geti/blob/develop/skills/library/getitune-exporting-a-model/SKILL.md). Geti's OpenVINO/ONNX workflow is guidance, not proof that an artifact satisfies the EasyVision `.segai` contract.
