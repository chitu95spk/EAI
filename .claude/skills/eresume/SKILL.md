---
name: eresume
description: Resume EasyVision work cold at the start of a session or after hitting the 5-hour limit, reading only the deferred-work file, the recent history tail, the usage snapshot and git status instead of re-reading finished work. Use when the user says continue or resume.
---

# EasyVision resume

Goal: continue exactly where the last session stopped without re-reading or re-reviewing finished work.

## Clean-context model (user decision 2026-10-03)
`/eresume` runs in a CLEAN context. A skill cannot clear context itself (`/clear` is a CLI command, not a tool), so the
user starts a new session or types `/clear` first. From then on the agent knows ONLY what `epause` stored: the
`deferred.md` handoff is the single source of truth, plus the issue ledger `tmp/<id>/issues.md` it points to.
- Do not try to recall or reconstruct the old conversation, and do not reopen finished work. If the handoff lacks something
  needed to continue, say exactly what is missing and ask the user; do not guess from the repository.
- The memory-at-pause is therefore only as good as `epause` wrote it: Task and scope, Done with evidence, Remaining
  slices, Decisions already made, Do NOT redo, Next commands, Risks, Acceptance owed. `epause -Check` must say READY.
- If the context is NOT clean (the old conversation is still loaded), tell the user a new session or `/clear` is cheaper and
  safer, then proceed from the handoff anyway.

0. If `memory_*` tools exist: `recall` the task ID/topic (limit 5) first; it only points to `deferred.md`, the files win on
   conflict. Server down or no tools: ask the user to restart it (message in the rules doc, section Truth order 3), then
   continue with the file flow below without waiting.
   Rules: `docs/architecture/agentmemory-rules.md`.
1. Find handoff files: `ls tmp/*/deferred.md`; pick the newest or the one matching the task ID the user
   names. Read only its LAST section (`grep -n '^## ' deferred.md | tail -3`, then `sed -n 'N,$p'`; the file is append-only and can reach 20+ KB). Read an earlier section only when the last one points to it. Do not reopen files it marks done.
2. EAI has no `CODEX_HISTORY.md`; use `git log --oneline -10` and the deferred file instead (only tail `CODEX_HISTORY.md` / `.codex-session.md` if they exist, `| cut -c1-300`).
2a. `sed -n '/^## Top rules/,/^## Entry format/p' docs/agent-lessons/LESSONS.md` (lessons from past sessions,
   about 10 lines; do not read the rest of the file).
3. `git status --short` and `git log --oneline -5` to confirm what is already committed.
4. Usage/budget is not a concern (user decision 2026-10-10): skip it, never ask for `/usage`.
5. Reuse the existing Task Contract (`.claude/task-contract.<ID>.toml`) named in `deferred.md`; open it by
   ID only, never glob the contracts, and never open a second one.
6. Restate in 5 lines: done, remaining, next command, blockers. Then continue with the first
   remaining slice only. When stopping again, update the deferred file (append, keep paths).
