# EasyVision Dispatch Policy — Claude backend

Companion to `references/routing.md` (the Codex/OpenAI backend). Use this file when the active
agent runtime is Claude (Claude Code, the Claude Agent SDK, or Cowork), detected via
`agents/claude.yaml`. Route categories, contract fields, and safety invariants are identical to the
Codex backend; only model selection and per-role tool enforcement differ.

## Why the model tiers don't mirror Codex 1:1

Codex's three aliases (`gpt-5.6-terra` default, `gpt-5.6-sol` stronger, `gpt-6-astra` top-tier) are
spent as: Terra on every grunt-work role (explorer, implementer, verifier, profiler), Sol on every
judgment role (reviewer, algorithm) and as coordinator for `standard`/`deep`, Astra once per `deep`
task as architect only.

Claude's three tiers (`haiku`, `sonnet`, `opus`) have a wider capability gap between the bottom and
middle tier than Codex's aliases appear to. This project edits native interop, camera lifecycle, and
PLC ladder logic on a production line — `haiku` is cheap and fast but is not trusted as the sole
production-code writer or as the correctness-judgment role on that surface. So the mapping is
capability-first, then cost-optimized within that constraint:

| Codex role class | Codex model | Claude model | Reasoning |
| --- | --- | --- | --- |
| Bulk read-only discovery (explorer, profiler, algorithm survey) | Terra | `haiku` for pure grep/read sweeps; `sonnet` once the role must weigh tradeoffs | Discovery is high-volume, low-judgment-per-token — cheap model, architect catches misses downstream |
| Implementer (sole writer) | Terra, high effort | `sonnet` | Writing safety-adjacent production code is not where we economize |
| Verifier (build/test only) | Terra, medium effort | `sonnet` | Needs to correctly interpret failures, not just execute commands |
| Reviewer / algorithm specialist | Sol, high effort | `sonnet`, escalate to `opus` when `risk="high"` or the change touches hardware/safety | Matches Codex's practice of spending more on judgment than on execution |
| Architect (deep only) | Astra, high effort | `opus` | Rare, expensive, used once per packet for synthesis — never for bulk work |

Net effect versus a naive 1:1 remap: fewer `opus` calls than an "always use the best model" approach,
but no downgrade on the two roles (implementer, reviewer-when-risk-is-high) where a wrong call is
expensive to discover later.

## Cost controls (added 2026-10-03)

- Every brief carries `.claude/invariants.md` plus only the Task Contract slice the role needs; agents
  do not read AGENTS.md/CLAUDE.md whole.
- Every brief states a hard budget and a report cap (about 300 words). Initial defaults, to be
  calibrated from `[budget]` actuals: explorer 25 tool calls, algorithm/profiler 20, architect 15,
  implementer 40, verifier 15, reviewer 25. At the limit the agent stops and reports; it does not run on.
- Implementer briefs (it has no Bash, so nothing it writes is compiled before the verifier runs, L009): require fully
  qualified names for types outside the file's `using` list (e.g. `BeeGlobal.Localization.LanguageStore`,
  `BeeCore.OcrRuntime.*`) especially in `tests/.../Program.cs`, C# 7.3 only, and a list of every file it changed.
  Verifier briefs: on a failed build report ALL distinct `: error ` lines with file:line, run the independent harness
  modes even if one fails, and quote the failing assertion line rather than the stack frame.
  Measurement briefs (L026): name the exact input image/file and region; require the run to go to a NEW run folder; the report
  must show the OLD-code result next to the new one, and if OLD does not show the user's symptom the verdict is INCONCLUSIVE
  (wrong input). The root quotes numbers only from the output files, never from the hand-back.
  Explorer/profiler briefs (L020): search named directories only (e.g. BeeCore, BeeInterface, Pattern, BeeGlobal) with
  `--include=*.cs` or the right extension; never `grep -r` from the repo root (bin/obj/ThirdParty make it time out).
- Route any code change beyond a trivial one-line fix through this skill and the agents; do not edit and test directly
  as root first (user correction, L011).
- Haiku for standard-route explorers is a trial: record whether the downstream architect/reviewer found
  missed paths. If it did on 2 tasks, revert to sonnet.
- Contracts are opened by ID only (named by the user or `deferred.md`); never glob
  `.claude/task-contract.*.toml`. Old contracts stay in place because history/map files reference them.

## Model tiers by task complexity (user decision 2026-10-03)

