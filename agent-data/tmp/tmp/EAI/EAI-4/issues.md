# EAI-4 commit A review: ACCEPT
| # | Sev | Where | Decision | Reason |
|---|---|---|---|---|
| 1 | minor | SegAiModelComparison epsilon on doubles (0.805-0.80 reads as change) | fix | added FloatTolerance 1e-9 + boundary test (root edit, 3 lines) |
| 2 | minor | direct session.Train while only the service reservation is set is not rejected until Train starts | accepted-risk | narrow race, outcome safe (service Train returns Busy) |
| 3 | note | session disposed before Train -> task faults instead of Failed result | accepted-risk | slot release is correct; UI wraps in try/catch in commit B |
| 4 | note | ReadRuns lists reparse-point folders | accepted-risk | names only |
| 5 | note | mAP gate could flag a strict thin-defect model | accepted-risk | warning only |
Deviation noted: run folders only hold status.json/failure.json (no summary.json/results.csv); ReadRuns returns what exists.

# Commit B review (ACCEPT)
| # | Sev | Where | Decision | Reason |
|---|---|---|---|---|
| 1 | minor | ScottPlot VersionOverride in EasyAI.Wpf.csproj bypasses Directory.Packages.props | accepted-risk | Directory.Packages.props is outside the approved in_scope; move it at the next scope revision |
| 2 | minor | OnProgress ignores callbacks once IsRunning is false: last epoch point may be dropped | accepted-risk | cosmetic; smoke curves were complete (2 and 300 epochs); result report holds all epochs |
| 3 | minor | RefreshEnvironmentAsync skipped while running and not rerun after EndRun | accepted-risk | host IsBusy is still true at EndRun, so a rerun there would also return early; next step change refreshes it |
| 4 | note | Start tooltip shows the block reason | accepted-risk | real code explaining a disabled state |
| 5 | note | ShowResultAsync may overlap Shutdown after a confirmed stop | accepted-risk | exceptions caught and logged |
Coordinator fix before review: compare-table columns (SharedSizeGroup, wrapping metric column, MinWidth 40, 5 px gaps) after the delta column clipped in vi.
Risks recorded: NU1701 (SkiaSharp.Views.WPF via ScottPlot restored with .NETFramework asset, works at runtime); 150% DPI and flags DifferentValidationSet/BoxOverextension not exercised in smoke; training time measured only on tiny data.
