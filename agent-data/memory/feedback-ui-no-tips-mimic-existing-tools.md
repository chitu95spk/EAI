---
name: feedback-ui-no-tips-mimic-existing-tools
description: "User wants EasyVision tool UIs without tip/help text blocks, built like existing tools (Segment AI menus/hotkeys, EditRot handles), one shared tuning dataset"
metadata:
  node_type: memory
  type: feedback
  originSessionId: df663410-d652-4c0c-abdc-df79b81f1bd3
  modified: 2026-10-04T11:09:09.020Z
---

On the CraftOCR Tuning/Advanced work (2026-10-04) the user repeatedly corrected: remove explanatory "tip" text blocks (Tuning help label, Advanced group hints); controls at the bottom with the primary action (Start Tuning) last; the image list fills from the top and scrolls; box editing must look and behave like EditRot (round handles, rotation knob); labelling uses the Segment AI right-click edit menu and hotkeys (Ctrl+C/V/D/A/Z, Delete, Space, Up/Down); only the NG box is NG coloured (never a full line), severity shown as a colour ramp per fault kind (Faded yellow-red, Heavy cyan-dark blue, Blur light-dark green); the tuning dataset is ONE shared set for the whole app, not per project.

**Why:** the user works on a narrow side panel on a production PC; text blocks eat the little space and duplicated look-and-feel costs them relearning.
**How to apply:** before designing a new editor/tab, read the closest existing tool (Segment AI canvas/menu, `Draws.DrawRectRotate`) and reuse its helpers; no help paragraphs, use titles only; render the real panel width (470-600 px, after ToolParameterAccordionBuilder + ToolEditorUiRules) and look at the PNG. Related: [[ocr-tuning-model-decisions]], [[segai-trainval-labeling-decisions]].
