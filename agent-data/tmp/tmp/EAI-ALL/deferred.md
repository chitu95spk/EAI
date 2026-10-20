# Deferred work: EAI-ALL

Written in English. Resume with /eresume EAI-ALL. Append new sections; never rewrite earlier ones.

## Status 2026-10-11 (autonomous run under the merged contract .claude/task-contract.EAI-ALL.toml)

Authorization: edit_code, build, run_tests, commit = true; push, deploy, hardware, benchmark = false. Old per-ID contracts are archived unchanged in .claude/archive/EAI-ALL/. Verify script: `bash tmp/EAI-ALL/verify.sh <artifacts-subdir>` (isolated build of EasyAI.Wpf + 5 EasyLabel test projects, prints summaries). UI smokes: tmp/EAI-ALL/smoke-*.ps1, tmp/EAI-CACHE/smoke, tmp/EAI-PROJ/smoke (UIA + PrintWindow; popups need CopyFromScreen).

### Done and committed (each with build 0 errors + all 5 test projects passing; logs in artifacts/<stream>/)
- EAI-PROJ: P1-P4 (fc62f5890 = P4 UI). Smoke vi+en at 100%.
- EAI-CACHE: C1 6edee238b, C2 c4787f8f8, C4 3479af7ef, C5 36355c68f. C3 (training hash cache) NOT done on purpose: plan says measure first and benchmark is not authorized.
- EAI-AGENT: S1, S2 2bb08f134, S3 3a7f6f93f, S4 edcef8e90, S5 22194c32c, S6 98e8f7032, S7 083092ffb, S8 2aaf74cda, S9 121bd028a, S10 adac28df3. S11 (yolo_detect) deferred by plan.
- EAI-CAM: K1 111b4ed21 (Opus review done, fixes applied), K2 8db9b1ce7, K3 7a4cfa9be, K4 cc12ba32d, K5 ff47c0dee. All against FakeCameraProvider (--fake-camera); real MVS camera UNVERIFIED.

### Needs a user decision / permission (stopped here)
1. Benchmark authorization for CACHE C3a (measure training decode/encode/hash before deciding C3b) and the before/after timing table the CACHE acceptance requires.
2. 150% DPI / 900x600 smokes (PROJ, AGENT chat window, CAM tab, Settings pages): needs changing the Windows display scale (contract out_of_scope without user consent) or running on another PC.
3. Real hardware: MVS camera (K1-K5), Check/batch with a real agent (S7-S10), real third-party MCP server (S10), real-model restart smoke for C4.
4. EAI-CLS P4 hardware and P5 (live folders) need the user's explicit OK; EAI-OPEN owed items (GPU latency etc.) need idle PC/hardware; EAI-PLUS owed: 150% DPI, lossy-convert Undo, SAM2 accuracy, production-PC S5; PLUS finding: <5 images gives an empty Ultralytics val dir (guard not decided).
5. Push (nothing pushed).

### Owed smokes (not run)
vi smoke of Live tab and capture buttons; Capture to current confirmation dialog visual and result; Inspect Capture button; AGENT S4 drawing (replace/remove dashed, check/X on remove) with an agent Check run; S10 add/approve dialogs; PROJ cancel/progress of Save As with copy images.

### Known notes
- Test flake: SegAI SegAiPredictorTests native load code -5 sometimes; rerun passes. ResultDiskCacheTests Dispose now retries.
- Windows namespace trap: inside EasyLabel.* namespaces `Convert` resolves to the project's Convert namespace: use System.Convert.
- Mcp.Tests links Wpf files by hand (AgentRunFiles needs AgentSkills.cs and McpServerRegistry.cs linked).

