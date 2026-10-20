# EasyAI — Plan r4 (đã chốt quyết định; chưa sửa code)

r1 2026-10-05: clone Segment AI thành app riêng net48. r2: đổi stack WPF/.NET 10.
**r3 (quyết định user):** xây trên EasyLabel; chỉ MVS; .NET 9; dùng thống nhất WPF-UI; không UI mô phỏng,
làm hoàn thiện ngay từ đầu; 2 luật đó áp cho nút, nền, sự kiện nhấn/nhả, mode, split (mục 5).

## 1. Mục tiêu
Front-end WPF mới tên **EasyAI**, nằm trong solution EasyLabel, tái dùng EasyLabel Core/Vision và thêm
engine Segment AI native của EasyVision. Luồng 5 bước: Dữ liệu → Dán nhãn → Train/Retrain → Kiểm tra → Thống kê.
Model `.segai` dùng qua lại 2 chiều với ToolSegmentAI của EasyVision.

## 2. Tái dùng gì
### Từ EasyLabel (`EasyLabel/src`, ~11.7k dòng, net8, đo 2026-10-05)
| Project | Giữ | Ghi chú |
|---|---|---|
| EasyLabel.Core | LabelMe JSON (canonical), `AnnotationDocument`, `DocumentEditor`/CommandStack (undo/redo), `ProjectService`, import LabelMe, YOLO/COCO/VOC converter, package ZIP, augment, class remap, bulk edit, `AtomicFile` | dùng nguyên; thêm converter SegAI ⇄ LabelMe |
| EasyLabel.Vision | `ImageLoader`, augment writer OpenCV | dùng nguyên |
| EasyLabel.Ai | bridge Python YOLO (runtime offline chưa có) | giữ, không đưa lên UI EasyAI cho tới khi runtime P2/P3 chạy được (luật không mô phỏng) |
| EasyLabel.App (WinForms) | không sửa, không xoá | EasyAI.Wpf thay thế dần; chỉ bỏ khi EasyAI bao đủ chức năng |
| `COMPLETION_PLAN.md` §4 workspace, §5 bảng chuột/phím, AppCommandRegistry | dùng làm spec tương tác | gộp với hotkey Segment AI (mục 5) |

### Từ EasyVision
| Nguồn | Cách dùng |
|---|---|
| `BeeNativeSegAI.dll` | dùng chung bản build, không fork; kiểm version khi khởi động |
| `BeeCore/Func/NativeSegAI.cs` (574) | clone sang EasyLabel.SegAI |
| `BeeCore/Unit/SegmentAI*.cs` (~5.2k) | clone phần dataset/options/train/inspection/evaluation/recommendation; cắt `Common`/`Global`/`PropetyTool` → `SegAiSession` (instance) |
| `ToolSegmentAI*.cs`, `FormSegmentTraining*.cs` | chỉ lấy luồng nghiệp vụ và hotkey; UI làm lại bằng WPF-UI |
| `ShapeEditing/*`, `RectRotate`, `GeometryHelper` | clone toán học handle/xoay/clamp biên (V176) vào canvas WPF |
| `BeeCore/Func/Camera.cs` nhánh MVS | tham khảo trình tự mở/grab/param |
Mỗi file clone có header `// Cloned from <path>@<commit>`.

## 3. Kiến trúc (net9.0 / net9.0-windows, x64)
```
EasyLabel/EasyLabel.sln
  src/EasyLabel.Core      (retarget net8 → net9)
  src/EasyLabel.Vision    (retarget net9)
  src/EasyLabel.Ai        (retarget net9, chưa lên UI)
  src/EasyLabel.App       (WinForms, giữ nguyên)
  src/EasyLabel.SegAI     MỚI — P/Invoke native, SegAiSession, train/infer/evaluate, registry model, converter
  src/EasyLabel.Camera    MỚI — ICameraSource + MvsCamera
  src/EasyAI.Wpf          MỚI — WPF-UI FluentWindow, MVVM (CommunityToolkit.Mvvm), canvas, 5 bước
  tests/EasyLabel.Core.Tests (+ SegAI/Camera tests)
```
- Thư viện mới (cần duyệt theo luật 0.3): WPF-UI 4.x, CommunityToolkit.Mvvm, ScottPlot.WPF 5.
- .NET 9: máy có SDK 9.0.304. **Cảnh báo:** .NET 9 (và .NET 8) hết hỗ trợ 10/11/2026, còn ~5 tuần.
  Code giữ tương thích để lên net10 chỉ bằng đổi `TargetFramework`.
