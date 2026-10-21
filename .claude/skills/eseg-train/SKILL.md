---
name: eseg-train
description: Plan or run gated local training for EasyVision Segment AI after dataset and baseline readiness are established. Use for training, retraining, fine-tuning, model selection, loss/sampling choices, or reproducible experiment comparison. Do not deploy models or embed Python in the production camera loop.
---

# EasyVision Segment Training

Produce a reproducible local model candidate without weakening EasyVision's C++ production contracts.

When entered directly, invoke the `eroute` skill once (Skill tool). When invoked by `eseg-flow`, reuse its already validated Task Contract and approved plan revision; do not redispatch. Training, retraining, build/test, long-running GPU work, writing checkpoints, and changing source code require current-task authorization. A request to improve accuracy does not authorize deployment or replacement of the active model.

## Preconditions

- `eseg-data` returned `READY` or an approved `READY_WITH_EXCLUSIONS` manifest.
- `eseg-baseline` froze the current model/runtime baseline and acceptance gates.
- The target hardware, runtime, license policy, time budget, and output artifact format are known.
- A verified isolated training path writes the candidate outside every program-owned model path and returns without activation.

Stop if a required class is absent from training, the held-out split leaks capture groups, or the baseline identities cannot be reproduced.

The repository's normal `TrainConfigured` / `TrainConfiguredCore` path calls `ActivateTrainedCandidate` after evaluation and therefore is activation-coupled. Do not use that path, `ImportModel`, `ReplaceModelFile`, or the UI Train action for a staging-only lifecycle run. Unless an approved non-activating harness or entry point is available, return `TRAINING_BLOCKED_NO_STAGING_PATH`; never weaken the no-activation boundary just to obtain a checkpoint.

## Select the training mode

- Use supervised semantic segmentation when representative pixel masks exist. Prefer a small, exportable baseline before a larger architecture.
- Use anomaly localization when normal images are abundant and representative NG masks are too sparse for supervised training. Keep anomaly thresholds calibrated on held-out production-like data.
- Use promptable models only for annotation assistance unless the user explicitly approves a separate production study.
- Preserve the current EasyVision V2 path when the task is bounded to its frozen backbone, lightweight head, prior balancing, resume/rehearsal, validation-tolerance, and `.segai` compatibility invariants.

Do not select a model from training loss alone. Compare full-resolution validation quality, small-defect recall, robustness subsets, model size, and the authorized production-runtime evidence.

## Reproducible run

Record the dataset-manifest hash, split, seed, model/weight provenance, code revision, preprocessing, augmentations, sampler and class weights, optimizer/schedule, early-stop rule, device/runtime, and every exported checkpoint identity. Training and validation loss must use compatible weighting semantics.

Keep validation/test data out of optimization, calibration, threshold search, and INT8 calibration unless the approved protocol assigns a separate subset. A retrain must document whether it resumes, rehearses unchanged samples, or performs a full fresh run.

## Result

Return the best checkpoint and a compact comparison against the frozen baseline, including full-resolution quality, failure identities, reproducibility metadata, license provenance, and residual risks. Status is `TRAINED_NOT_EXPORTED`, or `TRAINING_BLOCKED_NO_STAGING_PATH` when isolation cannot be guaranteed. Hand a successful immutable candidate identity to `eseg-export`. This skill never claims C++ parity, deployment readiness, or INT8 readiness.

Structural reference: [Geti training skill](https://github.com/open-edge-platform/geti/blob/develop/skills/library/getitune-training-a-model/SKILL.md). Do not add `getitune`, another framework, or a native dependency to EasyVision without explicit approval.
