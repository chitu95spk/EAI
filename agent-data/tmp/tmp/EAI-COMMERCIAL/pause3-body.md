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