## Update 2026-10-11 - C3a measured (benchmark authorized by the user), C3b dropped
Evidence: artifacts/EAI-CACHE/c3a/timing.md and timing.csv (synthetic 2448x2048 x60 and 640x480 x200, 16-core PC, warm-up + 3 passes; opt-in test CacheStageTimingTests, EASYAI_RUN_TIMING=1). Per frame at 2448x2048: decode 57 ms, PNG encode 101 ms, hashes ~16 ms, total ~203 ms (snapshot path). A persisted per-frame hash cache could skip only 7.8 percent (15.8 s per 1000 images) on the snapshot path, below the plan's 25 percent / 5 s worthwhile bar, and training itself takes hours: C3b NOT done by that rule. Where the cache DOES pay: image size at project open, 0.006 ms/frame from image-meta.json versus 57 ms/frame full decode (C1), about 57 s saved per 1000 large images. Caveat: synthetic frames, PNG encode time on real photos may differ; rerun with EASYAI_TIMING_IMAGES=<real folder> to confirm.
## Pause 2026-10-11 22:53 - User ran /epause during wave 1 of plan-v3

### Facts (filled by script)
- session: af114598-f691-4f69-9b15-797223752919 (started 2026-10-11 15:04)
- usage: snapshot age 12087 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: EAI-ALL, EAI-CACHE
- commits since session start (any session): 24
  - bf9ab7ec4 [EAI-AGENT] S9 follow-up: compact chat popup anchored next to the round button, tidy two-column Options, Model beside Run
  - f4a713ccc [process] EAI-ALL: benchmark authorized by the user for CACHE C3a; scope text aligned
  - 638a0883b [EAI-CACHE] C3a: opt-in stage timing test; measured, C3b not worthwhile (7.8% skippable < 25% bar)
  - 80ff558f1 [process] EAI-ALL handoff, verify/smoke scripts and stream plans
  - ff47c0dee [EAI-CAM] K5: capture from the other tabs (Label: Capture new / Capture to current with once-per-session overwrite confirmation; Inspect: Capture)
  - cc12ba32d [EAI-CAM] K4: Live camera tab (preview at <=30 fps latest-frame-wins, Start/Stop/Snapshot, Set parameter panel with supported-only rows and Auto buttons)
- files edited by this session: 8; still uncommitted: 1
  - tmp\EAI-AGENT\plan-v3.md [??]

### Task and scope
Contract EAI-ALL (.claude/task-contract.EAI-ALL.toml; edit/build/test/commit/benchmark true; push, deploy, hardware false). Active work: plan-v3 (tmp/EAI-AGENT/plan-v3.md) = user feedback F1-F11 on the agent chat popup, run as a team (Leader=lead, Planner=easyvision-architect, Coding=easyvision-implementer, Testing=lead build/tests + smoke, Review=easyvision-reviewer). Plus a NEW product-strategy request (user 2026-10-11): target = cheap local AI labelling/training for factories, simple for operators, many locally trainable models for this PC class, learn from chat data, study competitors; memory note easyai-product-target. Scope EasyLabel/** only for v3; strategy work is research + roadmap.

### Done (verified)
All of PROJ P1-P4, CACHE C1/C2/C4/C5 (+C3a measured, C3b dropped), AGENT S1-S10 + chat popup compact redesign (bf9ab7ec4), CAM K1-K5 are committed with build 0 errors and 5 test projects passing (see the earlier sections of this file and artifacts/EAI-*/). Live folder EasyAI.exe was rebuilt at 22:09 (artifacts/EAI-ALL/live, artifacts/EAI-AGENT/chat2). plan-v3 written (tmp/EAI-AGENT/plan-v3.md, UNCOMMITTED).

