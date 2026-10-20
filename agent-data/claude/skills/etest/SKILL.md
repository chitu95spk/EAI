---
name: etest
description: Build AND test the front-end (UI) and back-end (logic, native) projects that THIS session changed, then give a verdict for the task (PASS / FAIL / INCONCLUSIVE / unverified). Only when code was changed. Maps the session's edited files to projects, builds back then front in isolation, runs the matching test harnesses with a time limit, and concludes. Use when the user types /etest or asks to build and test what this session changed. Current session only; no hardware, deploy or live-folder build (use ebuild for that).
---

# EasyVision session build, test and verdict

Scope: files edited in THIS session. Docs, skills, rules, logs and memory never trigger a build. Invoking `/etest`
authorizes ONE isolated build of the affected projects AND one run of the applicable test harnesses listed by the plan.
It does not authorize benchmarks, hardware, commit, push, or a build into the live `bin\Release` (use `ebuild`).

## 1. Plan (always first, builds nothing)
`powershell -NoProfile -File .claude/skills/etest/scripts/plan-session-test.ps1 -SessionId <guid> [-AlsoFile a.cs,b.cpp] [-TestScope V161]`
- `<guid>` = last folder of the scratchpad path in the system prompt (same as `esum`).
- Reads the session's Edit/Write calls, keeps product/test code, maps each file to its owning project and classifies front
  (BeeInterface, BeeInterfaceControls, BeeUi, BeeMain, EasyLabel...) versus back (everything else, native C++ included).
- Edits made by Bash/Python scripts or subagents are not in the transcript: pass them with `-AlsoFile` (comma list,
  repo-relative). Check `git status` or `ecommit`'s "NOT attributed" list before concluding "nothing to build".
- Lists the harnesses it would run: `tests/` and `tools/` projects that reference an affected project, at most
  `-MaxHarness` (default 4). Excluded on purpose: hardware (Plc, Ecat, Ds5, Camera, Pylon), licensing/activation tools,
  and the Persistence harness (it has its own scoped runner, `-TestScope`).
- No code change: it prints `verdict: NOT APPLICABLE` and stops. Never build "just in case".

## 2. Execute: build, test, verdict
Same command with `-Execute` (add `-BuildOnly` to skip tests). Foreground only; never `run_in_background` (writes to `%TEMP%`).
1. Build each affected project isolated (`/p:EasyVisionIsolatedVerification=true`, mirror/deploy/version-bump disabled,
   `/m`, silent console). `bin\Release` and a running app are untouched. A build failure ends with `verdict: FAIL`.
2. For each harness: build it isolated, run its exe from the isolated output folder with `-HarnessTimeoutSec` (default 120),
   save stdout/stderr, and classify:
   - PASS = exit 0, at least one `PASS` line, no `FAIL`/`FAILED`/unhandled exception;
   - FAIL = non-zero exit or a failure line; INCONCLUSIVE = timeout (that harness only is killed);
   - UNVERIFIED = clean exit but no `PASS` line, or no exe: never counted as a pass.
   Before running, DLLs missing from the harness's isolated output (native `BeeCpp.dll`, OpenVINO, ...) are hard-linked from
   the live folders (`bin\x64\Release`, `bin\Release`, `lib\native\x64\Release`), read-only and never replacing isolated
   managed DLLs; the run prints how many were linked and from where (they may be older than this session's build).
   A harness that could not start (`Could not load file or assembly`, `BadImageFormat`, `DllNotFound`) with no PASS line is
   reported UNVERIFIED with an `ENVIRONMENT:` note, never FAIL, because it says nothing about the code under test.
   On a real FAIL the script prints the harness message and a ready `diag-harness-exception.ps1` command that runs the
   harness in-process and prints the stack of the failing exception (harnesses only print `ex.Message`). Use it to say which
   test method failed and whether it relates to this session's files before concluding.
3. Optional `-TestScope <U562|V147|V148|V150|V161|R286>` (only when the contract names it) builds and runs the Persistence
   harness through `invoke-check.ps1`; skipped while EasyVision.exe runs (it hijacks `bin\Release`; see lessons).
4. Verdict line: `FAIL` if anything failed; `INCONCLUSIVE` on timeouts; `PASS` only with builds OK and at least one passing
   harness; otherwise `BUILD OK, NO AUTOMATED TEST APPLICABLE` (runtime unverified). Logs and `summary.txt` are under
   `artifacts/verification/etest/<run>/`.

## 3. Conclude for the task (this is the point of the skill)
After the run, state the conclusion for the contract in the user's language mode:
- verdict per project and harness with counts and log paths, and what the verdict does NOT cover: live UI, DPI,
  language, hardware (for motion/safety code say "hardware evidence owed");
- map it to the contract's `acceptance`: items proven by this run, items still owed;
- next action: on FAIL fix with the single implementer (`eui` for front gaps) and rerun, at most two cycles, then ask for
  a new plan revision; on PASS offer `ecommit` (the verdict is its evidence for contract groups).
Never report PASS from a build alone, and never turn UNVERIFIED or INCONCLUSIVE into PASS.
