# Deferred work: EAI-COMMERCIAL

Written in English by /epause. Resume with /eresume EAI-COMMERCIAL. Append new sections; never rewrite earlier ones.

## Pause 2026-10-17 11:39 - User ran /epause

### Facts (filled by script)
- session: 4af13a41-e111-4d6b-a8cd-d04b9fa5e448 (started 2026-10-17 11:29)
- usage: snapshot age 20053 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: EAI-COST, EAI-ONL
- commits since session start (any session): 0
- files edited by this session: 3; still uncommitted: 2
  - .claude\task-contract.EAI-COST.toml [??]
  - .claude\task-contract.EAI-ONL.toml [??]

### Task and scope
Make EasyAI (EasyLabel/src/EasyAI.Wpf) a commercial product: review frontend/backend with a 6-role team, fix weaknesses,
priorities cost > accuracy > speed > ease of use. Flow: natural language -> agent prompt -> label -> train -> test. Add an Online
(realtime) mode: defect + position, CT < 50 ms per 5 MP image (camera time excluded), 1 Gbps, mono first then colour.
Plus restructure by command codes (DOMAIN.VERB.OBJECT). Two NEW contracts written, plan NOT approved, authorization all false:
.claude/task-contract.EAI-COST.toml (spend caps, consent, offline-only) and .claude/task-contract.EAI-ONL.toml (phase 1 measure,
phase 2 design gate, phase 3 build). Out of scope: EasyVision runtime, hardware, DLL deploy, licence/installer, BeeLicense.dll.

### Done (verified)
Static review only (nothing built, run or measured). Reports in tmp/EAI-COMMERCIAL/: SYNTHESIS.md (start here), frontend-review.md
(FE-01..21), backend-review.md (BE-01..25 + Online feasibility), customer-operator.md (CU-01..25, vi_dump.txt helper),
customer-buyer.md (CB-01..25), readiness-qa.md, command-code-architecture.md (507 lines, sec 7 Online). No source code edited.

### Remaining (in order)
1. User approves EAI-COST plan and grants edit/build/test (and commit) in its [authorization]; then dispatch easyvision-architect plan
   (first verify BE-15 `_killed` flag and the real behaviour of claude --max-budget-usd), then implementer (single writer).
2. User approves EAI-ONL phase 1; build the benchmark harness (tmp/EAI-ONL), measure p50/p95 per stage for 5 MP mono then colour,
   dev PC first; 2-core i7-7600U only with explicit authorization. Write phase-2 decision note.
3. Contract for Simple-mode Label step + localised errors (FE-01/02/03/05, CU-11/12) - not yet written.
4. Contract for training safety: checkpoint, seed, lower priority, honest ETA (BE-09/10, CU-09) - not yet written.
5. Command-code registry CC-0..CC-4 (docs: command-code-architecture.md sec 5) - contract not yet written.
6. Held-out accuracy report + OK/NG path to EasyVision + licence audit (CB-01/05/06/13) - contracts not yet written.

### Decisions already made
- User answers 2026-10-17: 50 ms = processing cycle time per image excluding camera capture; mono first, colour if achievable;
  "follow your estimates" = the working targets in EAI-ONL acceptance.performance (all still ESTIMATES until measured).
- Agent/SAM2 never in the per-frame path; agent output is frozen into a deterministic recipe (setup-only LLM).
- Contracts are English, plan_approved=false, authorization false (nothing granted yet).
- No usage gate (memory feedback-no-usage-gate); the stale snapshot above is ignored.

### Do NOT redo
The six reviews above are complete; do not re-run the team. Read SYNTHESIS.md, not the full reports. The worktree has many
unrelated dirty files from other sessions (.agents/skills, .claude/agents deleted/modified): leave them untouched.

### In-flight agents and background work
None. All six subagents completed. Output files are listed under Done.

### Next commands
- `/eresume EAI-COMMERCIAL`, then read tmp/EAI-COMMERCIAL/SYNTHESIS.md and the two contracts.
- Authorization state: build=false, run_tests=false, benchmark=false, commit=false everywhere; ask the user before any of them.
- Commit the two contract files only via `/ecommit` when the user asks; they are untracked now.

### Risks and unverified
- Every finding is from static reading; UI behaviour, DPI, vi truncation, and all Online latencies are unverified.
- Conflict: readiness-qa says a vi/en resx parity test exists; frontend-review FE-09 says none for WPF resx - check first.
- BE-15 (`_killed` sticky flag after cancel) unverified.
- Competitor prices in tmp/EAI-STRATEGY/competitors.md mostly unverified.
- Open user answers: smallest defect size/resolution; target hardware for Online (2-core local vs LAN GPU node); whether CT is
  latency or pipelined throughput.
- 5 MP mono test set: confirm one exists in E:\EAI_TEST or synthesise under tmp/EAI-ONL.

### Acceptance still owed
Everything in EAI-COST and EAI-ONL acceptance blocks (nothing implemented). Release gates from readiness-qa: held-out accuracy,
2-core time-to-first-model, 100/150% DPI vi/en UI smoke, licence audit, consent/offline proof, cost per 1000 images.