Sonnet 5.5 is the analyst, planner and approver: it analyzes the request and the evidence digests that Haiku readers
bring back, writes the plan, dispatches, and approves or rejects every issue before ACCEPT (decision gate below). Reading
raw files and writing code go to the cheapest tier that can do it safely. This supersedes the older "implementer = sonnet" rows
above (kept for history).

| Role | Simple task | Complex task |
| --- | --- | --- |
| Coordinator: analysis of digests, plan, approval of issues, ideas | `light` haiku; otherwise **sonnet** | **sonnet** (analysis and plan; opus architect only for deep) |
| Explorer / profiler (read-only evidence gathering: trace, grep, locate probes) | haiku | **haiku** (never sonnet; judgment goes to algorithm/architect) |
| Implementer (sole writer) | **haiku** (Sonnet forbidden) | **sonnet** (logic / complex; haiku not allowed) |
| Verifier (runs approved commands) | haiku | sonnet when failures need interpretation |
| Reviewer agent (reads plan, contract, diff and reports) | haiku ONLY for mechanical/cosmetic diffs (text, layout, docs, config): checklist and issue digest | **sonnet reads the code** for any change that touches logic; opus when `risk=high` or hardware/safety |
| Root Sonnet (decision gate) | decides every issue in the ledger from the digest, then ACCEPT | same; spot-checks high-risk claims |
| Architect (deep only) | not used | opus, once |

Simple = ALL of: mechanical or cosmetic change with NO new or changed logic (conditions, state, calculations, error
handling, data flow); at most 2 production files and about 80 changed lines; no native/C++, interop, concurrency or lifecycle,
persistence/serialized contract, public API, hardware/PLC/EtherCAT/Ladder output, vision math, or security/licensing; the
plan names the exact files and edit; acceptance is checkable by a build or a small harness. Typical: copy/labels, layout and
cosmetic UI, option wiring that follows an existing pattern, docs/config, mechanical renames inside one file.
Anything else is complex. When unsure, classify complex and say why in the contract (`risk`, `notes`).

Rules:
- Pass `model` explicitly on every Agent call (the frontmatter default is haiku for implementer, explorer and profiler).
  The `model` argument OVERRIDES the frontmatter, so an explorer or profiler call with `model: sonnet` silently defeats
  this table (observed 2026-10-03: a `Map ... lag path` explorer ran on Sonnet 5.5). Before each Agent call check the
  role against this table; read-only explorer/profiler calls must say `haiku`.
- A haiku implementer gets the most precise brief: exact file, anchor text, the change, invariants digest, report cap.
  It returns the diff summary and an issue list; the reviewer agent reads the actual diff, and root Sonnet decides every
  issue from that digest at the decision gate below (with mandatory spot-checks for high risk), never from the implementer's
  own summary alone.
- **Who writes code (user decision 2026-10-03, final):**
  - SIMPLE work (mechanical/cosmetic, no logic change, within the simple definition): `model: haiku` only. Sonnet is
    FORBIDDEN from writing it, even if Haiku struggles once; fix the brief or retry Haiku first. Only a documented repeated
    Haiku failure (same defect twice after bounded fix requests) allows one sonnet slice, with the evidence recorded in
    `tmp/<task>/issues.md` first.
  - Work with LOGIC or COMPLEX work (conditions, state, calculations, error handling, native, concurrency, persistence, public
    API, hardware, vision math): `model: sonnet` implementer is allowed and expected; haiku must not write it. Opus reviews
    when risk is high or safety/hardware.
  - The ROOT session never writes product code when a subagent can do it (user decision 2026-10-03): all code goes through the
    implementer subagent, which is the single writer. The root may edit code itself only when a subagent truly cannot do it
    (for example the harness denies the agent a tool it needs, the agent failed twice and its model tier was already
    escalated, or the change is a one-line fix whose dispatch costs more than the edit). Before doing so, state the reason
    and the evidence in one line and record it in `tmp/<task>/issues.md`. Skill, rule, doc and memory files are not
    product code and the root may edit them.
  - Before every Agent call state which class the task is in and pick the model from this rule.
- Model names are limited to the Agent tool's overrides (`haiku`, `sonnet`, `opus`, `fable`). A specific older version such
  as "4.8" cannot be selected; `haiku` means Haiku 4.5.

## Codex lane (user decision 2026-10-17)

