# EAI-ONL-MODE plan v1 (EAI-ONL-MODE-r1-plan-v1) - awaiting user approval

Decisions (user): full scope CC-12..16; budget 100 ms per 20 MP (5472x3648) frame; re-test only after the model is fixed.
Source design: tmp/EAI-COMMERCIAL/command-code-architecture.md sec 5 and 7. Contracts (all unapproved, no authorization yet):
.claude/task-contract.EAI-ONL-MODEL.toml, EAI-CC.toml, EAI-ONL-MODE.toml.

Facts that shape the order
- No `EasyLabel.Online` and no `EasyLabel.Commands` project exists. Online gating needs the registry (CC-1..).
- v512 and v005 both predict empty masks on TrainLOGONEW (train IoU ~0.01, recall proxy 6e-5). Nothing measured on accuracy is meaningful until fixed.
- 20 MP Predict-only (v512 iGPU p95 77 ms) fits 100 ms; full chain at 20 MP is unmeasured.

## Phase A - EAI-ONL-MODEL (first, blocks the final test; Claude-only for native trainer)
A1 dump training masks for 5 images (oriented_rectangle -> mask) and compare with labels. Codex luna trace + Claude review.
A2 check sampling / class prior / LR against train.log; DefectThreshold sweep on existing v512 (cheap, no retrain).
A3 fix the identified cause (Claude), retrain 512 (~40 min), report training-set and Val recall.
Exit: recall number on Val; user decides whether the model is good enough.

## Phase B - EAI-CC (command registry, behaviour-neutral, one commit per phase)
CC-1 EasyLabel.Commands project + tests (no app wiring). CC-2 wrap ~40 commands via WpfCommandHost. CC-3 slash commands. CC-4 hotkeys (medium risk, UIA smoke owed). CC-5 settings schema.
CC-0 (design freeze) is this document. CC-6+ (MCP attributes, XAML, router, pilot T2/T3) not in this approval.
Parallel with A: B touches different files; disjoint allow-lists, medium dispatch mode.

## Phase C - EAI-ONL-MODE (after B; final test after A)
CC-12 modes field + UI.RUNMODE.SET + gating tests. CC-13 EasyLabel.Online skeleton, recipe schema, freeze/load, golden determinism tests (offline). CC-14 local runner, bounded queue, stage metrics, OnlineBench on native 20 MP frames. CC-15 LAN endpoint, wire codec, overload policy, status UI. CC-16 pilot ONL.* commands (ONL.STOP/STATUS/THRESHOLD.SET only while running).
Hot-path assembly has no reference to AgentRunner, PilotService, Ai.Mcp or any LLM client.

## Final test (after A and C)
OnlineBench with native 5472x3648 mono, full chain, iGPU and CPU, split timing off, quiet machine; p95 against 100 ms; recall proxy on training set plus Val split; compare with v005. Colour measured separately.

## Owed / risks
- UI smoke (hotkeys, status view, 100/150% DPI, vi/en); hardware and the 2-core PC are NOT authorized.
- Online driving a reject output would be safety-adjacent: not in scope here.
- Size: B and C are many commits. Each phase ends with its own check; no phase merged with another.
- Open question for user: latency (grab-to-result) or throughput for the 100 ms budget; defaults to latency, p95.

## Authorization needed from user before dispatch
edit_code, build, run_tests, benchmark (dev PC only), update_tracking, commit on main for each of the three contracts.
