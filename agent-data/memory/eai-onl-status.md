---
name: eai-onl-status
description: "EAI-ONL (online inference <=50 ms) status: 512+iGPU accepted, full-chain re-measure pending, real NG data = E:\\Training\\TrainLOGONEW\\images"
metadata:
  node_type: memory
  type: project
  originSessionId: 27cf67d0-9844-4639-b1d5-0d6cf1b01d7c
  modified: 2026-10-17T17:03:18.220Z
---

User accepted the 512 network + Intel iGPU direction (2026-10-18) and wants the full chain (decode, host prep, infer, transport) re-measured. Real OK/NG data is `E:\Training\TrainLOGONEW\images` (126 LabelMe pairs side by side, 123 with NG); NOT the `json/` folder and NOT `E:\EAI_TEST` (OK-only).

**Why:** split-timing env (EASYAI_SEGAI_SPLIT_TIMING=1) adds ~15 ms, and load polluted P3a; quiet split-off numbers: 512 iGPU infer p95 27 ms vs 960 44.6 ms.
**How to apply:** resume with `/eresume EAI-COMMERCIAL`; handoff `tmp/EAI-COMMERCIAL/deferred.md`; never benchmark while training runs; recall on TrainLOGONEW is training-set only. See [[feedback-fewer-claude-subagents-codex-labour]].
