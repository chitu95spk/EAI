# EasyLabel completion proposal

Date: 2026-09-09. Tracking: U397. Status: proposed; implementation not started by this audit.

## 1. Purpose and authority

The primary user is a technical operator. The user's confirmed concern is that the current
interface is insufficiently intuitive. The intended experience is to configure and edit through
visible controls, direct mouse manipulation and discoverable keyboard shortcuts. Typing names,
paths through a picker, and numeric values remains normal; editing JSON, Python, YAML or
`classes.txt` must not be necessary for supported operator workflows.

This is a reviewable companion to `../EASYLABEL_SPEC.md`, not a silent replacement of its locked
decisions or its historical review entries. That specification and several source files already
had uncommitted changes when inspected. Existing implementation is retained. The change table
below identifies exactly what should be reconciled into the specification when adopting this plan.

Scope is the standalone `EasyLabel/` solution in `E:\Code\EasyVision_Unisen - NoService`.
Frontend means the WinForms desktop interface; backend means its local Core, Vision and AI
services. No web server, cloud account or new EasyVision service is needed for this scope.

Evidence is source inspection, existing development history and official documentation. No app
was launched and no build or tests were run. Historical passing results do not certify today's
dirty tree. Product acceptance conditions below describe required behavior, not authorization
to execute verification. Build/test verification is **n/a — skipped by build policy**; any such
execution is an optional later action requiring the user's explicit authorization.

The remaining unanswered preference questions are treated as assumptions: prioritize the entire
annotation workflow, then reliable dataset operations, then assisted labeling and training.
They do not block this proposal. Runtime assets, GPU target and the mask encoding decision are
real implementation prerequisites for their respective tasks, not prerequisites for UI planning.

## 2. Current state and evidence

App/Core/Vision/Ai paths below are relative to `EasyLabel/src/`; other project paths are relative
to `EasyLabel/`, except the specification and EasyVision readers.
"Present" means inspected code exists, not that runtime acceptance has passed.

