---
name: esave
description: Save the open decisions, milestones and gotchas of the current session into agentmemory (one short fact each) before /compact, /clear, a long context, or ending a session. Use when the user types /esave or asks to save to memory. Also refreshes the CodeGraph index with `codegraph sync`. Writes only agentmemory (or a pending file when the server is down) and the gitignored `.codegraph/` index; never edits repo files or commits.
---

# esave: persist open decisions to agentmemory

Goal: nothing important is lost by `/compact` or `/clear`, and the next session can `recall` instead of re-reading.
`/compact` does not save to agentmemory by itself (no hooks installed), so run this first. Rules:
`docs/architecture/agentmemory-rules.md` (one memory = one fact, <= 3 lines, no code/logs/secrets, files win over memory).

## 1. Collect (from this conversation only; do not re-read repo files)
List candidates, then keep only what a fresh session could not rebuild cheaply from git, the contract or `deferred.md`:
- Decisions with the reason (approach chosen or rejected, user choices and corrections).
- Milestones: `<ID>: plan approved rN`, `<ID>: committed <hash>`, `<ID>: verification owed = <what>`.
- Gotchas that cost time (cause, fix, `path:line`). Hardware/safety claims carry their evidence path or the word `unverified`.
- Open questions or blockers waiting on the user.
Skip anything already in git history, `LESSONS.md`, the Claude auto-memory `MEMORY.md`, or a memory found by step 2.

## 2. Dedupe (1 query)
`recall` with the task ID or topic, limit 5. Do not save a fact that is already there; if one is now wrong, tell the user and
offer `forget` (never delete on your own).

## 3. Save
Per fact: `memory_save` with `content` (<= 3 lines, start with the task ID), `concepts` = task ID + area (2-5 lowercase terms),
`files` = repo-relative paths. User corrections or "always/never" rules go through `lesson` instead. Max 8 saves per run; if
more qualify, keep the highest-impact and say how many were dropped.

Server up but `memory_*` tools missing: save through REST instead:
`curl -s -m 20 -X POST http://localhost:3111/agentmemory/remember -H "Content-Type: application/json" -d '{"content":"...","concepts":["<ID>","<area>"],"files":[]}'` (expect HTTP 201; build the JSON safely, no unescaped quotes).

Server down: do not retry. Append the facts to `tmp/memory-pending.md` (project-local, one line each:
`<date> | <ID> | <fact> | concepts | files`), tell the user to start the server (message in the rules doc, "Truth order" item 3) and
to run `/esave` again; the next run saves the pending lines first, then truncates only the lines it saved.

## 3b. Refresh CodeGraph index
Run `codegraph sync` from the repo root (incremental) so the next session or post-`/compact` context queries current code. 
Skip silently when `.codegraph/` does not exist; on failure report "CodeGraph not synced" and carry on. It touches only the 
gitignored `.codegraph/` index. Runs even when the memory server is down.

## 4. Report (<= 6 lines, user's language mode)
Saved N (list each as `title`), skipped M as duplicate or rebuildable, dropped K, pending P, CodeGraph synced or not. Then say it is safe to `/compact`
or `/clear`, and that the new session should start with `recall <ID>`; or what is still unsaved and why.