### Remaining (in order)
1. (start here) Finish wave 1 of plan-v3: S3 is IMPLEMENTED but unbuilt (SuggestionButtonLayout.cs, AnnotationCanvas.cs, SuggestionButtonLayoutTests.cs); S1 (duplicate guard) and S6 (model+effort pill) were STOPPED MID-EDIT: inspect git status/diff in EasyLabel (modified: Services/Agent/AgentChatRules.cs, AgentCommandLine.cs, AgentRunOptions.cs, Ai.Mcp EasyAiTools.cs, ProposalStore.cs, RunRequest.cs, tests AgentCodexTests.cs; new Core/Editing/ShapeOverlap.cs); decide per file to finish or revert only the half-applied hunks of S1/S6 (NOT the S3 files); S6 was at 'update AgentCodexTests'; AgentChatWindow.xaml pill UI may be unstarted. Then build via bash tmp/EAI-ALL/verify.sh <tag> and fix compile errors. 2. Merge the 'NEW STRINGS' blocks the agents report into Strings.resx/vi.resx/Designer in ONE edit (agents were forbidden to touch resx). 3. Waves W2 (S2 rotated boxes + 1.15 MP cap, S4 streaming backend, S7 chat session + /clear, S10 bar + confirm), W3 (S5 speed, S9 Remaining gating), W4 (S8 live transcript) exactly as in plan-v3. 4. Real-agent A/B with images from E:/EAI_TEST (small paid runs: the user gave the folder for testing the Agent; keep to about 3-5 images). 5. Strategy: re-run the market research (write tmp/EAI-STRATEGY/competitors.md) and the capability inventory, then the Planner writes tmp/EAI-STRATEGY/roadmap.md and the lead delegates.

### Decisions already made
plan-v3 D1 chat = one continuous view per project, runs stay stateless; D2 /clear wipes history file + view, no confirm; D3 parallel batch DEFERRED (rate limits, cancel/cost complexity); D4 duplicate rule IoU>=0.5 vs existing/pending shape (any label) or >=80% covered by same label; D5 Remaining enabled only after a successful agent run on the current image. Agents must not edit Strings*.resx (parallel clobber): they report NEW STRINGS blocks, lead merges. F8 root cause (not the coordinate conversion): images re-shrunk by the model API (~1.15 MP) after the tool reported a larger scale; fix = cap tool images at 1.15 MP and state the real scale. Windows trap: use System.Convert inside EasyLabel namespaces; Mcp.Tests links Wpf files by hand.

### Do NOT redo
Everything committed before this pause (S1-S10, K1-K5, C1-C5, P1-P4) was built and tested; Opus reviewed K1 interop. Do not re-review. The popup compact redesign was verified by UIA rects (popup adjacent to the button).

### In-flight agents and background work
Stopped with TaskStop at pause: S1 duplicate guard (agent a7aa7d5e6afba1c8a, partial edits in Ai.Mcp + ShapeOverlap.cs), S6 pill (agent a1e763e194da4456f, partial edits in AgentChat*/AgentRunOptions/AgentCommandLine/AgentCodexTests), market researcher (aa16a5e956050691a; had NOT written tmp/EAI-STRATEGY/competitors.md yet: rerun) and capability explorer (ad4a02ed1ad46f66c, no result: rerun). S3 agent finished (a76e1087459d76920). Transcripts are under the session tasks folder in %LOCALAPPDATA%/Temp/claude/ (do not cat: large).

### Next commands
Authorization: edit_code, build, run_tests, commit, benchmark true; push false. First: git status --short EasyLabel | grep -Ev 'bin/|obj/' ; git diff -- EasyLabel/src/EasyLabel.Ai.Mcp EasyLabel/src/EasyAI.Wpf/Services/Agent ; then bash tmp/EAI-ALL/verify.sh EAI-AGENT/w1 (build + 5 test projects). Smokes: tmp/EAI-ALL/smoke-chat2.ps1 (popup) and the other smoke-*.ps1. Commit per slice after green.

### Risks and unverified
UNVERIFIED: all wave-1 code (never compiled); S3 rotated icons visually; real agent speed/offset fixes (need paid runs on E:/EAI_TEST); 150% DPI; camera real hardware; stream-json assumption for the CLI. Other sessions' dirty files (task-contract O099/OCR/V178/V186/template, .claude/agents, skills deletions, settings.local.json) must not be touched. Known test flake: SegAiPredictorTests native load -5 (rerun).

