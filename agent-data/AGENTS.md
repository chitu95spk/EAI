# AGENTS.md

## Workspace Boundary

- The only writable EasyVision workspace for this repository is `E:\Code\EasyVision_Unisen - NoService`.
- Do not edit, build, test, deploy, or run files from `E:\Code\EasyVision_Unisen`; that sibling tree is an older workspace and is read-only unless the user explicitly changes the target. All EasyVision builds and tests must use `E:\Code\EasyVision_Unisen - NoService`.

## Map Files (source of truth)

| File | Prefix | Next ID |
| --- | --- | --- |
| `.structure-map.md` | `S` | see map header |
| `.code-review-map.md` | `R` | see map header |
| `.interop-map.md` | `I` | see map header |
| `.vision-map.md` | `V` | see map header |
| `.optimization-map.md` | `O` | see map header |
| `.ui-map.md` | `U` | see map header |

## Session Log

Progress is saved to `.codex-session.md` after every completed ID.
On restart (new session or token limit hit), read the last block in `.codex-session.md` to find the resume point before touching any map or code.

**Read the tail only:** `tail -n 120 .codex-session.md`. The file is ~1.4 MB and reading it
whole costs roughly 350k tokens, which is itself the main cause of the token-limit restarts
this rule exists to recover from. Never `cat` it. To find one past ID, `grep -n "<ID>"` first,
then `sed -n 'A,Bp'` the matched range.

---

## Context and token discipline (read before Step 0)

Every request resends the whole conversation. Context wasted in the first turn is paid again
in every later turn, so treat reading as a cost.

**Never read a whole large file.** Current sizes in this repo:

| File | Size | How to read it |
| --- | --- | --- |
| `.codex-session.md` | ~1.4 MB | `tail -n 120`, or `grep -n "<ID>"` then `sed -n 'A,Bp'` |
| `.ui-map.md` | ~340 KB | open-rows grep, see below |
| `.code-review-map.md` | ~150 KB | open-rows grep, see below |
| `.interop-map.md` | ~125 KB | open-rows grep, see below |
| `.vision-map.md` | ~70 KB | open-rows grep, see below |
| `.optimization-map.md` | ~56 KB | open-rows grep, see below |
| `CODEX_HISTORY.md` | ~190 KB | `tail -n 200` |
| `docs/architecture/refactor-plan.md` | ~66 KB | only when the task has a `P<x>.<y>` ID |

The map rows are single lines of 800-3000 characters each, so a 377-line map is still 340 KB.
List only the open ones, truncated:

```bash
grep -n '| open |' .ui-map.md | cut -c1-200
```

**Picking an entry in Step 0.3** — do NOT read all six maps. Run that grep on each map; it
returns the open rows as ID / status / risk / title. Then read the full row of the single entry
you select with `sed -n '<line>p' <map>`.

**Build output** must go to a file, never straight to stdout:
```
& "C:\Program Files\Microsoft Visual Studio2\Professional\MSBuild\Current\Bin\MSBuild.exe" EasyVision.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal > build.log 2>&1
Select-String -Path build.log -Pattern ": error " | Select-Object -First 20
```
One `/v:normal` solution build is ~100 KB of output; printing it inline burns ~25k tokens.

**Other rules:**
- Large logs / JSON supplied by the user: write a filter script, save the summary to a scratch
  file, read the summary. Do not read the raw file.
- Do not re-read a file just to confirm an edit landed. The edit fails loudly if it did not,
  and the build is the real proof.
- Do not create reports, changelogs, or docs the task did not ask for.
- In the final summary give the conclusion plus the evidence, not the code you read.

---

## Shared rules with the Claude agent

`CLAUDE.md` (~14 KB, always in context) is the shared source of truth for both agents and
takes precedence where the two overlap:

