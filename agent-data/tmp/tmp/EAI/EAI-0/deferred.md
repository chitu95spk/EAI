# EAI-0 deferred / progress (updated by root)
- S1 DONE (retarget net9; WFO1000 NoWarn in App.csproj; baseline 41 tests net8 = 41 net9). Logs artifacts/EAI/EAI-0/r1/*s1*.log
- S2 DONE (SegAI loader ABI=1, Camera interfaces + MVS SDK version 4.5.1.2 via P/Invoke; 5 SegAI tests pass; spike.json). Fix: MvsSdk candidate folders (env MVCAM_COMMON_RUNENV points at Development on this PC).
- S3 NEXT: src/EasyAI.Wpf per plan-r1.md section S3; then dotnet sln add, build, tests, UI smoke (screenshots 100%; 150% DPI owed), EasyLabel.App start regression, then commit EAI-0 (explicit paths: EasyLabel/** new+modified, .claude/task-contract.EAI-0.toml, tmp/EAI/plan.md? no - tmp is untracked scratch).
- S3 DONE, review ACCEPT (8 minor: R1,R2,R4,R5 fixed; R3,R6,R7,R8 accepted-risk). COMMITTED b0504bc31 (58 files). Evidence: artifacts/EAI/EAI-0/r1/ (build-final3.log, test-final3.log, spike.json, smoke/, badargs.log, app-regress.log).
- OWED: MVS enumerate/open/grab (EAI-6, hardware); 150% DPI; multi-monitor/mixed DPI; light/dark OS switch; ABI-mismatch/error-126/cookie-leak executable evidence; corrupt layout.json test; preview race test.
- INCIDENT: smoke run 1 opened read-only sibling E:\Code\EasyVision_Unisen (.easylabel/project.json LastOpenedUtc rewritten; stray layout.json deleted). Disclosed to user.
- NEXT: EAI-1 (see tmp/EAI/plan.md section 8). Not committed: tmp/ and artifacts/ (scratch/evidence). Stale .git/index.lock (7h45m, no git process) was removed once.
