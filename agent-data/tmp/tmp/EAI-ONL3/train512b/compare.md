# EAI-ONL P3b: fresh 512 vs reference mask benchmark

Run: `20261017T161746683Z-aa531a67`. Status: **Complete (see device coverage and notes)**. Updated UTC: 2026-10-17T16:34:40.2936773Z.

Dataset: `E:\EAI_TEST`. Candidate: `E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL3\train512b\v512.segai`. Reference: `E:\EAI_TEST\.easyai\models\v002.segai`.
Requested V2 network side: 512; epoch budget: 60. Filename v512.segai is fixed even with --size overrides.

**This is latency and predicted NG-mask agreement, not accuracy.** The default E:\EAI_TEST dataset has only OK labels (no NG ground-truth pixels); defect recall/accuracy cannot be established. The reference model is a comparison reference, not ground truth. No held-out accuracy is computed.

Recognized labelled images: 55; NG annotations in this snapshot: 0. All recognized labelled images are included without a cap.

Three warm-ups per model/device on the first labelled full-resolution image are excluded. One timed Predict per image/model/device; full-frame ROI, original-resolution BGR. Loading, decoding, configuration, mask comparison and CSV/report I/O are outside the stopwatch. Predict includes native preprocessing/inference and managed output copies. Even for models with instance heads, this experiment calls the mask Predict API only.

Masks are the raw native NG masks returned by Predict (nonzero = NG), with each model's production metadata/settings and default DefectThreshold=0.5. No host resizing or component/area filtering is added. UsedGpu and native backend are checked after every call; GPU fallback rows are excluded. OpenVINO GPU use does not establish the physical adapter identity.

| Device | Paired images | Reference p50 / p95 ms | Candidate p50 / p95 ms | Mean mask IoU | Pooled mask IoU | Both empty | Mean IoU, nonempty union | Mean absolute NG-area difference, percentage points |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| CPU | 55/55 | 67.6730 / 77.3131 | 29.7540 / 39.0292 | 1.0000 | n/a (empty union) | 55 | n/a | 0.0000 |
| iGPU requested | 55/55 | 37.1744 / 44.5552 | 20.5948 / 27.0412 | 1.0000 | n/a (empty union) | 55 | n/a | 0.0000 |

Percentiles use nearest rank ceil(p*N). Per-image IoU is 1 when both masks are empty; both-empty counts and nonempty-only means expose this degeneracy. Pooled IoU is sum(intersection)/sum(union), n/a for an entirely empty union. Partial device coverage is shown explicitly. CSV is appended with a run ID; this report summarizes only the latest invocation.

- Native SegAI folder: E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL3\native-stage

- Model: E:\EAI_TEST\.easyai\models\v002.segai; network side=960; SHA256=FC9F82248E88EF2AE39DD94DC325BAD2D704FF502647D612CA5DA0BAE3C7C74A

- Snapshot: 55 labelled images, 44 Train, 11 Val; NG annotations=0. All labelled images are measured, including Train and Val.

- Fresh V2 training: size=512, requested epochs=60, reported epoch rows=60, elapsed=00:16:26.0698453. Candidate: E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL3\train512b\project\.easyai\models\v001.segai

- Training/evaluation details remain in the isolated project .easyai/runs and the embedded model report; no model was activated.

- Model: E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL3\train512b\v512.segai; network side=512; SHA256=7D2F66143053C250F6112C8297AEFEAE089CCF0AE07FDAEAF3DFA9B7CA0E644D

- CPU: mask Predict only; reference HasNgClass=False, candidate HasNgClass=False. Physical GPU adapter identity is not exported.

- iGPU requested: mask Predict only; reference HasNgClass=False, candidate HasNgClass=False. Physical GPU adapter identity is not exported.
