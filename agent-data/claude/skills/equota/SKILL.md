---
name: equota
description: Record the token cost of the current session in a persistent project ledger after each piece of work, and report detailed cost statistics over time (totals, tokens, model, agent role, day, top sessions/contracts, trend). Prices are refreshed automatically from the live pricing page on every call. Use when the user types /equota or asks to log, summarize or compare cost. Every check is logged (session ledger + per-agent log) and `--audit` judges agent cost, input vs output, overruns and over/under-delegation. Writes only docs/agent-lessons/cost-ledger.csv, agent-ledger.csv and prices.json.
---

# EasyVision cost ledger

Two measures are recorded: RELATIVE units (input 1, cache-write 1.25, cache-read 0.1, output 5, times a model ratio haiku 0.33,
sonnet 1, opus 1.67, fable 5; assumed weights) and an estimated USD cost at API list prices. Subagent cost is included from the
session's `subagents/` transcripts. Thinking tokens are not visible and are not counted.

## Prices refresh themselves (every call)
`cost-ledger.py` fetches `https://platform.claude.com/docs/en/about-claude/pricing.md` at the start of EVERY run (any mode),
parses the model table (input, 5m write, 1h write, cache read, output per MTok), rewrites `prices.json` (`as_of` = today) and
prints one line: `prices: REFRESHED ... (changed: ...)` or `no price changes`. You do not refresh by hand.
- Network or layout failure: the line says `NOT refreshed (reason)` and the old prices are used; then say plainly that the
  dollars use prices from the `as_of` date shown. Only in that case may you WebFetch the pricing page and edit `prices.json`.
- A sanity check (at least 5 rows, opus + sonnet + haiku present) prevents a broken page from overwriting good prices.
- Flags: `--no-refresh` (offline), `--refresh-only` (just refresh and print the changes).
- Ledger rows keep the dollars computed with the prices valid when recorded (`price_as_of`); old rows are never repriced.
- Tell the user in one line what the prices line said (refreshed + what changed, or not refreshed + why).

## Invocation by argument
- `/equota` (no number): record this session and compare it with the most recent earlier recorded session:
  `python .claude/skills/equota/scripts/cost-ledger.py --session <guid> --compare [--note "..."]`
  (`<guid>` = last folder of the scratchpad path in the system prompt). Answer with USD, units, requests, agents, errors and the
  delta/percent against the previous session; with no earlier session say so.
- `/equota N` (1, 7, 30 or any number of days): detailed statistics, records nothing:
  `python .claude/skills/equota/scripts/cost-ledger.py --detail --days N` (`--days 0` = all time).
- `/equota all`: `--detail --days 0`.
- `/equota audit [N]`: `--audit --days N` (default 0 = all time). Add `--session <guid>` to every form so the check is logged.

## What `--detail` prints
TOTALS (sessions, USD, units, requests, subagents, errors, wall hours, per day / session / request / hour, error rate), TOKENS
(cache read/write, output, fresh input, cache reuse %), BY MODEL (USD, share, units, requests, $/request, bar), MAIN vs
SUBAGENTS (and USD per agent role), BY DAY (up to 14, bars, top day), TOP 5 SESSIONS, TOP 5 CONTRACTS, TREND against the
previous equal window. Older rows lack `input_m` and the per-role USD columns (they count as 0 there); the header is upgraded
automatically on the next record and no row is lost.

## Every check is logged (history for cost and agent evaluation)
Pass `--session <guid>` on EVERY invocation, including `--detail`, `--audit` and `--report`: the session is recorded first
(note `check: <mode>`), then the view prints. So each check leaves a ledger row and the history needed for trends.
Two append/upsert logs:
- `docs/agent-lessons/cost-ledger.csv`: one row per recording of a session (totals, model, role, USD).
- `docs/agent-lessons/agent-ledger.csv`: one row per agent run (`role=main` is the root session), upserted per session so
  re-recording never duplicates. Columns: role, model, description, minutes, requests, tool calls/errors, edits,
  product-code edits, reads and repeat reads, tool-result volume (k chars) and the largest one, first-request context (k tokens),
  tokens, input-side USD vs output-side USD, cache reuse %, hand-back length (chars).
History: `python .claude/skills/equota/scripts/cost-ledger.py --backfill [--days N]` adds every past transcript not yet
logged (`--rebuild` re-scans all after a metric change). Backfill rows carry note `backfill`.

## `--audit` (agent evaluation, `/equota audit [N]`)
`python .claude/skills/equota/scripts/cost-ledger.py --session <guid> --audit [--days N]` prints: per-role cost with the
input-side vs output-side split and medians, the ROOT SESSION share, what the spend bought (USD per Edit, USD per 1k chars
of hand-back), session shape (OVER-/UNDER-DELEGATED), flag counts with a rule hint, and the 10 costliest flagged runs.
Flags (heuristics, not verdicts): OVERRUN (>1.5x the role's call budget), COLD-START (>30k ctx on request 1), REREAD (>=8 repeat
reads), BIG-RESULT (>=40k chars in one result), LOW-CACHE (<85% reuse), ERRORS (>20% of calls), NO-REPORT (<60 chars handed back;
sample a transcript before acting, the last text message may not be the report), IMPLEMENTER-NO-EDIT, TIER (an
explorer/profiler on sonnet/opus, or a generic agent on opus). Reading it: agents are input-side by nature (they re-read context
every request), so judge input by cold start, re-reads and result size, and output by what was written or handed back.
Feed the result to `eskill` step 1c to turn findings into route/agent rule changes.

## Other reports
`python .claude/skills/equota/scripts/cost-ledger.py --report [--days N] [--by day|week|month|contract|model|session]`
gives one compact table per grouping. Use `--detail` for the full picture and `--report --by session` to find expensive sessions.

## Record
Append-only: each call with `--session` adds one row to `docs/agent-lessons/cost-ledger.csv` (time, session, start/end, minutes,
contracts edited, requests, agents, errors, USD main/agent/per model/per agent role, units, tokens, requests per model).
Recording the same session again adds a newer row; reports use the latest row per session, so record at the end of a session or
at each pause without double counting. Used also by `/epause` and `/eskill`.
Contracts come from this session's Edit/Write calls on `task-contract.<id>.toml`; a session with none shows `(no contract)`;
a multi-contract session is counted in each contract it edited.

## USD column
`cost_usd` is an ESTIMATE at first-party API list prices, not what a subscription (for example Claude Code on a plan) is billed;
treat it as the API-equivalent value of the usage. Requests whose model id has no price entry count in `unpriced_req` and are left
out of the dollars (the most specific key wins, e.g. `opus-4-1` before `opus-4`).

## How to answer
Short, in the session's language mode: period, total USD and units, the top cost driver (model share, main vs agents, long
sessions, cache reuse), the trend versus the previous period when it exists, the prices line, and what is not measured. Ledger
numbers start from the first recorded row; earlier sessions are missing unless `budget-review.py` is used to back-fill.
Writes only the two ledgers and `prices.json`; it does not edit contracts (use `eskill` to copy actuals into a contract `[budget]`).