## Progress 2026-10-17 (EAI-COST implemented)
- EAI-COST plan approved + build/test/commit authorized by user. Commits: 5eb0f0164 (S1-S3 meter/ledger/budget), a8d1c9171 (S4-S6 runner watchdog, BudgetLimit, batch, ledger day cap), 81ca7d8a5 (S7-S9 CLI probe, Offline-only, consent). Plan: tmp/EAI-COST/plan.md; logs artifacts/EAI-COST/*.
- Tests at last gate (s7s9-r3, working tree): SegAI 1558 pass/12 skip/0 fail; Core 435/435; resx parity 1126 keys. Clean-tree check of commit 3: SegAI 1516 pass, Core 435.
- Owed (unverified): UI smoke consent dialog + Offline switch at 100%/150% DPI vi/en; fake-agent run log artifacts; real claude --max-budget-usd support (claude not on PATH of this PC; probe runs in app); refiner-path consent/offline/BudgetLimit hunks sit in another session's UNCOMMITTED prompt-refiner code (LabelStepViewModel.AgentChat.cs RefineChatPromptAsync, AgentRunner.RefinePromptAsync): commit them with that session's refiner; S11 disposal not done; Acknowledge() resets day accounting after tamper (decision accepted, not objected).
- Routing note: from S5 on, logic writes went to Codex gpt-6.1-sol via codex-companion task --background --write --prompt-file; verifier = haiku; hunk isolation = sonnet general-purpose with temp index.
- Remaining contracts from SYNTHESIS (not started): EAI-ONL phase 1 (needs plan approval), Simple-mode Label step/localised errors, training safety, command-code registry CC-0..4, held-out accuracy + licence audit.
## Pause 2026-10-17 16:42 - User ran /epause

### Facts (filled by script)
- session: d03765b4-41e7-4775-8181-e9ffe33ff5c0 (started 2026-10-17 12:28)
- usage: snapshot age 20355 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: EAI-PAN
- commits since session start (any session): 10
  - 81ca7d8a5 [EAI-COST] S7-S9: Offline-only switch, per-project upload consent, CLI budget-flag probe
  - 212365421 [GEAR3-CREST] Draw the T tooth scores on the left of the Area Check so the gear stays visible
  - bb83100cf [GEAR3-CREST] Match % = worst chip window against its own limit (crest bands); row-gate shown beside it
  - d8452c7ff [GEAR3-CREST] Chip band = crest row widened by Offset rows, full width of the Area Check (clipped inside it); auto teach tie-break to fewest crests
  - a8d1c9171 [EAI-COST] S4-S6: budget-enforced agent runs, BudgetLimit batch stop, ledger-backed day cap
  - 076cc9ffd [GEAR3-CREST] Auto crest teach: prefer the setting with most crests among the almost-as-stable ones (L2 OK: 5 crests)
- files edited by this session: 2; still uncommitted: 1
  - .claude\task-contract.EAI-PAN.toml [??]

### Task and scope
Parent EAI-COMMERCIAL (make EasyAI commercial). This window ran three contracts, all user-approved: EAI-COST (implemented, committed), EAI-ONL phase 1 (harness written, benchmark NOT yet measured), EAI-PAN (Hand/Pan tool + keep-zoom view position; contract written, nothing implemented). Contracts: .claude/task-contract.EAI-COST.toml (committed), EAI-ONL.toml and EAI-PAN.toml (untracked, plan approved + build/run_tests/commit true, benchmark true for ONL, dev PC only; 2-core target NOT authorized; phase 2 and 3 of ONL need separate user approval). Scope EAI-PAN: EasyLabel/src/EasyAI.Wpf/Canvas/*, ShortcutTable.cs, CanvasViewModel.cs, Strings*.resx, SegAI.Tests. Scope EAI-ONL: EasyLabel/tests/EasyLabel.OnlineBench, tmp/EAI-ONL, artifacts/EAI-ONL; src is read-only.

### Done (verified)
- EAI-COST S1-S9 committed: 5eb0f0164 (meter/ledger/budget), a8d1c9171 (runner watchdog, BudgetLimit, batch, ledger day cap), 81ca7d8a5 (CLI probe, Offline-only, consent). Last gate on working tree: SegAI 1558 pass/12 skip/0 fail, Core 435/435, resx parity 1126 keys (artifacts/EAI-COST/s7s9-r3/). Clean-tree check of commit 3: artifacts/EAI-COST/commit3/.
- EAI-COST UI smoke at 100% DPI, en+vi, PASS for Offline switch, persistence, consent dialog, decline, accept, withdraw (artifacts/EAI-COST/ui-smoke/, scripts tmp/EAI-COST/smoke/). User settings.json restored (hash equal).
- EAI-ONL: Codex wrote the harness EasyLabel/tests/EasyLabel.OnlineBench (7 C# files + README) and added it to EasyLabel/EasyLabel.sln. Verifier build of it: exit 0, 0 errors (artifacts/EAI-ONL/p1-build/build.log). Selftest and benchmark were NOT run (agent stopped by /epause).

### Remaining (in order)
1. EAI-ONL phase 1 (first): run from repo root, TEMP/TMP in tmp/EAI-ONL/tmp, nothing else heavy running:
   dotnet run --project EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64 --no-build -- --selftest --threads 1
   then -- --warmup 20 --iterations 200 --threads 1,2,4,all (25 min limit). Read artifacts/EAI-ONL/<run>/report.md; summarise p50/p95/max per stage, whether p95 < 50 ms, dominant stage, which numbers are EMULATED. Check that the model really used is sensible (candidate v002.segai, input 960x960; frames 2592x1944). Write the phase-2 decision note in tmp/EAI-ONL/phase2-note.md and ask the user to approve before phase 3. Commit harness via hunk-isolated temp index (EasyLabel.sln hunk is ours; EasyVision.sln dirty belongs to someone else, do not touch).
2. EAI-PAN: dispatch Codex gpt-6.1-sol (codex-companion.mjs task --background --write --fresh --model gpt-6.1-sol --prompt-file <brief>) to implement per the contract: Hand tool button + key H in tool strip/ShortcutTable/CanvasViewModel, left-drag pan with grab cursor, vi/en strings, and keep-view math in AnnotationCanvas.OnImageReplaced (AnnotationCanvas.cs ~150-165: today it re-centres; must keep the image-space centre as a fraction of image size, clamp for different sizes) in a small pure helper with unit tests. Brief must say: no build/git, allow-list, preserve other sessions' hunks (ShortcutTable.cs, CanvasView.xaml.cs, Strings*.resx are dirty), then haiku verifier (build EasyAI.Wpf + full SegAI.Tests + resx parity python tmp/EAI-COST/resx_parity.py), then sonnet hunk-isolation agent with temp index (recipe: tmp/EAI-COST/stage3.py), commit [EAI-PAN], then git reset -q --pathspec-from-file for committed paths on the real index.
3. EAI-COST leftovers (optional, ask user): D1 offline sentence repeated 4x on Agents page; D2 --settings-dir does not isolate agent-ledger/agent-runs; S11 disposal; refiner-path consent/offline/BudgetLimit hunks must be committed with the other session's uncommitted prompt-refiner (AgentChat.cs RefineChatPromptAsync, AgentRunner.RefinePromptAsync); fake-agent run log for kill/cancel/tamper; 150% DPI smoke (needs a way to scale; not authorized to change Windows scale).
4. Unwritten contracts from SYNTHESIS.md: Simple-mode Label + localised errors; training safety (checkpoint/seed/priority/ETA); command-code registry CC-0..4; held-out accuracy + licence audit.

### Decisions already made
- Routing (user complaint 2026-10-17): use the Codex lane. Logic writes (outside native/persistence/concurrency) go to Codex gpt-6.1-sol via codex-companion; Claude sonnet keeps persistence, concurrency, plans and gates; verifier = haiku; hunk isolation = sonnet general-purpose with a temp GIT_INDEX_FILE. After every Codex write compare git diff --numstat with the saved snapshot (tmp/EAI-*/numstat-before-*.txt).
- Ledger Acknowledge() resets day accounting after tamper (evidence file kept) - accepted, user did not object.
- Pan style: Hand tool + key H (user choice among Hand/Space-drag/scrollbars/none). Space stays Fit. Middle-button pan stays.
- ONL: 50 ms = processing CT per image excluding camera; mono first; Agent/SAM2 never in the frame path; dev PC only for phase 1.
- Commit method: never line-level staging; temp index + clean-tree test; afterwards reset the real index for committed paths without hiding stderr; a stale 0-byte .git/index.lock (11:40) was removed once.

### Do NOT redo
EAI-COST S1-S9 implementation and its builds/tests; the six reviews in tmp/EAI-COMMERCIAL (read SYNTHESIS.md only); the EAI-COST UI smoke at 100%; ONL harness build (already exit 0).

### In-flight agents and background work
Verifier ab3494c673ae994c0 (EAI-ONL build+selftest+benchmark) was stopped by TaskStop after the build passed; partial output only artifacts/EAI-ONL/p1-build/. No other agent or Codex job runs (EAI-ONL Codex task mvc6puk5 completed). The monitor tasks have expired.

### Next commands
- /eresume EAI-COMMERCIAL, then step 1 above (benchmark). Authorization: EAI-ONL build/run_tests/benchmark/commit true (dev PC only), EAI-PAN build/run_tests/commit true, no UI run authorized for EAI-PAN, no hardware, no push.
- Uncommitted by this task: .claude/task-contract.EAI-ONL.toml, .claude/task-contract.EAI-PAN.toml, EasyLabel/tests/EasyLabel.OnlineBench/, EasyLabel/EasyLabel.sln hunk. Dirty files from other sessions (GEAR3-CREST, prompt refiner, BeeCore, EasyVision.sln, etc.) must be left alone.

### Risks and unverified
- ONL numbers are all unmeasured; SegAI has no thread setter or backbone/head split so the thread sweep is EMULATED by affinity; model v002.segai is 960x960 while frames are 5 MP (check what resize it does); colour and transport (loopback) not yet seen.
- EAI-COST: consent/offline UI at 150% DPI, real claude --max-budget-usd support (claude not on PATH of this PC), fake-agent run logs, hunks in the other session's refiner code. Overlap: ShortcutTable.cs, CanvasView.xaml.cs, MainWindow*, Strings*.resx are dirty from other sessions.
- Issue ledger: tmp/EAI-COST/issues.md (I1 info rejected, I2 info open until verified - verified in s5s6-r2; can be closed).

