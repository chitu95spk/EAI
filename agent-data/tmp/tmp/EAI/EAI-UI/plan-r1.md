# EAI-UI plan r1 (draft, awaiting user approval) - Labelme-style main window for EasyAI

Master plan: tmp/EAI/plan.md r4. Runs BEFORE EAI-5. Depends on EAI-4 (1a1109299). User answers (2026-10-08): Q1 step selector under the toolbar + right panel swaps per step (a); Q2 fixed panels, resizable, toggled from the View menu, Reset layout (a, no docking library); Q3 only the 4 real tools in the left strip (a); Q4 mirror Labelme panels; Q5 new contract EAI-UI.

Facts (explorer, 2026-10-08): MainWindow.xaml has 4 rows (title bar, notice, content, status bar); content = Left (ImageListPane, 240) | splitter | Center (CanvasView with a WrapPanel toolbar) | splitter | Right (StepPanel, 300: reset button, stepper, Data/Label/Train views, Engines card). No Menu/ContextMenu exists. Layout persistence stores window bounds + LeftWidth/RightWidth only. Hotkeys live in CanvasView.HandleKey and MainWindow.OnPreviewKeyDown. No per-shape visibility flag exists. The smoke harnesses (smoke3.ps1, smoke4.ps1) depend on AutomationIds that must stay.

## 1. Target layout (Labelme as the reference, Vietnamese labels like the screenshots)
```
Menu bar:  Tệp | Chỉnh sửa | Xem | Trợ giúp
Toolbar:   Mở | Mở thư mục | Ảnh trước | Ảnh tiếp | Lưu* | Xóa | | Sửa hình | Nhân đôi | Xóa hình | Hoàn tác | Làm lại | | Vừa cửa sổ | Kích thước thật | Thu phóng %
Step bar:  ( Dữ liệu ) ( Gán nhãn ) ( Train )   <- segmented, only steps that exist; class NG/OK selector sits here
Left strip (vertical, icon + text): Chọn | Chữ nhật | Chữ nhật có hướng | Đa giác
Centre:    canvas (start screen = two buttons Mở ảnh / Mở thư mục, no instruction text)
Right column (stack, each panel has a title bar and a close/hide button, resizable with splitters):
   Step panel (the existing Data / Label / Train views, content unchanged)
   Danh sách Nhãn   (NG, OK + colour + shape count on the current image; click = active class)
   Danh sách Hình dạng (current image shapes, visibility checkbox, click selects, Del)
   Danh sách Tệp    (search box + list; checkbox = image has annotations; badges Train/Val/Ignored/Stale kept)
Status bar: unchanged (+ file count)
```
* = Lưu only if a real save action exists (annotations auto-save today): the toolbar does NOT get a Save button (rule B: no dead control). "Lưu" menu item likewise omitted.

