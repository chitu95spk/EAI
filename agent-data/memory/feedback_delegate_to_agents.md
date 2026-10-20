---
name: delegate-to-agents-not-root
description: "For EasyVision plan/perf/algorithm tasks the user wants the root session to dispatch easyvision-* subagents, not do the investigation or edits itself"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 192d4d04-f185-432e-b6e1-9c8bb5ede77f
  modified: 2026-09-24T15:11:37.318Z
---

For EasyVision work, dispatch the easyvision-explorer/profiler/algorithm/architect/implementer/verifier/reviewer agents instead of investigating or editing directly in the root session.

**Why:** the user interrupted a plan request mid-investigation with "phải phân việc ra cho các agents, bạn không làm" (assign the work to agents, you don't do it). It matches the repo's easyvision-dispatcher skill (deep route: discovery agents -> architect -> single implementer -> verifier -> reviewer).

**How to apply:** root session writes/updates `.codex/task-contract.<ID>.toml`, briefs agents with self-contained prompts, and relays results. Verifier has Bash but no Edit/Write; implementer has Edit/Write but no Bash, so harness code is written by one and built/run by the other. Verifiers must use `/p:EasyVisionIsolatedVerification=true` (a non-isolated native build overwrote the live bin\Release DLLs once) and legacy csc.exe is C# 5 only.
