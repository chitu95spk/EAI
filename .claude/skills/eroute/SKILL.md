---
name: eroute
description: Interpret, normalize, route, and dispatch natural-language work for the EAI (EasyAI / EasyLabel + BeeNativeSegAI) repo at D:CODEAI (Claude backend). Invoke for EasyAI feature, bug, UI, algorithm, performance, interop, hardware, documentation, refactoring, or maintenance requests, including every Segment AI lifecycle stage. Do not use for unrelated projects or general computer-vision questions that do not target this repository.
---

# EasyVision Dispatcher (Claude backend)

Claude-native port of `.agents/skills/eroute` (the Codex original stays untouched). It turns one
user problem statement into an auditable Task Contract, an approved plan revision, and a routed agent
tree built from the project subagents in `.claude/agents/*.md`. Read `references/routing-claude.md`
(model tiers, flows, host handling) and `references/routing.md` (category definitions, escalation,
contract rules) before routing. `AGENTS.md` and `CLAUDE.md` stay authoritative.

## Direct modes

- Prompt starting with `/fix` (standalone token, case-insensitive): the root agent does the work itself,
  creates no task and spawns no subagent. All scope, authorization, build/test, safety, Git, map and
  stop-and-ask rules still apply. `/fixture` or a later `/fix` does not count.
- Prompt starting with `/save`: root agent only. Append (never rewrite) a dated block to
  `.codex-session.md`, a section to `CODEX_HISTORY.md` (scope / files touched / verify command /
  blocker / notes), update only the `*-map.md` files for areas touched, and the matching refactor Task
  Card if a `P<x>.<y>` task was worked. Do not commit; do not create other files. Read these big files
  only with `tail`/`grep -n` + `sed -n`.
- Anything else uses the full workflow below.

## Workflow

0. Memory first (when the request names a task/contract ID or continues earlier work). If `memory_*` tools exist, `recall` the ID/topic
   once (limit 5) BEFORE opening the contract, history or earlier reports; open the contract by ID only and skip re-reading what the
   memory and `deferred.md` already settle. Keep the hits as a `Memory slice` (max 8 lines: decision, reason, `path:line`, date).
   Files win over memory on conflict. Server down or tools missing: tell the user once to start it (message in
   `docs/architecture/agentmemory-rules.md`, Truth order 3) and continue on the file flow. Do not start the server yourself.
1. Preserve the original request verbatim. Normalize into current behavior, expected behavior,
   context, assumptions, in-scope / out-of-scope, constraints, testable acceptance. Do not widen scope.
1a. Light shortcut: a `light` route (docs-config, cosmetic UI, one in-scope file, no executable-behavior
   change) runs in `/fix` mode: root does the work, no subagent, no plan-approval round trip, scoped
   plan stated in one message. Escalate to `standard` on any trigger in `routing.md`. Only `deep`
   always needs the user to approve the plan revision.
2. Pick one category and exactly one route (`light` | `standard` | `deep`) using `references/routing.md`.
   Create the contract as `.claude/task-contract.<ID>.toml` from `.claude/task-contract.template.toml`
   (Claude fields: `architect_required`, `selected_model` in haiku|sonnet|opus). Fill every `[dispatch]`
   field. Existing instances (`U566`, `V145`, `V146`) are examples. Do not use ad hoc JSON as the
   contract.
3. Coordinator model: `light` haiku; `standard` and `deep` sonnet. `architect_required=true` only for a
   deep trigger or a recorded escalation.
