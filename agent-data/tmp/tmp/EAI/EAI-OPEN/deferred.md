# Deferred work: EAI-OPEN (everything owed except EAI-CLS)

Written 2026-10-09. Plan NOT approved, no build/test authorization yet. Sources: tmp/EAI/EAI-{2,5,DATA,UI,AI}/deferred.md and issues.md.
Smoke lessons: use PrintWindow (not CopyFromScreen); UIA Invoke for Run buttons; mouse clicks on first radio are flaky; UltraViewer steals foreground (needs idle PC); always build before dotnet test.

## Checklist
### EAI-AI
- [ ] Smoke at 150% DPI (needs Windows display scale change: ask the user first)
- [ ] SAM2 / YOLO-World latency on the target GPU (hardware)
- [ ] Ask the user whether any S3b review finding is still open (list was lost)
### EAI-UI
- [ ] Import LabelMe real run + Replace confirmation in UI
- [ ] Export formats via UI
- [ ] vi smokes: Inspect, Import, Convert
- [ ] Float/redock by mouse, layout restart round trip, compact mode
- [ ] Model switch to a non-active candidate + "Inspect with the new model"
- [ ] DPI 150%
- [ ] Long-run migration crash / read-only cases
- [ ] Flaky native SegAI test in full runs (passes alone): find cause
- [ ] Decide fate of uncommitted: EAI-UI contract plan-r4 edit, ucReport.* RJButton edits
### EAI-2
- [ ] Mouse smoke after fixes (tmp/EAI/EAI-2/smoke/smoke.ps1): draw Rect/Rot/Polygon, select, nudge, Alt-click vertex delete, pan/zoom, Import zip/folder, Add Images
### EAI-5
- [ ] vi smoke, CSV via UI, cancel during Run all, drag-drop, vi column clipping
### EAI-DATA
- [ ] Kill mid-migration, read-only folder, thousands-of-images timing
- [ ] Round-trip test for unknown keys + imageData
- [ ] Each export format via UI, cancel in UI, large project

## Next commands
1. Ask the user to approve a plan and grant build/run_tests (update .claude/task-contract.EAI-OPEN.toml approval and authorization).
2. Build EasyLabel.sln into artifacts/EAI/EAI-OPEN/<run>/build.log; run the four test projects without --no-build.
3. Run the smoke scripts on an idle PC; log under artifacts/EAI/EAI-OPEN/<run>/.