### Acceptance still owed
EAI-ONL phase 1 report, phase 2 decision note and user approval. EAI-PAN: all acceptance (Hand tool + H, keep view position, shortcut entry, unit tests, regression) plus UI smoke if the user authorizes it. EAI-COST: items listed in Risks.
## Pause 2026-10-17 19:53 - User ran /epause

### Facts (filled by script)
- session: aee26281-caa9-4087-954b-5e20d73cdc78 (started 2026-10-17 16:49)
- usage: snapshot age 20546 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: none
- commits since session start (any session): 5
  - 879aaedc8 [EAI-ONL] Online-inference benchmark harness (EasyLabel.OnlineBench): production SegAI/ONNX stages, EMULATED CPU sweep, serial 50 ms budget; contracts EAI-ONL, EAI-PAN
  - 6d6f659ec [EAI-COST] Agents page shows the offline sentence once
  - 38acd1dda [EAI-PAN] Hand (pan) tool with key H, keep the view position when Keep previous zoom is on, Delete removes the selected shape before a stale suggestion selection
  - f2601fa00 [EAI-COMMERCIAL] LabelMeDocument.Shapes never null: a label JSON with shapes null no longer throws NullReference after detect
  - e0c07aa70 [EAI-COMMERCIAL] Ship the VC++ runtime next to EasyAI so SegmentAI loads on a PC without the Redistributable
- files edited by this session: 7; still uncommitted: 2
  - CLAUDE.md [M]
  - EasyLabel\src\EasyAI.Wpf\Canvas\CanvasView.xaml.cs [M]

### Task and scope
Parent EAI-COMMERCIAL (make EasyAI commercial). This window ran: DLL/VC++ runtime fix, NRE and Delete fixes, EAI-PAN, EAI-COST D1, EAI-ONL phase 1 + phase 3 prep. Contracts: .claude/task-contract.EAI-ONL.toml and EAI-PAN.toml (both committed in 879aaedc8). Authorization (user): build/run_tests/commit true; EAI-ONL benchmark on THIS dev PC only, GPU use approved, no 2-core PC; no push, no hardware. Native change P3c approved by the user (measurement-only, additive).

### Done (verified)
- e0c07aa70 VC++ runtime (9 DLLs, EasyLabel/Lib/vcrt) copied next to EasyAI via EasyAI.Wpf.csproj; recursive dumpbin walk found only imagehlp.dll (Windows system) missing: tmp/EAI-COMMERCIAL/vcrt/deps.md. Whether it fixes the other PC's SegmentAI DLL error is UNVERIFIED (user must copy the new Release and retry Tu dong Val).
- f2601fa00 LabelMeDocument.Shapes never null (Core 440/440).
- 38acd1dda EAI-PAN Hand tool + H + KeepCentre + Delete precedence: build 0 errors, SegAI 1566 pass/12 skip/0 fail, Core 440, resx parity PASS; HEAD clean-export build: SegAI 1527 pass (tmp/EAI-PAN/headcheck.log). Independent review: ACCEPT, 1 low finding (below).
- 6d6f659ec EAI-COST D1 offline sentence once (10 of 11 lines; the AgentPricesStatus style line is inside another session's uncommitted Prices block).
- 879aaedc8 OnlineBench harness committed. Phase 1 report artifacts/EAI-ONL/20261017T120207594Z-40b11636/report.md; decision note tmp/EAI-ONL/phase2-note.md: no config reaches p95 < 50 ms (best SegAI all-CPU 105/126 ms; 1 CPU 306/394).
- Release build 19:35 of EasyAI.Wpf (0 errors), user closed EasyAI.exe before it.
- Native P3c (UNCOMMITTED): split backbone/head timing, env EASYAI_SEGAI_SPLIT_TIMING=1, new exports SEGAI_InferGetSplitTimings / SEGAI_GetSplitTimingApiVersion. Isolated native build 0 errors (tmp/EAI-ONL3/native-build.log), staged DLL set tmp/EAI-ONL3/native-stage, InfererSmoke+SplitTiming tests 4/4 pass (tmp/EAI-ONL3/seg-test.log). Real values on v002 NOT measured yet.
- Rules added to CLAUDE.md (UNCOMMITTED): items 2c, 2d (CodeGraph lock handling and release after read) + changelog lines.

### Remaining (in order)
1. Finish EAI-ONL P3a (Codex brief tmp/EAI-ONL3/brief-bench.md, task cancelled in verifying phase): working-tree edits exist in EasyLabel/tests/EasyLabel.OnlineBench (Measurements, Options, ProductionPipelines, Program, README modified; new AccuracyProxy.cs, RoiSelection.cs). Build the harness into tmp (dotnet build ... -p:OutDir=tmp/EAI-ONL3/bin/), fix compile errors, run --selftest with --model E:\EAI_TEST\.easyai\models\v002.segai, then run --gpu both --roi all --accuracy and EASYAI_SEGAI_DIR=tmp/EAI-ONL3/native-stage plus EASYAI_SEGAI_SPLIT_TIMING=1 to get backbone/head split. Report p50/p95, recall proxy, device actually used; write tmp/EAI-ONL3/p3a-note.md.
2. Commit P3c (BeeNativeSegAI/{SegV2Core.cpp,SegV2Core.h,SegAINativeExport.cpp,SegAINativeExport.h}, EasyLabel/src/EasyLabel.SegAI/{Engine/SegAiInferer.cs,Interop/SegAiInfererNative.cs}, tests/EasyLabel.SegAI.Tests/SegAiSplitTimingTests.cs) with the hunk-isolated temp-index recipe; SegBackboneFineTune.cpp and other EasyLabel.SegAI files are other sessions' hunks: do not include. Then commit the OnlineBench changes and CLAUDE.md (2c/2d only).
3. P3b: train a 512 model (SegTrainingOptions.InputSize=512, Epochs=60, V2) offline via SegAiSession.Train like SegAiTrainingServiceTests; compare latency/recall with v002 960. Only after P3a numbers; long run. GPU = Intel OpenVINO iGPU only (inference/feature extraction); no CUDA in SegAI V2; 512 cannot resume from the 960 model.
4. Optional small fix: AnnotationCanvas.OnMouseUp Hand branch (~808-812) leaves mouse capture and _aiBoxDragging stuck when H is pressed mid-drag; fix = clear _aiBoxDragging, ReleaseMouseCapture, InvalidateVisual when !_panning.
5. User answers needed: tmp/EAI-TRAIN-SAFE/plan.md Q1-Q7 (training safety slices S1-S8). Unwritten contracts from SYNTHESIS.md: Simple-mode Label + localised errors; command-code registry CC-0..4; held-out accuracy + licence audit.

### Decisions already made
- ONL phase 3 order approved by user: 1) 512 network, 2) backbone/head split (measure first, cache only if numbers justify; live frames differ so a feature cache rarely hits), 3) measure on this PC with GPU, not the 2-core PC. Counter is measurement-only and off by default.
- CodeGraph: user approved deleting .codegraph/codegraph.db; codegraph.json at repo root excludes tmp/, artifacts/, bin/, obj/, ThirdParty/, worktrees. Routing: Codex lane for logic writes, Claude sonnet for native/persistence/plans; commits via temp index.

### Do NOT redo
EAI-PAN implementation/review, LabelMeDocument fix, VC runtime packaging and dependency walk, phase-1 benchmark (report above), the read-only maps of network side, backbone/head and training safety (summaries in this file and tmp/EAI-TRAIN-SAFE/plan.md).

### In-flight agents and background work
None running. Codex P3a task-mvce00ts-68ri8k cancelled (partial edits kept in OnlineBench). CodeGraph: `codegraph init` failed 3 times with "database is locked" because the harness respawns `codegraph serve --mcp` for this project; .codegraph/ currently holds an EMPTY 172 KB db. User must run `codegraph init` in a separate terminal with this Claude session closed, or the next session uses Grep/Read ("CodeGraph: unavailable").

