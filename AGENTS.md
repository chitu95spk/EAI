# AGENTS.md — EAI (EasyAI / EasyLabel + BeeNativeSegAI)

Entry point for Codex. Derived from the EasyVision original (`agent-data/AGENTS.md`); the map files, `.codex-session.md`,
`CODEX_HISTORY.md`, `Common.PropetyTools`, EtherCAT and C# 7.3 rules of that repo do **not** apply here.

## Workspace boundary

- The only writable workspace is `D:\CODE\EAI`. `agent-data/` holds read-only handoff originals; never edit it.
- Do not read or write outside the workspace (no `%TEMP%`, user profile, other drives). Scratch goes to `tmp/<task-id>/`,
  evidence to `artifacts/<task-id>/<run>/` (CLAUDE.md §0.2b).

## Shared rules with the Claude agent

`CLAUDE.md` is the shared source of truth and wins where the two overlap. Read and follow:
- §0 Hard Rules (DO / DON'T / stop-and-ask / task flow and audit record), §0.2a frontend rules, §0.2b project-local data.
- §12 Git workflow and the Definition of Done.
- Layout and build/test commands: `docs/architecture/eai-build.md`.

This file only adds the Codex-specific parts. Do not copy rules from `CLAUDE.md` here; duplicated rules drift apart.

## Codex lane (how Claude dispatches you)

- Claude Code is the leader: it plans, owns native/safety and design decisions, reviews/ACCEPTs, builds and commits.
  Codex does the labour it is given: reading/tracing, writing code to a spec, writing tests, filtering logs.
- Work only on the files in the brief's allow-list. Need another file → stop and report; do not widen scope.
- **Never build, test, run git write commands (`add`, `commit`, `stash`, `checkout`, `reset`, `push`) or launch the app**
  unless the brief explicitly authorizes that exact command. Read-only git (`status`, `diff`, `log`, `show`) is fine.
- One writer at a time unless the brief gives you a private allow-list. Never edit shared tracking files
  (`tmp/<task>/deferred.md`, `issues.md`, task contracts, `CLAUDE.md`, `AGENTS.md`) unless the brief says so.
- Task context lives in `tmp/<task-id>/` (plan, `deferred.md` — read its LAST section only) and
  `.claude/task-contract.<task-id>.toml`. Open the contract by ID; do not glob contracts.

## Language and code rules

- C#: language version 12 / net9 as set in `EasyLabel/Directory.Build.props` (nullable and implicit usings enabled).
  Do not change `LangVersion`, target frameworks or central package versions (`Directory.Packages.props`).
  Adding a NuGet package or native dependency is a stop-and-ask item.
- EasyAI.Wpf is Any CPU only; never add `x64` platform/RID settings to it.
- BeeNativeSegAI (C++, v143, x64): ABI = 1; any public struct/export change must bump the ABI and is Claude-owned.
- Match the surrounding code: naming, comment density, MVVM patterns (CommunityToolkit.Mvvm), WPF-UI controls.
- Agent-facing text (plans, notes, comments for future agents, handoffs) in English; user-facing summaries in the user's language.

## Context and token discipline

- Never read large files whole: `grep -n`, then `sed -n 'A,Bp'`. `deferred.md` is append-only: read the last section.
- Logs/JSON: filter with a script, read the summary. Build/test output, when authorized, always goes to a log file under `tmp/`.
- Do not re-read a file you just edited to "check" it.

## Code change audit and compile-safety gate

Every code-changing task ends with the audit record from CLAUDE.md §0.4 in the report (not a new file): task ID, scope and
preserved invariants, exact changed files, verification commands and results or the skip reason, compiler assumptions
(C# 12 / net9), first distinct root errors or `none`, residual risks.

- Run scoped diff, whitespace, conflict-marker and static syntax checks; inspect each edited declaration with its surrounding scope.
- When compiler errors are supplied, fix the first distinct root errors first; do not chase parser-cascade errors.
- Build/test/runtime verification not run is `unverified`, never `PASS`.

## Sparring partner rule

When the user presents a new idea (not an approval of an existing plan), the first reply is three to five questions that
challenge assumptions, surface hidden risks and second-order effects, before offering solutions.

## Build policy (when a brief authorizes a build/test)

- Build once per batch, not per file. Prefer the narrowest check: one project or one test project over the solution.
- Never build for `.md`-only, `using`/doc-comment-only, or local-rename-only changes.
- Build individually only to isolate a failed build, or after a P/Invoke / native interop / unmanaged memory change.
- Close nothing the user runs: if `EasyAI.exe` locks `bin\Release`, use `-p:OutDir=tmp/<task>/out/` or stop and report.

## Required return format

```
Task: <task ID>
Changed files: <list, or none>
Verify result: <pass — N warnings> | <fail — first root error> | <unverified — reason>
Audit: <scope / invariants / compiler assumptions / residual risks>
Open questions or blockers: <list, or none>
```

## Stop-and-ask triggers

- The task needs a file outside the allow-list, or a build fails and the fix is out of scope.
- An in-scope file has overlapping uncommitted edits that cannot be preserved safely.
- A change would break loading saved data (`.segai`, datasets, LabelMe JSON, Online recipes, settings).
- A new dependency, a public API rename, or a native ABI change is required.
- The requested behavior is ambiguous and a wrong guess would be costly.
