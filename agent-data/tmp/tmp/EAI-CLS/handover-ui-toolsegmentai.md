# Bàn giao: sửa giao diện ToolSegmentAI cho N lớp (EAI-CLS)

Ngày: 2026-10-10. Người nhận: người sửa UI ToolSegmentAI. EasyLabel/EasyAI (EAI) đang được người khác sửa, **không chạm** `EasyLabel/**`.

## 1. Tình trạng

Backend và UI N lớp đã code và commit trên `main`. Chưa ai chạy app thật. Mọi thứ về hiển thị (cắt chữ, DPI, vi/en, cuộn) **chưa kiểm chứng**. Phần dưới là danh sách nguy cơ, không phải lỗi đã xác nhận.

Commit liên quan (cũ đến mới):
- 68e07f7d3 S3a: bộ mã hoá bảng lớp, `NativeSegAITrainer.SetClassTable`
- 9e4769c4d S3b: mask mang id 1..N
- bd32a9741 S3c: bảng lớp trên `SegmentAI`, gửi bảng khi train, retrain dùng lại bảng
- 380c994d9 S3d: sửa theo review, test train native 3 lớp
- cb45a88d2 S4: UI N lớp (editor, bộ chọn lớp, hotkey)
- 4e181bfe3 S4b: ẩn editor ở project 2 lớp
- 9f23df272 S4c: sửa cờ chờ hiện editor
- b580c26a6 S5: số đếm theo lớp trong kết quả Val

## 2. Quy tắc bất biến

- Project 2 lớp (không có bảng lớp, `GetClassTable() == null`) phải trông và chạy **y hệt trước đây**. Chỉ được khác duy nhất một hàng "Lớp nhãn…" cuối palette chuột phải.
- Chỉ dùng control tuỳ biến có sẵn (RJButton, TextBoxAuto, ...). Không dùng CheckBox/ComboBox/NumericUpDown gốc khi đã có control tuỳ biến cùng vai trò (CLAUDE.md 0.2a).
- Không UI mock, không tips/help text. Mimic tool có sẵn.
- Layout phải thích ứng kích thước cửa sổ, DPI, chữ vi/en; không cắt, không chồng.
- C# 7.3. Event luôn `-=` rồi `+=`. Không tạo `Common.PropetyTools[...]` mới.
- Giá trị mask 255 hoặc lớn hơn N là nền (0). Đã quyết, không đổi.
- Không build vào `bin\Release`, `bin\x64\Release`, `lib\native`. Không rebuild `BeeLicense.dll` vào Release.

## 3. File và vị trí (số dòng gần đúng, có thể lệch)

| Việc | File |
| --- | --- |
| Bộ chọn lớp trên canvas, màu, hotkey 1-9, thanh gợi ý, nút "Lớp nhãn…" | `BeeInterface/Custom/DefectLabelCanvas.cs` (SetClassTable ~137, palette ~93-102, LabelButtons ~358, TryClassDigitHotkey ~197, DrawRegion ~858, hint bar trong OnPaint ~839) |
| Editor bảng lớp (mục "Lớp nhãn" ở tab Basic), ClassApply, ClassNameProblem, ẩn/hiện | `BeeInterface/Tool/ToolSegmentAI.Workspace.cs` (section ~165, BuildClassTableEditor ~622, OpenClassTableEditor ~659-684, ClassApply_Click ~801-832, ClassNameProblem ~830) |
| Hook canvas và hotkey filter | `BeeInterface/Tool/ToolSegmentAI.Annotation.cs` (~65-73, ~249) |
| Chuyển tab, save-and-park khi gán nhãn | `BeeInterface/Tool/ToolSegmentAI.Training.cs` (MainTabSelecting ~318-338, mask scan ~915-950) |
| Huy hiệu NG thumbnail | `BeeInterface/Tool/ToolSegmentAI.TrainingImages.cs` (TrainingSampleHasNg ~166) |
| Cột kết quả Val (N lớp) | `BeeInterface/Tool/ToolSegmentAI.TrainingOptions.cs` (NamedClassCountsText, EnsureValResultColumns, BuildValResultRow) |
| Mã chết, không tới được từ UI: **không xoá** (cần smoke trước) | `BeeInterface/General/FormSegmentTraining.cs` |

API backend dùng từ UI: `SegmentAI.GetClassTable()`, `SetClassTable(IList<SegClassInfo>)`, `SegmentAI.NamedClassJudgement.UsesNamedClasses` (đúng khi có bảng lưu và khác layout NG/OK mặc định), `SegClassInfo` (Id = vị trí + 1, Name, Kind Defect/Good, Enabled). Tối đa 32 lớp. Tên 1..64 byte UTF-8, không ký tự điều khiển, không trùng, tối đa một lớp Good, ít nhất một lớp bật.

## 4. Việc cần làm: nguy cơ UI cần xem và chỉnh

