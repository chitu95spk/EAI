---
name: eseg-data
description: Audit and freeze EasyVision Segment AI datasets before model training. Use for image-mask integrity, split leakage, label coverage, class imbalance, missing files, dataset provenance, or deciding whether a dataset is ready to train. Do not start training or modify source data.
---

# EasyVision Segment Dataset Audit

Produce an evidence-backed dataset readiness decision for EasyVision Segment AI. Treat the dataset as immutable unless the user separately authorizes a copied repair workflow.

When entered directly, invoke the `eroute` skill once (Skill tool) for repository scope and execution permissions. When invoked by `eseg-flow`, reuse its already validated Task Contract and approved plan revision; do not redispatch. Read-only inspection does not authorize copying images, rewriting masks, changing Train/Val pins, or running training.

## Audit contract

1. Identify the exact dataset root, annotation format, label meanings, Check/ROI semantics, and intended task: supervised segmentation, anomaly localization, or annotation assistance.
2. Build or inspect a manifest containing stable image and mask identities, dimensions, content hashes, physical part/capture group, split, and provenance. Keep calibration data separate from validation/test data.
3. Validate image-mask pairs: presence, decoding, matching dimensions, channel/type assumptions, legal class values, empty/full masks, one-pixel and boundary-touching defects, disconnected components, and labels outside the active Check/ROI.
4. Detect exact duplicates and likely leakage across Train/Val caused by the same physical part, capture burst, transformed copy, or renamed file. Split by part/session rather than random frame when groups exist.
5. Report natural pixel/component counts separately from any sampled training rows. Flag class imbalance, missing defect modes, label noise, and validation classes absent from training.
6. Return `READY`, `READY_WITH_EXCLUSIONS`, or `BLOCKED`, with exact counts, excluded item identities and reasons, unresolved risks, and the immutable split-manifest hash.

## Readiness gates

- Every included image has a deterministic identity and usable annotation.
- Train and validation/test do not share a physical part or capture group.
- Exclusions are explicit; a missing file is never silently ignored.
- The held-out set covers normal, NG, small-defect, low-contrast, boundary, and multi-component cases relevant to production.
- Dataset, checkpoint, pretrained weights, and source-code licenses are recorded independently.

Do not claim a dataset is representative merely because aggregate class counts are large. Surface unknown capture conditions, absent defect families, and sparse validation coverage.

## Handoff

Provide the frozen manifest, readiness verdict, class/component summary, leakage report, and remaining risks to `eseg-baseline` and `eseg-train`. Do not invoke training when the verdict is `BLOCKED`.

Structural reference: [Geti dataset preparation skill](https://github.com/open-edge-platform/geti/blob/develop/skills/library/getitune-preparing-datasets/SKILL.md). EasyVision rules above take precedence.
