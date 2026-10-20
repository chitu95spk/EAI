# EAI-CLS brief: class table follows the labels (Codex, single writer)

Reply in English. Bound by tmp/EAI-ONL-MODE/codex/HANDOFF.md sections 0 and 6 (project-local TEMP/TMP, no git commit/stage/stash). Save results to `tmp/EAI-CLS/results.md`.
User decision (2026-10-18): users must not have to open "Class table" by hand. When the project's annotations contain a label that is not in the class table
(drawn, imported from LabelMe, or written by another tool), EasyAI adds it automatically and tells the user. Today such a label is shown as
"bị Segment AI bỏ qua" (`PnlIgnoredBySegAi`) and silently not trained. Reproduction data: `E:\Training\TrainLOGONEW` (classes.txt = NG, OK; the LabelMe JSONs in `images/`
also contain LECH and LECH_BOT oriented_rectangle shapes). DO NOT open, modify or train on that folder; use synthetic temp projects under `tmp/EAI-CLS/` in tests.

## Behaviour
1. New pure rule (new file, e.g. `EasyLabel/src/EasyAI.Wpf/Infrastructure/ClassTableSyncRules.cs` or in EasyLabel.SegAI if it must be shared): given the current `SegAiClassTable` and the distinct label names found in annotations, return the extended table or null when nothing is missing.
   - Matching is the same as `SegClassMap.LabelOf` (trim, ignore case). Blank names are skipped. A name that differs only by case/padding from an existing class is NOT added.
   - New class: kind Defect, enabled; if the trimmed name equals `OK`, `GOOD` or `Tot` (ignore case) use kind Good. Keep existing order, append in first-seen order (stable, deterministic).
   - The result must pass `SegAiClassTable.Validate` and the project-name rule (no leading/trailing spaces; no duplicates ignoring case); an invalid name (e.g. too long or illegal) is skipped and reported, never throws.
2. Session API: `SegAiSession` gets a method that scans all annotation JSONs of the project (reuse `EnumerateAnnotationJsons` / existing parsing, do not re-implement LabelMe parsing), applies the rule and saves through the existing `SaveClassTable` (so classes.txt and advanced.json stay consistent). Returns the list of added names. It must be idempotent and cheap on a project with nothing missing (~250 JSONs).
   `SegAiSession` is `sealed class` (not partial) and has ~10 uncommitted lines from another session: either add `partial` to the declaration line and put the new method in a new file `SegAiSession.ClassSync.cs`, or add it as a contiguous block; keep the edit inside one small region.
3. Call sites (EasyAI.Wpf): after a project is opened (where `_segSession = session;` is assigned in MainViewModel.cs, about line 2182), after LabelMe import completes, and after the canvas/label list reloads from external changes if such a hook exists. Each call: if names were added, call `Canvas.RefreshClasses()` like `NewLabel()` does and show ONE short notice via the existing `ShowDataNotice` (Informational) listing the added names. Notice text must be a new resource key in BOTH `Strings.resx` and `Strings.vi.resx` (+ `Strings.Designer.cs` accessor in the same style); Vietnamese e.g. "Đã thêm nhãn mới vào bảng lớp: {0}. Model cũ cần train lại." and English equivalent. No help/tip text beyond that one notice.
   A drawn label already goes through `NewLabel()`/class pickers (no change needed); do not change the Class table dialog.
4. Old models: when the table gains classes, the existing NG/OK model no longer matches (`DecideClassPrediction` throws). Do not change that logic; the notice already says retrain. Do check (read-only) what the Inspect/Train UI shows in that state and report it in results.md; fix only if it is a crash or a silent wrong result.
5. Idempotence/safety: never remove or reorder classes; never touch annotation JSON content; if `SaveClassTable` throws (IO/Invalid), show the existing warning style notice and leave the table unchanged.

## File discipline (other sessions have uncommitted edits in these files)
Snapshots of the working files BEFORE your work are in `tmp/EAI-CLS/before/` (MainViewModel.cs, SegAiSession.cs, PanelViewModels.cs, CanvasViewModel.cs). Claude extracts your hunks by diffing against them, so keep each edit in existing dirty files to a few contiguous lines near the stated places; do not reformat, reorder usings far from existing ones, or touch other hunks.
Prefer new files for logic and tests. Do NOT edit: LabelDialog.*, LabelStepViewModel.*, AppSettings.cs, ShortcutTable.cs, MainWindow.xaml(.cs), Services/Agent/*, SegAiTrainingPipeline.cs, SegProjectStore.cs, any EasyLabel.SegAI file except SegAiSession.cs (+ the new file).
Resource files `Strings.resx`, `Strings.vi.resx`, `Strings.Designer.cs` are dirty from other sessions too: add only your one key (contiguous, at the end of the file's data block) and list the exact line ranges.

## Tests (new files only)
- Pure rule tests: missing/none, case/padding duplicates, OK/GOOD kind, order stability, invalid names skipped, empty annotations.
- Session test on a synthetic project under `tmp/EAI-CLS/`: LabelMe JSONs with NG, OK, LECH, LECH_BOT -> table becomes NG, OK, LECH, LECH_BOT; second call adds nothing; classes.txt and advanced.json consistent; the JSON files are byte-identical before/after.
Place them where the existing tests for these types live (EasyLabel.SegAI.Tests uses source links: its csproj is dirty by another session, so do NOT edit it; if the new rule file must be linked, put the rule in EasyLabel.SegAI (project reference) instead, or create a new test project `EasyLabel/tests/EasyLabel.ClassSync.Tests`).
Run: the new tests, `dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release` (0 errors), and the existing class-table/ShapeList tests you can reach (`ClassTable|PanelRules|ClassTableRules`). Log to files, report counts. UI smoke is unverified: say so.
