# EAI-ONL P3a result (dev PC i7-1360P, v002.segai, EMULATED CPU, run 20261017T153322416Z-f426056a)

Report: artifacts/EAI-ONL/20261017T153322416Z-f426056a/report.md. Threads sweep limited to 4 and all (1/2 are in phase 1).
Accuracy NOT computable: all 55 labelled images in E:\EAI_TEST have only OK shapes (class 2), no NG pixels. Need NG-labelled data for recall.

SegAI infer p50 (ms, native, ROI 512 / 960 / whole), CPU vs Intel iGPU (OpenVINO):
- CPU, 4 logical: ~218-300 (these runs are slower than phase 1 105 ms; other load on the PC, treat as +-2x noisy).
- iGPU, 4 logical: roi512 ~52, roi960 ~49-54. Closest to the 50 ms budget, but only infer; serial budget p95 (infer + decode/host/transport) is 86-157 ms: NO config < 50 ms.
Backbone/head split (new native P3c, CPU 4 logical, roi512): backbone p50 85-93 ms, head p50 0 (mean ~1 ms). On iGPU: backbone p50 ~17 ms, head ~0.
=> the head is negligible; backbone is the cost, so a head cache gains nothing; a backbone cache needs identical frames (live frames differ).
=> on iGPU infer ~50 ms but backbone 17 ms: ~30 ms is outside backbone/head (pre/post-processing, mask copies, ROI analysis). Next: split that remainder.
512 network (P3b) is the remaining lever for CPU; GPU+ROI helps most.

## P3b result (512 vs 960, same PC, full 2592x1944 frame, 55 images, quiet machine) - tmp/EAI-ONL3/train512b/compare.md
- Train 512, 60 epochs, from scratch: 16 min 26 s, success (44 train / 11 val). Model tmp/EAI-ONL3/train512b/v512.segai.
- Predict p50/p95 ms: CPU v002(960) 67.7/77.3 -> v512 29.8/39.0; iGPU v002 37.2/44.6 -> v512 20.6/27.0 (about 2.2x / 1.8x faster).
- Mask agreement: both models predict NO defect pixels on all 55 images (all both-empty, IoU 1.0 is degenerate). Accuracy/recall NOT established: the dataset has only OK labels.
- The earlier P3a numbers (SegAI CPU 218-300 ms) were taken under other load; this quiet run shows CPU 960 = 68 ms. Treat P3a absolute values as pessimistic.
- Instance head may have been trained too (box-only labels, production default); not isolated.
Conclusion: 512 network + iGPU gets infer under 50 ms at p95 (27 ms); the 50 ms end-to-end budget still needs decode/host/transport added (not re-measured with v512), and needs NG-labelled data for any accuracy claim.

## CORRECTION (Codex trace, tmp/EAI-ONL3/nonbackbone-trace.md)
The P3a runs had EASYAI_SEGAI_SPLIT_TIMING=1. That switch runs an EXTRA backbone inference inside Predict (measured 15-17 ms), so every P3a SegAI infer number is inflated by ~15 ms and the "~30 ms unexplained" was mostly the instrumentation. Split OFF (P3b run): v002 iGPU infer 37 ms, CPU 68 ms. Use P3b numbers for latency; use the split run only for the backbone-vs-head ratio.
Real non-backbone costs (p50 ms, measured): managed postprocessing outside infer 19-22 (confidence/normal read 2.5+2.5, mask copies 1.2+1.2, free 0.2); native inferer tail (full-frame NG/OK/mask materialisation) 4.3; upsample 3.5; tensor fill 2.7; prepare 2.0; cc-score 0.9; device enumeration 0.27. Cheap fixes: reuse buffers, avoid full-frame materialisation when ROI is small, cache the OpenVINO device name.
