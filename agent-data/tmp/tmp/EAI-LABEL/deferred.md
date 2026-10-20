# Deferred work: EAI-LABEL

Written in English by /epause. Resume with /eresume EAI-LABEL. Append new sections; never rewrite earlier ones.

## Pause 2026-10-12 20:34 - User ran /epause after the full 84-image Economy+3-worker confirmation run

### Facts (filled by script)
- session: 2bfe5b78-f1ce-45b0-8843-f69fbb47df51 (started 2026-10-12 14:37)
- usage: snapshot age 13388 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: EAI-LABEL
- commits since session start (any session): 4
  - 90785d0c7 [EAI-LABEL] S1b: lever harness (parallel workers, effort, model, skills) and Foreground/Morphology sweep
  - 1c60c460d [EAI-LABEL] S1: offline bootstrap replay test (seeds from baseline, V2 train, auto-label, routing rules)
  - c3820bfdf [EAI-LABEL] S0: Agent Prompt baseline harness (opt-in) and rotated-IoU label metrics
  - 3a7de39f1 [EAI-OKNG] Review fixes: tag-only images skipped by exports, manifest null safety, export rollback, classifier train list in trace, P(NG) pass guard
- files edited by this session: 2; still uncommitted: 0

### Task and scope
Contract EAI-LABEL (.claude/task-contract.EAI-LABEL.toml, plan tmp/EAI-LABEL/plan-v1.md, approved by user 2026-10-12; scope EasyLabel/**, tools/easyai-train/**, tmp/EAI-LABEL, artifacts/EAI-LABEL; build/test/benchmark/commit true, push/deploy/hardware false). Goal: label all of E:\EAI_TEST (84 PNG) 4x faster and 50% cheaper than today's Agent Prompt batch. User decisions: baseline measured first; quality vs 18 real GT json in E:\EAI_TEST (5 'OK' oriented_rectangle each); direction = combine local-model fix + agent optimisation; extra real-agent budget 10 USD (about 7.7 used).

### Done (verified)
S0 baseline (artifacts/EAI-LABEL/s0-baseline): 84 imgs, 1938.99 s, 7.827 USD, quality vs GT P1 R1 IoU .975. S1 offline replay (artifacts/EAI-LABEL/s1-replay, native DLL artifacts/EAI-LABEL/native-run, EASYAI_SEGAI_DIR): plain V2 bootstrap NO-GO. S1b sweep (artifacts/EAI-LABEL/s1b-sweep, s1b-sweep2) and lever runs (s1b-par4, s1b-seq-*, s1b-eco-*; artifacts/EAI-LABEL/s1b-lever.py). FULL run Economy + 3 workers (artifacts/EAI-LABEL/s2-eco-par3-full): 466.5 s (4.16x), 2.598 USD (-66.8%), 84/84 Success, vs GT TP90 FP0 FN0 mean IoU .785, vs baseline labels P .995 R .968. Commits c3820bfdf, 1c60c460d, 90785d0c7 (test-only). Tests: LabelMetricsTests 12 pass. Status log tmp/EAI-LABEL/status.md.

### Remaining (in order)
1. (start here) Productize in the real batch: parallel workers (each its own AgentRunner; AgentBatchRunner.cs:73-191 is sequential, AgentRunner.cs:30,399 single _live, shared memory file AgentRunner.cs:274, experience block built after image 1 AgentBatchRunner.cs:161-165) + Economy preset for batch Agent Prompt, behind a setting (new AppSettings rows, en+vi strings, Label-step batch controls). Route via eroute standard/deep; implementer sonnet (logic), verifier builds. Keep product defaults unless the user approves. 2. Second full confirmation run (plan wants two runs both meeting target) - needs extra USD budget from the user (about 2.6 per run, about 2.3 left of the 10). 3. Look at box tightness: Economy mean IoU .785 vs .975 (baseline); try Economy + a refine step or Normal effort for OK-type images and compare IoU/cost. 4. UI smoke of the batch controls (100% and 150% DPI, en/vi). 5. Later: local small model only for NG-type images (5 objects), Foreground 0.95-0.97, routing rule count==seed median; OK-type images (28 touching objects) cannot be done by the local model (recall <=.56).

### Decisions already made
Segment AI bootstrap alone rejected: semantic mask merges touching objects, uncertainty buckets all Sure, k=2..20 gives R~.4; raising Foreground (0.95-0.97) helps NG a lot but OK images stay R<=.56. Haiku rejected as default: no speedup, recall .94 with eco-par2. 4 parallel workers on Normal is slower per image and costs more (2-core CPU, local SAM2 contention), use 3 workers with Economy. Cost/speed come from Economy effort (turns 8->3) and parallelism, not from the local model. Sonnet default model kept.

### Do NOT redo
Do not re-run S0/S1 or re-review LabelMetrics, BootstrapBaselineTests, BootstrapReplayTests (build OK, tests ran). Do not re-measure haiku or Normal-parallel. The 18 GT images are the first 18 by name (all NG-named); sample:12 selection rule is in summary.json. E:\EAI_TEST is read-only; work copies in tmp/EAI-LABEL/s0/project.

### In-flight agents and background work
No agents running. Background shells all finished. A leftover EasyAI.exe may exist from earlier UI smoke: check tasklist.

### Next commands
Authorization: edit_code, build, run_tests, benchmark, commit true; push, deploy, hardware false; extra real-agent USD only with a fresh user budget. Harness run (PowerShell, from project root): set TEMP/TMP to tmp\EAI-PROJ	emp, EAI_LABEL_RUN=1, EAI_LABEL_PROJECT=tmp\EAI-LABEL\s0\project, EAI_LABEL_APPDIR=artifacts\EAI-OKNG5\out, EAI_LABEL_OUT=<fresh folder>, EAI_LABEL_MAX_USD, EAI_LABEL_PARALLEL=3, EAI_LABEL_EFFORT=Economy; dotnet test EasyLabel	ests\EasyLabel.SegAI.Tests -c Release -p:Platform=x64 --no-build --filter FullyQualifiedName~MeasureAgentPromptBatchLabelling; scoring with LabelMetricsOnGroundTruthTests and LabelMetricsVsBaselineTests (EAI_LABEL_BASELINE=artifacts\EAI-LABEL\s0-baseline). Build: dotnet build EasyLabel/tests/EasyLabel.SegAI.Tests -c Release -p:Platform=x64.

### Risks and unverified
UNVERIFIED: productized behaviour (none written yet), UI smoke, 150% DPI, second full run, repeat variance, box tightness at Economy (IoU .785), Claude non-determinism, rate limits with more than 3 workers, real-app memory-file race under parallel. Native BeeNativeSegAI built isolated only (artifacts/EAI-LABEL/native-run); the live bin/Release DLL lacks SEGAI_TrainerSetClassTable so named-class V2 training from the shipped DLL is not possible (EAI-CLS native not deployed): flag to user. Overlap: other sessions' dirty files (.claude/agents, skills, native/version files) untouched.

### Acceptance still owed
Acceptance owed: 4x and -50% proven in two full runs (one done), quality thresholds (recall>=.85, precision>=.70 pass in the one run), productized setting works in the real Label step, UI smoke, history/tracking not updated.

## Pause 2026-10-15 09:14 - User asked to pause after finishing one step (5h limit nearly used); speed measurement agent running

### Facts (filled by script)
- session: 9210e0a7-61d4-4e7a-aa11-58c0b44a5142 (started 2026-10-14 12:43)
- usage: snapshot age 17027 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: none
- commits since session start (any session): 15
  - 52655f24c [EAI-LABEL] Agent chat: boxes may extend outside the image by default, per-image accept/discard, SAM2 refine area caps
  - ab2b0ad20 [MISSING-TOOTH] Profile check: automatic two-end check; Table Train section only in Profile mode
  - 041a1fe82 [MISSING-TOOTH] Chip check: Training OK image table with thumbnails, per-image chart and a Train button
  - cef40a539 [EAI-POS] P12: canonicalise oriented-box labels for k=4 (theta into (-45,45] deg, W/H swapped per quarter turn)
  - be3cda737 [EAI-POS] P11: configurable instance-head epochs (default 300 for Position and Detect box mode)
  - 3138cfde4 [EAI-POS] P10: automatic angle symmetry (k=4 for near-square oriented boxes) for Position and Detect box mode
- files edited by this session: 4; still uncommitted: 0

### Task and scope
Contract: EAI-LABEL (agent chat labelling, no contract file edited this session) plus EAI-POS follow-ups already committed. Goal of the open work: (a) measure why the Agent batch is slow (user: 10 min for 50 images with 8 workers), (b) change the agent skills so the agent labels EVERY object of the named kind in a crowded image (user chose: "doi skill de agent gan du moi vat", to be done AFTER the speed measurement). Scope: EasyLabel/** only (EasyLabel.Ai.Mcp Skills and ProposalStore, EasyAI.Wpf agent services/view models, EasyLabel.SegAI). Out of scope: BeeNativeSegAI, EasyVision runtime, BeeLicense.dll, deploying the native DLL. Authorization: build, run_tests, commit on main true; real paid agent runs are authorised by the user (2026-10-15: "optimise budget, may have no ceiling"); push/deploy/hardware false.

### Done (verified)
- Committed this session (all on main): EAI-POS P3 419528c51, P4 ba7c1339e, P5 09a9245b7, P6 3c2698c08, P8 f200e4fb6, P9 e0a7a9215, P10 3138cfde4, P11 be3cda737, P12 cef40a539, New-project caption contrast 536bb1a8b; EAI-LABEL tuning popup 2fbc23043 and 52655f24c (boxes may extend outside the image by default, YOLO/VOC/COCO/EasyVision export clip, ToInstances drops centres outside the image, per-image accept/discard in the chat batch result, SAM2 refine area caps 0.5-1.8). Evidence: artifacts/EAI-LABEL/final/*.log and artifacts/EAI-LABEL/boxfit (build 0 errors, Ai.Mcp.Tests 120, Core.Tests 430, SegAI.Tests 1466 pass + 1 parallel flake).
- Real-data results for EAI-POS: artifacts/EAI-POS/P7, P7c, P9real, P10real, P11real, P12real, UI smoke artifacts/EAI-POS/UI/results.md.
- Speed measurement (agent ae8338fced2933993) had produced runs A-par3, B-par8, B2-par8, C-par8-nosam, D-sam2-serial-cached under artifacts/EAI-LABEL/speed (per-image.csv, summary.json each) and tmp/EAI-LABEL/speed/*.log at 09:12; results.md was NOT yet written at pause time.

### Remaining (in order)
1. (start here) Collect the speed measurement: read artifacts/EAI-LABEL/speed/results.md if the agent finished; otherwise read the five summary.json/per-image.csv files and write results.md (machine cores/RAM/MaxWorkers, wall vs worker time, parallel efficiency, agent CLI vs SAM2 vs apply vs retry share, USD spent, top bottleneck, ranked fixes). Report to the user.
2. Implement the top speed fix(es) from the measurement (candidates from the read-only exploration: single SAM2 gate AgentSam2Refiner.cs:24,:76 held per image, loadInput awaited on the UI context AgentBatchRunner.cs:334 and apply on the UI thread :410, agent CLI per-image time, cap min(cores, RAM/1.5) AgentSpeedRules.cs:13-17 silently clamps x8, retry at Normal AgentBatchRunner.cs:122-134). Re-measure with the same harness.
3. "Gan du moi vat" skill change (after 1): edit EasyLabel/src/EasyLabel.Ai.Mcp/Skills detect.md, read-image.md, repeat.md, label-rules.md, label-placement.md. Today they say: keep tool calls minimal, skip doubtful objects, ONE pass, overview only 768 px JPEG q85 (ImageSource.cs:11,:14). Planned: default = label every object of the named kind in the image; a region or count limit in the user prompt wins ("five in the centre" is obeyed); count on the overview, split the image into zones with get_crop calls in one turn, one review pass for gaps, skip only objects that already have a shape; do not let the dedup filter (ShapeOverlap.IsDuplicate IoU>=.5 or cover>=.8, ProposalStore.DuplicateReason :386-412, AgentDuplicateFilter.cs:80/114-118) drop touching neighbours (check how it treats touching boxes). Then run one small paid batch (about 12 dense images, e.g. copies of E:\EAI_TEST images with ~40 teardrop fans) to measure completeness (count proposals vs visible objects) and time; trade-off: more crops = slower.
4. Pending user decisions/owed items below.

### Decisions already made
- Option A (user): relaxed EAI-POS thresholds for hand-drawn labels, recorded in tmp/EAI-POS/plan-v1.md; EAI-POS accuracy tuning is considered finished (centre ~5 px, angle ~3.6-4 deg, rIoU ~.88; tight vs relaxed thresholds, label noise ~4 deg).
- Detect box mode (P9): auto-on when labels are only rectangle/oriented_rectangle, off for polygons, V1, Retrain, InputSizeOff; Inspect stays mask-based (open decision, tmp/EAI-POS/issues.md).
- Boxes outside the image are allowed by default (AppSettings.AllowOutOfBounds true); at least 20% visible and at most 4 image areas.
- SAM2 refine: current prompt/min-area fit kept (measured IoU .972 vs GT vs .772 agent alone); no variant beat it.
- Never git stash in the shared worktree; stage explicit paths only; the worktree holds other sessions' dirty files (.agents, .claude, MISSING-TOOTH, BeeInterface).

### Do NOT redo
- Do not re-review the committed phases (each had a reviewer ACCEPT/ACCEPT-WITH-NOTES and fixes).
- Do not re-run P7/P9/P10/P11/P12 real-data harnesses: results are in artifacts/EAI-POS/P*real.
- The SAM2 variant study (centre click, shrunk prompt, clipping, area caps) is done: artifacts/EAI-LABEL/boxfit/measure/summary.txt.
- UI smoke for EAI-POS P1/P6/P8/P9 passed at 100% scale en+vi (artifacts/EAI-POS/UI/results.md); only 150% DPI and a few notices are owed.

### In-flight agents and background work
- Agent ae8338fced2933993 (speed measurement, general-purpose) may still be running in the background when this session ends; check artifacts/EAI-LABEL/speed/results.md and tmp/EAI-LABEL/speed/*.log. If results.md is missing, re-run is NOT needed: summarise the existing per-image.csv files.
- No implementer/reviewer agents running. No uncommitted source changes from this session (only tmp/ and artifacts/ evidence).

### Next commands
Authorization: edit_code, build, run_tests, commit true on main; real paid agent runs allowed for the measurement (no ceiling, optimise); push/deploy/hardware false.
- cd "E:\Code\EasyVision_Unisen - NoService"; ls artifacts/EAI-LABEL/speed; sed -n 1,80p artifacts/EAI-LABEL/speed/results.md
- python -c to print summary.json of each run dir (or open per-image.csv) if results.md is missing.
- Build/test recipe: dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release -v:minimal ; dotnet test EasyLabel/tests/EasyLabel.SegAI.Tests/EasyLabel.SegAI.Tests.csproj -c Release (also Core.Tests, Ai.Mcp.Tests). Do not use the isolated-verification flag for SDK projects. Build the Wpf csproj directly (the sln does not build it).
- Commit with explicit paths only.

### Risks and unverified
- Known flaky test: AutoLabelServiceTests.NativeModel_ImportModel_KeepsLoadedModelAndDoesNotActivate fails in full SegAI.Tests runs (passes alone): five test classes load the same V2Both fixture model concurrently (file in use). Fix = one xunit collection for them. InfererSmokeTests flaked once for the same reason.
- bin\Release\BeeNativeSegAI.dll is stale (2026-10-01, no instance head): box mode / Quality selector do not work for the user until it is replaced by the build in artifacts\verification\Release\x64\bin\BeeNativeSegAI (not authorised to deploy; ask the user). The app finds it by walking up from the exe; EASYAI_SEGAI_DIR overrides it for a test run.
- Owed UI smoke: per-image accept list at 440 px width and 50+ images, Settings allow-out-of-bounds toggle, 150% DPI (needs a human to set Windows scale), PositionRefused* notice, Import model dialog path typing.
- Not audited: StandaloneConvertService and UltralyticsExportService do not clip out-of-image shapes; native training with out-of-bounds labels never run end to end; MCP ProposalStore has no project kind (polygons for Position are refused only on the WPF side); SetAnnotations not gated for Position.
- 2-core i7-7600U production PC: agent speed, SAM2 time, instance training time (about 6-9 min on dev PC at 300 epochs) all unmeasured there.

### Acceptance still owed
- Speed: target from earlier work was 4x with 8 workers (s4b reached 3.06x with 3 workers); user's current complaint 10 min / 50 images / 8 workers not yet attributed.
- Completeness in dense images: not yet changed or measured.
- Skill change must be measured on both completeness and time before it is called done.

