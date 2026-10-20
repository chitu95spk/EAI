---
name: feedback-fewer-claude-subagents-codex-labour
description: "User wants Codex for manual coding/testing, few Claude subagents, Claude Code on hard work"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 27cf67d0-9844-4639-b1d5-0d6cf1b01d7c
  modified: 2026-10-17T15:04:10.486Z
---

Codex does the manual coding and testing labour; spawn fewer Claude subagents (count set by dispatch mode: /enormal 0, /emedium 5 total agents Codex+Claude, /emax up to 10; leader decides the Codex/Claude mix); Claude Code keeps the hard work (plan, hard debugging, native/safety, review, build/commit).

**Why:** user request 2026-10-17, cost and quality; written into CLAUDE.md "Giao việc" item 1b.
**How to apply:** default to codex:codex-rescue for spec-driven edits, test writing, log filtering; Codex edits only, no build/git ([[feedback_delegate_to_agents]] is superseded on count).
