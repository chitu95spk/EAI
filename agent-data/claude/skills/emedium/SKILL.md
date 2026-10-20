---
name: emedium
description: Set the dispatch concurrency mode to MEDIUM - up to 5 subagents (Claude subagents plus Codex runs) may run at the same time. Use only when the user types /emedium or explicitly asks to switch to medium mode. Stays active until /enormal or /emax is invoked.
---

# Dispatch mode: MEDIUM (up to 5 concurrent agents)

Invoked: write the single word `medium` to `.claude/dispatch-mode.txt` (overwrite the file), then confirm once in one
line ("Dispatch mode: MEDIUM - up to 5 agents at once."). The mode persists across sessions until another mode skill
overwrites the file. The full policy lives in `.claude/skills/eroute/references/routing-claude.md`, section "Dispatch modes".

## Rules in this mode
- Concurrency limit: 5 agents running at the same time, counting Claude subagents and Codex runs together. Read-only
  roles (explorer, profiler, algorithm, reviewer, Codex read-only) may fill any slot.
- Multiple writers are allowed only when ALL of these hold:
  1. Every writer has its own file scope, listed as an allow-list in its brief.
  2. No two writers share a file, and no file is in two allow-lists.
  3. No writer edits a shared tracking file (`tmp/<task>/issues.md`, the task contract, `CODEX_HISTORY.md`,
     `.codex-session.md`, map files, `docs/architecture/**` plans). Main alone writes those.
  4. After each writer finishes, main runs `git diff --stat` against that writer's allow-list (watchdog 7a).
- Single-writer zone (never more than one writer, in any mode): PDM, executor, solver and release code. Wrong edits there
  cost far more than elsewhere, so they are serialized.
- Existing Claude-only zone (native, EtherCAT/DS5, Ladder output, `Ds5Api.h`, persistence, public API, licensing) keeps
  Claude sonnet as the only writer.
- Hand work (reading, tracing, simple and logic edits) goes to Codex first; the leader (main) decides the mix of Codex runs and Claude subagents inside the 5 slots; Claude subagents are not required.
- Scope, authorization, build/test, safety, Git and commit rules are unchanged by this mode.