1. **Nút chọn lớp** trên canvas rộng cố định 84 px, không co theo DPI. Kiểm tra ở 125% và 150%. Palette 32 lớp phải cuộn được.
2. **Thanh gợi ý** một dòng cao 28 px: tiền tố "Lớp: tên ·" đẩy chữ ra khỏi mép phải ở cửa sổ hẹp.
3. **Cột Val** N lớp rộng 150 và 180 px: có thể phải cuộn ngang. Đang dựa vào "…" và tooltip. Thử tên dài và nhiều lớp có box cùng lúc.
4. **Editor bảng lớp**: hàng tên, kind, bật/tắt và dòng trạng thái ở 1024x768 và 125%. Nút "+ Thêm lớp" phải tắt ở lớp thứ 32.
5. **Chữ**: nút "Lớp nhãn…" chỉ có tiếng Việt, cần bản tiếng Anh. Thông báo lỗi từ core (ví dụ thiếu lớp bật, hai lớp Good) còn tiếng Anh thô.
6. **Lần nạp ảnh đầu** với bảng mới có thể chọn sẵn lớp 2 thay vì lớp bật đầu tiên (`SetClassTable` giữ `currentLabel`, trường khởi tạo là 2).
7. **Thử cả bốn tình huống nút "Lớp nhãn…"**: từ lúc đang gán nhãn khi tab Basic chưa chọn; lúc đang train (phải hiện thông báo, không mở mục); bấm đúp lúc đang đỗ phiên; đỗ phiên thất bại (không được tự mở mục khi sang Basic sau đó).
8. Sau "Áp dụng lớp", model cũ không bị vô hiệu: thông báo nói phải train lại. Kiểm tra thông báo đó đọc được.

## 5. Checklist smoke (100% DPI, vi và en; thêm 1024x768 và 125%)

1. Project 2 lớp: tab Basic không có mục "Lớp nhãn"; palette chuột phải có OK/NG/Undo cộng hàng "Lớp nhãn…"; vùng mới mặc định OK; màu và chữ như cũ; phím số không làm gì.
2. Bấm "Lớp nhãn…": sang Basic, mục mở. Thêm lớp, đổi tên, đổi kind, tắt một lớp, Áp dụng. Tên rỗng, trùng, 65 byte, ký tự điều khiển phải bị từ chối bằng thông báo bản địa hoá.
3. Chế độ N lớp: palette có bộ chọn lớp và Undo, không còn hàng OK/NG. Phím 1..N chọn lớp trên canvas và từ control khác; không bắt phím khi đang gõ trong ô văn bản. Đổi ảnh bằng ↓ giữ lớp đang chọn.
4. Vùng lớp 3 tính là đã gán nhãn và không phải NG; tắt một lớp Lỗi làm huy hiệu NG biến mất.
5. Train lại, mở lại project: tên lớp nạp lại đúng. Kết quả Val hiện "Tên: thật/dự đoán".
6. Chụp màn hình vào `artifacts/EAI/EAI-CLS/<run>/` (dùng `tools/ev-thumb.ps1` trước khi đọc ảnh). Có harness UIA smoke sẵn trong repo (xem `docs/` hoặc ghi chú EAI-3/EAI-4).

## 6. Cách build và test (cô lập, không chạm bin/lib)

```
MSBuild BeeInterface\BeeInterface.csproj -t:Build -p:Configuration=Release -p:Platform=x64 -p:EasyVisionIsolatedVerification=true "-p:OutDir=<repo>\artifacts\EAI\EAI-CLS\<run>\out\" -v:minimal > build.log 2>&1
```
Đặt `TEMP`/`TMP` về `<repo>\tmp\EAI-CLS`. Test logic: build `tests\BeeCore.Persistence.Tests\BeeCore.Persistence.Tests.csproj` cùng cách, chép `artifacts\EAI\EAI-CLS\p1\s3\run\*.dll` và thư mục `Models` vào thư mục out, rồi chạy `--segai-class-train`, `-class-mask`, `-class-encode`, `-n-class`, `-class-table`, `-class-native-train`, `--v148-focused`. Tất cả đang PASS (bằng chứng: `artifacts/EAI/EAI-CLS/p3/s5/`).

Worktree có nhiều file đang sửa của session khác. Chỉ stage hunk của mình. `tests/BeeCore.Persistence.Tests/Program.cs` có hunk lạ: dựng blob từ HEAD rồi chèn nhánh của mình (mẫu: `tmp/EAI-CLS/Program.new4.cs`), commit bằng index tạm `GIT_INDEX_FILE`.

## 7. Ngoài phạm vi / còn nợ không thuộc UI

- P4 (độ trễ N>2, bộ nhớ K=33 trên phần cứng) và P5 (live folders): cần user đồng ý riêng.
- `dataset.json` của BeeCore chưa có trường `classes`: dataset N lớp nhập từ EasyLabel mất bảng lớp. Chưa quyết có thêm không.
- Đổi tên lớp ở EasyLabel làm nhãn LabelMe cũ thành "other", không cảnh báo (thuộc EAI).
- EasyLabel có `BuildImageResults` riêng, chưa có số đếm theo lớp (thuộc EAI).
- Chưa có chất lượng dự đoán N lớp trên ảnh thật; test chỉ dùng ảnh tổng hợp.
- Chi tiết toàn bộ quyết định: `tmp/EAI-CLS/deferred.md` (đọc phần cuối, đừng đọc cả file 60 KB).
