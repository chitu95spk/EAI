# EAI-2 issues (commit A review, FIX_REQUIRED cycle 1)

| # | Sev | Where | Decision | Reason |
|---|---|---|---|---|
| 1 | major | CanvasViewModel.QueueSaveIfChanged / OnSaveFailed | fix | failed save advances _savedSignature, edit silently lost; reset signature on failure for current image |
| 2 | major | MainViewModel.OpenProjectAsync | fix | second flush right before SetSession; dispose old session only after it |
| 3 | minor | DeleteVertex not wired in UI | fix | plan promises vertex delete; wire Alt+Left click in Select tool on a selected polygon vertex |
| 4 | minor | AnnotationCanvas.OnMouseDown ClickCount==2 | fix | route to DoubleClick only for Select/Polygon, else PointerDown |
| 5 | minor | CanvasView.HandleKey Tab/Esc consumed window-wide | fix | consume Tab only when canvas has keyboard focus; Esc/Tab fall through when nothing to act on |
| 6 | minor | UpdateResize/UpdateRotate freeze at edge | deferred | no data loss; log only |
| 7 | minor | ShapeHoleStore.Carry key reuse | deferred | very low probability; log only |
| 8 | note | tooltips as help text | check | tooltips are names + shortcut only (allowed by plan section 5) |
Not covered by reviewer: git scope check (coordinator does it). Owed: Rot/Polygon in real UI, hole survival through UI, 150% DPI, quick switching, save-error path; add AnnotationSaveQueue unit test (failure + replace-latest).
