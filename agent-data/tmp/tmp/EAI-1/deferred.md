# Deferred work: EAI-1

Written in English by /epause. Resume with /eresume EAI-1. Append new sections; never rewrite earlier ones.

## Pause 2026-10-05 16:21 - S5 implementer died: API authentication_failed (Not logged in); user ran /epause

### Facts (filled by script)
- session: fa1df89c-fc0d-4e0a-882e-08ad010f38d0 (started 2026-10-05 14:05)
- usage: snapshot age 3055 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: EAI-0
- commits since session start (any session): 5
  - d1201fa3d [EAI-1] S4: SegAiSession and plain inference (cloned post-processing, no label rules)
  - 6b4797738 [EAI-1] S3: LabelMe converter with sidecar+raster holes, .easyai project store, Core skips dot-folders
  - ff2b70afe [EAI-1] S2: dataset types, training options, label raster, EasyVision dataset zip/folder IO
  - 4e7a4bf10 [EAI-1] S1: native trainer/inferer wrappers and model-file helpers in EasyLabel.SegAI
  - b0504bc31 [EAI-0] EasyAI skeleton: EasyLabel net9, SegAI/Camera libs, WPF-UI shell
- files edited by this session: 8; still uncommitted: 0

### Task and scope
EAI-1 (contract .claude/task-contract.EAI-1.toml, plan EAI-1-r1-plan-v1 = tmp/EAI/EAI-1/plan-r1.md, approved by user 2026-10-05). Goal: UI-free Segment AI backend in EasyLabel/src/EasyLabel.SegAI (net9.0-windows, C# 12) cloned from EasyVision, LabelMe converter with hole/raster sidecar, parity vs EasyVision. In scope: EasyLabel/src/EasyLabel.SegAI, tests/EasyLabel.SegAI.Tests, the approved Core dot-folder change, tmp/EAI, artifacts/EAI. Out of scope: any EasyVision project (read-only), UI, camera. Extra progress notes also in tmp/EAI/EAI-1/deferred.md and tmp/EAI/EAI-1/issues.md (the contract points at the latter path; this file is the /epause one).

### Done (verified)
S1 4e7a4bf10, S2 ff2b70afe, S3 6b4797738, S4 d1201fa3d, all committed after build 0 errors + tests green (last: 42 Core + 49 SegAI pass; logs artifacts/EAI/EAI-1/r1/build-s4.log, test-s4.log). S3 includes the approved Core change (ProjectService.EnumerateImages skips dot-directories) with test.

### Remaining (in order)
1. (START HERE) Finish S5 training: the S5 implementer died mid-way (auth error), so UNCOMMITTED, UNBUILT, UNREVIEWED partial work exists: EasyLabel/src/EasyLabel.SegAI/Training/ (new), Model/SegAiRegistry.cs (new), Data/SegTrainingSplit.cs (new), tests/EasyLabel.SegAI.Tests/TrainingFakes.cs (new), SegAiSession.cs (modified). Inspect `git status`/`git diff`, then either re-dispatch an implementer with the S5 brief (kept in this conversation; essentials: no real native training unless EASYAI_RUN_TRAIN=1; fake backend seam; registry vNNN no-overwrite; StageCandidate; cancel; run-folder pruning keep 5 failed; golden settings-JSON; full-image outline; backbone path from NativeSegAiLibrary.LoadedFolder) telling it which files already exist, or complete them. Gate: dotnet build 0 errors, tests green, commit.
2. S6 parity harness (tmp only): net48 console ref48 referencing bin/Release BeeCore.dll/BeeGlobal.dll read-only; dump to artifacts/EAI/EAI-1/<run>/parity/ref and net9 side; compare.ps1. Tolerance: native mask/confidence bit-exact, scores 1e-6. If the native mask itself differs: stop and report.
3. Close EAI-1: independent review (easyvision-reviewer sonnet), fix cycle, final commit, update memory easyai-project-plan.

### Decisions already made
Native engine shared, managed code cloned; LabelMe stays pure, SegAI data under .easyai/ (seg sidecar + labels.png, holes only in sidecar); export to EasyVision zip only (GDI+ keys not reproducible); parity means full-image DoWork(Mat, Rect), GPU off; label rules/multi-region/rotated check -> EAI-5; no real training run authorized (build + run_tests only); namespace EasyLabel.SegAI.Convert kept, System.Convert qualified; OpenCvSharp4 4.13 uses CmpTypes. SegInferenceSettings.EnableGpu default false (EasyVision true). Normalize returns a new Mat. Full list: tmp/EAI/EAI-1/issues.md and plan-r1.md.

### Do NOT redo
S1-S4 files are built, tested and committed; do not re-review them except via the final review. Do not re-run discovery/architect: plan-r1.md is the approved plan.

### In-flight agents and background work
S5 implementer (agent id unknown, terminated by authentication_failed) left partial uncommitted files listed above; nothing else running. Re-run the build first to see whether they compile.

### Next commands
cd "E:\Code\EasyVision_Unisen - NoService"; set TEMP/TMP to %CD%	mp\EAI\EAI-1	emp; dotnet build EasyLabel/EasyLabel.sln -c Release -nodeReuse:false > artifacts/EAI/EAI-1/r1/build-s5.log 2>&1; grep ": error " the log; dotnet test EasyLabel/EasyLabel.sln -c Release --no-build > artifacts/EAI/EAI-1/r1/test-s5.log 2>&1. Authorization (contract): build=true, run_tests=true, commit=true on main, push=false, NO train smoke, no hardware. Commit with explicit paths only (git add -- <paths>; the index holds ~29 unrelated staged skill renames from other work: always `git commit -- <paths>`). A stale .git/index.lock may reappear: check no git process first. Verify /login first (the last agent failed with Not logged in).

### Risks and unverified
Open issues: tmp/EAI/EAI-1/issues.md. Unverified: SegAiPredictor sub-ROI validMask guess (native output size for sub-ROI); real training never run (V1/V2 smoke owed, needs user authorization); parity not run; c10/torch extra-folder loading for training untested. Owed from EAI-0: MVS enumerate/open/grab (EAI-6), 150% DPI, multi-monitor. Incident: EAI-0 smoke opened sibling read-only workspace (disclosed). git index.lock was stale once.

### Acceptance still owed
Not yet proven: inference parity vs ToolSegmentAI (S6); training produces a loadable .segai (needs train authorization); SegAI<->LabelMe round trip is unit-tested (S3) but not with a foreign LabelMe tool.


## Update 2026-10-05 - S5 committed
S5 committed (build 0 errors, 42 Core + 86 SegAI pass, 1 skip = opt-in real training; logs artifacts/EAI/EAI-1/r1/build-s5b.log, test-s5b.log). Fixed System.Convert qualification in SegAiTrainingPipeline. Remaining: S6 parity harness, then independent review + close + update memory easyai-project-plan. Golden JSON float format passed.

## Update 2026-10-05 - S6 run (parity VACUOUS so far)
S6 test committed 73c81f7a3 (ParityDumpTests; ref48 harness + compare.ps1 stay in tmp/EAI/EAI-1/parity, uncommitted by design). Run artifacts/EAI/EAI-1/parity-r1/ : compare.log = 6/6 PASS, same native DLL sha256 both sides. BUT every native output (raw mask, conf, filtered mask, regions, score) is all-zero for both fixture models (artifacts/V133/native-checks/ng-only, ng-and-ok) on the 3 seeded synthetic images: equality is trivial, does NOT prove post-processing parity. Fixes made: ref48 Program.cs adds repo\BeeNativeSegAI\x64\Release (c10/torch) to PATH; compare.ps1 `${case}:` parse error; System.Convert qualification in ParityDumpTests.
Next: pick a model that actually fires (many .segai under artifacts/V133, V134, e.g. v2-both) plus images from its own dataset (EASYAI_PARITY_EXTRA_IMAGE copies one extra; models are found by glob native-checks only, extend FindModels), rerun, require non-zero masks and regions. Then independent review (easyvision-reviewer), close EAI-1, update memory easyai-project-plan.

## Update 2026-10-05 - S6 DONE (non-vacuous)
Model bin/Release/Program/TESSSSSST/SegAI_00_000/model.segai + 8 dataset PNGs (env EASYAI_PARITY_MODEL, EASYAI_PARITY_IMAGE_DIR) + 3 synthetic: 11/11 PASS bit-exact, 5 cases with mask nonzero (56k-80k px) and 1 region, scores ~0.011-0.016, same native DLL sha. Evidence artifacts/EAI/EAI-1/parity-r2/ (compare.log). Commit: parity test override. Remaining: independent review (easyvision-reviewer sonnet), fix cycle, final commit, update memory easyai-project-plan.

## Update 2026-10-05 - EAI-1 CLOSED
Review (sonnet) ACCEPT; fixes 1-5,8 committed after build 0 errors, 42 Core + 96 SegAI pass, 2 skip (opt-in real training, opt-in parity); logs artifacts/EAI/EAI-1/r2/. Skipped by choice: review items 6 (orphan running run folders), 7 (image re-read during training). Owed: real V1/V2 training smoke (needs user authorization), review fixes 3 and 8 have no tests (no seam). Next: EAI-2 per project plan memory.
