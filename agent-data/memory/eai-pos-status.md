---
name: eai-pos-status
description: EAI-POS (EasyAI Position instance head) status after P1-P6 commits and first real-data measurement on E:\EAI_TEST
metadata:
  node_type: memory
  type: project
  originSessionId: 9210e0a7-61d4-4e7a-aa11-58c0b44a5142
  modified: 2026-10-14T13:32:24.123Z
---

EAI-POS P1-P6 all committed on main (P3 419528c51, P4 ba7c1339e, P5 09a9245b7, P6 3c2698c08). P7 evidence harness (tmp/EAI-POS/p7, results artifacts/EAI-POS/P7/results.md) measured the head on real data 2026-10-14.

Real-data result (26 labelled images in E:\EAI_TEST, 7 held out, 35 boxes, teardrop fan housings, touching): recall/precision 1.00/1.00 (best run .971/1.0), so the synthetic recall gap (.84-.93) is NOT the real bottleneck. Accuracy misses the plan: centre 4.5 px median / 10 p95 (target 2/4), angle 2.7-3 deg median (target .5), rIoU ~.90 (target .95). Symmetry k=4 fits the hand-drawn labels better than k=1 (angle 3-6 deg vs 17-20). Quality refine barely helps on non-rectangular objects. Training 4-8 min on dev PC.

**Why:** GT boxes are hand-drawn squares around a teardrop, so label noise probably floors accuracy at a few px / degrees; the 2 px / 0.5 deg targets may be unrealistic on this part.
**How to apply:** do not keep tuning synthetic recall; decide with the user whether to relax targets or make Quality refine work on non-rectangular objects. Still owed: UI smoke P1/S5/P6 (100/150% DPI en/vi), circle W/H vs native sample, -4 retry test, 2-core latency. See [[easyai-project-plan]].

Update 2026-10-14 later: user chose option A (relaxed thresholds for hand-drawn labels, written into tmp/EAI-POS/plan-v1.md; P7c label check: tip-vs-box angle noise median 3.8 deg). P8 f200e4fb6 (Position gates: only Rot+circle, Convert items hidden) and P9 e0a7a9215 (Detect box mode: rect/Rot-only labels train the instance head, output same-kind boxes, splits overlaps; off for V1/Retrain/InputSizeOff/polygons) committed. Still owed: real overlapping-Detect-box accuracy and a native box-mode training run, UI smoke (P1, S5, P6, P8, P9) at 100/150% DPI en/vi, Inspect verdict still mask-based (open decision in tmp/EAI-POS/issues.md), MCP ProposalStore not kind-gated, 2-core latency.

Update 2026-10-15: P10 3138cfde4 (auto angleSymmetry k=4 for near-square boxes), P11 be3cda737 (instance epochs default 300, advanced.json override) committed; New-project kind caption contrast fix 536bb1a8b. Real-data (session path, E:\EAI_TEST 7 held-out): recall/precision 1.0, all overlapping pairs separated 13/13 (old mask path merged 11/13); centre median 5.1-5.9 px, angle median 3.9-4.9 deg, rIoU .87-.88 = just under relaxed thresholds, 600 epochs only marginal (+67% train time), 300 = knee. Decision pending: accept this level (label noise floor) vs further work. Owed: replace stale BeeNativeSegAI.dll in bin\Release (2026-10-01, no instance head; user has not authorised deploy), 150% DPI smoke, PositionRefused notice, 2-core train time/latency.