| Area | Observed state | Evidence | Consequence for the plan |
| --- | --- | --- | --- |
| Application structure | Four standalone App/Core/Vision/Ai projects and Core tests exist. | `src/*/*.csproj`, `EasyLabel.sln` | Preserve the existing separation; do not rewrite the app. |
| Manual tools | Rendering covers more kinds than the tool palette. The creation enum contains Select, Rectangle and Rotation only. Selection is one integer index. | `App/Canvas/Tools/CanvasTool.cs`, `CanvasControl.cs:53` | EL-1.3 and all-shapes M1 acceptance remain incomplete. Add polygon and multi-selection explicitly. |
| Editing foundation | Command stack, document snapshots, hotkeys, recovery and single-shape copy/paste are present in the working tree. | `Core/Commands/*`, `App/Shell/HotkeyMap.cs`, `AutoSaveService.cs`, `CanvasControl.cs:129` | EL-1.4/BE-U1 are implementation-present and acceptance-pending, not absent. EL-1.5 is partial; carry-forward is missing. |
| Command entry points | The Edit menu simulates key dispatch. Number keys both set the active class and relabel a selected object. | `App/Shell/MainForm.cs:157`, `:197`, `:229` | Introduce command IDs and separate active-class choice from changing existing annotations. |
| Discoverability | Text toolbar, fixed split and filename/status ListView. No thumbnail component, class legend, object inspector or command palette found in App. | `MainForm.cs:23`, `:274`, `PopulateImageList`; App file inventory | Prioritize visible context and structured panels over floating/docking effects. |
| UI responsiveness | Image decoding is synchronous inside `LoadImage`; Class Remap Preview/Apply call services directly in UI handlers. | `MainForm.cs:769`, `Dialogs/ClassRemapForm.cs:150`, `:166` | `async` on a method is not sufficient. Move actual work behind a cancellable operation coordinator. |
| Shell ownership | MainForm still owns services, active document, recovery, detection, menus and dialogs in one large file. | `MainForm.cs:19-80` and method inventory | EL-4.7 remains necessary; extract by responsibility while preserving working flows. |
| Persistence | Individual LabelMe/sidecar writes are atomic, but Save writes the two separately. | `Core/Io/AtomicFile.cs`, `App/Shell/MainForm.cs:840` | Preserve atomic writes and add a recoverable document-pair transaction. |
| Batch safety | Remap/bulk edit rewrite labels and class lists. No BatchBackupService or Restore last batch action found. Detection also writes unopened images directly. | `Core/Batch/ClassRemapService.cs`, `BulkLabelEditService.cs`, `MainForm.cs:1096` onward | D6 is still unimplemented and must cover detection plus class metadata, not only three dialogs. |
| Dataset status | Project scan initializes HasLabels from JSON-file existence; later save uses shape count. Every image subdirectory is scanned. | `Core/ProjectService.cs:44`, `MainForm.RefreshImageListItem` | Empty/negative labels can change meaning after reopen. Define explicit status and exclude managed output/cache directories. |
| Dataset export | YOLO/COCO/VOC conversion and ZIP packaging have implementation and existing fixture coverage. Export split logic groups by first shape's class and uses image paths for stable keys. | `Core/Convert/*`, `Core/Package/*`, `UltralyticsExportService.cs:119-175` | Keep these features; add canonical relative-path identity, parent/group-aware splits and conversion-loss visibility. |
| AI detection | Python bridge and model-assisted actions exist. Shell constructs requests without setting Task, leaving default `detect`; Python branches on that task. Cancellation is checked between images, not inside DetectService. | `MainForm.DetectAndSaveTargets`, `Ai/Detect/DetectRequest.cs`, `DetectService.cs`, `python/detect.py` | OBB/segment capability must propagate end to end. State honest cancellation boundaries and stage predictions before applying. |
| Offline runtime | `requirements.lock` is a placeholder; runtime and wheelhouse contain only READMEs. | `python/requirements.lock`, `runtime/python/`, `Lib/` | No claim of working offline inference; P2/P3 remain acceptance blockers. |
| Training | `train.py` raises NotImplementedError; no Train service/form exists in the source inventory. | `python/train.py`, App/Ai file inventory | M5 remains planned. A Train button alone cannot close it. |
| History memory | Each command holds before/after document snapshots, with no command/byte limit. | `Core/Commands/DocumentEditor.cs`, `CommandStack.cs` | Correctness comes first; then bound history memory for dense annotation documents. |

Concrete risks distinguish source facts from runtime hypotheses: synchronous handlers perform work
on the UI thread, but freeze duration is unmeasured; separate writes lack a shared commit, but no
actual corrupted user file was observed; repeated detect appends can duplicate predictions, but
model execution was not performed in this audit.

## 3. Amendments to the existing specification

