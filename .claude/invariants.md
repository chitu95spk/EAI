# EasyVision invariants digest (paste into every agent brief)

Source of truth stays `CLAUDE.md` and `AGENTS.md`. Agents read this digest, not those files whole;
grep them only for one named clause. Keep this file under 1.5 KB.

- Workspace: `E:\Code\EasyVision_Unisen - NoService` only. The sibling `E:\Code\EasyVision_Unisen` is read-only.
- Stay inside the contract's `in_scope`. The worktree is dirty; never format or touch other files.
- C# you write must compile as C# 7.3 (.NET Framework 4.8). Agent-facing docs in English.
- Tools: `Common.TryGetTool/TryGetToolList/EnsureToolList/SetToolList`, never `Common.PropetyTools[...]`.
  Events: `X.Event -= H; X.Event += H;`. Keep public and serialized (`ClassProject.json`) names.
- No build, test, benchmark, hardware, commit, push or deploy unless the contract flag is true and the
  user authorized it for this task. Unrun checks are `unverified`, never PASS.
- Verification builds use `/p:EasyVisionIsolatedVerification=true`. Never rebuild `BeeLicense.dll` into Release.
- Motion/safety (DS5, EtherCAT, Ladder output): a build alone is not production-ready; state hardware evidence owed.
- Large files (`CODEX_HISTORY.md`, `.codex-session.md`, maps, logs): `grep -n` then read a range. Build output
  goes to a log file, then grep. Never read them whole.
- Temp/test data only inside the project: `tmp/<scope>/`, evidence in `artifacts/<scope>/<run>/`. Never `%TEMP%`.
- Report: at most about 300 words, evidence as `path:line`, no pasted code blocks or logs. Longer detail goes to a
  project file and you return its path.
- Memory: you have no memory tool. A `Memory slice` in your brief is already-read context; do not re-open files it marks done.
- Budget: stop at the limit stated in your brief and report what you have; do not run on to finish.
