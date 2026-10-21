---
name: emem
description: Check that the agentmemory server (WSL2) and its MCP tools are healthy and run memory_diagnose, then report a short verdict. Use when the user types /emem, or asks to check, diagnose memory / agentmemory, or after the server was restarted. Read-only; never heals, saves or starts the server.
---

# emem: agentmemory health check

Goal: one short verdict on whether memory is usable, with the exact next step for the user if it is not.
Rules for memory use live in `docs/architecture/agentmemory-rules.md`. This skill only checks; it never runs `memory_heal`,
`memory_save` or `forget`, and never starts the server (auto mode blocks it; the user starts it).

## 1. Server (3 calls, one shell command)
`curl -s -m 5 http://localhost:3111/agentmemory/livez; echo; curl -s -m 10 http://localhost:3111/agentmemory/health | head -c 1500`
- Empty or failed `livez` = DOWN. Skip step 2-3 and give the restart message (below).
- From `health` take only: `health.status`, `connectionState`, `circuitBreaker.state`, `alerts`, `workers[].function_count`,
  `kvConnectivity.status`, `memory.rss`. Do not paste the JSON.

## 2. MCP tools
Check whether `memory_diagnose` is callable in this session (ToolSearch `select:mcp__agentmemory__memory_diagnose` if it is
deferred). Missing while the server is UP = the MCP shim was not loaded: tell the user to check `/mcp` (agentmemory must be
Connected and approved) and to open a new session or `Developer: Reload Window`.

## 3. Diagnose
If the tool exists, call `memory_diagnose` once with no arguments. Summarize per category: counts of stuck actions, expired or
orphaned leases, stale sentinels/sessions, inconsistent memories. Do not paste the raw output. If issues exist, list them and
ask the user whether to run `memory_heal` (dry run first: `dryRun: true`); do not heal on your own.
No tool but server UP: fall back to the `health` fields only and say diagnose was not run.

## 4. Report (about 8 lines, user's language mode)
Verdict **OK / DEGRADED / DOWN**, then: server (livez, status, circuit breaker), MCP tools (present/missing), diagnose result
(clean or N issues by category), viewer `http://localhost:3113`, and the single next step.

## Restart message (use when DOWN)
"agentmemory server is down. Please start it: open a terminal, run `wsl`, then `npx -y @agentmemory/agentmemory@latest` and
keep that window open; tell me when it is up and I will run /emem again." The work continues on the file flow meanwhile.