| Existing section / ID | Proposed amendment | Reason |
| --- | --- | --- |
| M4.5 introduction; section 14.3 status | Preserve the dated 2026-09-07 review as history. Add a current implementation/evidence/acceptance ledger. Mark EL-1.4 and basic clipboard partial rather than missing. | The working tree has advanced beyond the review. |
| D1 / technology / section 8 | Retain .NET 8, WinForms and x64. Record that the current props use C# 12, while this workspace's user instruction requires newly added/modified C# to remain 7.3-compatible. Do not raise LangVersion or mass-downgrade existing files in this planning task. | Explicitly expose the policy conflict; target framework and source-language policy are separate decisions. |
| EL-1.3 / M1 acceptance | Add concrete completion cards for multi-select, polygon vertices, inspector, other primitive tools and masks. Keep all-eight-shapes completion pending until mask encoding P5 is settled. | Rendering is not creation/editing or verified format interoperability. |
| EL-1.4 / BE-U1 | Reconcile existing code, centralize dispatch, bound history, preserve one gesture per command. Dataset writes stay outside document undo. | Avoid duplicate implementation and prevent ambiguous undo behavior. |
| EL-1.5 provenance wording | Keep original model provenance on copied model shapes. Record new edit/source-image metadata in the sidecar if needed; manual shapes remain manual. Define pixel-preserving paste and explicit scale-to-image option for different sizes. | Sections M1 and M4.5 currently disagree; scaling must not happen silently. |
| EL-4.6 / D6 | Cover labels, sidecars, classes/settings, newly created files and detection batches. Use a unique operation ID, preflight, manifest, staging, restore conflict checks and interrupted-operation recovery. | Timestamp-only directories and JSON-only backups cannot fully reverse an operation. |
| BE-U2 | Distinguish atomic individual files from a transaction for the LabelMe/sidecar pair. | A successful first write and failed second write leaves mismatched provenance. |
| EL-4.7 / BE-U3 | Add DocumentSession, AppCommandRegistry and OperationCoordinator. Extract by ownership, not an arbitrary line-count-only target. | Thin files can still contain shared-state bugs if only split into partial classes. |
| FE-U1 / FE-U2 / FE-U3 / FE-U5 | Make the three-pane workspace, thumbnails, legend, inspector, command search and shortcuts required. Keep free-floating docking optional after the fixed/collapsible layout is usable. | Technical users need visible workflow context more than window management. |
| D3 / BE-U5 | Specify `.onnx` for the optional ONNX Runtime path. OpenVINO execution provider is not a promise that the default .NET ONNX Runtime package loads native `best.xml`/`best.bin` directly. Native IR needs an explicitly supported backend/capability. | See the official ONNX Runtime reference below. Keep Python default and training in a separate subprocess. |
| BE-U4 / M2 | Introduce a model descriptor with task, classes, input shape, backend and readiness. Separate prediction from acceptance. | Current shell defaults every request to detect and immediately applies results. |
| BE-U6 / M4 / M6 | Pull essential geometry validation, reviewed-negative handling and source-group split protection ahead of training. Keep advanced active-learning analytics in M6/M7. | Training quality depends on trustworthy datasets, not just valid export files. |
| M5 done when | Require validated artifacts, registry record, readable metrics and actual target-consumer acceptance when authorized, not merely two files existing. | Existing detailed handoff contract also requires metadata and task compatibility. |
| Section 1 verification / commit rules | Make explicit that the workspace build authorization gate controls execution. No automatic build, test, commit, branch or push follows adoption of this document. | Planning and reading do not grant execution authorization. |

