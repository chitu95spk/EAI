# EAI-ONL phase 2 decision note

Report: artifacts/EAI-ONL/20261017T120207594Z-40b11636/report.md (i7-1360P, 16 logical CPUs; frame 2592x1944 tiled from a real PNG; model v002.segai, network 960; every thread number is EMULATED by affinity, not a 2-core i7-7600U).

## Result (mono, raw transport, p50 / p95 ms, budget p95 < 50)
| CPUs | SegAI mask (jpeg src) | SegAI mask (png src) | ONNX YOLO (png src) |
|---|---|---|---|
| 1 | 306 / 394 | 334 / 431 | 208 / 299 |
| 2 | 215 / 269 | 277 / 381 | 158 / 195 |
| 4 | 129 / 152 | 142 / 163 | 102 / 121 |
| all(16) | 105 / 126 | 129 / 163 | 136 / 190 |

- No configuration meets p95 < 50 ms. Best is 105/126 ms (SegAI, all CPUs).
- Dominant stage is `infer` everywhere; decode and transport are small next to it.
- ONNX gets worse from 4 to 16 CPUs (thread oversubscription / hybrid cores); SegAI keeps improving.
- Caveat: the run overlapped with builds, CodeGraph sync and other agents, so absolute values carry noise (about +-20%); the verdict (far above 50 ms) is not affected.
- SegAI has no thread setter and no backbone/head split, so the sweep cannot say what a real thread cap would do.

## Decision needed from the user
50 ms per image is not reachable by tuning threads alone on this model: the 2-core target would be slower than the 1-CPU row (300+ ms).
Options for phase 3 (pick one, each needs approval):
1. Smaller network side (e.g. 480/512 instead of 960) and ROI crop, measure latency vs recall.
2. Split backbone/head and cache or skip the backbone work (needs native change, safety/native lane).
3. Accept a relaxed budget (e.g. 150 ms mono) for the 2-core target and measure on the real i7-7600U (needs hardware authorization).
