# Deferred work: EAI-AI

Written in English by /epause. Resume with /eresume EAI-AI. Append new sections; never rewrite earlier ones.

## Pause 2026-10-08 21:18 - user /epause mid S3b review fixes

### Facts (filled by script)
- session: 4de80e53-5ab1-4d6b-8444-6160aaa132d7 (started 2026-10-08 18:01)
- usage: snapshot age 7671 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: EAI-AI
- commits since session start (any session): 10
  - 9d9442f17 [EAI-AI] S3a: real claude + MCP end-to-end evidence
  - 04d4b17fb [EAI-AI] S3a: EasyLabel.Ai.Mcp stdio MCP server (get_image_info, get_crop, list_shapes, sam2_segment, propose_shapes) + skill pack + tests
  - 8f4bc8cae [EAI-AI] S3: spike evidence (claude -p + stdio MCP works, minimal flags cut cost 5x) and design
  - b7c4427c4 [EAI-AI] S2b: AI-Points/AI-Box tools, AI Model Run, SAM2/YOLO-World results in the suggestion layer, backend service with cancel and stale-result guard
  - ea8d710d6 [EAI-AI] S2a: real-model tests (Category=Model), export script, evidence for SAM2.1 tiny and YOLO-World on DirectML
  - ae5f79878 [EAI-UI] Tool strip: checked tool icon and caption use the text-on-accent colour
- files edited by this session: 16; still uncommitted: 5
  - EasyLabel\src\EasyAI.Wpf\ViewModels\LabelStepViewModel.cs [M]
  - EasyLabel\src\EasyAI.Wpf\Views\LabelStepView.xaml [M]
  - EasyLabel\tests\EasyLabel.SegAI.Tests\RealAgentRunTests.cs [??]
  - tmp\EAI\EAI-UI\smoke\smokeg.ps1 [??]
  - tmp\EAI\EAI-UI\smoke\smokes3b.ps1 [??]

### Task and scope
Reconstructed by a clean resume session from files only (the pausing session 4de80e53 never wrote these sections); decisions made in review chat are NOT recoverable and are listed under Risks.
Contract EAI-AI (.claude/task-contract.EAI-AI.toml), plan EAI-AI-r1-plan-v1 (tmp/EAI/EAI-AI/plan-r1.md), approved 2026-10-08. Goal: Auto-label source selector (AI Model | Segment Model | Agent Prompt), SAM2/YOLO-World ONNX GPU backend, Claude Code `claude -p` agent over a local stdio MCP server (proposals only). Authorization: edit/build/test/commit yes; push, deploy, hardware, benchmark, tracking no. Out of scope: any EasyVision project, model weights, API-key Claude calls, agent writing saved labels.
Note: contract budget.deferred_file points to tmp/EAI/EAI-AI/deferred.md, which does not exist; the real handoff is this file.

### Done (verified)
- S1 source selector, S2a/S2b SAM2 + YOLO-World backend, S3 spike + design, S3a MCP server: committed, last commit 9d9442f17 (git log: ea8d710d6, b7c4427c4, 8f4bc8cae, 04d4b17fb, 9d9442f17). Evidence in artifacts/EAI/EAI-AI/{s1,s2a,s2b,s3a}.
- S3b implementation exists UNCOMMITTED: EasyLabel/src/EasyAI.Wpf/Services/AgentRunner.cs and Services/Agent/ (AgentCacheAndLog, AgentCommandLine, AgentEnvironment, AgentJob, AgentLocators, AgentResults, AgentRunFiles), edits to LabelStepViewModel.cs, LabelStepView.xaml, AutoLabelSourceRules.cs, AppSettings.cs, Strings*.resx, two test csproj; new tests Agent*Tests.cs, AgentRunContractTests.cs, RealAgentRunTests.cs.
- S3b test run at 20:57-20:58 (before the late review edits): artifacts/EAI/EAI-AI/s3b/build.log, test-*.log: Mcp 32/32, Onnx 26/26, Core 150/150, SegAI 457 passed + 3 skipped.
- Real claude run at 21:10 with the app path: artifacts/EAI/EAI-AI/s3b/test-real-agent.log, tmp/EAI/EAI-AI/s3b-real/runs/log.csv (Success, 5 turns, $0.0385, 1 proposal). UI smoke screenshots/scripts: artifacts/EAI/EAI-AI/s3b/smoke, tmp/EAI/EAI-UI/smoke/smokeg.ps1, smokes3b.ps1.

