# EAI-COMMERCIAL / EAI-ONL-MODE handoff to Codex (2026-10-18)

Audience: Codex lead. You assign your own Codex subagents. Claude does not dispatch the work below.
Language: English. Repo root: `E:\Code\EasyVision_Unisen - NoService`. Solution area: `EasyLabel/` (net9, Any CPU).

## 0. Rules that bind you (from CLAUDE.md / AGENTS.md)
- Temp and evidence only inside the project: scratch in `tmp/<scope>/`, evidence in `artifacts/<scope>/<run>/`. Set `TEMP`/`TMP` to `tmp/EAI-ONL-MODE` before dotnet. Never `%TEMP%`.
- One writer per file. Allow-list every subagent. Other sessions leave MANY dirty files (see section 6): never `git add .`, never `git stash`, never touch files outside your allow-list.
- Do not run git commit/push; Claude commits. Do not run builds that overwrite `bin/Release` of the legacy EasyVision app. `dotnet build/test` on EasyLabel projects is fine (this handoff authorises it; benchmark on dev PC only; no hardware, no 2-core PC).
- Build/test logs go to a file, then grep (`grep -E ' error |Passed!|Failed'`), never dump to chat. WPF build: `dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release` (Any CPU only; `bin\Release` is the only output).
- Check E: free space (>10 GB) before training or benchmarks. Stale empty `.git/index.lock` is a known nuisance; report it, do not loop on it.
- The user is relabelling `E:\Training\TrainLOGONEW` right now. Do NOT train or sweep on that folder until the user says labelling is done. Tile dataset copies already in `tmp/EAI-ONL-MODEL/tiles` are safe to use.

## 1. Backend state (all committed on main, tests green)
| ID | Commit | What | Tests |
|---|---|---|---|
| CC-1 | 3a2612f8e | EasyLabel.Commands registry, invoker, policy matrix | 133 |
| CC-2 | b6909dfa5 | 43 wrapped VM commands via WpfCommandHost | 143 |
| CC-12 | 83c12457c | run-mode gating, UI.RUNMODE.SET, 14 inert ONL.* | 249 total Commands |
| CC-13 | 75ef114f7 | EasyLabel.Online: Recipe schema, VerdictParser, RecipeEvaluator, RecipeStore (hash gate) | 127 |
| CC-14 | 21a86c601 | OnlineRunner (bounded queue, DropNewest/DropOldest), OnlineMetrics (p50/p95/p99) | +6 |
| CC-15a | bc6b01ec3 | WireCodec (EOL1, raw Mono8, JSON result), OnlineEndpoint (IP allow-list, 32 B token, Busy on overload) | +6 |
| CC-16 | e5e84ad5 | IOnlineControl, ONL.STOP/STATUS/THRESHOLD.SET live, OnlineSession (hot threshold swap), runner.SwapEvaluator | Online 142, Commands 255 |
Source: `EasyLabel/src/EasyLabel.Online/*.cs`, `EasyLabel/src/EasyLabel.Commands/{OnlineControl,OnlineCommandDescriptors}.cs`.
Hot path has NO reference to AgentRunner/PilotService/Ai.Mcp/LLM. Keep it that way.

## 2. Frontend state (committed 6ebcc801, build 0 errors, NOT rendered)
- `EasyAI.Wpf/Commands/OnlineControlAdapter.cs` bridges `IOnlineControl` to the attached `OnlineSession` (Attach/Detach).
- `EasyAI.Wpf/Views/OnlineStatusWindow.xaml(.cs)`, `ViewModels/OnlineStatusViewModel.cs`, `Resources/OnlineStrings{.cs,.resx,.vi.resx}`: status window (counters, p95 vs budget, threshold card, Stop). Written by Codex task-mvdgkwxp-9cv9m2, compiled by Claude, layout never viewed.
- NOT wired anywhere: nothing creates an `OnlineSession`, nothing attaches the adapter, nothing opens the window, `ONL.START` is still reserved (`NOT_IMPLEMENTED`), and `WpfCommandHost` still calls `Discover(...)` without a control. Wiring touches dirty shared files (`MainWindow.xaml(.cs)`, `MainViewModel.cs`, `Strings*`, `AppSettings.cs`, `ShortcutTable.cs`): needs a scope decision from Claude/user first; do not edit them on your own.