- Train/infer chạy Task nền, UI nhận `IProgress<T>`; huỷ bằng `CancellationToken`; không chặn UI thread.

## 4. Project & dataset
Project = thư mục EasyLabel hiện có (ảnh + LabelMe JSON + sidecar) + thư mục `.easyai/`:
`project.json` (class, bước, model active, camera), `advanced.json` (SegTrainingOptions, label rules,
angle, multi-region), `models/vNNN.segai` + `registry.json`, `runs/<time>/` (results.csv, summary.json, ảnh NG).
Import: folder ảnh, LabelMe, dataset ToolSegmentAI của EasyVision. Export: `.segai` (nạp vào ToolSegmentAI
bằng Import Model sẵn có), YOLO/COCO/VOC (converter EasyLabel), CSV thống kê.
Mask: polygon/rect/rot ⇄ SegAnnotation; mask raster có lỗ/rời rạc là quyết định P5 còn mở của EasyLabel → chốt ở EAI-1.

## 5. Luật UI (bắt buộc, áp cho mọi màn hình)
Hai luật gốc: **(A) chỉ dùng control WPF-UI, thống nhất toàn app; (B) không mô phỏng — control nào hiện ra
phải nối backend thật và chạy được; tính năng chưa xong thì không hiện (không placeholder, không "coming soon").**
| Hạng mục | Quy tắc |
|---|---|
| Nút | `ui:Button` (Appearance Primary/Secondary/Danger/Transparent) + icon `SymbolRegular`; mỗi panel đúng 1 nút Primary (hành động chính, nằm cuối); nút icon-only có tooltip tên + phím tắt; không tự vẽ nút, không style riêng |
| Nền | `ui:FluentWindow` + backdrop Mica, theme Sáng/Tối theo hệ thống; chỉ dùng brush theme của WPF-UI (`ApplicationBackgroundBrush`, `CardBackground...`); hex cứng chỉ cho màu class và token OK/NG khai báo 1 chỗ |
| Nhấn/nhả | Hành động nút = `ICommand` (kích hoạt khi nhả trong nút, có CanExecute); không gắn nghiệp vụ vào MouseDown của nút. Thao tác giữ (giữ để xem ảnh gốc không overlay, nút Live) dùng PreviewMouseDown/Up + CaptureMouse, nhả hoặc `LostMouseCapture` luôn kết thúc → không bao giờ kẹt. Canvas: nhấn bắt đầu kéo, nhả commit 1 undo unit, Esc huỷ, chuột phải mở menu không commit |
| Mode | Mỗi mode là 1 enum trong ViewModel: bước (1–5), công cụ (Select/Rect/Rot/Polygon/Brush), nguồn (Ảnh/CCD), Train/Retrain, xem (Label/Predict/Cả hai). Hiển thị bằng nhóm toggle WPF-UI (segmented); phím tắt và nút cùng đổi 1 enum; mode hiện tại luôn nhìn thấy |
| Split | `Grid` + `GridSplitter`: Danh sách ảnh \| Canvas \| Panel bước; MinWidth mỗi cột; màn hẹp thì panel trái thu thành tab; độ rộng lưu theo user, có "Reset layout"; panel Job/progress dưới cùng thu gọn được |
Phím tắt: lấy bảng §5 COMPLETION_PLAN + hotkey Segment AI (Ctrl+C/V/D/A/Z, Delete, ↑/↓ đổi ảnh, Space đổi Train/Val).
Xung đột: EasyLabel Space+kéo = pan ↔ Segment AI Space = Train/Val → đề xuất Space = Train/Val, pan = chuột giữa.
Không đoạn chữ hướng dẫn trong panel, chỉ tiêu đề + tooltip. vi/en qua resource.

