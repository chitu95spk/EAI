# EAI-ONL P3b: fresh 512 vs reference mask benchmark

Run: `20261018T022851347Z-de016891`. Status: **Complete (see device coverage and notes)**. Updated UTC: 2026-10-18T02:32:43.6859307Z.

Dataset: `E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL-MODEL\tiles`. Candidate: `E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL-MODEL\train-tiles512\v512.segai`. Reference: `E:\Training\TrainLOGONEW\LOGO_SegMent.easyai.data\models\v005.segai`.
Requested V2 network side: 512; epoch budget: 60. Filename v512.segai is fixed even with --size overrides.

**This is latency and predicted NG-mask agreement, not accuracy.** The default E:\EAI_TEST dataset has only OK labels (no NG ground-truth pixels); defect recall/accuracy cannot be established. The reference model is a comparison reference, not ground truth. No held-out accuracy is computed.

Recognized labelled images: 158; NG annotations in this snapshot: 224. All recognized labelled images are included without a cap.

Three warm-ups per model/device on the first labelled full-resolution image are excluded. One timed Predict per image/model/device; full-frame ROI, original-resolution BGR. Loading, decoding, configuration, mask comparison and CSV/report I/O are outside the stopwatch. Predict includes native preprocessing/inference and managed output copies. Even for models with instance heads, this experiment calls the mask Predict API only.

Masks are the raw native NG masks returned by Predict (nonzero = NG), with each model's production metadata/settings and default DefectThreshold=0.5. No host resizing or component/area filtering is added. UsedGpu and native backend are checked after every call; GPU fallback rows are excluded. OpenVINO GPU use does not establish the physical adapter identity.

| Device | Paired images | Reference p50 / p95 ms | Candidate p50 / p95 ms | Mean mask IoU | Pooled mask IoU | Both empty | Mean IoU, nonempty union | Mean absolute NG-area difference, percentage points |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| CPU | 158/158 | 104.8558 / 155.5823 | 40.7987 / 69.8304 | 0.2034 | 0.0273 | 30 | 0.0167 | 3.6258 |
| iGPU requested | 158/158 | 37.2008 / 45.4661 | 20.7194 / 29.6253 | 0.2035 | 0.0274 | 30 | 0.0168 | 3.6085 |

Percentiles use nearest rank ceil(p*N). Per-image IoU is 1 when both masks are empty; both-empty counts and nonempty-only means expose this degeneracy. Pooled IoU is sum(intersection)/sum(union), n/a for an entirely empty union. Partial device coverage is shown explicitly. CSV is appended with a run ID; this report summarizes only the latest invocation.

- Native SegAI folder: E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL3\native-stage

- Model: E:\Training\TrainLOGONEW\LOGO_SegMent.easyai.data\models\v005.segai; network side=960; SHA256=5FBF8E01D4C343BDC4A62FA521BDCCF521D93398B236F823FC51C9FF662FD822

- Snapshot: 158 labelled images, 127 Train, 31 Val; NG annotations=224. All labelled images are measured, including Train and Val.

- Fresh V2 training: size=512, requested epochs=60, reported epoch rows=60, elapsed=00:02:15.4380840. Candidate: E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL-MODEL\train-tiles512\project\.easyai\models\v001.segai

- Training/evaluation details remain in the isolated project .easyai/runs and the embedded model report; no model was activated.

- Model: E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL-MODEL\train-tiles512\v512.segai; network side=512; SHA256=749A3CA9C48EB832A0D113557EF64188F699F53566400CD230D1D3B97CF04957

- CPU: mask Predict only; reference HasNgClass=True, candidate HasNgClass=True. Physical GPU adapter identity is not exported.

- iGPU requested: mask Predict only; reference HasNgClass=True, candidate HasNgClass=True. Physical GPU adapter identity is not exported.
