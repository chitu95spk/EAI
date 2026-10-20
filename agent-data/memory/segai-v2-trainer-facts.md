---
name: segai-v2-trainer-facts
description: Segment AI V2 training is a native C++ head trainer (not YOLO); why it was slow and what V149/V150 changed
metadata:
  node_type: memory
  type: project
  originSessionId: a8090c57-2ecd-4188-b5e2-43c5fb31a8db
  modified: 2026-09-29T18:19:59.818Z
---

Segment AI V2 "training" (BeeNativeSegAI/SegV2Core.cpp) is NOT Ultralytics/PyTorch/CUDA. A frozen MobileNetV2 ONNX backbone runs once through OpenVINO (Intel iGPU or CPU, never the NVIDIA card: Device() only accepts an "Intel" GPU) to extract 448-channel features from 6 augment variants per image; then a 448->32->3 MLP head is trained with full-batch Adam on the CPU (cv::gemm, ~2.95M rows for 187 images at original-input size). Epoch cost is constant; augmentation is baked in once, so freeze/close_mosaic/imgsz-schedule ideas do not apply.

**Why 3 hours (measured from real epochs.csv in bin/Release/Program/TESSSSSST/SegAI_00_000/training-work):** ~12 s/epoch, 817 configured epochs, early-stop window max(20, epochs/10)=81 and min epochs 162 so runs go to 500-800 epochs; `ValidationPercent=0` (recheck mode) made every epoch do a redundant validation pass over the same rows (val_e == train_(e+1) exactly); RAM 97% came from x = samples.rows.clone() plus push_back concat plus cached full-res images.

**Done (V149, V150, all committed, all bit-exact when option unset):** no clone, exact-size concat, skip redundant val pass in recheck mode (14-23% faster/epoch on a small fixture, -170 MB peak); `EarlyStopPatience` option (0=legacy, else window 5..200; Retrain resumed head defaults to 20); native frees feature buffers after Impl::Train.

**Not done / owed:** real-scale timing on the user's 187-image set (all numbers so far are small-fixture), "active set" (train only hard rows in later epochs) needs the user's data for A/B, minibatch Adam untested, GPU training of the head not worth it (shared RAM already full, no OpenVINO training).

**Why:** the user wanted train/retrain faster and epoch cost to shrink over time; this records what the trainer really is so nobody re-proposes YOLO-style fixes.
**How to apply:** for any Segment AI speed request check these facts first; native verification uses the harness idea (train_dump.py/compare_out.py in a scratchpad, fixtures artifacts/V133/fixtures) comparing epochs.csv non-time columns + head.yml + model.segai before/after with /p:EasyVisionIsolatedVerification=true. See [[feedback-git-line-level-staging-verify-head]] and [[feedback_delegate_to_agents]].
