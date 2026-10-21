---
name: einfo
description: Smart inventory of EasyVision work - dashboard of task contracts by approval state, date, route, risk and commit status, plus lookup of one ID, skills, agents, deferred handoffs and map task IDs. Use when the user asks which IDs, contracts, plans, skills or agents exist, what is pending or recent, or says list or infor. Read-only.
---

# EasyVision info

Start with the summary, then drill down only where the user asks. Never dump everything.

`powershell -NoProfile -File .claude/skills/einfo/scripts/list-info.ps1 -View <view> [options]`

| View | Use for | Options |
|---|---|---|
| `summary` (default) | Counts by approval/commit/route/risk, plus attention items: plans not approved, high-risk approved work with no commit | none |
| `pending` | Contracts whose plan is NOT approved | `-Filter` |
| `open` | Approved but no commit found (possibly unfinished), default last 30 days | `-Days N` |
| `recent` | Contracts touched in the last N days (default 7), grouped by date | `-Days N`, `-Limit` |
| `group` | Counts and ID lists grouped | `-GroupBy route|risk|category|backend|prefix|month` |
| `id` | One contract in detail: goal, plan, authorization flags, matching commits | `-Id V174` (partial match ok) |
| `skills` | Skill names per runtime; descriptions only for `-Filter <word>` | `-Filter` |
| `agents` | The `easyvision-*` subagents with model and tools | none |
| `deferred` | `tmp/*/deferred.md` handoffs to resume (use `eresume`) | none |
| `tasks` | Task IDs from the map files (`.vision-map.md`, ...) | `-Filter`, `-Limit` |

Common options: `-Filter <text>` keeps lines containing the text; `-Limit N` caps rows (default 25).

Interpretation limits, state them when relevant:
- `committed` means a git subject carries `[ID]`. `no-commit` can still be finished work whose commit message
  lacks the ID, or work that is code-complete but owes hardware evidence. Confirm with `-View id` or `git log`.
- Dates are the contract file modification time, not the task start date.
- `.codex` contracts belong to the Codex backend; a Claude session reuses the existing contract for an ID
  instead of opening a second one.

Reply in the user's language with a short table or list, and say how to act on an ID (`/eresume <ID>`,
or name the ID in a new request). Do not claim a task is finished from this list.