## 2. Mapping to real code (nothing decorative)
- **Tệp menu:** Mở ảnh (Add images), Mở thư mục (Open folder), Ảnh trước/tiếp (existing Previous/Next image commands), Nhập dataset (Import dataset), Thoát. Hotkeys Ctrl+O, Ctrl+U, A/D (only when no text focus and no canvas conflict - A/D checked against existing keys), Ctrl+Q. No Save/Save as/Auto-save/Close/Delete-file (not real in EasyAI); Delete image file is excluded on purpose (destructive, no undo).
- **Chỉnh sửa menu:** the four tools (V/R/O/P keep their keys; Labelme's Ctrl+N/Ctrl+R labels are shown only if actually bound), Undo, Redo, Copy, Paste, Duplicate, Delete, Select all, Select next/previous shape (Tab/Shift+Tab already exist), class NG/OK (1/2). Items that have no backing command are not shown.
- **Xem menu:** checkable items for each panel (Step panel, Labels, Shapes, Files), Reset layout, Fit window, Actual size, Zoom in/out only if a real zoom command exists (else omitted), show/hide all shapes (new, see below).
- **Trợ giúp menu:** About only (version + engine status text from the existing Engines block). The Engines card moves into this About dialog and the status bar (it already shows SegAI/MVS status).
- **Right-click on canvas:** the Labelme context menu mapped to real commands (tools, Undo, Copy/Paste/Duplicate, Delete, Edit label = class toggle NG/OK of the selected shape - new real command, one undo unit).
- **Shapes list:** new `ShapesPaneViewModel` over `CanvasViewModel.Document.Shapes` (refresh on RenderRequested/DocumentReady/undo), selection two-way with `Session.SelectedIndices`; per-shape visibility is a **view-only** flag kept in the canvas view model (never saved, never in undo; hidden shapes are not drawn and not hit-tested); a "show all / hide all" action.
- **Labels list:** the existing class rows (NG/OK, count) moved out of the Label step view into this panel (the Label step keeps model/auto-label/suggestions/split sections). Rename/add class stays out of scope (NG/OK only).
- **Files list:** the current ImageListPane gains the search box (filter by name, real) and the annotated checkbox (read-only indicator: bound to `HasRegions`; it is not a user-editable selection) and keeps the Train/Val/Ignored/Stale badges as plain TextBlocks (Badge renders blank in this app).
- **Not included (no backing feature, rule B):** Flags panel, Save/Save as/Auto-save, Embed image data, Settings dialog, AI-Points/AI-Box/Circle/Point/Line/Linestrip tools, brightness/contrast, Keep previous annotation, merge shapes, add point to edge, delete selected vertex menu entry (Alt-click vertex delete stays a canvas gesture and is shown only in the context menu if the command is real).

## 3. Layout engine
- Grid with GridSplitters (existing mechanism); right column becomes a vertical stack of panels (each in a `Border` with a header + hide button, row heights persisted).
- `LayoutSettings` gains: per-panel Visible flags, right-column row heights, step-panel height; `CreateDefault()` restores them; old settings files load with defaults for the new fields.
- Compact mode (<CompactThreshold): panels collapse into the existing tab mechanism (Images / Step) extended with Labels / Shapes tabs.
- All hosted views keep their DataContext and AutomationIds; moved controls keep their ids (ClassList, ImageList, Stepper, ResetLayoutButton...). The smoke scripts are updated only where a control had to move to another container.

## 4. Commits
- **A - shell:** MenuBar + toolbar + step bar + left tool strip + start screen + About dialog; menus bound to existing commands; strings vi/en; layout persistence fields; compact tabs. Panels still the old ones.
- **B - right panels:** Labels / Shapes / Files panels (new ShapesPane + view-only visibility, class rows moved, search + annotated indicator), Label-view cleanup, context menu, Edit-label command, View-menu toggles.
- Each commit: build 0 errors, tests (Core 86, SegAI 187+), UI smoke en+vi at 100% DPI with the existing smoke3/smoke4 flows re-run unchanged (proves AutomationId compatibility) plus a new layout smoke (menus open, panel toggle, reset layout, compact width, shape visibility).

## 5. Out of scope
Docking/floating panels (needs AvalonDock = new NuGet, not approved), new drawing tools, flags, save model changes, EAI-5 inspection, any EasyVision file.

## 6. Risks
- Large XAML move touching MainWindow, StepPanel, LabelStepView, ImageListPane, CanvasView, resources: mitigated by two commits and by re-running the existing smoke flows.
- Hotkey conflicts (A/D, Ctrl+N/R vs existing V/R/O/P, Tab): each shortcut shown in a menu must be bound and tested; none is advertised unless it works.
- WPF-UI has `ui:Menu`/`ContextMenu` styling but menu items with icons need `ui:MenuItem` + SymbolIcon; if a WPF-UI control cannot do something (e.g. checkable menu item with icon) stop and report, no stock substitute.
- Hidden-shape state vs undo/redo and image switching: visibility resets on image change (documented), never saved.
- 150% DPI and long vi strings: layout verified at 100% only unless the user sets scaling.

## 7. Open questions (defaults if unanswered)
Q1 Files checkbox = read-only "has annotations" indicator (default) vs a user-checkable multi-select for batch actions (needs a real batch action first).
Q2 Flags panel omitted (default) vs implemented over LabelMe `flags` (real in the JSON but unused by SegAI).
Q3 Engines card: moved to About + status bar (default) vs kept at the bottom of the step panel.
Q4 Edit label (right-click) = toggle NG/OK of selected shapes (default).
Estimate: about 0.9M tokens (explorer done, two implementer slices, reviews, smoke).
