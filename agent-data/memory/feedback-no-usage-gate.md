---
name: feedback-no-usage-gate
description: User does not want token/usage budget checks, /usage requests, or 5-hour-window slicing in agent rules
metadata:
  type: feedback
---

Do not gate dispatch on the 5-hour usage window, never ask the user for `/usage`, never split work to fit a limit.

**Why:** user said (2026-10-10) usage is not a concern and asked to remove the rule from agents; replaces the old token-budget-before-dispatch rule.

**How to apply:** removed from CLAUDE.md, eroute 4a, eresume step 4. Write deferred.md only on explicit /epause. Temp files still go inside the project. See [[feedback_delegate_to_agents]].
