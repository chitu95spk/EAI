---
name: esum
description: Summarize the work of the CURRENT session only - files this session edited, task contracts it changed and what is still open for them (goal, acceptance evidence, map status, owed/blocker notes, commits), plus commits and worktree changes since the session began. Use when the user asks what this session did, or what remains from it. Read-only. For other sessions or the whole backlog use einfo or eresume.
---

# EasyVision session summary

Scope is this session, not the whole backlog. The window starts when this session's transcript was created.

`powershell -NoProfile -File .claude/skills/esum/scripts/summarize-remaining.ps1 -SessionId <guid> [-Id V174] [-Limit 8] [-NoHistory] [-Since "yyyy-MM-dd HH:mm"]`

- `<guid>` is this session's id: the last folder name of the scratchpad path in the system prompt
  (`...\claude\<project>\<guid>\scratchpad`). Always pass it. Without it the script guesses the newest
  transcript, which is wrong when another session is writing at the same time.
- `-Since` overrides the start time (use it if the transcript cannot be found).
- `-Id` looks up a named contract even if this session did not edit it (still reported, labelled as such).

What it prints:
1. Files edited by THIS session (Edit/Write tool calls in the main transcript, grouped by folder). Edits made
   through Bash/PowerShell scripts or by subagents are not counted; say so if the user asks about them.
2. Commits since the session start and worktree files modified since then. These include other sessions,
   Codex and tools working in the same checkout, so label them "any session" and never claim them as ours.
3. Deferred handoffs written this session.
4. For each task contract this session edited: state, goal, acceptance evidence, authorization flags, map row,
   up to 3 owed/blocker lines from history, commits, next command.

How to answer, in the user's language and compact:
- What this session did (edited files by area), what is committed vs uncommitted, what is still owed, one next
  action.
- A session with no contract edits is normal for process/skill work; say "no contract changed" rather than
  listing other contracts.
- History lines are hints; open the cited range before relaying a blocker as current. "No evidence of
  completion" means no commit with `[ID]` and no map `done`, not proof the work is unfinished.
- Do not edit contracts, maps or history. To continue a task use `eresume`; for all IDs use `einfo`.
