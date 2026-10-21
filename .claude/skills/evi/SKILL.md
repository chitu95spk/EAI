---
name: evi
description: Switch this session to Vietnamese reporting mode. Accept commands in English or Vietnamese, do all internal work and stored artifacts in English, and write every reply and summary to the user in Vietnamese. Use only when the user types /evi or explicitly asks to switch to Vietnamese reporting. Stays active until /een is invoked.
---

# Language mode: VI (Vietnamese reporting, English working)

Invoked: this mode is now ACTIVE for the rest of the session, until `/een` is invoked. Confirm once with a single
Vietnamese line ("Đã chuyển sang chế độ /evi: làm việc và lưu trữ bằng tiếng Anh, báo cáo bằng tiếng Việt."), then
continue. A later `/evi` just re-confirms.

## Input
Accept English or Vietnamese commands, mixed or with or without diacritics. Interpret the intent; do not ask the user to
rephrase. Quote the user's original wording only when preserving the request verbatim in a Task Contract
(`normalized_request` stays in English, the original prompt is kept beside it as written).

## Work and storage: English
Everything that is stored or consumed by tools or agents is written in English:
- Subagent briefs and the reports you ask them to return (tell every agent: reply in English).
- Task contracts (`.claude/task-contract.*.toml`), plans, `deferred.md`, `.codex-session.md`, `CODEX_HISTORY.md`,
  `*-map.md`, docs under `docs/`, memory files, scratch notes in `tmp/` and evidence in `artifacts/`.
- Code, comments, identifiers, log messages, commit messages, branch names, test names.
This keeps stored text uniform for searching, grep-based tools (`einfo`, `esum`, `eresume`) and future sessions.

## Report to the user: Vietnamese
- Every message to the user is Vietnamese with full diacritics: progress notes, questions, errors, final summaries,
  and the output of `esum`, `einfo`, `elog`, `eui`, `eresume`. Translate relayed agent reports; do not paste
  them in English.
- Keep untouched: code, file paths, identifiers, command lines, log excerpts, error codes, task IDs, model names, and
  product terms such as Contract, Route, deep/standard/light, FIX_REQUIRED, ACCEPT. Add a short Vietnamese gloss on
  first use when the meaning is not obvious.
- Counts, statuses and evidence (`path:line`, build log paths) must stay exactly as measured; translation must not
  soften a failure or turn `unverified` into a pass.

## Not affected
- Product UI text and localization catalogs are changed only when the task is about them, and then follow
  `UiLocalizer` and the localization checks, not this mode.
- Safety, authorization, build/test, scope and commit rules are unchanged.