## 3. Work packages for you to split (each is independent unless noted)
T1 Unit tests for the WPF pieces (no UI host): `OnlineStatusViewModel.TryParseStatus`, `TryParseThresholds`, `BudgetState`, command CanExecute, adapter Attach/Detach/NOT_RUNNING. `EasyAI.Wpf` has no test project; propose `EasyLabel/tests/EasyAI.Wpf.Tests` (net9-windows, reference Wpf project) only if it builds without dragging the whole app; else mark blocked and say why. New files only.
T2 UIA/visual smoke of `OnlineStatusWindow` at 100% and 150% DPI, vi and en, with a long Vietnamese string, narrow width (360), and idle vs running state. Needs a tiny harness (new project under `tmp/EAI-ONL-MODE/smoke/` or `EasyLabel/tests`) that constructs the VM with a fake running adapter. Capture with PrintWindow, never CopyFromScreen (a private window leaked once); quote all path args. Save PNGs to `artifacts/EAI-ONL-MODE/ui-smoke/<run>/`. Report clipping/overlap findings; do not fix XAML in this task.
T3 Review of `OnlineStatusWindow.xaml` against rule 0.2a (production controls, no stock CheckBox/ComboBox/NumericUpDown, adaptive layout, brushes not hard-coded colours, AutomationIds). Read-only; findings list with file:line.
T4 Backend hardening tests for Online: concurrency stress for `OnlineEndpoint` (many clients, slow client, truncated body, oversize length field, duplicate frameId, client disconnect mid-wait, Dispose while serving), `OnlineRunner` Stop/Submit races, `OnlineSession.SetThreshold` concurrent with frames. Add to `EasyLabel/tests/EasyLabel.Online.Tests` as NEW files only; report any real defect with a failing test instead of editing `src`.
T5 Security review of `OnlineEndpoint`/`WireCodec` (read-only): token compare, allow-list, memory amplification (body read before auth: `ReadRequest` allocates up to `maxBodyBytes` before the token check; assess and propose), receive timeout, thread-per-client limits, no file paths. Findings with severity.
T6 OnlineBench prep (no model needed yet): the uncommitted `--dataset` changes in `EasyLabel/tests/EasyLabel.OnlineBench/*` (7 files, from an earlier window). Review, build (`dotnet build ... -c Release`), run its own help/smoke on a synthetic frame, and add a native 5472x3648 Mono8 synthetic-frame mode if missing so CC-14's final p95 run can be done later. Writer: one agent only for this folder.
T7 Phase A (BLOCKED until the user finishes relabelling): held-out recall on Val tiles only; tile size 1024/1536/2048 at net 512; net 768; cost of tiled inference for one 20 MP frame vs 100 ms. Prior brief and harness commands: `tmp/EAI-ONL-MODEL/codex/brief.md`; findings so far in `tmp/EAI-COMMERCIAL/deferred.md` (last 3 sections). Whole-frame 512 models are useless (recall 0.002); tile training gave recall 0.22 on a train+val mix (not held-out). Thresholds are irrelevant; do not re-sweep them.

## 4. Acceptance owed overall (Claude decides ACCEPT)
EAI-ONL-MODEL: held-out recall number + model decision. EAI-CC: CC-3 slash commands, CC-4 hotkeys, CC-5 settings schema (dirty shared files, per-hunk care, not in this handoff). EAI-ONL-MODE: wiring (section 2), UI smoke, final 20 MP full-chain p95 < 100 ms (iGPU and CPU, quiet machine), Val recall, colour measured separately.

## 5. Report format
Per task: files created (path list), commands run with log paths, numbers copied from CSV/log (not prose), defects found, what stayed unverified. Write to `tmp/EAI-ONL-MODE/codex/results-<Tn>.md`. Do not claim a build/test you did not run.

## 6. Dirty-tree warning
`git status` shows ~100 files modified/deleted by other sessions (`.agents/skills/*`, `.claude/*`, `BeeCore/*`, `BeeCV/version.h`, many `EasyLabel/src/EasyAI.Wpf/*`). They are not yours. After each subagent finishes, run `git diff --stat -- <its allow-list>` and confirm nothing else changed.

## Codex execution plan EAI-ONL-MODE-r1 (2026-10-18)

Direct request: execute this handoff with Codex subagents. Deep route selected for protocol/concurrency and WPF lifecycle evidence. Read-only backend, UI and bench discovery completed; easyvision_architect approved exact revision r1. This is a direct local request, not a fabricated ChatGPT Work activation. This session uses the already-active root runtime; no separately model-selected Codex task was created. Final overall acceptance remains with Claude.

