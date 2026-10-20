# EAI-ONL P3b: fresh 512 vs reference mask benchmark

Run: `20261017T161701371Z-c1d08031`. Status: **Failed / partial**. Updated UTC: 2026-10-17T16:17:06.6366273Z.

Dataset: `E:\EAI_TEST`. Candidate: `E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL3\train512\v512.segai`. Reference: `E:\EAI_TEST\.easyai\models\v002.segai`.
Requested V2 network side: 512; epoch budget: 60. Filename v512.segai is fixed even with --size overrides.

**This is latency and predicted NG-mask agreement, not accuracy.** The default E:\EAI_TEST dataset has only OK labels (no NG ground-truth pixels); defect recall/accuracy cannot be established. The reference model is a comparison reference, not ground truth. No held-out accuracy is computed.

Recognized labelled images: 55; NG annotations in this snapshot: 0. All recognized labelled images are included without a cap.

Three warm-ups per model/device on the first labelled full-resolution image are excluded. One timed Predict per image/model/device; full-frame ROI, original-resolution BGR. Loading, decoding, configuration, mask comparison and CSV/report I/O are outside the stopwatch. Predict includes native preprocessing/inference and managed output copies. Even for models with instance heads, this experiment calls the mask Predict API only.

Masks are the raw native NG masks returned by Predict (nonzero = NG), with each model's production metadata/settings and default DefectThreshold=0.5. No host resizing or component/area filtering is added. UsedGpu and native backend are checked after every call; GPU fallback rows are excluded. OpenVINO GPU use does not establish the physical adapter identity.

| Device | Paired images | Reference p50 / p95 ms | Candidate p50 / p95 ms | Mean mask IoU | Pooled mask IoU | Both empty | Mean IoU, nonempty union | Mean absolute NG-area difference, percentage points |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

Percentiles use nearest rank ceil(p*N). Per-image IoU is 1 when both masks are empty; both-empty counts and nonempty-only means expose this degeneracy. Pooled IoU is sum(intersection)/sum(union), n/a for an entirely empty union. Partial device coverage is shown explicitly. CSV is appended with a run ID; this report summarizes only the latest invocation.

- Native SegAI folder: E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL3\native-stage

- Model: E:\EAI_TEST\.easyai\models\v002.segai; network side=960; SHA256=FC9F82248E88EF2AE39DD94DC325BAD2D704FF502647D612CA5DA0BAE3C7C74A

- Snapshot: 55 labelled images, 44 Train, 11 Val; NG annotations=0. All labelled images are measured, including Train and Val.

- Failure: Training failed: V2 backbone is missing: E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL3\native-stage\Models\DefectV2\mobilenetv2-12.onnx (expected Models\DefectV2\mobilenetv2-12.onnx next to BeeNativeSegAI.dll).
