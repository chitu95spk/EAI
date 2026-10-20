# EAI-CLS results

ID: EAI-CLS (user brief; no new map entry)
Phase: bug
Verify result: PASS — 19 new tests; 69 focused tests total; Release WPF build 0 errors / 25 warnings. UI smoke unverified.
Next ID: n/a — requested scope complete.

## Scope and implementation

Plan EAI-CLS-r1 follows the direct user request and its single-writer/file-discipline requirements. No Work activation or approval fields were invented. Current runtime handled implementation and scoped self-review; no separate agent/task was created.

- `ClassTableSyncRules.Extend` trims labels, ignores case, skips blanks, preserves existing entries/order/settings, and appends enabled classes in first-seen order. OK/GOOD/Tot become Good; other names become Defect. Candidates are validated before acceptance; rejected names and reasons are returned. No extension returns null.
- `SegAiSession.SynchronizeClassTableFromAnnotations(out skipped)` sorts the existing annotation enumeration by ordinal path, reads shapes through the existing LabelMe reader, deduplicates names, and calls the existing `SaveClassTable`. Metadata and legacy sidecars are excluded through the existing enumeration. Append-only saves produce no rename plans and never write annotation JSONs. No missing classes means no save.
- The session retains the prior map and exact metadata/classes bytes. A save failure restores the map and changed files before propagating the error to the UI warning handler. Tests force a classes-file sharing violation after metadata is saved, with and without an existing advanced.json, then verify recovery and successful retry.
- A WPF partial helper scans on a worker, refreshes the real canvas/classes, and shows one informational notice listing added names and requesting retraining. Project-open code refreshes sample counts after additions. LabelMe import already calls the central image reload, which now synchronizes before reading samples. The existing selected-image reload hook also synchronizes. No file watcher was introduced.
- Rejected names/reasons are logged; when no names can be added they also receive the existing warning-style notice. If valid additions coexist with rejections, the one user notice remains the required informational additions notice and rejection details remain in the log.

The existing validator allows at most one Good class and 32 total classes. A second Good alias, overflow, excessive UTF-8 name length, controls, or invalid Unicode is skipped and reported rather than producing an invalid table. Existing entries are never removed, renamed, reordered, enabled, or retyped.

## Exact source changes

New files:

1. `EasyLabel/src/EasyLabel.SegAI/ClassTableSyncRules.cs`
2. `EasyLabel/src/EasyLabel.SegAI/SegAiSession.ClassSync.cs`
3. `EasyLabel/src/EasyAI.Wpf/ViewModels/MainViewModel.ClassSync.cs`
4. `EasyLabel/tests/EasyLabel.SegAI.Tests/ClassTableSyncTests.cs`

Existing files, current line ranges:

| File | Task-owned change |
| --- | --- |
| `EasyLabel/src/EasyLabel.SegAI/SegAiSession.cs` | Line 91: add `partial` only |
| `EasyLabel/src/EasyAI.Wpf/ViewModels/MainViewModel.cs` | Line 862: reload sync; 2233–2237: open sync/sample refresh; 2337–2340: selected-image reload hook |
| `EasyLabel/src/EasyAI.Wpf/Resources/Strings.resx` | 1430–1432: only `ClassTableSyncAddedFormat`, appended at end of data block |
| `EasyLabel/src/EasyAI.Wpf/Resources/Strings.vi.resx` | 1430–1432: same single key, Vietnamese text |
| `EasyLabel/src/EasyAI.Wpf/Resources/Strings.Designer.cs` | 916: single matching accessor |

Task-only diffs against the supplied snapshots: `artifacts/EAI-CLS/static/MainViewModel.task.diff` and `SegAiSession.task.diff`. CanvasViewModel.cs and PanelViewModels.cs still match their supplied before snapshots byte-for-byte. No prohibited files or existing test project files were edited. Other dirty hunks were preserved.

Requested report: `tmp/EAI-CLS/results.md`. Reproducible scratch tooling added: `tmp/EAI-CLS/run-checks.ps1`, `static-audit.ps1`, `SyntaxAudit.cs`. Synthetic projects remain under `tmp/EAI-CLS/synthetic/`; logs, TRX, binlogs and static evidence remain under `artifacts/EAI-CLS/`.

## Commands and measured evidence

