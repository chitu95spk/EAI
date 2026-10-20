# EAI-2-r1-plan-v1 (architect candidate, NOT yet approved by the user)

Source: architect report (opus), 2026-10-07. Two explorer claims were wrong: GeometryHelper/RotatedBoxGeometry is in
BeeInterface\ShapeEditing\GeometryHelper.cs:160-324 (not BeeCore); ViewTransform is in EasyLabel.App (WinForms).

## Scope
Commit A: UI-free geometry + edit session in EasyLabel.Core, edit/save API with holes in EasyLabel.SegAI, WPF annotation
canvas replacing ImagePreviewPane. Commit B: Step 1 Data panel wired to real SegAiSession methods.
Dropped: Read CCD (EAI-6), LabelMe/YOLO/COCO/VOC import, export, any data check without a real backend.

## Invariants
- Core gets no reference to WPF/WinForms/EasyLabel.App; App's EditController/CanvasControl unchanged.
- Cloned files start with `// Cloned from <path>@<commit>`. No EasyVision file changes (C# 7.3 rule not applicable).
- LabelMe JSON stays plain; `.easyai/seg` sidecars own holes and mode; non-SegAI shapes preserved; edit never changes mode.
- One undo unit per mouse-up that changed geometry; Esc during drag = nothing recorded; right-click records nothing.
- Every saved region inside the image; SegLabelRaster.ValidateAnnotations must pass.
- UI rules (plan.md section 5): WPF-UI only, one Primary per panel, ICommand, enum modes as segmented toggles, vi/en, no help text.

## Architecture
- Core/Geometry: RotatedBoxGeometry (clone, public, PointF), EditHandleMetrics (0.42 cap, 6 px floor, rotate gap 24-96),
  ShapeHitTester (corner > rotate handle > vertex > even-odd), ViewTransform.
- Core/Editing: CanvasTool enum (Select/Rect/Rot/Polygon), CanvasEditSession (UI-free pointer state machine, edge clamp),
  ShapeHoleStore (geometry key -> holes, grow-only per image session), ShapeClipboard.
- DocumentEditor: add DeleteMany, ReplaceMany.
- SegAI: SegAiSession.LoadForEdit(rel), SaveEditedShapes(rel, doc, sidecar, holesOf), ClassNames (if absent).
- WPF: Canvas/AnnotationCanvas (FrameworkElement, MatrixTransform, OnRender overlay, frozen brushes, EvenOdd holes),
  CanvasView.xaml, CanvasViewModel (tool/class enums, Undo/Redo/Delete/Copy/Paste/Duplicate/SelectAll/Fit/100%).
  Hotkeys via window PreviewKeyDown filter, skipped when a TextBox has focus. Save = single-writer background queue
  after each undo unit, flushed on image/project change/close; per-image undo stack.

## Behaviour
Select (click, Ctrl+click add, Tab cycle); box drag = TryMove/TryResize(min 2 px)/TryRotate (Shift = 15 deg); polygon vertex
drag, dbl-click edge inserts, Delete vertex (min 3); Rect press-drag-release (<2 px discarded); Rot 3 clicks normalised to
positive cross product; polygon draw Enter/dbl-click/first-point finishes, Backspace removes draft vertex, Esc cancels.
Holes: move/rotate rigid, resize/vertex edit keep, Ctrl+D/V translate. Hotkeys: Ctrl+Z/Y/C/V/D/A, Delete, V/R/O/P, F,
Ctrl+1, Esc. Up/Down change image (or nudge selected shape, see Q2). Middle-drag pan, wheel zoom x1.25 (0.05-40).

## Step 1 (commit B)
Import Dataset (Secondary, ImportEasyVisionDataset, result MessageBox), Add Images (Primary, multi-select, AddImage on
background task), summary card (Train/Val/Ignored/Stale/Missing JSON, click row selects images), list badges, Space = Train/Val
via SetMode (key repeat ignored, not an undo unit).

## Tests / evidence
Core.Tests: RotatedBoxGeometryTests, ShapeHitTesterTests, CanvasEditSessionTests, DocumentEditorBatchTests.
SegAI.Tests: SegAiEditSaveTests. UI smoke 100%/150% DPI, vi/en, PrintWindow screenshots (quoted args), fixture in
tmp/EAI/EAI-2/fixture, logs in artifacts/EAI/EAI-2/<run>/.

## Estimate ~1.0M tokens
A1 geometry 120k sonnet; A2 session 180k sonnet; A3 SegAI save 100k sonnet; A4 WPF canvas 200k sonnet; B1 VM 60k sonnet;
B2 XAML/resx 80k haiku; verifier 120k; reviewer 60k x2.

## Risks
Two writers (SegAiSession immediate vs canvas queue -> route via session lock, flush before Step 1 ops); 150% DPI needs a
machine setting; overlay redraw cost on 2-core; clone drift; custom canvas is a drawing surface (judged compatible with rule A).

## Open questions
Q1 SegAI shapes limited to NG/OK classes in EAI-2 (full class list EAI-3)? Q2 Up/Down: change image only when nothing is
selected, else nudge 1 px (Shift 10)? Q3 who sets 150% display scaling? Q4 opening a folder may write .easyai/classes.txt: ok?