Official technical reference: the [OpenVINO execution provider documentation](https://onnxruntime.ai/docs/execution-providers/OpenVINO-ExecutionProvider.html)
describes accelerating ONNX models and documents separate C# provider packaging. The proposed
format/backend capability table is a conservative consequence of that contract, not a tested
claim about a newly installed package. No dependency version or package installation is selected here.

## 4. Target desktop workspace

Use a stable three-pane workspace with collapsible side panels and a bottom job drawer. Save panel
widths and visibility per user, and class colors/project preferences per project. Separate Annotate,
Dataset and Train workspaces with the same project identity and status bar.

```text
Project / Recent   Annotate | Dataset | Train   Commands   Help / Shortcuts
Open  Save  Undo  Redo     contextual actions     runtime / active job status
+--------------------+-----------------------------------+--------------------+
| Images             |                                   | Classes            |
| Search + filters   |       Canvas                      | + Add / color      |
| Thumbnail grid     |       visible tool + class        | active class       |
| status/count       |       direct handles + hints     +--------------------+
|                    |                                   | Objects            |
| previous / next    |                                   | select/hide/lock    |
| next unreviewed    |                                   +--------------------+
|                    |                                   | Inspector          |
|                    |                                   | class/XY/WH/angle  |
+--------------------+-----------------------------------+--------------------+
Image 32/500 | 8 objects | In progress | Saved 10:25 | 125% | pointer X,Y
Jobs: operation / progress / completed-failed counts / Cancel / details
```

Suggested defaults: 240 logical-pixel image pane and 300 logical-pixel inspector at a 1440-wide
window; both adjustable. At narrow widths, collapse one side pane into a reachable tab and retain
canvas space. These are design starting points, not measured performance requirements.

Use consistent spacing, fonts, selection colors, icon-plus-label primary actions and inline
validation beside the offending input. Tooltips show action name and shortcut. Do not encode
review state only by color. Show disabled-action reasons. Provide Reset layout and English/Vietnamese
resource keys if both languages are shipped; internal enum/schema values remain unchanged.

Initial/empty state offers Open dataset and Recent projects. A missing class list opens a class
setup panel with Add, Rename, Delete and Import controls. A missing runtime explains what is
missing and offers Import offline runtime bundle; manual annotation remains usable.

## 5. Mouse and keyboard interaction contract

One AppCommandRegistry supplies ID, localized title, icon, context, CanExecute, handler and
optional default shortcut. Menu, toolbar, context menu and palette execute the same command,
not a synthetic keypress. Every supported action has a visible mouse path and is keyboard
reachable through focus navigation or the palette. Frequently used actions also have direct
shortcuts; do not invent hundreds of collision-prone global bindings.

Defaults below are proposals. Retain established V/R/O/F and Ctrl+P command-palette intent.
Allow customization, conflict detection and Reset defaults. A shortcut editor itself must be GUI driven.

| Action | Mouse route | Proposed keyboard route |
| --- | --- | --- |
| Open/save dataset | File menu / toolbar | Ctrl+O / Ctrl+S |
| Select / rectangle / rotated rectangle | Tool strip | V / R / O |
| Polygon / circle / line / polyline / point | Tool strip or More tools | P / C / L / Shift+L / K |
| Finish polygon or polyline | Double-click or Finish near cursor | Enter; Backspace removes draft vertex; Esc cancels draft |
| Pan / zoom / fit / 100% | Middle drag or Pan tool; wheel; zoom buttons | Space+drag; + / -; F; Ctrl+1 |
| Select multiple | Ctrl+click toggle; drag selection box in Select mode | Ctrl+A in canvas; Tab / Shift+Tab cycle objects |
| Move selection precisely | Drag body or inspector numeric steppers | Arrow = 1 image pixel; Shift+Arrow = 10; coalesce key repeats |
| Resize / rotate / edit vertices | Handles; inspector; vertex context menu | Focused inspector spin controls; all exact geometry reachable by keyboard |
| Undo / redo / delete | Edit menu and toolbar | Ctrl+Z / Ctrl+Y / Delete |
| Duplicate / copy / paste | Object context menu / Edit menu | Ctrl+D / Ctrl+C / Ctrl+V |
| Carry from previous image | Explicit Carry previous button | Ctrl+Shift+V; preview/choice for scale mismatch |
| Choose active class | Class legend single click | Existing 0-9 mapping retained; palette search for further classes |
| Change selected objects' class | Inspector class dropdown / context menu | F2 opens class picker; one undo unit |
| Navigate images | Thumbnail click / previous-next buttons | PgUp / PgDn; optional A/D aliases |
| Mark reviewed and move on | Reviewed & next button | Ctrl+Enter after validation |
| Find image/object / command | Search field / Commands button | Ctrl+F / Ctrl+P |
| Hide/lock object | Object-list eye/lock and context menu | Focusable list controls and command palette |
| Detect current image | Assisted labeling panel | F6 after readiness succeeds |
| Apply/reject suggestions | Suggestion list and Apply/Reject buttons | Palette commands / normal Tab-Enter navigation |
| Cancel draft / cancel active job | Canvas Cancel / job drawer Cancel | Esc cancels the active local gesture/dialog; job cancellation stays an explicit separate command |
| Export / train / runtime setup | Dataset/Train workspace buttons | Palette commands and form focus navigation |
| Shortcut reference | Help button | F1 |

Context rules: text fields keep native editing shortcuts and digits; arrows in lists navigate lists;
Space inside text inputs types a space. Modal dialogs own Enter/Escape. Tool changes do not accept
unfinished gestures. Right-click opens actions without committing a drag. A click on the class
legend changes only the creation class; relabeling existing objects is explicit. Locked/hidden
objects cannot accidentally join canvas edits. Batch delete operates only on a visible, counted
selection. Destructive dataset operations use Preview then Apply; ordinary reversible edits use Undo.

Object IDs should be maintained inside the session and compatible sidecar metadata, without
adding proprietary required fields to pure LabelMe JSON. Reordering, filtering, paste, undo and
reload must keep shape/provenance alignment. Unknown LabelMe fields need a deliberate preserve
or report policy, and unsupported shape export must never silently discard geometry.

## 6. Required user journeys

| Journey | Visible flow | Required outcome |
| --- | --- | --- |
| First dataset | Open folder -> class setup -> image -> draw -> save | A technical user can complete it from controls without editing external files. |
| Daily annotation | Select image -> choose class/tool -> draw -> inspect -> Reviewed & next | Clear active tool/class, selection, review status and save state throughout. |
| Precise correction | Select object -> drag vertex or edit inspector -> undo/redo | Canvas/list/inspector remain synchronized; one action is one undo unit. |
| Assisted labeling | Import model -> readiness -> choose scope -> predict -> review suggestions -> apply | Manual labels are preserved; repeated runs follow an explicit merge/replace policy. |
| Batch cleanup | Choose operation/scope -> preview counts and samples -> apply -> result -> restore | Exact affected files are shown and recoverable, including class-list changes. |
| Dataset export | Health check -> format -> source-group split -> preview -> destination -> export | Classes and task match; geometry losses and partial output are visible. |
| Training | Choose validated dataset -> task/base model -> basic settings -> start -> metrics -> artifacts | No terminal, Python command or YAML editing in the operator flow. |
| Recovery | Reopen after interruption -> recovery list -> compare/restore/discard | Recover document and provenance together; original data stays available if recovery is corrupt. |

## 7. Backend contracts that support the interface

Keep `App -> Core/Vision/Ai`, `Vision -> Core`, `Ai -> Core`. Core must stay free of WinForms,
OpenCV and Python.NET types. Thumbnail bitmap ownership therefore belongs to App/Vision;
Core may own cache keys/budgets, but not a `Bitmap` cache masquerading as UI-free Core.

| Contract | Responsibility | Required behavior |
| --- | --- | --- |
| DocumentSession + DocumentEditor | Open document, selection, dirty/review state, undo and recovery snapshot | Stable project/image identity and revision; one gesture per command; no stale async result applied to another image. |
| AppCommandRegistry | Route all UI actions | Shared enable rules and shortcuts; consistent mouse/keyboard result; instance-owned state. |
| ProjectIndex / ProjectStore | Enumerate, classify, filter and persist project preferences | Exclude `.easylabel`, configured output/runs directories; handle Unicode and same-stem image collisions explicitly. |
| LabelStore / document transaction | Save LabelMe plus sidecar | Stage both, journal intended revision, recover interrupted commit; mark Saved only after both succeed. |
| BatchBackupService | Preview manifest, backup, write and restore | Snapshot originals before first mutation; track created/deleted files, classes/settings and hashes; stop on backup failure; never overwrite later user edits during restore silently. |
| OperationCoordinator | Progress, cancellation, concurrency and logging | Immutable request snapshot, operation ID, one writer per project, bounded worker concurrency, result summary; UI thread only renders progress. |
| Detect service / model descriptor | Load/validate model, infer and return suggestions | Correct detect/OBB/segment task, class mapping, backend capability, bounded model cache and documented cancellation boundary. |
| DatasetValidation / export services | Validate geometry/schema, preserve identity and publish artifacts | Dry-run losses, reviewed negatives, stable class indices and relative-path identities, grouped split manifest, staged output. |
| Train service | Own subprocess, logs, state and artifact registry | Versioned JSON-line protocol; separate stderr; process-tree cancellation; persisted run manifest; no accidental orphan workers. |

Document writes and recovery share revision-aware serialization. A timer save for image A must
not clear newer edits or recovery for image B. External file modifications need a Reload/Keep
local/Save copy decision. A corrupt file should be flagged in the image list and must not silently
become an empty annotation. A successful read-only scan should not imply that every image has
valid annotations or has been reviewed.

Define persistent statuses without confusing them with JSON existence: unlabeled, in progress,
reviewed-positive and reviewed-empty (the last two can use the existing reviewed status plus an
explicit negative-image flag). Rejected model proposals do not turn an image into a verified negative.
Reviewed images changed by editing return to in progress; export selects states explicitly.

Backups require a manifest with unique operation ID, relative paths, original existence/hash,
created outputs and final status. A project-level write lock prevents a remap and detect batch
from racing. Cancellation reports completed/pending/failed items and keeps restore available;
it must not pretend a partly written batch was rolled back. Expensive inference may finish its
current image before cancellation; the UI should say so and discard results after cancellation.

Split datasets by source group before generating training augmentations. Keep related frames,
duplicates and every derived image with the source; augment the training subset only by default.
Use project-relative canonical identity so moving a dataset does not change a fixed-seed split.
Essential health checks cover unknown/empty classes, non-finite/out-of-bounds/degenerate geometry,
missing images, sidecar mismatch, label-file collisions and incompatible export shape types.

The offline runtime screen accepts an approved local bundle, verifies its manifest/hashes and
shows package/CPU/GPU/backend readiness without altering system Python or accessing the network.
Training weights must also be locally available; selecting a model name must not trigger a download.
The capability table distinguishes `.pt`, `.onnx` and native OpenVINO IR and never promises support
from filename extension alone. A manual-only application can be released while AI assets are pending,
but the full product cannot be called complete until the AI/training gates are satisfied.

## 8. Execution cards and dependencies

All implementation cards start **open**. Paths in Files are relative to `EasyLabel/`. Each card
must preserve existing work, refine its exact file list before editing and record evidence separately
from status. No card authorizes a build/test run. Estimated size S/M/L is relative scope, not a delivery promise.

| ID | Priority / size | Existing IDs | Depends on | Files / ownership | Deliverable and acceptance behavior |
| --- | --- | --- | --- | --- | --- |
| EL-C01 | P0 / S | EL-1.4, BE-U1, EL-1.5 | none | `Core/Commands/*`, `App/Shell/AutoSaveService.cs`, `HotkeyMap.cs`, `MainForm.cs`, `App/Canvas/CanvasControl.cs`; specification status ledger | Reconcile current edits and missing acceptance; inventory every mutation; preserve basic undo/recovery/clipboard. No duplicate rewrite and no unsupported done status. |
| EL-C02 | P0 / L | EL-4.6, BE-U2, D6 | C01 | `Core/Io/*`, new `Core/Batch/BatchBackupService.cs`, `Core/Batch/*`, `App/Dialogs/*`, `App/Shell/*` | Transactional document pair plus batch backup/restore. Failure before/during a write has a recoverable outcome; classes and created files are included; restore detects intervening edits. |
| EL-C03 | P0 / L | EL-4.7, BE-U3, FE-U5 | C01 | new `App/Composition/*`, `App/Shell/*`, new `Core/Services/*`, affected dialogs | DocumentSession, shared command registry and job coordinator. No scan/decode/batch work on UI thread; every long operation has status and truthful cancellation. Move existing implementations behind contracts without changing formats. |
| EL-C04 | P1 / M | FE-U1, FE-U3 | C03 | `App/Shell/*`, new `App/Panels/*`, `Core/Model/ProjectSettings.cs`, `App/Canvas/ClassPalette.cs` | Three-pane shell, class add/rename/color, object list, inspector and reset layout. Selection and save state stay visible; no class-file editing needed. Theme and optional language resources centralized. |
| EL-C05 | P1 / L | EL-1.3, EL-1.5 | C03, C04 | `App/Canvas/*`, `App/Canvas/Tools/*`, `Core/Commands/*`, `Core/Model/*` | Multi-select, polygon create/edit, exact inspector edits, nudges, lock/hide, duplicate and carry-forward. Mouse and keyboard share command semantics; dimension mismatch and provenance are explicit. |
| EL-C06 | P1 / M | EL-0.4, FE-U2, FE-U5 | C03, C04 | new `App/Shell/ThumbnailGrid.cs`, `App/Panels/*`, new `Vision/Thumbs/*`, `Core/ProjectService.cs` | Async virtual thumbnails, bounded cache, filename/class/status search, Reviewed & next, next-unreviewed and command/shortcut editor. All actions mouse and keyboard reachable. |
| EL-C07 | P0 before AI/training / M | BE-U6, minimum M6 | C02, C03, C06 | new `Core/Validation/*`, `Core/Convert/*`, `Core/Package/*`, `Core/Batch/Augment*`, `App/Dialogs/*` | Consistent negative-image status, data-health report, conversion-loss preview, relative identities and source-group split rules. Export preview explains every skipped/rejected file. |
| EL-C08 | P1 / M | BE-U4; BE-U5 optional; P2/P3 | C03; supplied runtime/model assets for acceptance | `Ai/Runtime/*`, `Ai/Detect/*`, `python/detect.py`, `python/requirements.lock`, new `App/Panels/RuntimePanel.cs` | GUI runtime/model readiness and explicit format/task capability. Offline bundle installation has clear progress/failure states; manual tools work without AI. Optional ONNX backend is a separate bounded implementation decision. |
| EL-C09 | P1 / L | EL-2.3, BE-U4 | C02, C05, C07, C08 | `Ai/Detect/*`, `App/Shell/*`, new `App/Panels/SuggestionsPanel.cs` | Detect current/selected/unlabeled -> preview -> accept/reject. Correct OBB/segment task, explicit class mapping, no duplicate accumulation from silent reruns, open-document undo and dataset restore. |
| EL-C10 | P1 / L | EL-1.3 remainder, P5 | C05; P5 only for mask portion | `App/Canvas/Tools/*`, `App/Canvas/*`, `Core/Model/*`, `Core/Convert/*` | Circle, line, polyline, point and agreed mask workflow. All-eight-shapes M1 remains incomplete until mask interchange is resolved; unsupported exports explain conversion or exclusion. |
| EL-C11 | P1 / L | EL-5.1, EL-5.2; P4 | C02, C03, C07, C08, C09; M4.5 acceptance; supplied training assets | new `Ai/Train/*`, `python/train.py`, new `App/Train/*`, `Core/Model/*` | GUI training config with Basic/Advanced, validation, progress/loss metrics, cancel/resume and failed/interrupted run recovery. No external config editing. Full M1 remains a separate unresolved gate if C10 is incomplete. |
| EL-C12 | P1 / M | EL-5.3, EL-5.4 | C11 | `Ai/Train/*`, new `Core/Models/*`, `App/Train/*`, `Ai/Detect/*` | Run comparison, validated model registry, ONNX/IR artifacts and Label remaining action. Handoff requires `best.xml`, `best.bin`, `metadata.yaml` with names/imgsz/task; current EasyVision handoff is detect/OBB only. |
| EL-C13 | P2 / M | FE-U4, FE-U1 optional | C05, C06 | `App/Canvas/*`, `Vision/*`, `Core/Commands/*` | Bound undo/cache memory; prioritize visible images, cancel stale decode and dispose images. Add pyramid/magnifier only where measured workloads justify it; free docking remains optional. |
| EL-C14 | P1 release closure / M | acceptance across M0-M5 | C02-C12, essential C13 limits; external acceptance evidence when authorized | App packaging/settings/help, existing specification/changelog; exact packaging files selected during implementation | Offline distribution, GUI diagnostics and recovery, retained user projects/settings, consistent DPI/focus behavior, runtime capability disclosure. No full-product completion while required functionality or acceptance is blocked. |

Recommended execution sequence: C01 -> C02 -> C03 -> C04 -> C05 -> C06 -> C07 -> C08 ->
C09 -> C10 -> C11 -> C12 -> C13 -> C14. C03 foundations may be extracted narrowly inside C02
where needed, without using that exception to redesign the whole shell. Hardware/runtime asset
collection can be prepared while UI work proceeds, but no parallel agent execution is requested.

The first implementation batch is C01 only, followed by a bounded C02 transaction/backup slice.
Do not begin with training, install new packages for visual polish, or replace WinForms.

## 9. Completion gates

| Gate | Scope | Required evidence / behavior before claiming complete |
| --- | --- | --- |
| G1: reliable manual workflow | C01-C06 and relevant C10 tools | Open/create classes, draw, select/edit, copy/carry, undo, save, review and recover all through GUI. Mouse-only route and keyboard route both complete the same tasks. |
| G2: trustworthy dataset | C02, C07 | Label/sidecar pairs remain aligned; interrupted batch has explicit recoverable state; restore recovers original contents; reviewed negatives and split ancestry survive export/package. |
| G3: usable assisted labeling | C08-C09, P2/P3 | A supplied offline runtime/model really executes; task/class/geometry match; suggestions can be inspected, rejected and restored; no hidden download requirement. |
| G4: training and integration | C11-C12, P4 | A real run produces valid artifacts and metadata and a consumer-compatible detect/OBB model; segment output is clearly distinct from the current EasyVision handoff. |
| G5: product release | All required cards and G1-G4 | No unresolved data-loss or inaccessible-action blocker. Install/start/open existing dataset works on the designated offline target. Help, diagnostics and recovery are accessible without a terminal. |

Suggested usability targets for later authorized evaluation: a technical user completes the first
rectangle annotation/save in five minutes without verbal instructions; any common action is within
two clicks or one direct shortcut after selecting context; no action requires editing configuration
files. These are proposed targets, not observed measurements.

Suggested performance acceptance profile: name the target Windows version, CPU/GPU/RAM and storage
before interpreting results. At 1366x768 and 100/150/200% scaling, primary actions remain reachable.
A 2,000-image, 12 MP dataset uses virtual thumbnails with an initial 128 MB cache budget;
navigation cancels stale loads. UI input response target is below 100 ms while jobs run; expensive
decodes show progress rather than blocking. A 100 MP / dense-polygon workload is an extended
profile requiring explicit image and history budgets, not a claim of flat memory or >30 fps on
unknown hardware. Bound decoded-image, thumbnail, command-history and model caches separately.

Later verification, if authorized, should focus on meaningful failure boundaries: save between the
two file commits, corrupt recovery, disk-full/permission failure, interrupted batch restore,
external edits, cancel during inference, class remap with existing annotations, moved project roots,
Unicode/same-stem images, mixed tool undo and training subprocess failure. Existing tests are
evidence to inspect and extend as needed; none was executed or scheduled by this document.

## 10. Decisions still requiring concrete inputs

1. P2/P3/P4: designated offline runtime bundle, pinned compatible dependencies, sample model,
   representative dataset and hardware target before AI/training acceptance. Do not select CUDA
   versions or promise GPU behavior based on this source audit.
2. P5: mask encoding/interchange. Retain the all-shapes requirement as open; do not call polygon
   outlines equivalent to every raster mask, including holes or disconnected regions.
3. Whether a manual/dataset edition should ship before AI is ready. This is an optional release
   decision, not permission to claim full completion or silently change D5.

No user approval is needed to understand or review this proposal. Implementation should use the
existing workspace permission rules, preserve current edits and obtain only the concrete missing
inputs at the point where they matter. Optional build/test execution requires explicit authorization.