Scope/sequence: T1 report BLOCKED (WPF dependency graph/internal seam); T3/T5 read-only reports; sole implementer adds T4 three new Online test files, then patches T6 Options.cs/Frames.cs/Program.cs/README.md preserving the pre-existing seven-file dataset diff, then creates T2 temporary production-window harness. T6 adds help, deterministic 5472x3648 Mono8 generation and model-free synthetic smoke; any later synthetic inference requires an explicit model and cannot train. T2 uses actual adapter/VM/window plus real OnlineSession/Runner and harness-only fake inference host; capture is PrintWindow only. A native DPI claim requires GetDpiForWindow=96 or144. Existing binary use must identify hash/time and stale-source limitation.

Authorization: HANDOFF focused EasyLabel build/test/smoke only, logs/binlogs redirected locally; TEMP/TMP inside tmp/EAI-ONL-MODE. No production source fixes, XAML changes, shared-file wiring, packages, training, hardware, legacy build, git commit/push, branches/worktrees, or map/history/session updates. T7 blocked by ongoing relabelling. Existing empty index.lock observed and untouched. Keep finite clients, synchronization gates and bounded waits; genuine production defects remain failing evidence, not authorized fixes. New C# uses7.3 syntax; existing project LangVersion unchanged.

Verification: scoped static checks, focused Online tests, bench build/help/model-free smoke, production-window visual evidence when feasible, then independent read-only reviewer with at most two fix cycles. Reports results-T1 through results-T7 retain exact changed files, commands/logs, numeric results, compiler/root errors and residual risks. Missing evidence is unverified. Baseline of192 existing modified files and original OnlineBench diff saved beside this handoff.

## Codex final handoff — EAI-ONL-MODE-r1 (2026-10-18)

| Package | Result | Evidence |
| --- | --- | --- |
| T1 | BLOCKED by WPF dependency/accessibility condition; no ineffective test project created | results-T1.md |
| T2 | PARTIAL: reviewer ACCEPT for harness + manually reviewed 96-DPI en/vi idle/running; UIA and native 144 DPI unverified | results-T2.md; ui-smoke/20261018T072500Z |
| T3 | ACCEPT read-only review; UIA identifier gap and narrow-layout source risk distinguished from observed defects | results-T3.md |
| T4 | ACCEPT diagnostic tests; 147 pass, 1 retained production-defect failure, backend NOT green | results-T4.md |
| T5 | ACCEPT security review; unbounded clients, preauth allocation, and accepted-client disposal findings remain unfixed | results-T5.md |
| T6 | ACCEPT four-file preparation: final build 0 errors/3 warnings; help and model-free 5472x3648 Mono8 smoke PASS | results-T6.md |
| T7 | BLOCKED by ongoing relabelling; no training, sweep or model benchmark | results-T7.md |

Code audit: exact authored files are the three NEW Online.Tests files EndpointHardeningTests.cs, RunnerConcurrencyTests.cs, SessionConcurrencyTests.cs; existing OnlineBench Options.cs, Frames.cs, Program.cs, README.md; NEW temporary smoke/OnlineStatusSmoke.csproj and smoke/Program.cs. Requested results-T1..T7 reports and this handoff were written. No production src, shared UI/wiring, dependency, native ABI, persisted API, model/data, Git history/index, or tracking changes were authorized or made by this team. Existing seven-file dataset work was preserved; the other three dirty bench files retain their original hashes.

Verification: exact commands/logs/binlogs, compiler assumptions, first root errors, risks and reviewer evidence are in each results report. Final T4 intentionally fails only DisposeClosesAcceptedClientConnection (expected EOF -1, actual EOL1 byte69); no source fix. T6 build uses existing C#12 configuration with newly authored C#7.3 syntax; no project LangVersion changed. T4 and temporary WPF harness compile as C#7.3. T2 final build is 0/0, but manual visual inspection supplies capture validity: automated ValidatePixels remains unreliable and old0900/0915 captures are rejected. No inference latency, model quality, native144-DPI, or UIA PASS is claimed. Two bounded fix cycles were consumed for T2; full acceptance remains with Claude.

Root static audit: scoped diff/whitespace/conflict-marker checks passed for tracked bench changes and enumerated new tests; final temporary harness checked separately. Baseline192 existing modified-file hashes changed only in the four owned bench files; no additional existing tracked files became modified. Original deleted/untracked work remains outside team ownership. No commit/push, branch/worktree, build of legacy EasyVision, training, hardware operation, or map/history/session update was performed. Empty index.lock was observed initially and not touched.

Next: Claude decides the production endpoint hardening scope, T1 testing seam, remaining UIA/144-DPI verification and wiring. T7 remains gated until explicit relabelling completion; final CPU/iGPU 20MP p95 and held-out recall are still owed.