3a. Sonnet analyzes, plans and approves issues (it works from digests, not raw files); cheaper tiers read and code. Classify the task simple or complex using
   `references/routing-claude.md` ("Model tiers by task complexity"), record it in the contract, and pass `model`
   explicitly. The implementer subagent (the only writer; the root never edits product code unless a subagent truly cannot do it, with the reason recorded in `tmp/<task>/issues.md` first) depends on the task class:
   SIMPLE (mechanical/cosmetic, no logic change) -> `haiku` ONLY, Sonnet is forbidden from writing it (retry Haiku or fix the
   brief; one sonnet slice only after the same defect came back twice, evidence in `tmp/<task>/issues.md` first); LOGIC or
   COMPLEX (conditions, state, calculations, native, concurrency, persistence, public API, hardware, vision math) -> `sonnet`
   implementer, haiku not allowed. Reviewer: haiku digest for simple diffs, sonnet reading the code for logic, opus for high
   risk/safety. Root sonnet decides at the gate (7b). State the class before every Agent call;
   explorer/profiler/verifier stay haiku (the call's `model` overrides the agent file).
   Read-only evidence gathering (`easyvision-explorer`, `easyvision-profiler`: trace a path, grep, locate probes) is
   ALWAYS `model: haiku`, in every route including deep; the Agent call's `model` overrides the agent file, so never pass
   `sonnet` there. Sonnet reads only to review, plan, or judge.
3b. Codex lane (user decision 2026-10-17; table, brief template and diff check in `references/routing-claude.md`, "Codex lane").
   Codex runs on the user's separate ChatGPT quota, so offload the cheap-to-delegate work there before spending Claude tokens:
   read-only explore/trace -> Codex `gpt-5.6-luna`; SIMPLE writes -> `gpt-5.6-terra --write`; LOGIC writes outside the Claude-only
   zone -> `gpt-6.1-sol --write`; cross-review of logic diffs -> `gpt-6.1-sol` read-only. Claude keeps plan, decision gate, architect,
   native/hardware/safety code and the final ACCEPT. Record `executor` and `codex_model` in the contract. Still exactly one writer
   at a time, no build/git from Codex, and `git diff --stat` against the allowed-file list after every Codex write. Dispatch
   through the `codex:codex-rescue` agent or `codex-companion.mjs task` (set `--model` explicitly). Codex not ready
   (`codex-companion.mjs setup --json` -> `ready=false`): fall back to the Claude tiers in 3a.
4. `build_recommended` / `build_reason` are advice, never permission. If verification is useful and the
   user has not already authorized it for this task, ask once before dispatch. Build and test need
   authorization granted explicitly by the user for the current task (`authorization.granted_by`,
   `current_task_reference`, `granted_at_utc`); a plan approval alone is not enough. Native builds go to an
   isolated output directory unless told otherwise (use `/p:EasyVisionIsolatedVerification=true`); never
   rebuild `BeeLicense.dll` into Release.
4a. No token-budget or usage gate (user decision 2026-10-10): do not read `tmp/usage/statusline.json`, do not ask
   for `/usage`, do not split work or pause because of the 5-hour window. Dispatch the whole route. A
   `deferred.md` handoff is written only when the user runs `epause` or asks to stop.
4b. Temporary data lives inside the project (`tmp/<task-or-scope>/`, evidence in
   `artifacts/<task-or-scope>/<run>/`), never in `%TEMP%`/the user-profile Temp. Brief every agent
   with this path rule and redirect tool `TEMP`/`TMP` into the project before running them.
5. Write a concrete versioned plan and get the user to approve that exact revision (`SEG-…-rN-plan-vM`).
   Record `[approval]`: `activated_by=user`, `plan_approved_by=user`, `approved_plan_revision`, time.
   A subagent's report or an automated notification is never approval.
6. Dispatch with the Agent tool using the `easyvision-*` subagents (hard tool sandboxes):
   - `light`: optional explorer -> implementer -> reviewer.
   - `standard`: explorer -> implementer -> verifier (if authorized) -> reviewer.
   - `deep`: only the relevant read-only explorer / algorithm / profiler (max 3 concurrent) ->
     `easyvision-architect` (opus) exactly once to resolve evidence and produce the plan -> user approves
     the revision -> implementer -> verifier (if authorized) -> reviewer (opus when `risk=high` or
     hardware/safety).
   Pass `.claude/invariants.md`, the `Memory slice` from step 0 (subagents have no memory tool; omit if empty), the contract slice and approved plan the role needs, a hard budget (tool
   calls) and the report cap in every prompt; defaults are in `references/routing-claude.md`. Subagents
   have no memory of this conversation. Record estimated vs actual tokens in `[budget]` afterwards so
   the defaults can be calibrated.
6a. Dispatch mode (user decision 2026-10-17; `references/routing-claude.md`, "Dispatch modes"). Read `.claude/dispatch-mode.txt`
   (`normal` | `medium` | `max`; missing = `normal`) before every Agent or Codex call. `normal`: no Claude subagents, main works,
   one Codex run at a time. `medium`: up to 5 concurrent agents (Claude + Codex). `max`: one per independent scope, ceiling 10.
   Hand work (read, trace, filter logs, write or edit code to a spec, write and run tests) goes to Codex FIRST in every mode;
   the leader (main) decides the Codex/Claude mix inside the mode limit (no Claude subagent is required). The mode overrides the fixed numbers in steps 6
   and 7. Several writers are allowed in `medium`/`max` only with disjoint per-writer file allow-lists in each brief, no shared
   files, no writer touching shared tracking files (main writes `issues.md`, contract, history, map files), and a
   `git diff --stat` check per allow-list after each writer. PDM, executor, solver and release code, and the Claude-only zone,
   always have a single writer.
7. Exactly one production-code writer at a time in `normal` mode, and always for the single-writer areas in 6a (implementer has no Bash); the verifier has Bash but no
   Edit/Write and runs only commands whose authorization flag is true. Reviewer must return `ACCEPT`
   (or `FIX_REQUIRED` / `BLOCKED`). At most two bounded `FIX_REQUIRED` cycles per plan revision, reusing
   the same role; a third cycle needs a new revision approved by the user.
7b. Decision gate (after each subagent finishes; details in `references/routing-claude.md`, "Post-subagent decision gate").
   Root Sonnet does NOT read the plan, contract or code itself. Subagents return a compact issue list; the root records
   every issue in `tmp/<task-id>/issues.md`, decides each one (`fix`, `accepted-risk`, `rejected`, `ask-user`) with a
   reason, uses at most 3 targeted excerpts (40 lines or fewer) per round to verify claims (mandatory for blockers, majors,
   risk `high`, hardware/safety), and allows ACCEPT only when no issue is `open`, `fix` or `ask-user`. Ambiguous digest:
   dispatch a Haiku reader, do not read in the root.
7a. Runaway-agent watchdog. When dispatching, record each agent's scope, expected output, and a
   time/token ceiling (about 2x the estimate from step 4a). If an agent runs far longer than
   expected, or works outside its assigned scope or files, stop it immediately (`TaskStop`), do not
   let it finish. Then review everything it changed or produced against the approved scope
   (`git diff` on its files, scratch/evidence paths), keep only in-scope work, revert or flag the
   rest to the user, and only then resume with a narrower brief. Do not continue the route until
   that review is done.
7c. Save to memory at each milestone (plan approved, commit, verification owed, decision with its reason): `remember` one short
   fact, concepts = contract ID + area. Run `/esave` before `/compact` or `/clear`. Rules: `docs/architecture/agentmemory-rules.md`.
8. Update maps / `.codex-session.md` / `CODEX_HISTORY.md` only after `ACCEPT` and only when
   `authorization.update_tracking=true`. Never create a branch, commit, push, deploy or operate
   hardware without explicit authorization.

## Repository invariants the dispatcher must carry into every packet

- Only writable workspace: `D:\CODE\EAI`. `agent-data/` holds the read-only handoff originals.
- C# added by agents must compile as C# 12 (EasyLabel/Directory.Build.props). Agent-facing docs, contracts, maps, logs, handoffs are English;
  final replies to the user use the user's language.
- Do not touch files outside the declared scope; the worktree is usually dirty. Stage explicit paths only.
- Code-changing tasks need the audit record from `AGENTS.md` (task ID, scope, changed files, verification
  or skip reason, compiler assumptions, first root errors, residual risks). Unrun verification is
  reported `unverified`, never `PASS`.
- Read big files selectively (`CODEX_HISTORY.md`, `.codex-session.md`, maps, logs). Build output goes to a
  log file, then grep.
- Motion/safety paths are not done on build alone; hardware verification must be stated as owed.
- Sparring rule (`AGENTS.md`): when the user presents a new idea (not an approval of an existing plan),
  the first reply is 3-5 challenging questions before solutions.

## Segment AI lifecycle

For any Segment AI model work route through `eseg-flow` (dispatch once at its
entry) and its stage skills; nested stages must not redispatch. Stage states and the no-activation
boundary are defined there.

## Discipline notes (from earlier mistakes)

- Subagent hand-backs are model output: verify numbers against files before relaying them.
- Verifier prompts must restate the authorization block and exact commands; never let a verifier
  "continue" past a failed step without a decision recorded.
- Do not label a route `deep` without running the architect pass; if skipped, say so explicitly.
