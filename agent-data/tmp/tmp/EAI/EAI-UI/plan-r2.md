# EAI-UI plan r2 (draft, awaiting user approval) - EasyAI = Labelme, item for item, plus EasyAI features

Replaces r1. User requirement (2026-10-08): identical to Labelme on the user's machine, not approximate, plus the EasyAI features. Reference = the 25 screenshots the user sent (menus, toolbar, tool strip, panels, Settings dialog, Label dialog). Commit A of r1 (224e06604, shell) is the base.
Defaults taken for the 3 open questions (user sent screenshots instead of answering): Q1 all drawing tools built for real (Circle, Point, Line, Linestrip); Q2 AI items shown exactly as Labelme shows them when no model exists (disabled, label "Không khả dụng"), enabled by real model detection, SAM2/YOLO-World integration is a later milestone; Q3 dockable/floating panels with AvalonDock (new NuGet = stop-and-ask rule 0.3: this plan's approval is the explicit approval for AvalonDock only).

## 1. Exact Labelme inventory to reproduce (from the screenshots)
**Menu bar** Tệp(F) | Chỉnh sửa(E) | Xem(V) | Trợ giúp(H) (+ one EasyAI menu, section 3).
- **Tệp:** Mở(O) Ctrl+O; Hình ảnh Tiếp theo(N) D; Hình ảnh Trước(P) A; Mở Thư mục Ctrl+U; Lưu(S) Ctrl+S; Lưu thành(S) Ctrl+Shift+S; Tự động lưu(A) checkable; Thay đổi Thư mục Đầu ra(C); Nhúng Hình ảnh vào Tệp nhãn checkable; Đóng(C) Ctrl+W; Xóa(D) Ctrl+Del; Cài đặt... Ctrl+Shift+,; Thoát(Q) Ctrl+Q. Icons as in Labelme (folder blue, arrows, floppy, red X ...).
- **Chỉnh sửa:** Đa giác Ctrl+N; Hình chữ nhật Ctrl+R; Hình chữ nhật có hướng; Hình tròn; Điểm; Đường thẳng; Đường gấp khúc; AI-Points; AI-Box; Chỉnh sửa Hình dạng Ctrl+J; Hoàn tác Ctrl+Z; Hoàn tác điểm cuối cùng Ctrl+Z; Sao chép vào Clipboard Ctrl+C; Dán từ Clipboard Ctrl+V; Nhân đôi Hình dạng Ctrl+D; Chỉnh sửa Nhãn(E) Ctrl+E; Xóa Hình dạng Del; Gộp Hình dạng; Xóa Đỉnh đã Chọn Meta+H; Chọn hình dạng phía trên/dưới/trái/phải Ctrl+arrows; Giữ Chú thích Trước đó Ctrl+P (checkable).
- **Xem:** checkable Cờ / Danh sách Nhãn / Danh sách Hình dạng / Danh sách Tệp; Đặt lại bố cục; Tô Đa giác Vẽ (toggle); Ẩn Hình dạng(H); Hiển thị Hình dạng(S); Bật/tắt Hình dạng T; Phóng to Ctrl++; Thu nhỏ Ctrl+-; Kích thước thật Ctrl+0; Giữ Mức Phóng to Trước đó (checkable); Vừa Cửa sổ Ctrl+F; Vừa Chiều rộng Ctrl+Shift+F; Độ sáng Độ tương phản(B); Giữ Độ sáng/Độ tương phản Trước đó (checkable).
- **Trợ giúp:** Hướng dẫn(T) (opens the real user guide of EasyAI: docs page/markdown shown in a window), About EasyAI.
**Toolbar (text under icon, groups separated):** Mở(O) | Mở Thư mục | Hình ảnh Trước(P) | Hình ảnh Tiếp theo(N) | Lưu(S) | Xóa(D) || Chỉnh sửa Hình dạng | Nhân đôi Hình dạng | Xóa Hình dạng | Hoàn tác | Độ sáng Độ tương phản(B) || Vừa Cửa sổ(W) | "Thu phóng" spin box in % || AI group: "Chú thích dữ liệu với sự hỗ trợ của AI" (model combo + shape-type combo + settings button) | "AI Văn bản sang Chú thích" (text box, Chạy, model combo, Điểm số, IoU).
**Left tool strip (vertical, icon + text):** Đa giác, Hình chữ nhật, Hình chữ nhật có hướng, Hình tròn, Điểm, Đường thẳng, Đường gấp khúc | AI-Points, AI-Box.
**Right panels (each with a title bar, float and close buttons, splitters):** Cờ (empty list with check items); Danh sách Nhãn (label + colour dot); Danh sách Hình dạng (checkbox + label + dot); Danh sách Tệp (search box "Tìm kiếm Tên tệp" + checkbox + path).
**Status bar:** "mode=EDIT | x=.. y=.." (cursor in image pixels) plus message line on the left.
**Canvas context menu:** the tool list + Chỉnh sửa Hình dạng, Hoàn tác, Hoàn tác điểm cuối cùng, Sao chép, Dán, Nhân đôi, Chỉnh sửa Nhãn(E), Xóa Hình dạng, Gộp Hình dạng, Thêm điểm vào cạnh, Xóa Đỉnh đã Chọn.
**Label dialog "Nhãn hình dạng":** label text box + "ID nhóm" box, OK/Cancel, label list below, description box ("Mô tả").
**Settings dialog "Cài đặt"** (search box left, 8 pages, footer "Mở tệp cấu hình dưới dạng văn bản..." and "Đóng"): Giao diện và ngôn ngữ (Chủ đề màu: Hệ thống/Sáng/Tối; Ngôn ngữ, applies after restart); Tệp và lưu (Tự động lưu; Nhúng hình ảnh vào tệp nhãn); Vẽ và canvas (Hiển thị cửa sổ nhãn khi tạo hình dạng mới; Chế độ màu hình dạng Tự động/Đồng nhất/Theo nhãn; Dịch bảng màu tự động; Màu đồng nhất; Màu dự phòng Theo nhãn; Tô đa giác khi vẽ; Cho phép điểm nằm ngoài ranh giới ảnh BETA; Hiển thị nhãn của hình dạng trên canvas BETA); Tiếp tục giữa các ảnh (Giữ chú thích / mức thu phóng / độ sáng-tương phản trước đó); Nguồn nhãn (Nhãn định sẵn, Cờ hình ảnh định sẵn, Cờ hình dạng định sẵn with regex rules + "Thêm quy tắc"); Hành vi nhãn (Kiểm tra nhãn, Sắp xếp nhãn, Hiển thị trường văn bản nhãn, Tự động hoàn thành nhãn Bắt đầu bằng/...); Hỗ trợ AI (Mô hình mặc định, AI Văn bản sang Chú thích model, Độ chi tiết đa giác slider 60, Chặn kết quả trùng với hình dạng hiện có); Mô hình AI (list/status of AI models).

## 2. Mapping to real behaviour (no dead control; each item states what makes it real)
- **Lưu / Lưu thành / Tự động lưu / Thay đổi Thư mục Đầu ra / Nhúng ảnh / Đóng / Xóa:** real. Annotation writing becomes Labelme-style: Auto-save ON (EasyAI default = today's behaviour, queue writes immediately); OFF = edits are held (dirty marker "*" in the title and Files list) until Lưu (writes LabelMe JSON + SegAI sidecar through the existing `SaveEditedShapes` path) or Lưu thành (choose a JSON path; output directory setting changes where LabelMe JSON is written); image switch with unsaved edits prompts like Labelme. Nhúng ảnh = `imageData` in the LabelMe JSON (Core already carries it). Đóng = close current image/project. Xóa = delete the image's annotation JSON files after confirmation (never the image itself; the Labelme "delete file" removes the label file) - recoverable via undo-less confirm text naming the files.
- **Flags:** `LabelMeDocument.Flags` (image flags) are real in the JSON: Cờ panel lists the predefined image flags from Settings (Nguồn nhãn) as check items per image; saved with the JSON; SegAI ignores them. Shape flags per shape stored in the shape's `flags`.
- **Tools Hình tròn / Điểm / Đường thẳng / Đường gấp khúc:** drawn, edited (move, vertex handles), saved as LabelMe `circle/point/line/linestrip`, copied/duplicated, listed in Shapes. SegAI training/inference only consumes rect/rotation/polygon; the other kinds are kept untouched on save (existing "kept in document, hash covers it" path) and are marked by a real indicator in the Shapes list tooltip.
- **Chỉnh sửa Nhãn / Label dialog:** edits label (any text), group id, description; the label list under the field comes from the project's labels (NG, OK + any used). Class colours: NG/OK keep the EasyAI colours; others follow the Settings colour mode (Tự động palette with shift, Đồng nhất, Theo nhãn). Non-NG/OK labels are stored and drawn but ignored by SegAI (indicator in Shapes list).
- **Gộp Hình dạng, Thêm điểm vào cạnh, Xóa Đỉnh đã Chọn (Meta+H), Hoàn tác điểm cuối cùng, Chọn hình dạng theo hướng, Giữ Chú thích Trước đó, Chỉnh sửa Hình dạng (Ctrl+J mode):** implemented in `EasyLabel.Core.Editing` (UI-free, unit tested) over the existing `CanvasEditSession`/`DocumentEditor` so each is one undo unit.
- **Ẩn/Hiển thị/Bật-tắt Hình dạng (T), Tô Đa giác Vẽ, Độ sáng Độ tương phản, Phóng to/Thu nhỏ/Vừa chiều rộng/Giữ mức phóng to/Giữ độ sáng:** view-state features of the canvas (never saved, not undoable); brightness/contrast applies to the displayed bitmap only.
- **Status bar mode/x/y:** real: mode = EDIT / CREATE(<tool>), x/y = pixel under the cursor.
- **AI group (toolbar, AI-Points, AI-Box, Settings > Hỗ trợ AI / Mô hình AI):** shown exactly like Labelme without models: controls present, disabled, text "Không khả dụng: <model>". Availability is detected for real (model files / runtime present); there is no fake enabled state. Integrating SAM2 / YOLO-World is a separate milestone needing the user to supply models (not in this plan).
- **Settings:** persisted in the existing settings store (`settings` folder of the app); every row changes a real behaviour above; rows whose behaviour is AI are disabled with the same reason; "Mở tệp cấu hình dưới dạng văn bản" opens the real settings JSON in the default editor; search box filters rows.
- **Language/theme:** Ngôn ngữ (vi/en, restart) and Chủ đề màu (System/Light/Dark) are real (resources and WPF-UI theme).
- **Hướng dẫn:** opens a real user guide (docs/easyai/guide.md rendered in a window); written in this milestone (short).

## 3. EasyAI features inside the Labelme layout
- **EasyAI menu** (between Xem and Trợ giúp): Nhập dataset, Thêm ảnh, Train/Val toggle (Space), Gán nhãn tự động (ảnh hiện tại / mọi ảnh chưa nhãn), Chấp nhận / Từ chối gợi ý, Bắt đầu / Dừng train, Danh sách model (opens the Train panel), Dùng model này.
- **Step bar** (Dữ liệu | Gán nhãn | Huấn luyện | later Kiểm tra, Thống kê) under the toolbar, as in r1 commit A.
- **EasyAI panel:** the existing Data / Label / Train step views live in one dockable panel "EasyAI" in the right column (above or below Cờ/Nhãn/Hình dạng/Tệp, user-arrangeable); the NG/OK class selector stays in the step bar and the Danh sách Nhãn panel shows NG/OK counts.
- **Files panel** keeps Labelme's checkbox (checked = image has a saved annotation, as in Labelme) and shows the EasyAI badges (Train/Val/Ignored/Stale) as plain text columns; search box filters.
- **Engines/status:** SegAI/MVS status in the status bar (already), details in About.

## 4. Docking engine
AvalonDock (Dirkster.AvalonDock.Themes.VS2013 or the maintained Dirkster fork, net9-windows compatible, to be confirmed on restore). Panels: Cờ, Danh sách Nhãn, Danh sách Hình dạng, Danh sách Tệp, EasyAI; each floatable/closable/redockable with title bar buttons like the screenshots; layout serialized in `layout.json` (+ existing window bounds); View-menu checkable items sync with panel visibility; Đặt lại bố cục restores the default arrangement. Theme matched to the WPF-UI light/dark theme. If AvalonDock cannot meet this, STOP and report (no hand-rolled substitute).

## 5. Commits (each: build 0 errors, unit tests, real-app UIA smoke vi+en, re-run of the EAI-3/EAI-4 smoke flows unchanged, screenshot comparison with the user's Labelme screenshots)
- **B:** Menus/toolbar/status bar exact (sections 1-2 UI parts), Settings store, Save/Lưu thành/Tự động lưu/dirty tracking/output dir/embed/close/delete-annotation, Settings dialog shell + pages (non-AI rows real), Hướng dẫn, theme/language.
- **C:** Core editing features + new shape kinds (circle, point, line, linestrip), Label dialog, Merge/Add point/Delete vertex/Select by direction/Keep previous annotation/Ctrl+J edit mode, Flags data model, zoom/brightness/fill/hide-show view features, context menu, tool strip final.
- **D:** AvalonDock panels (Cờ, Nhãn, Hình dạng, Tệp, EasyAI), Files/Shapes/Labels/Flags content, View-menu sync, layout persistence/reset, compact mode, EasyAI menu.
- **E:** AI items (availability detection, disabled states, Settings AI pages), Hướng dẫn guide content, final polish + full smoke matrix.

## 6. Out of scope
SAM2 / YOLO-World / any AI inference integration (needs models; separate milestone), EAI-5..8, any EasyVision file, EasyLabel.App WinForms, changing SegAI training behaviour.

## 7. Risks
- Size: Labelme parity is large (estimate 3.0M tokens, ~4 commits after A). Work is sliced; progress recorded in tmp/EAI/EAI-UI/deferred.md if the 5 h budget requires a pause.
- AvalonDock + WPF-UI theming (light/dark, Mica) may need custom templates; fallback is to report, not to fake.
- Save semantics change (Auto-save OFF holds edits): data-loss risk on crash; mitigated by prompt on switch/close and by Auto-save ON default.
- New shape kinds touch the shared edit session and hit-testing (Core tests required for each).
- Labelme's exact pixels/fonts (Qt) cannot be bit-identical in WPF; parity means the same items, order, icons, shortcuts, texts, panels and behaviour, checked by side-by-side screenshots with the user.
- 150% DPI and long strings checked only if the user sets scaling.

## 8. Open questions (defaults if unanswered)
Q1 Delete (Xóa D, Ctrl+Del) deletes the annotation files of the current image only (default) vs also the image file.
Q2 Settings storage: app settings folder (default) vs per project.
Q3 Non-NG/OK labels allowed in the Label dialog (default, ignored by SegAI with an indicator) vs restrict to NG/OK.