### Next commands
- /eresume EAI-COMMERCIAL, then step 1 above. Build to tmp OutDir only (never bin/Release while EasyAI.exe may run). Native builds only with /p:EasyVisionIsolatedVerification=true via powershell (bash MSBuild call fails with MSB1008). Authorization: build/run_tests/commit true, benchmark dev PC only, no push, no UI run authorized, BeeLicense.dll never rebuilt.

### Risks and unverified
- UI smoke owed: Hand tool/H/pan, Delete after detect, Agents page offline text, 150% DPI; DLL fix on the other PC; the NRE fix is a suspected cause only (no stack trace seen).
- Overlap: CanvasView.xaml.cs (FitSpace hunk), ShortcutTable.cs, Strings*.resx, ShortcutTableTests.cs, AgentsSettingsView.xaml (Prices block + 1 style line), AgentsSettingsViewModel.cs, SegBackboneFineTune.cpp, SegAiPredictor/SegAiSession/SegProjectStore etc. are other sessions' uncommitted hunks.
- Benchmark numbers overlapped other load (about +-20%), all thread sweeps EMULATED.

### Acceptance still owed
EAI-ONL: p3a note, split timings with real values, 512 comparison, user decision on budget. EAI-PAN: UI smoke. EAI-COST leftovers D2 (--settings-dir does not isolate ledger), S11 disposal, fake-agent run log, 150% DPI.

## Progress 2026-10-17 23:40 - EAI-ONL P3a/P3b done
- Commits: 6e74c53f1 native split timing (reviewer ACCEPT), dfba3ba39 OnlineBench P3a, ee95a5be4 CLAUDE.md 2c/2d/1b, 2bd847932 Hand tool OnMouseUp fix. Train512Bench harness committed after this note.
- Results: tmp/EAI-ONL3/p3a-note.md (P3a split + P3b 512). Backbone dominates, head ~0; 512 model 2x faster; accuracy not measurable (no NG labels in E:\EAI_TEST).
- Pending: Codex trace of the ~30 ms non-backbone cost (tmp/EAI-ONL3/nonbackbone-trace.md); user decision on 50 ms budget and on NG-labelled data; optional channel check in SplitBackbone (reviewer Low 2).
- Run native/train with EASYAI_SEGAI_DIR=tmp/EAI-ONL3/native-stage (Models/DefectV2 copied in). Dispatch mode: medium.
- Codex trace: split timing inflates P3a infer by ~15 ms (extra backbone run); real latency = P3b numbers. Non-backbone fixes ranked in tmp/EAI-ONL3/nonbackbone-trace.md (managed postprocessing 19-22 ms outside infer is the biggest).
## Pause 2026-10-18 00:02 - User ran /epause

### Facts (filled by script)
- session: aee26281-caa9-4087-954b-5e20d73cdc78 (started 2026-10-17 16:49)
- usage: snapshot age 20796 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: none
- commits since session start (any session): 10
  - dfe8d9205 [EAI-ONL] Train512Bench: fresh 512 train + latency/mask comparison vs v002 (P3b)
  - 2bd847932 [EAI-PAN] Hand tool: mouse-up clears stuck drag state and mouse capture when H pressed mid-drag
  - ee95a5be4 [process] CLAUDE.md: CodeGraph lock rules (2c/2d), Codex-first delegation (1b)
  - dfba3ba39 [EAI-ONL] OnlineBench P3a: GPU/ROI modes, accuracy proxy (soft-fail), backbone/head split rows
  - 6e74c53f1 [EAI-ONL] Native backbone/head split timing (env EASYAI_SEGAI_SPLIT_TIMING=1), additive exports
  - 879aaedc8 [EAI-ONL] Online-inference benchmark harness (EasyLabel.OnlineBench): production SegAI/ONNX stages, EMULATED CPU sweep, serial 50 ms budget; contracts EAI-ONL, EAI-PAN
- files edited by this session: 8; still uncommitted: 1
  - EasyLabel\src\EasyAI.Wpf\Canvas\CanvasView.xaml.cs [M]

### Task and scope
Parent EAI-COMMERCIAL (make EasyAI commercial); this window: sub-task EAI-ONL (online inference <= 50 ms) phase 3 on dataset E:\Training\TrainLOGONEW\images (126 LabelMe pairs side by side, 123 with NG, oriented_rectangle, 5472x3648; classes NG/OK). Contracts: .claude/task-contract.EAI-ONL.toml, EAI-PAN.toml (committed). Authorization: build/run_tests/commit true; benchmark on THIS dev PC only (i7-1360P, iGPU OpenVINO ok); no push, no hardware, no UI run. User decision this window: ACCEPT 512 network + iGPU, re-measure the FULL chain. Dispatch mode medium (5 agents); Codex does coding/testing labour, Claude does hard work (CLAUDE.md 1b).

### Done (verified)
- Commits: 6e74c53f1 native split timing (reviewer ACCEPT, no High/Med); dfba3ba39 OnlineBench P3a; ee95a5be4 CLAUDE.md 2c/2d/1b; 2bd847932 Hand tool OnMouseUp fix (WPF build 0 err; UI smoke owed); dfe8d9205 Train512Bench harness.
- P3a/P3b numbers and correction: tmp/EAI-ONL3/p3a-note.md. EAI_TEST 512 vs 960 (split OFF, quiet): CPU p50 29.8 vs 67.7 ms, iGPU 20.6 vs 37.2 ms (tmp/EAI-ONL3/train512b/compare.md). Split-timing ON adds ~15 ms (extra backbone run), so P3a infer numbers are inflated.
- Non-backbone cost ranking (Codex, measured): tmp/EAI-ONL3/nonbackbone-trace.md (managed postprocessing outside infer 19-22 ms is the biggest).
- Dataset note: E:\EAI_TEST has only OK labels (no accuracy possible); the user's real NG data is E:\Training\TrainLOGONEW\images (NOT the json/ folder, which is an old 19-file set).

