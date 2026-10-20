# EAI-3 plan r1 (draft, awaiting user approval) - Step 2 Label: auto-label + import model

Master plan: tmp/EAI/plan.md r4 section 6 (Step 2) and 8. Depends on EAI-2 (canvas, Data step; last commits 245ef9bd9, b47f995f1).
Facts (explorer, 2026-10-08): SegAiSession has Predict/PredictFile (SegAiResult: Mask, NormalMask, Regions, NormalRegions, RegionScores,
NormalRegionScores, Score), LoadModel(path), Models (registry.json), ActiveModel, SetActiveModel, Inference.DefectThreshold. NOT present:
prediction -> LabelShape conversion, "import model into registry", pending-suggestion layer on the canvas, class editing/persistence.
Only Data is registered in MainViewModel.AvailableSteps; DocumentEditor.Add(shapes, provenance, name) = one undo unit.

## 1. Scope (two commits)
### Commit A - backend (UI-free, tested)
1. `EasyLabel.SegAI/AutoLabel`: `SegAiSuggestionBuilder` converts a SegAiResult into suggestions (kind Polygon, label NG/OK, score, holes) by tracing
   mask contours at full image resolution (OpenCvSharp FindContours, RETR_CCOMP so holes survive, area filter = Inference.MinDefectArea).
   A suggestion is a plain record {Rel, ClassIndex, Outer, Holes[], Score?}; no LabelMe types leak into the native layer.
2. `SegAiSession.ImportModel(path)`: validates through LoadModel, copies into `.easyai/models/` as the next vNNN.segai, appends a registry entry
   (source = "imported", never overwrites), does NOT activate. Existing `SetActiveModel` activates.
3. `AutoLabelService` (SegAI): run one image or a list of images (background, IProgress, CancellationToken), keeps results in a cache keyed by
   (rel, model file, image hash); the threshold only filters cached suggestions (no re-inference). Inference runs at a low floor threshold (0.05) so the
   slider can go both ways; if the model returns no RegionScores the threshold control is not offered.
4. Accept writes through the existing hole-preserving `SaveEditedShapes` for any image (current or not).
5. Tests (EasyLabel.SegAI.Tests): contour/hole/area-filter unit tests on synthetic masks; ImportModel registry tests; native predict test with
   artifacts/V133 models behind NativeSegAiFact.

### Commit B - WPF Step 2 panel + canvas suggestion layer
1. Register `WorkflowStep.Label` in AvailableSteps; `LabelStepView` + `LabelStepViewModel` hosted like DataStepView (StepPanel DataTrigger).
2. Panel content (every control backed by real code, no help text, WPF-UI controls only):
   - Class list: NG / OK rows (colour swatch, shape count on the current image, click = active class; hotkeys 1/2). Renaming only if a real
     persistence path is added in commit A (see open question Q2); otherwise names are read-only.
   - Model: active model name + "Choose model" (registry list, SetActiveModel) + "Import model..." (.segai file dialog -> ImportModel).
   - Auto-label: "Current image", "All unlabelled" (progress + Cancel), score threshold slider (only when scores exist).
   - Suggestions: "Accept all" (current image, one undo unit), "Accept selected", "Reject all"; Primary button = Accept all (one Primary per panel).
   - Train / Val segmented control for the current image (same command as Space). No Ignore button: Ignored is derived from "no regions" and
     cannot be stored (decision Q3).
3. Canvas: a read-only suggestion layer drawn dashed in the class colour with its score; not part of Document, not in the undo stack, not saved.
   Click selects a suggestion, Enter/Accept promotes it (DocumentEditor.Add, one undo unit), Delete rejects. Switching image clears the visible layer
   but the cache keeps results until the model changes or the image file changes.
4. Stepper shows Step 2 once the project is open; vi/en strings added to Strings.resx (+ vi).

## 2. Out of scope
Class add/remove (SegAI is NG/OK), train (EAI-4), inspection list (EAI-5), camera (EAI-6), statistics (EAI-7), advanced drawer (EAI-8),
any EasyVision file, EasyLabel.App WinForms.

## 3. Verification
Build 0 errors; existing tests pass (Core 86, SegAI 106) + new tests. Parity ("so JSON"): for 3+ images and model(s) in artifacts/V133, the polygons
produced by auto-label, rasterised back, must reach IoU >= 0.99 with the native NG mask (same threshold), and region count equal to
SegAiResult.Regions after the area filter; saved .seg.json + LabelMe JSON compared. UI smoke (smoke3.ps1, vi + en, 100% DPI): switch to Step 2, import model,
auto-label current image, move threshold, accept all, undo (one unit), Space toggle, all-unlabelled run with cancel. 150% DPI stays owed unless the user sets scaling.

## 4. Risks
- Contours from a mask can be heavy (thousands of points): simplify with ApproxPolyDP epsilon <= 0.5 px, and cap points per shape.
- Predict is native and not thread-safe per session: all inference goes through the session lock, background task, canvas input blocked as in RunStep1Async.
- Large "All unlabelled" runs: progress + cancel; results kept in memory only (bounded, LRU 200 images).
- Whether SegAiResult exposes enough to rebuild holes exactly is verified in the first slice of commit A; if not, the plan is revised, not improvised.

## 5. Open questions (defaults taken if unanswered)
Q1 Pending suggestions: dashed overlay layer, accepted explicitly (default) vs written straight into the document as editable shapes.
Q2 Class list: NG/OK read-only names (default) vs rename persisted to classes.txt.
Q3 Train/Val/Ignore: Train/Val only, Ignored derived (default) vs an "Ignore" action that clears the image's regions.
Q4 Import model: copy into `.easyai/models` as a new registry candidate, not activated (default) vs also activate immediately.
Q5 "All unlabelled": include in EAI-3 (default) vs current image only.

Estimate: about 1.0M tokens for the route (explorer, architect pass, A, B, review, smoke).
