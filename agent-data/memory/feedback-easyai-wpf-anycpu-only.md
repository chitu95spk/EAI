---
name: feedback-easyai-wpf-anycpu-only
description: "Build EasyAI.Wpf (EasyLabel) as Any CPU only, never x64; run bin\\Release, not bin\\x64 (stale)"
metadata:
  node_type: memory
  type: feedback
  originSessionId: de50294f-4e69-490b-98ed-8bd6ae8f78c4
  modified: 2026-10-15T12:36:49.647Z
---

Build `EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj` with `dotnet build -c Release` (Any CPU) only, never with x64 platform/arch. The one output is `bin\Release\net9.0-windows\EasyAI.exe`.

**Why:** two output folders (`bin\Release`, `bin\x64`) made the user launch an old build and think a fix was missing (2026-10-15, EAI-LOGO Delete-key work). `bin\x64` is stale from Oct 8.

**How to apply:** tell the user the exact `bin\Release` exe path after every build; never point scripts at `bin\x64`; do not delete `bin\x64` unless asked. Also written into `.claude/skills/ebuild/SKILL.md`. Close the running app before building into `bin\Release`; to build beside a running app use `-p:OutDir=<project-local folder>/`.
