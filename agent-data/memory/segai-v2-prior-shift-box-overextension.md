---
name: segai-v2-prior-shift-box-overextension
description: Why Segment AI V2 gets mAP50 0.89 but mAP50-95 0.23 — boxes ~1.6x too big from class-prior shift in head training; measured offline 2026-10-01
metadata:
  node_type: memory
  type: project
  originSessionId: 1545b8a3-2319-4809-a95e-b6618519273a
  modified: 2026-10-01T01:19:06.385Z
---

Diagnosis of E:\Mobile\AI-Defect-V2-statisticsL1New.json (TESSSSSST/SegAI_00_000, 134 train / 69 val, 2592x1944, GT = hand-drawn axis-aligned RotatedBox ~300x242).

**Finding:** the V2 head over-extends every foreground blob. Matched predicted bbox is ~1.62x GT area (w x1.44, h x1.13, left edge -73 px, right +54 px); pixel precision 0.77 vs recall 0.94. Identical on TRAIN and VAL (box IoU 0.623 both) -> systematic bias, not overfit. The 18 FP + 18 FN at fg=0.5 are the same objects (localisation IoU<0.5 counted twice), not missed detections.

**Cause (measured):** `Gather()` in BeeNativeSegAI/SegV2Core.cpp caps rows per image at bg 2000 / fg 450 per class, weight 1. Natural bg:fg = 33.5:1, sampled = 2.2:1 -> ~15x odds shift, so posterior 0.5 really means ~0.06 true P(fg). Fringe/ambiguous pixels pass the Foreground=0.5 gate and the connected-component bbox balloons. Sweeping Foreground on val (CPU harness, 58/69 val images): 0.5 -> mean recall over IoU .5-.95 = 0.247; 0.8 -> 0.51; 0.9 -> 0.62; **0.95 -> 0.66 (area ratio 1.03, w 1.04, h 0.99)**; 0.98 -> 0.56; 0.99 -> 0.42. 1/(1+1/15)=0.94 matches the optimum. Morphology 0/3/5 is irrelevant.

**Other facts:** `ScorePercent`=26 in that JSON is the defect score gate (defectThreshold*100), not a quality score. Training-log val IoU 0.816 is on capped sampled rows; full-res pixel IoU is 0.71-0.74 even on train images. Early stop fired at epoch 339/1032 (best-loss gain <2% over 60 epochs) with LR still 0.0039 (cosine not annealed) — secondary.

**Why:** the user asked to find the error direction before fixing. **How to apply:** fix options in order of cost: (1) inference-time prior correction / default Foreground ~0.9-0.95 (no retrain); (2) weight bg rows by natural/sampled ratio in training so posteriors are calibrated; (3) report box IoU + full-res pixel IoU in the eval, not row IoU. Offline harness recipe: compile BeeCore/Func/NativeSegAI.cs + a small Main with Roslyn csc against copied OpenCvSharp/BeeNativeSegAI/openvino DLLs (don't load VMProtected BeeCore.dll); minDefectArea assumed 50. Related: [[segai-v2-trainer-facts]], [[segai-trainval-labeling-decisions]].