### Remaining (in order)
1. (start here) Finish the S3b review fixes: no list of them survived. Re-derive by reading `git diff` of the uncommitted files and the Agent*Tests, ask the user which review findings were still open.
2. Rebuild EasyLabel.sln and rerun the four test projects; the Services/Agent files were modified at 21:16-21:17, after the last green logs (20:58), so S3b is currently unverified. Log to artifacts/EAI/EAI-AI/s3b-r2/.
3. Commit S3b as `[EAI-AI] S3b: ...` (stage only the EasyLabel files listed above; do not include tmp/ smoke scripts unless wanted).
4. S4 hardening: timeouts, cancel kills child process, error text, no secrets in logs, UI smoke vi/en at 100% and 150% DPI, evidence.

### Decisions already made
- Minimal claude flags (`--tools "" --system-prompt <own> --disable-slash-commands --setting-sources "" --no-session-persistence`): $0.0087 vs $0.043 per run (plan-r1.md S3 spike).
- stdio MCP child process, no listening port; run folder %LOCALAPPDATA%\EasyAI\agent-runs\<guid>; proposals.json goes to the suggestion layer only, never the saved JSON.
- Claude discovery order: setting override, PATH, %USERPROFILE%\.claude\local, newest VS Code extension folder.
- Models live in %LOCALAPPDATA%\EasyAI\models and are never committed; DirectML on RTX A500 4 GB, SAM2 tiny/small only.

### Do NOT redo
- S1, S2a, S2b, S3 spike, S3a: committed with evidence; do not re-review.
- The 20:58 green test logs are valid only for the code as of 20:58.

### In-flight agents and background work
None known. The pausing session stopped mid review-fix; no agent output paths recorded.

### Next commands
- `git diff --stat -- EasyLabel` then read the diffs of AgentResults.cs, AgentRunFiles.cs, AgentCacheAndLog.cs (modified last).
- Build and test (authorized): follow the s3b build.log/test-*.log pattern; build the EasyLabel solution directly, log to file, grep errors.
- Commit allowed after green (authorization.commit=true); push not allowed.

### Risks and unverified
- Review findings and fix decisions from the previous chat are lost; step 1 must recover them from the diff or the user.
- Code changed after the last green build/test: unverified until step 2.
- UI smoke at 150% DPI and the vi/en screenshots for S3b: only partial evidence in artifacts/EAI/EAI-AI/s3b/smoke, not reviewed here.
- Usage snapshot stale (7671 min old); ask the user for /usage before dispatching.
- Worktree has many unrelated dirty files (BeeCore, .claude, etc.) owned by other work; leave untouched.

### Acceptance still owed
- Agent Prompt: cancel works, clear Unavailable reason when claude is missing or not signed in, per-image cache and token log (cache/log files exist; behaviour tests need re-run).
- Persisted source choice and AI-Points enable rule re-verified after S3b edits.
- UI smoke screenshots vi/en at 100% and 150% DPI.
- SAM2/YOLO-World latency on target GPU (hardware, owed).

## Update 2026-10-09 - S3b committed
- S3b committed as b35a25eaf. Found and fixed 3 build breaks left by the interrupted session: AgentJob.cs not linked in SegAI.Tests csproj, Strings.Designer.cs missing AgentPromptTooLongFormat/AgentExitNoOutputFormat, malformed `name="AgentRunErrorTitle"xml:space` in both resx.
- Evidence: artifacts/EAI/EAI-AI/s3b-r2/ (build.log 0 errors; Mcp 32, Onnx 26, Core 150, SegAI 458 passed / 3 skipped; Model/RealAgent categories not run).
- Remaining: only S4 hardening (timeouts, cancel kills child, no secrets in logs, UI smoke vi/en at 100% and 150% DPI). The lost S3b review-finding list was not recovered; ask the user whether any finding is still open before S4.

## Update 2026-10-09 - S4 done except 150% DPI
- S4 committed (see git log). Timeout/cancel/kill-tree/job object were already implemented and tested in S3b (AgentRunnerProcessTests); logs hold lengths only (log.csv records prompt_chars, no prompt text, no keys).
- Real UI smoke (tmp/EAI/EAI-AI/smoke-s4.ps1, evidence artifacts/EAI/EAI-AI/s4/smoke): en and vi at 100% DPI, Agent source select, prompt, Run via UIA, 1 suggestion $0.026 13 s, second run cached. Fixed: Auto-label scroll MaxHeight 230 -> 340.
- Smoke script lessons: mouse clicks on the first radio/Run are flaky (use UIA Invoke for Run; focus + Space for the radio); a close during a run logs Cancelled.
- Owed: 150% DPI smoke (needs the Windows display scale changed; not done, machine-wide setting), SAM2/YOLO-World latency on the target GPU (hardware).
