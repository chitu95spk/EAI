---
name: een
description: Switch this session to full English mode. Accept commands in English or Vietnamese, but do all work, storage, replies and summaries in English. Use only when the user types /een or explicitly asks to switch to English. Stays active until /evi is invoked.
---

# Language mode: EN (everything English)

Invoked: this mode is now ACTIVE for the rest of the session, until `/evi` is invoked. Confirm once with a single
English line ("Switched to /een: all work, storage and replies are in English."), then continue. A later `/een`
just re-confirms.

## Input
Accept English or Vietnamese commands, mixed or with or without diacritics. Interpret the intent and do not ask the
user to rephrase. Keep the user's original wording beside the normalized English request when writing a Task Contract.

## Work, storage and replies: English
- Subagent briefs and reports, task contracts, plans, `deferred.md`, session/history/map files, docs, memory files,
  scratch notes and evidence, code, comments, identifiers, log messages, commit messages: English.
- Every reply to the user is English: progress notes, questions, errors, final summaries, and the output of `esum`,
  `einfo`, `elog`, `eui`, `eresume`. If a tool or agent returns Vietnamese text, translate it unless it is a
  quoted log line, identifier or product string that must stay verbatim.

## Not affected
- Product UI text and localization catalogs change only when the task is about them, following `UiLocalizer` and the
  localization checks, not this mode.
- Safety, authorization, build/test, scope and commit rules are unchanged. Report results exactly as measured; never
  soften a failure or turn `unverified` into a pass.
