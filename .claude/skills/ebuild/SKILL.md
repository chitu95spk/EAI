---
name: ebuild
description: Build EasyAI.Wpf (net9, Release, Any CPU only) or the BeeNativeSegAI native DLL for the EAI repo, logging to files and reporting a short summary. Use when the user types /ebuild or asks to build, rebuild, compile or verify compilation. Never deploys, benchmarks or touches hardware.
---

# EAI build (Claude)

Invoking `/ebuild` or asking to build/rebuild is the user's authorization for ONE build of the current task. It does
not authorize tests, benchmarks, hardware, commit or push, and it is not carried over to later tasks.

## Default: EasyAI.Wpf Release | Any CPU
Run in the foreground (`timeout: 600000`), from the repo root:
```
dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release -v:minimal > tmp/ebuild/build.log 2>&1
grep -cE ": error " tmp/ebuild/build.log; grep -E ": error " tmp/ebuild/build.log | sort -u | head -10
grep -E "Warning\(s\)|Error\(s\)" tmp/ebuild/build.log
```
- Never pass `-p:Platform=x64`, `-a x64` or `-r win-x64`. The only output is
  `EasyLabel\src\EasyAI.Wpf\bin\Release\net9.0-windows\EasyAI.exe`; tell the user to run exactly that path.
  `bin\x64\...` is stale: never build into it or point anyone at it (two output folders made the user launch an old build).
- Build the csproj, not the `.sln`. Close `EasyAI.exe` first (locked DLLs; never kill it, ask the user). To build while the
  app must keep running use `-p:OutDir=tmp/ebuild/out/`.
- The native set (BeeNativeSegAI, libtorch, OpenCV, OpenVINO) is copied from `EasyLabel/Lib/native/segai`
  (`-p:SegAiNativeDir=...` overrides). A "Segment AI native set not found" warning means the app cannot train/infer.
- `-t:Rebuild` only when the user asks for a clean rebuild or a stale-output problem is proven.

## Native: BeeNativeSegAI (only when the task edits `BeeNativeSegAI/`)
```
"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" BeeNativeSegAI/BeeNativeSegAI.vcxproj /p:Configuration=Release /p:Platform=x64 /m /v:minimal > tmp/ebuild/native.log 2>&1
```
The vcxproj include/lib paths point at the original repo's `ThirdParty` (libtorch, OpenCV 4.5.5); fix them before the first
native build. ABI = 1: changing a public struct requires bumping the ABI. Copy the new DLL into `EasyLabel/Lib/native/segai`
only when the user asks.

## Report
Command, exit code, unique error and warning counts, the first errors, EasyAI.exe timestamp, and the log path.
A successful build proves compilation only; it is not a runtime, UI or hardware verification. State that.
On failure take the first unique error, fix, rerun the same (incremental) command. Use `elog` for other large logs.

## Tests (separate authorization)
`dotnet test EasyLabel/EasyLabel.sln -c Release > tmp/ebuild/test.log 2>&1`, then grep `Passed!|Failed!`.
SegAI native tests need `EASYAI_SEGAI_DIR=D:\CODE\EAI\EasyLabel\Lib\native\segai` (set at user level on this PC).
