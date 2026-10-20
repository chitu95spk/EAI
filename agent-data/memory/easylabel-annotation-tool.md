---
name: easylabel-annotation-tool
description: "Planned EasyLabel — standalone C# annotation + auto-train tool (LabelMe-like) for EasyVision"
metadata: 
  node_type: memory
  type: project
  originSessionId: a9f1ac00-9c34-4db7-99a9-887871ce6199
  modified: 2026-08-29T09:25:05.980Z
---

New sub-project "EasyLabel": a standalone LabelMe-style annotation + auto-labeling + on-machine training tool feeding the EasyVision YOLO pipeline. Spec = `EASYLABEL_SPEC.md` at repo root (Codex executes it, Claude supervises/reviews). State 2026-09-07: built through `[EL-4.3]` (M1.5, M2 detect, M3 batch, M4.1-4.3 converters/export), tests 26/26 green. Codex skipped EL-0.4/EL-1.4/EL-1.5 (no undo, autosave, hotkeys, clipboard, thumbnails) and BE-U1/BE-U3 -> supervisor added milestone **M4.5 Debt payback** as a hard gate before M5. P2/P3 (offline wheelhouse + runtime/python + sample model) are still empty, so EL-2.1/2.2/2.3 compile but have never run.

Locked decisions (from user):
- **Platform**: standalone C# app, separate solution (`EasyLabel.sln`, .NET 8 WinForms) — NOT a module inside EasyVision_Unisen; distributable to label-only users.
- **Canonical format**: LabelMe JSON is source-of-truth; everything else (YOLO txt/OBB/seg, COCO, VOC) is export.
- **Training**: Ultralytics via Python.NET, GPU on dev machine (CUDA); detect via Python.NET in-proc, train via `yolo train` subprocess. Auto-export ONNX + OpenVINO IR.
- **Python env = fully offline, bundled in program folder** (user requirement, no online pip): embedded CPython in `runtime/python/`, wheelhouse of `.whl` in `Lib/`, pinned `requirements.lock`. `EnvironmentBootstrapper` at startup scans installed vs lock, installs only missing/mismatched via `pip install --no-index --find-links Lib`. Python.NET points at `runtime/python` (never system Python). Wheelhouse must match target CUDA; label-only build omits the heavy GPU wheels.
- **MVP** = M0 skeleton, M1 manual annotation (all shapes), M2 auto-detect, M3 batch remap/clone/augment, M4 convert+package. M5 training next; M6 QA / M7 SAM+active-learning+production-feedback are v2.

Integration contract with EasyVision: export OpenVINO IR in the exact `best.xml`/`best.bin` + class-map layout that [[e804-workcounterloss-camera-starvation]]'s runtime peers read — `OpenVinoTrackingDetector` loads `best.xml` + `TrackingModelMetadata.LoadClassNames`; ToolYolo consumes it. Reuse `RectRotate` OBB math (copy formulas, don't reference BeeCore which is .NET Framework).

Roadmap artifact: https://claude.ai/code/artifact/77fbf96d-881d-4d5a-a4f1-2e7e1635109b
