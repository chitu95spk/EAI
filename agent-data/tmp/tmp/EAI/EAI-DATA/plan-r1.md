# EAI-DATA plan r1 (draft, awaiting user approval) - one `.easyai` folder, Labelme-compatible JSON, multi-format Export

User request (2026-10-08): merge the two metadata folders `.easyai` and `.easylabel` into ONE `.easyai` folder; keep the JSON format like Labelme; add Export to different formats. User answered "mặc định" to the five questions, so the recommended options below are taken.
Facts: EasyLabel Core writes `.easylabel/` (project.json, recovery/<image>.json) via ProjectService and AutoSaveService, plus a per-image `<name>.easylabel.json` sidecar beside the image (EasyLabel UI metadata); Segment AI writes `.easyai/` (project.json, seg/<image>.seg.json + .labels.png, models/registry.json + vNNN.segai, runs/<utc>/status.json). LabelMe JSON `<name>.json` sits beside each image. Core already has exporters: YoloAnnotationConverter, PascalVocConverter, CocoExporter, UltralyticsExportService, StandaloneConvertService, package ZIP (Package/*).

## 1. Target layout (single folder)
```
<project>/
  image1.png
  image1.json              <- Labelme-standard JSON, beside the image (Labelme opens it unchanged)
  .easyai/
    project.json           <- merged project file (schema 2: classes, active model, options; old .easylabel/project.json fields included)
    classes.txt            <- unchanged location rule (project root classes.txt stays if present; mirrored into project.json)
    meta/<rel>.json        <- former <name>.easylabel.json sidecars (UI metadata, provenance), one folder instead of files beside images
    recovery/<rel>.json    <- former .easylabel/recovery
    cache/<rel>.seg.json, cache/<rel>.labels.png   <- Segment AI derived data (hash, regions, raster): deletable, regenerated from the LabelMe JSON
    models/registry.json, models/vNNN.segai
    runs/<utc>/status.json ...
```
No `.easylabel` folder and no `<name>.easylabel.json` beside images any more.

## 2. JSON rules (Labelme-compatible)
- Annotations (labels, shapes, flags) live ONLY in the standard LabelMe JSON beside the image. Segment AI Train/Val mode is stored as the image-level boolean flags `train` / `val` in that JSON's `flags` (both false = auto, derived Ignored when no regions). Labelme preserves unknown keys, so opening/saving in Labelme does not lose them.
- Segment AI holes and rasters are cache: when `cache/` is missing the regions are re-derived from the JSON polygons (holes of old data are lost unless the optional extension below is present). Optional extension: shapes may carry `"easyai": {"holes": [[[x,y],...]]}` so holes persist in the JSON itself (Labelme keeps unknown shape keys); the writer adds it only for shapes that have holes.
- imageData embedding stays off by default (EAI-UI setting).

## 3. Migration (automatic, non-destructive)
On project open, if `.easylabel/` or the old `.easyai/seg/` layout exists: copy/move into the new layout (meta/, recovery/, cache/), merge both project.json files into `.easyai/project.json` (schema 2, keeping unknown fields), convert the old per-image mode (seg sidecar Mode) into the `train`/`val` flags of the LabelMe JSON, move `<name>.easylabel.json` into `.easyai/meta/`, then rename the old folder to `.easylabel.migrated-<utc>` (never deleted) and log a one-line report (counts, errors) shown once as a notice. Idempotent and resumable (a half-finished migration re-runs safely). Writes use the existing atomic-write helper; the project is never modified when a step fails (the notice explains which file blocked it).
Backward compatibility: opening a migrated project with an old EasyAI build is not supported (documented in the migration notice); EasyLabel.App (WinForms) reads the new layout through Core.

## 4. Export (File > Export, one dialog)
Formats (reusing Core exporters; each real and tested): LabelMe JSON (copy), YOLO detect, YOLO segment (Ultralytics export service with train/val split), COCO instances, Pascal VOC, Mask PNG (class-index masks), EasyVision dataset (the folder/zip ToolSegmentAI imports), CSV (image, mode, label counts, regions). Options: set (All / Train / Val), output folder, copy images or write a ZIP, include only annotated images. Shapes that a format cannot represent (circle/point/line/linestrip for box/mask formats) are skipped and reported in a result summary; nothing is silently dropped.

## 5. Commits
- **A (Core + SegAI, tested):** `ProjectLayout` (single place for all paths), migration service, new mode-in-flags read/write, cache relocation, project.json merge, optional holes extension; AutoSaveService/ProjectService/Batch services updated to the new paths; tests with fixtures of the old layout (round trip, idempotence, failure safety).
- **B (EasyAI.Wpf + export):** open-project migration notice, Export dialog + commands, menu entries, strings en/vi; UIA smoke (migrate an old fixture project, edit, Save, re-open, Export each format and compare file contents with the existing converters' golden outputs).
Evidence: build 0 errors; Core/SegAI tests; migration of the real fixture projects (EAI-0 fixture, EAI-4 fixture) verified by file listing + re-open; exported files validated (YOLO txt line counts, COCO json schema, VOC xml parse, mask PNG dims).

## 6. Out of scope
EAI-5 Inspect, camera, SAM2, changing training behaviour, deleting old data.

## 7. Risks
- Touches every read/write path (ProjectService, AutoSaveService, SegAiSession store, Batch services, Package/Convert): mitigated by the single `ProjectLayout` class and tests on old-layout fixtures.
- Data loss risk on migration: non-destructive (old folder renamed, never deleted), atomic writes, idempotent.
- Holes persistence (extension key) is optional; without it holes live only in cache.
- Mode stored in LabelMe flags changes the JSON of existing projects (keys `train`/`val` appear); Labelme tolerates them.
- EasyLabel.App compatibility (shares Core) must be re-checked by its tests.
Estimate: about 1.6M tokens.

## 8. Open questions (defaults taken)
Q1 Single `.easyai`, LabelMe JSON beside images (taken). Q2 Mode in LabelMe flags `train`/`val`; derived data in `cache/`; optional holes extension (taken). Q3 Auto-migrate with `.easylabel.migrated-<utc>` backup (taken). Q4 Export formats as listed in section 4 (taken). Q5 Order: after commit F, before commit G (taken).
