# EAI-0 issues
| # | Slice | Issue | Decision | Reason |
|---|---|---|---|---|
| 1 | S1 | net9 SDK analyzer WFO1000 is an error in EasyLabel.App CanvasControl.cs (4 properties: ActiveLabel, RequestLabel, RequestEditedLabel, Tool) | fix: <NoWarn>WFO1000 in EasyLabel.App.csproj (root edit, config only) | no .cs change, no behavior change; designer-serialization analyzer only; plan invariant 2 allows net9 compile fixes; attribute fix would touch Canvas source |
| 2 | S3 | Namespace EasyAI.Wpf shadows Wpf.Ui in generated XAML (CS0234) | fix: root namespace renamed EasyAI.Desktop (mechanical sed, project/file names unchanged) | root cause, not workaround |
- Lesson: CopyFromScreen captured an unrelated private chat window; deleted. Use PrintWindow only.
| 3 | smoke | Start-Process -ArgumentList array did not quote paths with spaces: --project/--settings-dir were cut at "E:\Code\EasyVision_Unisen". The sibling READ-ONLY workspace was opened: ProjectService.Open rewrote its .easylabel/project.json LastOpenedUtc (old value lost; .easylabel + classes.txt pre-existed since 2026-09-03), and EasyAI wrote layout.json at its root (deleted by root, birth 2026-10-05). | fix script: pass one pre-quoted string; ask-user: disclose in final report | invariant: sibling workspace read-only |
## Review round 1 (reviewer sonnet): ACCEPT, 0 blocker, 0 major, 8 minor
| # | Finding | Decision | Reason |
|---|---|---|---|
| R1 | MvsSdk.TryLoadFrom overwrites shared _handle (MvsSdk.cs:88-94,149) | fix | EAI-6 will build on it |
| R2 | --settings-dir/--project not validated; bad path throws in OnStartup with null store | fix | same class as the smoke incident |
| R3 | IsMaximized lost when closed while minimized | accepted-risk | cosmetic; defer to EAI-8 |
| R4 | hard-coded English "ABI " in MainViewModel.cs:337 bypasses vi resx | fix | localization rule |
| R5 | preview-error InfoBar not closed after a good image | fix | stale error UX |
| R6 | _saveTimer.Tick never unsubscribed | accepted-risk | timer stopped, harmless |
| R7 | clamp uses VirtualScreen in system-DPI DIPs (mixed-DPI multi-monitor) | accepted-risk | unverified, no repro; owed multi-monitor smoke |
| R8 | redundant window-level Up/Down KeyBinding | accepted-risk | no failure found |
Owed (no evidence): ABI-mismatch / error-126 / cookie-leak paths, corrupt layout.json, preview race, DPI 150%, multi-monitor, light/dark switch, real MVS enumerate/open/grab (EAI-6, hardware).
