---
name: model-tiers-simple-to-haiku
description: "ev-route model policy - simple tasks go to Haiku for hands-on code, Sonnet 5.5 only plans, dispatches and reviews; complex/native/safety code stays on Sonnet"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 78436208-dd30-47b8-a368-e658f79f32dc
  modified: 2026-10-03T13:16:04.217Z
---

User decision 2026-10-03: do not spend Sonnet 5.5 on coding simple work. Sonnet should analyze, plan, hand out work, propose ideas, and approve or reject every issue (not read raw plan/contract/code itself). Simple tasks run on Haiku (the user also mentioned a "4.8" model, which the Agent tool cannot select; only haiku/sonnet/opus/fable overrides exist, haiku = 4.5).

**Why:** cost and token budget (5-hour limit); see [[token-budget-before-dispatch]].

**How to apply:** `.claude/skills/ev-route/references/routing-claude.md` section "Model tiers by task complexity" defines simple (at most 2 files, about 80 lines, no native/concurrency/persistence/public API/hardware/vision math). Simple -> haiku implementer/explorer/verifier with a precise brief, Sonnet reviews the real diff. Complex code stays on Sonnet implementer (my safety exception; relax only if the user says so for a named task). Pass `model` explicitly on every Agent call; escalate haiku to sonnet once on a capability problem.

**Decision gate (added 2026-10-03):** root Sonnet does not read plan/contract/code itself; Haiku readers and the reviewer agent return a compact issue digest, Sonnet decides every issue AFTER the subagent finishes (ledger tmp/<task>/issues.md, max 3 spot-check excerpts per round, mandatory for high risk/safety) and only then allows ACCEPT. See routing-claude.md "Post-subagent decision gate".

**Update 2026-10-03 (user, after seeing a Sonnet implementer edit code):** Sonnet must NOT code at all. Implementer = haiku always (simple or complex); complexity raises the review (sonnet/opus reader + spot-checks), not the writer. Sonnet may implement a slice only after Haiku really failed, with the failure evidence written to issues.md first. This supersedes the earlier "complex code stays on Sonnet" exception.

**FINAL 2026-10-03 (supersedes the previous update):** Sonnet may implement LOGIC/COMPLEX work; Sonnet is FORBIDDEN from coding SIMPLE (mechanical/cosmetic) work, which is haiku only. Root session never edits product code; only the implementer subagent does. State the task class before every Agent call.

**Root rule 2026-10-03:** the root session never writes product code if a subagent can; root edits code only when a subagent truly cannot (reason recorded in issues.md first). Skills/rules/docs/memory are not product code.