### Remaining (in order)
1. FIRST: when the background train finishes, read tmp/EAI-ONL4/train512-full/compare.md (+ train.log). Model: tmp/EAI-ONL4/train512-full/v512.segai (if absent, rerun train: see Next commands).
2. Build is already done: tmp/EAI-ONL4/bin (OnlineBench with --dataset, Codex edits UNCOMMITTED in EasyLabel/tests/EasyLabel.OnlineBench: AccuracyProxy, Frames, Measurements, ModelFinder, Options, Program, README). Run the FULL-CHAIN measurement with v512 on the iGPU, split timing OFF (env var unset), machine quiet, no training running: --dataset E:\Training\TrainLOGONEW\images --model <v512> --gpu on --roi all --accuracy --threads all,4. Report serial budget p95 (<50 ms?) and the TRAINING-SET recall proxy (not held-out), compare with the project's v005 (960) via Train512Bench compare.md.
3. Commit the OnlineBench --dataset change after step 2 (hunk-clean files, temp index, then ALWAYS git reset real index; rm .git/index.lock only if empty and no git process).
4. If p95 still > 50 ms: optimise managed postprocessing (Codex) and native tail (Claude): reuse buffers, no full-frame materialisation for small ROI, cache OpenVINO device name; each needs its own contract (native = Claude only).
5. Optional: SplitBackbone channel check (reviewer Low 2); EAI-COST D2/S11 (other sessions' hunks); user answers for tmp/EAI-TRAIN-SAFE/plan.md Q1-Q7.

### Decisions already made
- ACCEPTED by user: 512 network + iGPU is the direction; measure full chain on this PC, not the 2-core PC.
- Do not benchmark while training/other load runs (P3a was polluted by load); split timing only for ratios.
- Accuracy numbers on TrainLOGONEW are training-set proxy (model trained on same images); say so.

### Do NOT redo
EAI-PAN implementation/review, LabelMeDocument fix, VC runtime, P3c native review, P3b on EAI_TEST (done), the 18-image staging tmp/EAI-ONL4/dataset (WRONG source, ignore; stale run tmp/EAI-ONL4/train512 was killed).

### In-flight agents and background work
- RUNNING background (not stopped, no token cost): EasyLabel.Train512Bench.exe training 512 x 60 epochs on E:\Training\TrainLOGONEW\images, was at 77 %; log tmp/EAI-ONL4/t512-full.log, out tmp/EAI-ONL4/train512-full (has run.lock until done). If the session ended and the process died: rerun with the command below into a NEW --out dir (the harness refuses a non-empty out).
- Codex agents all finished. No Claude subagent running.

### Next commands
- Train (if needed): cd repo root; $env:EASYAI_SEGAI_DIR=tmp\EAI-ONL3\native-stage (has Models\DefectV2 copied in); tmp\EAI-ONL3\t512bin\EasyLabel.Train512Bench.exe --dataset "E:\Training\TrainLOGONEW\images" --out tmp\EAI-ONL4\train512-full2 --epochs 60 --size 512 --compare "E:\Training\TrainLOGONEW\LOGO_SegMent.easyai.data\models\v005.segai"
- Bench: dotnet tmp\EAI-ONL4\bin\EasyLabel.OnlineBench.dll --dataset ... --model ... (see README in OnlineBench). Set TEMP/TMP to the project tmp. Authorization as above.

### Risks and unverified
- UI smoke owed: Hand tool/H/pan, Delete after detect, Agents page offline text, 150% DPI; DLL fix on the other PC unverified.
- Not yet measured: full chain with v512 (decode + host prep + infer + transport); real recall on NG data; whether instance head was trained and its cost.
- Overlap: SegV2Core.cpp still has 10 EAI-LOGO hunks of another session; CanvasView.xaml.cs, ShortcutTable.cs, Strings*.resx, AgentsSettings* etc. are other sessions' hunks. Worktree has 800+ dirty entries from other sessions: never git add . or stash.
- CodeGraph: .codegraph db was empty/locked earlier; use Grep/Read if unavailable.

### Acceptance still owed
EAI-ONL: full-chain p95 < 50 ms with v512 on iGPU, recall proxy on TrainLOGONEW, user decision on budget. EAI-PAN: UI smoke. EAI-COST leftovers D2, S11.


## Update after pause (train finished)
- Train 512 on TrainLOGONEW/images DONE: 39 min 45 s, success, 97 train / 29 val; model tmp/EAI-ONL4/train512-full/v512.segai; compare tmp/EAI-ONL4/train512-full/compare.md. No process left running.
- Predict p50/p95 ms on 5472x3648 frames, split OFF: CPU v005(960) 116.1/151.1 -> v512 79.1/97.2; iGPU v005 69.8/81.9 -> v512 64.9/77.3. NOT under 50 ms on this larger frame size (EAI_TEST frames were 2592x1944); gain from 512 is small on iGPU here, so native prep/upsample/full-frame mask cost at 5472x3648 dominates (see nonbackbone-trace.md).
- Mask agreement v512 vs v005 is poor (pooled IoU 0.028; 97/126 images both-empty): recall vs ground truth NOT yet measured. Remaining step 2 (OnlineBench --accuracy with --dataset ...images, --model v512) is now the next command; also run it for v005 to get the real recall of both.

## Resume 2026-10-18 - full-chain v512 measured
- Run: artifacts/EAI-ONL/20261018T010305912Z-63a82914 (log tmp/EAI-ONL4/fullchain-v512.log), iGPU, split OFF, --threads all,4, TrainLOGONEW images.
- Latency (threads all, whole frame 2592x1944 bench frame): mono serial_processing p50/p95 64.0/71.1 ms; colour 128.6/165.2 ms. NOT < 50 ms.
- Recall proxy on training set is ~0: 320 rows, mean 6e-5, 318 zero; only 2 whole-frame predictions with any NG pixels (max recall 0.01); ROI 512/960/half all 0. v512 effectively detects nothing on its own training images -> model quality problem (train config / label rasterisation / threshold), not latency, is the blocker.
- Not done: v005 accuracy run for comparison; commit of OnlineBench --dataset change (step 3, still uncommitted); step 4 optimisation (pointless until model detects).
- Next: diagnose why v512 predicts empty (train.log, val metrics, threshold, oriented_rectangle -> mask rasterisation); run v005 --accuracy for baseline.
- Diagnosis 2026-10-18 (static, no new runs): v512 training never learned. train.log: train_iou 0 at epochs 4-12, ~0.012 at epoch 60 (val 0.004), train_loss plateau 0.062. Not a size issue: 183 NG oriented_rectangle, sqrt-area median 376 px / min 81 px on 5472x3648 (~35 px at 512). v005 (960) also gives empty masks on 97/126 images, so the V2 trainer/label path is suspect for both.
- Next candidates: (1) verify oriented_rectangle -> mask rasterisation reaches the trainer (dump a training mask for one image); (2) check foreground sampling / class prior (see memory segai-v2-prior-shift); (3) lower DefectThreshold in a bench run to see if scores exist below 0.5. Trainer is native/safety-adjacent: needs its own contract, Claude only.

## Decision 2026-10-18 (user): Online budget raised to 100 ms per 20 MP image
- Replaces the 50 ms / 5 MP budget for EAI-ONL. 20 MP = 5472x3648 (the TrainLOGONEW frame size).
- Existing evidence on 20 MP frames (P3b, Predict only, split OFF, v512): CPU p50/p95 79.1/97.2 ms, iGPU 64.9/77.3 ms; v005(960) iGPU 69.8/81.9, CPU 116/151. So v512 on iGPU is inside 100 ms for inference alone; v512 CPU is borderline.
- NOT yet measured at 20 MP: full chain (decode + host prep + transport). The OnlineBench full-chain run used 2592x1944 (5 MP) tiled frames, so its 71/165 ms p95 is not comparable to the new budget. Need a 20 MP full-chain run (Frames.cs must use the native 5472x3648 frame, mono first).
- Still blocked by model quality: v512 recall proxy ~0 (see Diagnosis above). Budget change does not fix that.

## Progress 2026-10-18 - EAI-ONL-MODEL A1/A2 (plan EAI-ONL-MODE-r1-plan-v1 approved, all 3 contracts authorised: build/test/commit/benchmark dev PC)
- Contracts: .claude/task-contract.EAI-ONL-MODEL.toml, EAI-CC.toml, EAI-ONL-MODE.toml (approved, authorization set). Dispatch mode max. Plan: tmp/EAI-ONL-MODE/plan.md. Codex companion runs ONE job at a time per workspace.
- A2 done: tmp/EAI-ONL-MODEL/ThresholdSweep (Codex wrote, built 0 errors, artifacts/EAI-ONL-MODEL/sweep-build). First 40 NG images, CPU. v512: mean recall 0.0019 at EVERY threshold 0.05..0.5 (identical), 1/40 images any prediction, max probability inside NG labels 0.52 in one image, 0 elsewhere, outside 0. v005: recall 0.0004, 4/40 images with predictions, inside max .52, outside max .56. So not a threshold issue: both models output ~0 probability even on labelled NG of their own training images.
- A1 (static): LabelMe oriented_rectangle -> Rot region -> FillPoly is correct; labelled NG pixel counts plausible; overlay views tmp/EAI-ONL-MODEL/view/*.png show labels aligned. Defects are subtle (small white spot on bottle ~100 px, text scuffs) on 5472x3648 frames where the bottle fills a minor part -> at 512 net the defect is ~8-35 px: task is hard, 97 train images.
- Hypothesis for A3 (not yet tested): whole-frame downscale kills the signal; try tiled/cropped training (crop around product or tiles at native scale) and a larger network side; also check class-balance (Gather fg cap) with foreground sweep. Needs user choice before the 40-min retrains.
- CC-1 (EAI-CC): Codex agent a55a7806496ddaaa4 resumed with decision: lexical CommandId grammar only + separate CommandVerbs helper. Awaiting result; then Claude builds/tests/commits.
- 2026-10-18 commits: 3a2612f8e CC-1 (133 tests), b6909dfa5 CC-2 (43 wrapped cmds, skipped IMG.TAG AGT.MODEL.SET AGT.MODE.SET AGT.FORGET SET.GET/SET/RESET EXP.SEND.EV SYS.CACHE.CLEAR), CC-12 run-mode gating + UI.RUNMODE.SET + inert ONL.* (249 tests, Wpf build 0 err). Next: CC-13 EasyLabel.Online skeleton + recipe schema/freeze/load; CC-3..CC-5 touch dirty shared files (AgentSlash*, ShortcutTable, AppSettings) - need per-hunk care. Phase A waits for user choice (tile training vs bigger net vs foreground check).
- CC-12 committed 83c12457c (249 tests, Wpf build 0 err). git index.lock was stale-empty twice (removed after checking no writing git process). 7 git.exe running = IDE/other sessions, read-only.
- 2026-10-18 CC-13 committed 75ef114f7 (EasyLabel.Online, 127 tests). The commit printed "Out of diskspace" from auto gc but the commit objects are intact (checked). E: had 0 GB free; user chose to delete old artifacts: removed artifacts/V132 (23 GB) and artifacts/O062 (4.5 GB), both untracked; E: now ~28 GB free. Watch E: space before trainings/benchmarks (artifacts/EAI 59 GB, EAI-AGENT 15, EAI-LOGO 15, tmp/V116-deep-seg-pilot 7 are other sessions').
- Phase A tile experiment (tmp/EAI-ONL-MODEL/make_tiles.py 1536 -> tmp/EAI-ONL-MODEL/tiles, 158 tiles/224 NG, 127 train 31 val; Train512Bench size 512, 60 ep, 2m15s): train_iou 0.18 val_iou 0.25 (whole-frame was 0.012/0.004). Model tmp/EAI-ONL-MODEL/train-tiles512/v512.segai. Sweep on first 40 tiles (train+val mix): recall 0.22, 22/40 tiles with prediction (whole-frame model: 0.002, 1/40). Threshold still irrelevant. Confirms hypothesis: whole-frame downscale killed the signal. Not yet held-out recall (need Val-only sweep) and not yet production-path (online mode would need tiling at inference: cost x tiles at 20 MP).
## Pause 2026-10-18 10:01 - User asked to pause, limit near

### Facts (filled by script)
- session: e5afe1e2-3ed9-4baf-94a6-2bf0d0407314 (started 2026-10-18 08:02)
- usage: snapshot age 21394 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: none
- commits since session start (any session): 4
  - 75ef114f7 [EAI-ONL-MODE] CC-13: EasyLabel.Online recipe schema, verdict evaluator, freeze/load with hash gate + 127 tests
  - 83c12457c [EAI-ONL-MODE] CC-12: run-mode gating (simple/advanced/online), UI.RUNMODE.SET, 14 inert ONL.* descriptors
  - b6909dfa5 [EAI-CC] CC-2: scanner, catalog writer, 43 wrapped VM commands via WpfCommandHost (behaviour-neutral)
  - 3a2612f8e [EAI-CC] CC-1: EasyLabel.Commands registry, invoker, policy matrix + 133 tests (no app wiring)
- files edited by this session: 2; still uncommitted: 0

### Task and scope
Parent EAI-COMMERCIAL. Plan EAI-ONL-MODE-r1-plan-v1 (tmp/EAI-ONL-MODE/plan.md) approved by user 2026-10-18 with 3 contracts: EAI-ONL-MODEL (fix SegAI training that predicts empty masks), EAI-CC (command registry CC-1..CC-5), EAI-ONL-MODE (Online run mode CC-12..16). Budget decision: 100 ms per 20 MP (5472x3648) frame. Authorization: edit, build, run_tests, benchmark (dev PC only), commit on main, tracking; no push, no hardware, no 2-core PC. Dispatch mode max. Final re-test only after the model is fixed.

### Done (verified)
Commits: 3a2612f8e CC-1 (133 tests), b6909dfa5 CC-2 (43 wrapped cmds, 143 tests, EasyAI.Wpf build 0 err), 83c12457c CC-12 (run-mode gating + UI.RUNMODE.SET + 14 inert ONL.*, 249 tests), 75ef114f7 CC-13 (EasyLabel.Online recipe/evaluator/store, 127 tests). Logs: artifacts/EAI-CC/cc1,cc2; artifacts/EAI-ONL-MODE/cc12,cc13. Phase A evidence: tmp/EAI-ONL-MODEL/sweep-v512.csv, sweep-v005.csv, sweep-tiles.csv, view/*.png, make_tiles.py, tiles/, train-tiles512/ (v512.segai, compare.md). Findings: whole-frame 512/960 models output ~0 probability (recall 0.002/0.0004, threshold irrelevant); labels/rasterisation correct; TILE training (1536 px crops, 158 tiles) learns: train IoU 0.18, val IoU 0.25, recall 0.22 on first 40 tiles, 22/40 tiles predicted.

### Remaining (in order)
1) Phase A: held-out recall on the 31 Val tiles only (sweep currently takes the first 40 tiles = train+val mix; add a --val-only or filter by flags.val); try larger tile / network side / foreground sweep to raise recall; measure tiled inference cost for one 20 MP frame (tiles per frame x ms) against 100 ms. 2) CC-14 local runner + stage metrics + OnlineBench on native 5472x3648 mono frames (bench currently tiles an NG image into 2592x1944, not comparable). 3) CC-15 LAN endpoint + overload policy + status UI (100/150% DPI). 4) CC-16 pilot ONL.* (ONL.STOP/STATUS/THRESHOLD.SET only while running). 5) CC-3 slash, CC-4 hotkeys, CC-5 settings: touch other sessions' dirty hunks (AgentSlash*, ShortcutTable, AppSettings): per-hunk staging. 6) Final test: full-chain 20 MP, recall on Val, vs v005.

### Decisions already made
User chose full scope and re-test only after the model is fixed. CommandId = lexical grammar only (verb list is a separate CommandVerbs helper). Online mode: Agent/SAM2 never in the frame path (recipe frozen from agent output). Tile training chosen as the first model experiment because whole-frame downscale killed the defect signal. User authorised deleting old artifacts: artifacts/V132 and artifacts/O062 removed (untracked).

### Do NOT redo
Do NOT redo: threshold sweeps (irrelevant), label rasterisation check, v512/v005 whole-frame full-chain (71/165 ms p95 on a 5 MP bench frame), CC-1/2/12/13 (tested, committed), the E: cleanup.

### In-flight agents and background work
No agents running; all Codex jobs finished (Codex companion allows ONE job at a time per workspace). No background training.

### Next commands
Sweep: cd repo root; EASYAI_SEGAI_DIR=tmp/EAI-ONL3/native-stage; dotnet artifacts/EAI-ONL-MODEL/sweep-build/bin/ThresholdSweep.dll --model <segai> --dataset <dir> --out <csv> (set TEMP/TMP to project tmp). Tile train: tmp/EAI-ONL3/t512bin/EasyLabel.Train512Bench.exe --dataset tmp/EAI-ONL-MODEL/tiles --out <NEW dir> --epochs 60 --size 512 --compare E:/Training/TrainLOGONEW/LOGO_SegMent.easyai.data/models/v005.segai. Tests: dotnet test EasyLabel/tests/EasyLabel.Commands.Tests / EasyLabel.Online.Tests -c Release; build Wpf: dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release (Any CPU). Build/test/commit authorised. If git index.lock is empty and old with no writing git process, rm it.

### Risks and unverified
Disk: E: had 0 GB free; now ~28 GB; check before trainings. Tile model recall is not held-out and tiling at inference may break the 100 ms budget. UI smoke owed (hotkeys, status view, DPI). Hardware/2-core not measured. Uncommitted by me: tmp/EAI-ONL-MODEL scripts only (tmp is untracked); OnlineBench --dataset hunks (EasyLabel/tests/EasyLabel.OnlineBench/*) from the earlier window are still uncommitted. Other sessions' dirty files: never git add . or stash.

### Acceptance still owed
EAI-ONL-MODEL: held-out recall number, model decision. EAI-CC: CC-3..CC-5. EAI-ONL-MODE: CC-14..CC-16, final 20 MP full-chain p95 under 100 ms, recall on Val. Uncommitted: OnlineBench --dataset change.


## Update 2026-10-18 (second pause, after /ebuild)
- /ebuild done: EasyAI.Wpf Release Any CPU build exit 0, 0 errors, 76 unique warnings (baseline not compared); log artifacts/EAI-ONL-MODE/ebuild/wpf-build.log; exe EasyLabel\src\EasyAI.Wpf\bin\Release\net9.0-windows\EasyAI.exe.
- HANDED TO CODEX (hand work, runs in background, no tokens from Claude): job task-mvdfjmsz-4i9mz3 (gpt-6.1-sol, --write), brief tmp/EAI-ONL-MODEL/codex/brief.md, started via codex-companion directly (the Claude relay agent hit the session limit and died; limit resets 13:00 Asia/Ho_Chi_Minh). Steps: val-only held-out recall of the tile model; tile size 1024/1536/2048 at net 512; net 768; tiled inference cost for one 20 MP frame; writes tmp/EAI-ONL-MODEL/codex/results.md. Authorised for Codex: dotnet build/test + listed harnesses + python inside tmp/EAI-ONL-MODEL; no git, no product edits.
- FIRST on resume: node C:/Users/chitu/.claude/plugins/cache/openai-codex/codex/1.0.6/scripts/codex-companion.mjs status ; then `result task-mvdfjmsz-4i9mz3`; read tmp/EAI-ONL-MODEL/codex/results.md (do not trust its prose: verify numbers in the CSV/logs it names); `git status --short` to confirm Codex touched no product files (git diff --stat vs allow-list tmp/EAI-ONL-MODEL only). Check E: free space (>10 GB). If the job died, rerun with the same prompt.txt.

## Update 2026-10-18 (resume, user relabelling training folder -> Phase A tests skipped)
- CC-14 runner part committed: OnlineRunner (bounded queue, DropNewest/DropOldest), OnlineMetrics (p50/p95/p99), RunnerTests; Online.Tests 133 pass; log artifacts/EAI-ONL-MODE/cc14/test.log. Still owed in CC-14: OnlineBench on native 20 MP frames (needs model; deferred with Phase A).
- Next without training: CC-15 (LAN endpoint + wire codec + status UI), CC-16, CC-3..CC-5 (dirty shared files, per-hunk care). Phase A (val recall, tile size) resumes only after user finishes relabelling.

- CC-15a committed: WireCodec (EOL1 raw Mono8), OnlineEndpoint (IP allow-list, 32B token, Busy on overload), Online.Tests 139 pass (artifacts/EAI-ONL-MODE/cc14/test15.log). Owed: CC-15b status UI in EasyAI.Wpf (100/150% DPI smoke), CC-16.

- CC-16 committed: IOnlineControl + 3 live ONL commands (default stays reserved), OnlineSession (SetThreshold swaps evaluator, StatusJson), runner.SwapEvaluator. Online.Tests 142, Commands.Tests 255, Wpf build 0 err (artifacts/EAI-ONL-MODE/cc14/test16-*.log, wpf16.log). Owed: WPF adapter wiring IOnlineControl to OnlineSession + CC-15b status UI (dirty WPF files), CC-3..CC-5.

## Update 2026-10-18 (CC-15b committed, handoff written)
- CC-15b committed 6ebcc801 (adapter + OnlineStatusWindow, Wpf build 0 err, log artifacts/EAI-ONL-MODE/cc14/wpf15b.log). UI not rendered, nothing wires the adapter/window yet (needs dirty MainWindow/MainViewModel/WpfCommandHost).
- User decision: NO more dispatch by Claude for test/summary work; Claude wrote tmp/EAI-ONL-MODE/codex/HANDOFF.md and Codex lead splits tasks T1-T7 among its own subagents. T7 (Phase A) blocked until user finishes relabelling E:\Training\TrainLOGONEW.

## Update 2026-10-18 (CC-17 dispatched)
- Codex task task-mvdidyw5-6zr0qz running brief tmp/EAI-ONL-MODE/codex/cc17-brief.md (A endpoint hardening, B Wpf.Tests, C window follow-ups). Claude reviewer a5c6a7439b8d20948: OnlineBench synthetic-frame ACCEPT; Wpf status UI FIX_REQUIRED.
- After Codex finishes: (1) remove Unloaded="OnClosed" from Views/OnlineStatusWindow.xaml (keep Closed only); (2) OnlineStatusViewModel.Stop must not block UI (runner.Stop joins and drains queue): run on Task; (3) review Package A line by line, run Online.Tests, then commit T4 tests + A + OnlineBench (baseline+T6) in separate commits.
- T7/Phase A still blocked on user relabelling E:\Training\TrainLOGONEW.

## Update 2026-10-18 (CC-17 done)
- Commits: bcc5c979 CC-17a endpoint hardening (Online.Tests 154), 23375739 CC-17b status window follow-ups + EasyAI.Wpf.Tests (12), CC-14b OnlineBench synthetic frame (build 0 err, smoke ok). Logs artifacts/EAI-ONL-MODE/cc17/. Codex results: tmp/EAI-ONL-MODE/codex/results-CC17-*.md and results-T1..T7.md.
- Still owed: native 144 DPI + real UIA smoke; wiring adapter/window into MainWindow/MainViewModel/WpfCommandHost + ONL.START (dirty shared files, needs user scope); CC-3..CC-5; T7/Phase A after user finishes relabelling; final 20MP p95 run.

## Update 2026-10-18 (CC-3 started)
- User chose: edit dirty files and commit per hunk. Snapshots of working files before CC-3 in tmp/EAI-CC/cc3-before/. Codex job running brief tmp/EAI-CC/cc3-brief.md (results tmp/EAI-CC/results-CC3.md). Staging plan: diff snapshot vs working tree = Codex-only hunks; apply that patch to the HEAD blob and git update-index (never git add on dirty files); verify patch applies cleanly and compile from HEAD blobs if possible.

## Update 2026-10-18 (CC-3 committed 709c5301)
- CC-3 done: 8 AGT.* aliases via registry, generated /help, Commands.Tests 275, SegAI AgentSlash/Learning 49, working-tree Wpf build 0 err (artifacts/EAI-CC/cc3/). AgentChat.cs committed via blob patch (82 lines); other session 137 lines still uncommitted in that file.
- FINDING: a clean checkout of HEAD does NOT build: Commands/WpfCommandHost.cs:111 (from CC-2) references MainViewModel.DeleteImageCommand, which exists only in another session uncommitted MainViewModel.cs (HEAD has 0 hits). Not caused by CC-3. Needs that session to commit MainViewModel.cs, or a decision. Log artifacts/EAI-CC/cc3/head-wpf.log. tmp/EAI-CC/verify is a git-archive copy of HEAD (delete when no dotnet server holds it).
- Next: CC-4 hotkeys (MainWindow.xaml.cs, ShortcutTable dirty), CC-5 settings (AppSettings dirty), Online wiring.

## Update 2026-10-18 (EAI-CLS)
- User decision: class table must follow labels automatically (auto-add unknown labels, notice, no manual dialog needed). Codex job brief tmp/EAI-CLS/brief.md, snapshots tmp/EAI-CLS/before/. Repro project E:\Training\TrainLOGONEW must NOT be touched. After Codex: review, run tests, stage only Codex hunks via blob patch (MainViewModel/SegAiSession/Strings* are dirty by other sessions), commit.

## Update 2026-10-18 (EAI-CLS committed)
- EAI-CLS committed: class table auto-extends from annotation labels (open/import/reload), notice, 19 tests (I fixed test root default; SegAI.Tests filtered 61 pass, Wpf build 0 err; artifacts/EAI-CLS/mine). Staged via blob patches; other sessions hunks remain dirty. UI smoke owed. User must restart EasyAI (bin\Release built Any CPU) and retrain: old v011 NG/OK model will not match the extended table (Inspect shows Error verdict).
## Pause 2026-10-19 11:25 - User asked to pause

### Facts (filled by script)
- session: ae76e148-a467-4f3d-a47d-6c0a1c412002 (started 2026-10-18 13:15)
- usage: snapshot age 22919 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: none
- commits since session start (any session): 11
  - c76d1ed3 [EAI-CLS] Native set also stages OpenCV, OpenVINO, tbb and Models/DefectV2 (clean OutDir loads BeeNativeSegAI, ABI 1)
  - 170d035b [EAI-CLS] EasyAI.Wpf build copies the Segment AI native set from Lib/native/segai (git-ignored), warns when missing
  - 6188321d [EAI-CLS] Class table follows annotation labels: unknown labels auto-added (Defect, OK/GOOD=Good) on open/import/reload, one notice, 19 tests
  - 709c5301 [EAI-CC] CC-3: chat slash commands routed through the command registry (8 AGT.* aliases, generated /help, mode gating) + 20 tests
  - 11971e25 [EAI-ONL-MODE] CC-14b: OnlineBench --dataset and native 5472x3648 Mono8 synthetic-frame mode (guards; inference not run)
  - 23375739 [EAI-ONL-MODE] CC-17b: Online status window follow-ups (AutomationIds, Closed only, non-blocking Stop) + EasyAI.Wpf.Tests (12)
- files edited by this session: 19; still uncommitted: 0

### Task and scope
Parent EAI-COMMERCIAL. Plan EAI-ONL-MODE-r1-plan-v1 (tmp/EAI-ONL-MODE/plan.md), contracts EAI-ONL-MODEL, EAI-CC, EAI-ONL-MODE (approved; build/test/commit on main, benchmark on dev PC; no push, no hardware, no 2-core PC). Plus ad-hoc user requests this session: EAI-CLS (class table follows labels) and native Segment AI staging. Dispatch mode medium. Codex handoff: tmp/EAI-ONL-MODE/codex/HANDOFF.md.

### Done (verified)
Committed on main: CC-14 runner 21a86c601, CC-15a endpoint bc6b01ec3, CC-16 e5e84ad5, CC-15b adapter+status window 6ebcc801, CC-17a endpoint hardening bcc5c979 (Online.Tests 154), CC-17b window follow-ups + EasyAI.Wpf.Tests 23375739 (12), CC-14b OnlineBench synthetic frame 11971e25, CC-3 slash via registry 709c5301 (Commands.Tests 275, SegAI slash 49), EAI-CLS class-table auto-sync 6188321d (61 filtered tests), native staging csproj 170d035b + c76d1ed3. Logs: artifacts/EAI-ONL-MODE/cc14,cc17; artifacts/EAI-CC/cc3; artifacts/EAI-CLS/mine. Codex reports: tmp/EAI-ONL-MODE/codex/results-T1..T7.md, results-CC17-A/B/C.md, tmp/EAI-CC/results-CC3.md, tmp/EAI-CLS/results.md.

### Remaining (in order)
1) FIRST: ask user whether labelling of E:\Training\TrainLOGONEW is finished; if yes run Phase A (T7): retrain with LECH/LECH_BOT classes (class table now auto-extends), held-out Val recall, tile 1024/1536/2048 at net 512, net 768, tiled inference cost for one 20 MP frame vs 100 ms (brief tmp/EAI-ONL-MODEL/codex/brief.md; tile data tmp/EAI-ONL-MODEL/tiles). 2) Wire Online into the app: create OnlineSession, attach OnlineControlAdapter, open OnlineStatusWindow, ONL.START, pass the control to Discover in WpfCommandHost; touches dirty MainWindow/MainViewModel/WpfCommandHost (blob-patch staging). 3) CC-4 hotkeys (MainWindow.xaml.cs, ShortcutTable dirty; UIA smoke of shortcuts owed). 4) CC-5 settings schema (AppSettings dirty, schema/legacy compat). 5) Final OnlineBench on native 5472x3648 frames, iGPU and CPU, p95 < 100 ms (needs a model).

### Decisions already made
User: edit dirty files and commit per hunk (blob patch: diff snapshot vs working tree, apply to HEAD blob, git update-index --cacheinfo; never git add on dirty files, never stash). Class table auto-adds unknown labels (Defect; OK/GOOD/Tot = Good) with one notice (user chose over prompt/warning-only). Native set staged in EasyLabel/Lib/native/segai (git-ignored, ~500 MB) and copied by the Wpf csproj. No more Claude dispatch for test/summary work: Codex lead splits tasks. Tile training chosen over whole-frame (whole-frame 512 recall 0.002).

### Do NOT redo
Do not redo: threshold sweeps, label rasterisation check, CC-1/2/3/12/13/14/15/16/17 reviews and tests, OnlineBench synthetic frame review (ACCEPT), endpoint hardening review, EAI-CLS review, E: cleanup. T2 harness (tmp/EAI-ONL-MODE/smoke) only has 96 DPI evidence.

### In-flight agents and background work
No agents or Codex jobs running (last jobs completed). No background training. tmp/EAI-CLS/verify and outtest dirs may remain (scratch, deletable).

### Next commands
git status --short | head; git log --oneline -8. Tests (set TEMP/TMP to tmp/EAI-ONL-MODE): dotnet test EasyLabel/tests/EasyLabel.Online.Tests -c Release; dotnet test EasyLabel/tests/EasyLabel.Commands.Tests -c Release; dotnet test EasyLabel/tests/EasyAI.Wpf.Tests -c Release; dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release (Any CPU only, output bin\Release\net9.0-windows). Authorization: edit, build, test, benchmark (dev PC), commit on main; no push/hardware. If .git/index.lock is empty and no git.exe runs, delete it. Codex companion: node C:/Users/chitu/.claude/plugins/cache/openai-codex/codex/1.0.6/scripts/codex-companion.mjs (task --background --write --model gpt-6.1-sol; one job at a time).

### Risks and unverified
A clean checkout of HEAD does NOT build: WpfCommandHost.cs:111 (CC-2) uses MainViewModel.DeleteImageCommand which exists only in another session's uncommitted MainViewModel.cs (log artifacts/EAI-CC/cc3/head-wpf.log). Other sessions' dirty files: LabelStepViewModel.AgentChat.cs (137 lines), MainViewModel.cs, AppSettings.cs, ShortcutTable.cs, MainWindow*, Strings*, SegAiSession.cs and ~100 more. UI smoke never run on real app: status window (native 144 DPI and OS UIA unverified), slash commands in the chat box, class-table auto-sync notice. Old v011 NG/OK model does not match the extended class table: user must retrain. agentmemory server was DOWN at session start. User photo showed another PC lacking Segment AI native files: fixed by copying whole net9.0-windows folder.

### Acceptance still owed
EAI-ONL-MODEL: held-out recall number + model decision. EAI-CC: CC-4, CC-5. EAI-ONL-MODE: app wiring, UI smoke, final 20 MP full-chain p95 < 100 ms (CPU and iGPU), recall on Val, colour measured separately. Endpoint network behaviour beyond loopback and IPv4-mapped sockets unverified.

