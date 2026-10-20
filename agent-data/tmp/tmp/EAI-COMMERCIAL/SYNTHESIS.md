# EasyAI commercial review - synthesis (2026-10-17)

Sources (all static reads, nothing built or measured): frontend-review.md (FE-01..21), backend-review.md (BE-01..25),
customer-operator.md (CU-01..25), customer-buyer.md (CB-01..25), command-code-architecture.md (CC-0..CC-11, sec 7 Online),
readiness-qa.md. Unresolved conflict: QA says a vi/en parity test exists; FE-09 says none for WPF resx. BE-15 (_killed flag) unverified.

## Priority 0 - cannot sell without
| # | Item | Evidence |
|---|---|---|
| P0-1 | Per-run cost cap: claude -p has no --max-budget-usd; cancel/timeout log cost 0; suggest-prompt and sub-agent unlogged; Codex no cost; log.csv deletable | BE-01..04 |
| P0-2 | Consent + offline-only switch: images are sent to Claude/Codex with no consent step | CU-01/02, CB-07, QA-5 |
| P0-3 | Held-out accuracy report (YOLO val==train; SAM2/anomaly/SegAI unmeasured on 2-core) | CB-01, QA-2 |
| P0-4 | Commercial wrapper: installer, updater, licence/activation, CI, crash reporting, log rotation | CB-03/04, BE-12/13/14, QA |
| P0-5 | OK/NG path to EasyVision (message "chua doc duoc mo hinh OK/NG"); Send-to-EasyVision unbuilt | CU-04, CB-06/13 |
| P0-6 | Licence audit: Ultralytics AGPL, YOLO-World GPL, SAM3, MobileNetV2 weights source | CB-05, QA-4 |

## Priority 1 - ease of use / cost (user's stated order: cost, accuracy, speed, ease)
- Simple mode must simplify Label step; NL prompt box is the primary control; one button "label all with this description" (FE-01/02/05).
- Plain Vietnamese errors with fix actions, no ONNX/.venv/CUDA/exit code (FE-03, CU-11/12). Jargon (epoch, Val, mAP, IoU) -> plain results (CU-03/14).
- Honest training ETA + overnight-safe mode; training checkpoint, seed, lower priority so UI does not starve on 2 cores (BE-09/10, CU-09).
- Local-first routing before any LLM call; cache key survives moved folders; no memory edits mid-batch (BE-05/06/07; CC T0/T1).
- Backup/bundle, audit trail, team sharing (CB-14/15/16).

## Priority 2 - structure (command codes)
7 unlinked addressing systems (158 RelayCommand, 51 ShortcutId, ~558 AutomationId, ~86 AppSettings, 8 slash, 11 MCP, 1111 resx).
Plan: EasyLabel.Commands project, ID = DOMAIN.VERB.OBJECT, JSON descriptor (cost C0-C3, risk R0-R4), router T0 exact / T1 VI keywords /
T2 small model sliced catalog / T3 big model; phases CC-0..CC-11 (wrap existing commands first). God classes: LabelStep 2566, Main 2474, SegAiSession 1994, Canvas 1844, Train 1749; legacy WinForms MainForm 1726.

## Online mode (<50 ms / 5 MP, 1 Gbps) - ESTIMATES ONLY
- Agent/SAM2 never in the frame path; agent output frozen into a hashed deterministic recipe (setup-only LLM).
- Transfer: raw BGR 15 MB ~120 ms (fails); Mono8 ~40 ms; JPEG ~8-12 ms. Serial totals ~60-70 ms (arch), so latency <50 ms needs ROI/binning or local endpoint.
- Backend estimate 150-300 ms/frame on 2-core i7-7600U (extrapolated, 3-6x over). Needs INT8 backbone + ROI, or a stronger LAN inference node.
- No measured 5 MP SegAI/ONNX latency exists in the repo. First task: measure p50/p95 per stage.
- User answers (2026-10-17): 50 ms = processing cycle time (CT) per image, camera exposure/readout excluded; mono first, colour if achievable.
- Still open: smallest defect size / resolution needed (decides ROI/binning); target hardware (2-core vs LAN GPU node); is the CT per-image latency or pipelined throughput.

## Roadmap (QA: Trust -> Proof -> Ship)
- M1 Trust: P0-1, P0-2, FE-01/02/03/05, error localisation, checkpoint/seed/priority.
- M2 Proof: held-out report, Online latency measurement (stage p50/p95), 2-core time-to-first-model, 100/150% DPI vi/en UI smoke, OK/NG path, licence audit.
- M3 Ship: installer/updater/licence, Send-to-EasyVision, CC-0..CC-4 command registry, Online mode MVP per measured result.
Test gates (QA, placeholders until measured): <=15 USD per 1000 images, label IoU >=0.80, <=12 clicks to a model, time-to-first-model <=45 min on 2-core.
