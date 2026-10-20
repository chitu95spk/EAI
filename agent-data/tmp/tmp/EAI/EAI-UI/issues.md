# EAI-UI commit A review: ACCEPT
| # | Sev | Decision | Reason |
|---|---|---|---|
| 1 | minor Up/Down gesture text on prev/next image | fix | removed (keys nudge a selected shape) |
| 2 | minor two Primary buttons (start screen Open folder + Data Add images) | accepted-risk | different panels; revisited in the full Labelme re-layout |
| 3 | minor Ctrl+O/U/Q not guarded by InputBlocked | accepted-risk | commands have CanExecute that already follow IsBusy |
| 4 | note EAI-0 smoke uses ChooseNativeFolderButton on the main window (now in About) | accepted-risk | old harness only |
Unverified: popup menu rendering, About window look, 150% DPI, Ctrl+O/U/Q.

# Commit B review: FIX_REQUIRED -> fixed
| # | Sev | Decision | Reason |
|---|---|---|---|
| 1 | major RunLabelAsync/RunStep1Async skip ConfirmLeaveImage (Auto-save OFF: auto-label/accept/import could overwrite held edits) | fix | ConfirmLeaveImage(false) added to both |
| 2 | major EmbedImageData default true | fix (deviation from Labelme default, told to user) | default false: every autosave re-encoded 2520x2600 images; setting stays in Settings |
| 3 | minor mirror write failure blocks sidecars; mirror not path-checked | fix | mirror written last; output dir inside the project rejected |
| 4 | minor OnSaveFailed after navigating away loses the held edit | accepted-risk | same as autosave before; error notice shown |
| 5 | note Settings pages one at a time vs Labelme single scroll | open | polish in D/E |
| 6 | note no automated test for Auto-save OFF paths | fix | UIA smoke tmp/EAI/EAI-UI/smoke/smokesave.ps1: draw held, prompt shown + Cancel, Ctrl+S writes JSON (no imageData) |

# Commit C review: FIX_REQUIRED -> fixed
| # | Sev | Decision | Reason |
|---|---|---|---|
| 1 | major Merge dropped Segment AI holes | fix | IHoleCarrier.HasHoles (default false, store overrides); TryBuildMerge refuses shapes with holes; test added |
| 2 | major enabled Settings rows with no consumer (predefined image/shape flags) | fix | rows disabled with reason (consumed in D) |
| 3 | major 'allow points outside image' made the image unsaveable (SaveEditedShapes validates regions in bounds) | fix | row disabled with reason until the save path clamps; Core clamp stays |
| 4 | minor exporters (YOLO) not hardened for circle/point/line/linestrip | deferred | export is not in this milestone; note for EAI-8 / export work |
| 5 | minor circle/line could be dragged to zero size | fix | UpdateVertex rejects drafts below MinBoxSize |
| 6 | minor Ctrl+arrows/Ctrl+A consumed while a list/slider has focus | fix | guard before the ctrl block |
| 7 | minor ImageAdjuster allocates a Bgra32 copy per slider tick on the UI thread | accepted-risk | brightness dialog only; revisit if lag is reported |
| 8 | minor keep-previous-annotation saves for any empty image visited; holes not carried | accepted-risk | opt-in setting (default off) |
Unverified: Merge UI, Add point, Delete vertex, Select by direction, Keep previous, context-menu content, brightness effect, colour modes, 150% DPI.

# Commit D review: ACCEPT
| # | Sev | Decision | Reason |
|---|---|---|---|
| 1 | minor Capture while compact returns the wide snapshot (View-menu/float changes made in compact lost) | accepted-risk | compact is the small-window fallback; revisit after runtime check |
| 2 | minor help text SetImageFlagsHint (rule: no help text) | fix | row Description and the string removed from resx/Designer/SettingsModel |
| 3 | minor Shapes list: click on a hidden row leaves row highlight out of sync | fix | Refresh() in the early-return branch |
| 4 | note floating panel windows get no window shortcuts | accepted-risk | Shapes list Delete works; noted |
| 5 | note compact threshold has no hysteresis | accepted-risk | unverified at runtime; polish pass |
| 6 | note TryRestore comment inaccurate | accepted-risk | callers always rebuild default |
Coordinator fixes before review: Serializer.Xml package + namespace, Adopt() re-parenting, enabled-rows test. vi regression smoke could not be repeated (desktop busy: 'dialog not foreground'); en passed twice on this layout; one earlier vi run showed Start disabled after Use-this-model while input was being lost (harness).
Unverified: mouse float/redock, restart round trip, compact mode, close-button<->View sync, Flags toggling, Shapes visibility checkbox, 150% DPI.

# Commit F review (tabs design): FIX_REQUIRED -> fixed
| # | Sev | Decision | Reason |
|---|---|---|---|
| 1 | minor stale charts after project change | fix | TrainChartsView clears both plots once when HasChart is false and dirty |
| 2 | minor drawing tools/toolbar/Edit menu clickable while canvas hidden on Train tab | fix | tool strip, toolbar WrapPanel and Edit menu disabled via MainViewModel.IsLabelTab |
| 3 | note Train/Val click on another row prompts Save/Cancel when the selected image holds unsaved edits (Auto-save off) | accepted | safe (SaveNow then flush); documented |
| 4 | note CanSetMode not re-queried at drag start | accepted-risk | drag blocks row clicks |
| 5 | note every label group has AutomationId ShapeList | accepted-risk | smoke scopes by parent |
| 6 | note shape-flags settings row shows help text | fix | row hidden |
| 7 | note stock CheckBox/ToggleButton in Button rows | accepted-risk | WPF-UI themed |
Coordinator: deleted 4 obsolete views; fixed startup XamlParseException (BasedOn on ui:TextBlock); smoke4.ps1 updated to tab ids + FileValButton. Unverified: vi tool-strip 3-line captions, float/redock of the retitled panel, 150% DPI.

# Commit H review (Import LabelMe, Convert to YOLO/OBB, Export preset): FIX_REQUIRED -> fixed
| # | Sev | Decision | Reason |
|---|---|---|---|
| 1 | major importer: only IO/JSON exceptions handled per file; others skipped classes.txt extension | fix | any non-cancel exception fails that file; ExtendClasses always runs |
| 2 | major Replace overwrote image+annotation without confirmation | fix | confirm dialog naming the conflict count before Replace |
| 3 | minor imagePath could reach outside the source folder | fix | candidate must be under the source root |
| 4 | minor source folder inside the project | fix | refused in Preview |
| 5 | minor image extensions list | accepted-risk | jpg/jpeg/png/bmp = what the project loads |
| 6 | minor converter aborted the whole run on one unwritable file | fix | per-file error list, counters and classes.txt kept |
| 7 | minor OBB rectangle of zero area written | accepted | follows the user's OBB script |
Unverified: real import run via UI (preview, conflicts, Replace/Rename, cancel). Evidence: Convert dialog ran in bbox and OBB on a 3-file fixture (artifacts/EAI/EAI-UI/smoke/en-h*.png), tests Core 148 / SegAI 288.