Goal: cut Claude token spend by sending cheap-to-delegate work to Codex (separate ChatGPT quota) while Claude keeps judgment and
safety. Available Codex models (`~/.codex/models_cache.json`): `gpt-6.1-sol` (workhorse), `gpt-6-sol`, `gpt-5.6-sol`,
`gpt-5.6-terra` (balanced, straightforward work), `gpt-6-luna` / `gpt-5.6-luna` (fast, cheap), `gpt-6-astra` (frontier; only on
user request or a second diagnosis pass for a hard bug).

| Work | Executor | Codex model | Mode |
| --- | --- | --- | --- |
| Explore / trace / grep / long reads | Codex (Claude haiku if Codex not ready) | `gpt-5.6-luna` or `gpt-6-luna` | read-only |
| SIMPLE write (definition above) | Codex (Claude haiku fallback) | `gpt-5.6-terra` | `--write`, file allow-list |
| LOGIC write, one area, outside the Claude-only zone | Codex | `gpt-6.1-sol` | `--write`, file allow-list |
| Cross-review of a logic diff | Codex read-only, then Claude decides | `gpt-6.1-sol` | read-only |
| Native/C++, EtherCAT/DS5, Ladder output, concurrency, `Ds5Api.h`, persistence/serialized contract, public API, licensing | **Claude sonnet only** (Codex never writes) | - | - |
| Plan, decision gate 7b, architect, ACCEPT, high-risk review | **Claude** | - | - |
| Verifier (approved build/test) | Claude haiku (Bash, authorization flag) | - | - |

Cross-review is optional by default (run it when the user asks or `risk` is medium/high); it never replaces the Claude gate.

Rules:
- One writer at a time, Codex or Claude, never both. A Codex write is a subagent write: the root still does not edit product code.
- Brief for Codex (it has no memory of this conversation and its local config is `danger-full-access`, so scope lives in the brief):
  task and exact files (allow-list), `.claude/invariants.md` digest, C# 7.3, project-local temp (`tmp/<task>/`), "do not build, test,
  or run git", "do not touch other files", report cap, reply in English. Pass `--model` explicitly; leave `--effort` unset unless asked.
- After every Codex write: `git diff --stat` + `git status --short` against the allow-list. Anything outside the list is reverted
  (only those hunks) and recorded in `tmp/<task>/issues.md`; then continue with a narrower brief. This is the watchdog 7a check.
- Codex output is model output: record its issue list in `tmp/<task>/issues.md` and decide at gate 7b; verify numbers against files.
- Long or open-ended Codex runs go `--background`; apply the 7a runaway rule (`TaskStop`, review diff) when it exceeds ~2x estimate.
- Contract fields: `executor = "claude" | "codex"` and `codex_model` (empty when claude).

## Post-subagent decision gate (user decision 2026-10-03)

Root Sonnet does not go and read the plan, the contract or the code. Cheap readers (Haiku) and the reviewer agent read them
and hand back a compact digest; Sonnet, AFTER the subagent has finished, reviews that digest, decides every issue, and only
then allows ACCEPT. Nothing is accepted on a subagent's say-so alone, and nothing is accepted while an issue is open.

1. **Report format.** Every implementer, verifier and reviewer hand-back ends with an issue list in this form, at most
   300 words in total: `ID | severity (blocker/major/minor/info) | evidence path:line or log path | what is wrong | proposed
   resolution`. The reviewer also returns a plan-conformance table (each plan item MATCH / GAP / UNVERIFIED with evidence).
2. **Issue ledger.** The root keeps `tmp/<task-id>/issues.md` (project-local, English): one row per issue with status
   `open`, `fix` (sent back to the implementer), `accepted-risk` (decided, with the reason), `rejected` (not a defect, with the
   reason) or `ask-user`. Rows are only added or updated, never deleted, so a later session can resume from it.
3. **Decide each issue.** Sonnet decides every `open` row from the digest and records the reason. Blockers and majors need
   `fix` or an explicit user decision; minors and info may be `accepted-risk` or `rejected` with a stated reason.
4. **Read budget for the root.** No whole-file reads of the plan, contract or code. Use the contract digest
   (`esum`/`einfo`) and the agents' digests. To verify a claim the root may do at most 3 targeted excerpts of 40 lines or
   fewer per review round (`path:line`), and MUST do at least one for any blocker/major, for risk `high`, and for hardware or
   safety code. If a digest is ambiguous, send a Haiku reader for a sharper digest instead of reading in the root.
