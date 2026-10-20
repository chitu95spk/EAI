---
name: elog
description: Summarize a large EasyVision log (build.log, ECAT/PLC/camera logs, logs.json, binlog text) into counts, grouped error patterns and first/last lines without reading it whole. Use when the user points at a log file or asks to analyze errors/dropouts in a big log. Read-only; does not build or run hardware.
---

# EasyVision log triage

Never read a large log whole. Run the script, read the summary, then open only the cited line ranges.

1. `powershell -NoProfile -File .claude/skills/elog/scripts/summarize-log.ps1 -Path <log>`
   (optional `-ErrorPattern <regex>`, `-Top N`). The summary is also saved to
   `tmp/log-summaries/<name>.summary.txt` so a later session can reuse it instead of re-reading.
2. Take the first error line number and the top grouped patterns. Read context with Read
   offset/limit (about 40 lines) or `sed -n 'A,Bp'`, not the whole file.
3. Report: conclusion, evidence (line numbers, counts), and what is unverified. Do not paste log blocks.
4. For JSON logs with a known schema, write a small filter script under `tmp/<scope>/` instead.
5. MSBuild output: use `ebuild` (logs are already compact). For a binlog use the
   `binlog-failure-analysis` guidance in `.agents/skills/`.

Rules: temp output stays in the project (`tmp/`), never `%TEMP%`. Do not edit the log.
