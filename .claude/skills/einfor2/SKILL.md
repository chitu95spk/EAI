---
name: einfor2
description: Show the contracts that are currently PAUSED - one line per paused task with contract name, route, risk, pause time and whether the handoff is complete, plus a detail view of one paused task (usage at pause, first remaining step, open issues, resume line). Use when the user types /einfor2 or asks which contracts are paused, or what can be resumed. Read-only. For all contracts use einfo.
---

# EasyVision paused-work info

Paused = a handoff file `tmp/<TaskId>/deferred.md` exists (written by `epause`). This skill only lists them.

`powershell -NoProfile -File .claude/skills/einfor2/scripts/list-paused.ps1 [-Id <TaskId>] [-Limit 20]`

- No `-Id`: one line per paused task, newest first: TaskId, pause time, route, risk, handoff state, contract name (title read
  from `.claude/task-contract.<id>.toml` or `.codex`, also trying the id without its `-rN` suffix).
- `-Id O099-r4` (partial match ok): contract file and name, route/risk, pause time and reason, usage at pause (flagged STALE when
  old), uncommitted session files at pause, first remaining step, open issues in `tmp/<id>/issues.md`, handoff path, resume line.

How to read it:
- `READY` = the latest pause section has no `TODO(claude)` left. `INCOMPLETE(n)` = n placeholders still open, so the handoff is
  not trustworthy and the pause should be finished first (`epause -Check`).
- A task is listed while its `deferred.md` exists; the script does not know whether the work was finished later. Cross-check
  with `einfo -View id -Id <ID>` and `git log` before resuming something that may already be done.
- `(no title: contract missing)` means no contract file matches that folder name (for example a slug-named task).
- Test folders starting with `_selftest` are hidden.

Answer in the user's language mode with a short table. Resume line to give: `/clear` (or a new session), then
`/eresume <TaskId>`. This skill never resumes, edits or deletes anything.