- Section 0 — Hard Rules (DO / DON'T / stop-and-ask).
- Section 0.2a — Frontend implementation rules for every agent: production UI and custom controls,
  responsive layouts, and no hidden or clipped content.
- Section 12 — Git and commit workflow.
- Definition of Done, standard build commands, quick file map.

`AGENTS.md` adds the Codex-specific parts: the map-driven ID workflow, the build authorization
gate, and the session log format. Where a rule exists in both files, keep it in `CLAUDE.md`
only and link to it here — duplicated rules drift apart.

---

## Agent Permission and Language Rules

- The agent has standing permission to inspect files, edit in-scope files, and run permitted non-destructive static checks. Update maps/session logs only when `authorization.update_tracking=true` or the user explicitly requests tracking.
- Follow `CLAUDE.md` §12.1/§12.4 for current-branch work, large tasks on `main`, automatic completed-task commits, and task-owned staging. Branch/worktree creation still requires the user's request.
- Do not ask for confirmation before normal implementation, planning, refactoring, or permitted static checks within scope. Build and test still require explicit current-task user authorization.
- Ask only when a repository stop condition is hit, when destructive filesystem or git history operations are requested, when a change would require out-of-scope files after a failed build, or when the requested behavior is technically ambiguous and a reasonable assumption would be risky.
- All C# code added or modified by an agent must compile with C# language version 7.3. Do not raise `<LangVersion>` to make agent-authored code compile. Avoid C# 8+ syntax and semantics, and explicitly cast conditional-expression branches to a common C# 7.3 type when required.
- All agent-facing documentation must be written in English. This includes plans, map entries, AGENTS files, session logs, history notes, implementation notes, comments added for future agents, and handoff notes.
- Keep user-facing final summaries in the language the user uses, unless the user asks otherwise.

## Code Change Audit and Compile-Safety Gate

For every code-changing task, before it can be marked `done`, include an audit record in the existing task evidence/handoff, not in a new file. It must contain the task ID, approved scope and preserved invariants, exact changed files, verification commands and results (or the explicit skip reason), C# 7.3/compiler assumptions, first distinct compiler/root errors or `none supplied/found`, and known residual risks. No code task may be marked done or have tracking updated as passed without this record.

When `authorization.update_tracking=true`, record durable audit detail in the existing `CODEX_HISTORY.md` entry required by CLAUDE.md §0.1.8 and a summary in `.codex-session.md`. Otherwise retain the audit only in the final task report and do not modify tracking.

- Run scoped diff, whitespace, conflict-marker, language-version, and static syntax checks. Each authorized static check may be reported `PASS`. Inspect every edited declaration together with its surrounding scope so parser or local-scope errors cannot cascade unnoticed.
- When compiler errors are supplied, triage the first distinct root errors first. Do not chase, count, or report parser-cascade errors as separate causes.
- This gate does not grant build or test permission. Compilation, build, test, runtime, and other executable verification are `unverified` unless explicitly authorized and actually run. If authorized verification fails, fix and reverify within scope or stop under the existing repository rules.

## Sparring Partner Rule

- When the user presents an idea, the first response must be three to five questions that challenge assumptions, uncover blind spots, and surface hidden risks before offering solutions or agreement.
- Challenge the idea constructively: focus on second-order effects, operational risks, technical ambiguity, opportunity cost, and failure modes without dismissing the user's intent.

---

## EasyVision Agent Tree

Skill names and selection examples: [`docs/agent-skill-guide.md`](docs/agent-skill-guide.md).
Read only the relevant section when a skill choice or previous name needs resolution.

The Work project must invoke the project-local `$eroute` skill for every prompt that
targets this EasyVision repository. The dispatcher preserves the original prompt, normalizes it into
a testable Task Contract, selects a route and coordinator model, reviews and approves an exact plan
revision, and then dispatches the complete packet to Codex. Explicit skill invocation is optional;
the skill's implicit-invocation policy is the default Work project behavior.

Use the adaptive agent tree for production-code changes so Implementer and Reviewer can check each
other's evidence, and when the dispatcher supplies `agent_tree = true`, the user asks for agents,
or a running task escalates to `deep`. Documentation and skill-only requests may stay single-agent
unless the user requests the tree. Direct `/fix` mode remains the explicit single-agent exception.

### Direct `/fix` mode

- When the user's prompt begins with `/fix` as a standalone command token, after optional leading
  whitespace and matched case-insensitively (for example `/fix`, `/Fix`, or `/FIX`), the root agent
  handles the request directly from analysis through implementation and permitted verification.
- In direct `/fix` mode, do not create a separate Codex task and do not spawn, dispatch to, or hand
  work to any subagent, including Explorer, Architect, Implementer, Verifier, or Reviewer roles.
- Direct `/fix` mode overrides only the dispatcher and adaptive-agent-tree delegation workflow. It
  does not override the workspace boundary, map and dependency checks, file scope, C# 7.3 limit,
  build/test authorization gate, stop-and-ask triggers, or any safety and Git rules.
- Text that merely contains `/fix` later in the prompt, or starts with a longer token such as
  `/fixture`, does not activate direct `/fix` mode.

### Authority and configuration

- Do not force a root model in `.codex/config.toml`. The filled `dispatch.selected_model` records the
  route coordinator: `gpt-5.6-terra` for `light`, and `gpt-5.6-sol` for `standard` or `deep`.
- Work must pass `dispatch.selected_model` to Codex task creation, with medium reasoning for `light`
  and high reasoning for `standard` or `deep`. If the selected model or effort is unavailable, report
  the mismatch instead of silently falling back to the project default.
- GPT-6 Astra High is available only through `easyvision_architect`. Invoke it when
  `dispatch.astra_required = true`; it makes the final technical plan decision for deep work and
  specialist disagreements, but it is not the root or production-code writer.
- ChatGPT Work may create the Task Contract, activate Codex, and approve a specific plan revision.
- Plan approval is not blanket execution permission. Read the booleans under `authorization` in
  `.codex/task-contract.template.toml` for every run.
- A valid activation records `approval.activated_by = "chatgpt_work"`. A valid approved plan also
  records `approval.plan_approved_by = "chatgpt_work"`, a non-empty
  `approval.approved_plan_revision`, and `approval.approved_at_utc`.
- For a Work-dispatched Task Contract, every authorized write or execution action must record
  `authorization.granted_by`, `authorization.current_task_reference`, and
  `authorization.granted_at_utc`. A direct Codex request uses the current user message as its scope
  and authorization for in-scope edits; do not fabricate Work approval fields. Build and test still
  require explicit user authorization for the current task; plan approval cannot supply it.
- `dispatch.build_recommended` and `dispatch.build_reason` are advisory. When recommendation is true
  and no current-task authorization exists, Work asks the user once before dispatch. A denial leaves
  the action flags false and the missing runtime evidence explicit. If the need is discovered after
  dispatch, Codex pauses once for the same decision.
- Repository rules and stop-and-ask conditions remain binding. No coordinator or architect may
  override a safety boundary, widen scope, or invent missing authorization.

### Routing and automatic escalation

| Route | Intended scope | Coordinator | Agent flow |
| --- | --- | --- | --- |
| `light` | Docs/config metadata and cosmetic UI copy, icon, spacing, color, localization, or layout with no behavior/state effect | Terra | Optional Explorer -> Implementer -> Reviewer |
| `standard` | Localized managed-code bug, refactor, or UI behavior in one known subsystem with stable interfaces | Sol High | Explorer -> Implementer -> authorized Verifier -> Reviewer |
| `deep` | Vision algorithm/accuracy/geometry; native or unmanaged boundaries; lifecycle/threading; performance; public/persisted contracts; hardware/safety; cross-subsystem or high-risk ambiguity | Sol High plus Astra Architect | Relevant discovery specialists -> Architect -> Implementer -> authorized Verifier -> Reviewer |

Escalate `light` to `standard` when discovery finds executable behavior, state, persistence, or more
than cosmetic scope. Escalate any route to `deep` for vision math, P/Invoke/native ABI/unmanaged
ownership, camera lifecycle, concurrency or UI-thread behavior, performance-critical paths,
serialized/public APIs, multiple subsystems, PLC/EtherCAT/DS5/motion/hardware safety, unclear
high-risk invariants, or conflicting specialist evidence.

Every escalation must update `dispatch.route`, `dispatch.astra_required`, the reason, and a new plan
revision. Work must approve that exact revision before source edits resume on Work-dispatched runs;
direct local requests use the root's revised scoped handoff and stop for any required user scope
decision. Never silently de-escalate a deep route. Escalation does not grant additional scope or
permissions.

### Required workflow

1. For a Work-dispatched run, validate its Task Contract, dispatch fields, map ID, dependencies,
   file scope, risk, acceptance criteria, exact plan approval, workspace, and permissions. For a
   direct Codex request, derive the same scope and acceptance checks from the user request and
   repository evidence without inventing Work activation. One run owns one map ID unless the
   user request or contract explicitly allows more.
2. For `light`, use Explorer only when repository evidence is ambiguous. For `standard`, run
   Explorer. For `deep`, launch only the relevant `easyvision_explorer`, `easyvision_algorithm`, and
   `easyvision_profiler` roles concurrently; they are read-only and return compact evidence.
3. On `deep`, wait for discovery, close completed discovery threads, and launch
   `easyvision_architect`. It resolves disagreements and produces one versioned plan candidate.
   For Work-dispatched runs, Work approves that exact revision before source edits; light and
   standard plans also require exact Work revision approval. For direct Codex requests, the root
   records the exact scoped plan in the agent handoff without inventing Work approval.
4. Launch `easyvision_implementer` as the only production-code writer. No other subagent may edit
   source while it runs. The implementer reviews its own scoped diff against the approved plan,
   invariants, and authorization before handoff and records concrete residual risks.
5. Launch `easyvision_verifier` only after implementation and only when relevant. It obeys every
   authorization flag; build, test, and benchmark remain disabled unless their flags are true.
6. Launch independent read-only `easyvision_reviewer`. It checks the implementer's self-review
   against the actual diff and evidence, distinguishes verified from unavailable evidence, and
   returns `ACCEPT`, `FIX_REQUIRED`, or `BLOCKED` with file-specific findings.
7. For each `FIX_REQUIRED` finding, the same implementer either fixes it or gives a source-backed
   rebuttal. The same reviewer then rechecks the response and current diff. The reviewer cannot
   silently dismiss an unresolved finding; the coordinator adjudicates a disagreement, using the
   Architect for deep technical disputes. Reuse the verifier only when a fix changes evidence.
   Stop after two fix cycles and report `blocked` if acceptance is still impossible.
8. On `ACCEPT`, the route coordinator updates the applicable map, `.codex-session.md`, and
   `CODEX_HISTORY.md` only when `authorization.update_tracking = true`, then reports completed and
   unverified evidence separately.

Keep at most three subagents open concurrently. Use parallelism for the read-heavy analysis stage;
implementation, verification, review, and tracking updates are sequential. Independent map IDs may
use separate Git worktrees only when the user authorizes branches or worktrees and the starting Git
state is explicit.

---

## Daily Workflow (multi-step loop)

Use this loop only when the user explicitly requests autonomous map maintenance. For a specific
user request, select only its matching map ID or approved task contract, complete that scope, and
stop. A resume point or priority list never authorizes work on another ID. A process or skill-only
request without a matching map entry does not require inventing an ID.

### Step 0 — Read history
1. `tail -n 120 .codex-session.md` — find the last session block. Never read the whole file.
2. If a "Resume point" exists with a next ID, start from that ID.
3. Otherwise list open entries across the 6 maps with the open-rows grep from "Context and
   token discipline", and pick the highest-priority one. Read the full row only for the entry
   you select.

### Step 1 — Before each ID
1. Check `Status` in the relevant map — confirm entry is still `open`.
2. Confirm no unresolved `Depends on` entry blocks this ID.
3. Rescan files only if `git log --since=1.day -- <files>` shows recent changes AND the entry is marked `stale`. Otherwise trust the existing entry.

### Work priority order
1. **Structure cleanup** — `Sxxx` open, low-risk first (includes duplicate extraction and dead code)
2. **Bug / regression / dead code** — `Rxxx` / `Ixxx` / `Vxxx` (confirmed breaks first, then dead code)
3. **Performance optimization** — `Oxxx`
4. **WinForms UI optimization** — `Uxxx`

### Picking one ID
- Select: `Status=open`, highest workflow priority, then lowest `Risk`.
- Skip if `Risk=high` — pick next.
- Skip if `Depends on` entry is still `open` — pick next.

---

## Build Rules — build is not automatic

### User authorization gate (highest priority)

- Never add a build or test step to a work plan, and never run a build or test command, unless the user explicitly requested it in the current task or explicitly approved it beforehand.
- Risk level, touched boundaries, map notes, prior-session verification, and the trigger table below do not independently authorize a build or test.
- When build or test verification would be useful but has not been authorized, report it as an optional next action and wait for the user's approval. Read-only source inspection and static diff/whitespace checks remain allowed.
- This authorization gate overrides every automatic build/test instruction elsewhere in this file.

**Default:**
- Do not run any build or test without the user authorization defined above.
- Do not run the full solution build unless the user explicitly asks for it or explicitly approves it.
- Prefer focused checks over full solution builds when verification is useful: targeted project build, existing narrow test executable, parser/static checks, or manual code inspection.
- When a build is intentionally skipped, record `n/a — skipped by build policy` in the verify result.

**Never build for:**
- `.md`-only changes
- Adding/fixing `using` directives only (no logic change)
- Renaming a local variable inside one method
- Adding XML doc comments
- Low-risk UI/layout/text/icon-only changes unless the user asks for a build

**If a build is requested or required, build once per batch, not per file or per method:**
- Batch all changes of the same type together, then build once at the end.
- A "batch" is complete when all files in the entry's `Files` scope have been updated.

**Build individually only when a build is already required and:**
- Previous build failed and you need to isolate which change broke it
- The change touches a P/Invoke signature, native interop, or unmanaged memory
- The entry has `Risk=high`

**Build trigger table:**

| Change type | Build? |
| --- | --- |
| `.md` only | Never |
| `using` / doc comment only | Never |
| Low-risk UI/layout/text/icon-only change | Skip unless requested |
| Dead code removal (1–5 methods, same file) | Recommend a focused check if risk warrants; run only when authorized |
| Dead code removal (multiple files) | Recommend a focused check if risk warrants; run only when authorized |
| Duplicate extraction — add shared class + all replacements | Recommend a targeted build/test; run only when authorized |
| Duplicate extraction — partial (some replacements fail mid-way) | Recommend a targeted check to isolate the break; run only when authorized |
| Bug fix in one method | Recommend a targeted test/build when it proves the fix; run only when authorized |
| Any P/Invoke, native interop, or unmanaged memory change | Recommend verification after each touched file; run only when authorized |
| Risk=high entry | Recommend verification after each touched file; run only when authorized |

---

## Step 2 — Implement

### Pre-flight before any extraction or removal (run once, not per method)
1. Collect all `using` namespaces needed by the target shared class — add them all upfront.
2. For duplicate extraction: read ALL copies of the method across all files first, confirm signatures are identical. If signatures differ, note the differences before writing anything.
3. For dead code: `grep -rn "<MethodName>" --include="*.cs" .` — confirm zero callers. If callers found, skip and mark entry `blocked`.

### Execution rules
- Minimal diff — no cleanup outside the entry's `Files` scope.
- **Reference implementation parity is real reuse, not imitation.** When the user asks for code or UI
  to be "the same as" an existing EasyVision tool or feature, reuse or extend the actual production
  implementation: shared model/types, controls, layout composition, binding/events, persistence,
  interaction lifecycle, localization, and runtime path as applicable. Do not create parallel
  adapters, generic controls, duplicated algorithms, or approximate behavior that merely looks or
  acts similar. If direct reuse is blocked, stop and obtain an approved scope revision instead of
  shipping a simulation.
- **Dead code removal**: confirm zero callers → delete all target methods in one pass per file → verify according to the Build Rules above.
- **Duplicate extraction**: create shared class with ALL methods first → replace all copies across all files → verify according to the Build Rules above → only then remove originals if still present.
- Preserve C# ↔ C++ wrapper API: no P/Invoke signature changes.
- Preserve unmanaged memory ownership: `AllocHGlobal` paired with `FreeHGlobal` in `finally`.
- Preserve camera lifecycle: honour Start → Read → Stop sequence.
- Never block the UI thread: no synchronous SDK calls on WinForms dispatch thread.

---

## Step 3 — Verify

Build command:
```
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" EasyVision.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal
```

Only run this after the user explicitly requests or approves the build. The Build Rules table and task risk do not grant authorization by themselves.

If build fails: fix the error, then build again. Do not revert unless the fix would require touching files outside the entry scope.

---

## Step 1b — Periodic dead code + duplicate scan
Run only when all existing Rxxx and Sxxx entries are `done` or `blocked`:

**Dead code:**
```
grep -rn "private.*void\|private.*bool\|private.*int\|private.*double" --include="*.cs" BeeCore/ BeeInterface/Tool/ BeeUi/
```
Verify each: `grep -rn "<MethodName>" --include="*.cs" .` returns only the definition line.
Confirmed dead → add new `Rxxx` (Status=open, Risk=low).

**Duplicates:**
```
grep -rh "void \|bool \|int \|double \|string " --include="*.cs" BeeCore/Algorithm/ BeeCore/Unit/ BeeCore/Func/ | grep -oP "(?<=void |bool |int |double |string )\w+(?=\()" | sort | uniq -c | sort -rn | head -30
```
Method in 3+ files with matching signatures → add new `Sxxx` (Risk=medium if 3-4 files, low if 2).
Only add confirmed issues — not speculative.

---

## Step 4 — Update map + session log

Only when `authorization.update_tracking=true` or the user explicitly requests tracking updates:
1. Set `Status=done` (or `partial`) in the affected map entry after required audit evidence exists.
2. Increment `Next ID` in the map header only if a new entry was added.
3. Append to `.codex-session.md` — add a row to the Completed table and update the Resume point.
Otherwise retain the audit in the final report and leave tracking unchanged.

---

## Step 5 — Loop or stop

For a specific request, stop after its ID or approved scope. Continue to the next ID only in the
explicitly requested autonomous map-maintenance loop and only while no stop condition is met:

| Stop condition | Action |
| --- | --- |
| Build fails and fix requires touching out-of-scope files | Save state, ask user, stop |
| Risk escalates to `high` mid-task | Save state, ask user, stop |
| No more `open` entries | Run Step 1b; if nothing found write "All entries resolved", stop |
| Token budget running low | Save resume point to `.codex-session.md`, stop cleanly |

---

## Session log format

```
| <ID> | <phase> | <done|partial> — <pass/fail/n/a> | <changed files> |
```

Resume point block:
```
### Resume point
- **Next ID**: <Xxx> — reason
- **Blocker**: <none | description>
- **Notes**: <anything future-Claude/Codex needs to know>
```

---

## Required return format
```
ID: <Xxx | n/a — process or skill request without a map ID>
Phase: <structure | bug | perf | ui | process>
Changed files: <list>
Verify result: <pass — N warnings> | <fail — reason> | <n/a — not required>
Next ID: <Xxx | n/a> — reason
```

---

## Stop-and-ask triggers
- Build fails and fix requires out-of-scope files.
- Risk rises to `high` mid-task.
- `Depends on` entry still `open`.
- Caller found during dead-code grep — method is not actually dead.
- Duplicate method signatures differ across files — cannot safely abstract.