5. **ACCEPT gate.** ACCEPT only when: the ledger has no `open`, `fix` or `ask-user` rows; the reviewer returned ACCEPT or
   its findings are all decided; verification is reported as measured or `unverified`; scope and authorization are intact.
   Otherwise `FIX_REQUIRED` (reuse the same implementer, at most two cycles) or `BLOCKED`.
6. **What a digest cannot prove.** A haiku reviewer can miss logic, concurrency and lifecycle defects, so reading and
   judging code that contains logic is Sonnet's job (user decision 2026-10-03): the reviewer agent is sonnet for ANY change
   that touches logic, and the same applies to implementing pure-logic work (it is complex, see the simple definition). For
   risk `high` work the reviewer agent is sonnet (opus when high risk or safety), and the root's spot-checks are mandatory. If a
   spot-check contradicts a digest, discard that digest, record the issue, and re-dispatch.

## Route matrix

| Route | Typical categories | Coordinator | Architect | Default verification |
| --- | --- | --- | --- | --- |
| `light` | `docs-config`, `ui-cosmetic` | `haiku` | No | Static and diff checks; build normally not recommended |
| `standard` | `ui-behavior`, `managed-bug`, `managed-refactor` | `sonnet` | No | Recommend the narrowest build/test for executable state or logic changes |
| `deep` | `vision-algorithm`, `native-interop`, `concurrency-lifecycle`, `performance`, `hardware-control`, `cross-subsystem` | `sonnet` | `easyvision-architect` on `opus`, one pass per packet | Recommend focused build/test/benchmark only when it proves an identified risk |

Category definitions are unchanged from `references/routing.md` — reuse them verbatim.

## Dispatch modes (user decision 2026-10-17)

The mode is one word in `.claude/dispatch-mode.txt` (`normal` | `medium` | `max`), set by the skills `enormal`, `emedium`, `emax`.
The dispatcher reads it before every Agent or Codex call; file missing or unreadable means `normal`. The mode overrides every
fixed concurrency number elsewhere in this skill ("exactly one writer", "max 3 concurrent", CLAUDE.md "max 1 Claude subagent").

| Mode | Concurrent agents (Claude subagents + Codex runs) | Writers |
| --- | --- | --- |
| `normal` | 0 Claude subagents; main does the work. One Codex run at a time for hand work | 1 (main or one Codex run) |
| `medium` | up to 5 | several, under the conditions below |
| `max` | one per independent scope, ceiling 10 | several, under the conditions below |

Hand work first: in every mode, manual labour (reading, tracing, log filtering, writing or editing code to a spec, writing and
running tests) goes to Codex first when it is ready. the leader (main) decides the mix of Codex runs and Claude subagents within the mode limit; the limit replaces the old fixed "max 1 Claude subagent" cap and no Claude subagent is required.
Main keeps plan, decision gate 7b, architect, ACCEPT, native/safety work and shared tracking files.

Multiple writers (`medium`, `max`) need all of:
1. Each writer has its own file allow-list written in its brief.
2. No file appears in two allow-lists; no two writers edit the same file.
3. No writer edits a shared tracking file (`tmp/<task>/issues.md`, the task contract, `CODEX_HISTORY.md`, `.codex-session.md`,
   map files, plan docs). Main alone writes them.
4. After each writer finishes, run `git diff --stat` + `git status --short` against that writer's allow-list (watchdog 7a); revert
   out-of-list hunks and record them.

Always one writer, in every mode: PDM, executor, solver and release code (wrong edits there cost far more). The Claude-only zone in
the Codex lane table (native, EtherCAT/DS5, Ladder output, `Ds5Api.h`, persistence, public API, licensing) also stays single-writer,
Claude sonnet only. When a task mixes a single-writer area with other areas, run the single-writer part alone and parallelize the rest.

All other rules are unchanged: roles, models, gate 7b, authorization, build/test and commit gates apply in every mode.

## Agent flows

- `light`: optional `easyvision-explorer` (haiku) -> plan -> `easyvision-implementer`
  (haiku) -> `easyvision-reviewer` (sonnet).
- `standard`: `easyvision-explorer` (haiku for a pure execution-path trace; sonnet when it must weigh
  tradeoffs) -> approved plan -> `easyvision-implementer` (haiku if simple, sonnet if complex) ->
  `easyvision-verifier` (haiku if simple, else sonnet) when authorized -> `easyvision-reviewer` (sonnet).
