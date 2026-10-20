## Update 2026-10-07 - commit A done, commit B (Step 1 Data panel) not started
Plan: tmp/EAI/EAI-2/plan-r1.md (approved by user 2026-10-07). Contract: .claude/task-contract.EAI-2.toml. Issues: tmp/EAI/EAI-2/issues.md.
Commit A = geometry (Core/Geometry), CanvasEditSession/ShapeHoleStore/ShapeClipboard (Core/Editing), DocumentEditor.DeleteMany/ReplaceMany,
SegAiSession.LoadForEdit/SaveEditedShapes/ClassNames, WPF canvas (EasyAI.Wpf/Canvas, CanvasViewModel, AnnotationSaveQueue). Build 0 errors; Core 86/86; SegAI 102 pass 2 skip (artifacts/EAI/EAI-2/a5/).
Owed (unverified): mouse path after fix cycle 1 in the real UI (pre-fix smoke OK: rect draw, undo/redo, 4 handles, vi+en; evidence artifacts/EAI/EAI-2/evidence/); post-fix smoke failed because synthetic mouse is unreliable while UltraViewer is the foreground session (harness tmp/EAI/EAI-2/smoke/smoke.ps1 has a foreground guard; run it when the PC is idle); Rot/Polygon drawing, pan/zoom, holes through the UI, 150% DPI, quick image switching, save-error path; no AnnotationSaveQueue unit test (queue lives in EasyAI.Wpf, no WPF test project).
Deferred minor: #6 resize/rotate freezes at image edge instead of clamping; #7 ShapeHoleStore.Carry key reuse.
Next: commit B per plan section 4 (Import Dataset, Add Images, summary card, Space Train/Val, list badges); drop Read CCD.

## Update 2026-10-07 - EAI-2 CLOSED (A 04e89a0b9, B da6293798)
Commit B = Step 1 Data panel (Import Dataset, Add Images, summary, badges, Space Train/Val). Build 0 errors; Core 86/86; SegAI 106 pass 2 skip (artifacts/EAI/EAI-2/b/). Review B fixed: import-failure reload, focus back to canvas after button clicks (Space). Deferred: review B #3 (badge refresh ordering).
OWED (needs idle PC, UltraViewer steals foreground; run tmp/EAI/EAI-2/smoke/smoke.ps1): mouse smoke after fixes (draw Rect/Rot/Polygon, select, nudge, Alt-click vertex delete, pan/zoom), Import Dataset zip/folder, Add Images, Space with key repeat, holes through the UI, 150% DPI, compact layout, save-error path.
Next: EAI-3 (Step 2 labelling + auto-label + import model) per tmp/EAI/plan.md section 8.
