---
name: eseg-baseline
description: Freeze and evaluate an EasyVision Segment AI baseline before model or algorithm changes. Use for full-resolution mask quality, small-defect recall, box expansion, model/runtime parity, latency evidence, or defining acceptance gates. Do not train, deploy, or overwrite live binaries.
---

# EasyVision Segment Baseline Evaluation

Create a reproducible baseline that can distinguish a real model improvement from dataset, threshold, postprocessing, runtime, or split changes.

When entered directly, invoke the `eroute` skill once (Skill tool). When invoked by `eseg-flow`, reuse its already validated Task Contract and approved plan revision; do not redispatch. Build, test, benchmark, hardware execution, copying fixtures, and writing artifacts require the current task's explicit authorization. Never use or replace live `bin/Release` binaries as an oracle.

## Freeze the baseline

Record hashes and versions for the dataset/split manifest, model, native DLL, managed assembly, runtime, device/driver, training options, inference threshold, resize/letterbox, normalization, morphology, component filtering, and evaluation code. If any identity is unknown, mark the comparison inconclusive rather than choosing a convenient file.

Separate cold initialization from warm inference and network inference from end-to-end preprocessing/postprocessing. Historical results from a different model, DLL, device, fixture, or postprocessing revision are context only, not current evidence.

## Required quality evidence

Measure against original-resolution ground truth:

- Per-class pixel precision, recall, IoU, and Dice.
- Tiny-defect recall using an explicitly defined size bucket.
- Boundary-touching and low-contrast subset results.
- Matched component/box precision and recall, mean matched IoU, and predicted/ground-truth bounding-box area ratio.
- mAP50 and mAP50-95 when instance/component matching is applicable.
- Counts and identities of misses, false positives, empty masks, and excluded samples.

Sampled training-row IoU is telemetry only. Never present it as full-resolution production quality.

## Parity and performance gates

For exported or external models, compare the training-framework reference with the exact C++ production preprocessing, tensor layout, output interpretation, and postprocessing. Fail fast on metadata disagreement. Set numeric tolerances according to precision and approve them before using the candidate.

Report latency distributions (median, p95, maximum), cold first result, peak RAM/VRAM, and end-to-end time only when performance measurement is authorized and controlled. Do not infer production speed from framework-only inference.

## Decision

Return `BASELINE_FROZEN`, `CANDIDATE_ACCEPTED`, `CANDIDATE_REJECTED`, or `INCONCLUSIVE`. Include exact hashes, commands, raw-result locations, thresholds, failures, and unverified risks. A faster candidate fails if its approved quality gates fail.

Hand the frozen baseline and acceptance gates to `eseg-train`. This skill is intentionally stricter than the generic Geti workflow because EasyVision has a native C++ production path and persisted `.segai` compatibility obligations.
