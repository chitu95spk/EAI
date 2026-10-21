---
name: eskill
description: End-of-session retrospective for the CURRENT session, built on esum. Review what happened, record mistakes, issues, cost drivers and wins as lessons in docs/agent-lessons/LESSONS.md, and propose rule or skill changes so later sessions are faster and use fewer tokens. Use when the user types /eskill, or asks to review the session and record lessons. Writes only the lessons file (and approved rule changes).
---

# EasyVision session retrospective

Scope: this session only. Output stored in English (see `evi`/`een`); the summary to the user follows the
active language mode (Vietnamese in `evi`).

## 1. Collect facts (cheap, scripted)
1. `esum` first: `powershell -NoProfile -File .claude/skills/esum/scripts/summarize-remaining.ps1 -SessionId <guid>`
   (guid = scratchpad folder name). It lists what this session edited, commits, and open contract items.
2. Then the cost signals:
   `powershell -NoProfile -File .claude/skills/eskill/scripts/session-stats.ps1 -SessionId <guid>`
   It reports duration, token sums (output, cache-write, cache-read), tool-call counts, the biggest tool results,
   tool errors and files read repeatedly; also saved to `tmp/eskill/<guid>.stats.txt`.
   Limits: it covers the main transcript only (subagent cost is in separate transcripts) and cannot see thinking
   tokens. Say "unmeasured" when a cost is not in the stats.
3. Read the top of `docs/agent-lessons/LESSONS.md` ("Top rules") and `grep -n` it for keywords of any candidate
   lesson to avoid duplicates. Never read the whole file.

### 1b. Budget actuals and the weekly review
- Per session: `python .claude/skills/eskill/scripts/budget-review.py --session <guid>` prints one line (main units,
  agents, requests, errors, minutes, requests per model). Units are RELATIVE (input 1, cache-write 1.25, cache-read 0.1,
  output 5, times a model price ratio), not a bill. When the session edited exactly one contract and its
  `authorization.update_tracking=true`, write the numbers into that contract's `[budget]` block
  (`actual_units_millions`, `actual_requests`, `actual_agents`, `actual_session`) so estimated and actual can be compared.
  If the session edited several contracts, do not split the cost; say so.
- Weekly or on request: `python .claude/skills/eskill/scripts/budget-review.py --days 7` writes
  `artifacts/budget-review/<run>/report.md` (per-session and per-agent cost, cost composition, long sessions, large
  results, repeated reads, verifier errors, per-contract cost). Read only its summary lines, then record the top
  findings as lessons. Limits: wall-clock minutes include idle time; the image-read sizes are characters, not tokens.

### 1c. Agent audit -> route rule tuning (data comes from equota)
1. Log and audit in one call: `python .claude/skills/equota/scripts/cost-ledger.py --session <guid> --audit --days 14 > tmp/eskill/audit.txt`
   (first run on a new machine: `--backfill`). Read only the printed sections, not the CSVs.
2. Map each finding to a rule; propose only changes with evidence (counts, $ and 2-3 run ids), and check the two targets
   that Claude actually loads (`.claude/skills/eroute/references/routing-claude.md`, `.claude/agents/*.md`):
   | Finding | Rule change to propose |
   | --- | --- |
   | OVERRUN on a role in >25% of runs | lower the call budget in the brief template or split that role's task; if the brief already caps it, add "stop and report at the limit" to the agent file |
   | REREAD / BIG-RESULT high | brief template must give path:line ranges and `head`/limit caps; forbid whole-file reads |
   | TIER (explorer/profiler on sonnet/opus, Explore on opus) | enforce the "model explicit per Agent call" rule; add the generic roster rows (Explore/Plan) to the model table |
   | root = most of the USD, many sessions >150 requests | split long sessions at a phase boundary (`epause`), keep root reads under the read budget, move code reading to haiku readers |
   | UNDER-DELEGATED (root made product-code edits, no agents) | tighten the "root never writes product code" rule or record the allowed exception in `issues.md` |
   | OVER-DELEGATED (agents on a tiny session) | add a /fix-mode threshold: no agents under 2 files / 80 lines |
   | Implementer $ per edit high or many runs with 0 edits | require an explorer or exact anchors before dispatching the implementer |
   | NO-REPORT | sample the transcript first; if real, enforce the 300-word hand-back format in the agent file |
   | Reviewer $ per 1k chars far above explorer | cap reviewer reads to the diff plus listed files; haiku reviewer only for mechanical diffs |
3. Compare with the previous audit in LESSONS (grep `audit`): did the last rule change reduce its flag count or median $? Record
   the before/after numbers so a rule that did not help is reverted.
4. The audit is a heuristic: thresholds live in `cost-ledger.py` (`CALL_BUDGET`, `agent_flags`). Change a threshold only with a
   stated reason, and never treat a flag as proof of waste (a complex implementer may legitimately exceed the call budget).

## 2. Review (from this session's own context plus the facts)
Classify each finding as `mistake` (wrong target, wrong assumption, rework, unsafe or out-of-scope action),
`issue` (blocker, owed evidence, unverified result, tool or environment problem), `cost` (large outputs, repeated
reads, long context, agents that overran, avoidable retries) or `win` (a practice worth keeping). For each,
state evidence (`path:line`, command, stat), cost, root cause and a concrete prevention rule. Do not blame
without evidence and do not invent costs; mark "unmeasured". Include user corrections from this session; they are
the highest-value lessons.

## 3. Store (append-only)
- Append entries to `docs/agent-lessons/LESSONS.md` using its entry format and the next free `L<nnn>`
  (`grep -o '^### L[0-9]*' ... | tail -1`). Keep each entry under about 6 lines. English only.
- Update "Top rules" only for lessons that are high-impact or recurring; keep at most 15 lines, merge near
  duplicates, drop stale ones, and cite the entry IDs.
- Durable user preferences also go to the auto-memory directory per the memory rules (one fact per file).
- Create no other files. Do not edit `CODEX_HISTORY.md`, maps or contracts here (that is `/save`).

## 4. Propose, then apply only what is approved
List proposed changes (route rules from step 1c first) to skills, agent files, `invariants.md` or `CLAUDE.md` with expected saving (tokens, calls,
minutes) and risk. Ask the user which to apply; apply only those, within scope, and record the result in the
entry (`Status: applied|proposed|rejected`). A proposal never edits a rule by itself.

## 5. Report (user's language mode)
Short: facts (duration, tokens, calls, errors), top 3 lessons, what was stored (entry IDs), proposals awaiting
approval, and what remains unmeasured. State plainly what is estimated versus measured.
