---
name: epause
description: Pause the current work cleanly when the 5-hour token budget is nearly used up and hand it to the next session - stop agents, finish or park the in-flight step, write a self-contained tmp/<task>/deferred.md handoff, and tell the user how to resume. Use when the user types /epause, says pause, or when the usage snapshot shows the 5-hour window at 85% or more. Writes only the handoff file (plus agentmemory facts and a `codegraph sync` index refresh); commits nothing.
---

# EasyVision pause and handoff

Goal: the next session continues from the exact point, without re-reading or re-reviewing finished work. Written in English
(stored artifact); the message to the user follows the active language mode.

## When to trigger
- The user asks. Or, before any dispatch or long step, `tmp/usage/statusline.json` shows `rate_limits.five_hour.used_percentage`
  at 85 or more, or the remaining window is smaller than the next slice estimated by `eroute` step 4a.
- A stale or missing snapshot is unknown, not safe: ask the user for `/usage` instead of guessing.

## 1. Stop cleanly (no new expensive work)
1. Do not start new agents or builds. Stop agents that are running (`TaskStop`) unless they finish within a minute, and keep
   their partial output paths.
2. Finish only what is already half-applied and cheap to finish (a single in-progress edit). Never leave a file half-edited or
   an implementer mid-change: either complete the bounded step or record exactly what is unfinished.
3. Do not commit or build just to "use up" the budget. If the code state is coherent and checks already passed, mention
   `/ecommit` as the first next step; otherwise leave it uncommitted and say why.

## 2. Write the handoff
`powershell -NoProfile -File .claude/skills/epause/scripts/pause-handoff.ps1 -SessionId <guid> -TaskId <contract ID or slug> [-Reason "..."]`
(`<guid>` = last folder of the scratchpad path in the system prompt, as in `esum`.)
It creates or appends a dated section to `tmp/<TaskId>/deferred.md` (never overwrites earlier sections) with the facts filled in:
session id, usage snapshot (flagged STALE when old), contracts edited, commits since session start, files this session edited and
which are still uncommitted. Then replace EVERY `TODO(claude)` line with real content:
Task and scope; Done (verified) with evidence paths; Remaining as numbered slices that each fit one window; Decisions already made
(with reasons); Do NOT redo; In-flight agents and background work; Next commands (copy-paste, with authorization state); Risks and
unverified; Acceptance still owed. If `tmp/<TaskId>/issues.md` exists, put its path and the open/fix/ask-user rows in "Risks and unverified" so the next session
resumes the decision gate instead of re-reviewing. Use `path:line` and log paths under `artifacts/`; no pasted code or logs. Run the same command with
`-Check` until it prints READY.

### 2a. Save to memory (automatic, after `-Check` printed READY; do not ask the user first)
Run the `esave` procedure (`.claude/skills/esave/SKILL.md`) for this session: open decisions, milestones, gotchas, one
short fact each, deduped with `recall`. Then `remember` the pointer: `<TaskId> paused: <deferred.md path>; next: <first
remaining slice>`, concepts = TaskId + area. `deferred.md` stays the full handoff; memory holds only short facts and the pointer.
Server down or tools missing: follow `esave` fallbacks (REST, or `tmp/memory-pending.md`) and say in the final message that
memory was not saved and the user must start the server and run `/esave`. Never block the pause on memory.
Rules: `docs/architecture/agentmemory-rules.md`.

### 2b. Refresh CodeGraph index (automatic, never blocks the pause)
Run `codegraph sync` from the repo root (incremental, only files changed since the last index) so the next session's 
`codegraph_explore` sees this session's edits. Skip silently when `.codegraph/` does not exist. Command missing or failing: 
say "CodeGraph not synced" in the final message and continue. It updates only the gitignored `.codegraph/` index, never repo files.

## 3. Optional tracking
If the contract has `authorization.update_tracking=true` or the user asks, also run `/save` (session, history and map entries).
Do not edit those files otherwise. Run `/eskill` only if the user wants the retrospective now; it costs tokens too.

## 4. Tell the user (language mode)
Short: why paused, usage at pause (or "unknown"), what is done, what remains, the handoff path, memory saved (N facts, or
"not saved: server down"), CodeGraph synced or not, and the exact resume line:
`/eresume <TaskId>` in a new session after the window resets (reset time from the snapshot when it is fresh). Say plainly what is
uncommitted and which checks are still `unverified`.
