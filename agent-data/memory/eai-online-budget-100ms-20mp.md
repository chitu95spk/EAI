---
name: eai-online-budget-100ms-20mp
description: "EAI-ONL Online latency budget is 100 ms per 20 MP (5472x3648) image, not 50 ms / 5 MP"
metadata:
  node_type: memory
  type: project
  originSessionId: e5afe1e2-3ed9-4baf-94a6-2bf0d0407314
  modified: 2026-10-18T01:37:42.233Z
---

User decision 2026-10-18: EasyAI Online detect budget raised to 100 ms for a 20 MP image (5472x3648). Replaces 50 ms / 5 MP.

**Why:** the real production frames (TrainLOGONEW) are 20 MP; 50 ms was unreachable (best 65-77 ms inference only).
**How to apply:** judge latency on native 20 MP frames. Predict-only v512 iGPU p95 77 ms fits; full chain (decode, prep, transport) at 20 MP is unmeasured. The 5 MP bench frames (2592x1944) are not comparable. Real blocker is model quality (v512 recall ~0), see [[easyai-project-plan]] and [[eai-onl-status]].