- `deep`: launch only the relevant read-only `easyvision-explorer`, `easyvision-algorithm`, and
  `easyvision-profiler` roles, at most three concurrently. Explorer and profiler gather evidence (trace a path,
  find existing probes/logs) on `haiku` ALWAYS; only `easyvision-algorithm`, which must compare options, runs on
  `sonnet`. Tracing or mapping a path is never a reason for sonnet: the architect and reviewer catch misses. Then run `easyvision-architect` (`opus`, one
  pass) to resolve evidence and produce the plan. After Work approves that revision, run
  `easyvision-implementer` (sonnet; deep work is complex), authorized `easyvision-verifier` (sonnet), and
  `easyvision-reviewer` (sonnet, escalate to `opus` when `risk="high"`) sequentially.

The deep coordinator stays `sonnet`; `opus` is a conditional architect, not the root — the same
principle as the Codex backend's "Astra is a conditional architect, not the root."

## Host-specific execution

Two hosts can run this backend; detect which one is live before dispatch and follow its constraints.

### Claude Code (this repository opened with the Claude Code CLI / Claude Agent SDK)

Use the `Task` tool with the custom subagents defined in `.claude/agents/*.md`
(`easyvision-explorer`, `easyvision-algorithm`, `easyvision-profiler`, `easyvision-architect`,
`easyvision-implementer`, `easyvision-verifier`, `easyvision-reviewer`). Each file's `tools:`
frontmatter is a hard sandbox, not just an instruction: the five read-only roles carry no `Edit`,
`Write`, or `Bash`; the implementer carries `Edit`/`Write` but no `Bash`; the verifier carries `Bash`
but no `Edit`/`Write`. This is a stronger guarantee than Codex's `sandbox_mode`, which still lets a
`workspace-write` role touch files it is only instructed not to.

### Cowork / the Claude Agent SDK's fixed roster (this session)

There is no custom-subagent mechanism here — only a fixed roster (`claude`, `Explore`, `Plan`,
`general-purpose`, plus two irrelevant to this project) with a `model` override
(`haiku` | `sonnet` | `opus`). Map roles onto it like this:

| Role | Subagent type | Why |
| --- | --- | --- |
| explorer, profiler, algorithm, architect, reviewer | `Explore` (discovery) or `Plan` (judgment/plan output) | Both exclude `Edit`/`Write` in this host, giving the same hard read-only guarantee as the Claude Code role files |
| implementer | `general-purpose` or `claude` | Only these carry `Edit`/`Write` here |
| verifier | `general-purpose` or `claude`, instructed not to touch source | **Soft constraint only** — this host has no role that grants `Bash` without also granting `Edit`/`Write`. Compensate by having the reviewer diff-check that the verifier touched nothing outside its command log, and keep the verifier's prompt scoped to "run exactly these commands, edit nothing." |

Always pass `model` explicitly per the table above — the roster's default model is this session's own
model, which is more expensive than the per-role assignment calls for.

Single-writer and max-three-concurrent-discovery-agent rules are enforced by dispatch discipline
(never launch a second implementer, never launch a fourth discovery agent) since neither host
enforces concurrency limits at the tool level.

## Task Contract rules

Same fields and meaning as `references/routing.md`, with one rename: `astra_required` becomes
`architect_required` in `.claude/task-contract.template.toml`, since "Astra" is Codex-specific
vocabulary. `architect_required=true` only for a deep trigger or a recorded escalation, exactly as
before.

## Automatic escalation

Identical triggers to the Codex backend: escalate `light` to `standard` on any executable-behavior,
state, persistence, or more-than-cosmetic-file change; escalate any route to `deep` on vision math,
native/unmanaged boundaries, threading/lifecycle constraints, performance-critical behavior, hardware
control, public/persisted contracts, cross-subsystem scope, unclear high-risk invariants, or
conflicting evidence. Record the new route, `architect_required`, reason, affected plan revision, and
any newly unavailable acceptance evidence. Never silently de-escalate a deep route or expand scope.

## Completion safeguards

Same as the Codex backend: preserve the original prompt beside the normalized request, validate the
exact workspace before dispatch, never claim a task was created when tooling is unavailable, require
`easyvision-reviewer` `ACCEPT` while treating safety/authorization gates as higher priority than that
verdict, and on `FIX_REQUIRED` reuse implementer/verifier/reviewer for at most two bounded cycles
before reporting `BLOCKED`.
