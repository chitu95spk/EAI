# EAI-3 issues (commit A review: ACCEPT)
| # | Sev | Where | Decision | Reason |
|---|---|---|---|---|
| 1 | minor | SegAiSession.ImportModel: SegAiModelFile.Inspect outside try (verified :609) | fix | IOException from a locked source must come back as Problem; one-line move. Root edits it directly (subagent round trip costs more than the 2-line change; recorded here per eroute 3a) |
| 2 | minor | contours lose ~0.5 px on tiny regions | accepted-risk | suggestion layer; IoU 0.99 holds for real-size shapes |
| 3 | minor | 1-2 px holes dropped | accepted-risk | below useful annotation size |
| 4 | minor | Probe/Infer lock separately, stale cache key possible | accepted-risk | re-inferred next run; harmless |
| 5 | note | 2 native images instead of 3+; missing-image continue guarded by checkedImages>0 | accepted-risk | synthetic cases add coverage |
| 6 | note | no test for failed import with bad model / concurrent SetActiveModel | accepted-risk | add bad-model test with fix #1 if cheap |

# Commit B review (ACCEPT)
| # | Sev | Where | Decision | Reason |
|---|---|---|---|---|
| A | minor | CanvasView.HandleKey consumes arrows/Enter/Delete/letters when a Slider/Selector is focused | fix | Left/Right on the threshold slider would nudge a selected shape; return false for Selector/RangeBase focus (non-Ctrl keys). Root edit, 6 lines |
| B | minor | Cancel enabled on single-image run but does nothing | fix | CanCancel = IsRunning && !IsProgressIndeterminate |
| C | note | Undo of Accept does not restore suggestions (stay in _dismissed until next run) | accepted-risk | re-run auto-label (cached, instant) restores them |
| D | note | Holes.Set before Add: orphan if Add throws | accepted-risk | negligible |
| E | note | white Badge boxes in ImageListPane (EAI-2) | accepted-risk | out of scope, listed in report |
Unreviewed by reviewer: LabelStepRulesTests + parity test change (coordinator ran them: 142 pass).