## 6. Năm bước
| Bước | Panel phải | Backend |
|---|---|---|
| 1 Dữ liệu | Import Dataset · Add Images · Read CCD (Live/Snap/Auto-save) · Kiểm tra dữ liệu | ProjectService, LabelMe import, MvsCamera, eseg-data checks |
| 2 Dán nhãn | Class list · công cụ · Auto-label (model active hoặc Import model) · ngưỡng · Accept all · Train/Val/Ignore | DocumentEditor, SuggestAnnotation, NativeSegAIInferer |
| 3 Train | Train mới / Retrain · split · epochs · Start/Stop · biểu đồ loss/mAP live · bảng so candidate vs active · "Dùng model này" | NativeSegAITrainer, SegTrainingOptions, TrainingRecommendation |
| 4 Kiểm tra | Nguồn Ảnh/CCD · Run / Run All / Live · OK/NG, ms, overlay | Inspection, LabelRules |
| 5 Thống kê | OK/NG, theo class, phân bố diện tích/score, ms; so nhãn → precision/recall/mAP; so 2 version; Export CSV | TrainingEvaluation, SegTrainingReport |
Nâng cao: 1 nút ⚙ mở `ui:NavigationView` dạng drawer (Train options, Label rules, Detect/Angle, Multi-region,
Camera MVS, Hiệu năng). Foreground (lỗi prior-shift V2) nằm ở đây; Thống kê cảnh báo khi mAP50 cao mà mAP50-95 thấp.
Model registry: mỗi lần train = 1 candidate, không ghi đè; trạng thái theo eseg-flow READY → BASELINE → TRAINED →
EXPORTED (parity) → VALIDATED → ACTIVE (user chọn).

## 7. Camera MVS trên .NET 9
`MvCameraControl.Net.dll` máy đang cài target .NET Framework 4.0. EAI-0 spike:
(a) reference dll đó từ net9; nếu lỗi nạp/runtime → (b) P/Invoke thẳng C API `MvCameraControl.dll`
(enum device, open, set exposure/gain/trigger, grab callback, close; ~15 hàm). Pylon/USB: không làm.

## 8. Lộ trình (mỗi mốc 1 commit, UI ra mắt phải hoàn chỉnh cho phần nó có)
| Mốc | Nội dung | Kiểm chứng |
|---|---|---|
| EAI-0 | Retarget net9, thêm 3 project, WPF-UI shell (nền, theme, split + Reset layout, stepper chỉ hiện bước đã làm); spike native DLL + MVS SDK version (enumerate/open/grab chuyển EAI-6) | build, test Core cũ vẫn xanh, spike MVS (cần camera) |
| EAI-1 | EasyLabel.SegAI: clone core, converter SegAI ⇄ LabelMe, infer 1 ảnh | parity: cùng ảnh + model → kết quả = ToolSegmentAI |
| EAI-2 | Canvas WPF (Select/Rect/Rot/Polygon, handle EditRot, undo) + Bước 1 (Ảnh/Dataset) | test toán học + UI smoke 100/150% DPI, vi/en |
| EAI-3 | Bước 2 Dán nhãn + auto-label + import model | UI smoke + so JSON |
| EAI-4 | Bước 3 Train/Retrain + registry + so sánh candidate | train dataset nhỏ, export parity |
| EAI-5 | Bước 4 Kiểm tra list ảnh | so với EasyVision |
| EAI-6 | Camera MVS ở Bước 1 + 4 | phần cứng |
| EAI-7 | Bước 5 Thống kê + CSV | harness số liệu |
| EAI-8 | Drawer Nâng cao, hoàn thiện phím tắt, Reset layout | UI smoke |

## 9. Quyết định đã chốt (user, 2026-10-05)
1. Space = đổi Train/Val; pan ảnh = kéo chuột giữa (Space+kéo của EasyLabel bỏ).
2. Đồng ý thêm WPF-UI 4.x, CommunityToolkit.Mvvm, ScottPlot.WPF 5.
3. Đồng ý retarget EasyLabel.Core/Vision/Ai/App + tests sang net9.
4. Mask raster có lỗ/rời rạc hỗ trợ ngay ở EAI-1 (đóng P5 cho phần SegAI: lưu mask PNG sidecar + polygon outline cho LabelMe).
5. Không lên .NET 10 — ở lại net9 (chấp nhận rủi ro EOL 10/11/2026).

## 10. Rủi ro
- Clone = sửa bug 2 nơi → header nguồn, định kỳ diff.
- ABI native: kiểm version DLL khi khởi động.
- .NET 9 EOL 10/11/2026.
- MVS net40 có thể không chạy trên net9 → phương án (b).
- Train trên máy production 2-core chậm → train trên máy dev.
