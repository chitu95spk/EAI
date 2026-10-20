# EAI-ONL P3a brief (Codex gpt-6.1-sol, single writer)

Repo E:\Code\EasyVision_Unisen - NoService. Project: EasyLabel/tests/EasyLabel.OnlineBench (committed harness, read its README.md and Program.cs/Options.cs/ProductionPipelines.cs first).

RULES: no build, no tests, no git. Edit ONLY files under EasyLabel/tests/EasyLabel.OnlineBench/ (and its README.md). Keep BOM/line endings; C# LangVersion 12. Do not touch src.

GOAL P3a: measure SegAiInferer latency and a recall proxy on this PC with
(1) GPU on (SegAiInferer / production SegAiPredictor EnableGpu path, same flag the app uses; record in the report which device was actually used; if the GPU/OpenVINO device is unavailable, say so in the report setup notes, never silently fall back) versus CPU, and
(2) ROI: whole frame versus a centred ROI crop of 512x512 and 960x960 and 1296x972 (half frame) on the 2592x1944 frame, passing the ROI rect to the production predict call (SEGAI_InferPredict takes roiX/Y/W/H; SegAiPredictor.Predict(Mat?, Rect?, ...)).
New options: `--gpu on|off|both` (default off = unchanged behaviour), `--roi whole|512|960|half|all` (default whole = unchanged). Config names must include gpu/roi tags; report.md and summary.csv must show them; percentile rules unchanged; serial budget table unchanged in meaning (note ROI rows exclude nothing else).
RECALL PROXY: when real labelled images exist in E:\EAI_TEST (png with matching label json / .easyai cache labels.png — inspect how E:\EAI_TEST is laid out read-only), add an `--accuracy` mode that, for each labelled image (cap 40), runs whole-frame and each ROI mode (ROI centred on the labelled defect bbox, size as above, clamped to image) and reports: fraction of labelled defect pixels covered by predicted mask (recall proxy) and predicted pixels outside the labels / predicted total (precision proxy), plus per-mode p50 latency. Document that this is a proxy on training-set images (the model v002 was trained on them), not held-out accuracy. If the layout cannot be parsed reliably, implement the latency parts only and say exactly what was missing.
Add a selftest path that still works (--selftest) and update README.md usage. Report under 25 lines: files changed, new flags, what is unverified. Do not paste files.
