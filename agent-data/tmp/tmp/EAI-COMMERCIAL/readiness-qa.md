# EasyAI commercial readiness (QA/Product lead, read-only audit, 2026-10-17)
Evidence: source inspection, tmp/EAI-STRATEGY/*.md, tmp/EAI-*/deferred.md, git log. No build/test run in this audit.

## 1. Scorecard (0-5)
| Area | Score | Evidence |
|---|---|---|
| Installer | 0 | No WiX/Inno/MSIX/publish profile in any csproj; runtime/python/ is a loose folder, Lib/ only README. |
| Auto-update | 0 | No Velopack/Squirrel/update check in src. |
| Licensing/activation | 0 | None in EasyAI (EasyVision's BeeLicense not shared). YOLO AGPL, SAM3/YOLO-World licence questions (roadmap Q1) open; MobileNetV2 weight source unconfirmed. |
| Crash reporting | 1 | App.xaml.cs:120 DispatcherUnhandledException logs to SettingsStore + dialog; no AppDomain/Task handlers, no report bundle/upload. |
| Logging | 1 | SettingsStore.Log + agent log.csv; no structured logger, no rotation, no support bundle. |
| Docs/help | 2 | docs/easyai/guide.md exists; deliberate "no tips text" UI policy; no in-app help, no quick-start. |
| Localisation | 4 | Strings.resx / Strings.vi.resx both 1111 entries, en/vi parity unit test (T2). |
| DPI | 2 | PerMonitorV2 manifest present; 150% DPI smoke never run (owed since EAI-PLUS), 880x560 layout partly unverified. |
| Accessibility | 1 | AutomationProperties in only 31 XAML files-count hits; no keyboard/contrast/screen-reader audit. |
| API-key security | 3 | No API keys stored (grep: none); agent uses user's own Claude/Codex CLI login. Gaps: images/prompts leave PC, per-project consent (Q5) not confirmed built; external MCP needs per-server approval (done S10). |
| Data portability | 3 | Project = folder + .easyai manifest, jsonl logs, export to YOLO/.segai, Send to EasyVision versioned. No documented import from COCO/LabelMe verified; no backup/migration test. |
| Test coverage | 3 | ~1700 [Fact/Theory]: SegAI 1164, Core 314, Mcp 91, Camera 88, Onnx 47; WPF ViewModels (LabelStep 2566 lines, Main 2474) and rendering have no UI automation; real training/parity are opt-in skips. |
| CI | 0 | No .github / pipeline; builds only by hand with MSBuild. |

## 2. Owed / unverified release blockers (deduplicated)
1. UI smoke at 100% and 150% DPI, vi+en, 880x560 (Train classify, Export/Settings, Easy mode strip, agent chat popup, suggestion icons S3/S4c, OK/NG UI).
2. 2-core i7-7600U / 8 GB timing: T3 recommended settings, train time-to-first-model, SegAI/classifier/anomaly inference, SAM2 CPU latency, safe-mode input cap "to be measured".
3. Accuracy unproven: YOLO validated with val=train; SAM2, anomaly, POS (centre 4.5 px / 3 deg misses target; label noise suspected) unmeasured on held-out data.
4. Real paid agent runs on E:/EAI_TEST (box offsets, speed, oriented boxes only 2 of 5 rotated, stream-json CLI assumption, Remaining mode no-pause cost).
5. Real-training smokes V1/V2 and training/native parity (opt-in skipped tests).
6. Real MVS camera (K1-K5 only against FakeCameraProvider); EAI-CLS P4/P5 hardware need user OK.
7. Send to EasyVision step 5 end-to-end on real EasyVision; .segai v4 reader compatibility (A2).
8. Licence decisions Q1 (YOLO Enterprise vs opt-in), SAM3/YOLO-World bundling, weights provenance.
9. Warm-disk cache after restart, undo after lossy convert, SAM2 sub-ROI validMask guess, orphan running run folders (EAI-1 items 6/7 skipped).
10. Uncommitted/unverified builds: sessions that changed code after last green build (EAI-AI S3b); push never authorised.

## 3. End-to-end test plan: NL -> prompt -> label -> train -> test
Fixture: 50-image set (E:\EAI_TEST), 3 defect/part types, 20% held-out never used in training; run on (A) 2-core/8 GB, (B) 16-core+GPU. EasyAI.exe closed, idle PC.
| Step | Procedure | Acceptance |
|---|---|---|
| NL->prompt | Operator types plain-language goal (vi and en) in agent chat | Prompt/skill chosen with 0 manual JSON edits; <=3 clicks to Run |
| Label | Agent batch, cap USD set, Economy effort, then Accept/Reject | Cost <= 15 USD per 1000 images (target; measure median from log.csv, extrapolate; fail if >25); label IoU vs gold >= 0.80 median (rotated IoU harness S0), recall >= 0.90; doubtful-first review resolves 90% of images with <=1 click each |
| Train | Easy mode, recommended settings | Click count from folder open to model <= 12; time-to-first-model on 2-core <= 45 min for 30 images (to be set after measurement); no crash, ETA within +/-30% |
| Test | Held-out set via Inspect | Segment: mask mAP50 >= 0.85 and mAP50-95 >= 0.5 held-out; OK/NG: recall NG >= 0.98, false-reject <= 5%; Position: centre <= 2 px, angle <= 1.5 deg |
| Deliver | Send to EasyVision | .segai loads in EasyVision tool, prediction matches EasyAI output within tolerance, 0 manual file copies |
| Robustness | 150% DPI, vi/en, network off (offline mode), kill app mid-train | No clipping, full offline path works, resume without data loss |
Repeat 3 runs; report median and worst. Record all in artifacts/EAI-COMMERCIAL/<run>/.

## 4. Top technical debt (EasyLabel/src)
- EasyAI.Wpf/ViewModels/LabelStepViewModel.cs (2566 lines) + LabelStepViewModel.AgentChat.cs (1115): split agent, batch, suggestion, export concerns.
- ViewModels/MainViewModel.cs (2474), CanvasViewModel.cs (1844), TrainStepViewModel.cs (1749), InspectViewModel.cs (1258), AgentsSettingsViewModel.cs (936).
- EasyLabel.SegAI/SegAiSession.cs (1994), Training/SegAiTrainingPipeline.cs (1052), Export/ExportService.cs (931).
- Dead/duplicate: EasyLabel.App/Shell/MainForm.cs (1726, legacy WinForms shell next to EasyAI.Wpf; both ship in sln) and COMPLETION_PLAN.md (WinForms plan, "no build run"); EasyLabel/build.log, test-EL-*.log, tmp/ checked into the solution folder.
- Single global error handler only (App.xaml.cs:120); no logging abstraction (SettingsStore.Log).
- Exact line numbers per duplicate not computed (no static analysis run).

## 5. Roadmap to sellable v1
M1 Trust (3-4 wk): CI (build + 5 test projects on push), installer + per-user install of runtime/python, crash/log bundle with rotation, support bundle button, licence decision Q1 and third-party notices, 150%/100% DPI + vi/en smoke, remove or quarantine MainForm legacy.
M2 Proof (3-4 wk): run section-3 plan on 2-core PC and GPU PC, fix accuracy gaps (held-out metrics, POS label noise), cost per 1000 measured and caps enforced (T5), per-project upload consent (Q5), offline mode verification, real camera and Send-to-EasyVision end-to-end.
M3 Ship (2-3 wk): activation/licence + auto-update (Velopack), quick-start/help + sample project, accessibility pass (keyboard, contrast, Automation IDs), VM split refactor of the 3 largest files behind tests, pilot with 2-3 factories, support runbook.

## Top 5 blockers
1. No installer, updater, licence/activation, or CI (all 0).
2. Held-out accuracy and 2-core time-to-first-model never measured (YOLO val=train; hardware owed).
3. 150% DPI / vi-en UI smoke never run; most UI changes unverified on screen.
4. Licensing/IP: YOLO AGPL, SAM3/YOLO-World, MobileNetV2 weights unresolved.
5. Data privacy: images leave the PC to Claude/Codex; per-project consent and cost-cap safety (batch cost per 1000 unmeasured) not proven.
