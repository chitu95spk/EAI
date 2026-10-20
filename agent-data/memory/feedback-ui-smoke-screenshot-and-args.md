---
name: feedback-ui-smoke-screenshot-and-args
description: UI smoke scripts must use PrintWindow (not CopyFromScreen) and one pre-quoted argument string for paths with spaces
metadata:
  node_type: memory
  type: feedback
  originSessionId: fa1df89c-fc0d-4e0a-882e-08ad010f38d0
  modified: 2026-10-05T07:58:52.389Z
---

Two mistakes in the EAI-0 UI smoke (2026-10-05): (1) `CopyFromScreen` captured an unrelated private chat window lying over the app; the file was deleted at once and screenshots now use `PrintWindow` with flag 2. (2) `Start-Process -ArgumentList @(...)` does not quote items, so `--project "E:\Code\EasyVision_Unisen - NoService\..."` was cut at the first space; the app opened the read-only sibling workspace `E:\Code\EasyVision_Unisen`, rewrote `.easylabel/project.json` LastOpenedUtc there and wrote a stray `layout.json` (deleted).

**Why:** the repo path contains spaces and the sibling workspace is read-only; a wrong argument silently targets real user data.
**How to apply:** pass one pre-quoted string to `-ArgumentList`; make the smoke script verify the app shows the fixture before doing anything (guard in `tmp/EAI/EAI-0/smoke/smoke.ps1`); never view a screen grab that may include other windows. Related: [[persistence-tests-hijack-bin-release]].
