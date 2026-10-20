---
name: emax
description: Set the dispatch concurrency mode to MAX - as many concurrent subagents as the task has independent scopes (hard ceiling 10). Use only when the user types /emax or explicitly asks to switch to max mode. Stays active until /enormal or /emedium is invoked.
---

# Dispatch mode: MAX (as many agents as the work can use)

Invoked: write the single word `max` to `.claude/dispatch-mode.txt` (overwrite the file), then confirm once in one line
("Dispatch mode: MAX - parallel agents up to the number of independent scopes, ceiling 10."). The mode persists across
sessions until another mode skill overwrites the file. The full policy lives in
`.claude/skills/eroute/references/routing-claude.md`, section "Dispatch modes".

## Rules in this mode
- Concurrency limit: one agent per independent scope the task really has, never more than 10 at once, counting Claude
  subagents and Codex runs together. Do not spawn agents just to fill slots; split only along real boundaries
  (separate files, separate questions, separate log sets).
- The same multi-writer conditions as MEDIUM apply without exception: per-writer file allow-list written in the brief,
  no shared files between writers, no writer touches shared tracking files (main writes `issues.md`, the contract,
  history, session and map files), and `git diff --stat` against each allow-list after every writer finishes.
- Single-writer zone (never more than one writer, in any mode): PDM, executor, solver and release code.
- Existing Claude-only zone (native, EtherCAT/DS5, Ladder output, `Ds5Api.h`, persistence, public API, licensing) keeps
  Claude sonnet as the only writer.
- Hand work goes to Codex first; the leader (main) decides the mix of Codex runs and Claude subagents inside the limit. Judgment stays with main: plan, decision
  gate, ACCEPT.
- Scope, authorization, build/test, safety, Git and commit rules are unchanged by this mode.