### Acceptance still owed
plan-v3 QA checklist items 1-12 (in plan-v3.md) none proven yet; strategy deliverables (competitors.md, capability inventory, roadmap) not produced; earlier owed list (150% DPI, hardware, CLS P4/P5, OPEN/PLUS items, push) unchanged - see the 'Status 2026-10-11' section above.

## Pause 2026-10-12 08:56 - User ran /epause during wave C of the roadmap (T3-UI done, T7 stopped mid-edit)

### Facts (filled by script)
- session: af114598-f691-4f69-9b15-797223752919 (started 2026-10-11 15:04)
- usage: snapshot age 12690 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: EAI-ALL, EAI-CACHE
- commits since session start (any session): 31
  - d3e6b5326 [EAI-ROADMAP] Wave B: T2 actionable Train errors (fix buttons) + en/vi parity test, T6 learning loop from all suggestion sources + project vocabulary
  - 96194823d [EAI-ROADMAP] Wave A: T1 Train/Val auto split + guards, T3 hardware probe + recommended settings (no UI yet), T4 send model to EasyVision, T5 agent spend guard
  - 048c2b034 [EAI-AGENT] W4: S8 live agent transcript in the chat (stream events -> readable lines, status line, no progress bar) + S3b icon pairs stay attached to their box, no score text for agent suggestions
  - b0e0992fe [EAI-AGENT] W3: S5 agent speed (skills per run kind, image facts block, one-pass skills, turn cap) and S9 Remaining mode gating
  - 48d80d459 [EAI-AGENT] W2: S2 box accuracy (tool images capped at 1.15 MP with real scale, SAM2 rotated boxes, EXIF), S4 streaming backend (stream-json, line reader, --fake-agent), S7 chat session + slash commands (/clear), S10 Auto-label bar + switch confirmation
  - 2fc84a396 [EAI-AGENT] S1 + S6: duplicate guard (F1) and model+effort pill (F5, Check button removed from the row)
- files edited by this session: 13; still uncommitted: 1
  - EasyLabel\tests\EasyLabel.SegAI.Tests\EasyLabel.SegAI.Tests.csproj [M]

### Task and scope
OBSOLETE section (earlier pause 2026-10-12 morning): superseded by the "Status 2026-10-12 (late)" section and the last Pause section below. Contract EAI-ALL.

### Done (verified)
OBSOLETE - see the last Pause section.

### Remaining (in order)
OBSOLETE - see the last Pause section.

### Decisions already made
OBSOLETE - see the last Pause section.

### Do NOT redo
OBSOLETE - see the last Pause section.

### In-flight agents and background work
OBSOLETE - see the last Pause section.

### Next commands
OBSOLETE - see the last Pause section.

### Risks and unverified
OBSOLETE - see the last Pause section.

### Acceptance still owed
OBSOLETE - see the last Pause section.


## Status 2026-10-12 (late) - wave C done, T8 done
Committed (build 0 errors, 5 test projects green: SegAI 1239, Core 354, Mcp 111, Onnx 31, Camera 92): d468b4cb5 T3-UI, 9feba3dd2 T7 p1, 6caae38e4 T7 p2, 166133e78 T8 Easy mode, d4b927d28 T8 layout fix (header scroll, Simple declutter), eb00a5c59 follow-ups (step 5 Done, OK/NG counts, disk warm). Earlier TODO-lines in the 'Pause' section above are obsolete.
Smoke: tmp/EAI-ALL/smoke-t8.ps1 (artifacts/EAI-AGENT/t8/). 1280x800 en/vi pass except the physical-mouse toggle click is flaky (harness); 880x560 canvas visible again, right docks collapse to tabs there (existing behaviour).
Unverified: real Send->step 5 Done, step 4 counts when filtered, disk warm after restart (unit test only), ETA in Simple shows in step-3 text, 150% DPI, real Claude agent run, camera hardware, T3 thread priority (Segment AI trains on a pool thread, SegAiTrainingService.cs:124).
Remaining (needs user decisions, see final message): OK/NG classification (reuse frozen MobileNetV2 backbone of Segment AI) + EasyVision consumer tool (Q3: new tool + new file on ToolSegment), offline model pack installer, YOLO CPU in Advanced, traceability pack, INT8, SimpleLabelTarget=10 calibration.
## Pause 2026-10-12 11:07 - User ran /epause during EAI-OKNG review-fix wave

