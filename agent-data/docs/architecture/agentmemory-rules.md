# agentmemory usage rules (all agents)

Goal: stop re-reading history, contracts and finished work at every new session, resume, review, or very long context.
agentmemory is a **local index of decisions and pointers**. It never replaces the files below.

## Truth order
1. Files are authoritative: Task Contract `.claude/task-contract.<ID>.toml`, `tmp/<ID>/deferred.md`, `tmp/<ID>/issues.md`,
   `CODEX_HISTORY.md` tail, git. A memory that disagrees with a file loses; fix or `forget` the memory.
2. A memory is a hint. Before acting on one that names a file/line/flag, check it still exists (`grep -n`).
3. Server down (WSL window closed, `curl -s -m 5 http://localhost:3111/agentmemory/livez` fails) or `memory_*` tools
   missing/erroring: **tell the user once, at the start of the turn, then continue with the file-based flow** (`eresume`,
   `deferred.md`). Never block work on memory, and do not retry in a loop. Message (user's language mode):
   "agentmemory server is down. Please start it: open a terminal, run `wsl`, then `npx -y @agentmemory/agentmemory@latest`
   and keep that window open; tell me when it is up and I will re-check `livez` and the MCP connection (`/mcp`)."
   Do not start the server yourself (it is blocked by auto mode). If the server is up but tools are missing, tell the user to
   open a new session or reload the window. Ask again only if a later turn finds it still down after the user said it was up.

## Who can call it
- **Root/coordinator session only** (has the MCP tools `memory_*` / skills `recall`, `remember`, `lesson`, `handoff`, `recap`).
- **Subagents (`easyvision-*`) have only Read/Grep/Glob: they cannot call memory.** The coordinator recalls first and pastes a
  `Memory slice` (max 8 lines: decision, reason, `path:line`, date) into the brief next to the invariants digest.
  Subagents treat that slice as already-read context and must not re-open the files it marks done or re-review settled points.
- Subagents report new decisions/gotchas in their normal report; the coordinator decides what to `remember`.

## When the coordinator recalls (cheap, 1 query, `limit` <= 5)
- New session / `eresume`: `recall` the task ID or topic BEFORE reading history; then read only `deferred.md` and the
  contract by ID. Skip `tail CODEX_HISTORY.md` when recall + `deferred.md` already answer "what changed, what is open".
- Before dispatching explorer/reviewer/algorithm: recall the contract ID + area to build the `Memory slice`; this replaces
  re-reading prior review findings and rejected approaches.
- Context is long or about to be compacted: `remember` open decisions first, then continue from recall instead of rereading.

## When the coordinator saves (`remember`, one memory = one fact)
- A decision with its reason (approach chosen/rejected), a gotcha that cost time, a contract milestone
  (`<ID>: plan approved r2`, `<ID>: committed <hash>`, `<ID>: verification owed = <what>`), a user correction (use `lesson`).
- Always: `concepts` = contract ID + area (e.g. `O099`, `ds5-quickstop`), `files` = repo-relative paths.
- At `epause`: after `deferred.md` is READY, `remember` one memory `<ID> paused: <path to deferred.md>, next step: ...`.
  `deferred.md` stays the full handoff; the memory is only the pointer.
- Never store: secrets, logs, code blocks, diffs, long text (>3 lines), or anything already in git history.
- Do not duplicate `LESSONS.md` or the Claude auto-memory (`MEMORY.md`); use `lesson` only for rules that are NOT yet there.

## Hygiene
- Verified-wrong or superseded memory: `forget` it (skill shows it and asks for confirmation first).
- Hardware/safety facts (DS5, EtherCAT, Ladder output) are saved only with their evidence path; unverified claims are
  tagged `unverified` in `content`.
