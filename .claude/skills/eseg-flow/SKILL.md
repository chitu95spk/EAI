---
name: eseg-flow
description: Coordinate an end-to-end EasyVision Segment AI model improvement request across dataset audit, baseline evaluation, training, export parity, native inference validation, and optional INT8 optimization. Use when a request spans multiple model-lifecycle stages. Do not activate or deploy models.
---

# EasyVision Segment Model Lifecycle

Coordinate model work as explicit, resumable gates. Keep the original user goal, dataset/model identities, authorization flags, evidence, and unresolved risks visible at every handoff.

Invoke the local `eroute` exactly once at lifecycle entry. Pass the validated Task Contract and approved plan revision to every stage skill, and do not let nested stages redispatch. This lifecycle never broadens permissions: read-only analysis, file writes, source edits, training, build, test, benchmark, hardware use, deployment, commit, and push retain separate authorization gates.

## Route the request

1. Use `eseg-data` to freeze and qualify the dataset.
2. Use `eseg-baseline` to freeze current behavior and define measurable acceptance gates.
3. Use `eseg-train` to produce a candidate only after both earlier gates pass.
4. Use `eseg-export` to create a staged `.segai` artifact and prove reference-to-native load-back parity.
5. Use `eseg-infer` to exercise the exact managed-to-native production path without activation.
6. Use `eseg-int8` only when INT8 is requested and the FP candidate has passed export parity and inference validation. Re-run export parity and production inference validation for the quantized candidate.

Do not skip a gate because a previous report looks plausible. Reuse prior evidence only when its dataset, split, model, DLL/runtime, options, evaluation code, and hashes match the current task.

## Mode decision

- Representative pixel masks: supervised segmentation.
- Many normal images and scarce/partial NG masks: anomaly localization study.
- Need faster annotation: promptable label assistance, followed by human confirmation; do not treat its masks as trusted ground truth automatically.
- Existing EasyVision V2 regression: improve and verify the current native path before evaluating an external framework.

## Stage state

Maintain a compact handoff with:

- Current stage and status.
- Approved dataset/split manifest hash.
- Baseline model/runtime/evaluation hashes.
- Authorized actions and explicitly forbidden actions.
- Acceptance gates and observed results.
- Artifact locations, blockers, and next decision.

Stop on `BLOCKED`, an authorization boundary, missing immutable inputs, incompatible licenses, or an EasyVision repository stop condition.

## Lifecycle states and boundary

Advance only through evidence-backed states:

`READY` or approved `READY_WITH_EXCLUSIONS` → `BASELINE_FROZEN` → `TRAINED_NOT_EXPORTED` → `EXPORTED_PARITY_ACCEPTED` → `INFERENCE_VALIDATED_NOT_DEPLOYED` → optional `INT8_VALIDATED_NOT_DEPLOYED`.

`TRAINING_BLOCKED_NO_STAGING_PATH` is terminal until an approved non-activating training entry point exists; it must not be converted into `TRAINED_NOT_EXPORTED` by using EasyVision's activation-coupled UI or managed training path.

The implemented lifecycle ends before activation and deployment. No earlier state permits copying over a program-owned model, replacing a live DLL, changing the active recipe, committing, pushing, or operating production hardware. If a requested format cannot be packaged into a compatible `.segai`, stop at the matching unsupported-format state rather than substituting raw Geti commands.

Reference workflow shapes:

- [Geti training](https://github.com/open-edge-platform/geti/blob/develop/skills/library/getitune-training-a-model/SKILL.md)
- [Geti dataset preparation](https://github.com/open-edge-platform/geti/blob/develop/skills/library/getitune-preparing-datasets/SKILL.md)
- [Geti export](https://github.com/open-edge-platform/geti/blob/develop/skills/library/getitune-exporting-a-model/SKILL.md)
- [Geti optimization](https://github.com/open-edge-platform/geti/blob/develop/skills/library/getitune-optimizing-a-model/SKILL.md)
- [Geti inference](https://github.com/open-edge-platform/geti/blob/develop/skills/library/getitune-running-inference/SKILL.md)

Use these as structural references only. EasyVision's native ABI, `.segai` persistence, C# 12 (EasyLabel/Directory.Build.props), UI-thread, camera lifecycle, build authorization, and workspace rules remain authoritative.