### Facts (filled by script)
- session: af114598-f691-4f69-9b15-797223752919 (started 2026-10-11 15:04)
- usage: snapshot age 12821 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: EAI-ALL, EAI-CACHE
- commits since session start (any session): 42
  - 88d4d7c80 [EAI-OKNG] Classifier uncertainty in the doubtful-first sort; YOLO CPU opt-in (Advanced + Settings toggle)
  - c4e6e4872 [EAI-OKNG] Item 1-2 UI: OK/NG training kind + list badges + Inspect verdicts, OK/NG export with trace record, model pack install, Simple-mode wiring
  - a95f9b256 [EAI-OKNG] Item 1 UI part 1: G/B OK/NG hotkeys with undo, ClassifyRules, 21 strings
  - 52ec0da1e [EAI-OKNG] Items 2-4 back-end: model pack installer, YOLO CPU training path, traceability record writer
  - 38f1701f9 [EAI-OKNG] Item 1 engine: OK/NG image classification (frozen MobileNetV2 features + deterministic logistic head, .eaicls model file, class-probability uncertainty)
  - eb00a5c59 [EAI-ROADMAP] T8/T7 follow-ups: step 5 Done after Send to EasyVision, OK/NG counts ignore list filter, uncertainty warm from disk cache
- files edited by this session: 15; still uncommitted: 2
  - tmp\EAI-ALL\deferred.md [M]
  - tmp\EAI-ALL\fill-handoff2.py [??]

