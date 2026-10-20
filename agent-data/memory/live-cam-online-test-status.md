---
name: live-cam-online-test-status
description: "LIVE_CAM agent-detect speed/quality results (2026-10-19), what was built, what failed, what is uncommitted"
metadata:
  node_type: memory
  type: project
  originSessionId: 4417bfe8-f59d-473e-9282-4cbf1f79c438
  modified: 2026-10-19T14:15:28.412Z
---

LIVE_CAM (MVS camera, screws pile 2448x2048): the Online mode UI/ONL.START is still NOT built; work went into making the Codex agent detect fast.

Built (all uncommitted, mixed hunks with other sessions in MainViewModel/AgentRunner/AgentCommandLine): `detect_points` tool (SAM2 per click, parallel, oversize filter), `pick_candidates` tool + `Sam2Candidates` (SAM2 auto outlines via distance-transform seeds, ~8 s warm, ~19 s cold load) + `Sam2Preview` (numbered preview + candidates.json + table in prompt), `ImageMarkdown` blob table in the prompt, SAM2 mask threshold 1.5 (`Sam2Tensors.TightThreshold`), refiner skip for source=sam2.

Measured (Codex gpt-6.x-sol, effort low): baseline 40-45 s; detect_points ~27 s (best, loose boxes); SAM2-candidates + pick ~36 s warm / 52 s cold (tight boxes, agent spends ~24 s merging head+shaft parts). x4 not reached.

Failed ideas: second click x2,y2 per object (SAM2 merged neighbours, worse); image_to_md as an agent tool (slower, pile = 4 merged blobs); SAM2 "whole object" mask level (same head/shaft split); geometric head+shaft merge (true pairs fill ~0.65-0.70, threshold 0.72 too strict, risky in piles).

**Why:** SAM2 single-point masks split a screw into head and shaft; the LLM grouping step costs as much as locating objects.
**How to apply:** keep user settings (AgentEffort high, AgentPrecision hard) restored after tests; edit settings.json only while the app is closed; test harness in tmp/LIVE-CAM-test (ui/*.ps1, cand/).
