---
name: enormal
description: Set the dispatch concurrency mode to NORMAL - no Claude subagents run at the same time; the main session does the work itself. Use only when the user types /enormal or explicitly asks to switch to normal mode. Stays active until /emedium or /emax is invoked.
---

# Dispatch mode: NORMAL (no Claude subagents)

Invoked: write the single word `normal` to `.claude/dispatch-mode.txt` (overwrite the file), then confirm once in one
line ("Dispatch mode: NORMAL - main does the work, no Claude subagents."). The mode persists across sessions until
another mode skill overwrites the file. The full policy lives in `.claude/skills/eroute/references/routing-claude.md`,
section "Dispatch modes".

## Rules in this mode
- Claude subagent limit: 0. The main session does planning, reading, coding and review itself. Do not call the Agent
  tool for any `easyvision-*` role.
- Codex is not a Claude subagent: when `codex-companion.mjs setup --json` reports `ready=true`, manual labour (reading,
  tracing, simple or logic edits outside the Claude-only zone) still goes to Codex first, one Codex run at a time, with the
  usual brief, file allow-list and `git diff --stat` check from the Codex lane. If Codex is not ready, main does it.
- Writers: exactly one writer at any time (main or one Codex run), never both.
- Scope, authorization, build/test, safety, Git and commit rules are unchanged by this mode.