### Task and scope
Contract EAI-ALL (.claude/task-contract.EAI-ALL.toml; edit/build/test/commit/benchmark true; push/deploy/hardware false). Current work: EAI-OKNG (plan tmp/EAI-OKNG/plan-v1.md): OK/NG whole-image classification, model pack installer, YOLO CPU (Advanced opt-in), traceability pack, all INSIDE EasyLabel/EasyAI (user 2026-10-12: skip the EasyVision ToolSegment consumer tool; new deps allowed; SimpleLabelTarget=10 kept). Scope EasyLabel/** and tools/easyai-train/**.

### Done (verified)
Committed, build 0 errors, 5 test projects green (SegAI 1300, Core 408, Mcp 111, Onnx 47, Camera 92; SegAiPredictor native load -5 is a known flake, rerun passes): T3-UI d468b4cb5, T7 9feba3dd2 + 6caae38e4, T8 Easy mode 166133e78 + d4b927d28 (layout fix) + eb00a5c59 (follow-ups), OKNG engine 38f1701f9, items 2-4 backend 52ec0da1e, UI part1 a95f9b256, UI c4e6e4872, uncertainty sort + YOLO CPU 88d4d7c80 (HEAD). Bench (this PC, 2-core pinned): train 84 imgs 12.5 s, inference 129 ms/img, peak WS 221 MB, all gates pass (artifacts/EAI-OKNG/bench/results.md); accuracy 100% is NOT trustworthy (all NG from one 5-min session) - do not show accuracy in Simple mode. T8 smoke: tmp/EAI-ALL/smoke-t8.ps1, artifacts/EAI-AGENT/t8/.

### Remaining (in order)
1. (start here) Fix the independent review findings (Opus reviewer, verdict FIX_REQUIRED) - the fix agent was stopped before editing, nothing applied: HIGH LabelStepViewModel.cs ~1283/1293 AgentBatchImage must get only the segmentation bucket not the classifier-merged value; HIGH OK/NG mark on an image without shapes creates a LabelMe JSON that UltraDatasetBuilder.cs:274-275 and UltralyticsExportService.cs:116-117 (and Segment AI dataset gather) treat as empty background -> skip zero-shape images; MEDIUM ModelPackInstaller.cs ~242-287 null manifest fields must be ManifestInvalid not throw; MEDIUM OkNgModelExporter.cs ~47/82 failed trace write leaves copied model blocking retries; MEDIUM trace should record the classifier's own training list+split+model version (save list next to .eaicls); MEDIUM post-training P(NG) pass (TrainStepViewModel.Classify.cs:155, LabelStepViewModel.Classify.cs:66-99) only when Most-doubtful toggle is on and host not busy; LOW ClassifyModelFile.Load must never throw (:127,139,221-247); LOW use OkNgModelRules.LatestPerName in the Label pass (:137); LOW dispose CTS; LOW ImageClassLoader.cs:36 lenient flags; LOW pack confirm dialog lists target folders (new key ModelPackTargetsFormat 'Folders: {0}' / 'Thu muc: {0}'). Ignore review finding #9 (BelowNormal priority) for now. Re-dispatch one easyvision-implementer with exactly this list (brief was in this session; rewrite from this list). 2. Build+test (bash tmp/EAI-ALL/verify.sh EAI-OKNG/b5), commit. 3. Re-run the OK/NG UI smoke (agent was stopped; tmp/EAI-OKNG/smoke-okng.ps1 may be partial) on a fresh build: open fixture, G/B tagging, Simple Train classifier, Inspect verdict+Confidence, Export OK/NG + trace, Settings install pack with bad-hash zip, en+vi at 1280x800 and 880x560. 4. Remaining items: INT8 spike (tools/easyai-train/quantize_cls_backbone.py, adopt only if >=1.3x faster at 2 threads with no OK/NG flips) - measure first; classification accuracy needs a hold-out from another day/session before any accuracy claim; real EasyVision load of published .segai is OUT of scope now; push not authorized.

### Decisions already made
Decisions (user 2026-10-12): skip EasyVision tool (ToolSegment) for now; new dependencies allowed (CPU torch wheel, SAM2 ONNX only when needed); SimpleLabelTarget=10; YOLO stays Advanced opt-in (Settings toggle UltraAllowCpu default false). Leader decisions: derived mobilenetv2-12-cls.onnx is NOT committed (make_cls_backbone.py + model pack; a dev copy at artifacts/EAI-OKNG/classify-backbone), NG threshold 0.5 + Doubtful band, minimum 2 images per class, operator name optional free text, measure on this PC. OK/NG tags never change segmentation 'labelled' meaning. Agents never edit Strings*.resx/Designer; lead merges via python tmp/EAI-ALL/add-strings.py <tsv> (strings tsvs in tmp/EAI-OKNG/); Designer needs manual property only for keys used via x:Static (UiMode*, EasyStripName were added).

### Do NOT redo
Reviewer confirmed fine: deterministic stratified split, leave-one-out keeps >=1 per class in train, deterministic head, threshold 0.5 used consistently, .eaicls hash refusal, atomic write/no overwrite, zip-slip + size cap guards, undo unit for G/B (train/val untouched), all 18 new string keys present in en and vi. Do not re-review those.

### In-flight agents and background work
Stopped at pause: fix-review agent a023e416effdaa0ea (no edits applied, git clean), OK/NG UI smoke agent aa77d323ec22a7761 (may have left tmp/EAI-OKNG/smoke-okng.ps1, smoke-project/, smoke-settings/ and partial screenshots in artifacts/EAI-OKNG/smoke/; a leftover EasyAI.exe was killed). No builds or tests running. Slice-A API list: tmp/EAI-OKNG/slice-a-public-api.txt.

### Next commands
git status --short -- EasyLabel tools/easyai-train | grep -Ev 'bin/|obj/' (expect clean) ; then dispatch the fix agent (list above) ; bash tmp/EAI-ALL/verify.sh EAI-OKNG/b5 ; commit with [EAI-OKNG] prefix and Co-Authored-By Claude Sonnet 5.5. Authorization: edit_code, build, run_tests, commit, benchmark true; push, deploy, hardware_test false. Do not touch other sessions' dirty files (.claude/agents, skills deletions, settings.local.json, other contracts).

### Risks and unverified
UNVERIFIED: whole OK/NG UI on the real app (smoke never completed), vi/en layout at 880x560 and 150% DPI for Train classify kind, Inspect Confidence column header binding (GridViewColumn Header binding may be blank), Export/Settings windows layout, undo after G/B auto-advance, classifier-pass concurrency on 2 cores, CPU YOLO ETA and time (unmeasured), real Claude agent run, camera hardware. Accuracy of OK/NG is confounded by session (bench). The 12-item review ranked list is above in this section.

### Acceptance still owed
EAI-OKNG acceptance owed: smoke pass of the OK/NG flow; fixed review findings; time-blocked/hold-out accuracy on data from a different day; INT8 decision; reviewer re-check of fixes; then roadmap H3 leftovers (anomaly detection deferred with the EasyVision tool).


## Status 2026-10-12 (resume) - EAI-OKNG review fixes committed 3a7de39f1
Build 0 errors; Core 408, Mcp 111, Onnx 47, Camera 92 pass; SegAI 2 fixtures fixed (shapes added), TrainValSplitTests 16 pass, full SegAI not re-run. Key ModelPackTargetsFormat added to both resx.
Smoke (tmp/EAI-OKNG/smoke-okng.ps1, now on b5 build, en 1280x800 only; log artifacts/EAI-OKNG/smoke/en-1280x800-smoke.log): PASS badges+counts, G/B tagging + auto-advance, Simple step 3 trains a classifier (okng_v001/v002.eaicls exist in smoke-run/.easyai/classify), Inspect combo lists OK/NG model, Advanced KindClassify visible. HARNESS GAPS (not app failures proven): C 'train finished' (progress controls not found/finished fast), D Inspect never had images added (Run all disabled, needs Add folder dialog), E MenuExport not found, script ended with 'Parameter is not valid' exception @56. Still unverified: Inspect verdict+Confidence column, Export OK/NG + trace, bad-hash pack install, vi, 880x560.
Next: fix harness (add Inspect images via drag/UIA dialog or preload project), find Export menu id, rerun en+vi at 1280 and 880; then INT8 spike, hold-out accuracy.

## Status 2026-10-12 (smoke on b5) 
Earlier smoke runs used the OLD b3 build (sed on line 8 did not match; fixed now, exe = b5). Harness additions: Inspect Add-folder via SendKeys (flaky: worked once on b3, InspectCount 0 on b5 runs), Export send-model selection, zero-size Shot guard.
Verified on b5 (en 1280x800): G/B tagging + auto-advance; Simple step 3 trains classifier and writes okng_v001.train.json (M3 works; trace imageCount=14 from the list, was 84 by project scan); Export OK/NG writes .eaicls + manifest.csv + trace.json + cls-source.json; Send to EasyVision blocked with reason 'EasyVision cannot read OK/NG models yet'.
Open: Inspect model combo was EMPTY at open in one b5 run (timing of RefreshModelsAsync after fast train? InspectViewModel.cs:271/325) and populated in another - check for a race. Inspect run/verdict/Confidence on b5 not seen (add-folder dialog automation flaky). C 'train finished' and F KindClassify checks are harness-detection failures (Simple has no done text control found; KindClassify offscreen). vi + 880x560 not run. Accuracy: 14 labelled images only; no hold-out.
