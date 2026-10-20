---
name: eseg-int8
description: Plan or validate post-training INT8 optimization for an exported EasyVision Segment AI model. Use for OpenVINO/NNCF calibration, FP-versus-INT8 quality and latency comparison, quantized artifact compatibility, or deciding whether INT8 is production-ready. Do not assume the current .segai packager supports quantized graphs, and do not deploy models.
---

# EasyVision Segment INT8 Optimization

Create an INT8 candidate only when the exact quantization, packaging, and native loading path is supported and its benefit survives production-path validation.

When entered directly, invoke the `eroute` skill once (Skill tool). When invoked by `eseg-flow`, reuse its validated Task Contract and approved plan revision; do not redispatch. Quantization, calibration reads, artifact writes, inference, benchmark, GPU/hardware work, build/test, activation, and deployment retain separate authorization gates.

## Capability gate

Require an immutable `EXPORTED_PARITY_ACCEPTED` FP32/FP16 artifact and `INFERENCE_VALIDATED_NOT_DEPLOYED` evidence. Identify the OpenVINO and NNCF versions, supported operations, target CPU/device, source graph/weights, `.segai` extraction/repackaging path, and the EasyVision loader that will consume the result.

The current V2 `.segai` embeds OpenVINO graph and weights; that fact alone does not prove that a generic NNCF output can be repackaged or loaded. If extraction, metadata preservation, graph compatibility, or repackaging is unsupported, return `INT8_BLOCKED_UNSUPPORTED_FORMAT`. Do not quantize an unrelated `.xml` and call it an EasyVision candidate.

## Calibration protocol

- Freeze a calibration manifest with hashes, capture-group provenance, preprocessing, and class/defect coverage.
- Use training-derived or separately approved calibration data. Never consume validation/test samples, and never tune calibration on acceptance results.
- Cover normal background, each relevant defect family, tiny/low-contrast/boundary cases, production lighting, and representative input sizes. Choose the sample count from convergence and coverage evidence rather than copying a generic number.
- Preserve the FP artifact unchanged and write the INT8 candidate to a unique staging path.

Record quantized and fallback operations, ignored scopes, calibration settings, input statistics, quantizer revision, runtime versions, target device, artifact hashes, and size change.

## Acceptance

Run `eseg-export` against the quantized staged artifact, then `eseg-infer` on the same frozen holdout and production path. Reuse the parent Task Contract and do not redispatch either skill.

Compare full-resolution pixel quality, tiny-defect recall, component/box metrics, final OK/NG decisions, threshold-near disagreements, model size, cold start, median/p95/maximum latency, peak memory, and backend actually used. All quality-loss and performance-win gates must be approved before looking at the result. Reject INT8 if quality fails, compatibility is uncertain, or the measured production path has no meaningful benefit.

## Result

Return `INT8_VALIDATED_NOT_DEPLOYED`, `INT8_REJECTED`, `INT8_INCONCLUSIVE`, or `INT8_BLOCKED_UNSUPPORTED_FORMAT`. Include FP and INT8 hashes, calibration-manifest hash, tool/runtime identities, commands, raw-result locations, quality/performance deltas, unsupported operations, and residual risks. Never replace the FP artifact or activate the INT8 model.

Structural reference: [Geti INT8 optimization skill](https://github.com/open-edge-platform/geti/blob/develop/skills/library/getitune-optimizing-a-model/SKILL.md). Its OpenVINO/NNCF workflow does not override EasyVision packaging, ABI, or verification rules.
