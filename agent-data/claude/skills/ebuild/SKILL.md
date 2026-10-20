---
name: ebuild
description: Build BeeMain as Release|Any CPU with the solution's existing x64 dependency mappings (incremental parallel target build, silent console, log files, BeeLicense.dll guard), or run a focused isolated check. Use when the user types /ebuild or asks to build, rebuild, compile or verify compilation. Never deploys, benchmarks or touches hardware.
---

# EasyVision build (Claude)

Invoking `/ebuild` or asking to build/rebuild is the user's authorization for ONE build of the current task. It does
not authorize tests, benchmarks, hardware, commit or push, and it is not carried over to later tasks.

## Default: BeeMain Release|Any CPU into bin\Release
`powershell -NoProfile -File .claude/skills/ebuild/scripts/release-build.ps1 -Execute`
Without `-Execute` it prints a plan and builds nothing. Run it once with `-Execute` when the user invoked the skill.
Run it in the FOREGROUND (Bash/PowerShell `timeout: 600000`); the incremental build takes about a minute and prints
about 10 lines. Do NOT use `run_in_background`: the harness stores that output under `%TEMP%`, which breaks the
project-local temp rule (CLAUDE.md 0.2b). If a background run is ever unavoidable, redirect it into the project
(`> tmp/ebuild/<run>.out 2>&1`) and read that file. The script also saves its summary to
`artifacts/verification/release-build/<run>/summary.txt`.

What it does and why it is the fast, cheap way:
- Builds the `BeeMain` solution target in `Release|Any CPU` with `/m` (all cores); the solution keeps
  native dependencies on their existing x64 mappings. Unchanged projects are skipped. Add `-Rebuild`
  only when the user asks for a clean rebuild or a stale-output problem is proven; it is much slower.
- This targets `BeeMain` only, not the whole solution. The unrelated missing
  `Update\TestCPlus.csproj` prevents a full solution build. `BeeMain` writes to `bin\Release`;
  x64 dependencies may write to `bin\x64\Release`.
- Console output is switched off (`/noconlog`); only errors and warnings are written to
  `artifacts/verification/release-build/<run>/errors.log|warnings.log`. The script prints a ~10 line summary: exit
  code, elapsed seconds, unique error/warning counts, the first 10 errors, license-guard result, and the freshness
  of key DLLs in both folders. Read nothing else; open a log only if errors exist, with `head`.
- Refuses to run while `EasyVision.exe` is running (locked DLLs); it never kills the process. Ask the user to close it.
- BeeLicense guard: before building it backs up `BeeLicense.dll` from both folders and compares SHA256 afterwards; if
  the build changed it, the original is restored automatically and reported. The activation DLL is never replaced.
- Add `-Binlog` only when diagnosing a failure (slower, large).

After the run, report: command, exit code, error/warning counts versus `docs/architecture/baseline_build.md`,
BeeMain freshness in `bin\Release`, relevant dependency freshness, and the log path. A successful build proves compilation
only; it is not a runtime, UI or hardware verification. State that.

If it fails: take the first unique error from `errors.log`, fix, and rerun the same command (incremental, so the
retry is cheap). Use `elog` for any other large log.

## Focused isolated check (does not touch live folders)
For a single project, or the Persistence test harness, use `.agents/skills/ebuild/scripts/invoke-check.ps1`
(`-Action Build -Target Core|Interface|Persistence|BeeMain`, `-Execute` to run; details in that skill's SKILL.md). It writes
to `artifacts/verification/` using `/p:EasyVisionIsolatedVerification=true`, so it cannot overwrite the app. Never run
the Persistence tests while checking the app folders; they hijack `bin\Release` (see lessons/memory).

## EasyLabel / EasyAI.Wpf (net9, WPF): Any CPU only, never x64
Build only the Any CPU configuration; do not pass `-p:Platform=x64`, `-a x64` or `-r win-x64`:
`dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release -v:minimal > <log> 2>&1`
(the `.sln` does not build it, build the csproj directly). The one and only output is
`EasyLabel\src\EasyAI.Wpf\bin\Release\net9.0-windows\EasyAI.exe`; tell the user to run exactly that path.
`EasyLabel\src\EasyAI.Wpf\bin\x64\...` is a stale folder from older x64 builds: never build into it, never point the user
or a smoke script at it, and do not delete it unless the user asks. Close `EasyAI.exe` before building into `bin\Release`
(locked files); to build while the app or a harness must keep running use `-p:OutDir=<project-local folder>/`.
Reason: two output folders (`bin\Release` and `bin\x64`) made the user launch an old build (2026-10-15, EAI-LOGO).

## Not covered
Projects in the solution that `BeeMain.csproj` does not reference (for example EasyUpdate, BeeUpdate, tools, tests) are
not built by this path; build them with their own project file only when the task needs them. Native
`EtherCatCommunication` ABI rule: do not change `Ds5Api.h` structs without bumping `Ds5NativeConstants.EtherCatApiVersion`;
the script reports whether `bin` and `lib/native` copies match.