Run from the approved workspace:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tmp/EAI-CLS/run-checks.ps1 -Check regression -Run run3
powershell -NoProfile -ExecutionPolicy Bypass -File tmp/EAI-CLS/run-checks.ps1 -Check new -Run run3
powershell -NoProfile -ExecutionPolicy Bypass -File tmp/EAI-CLS/run-checks.ps1 -Check wpf -Run run3
powershell -NoProfile -ExecutionPolicy Bypass -File tmp/EAI-CLS/static-audit.ps1
```

The runner contains the exact expanded dotnet arguments: tests use the existing SegAI.Tests project in Release; WPF uses `dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release`. Every invocation redirects output and captures a separate binlog. TEMP/TMP are `<repo>/tmp/EAI-ONL-MODE`. Existing project-local cc17 AppData/SDK/NuGet paths and offline NuGet configuration are reused through process-only settings. No package or dependency was added.

| Check | Result | Evidence |
| --- | --- | --- |
| New tests | 19 passed / 0 failed / 0 skipped, exit 0 | `artifacts/EAI-CLS/run3/new.{log,trx,binlog}` |
| Focused tests | 69 passed / 0 failed / 0 skipped, exit 0; includes the 19 new tests and 50 existing tests | `artifacts/EAI-CLS/run3/regression.{log,trx,binlog}` |
| WPF Release | 0 errors / 25 warnings, exit 0 | `artifacts/EAI-CLS/run3/wpf.{log,binlog}` |
| C# 7.3 syntax | Four new source files, 0 syntax errors | `artifacts/EAI-CLS/static/syntax.log`, `syntax-compile.log`, `syntax.rsp` |
| Scoped whitespace/conflicts/resources | PASS; git diff --check exit 0; 0 new-file whitespace/conflict/resource findings | `artifacts/EAI-CLS/static/summary.log`, `whitespace.log` |

The focused filter is `ClassTable|PanelRules|ShapeList|ClassRenameTests`, expressed as FullyQualifiedName contains clauses. Actual test-class counts: ClassRenameTests 9; ClassTableRulesTests 9; ClassTableSyncTests 19; PanelRulesTests 17; SegAiClassTableSaveTests 4; ClassTableEditRulesTests 9; SegAiNamedProjectTests 1; ClassApiInteropTests 1. No separately named ShapeList test matched; existing PanelRules coverage ran.

New tests verify NG/OK/LECH/LECH_BOT session persistence/reopen, matching classes.txt and advanced.json, annotation byte identity, unchanged files on repeat, metadata/sidecar exclusion, malformed JSON safety, failure rollback/retry, kind selection, case/padding deduplication, order, enabled flags, UTF-8/control/Unicode validation, class capacity, empty inputs, and old-model refusal. The latest synthetic 250-JSON no-missing scan recorded **25.374 ms**; earlier runs recorded 51.714 and 73.725 ms. This is a local observation, not a performance guarantee.

## Old-model UI inspection (read-only)

`SegAiSession.DecideClassPrediction` remains unchanged: an extended table rejects an old NG/OK or table-less model. `ProbeForInspect` includes `ClassCacheSuffix` in the cache stamp, so old results cannot be reused under the extended table. `Inspection/InspectService.cs` catches the resulting InvalidOperationException and produces Error results. `InspectViewModel.cs` maps these to the localized Error verdict, includes errors in the summary, and shows the existing failed-count/first-error warning. Model-load failures are also caught by MainViewModel.RunLabelAsync and shown as an error notice. Source inspection found no escaping crash or silent wrong-result path requiring a fix.

`TrainStepViewModel.CurrentBlock` continues to gate training by session/busy/environment and Train/Val availability; a mismatched loaded model is not a training prerequisite. The normal training path can create a matching candidate and reports success/failure through existing notices. Training and candidate activation were not run. These UI conclusions are source-backed; UI smoke, rendering and native old-model execution remain unverified.

## Code-change audit and residual risks

Task EAI-CLS; approved scope is brief.md behavior, exact files above, new-file tests, focused verification and this report. Preserved invariants: class ids/order/settings, annotation bytes, existing persistence/model formats and compatibility logic, real UI paths, unrelated dirty work, workspace boundary. No real dataset was opened, scanned, modified or trained; no legacy build, hardware, deployment, Git stage/commit/stash/push, branch/worktree or tracking update occurred.

Compiler assumptions: existing net9 projects retain LangVersion 12.0; all authored new source files independently parse as C# 7.3, including the nullable-flow attributes (ordinary attribute syntax). Existing-file edits introduce only partial/async/await constructs supported by C# 7.3. Edited declarations and surrounding scopes were inspected. Final compiler/root source errors: **none found**; no task-source warnings appear in final run3 logs. WPF's 25-warning total is the build summary, not a unique-warning count.

Initial verification encountered environmental MSB4184 (denied per-user Microsoft SDKs path), then the shell's disabled script-execution policy. Process-local SDK/AppData/cache settings and a process-only script invocation resolved these without source changes. Earlier evidence remains in run1/run2. No compiler-cascade errors were chased.

Residual risks: separate filesystem writes cannot guarantee recovery if rollback itself encounters a second filesystem failure; the prior in-memory map is restored and rollback failure is explicitly surfaced as a warning. External tools can also race an annotation scan; the next reload/open retries discovery. Invalid labels remain untrainable and are reported as described above. No independent subagent review was performed under this single-writer brief; scoped self-review and all listed executable/static checks passed. UI smoke is explicitly unverified. CodeGraph CLI exited; no remaining CodeGraph process was observed at completion.
