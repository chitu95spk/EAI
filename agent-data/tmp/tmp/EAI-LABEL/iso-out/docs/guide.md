# EasyAI - Hướng dẫn / User guide

## Tiếng Việt

### Mở ảnh và dự án
- **Mở Thư mục (Ctrl+U)** chọn thư mục ảnh làm dự án. Mỗi ảnh có tệp nhãn LabelMe (.json) cạnh ảnh.
- **Mở (Ctrl+O)** thêm ảnh vào dự án đang mở. **Nhập dataset** (menu EasyAI) nhập dataset EasyVision.
- **Hình ảnh Trước (A)** và **Hình ảnh Tiếp theo (D)** chuyển ảnh; phím mũi tên lên/xuống cũng chuyển ảnh khi không chọn hình nào.

### Vẽ nhãn
- Công cụ: Chọn (V), Hình chữ nhật (R hoặc Ctrl+R), Hộp xoay (O), Đa giác (P hoặc Ctrl+N). Phím 1 chọn lớp NG, phím 2 chọn lớp OK.
- Enter hoàn tất đa giác, Backspace hoặc Ctrl+Z bỏ điểm cuối khi đang vẽ, Esc huỷ.
- Hoàn tác Ctrl+Z, làm lại Ctrl+Y, sao chép Ctrl+C, dán Ctrl+V, nhân đôi Ctrl+D, xoá hình Del.
- Vừa Cửa sổ: F hoặc Ctrl+F. Kích thước thật: Ctrl+0 hoặc Ctrl+1. Chuột giữa để kéo ảnh.
- Thanh trạng thái hiển thị chế độ (EDIT hoặc CREATE) và toạ độ điểm ảnh dưới con trỏ.

### Lưu
- **Tự động lưu** (bật mặc định): mỗi chỉnh sửa được ghi ngay.
- Tắt **Tự động lưu**: chỉnh sửa được giữ trong bộ nhớ, tiêu đề cửa sổ có dấu *. **Lưu (Ctrl+S)** ghi ảnh hiện tại. Khi đổi ảnh, đóng ảnh hoặc thoát mà chưa lưu, chương trình hỏi Lưu, Không lưu hoặc Huỷ.
- **Lưu thành (Ctrl+Shift+S)** ghi tệp nhãn LabelMe ra đường dẫn bạn chọn; dự án không đổi.
- **Nhúng Hình ảnh vào Tệp nhãn** ghi dữ liệu ảnh (imageData) vào tệp .json; tệp sẽ lớn hơn.
- **Thay đổi Thư mục Đầu ra** ghi thêm một bản tệp nhãn LabelMe vào thư mục đó (cùng đường dẫn tương đối). Tệp của dự án vẫn nằm cạnh ảnh.
- **Đóng (Ctrl+W)** đóng ảnh hiện tại. **Xóa (Ctrl+Del)** xoá các tệp nhãn của ảnh hiện tại sau khi xác nhận; ảnh không bị xoá.

### Cài đặt (Ctrl+Shift+,)
- Chủ đề màu (Hệ thống, Sáng, Tối), Ngôn ngữ (áp dụng sau khi khởi động lại), Tự động lưu, Nhúng hình ảnh, Thư mục đầu ra.
- Các dòng bị mờ chưa khả dụng trong phiên bản này; rê chuột để xem lý do. Hỗ trợ AI hiển thị "Không khả dụng" khi thiếu tệp mô hình.
- **Mở tệp cấu hình dưới dạng văn bản** mở settings.json bằng trình soạn thảo mặc định.

### Các bước EasyAI
- Dữ liệu: ảnh, chế độ Train/Val (phím Space), nhập dataset.
- Gán nhãn: gán nhãn tự động bằng model, chấp nhận hoặc từ chối gợi ý.
- Huấn luyện: bắt đầu, dừng train, so sánh và dùng model.
- Các mục menu bị mờ (hình tròn, điểm, đường, gộp hình, thu phóng, độ sáng, AI...) sẽ được bật khi tính năng được thêm.

## English

### Opening images and projects
- **Open Dir (Ctrl+U)** picks an image folder as the project. Each image has a LabelMe file (.json) beside it.
- **Open (Ctrl+O)** adds images to the open project. **Import dataset** (EasyAI menu) imports an EasyVision dataset.
- **Prev Image (A)** and **Next Image (D)** switch images; the Up/Down arrows also switch when no shape is selected.

### Drawing labels
- Tools: Select (V), Rectangle (R or Ctrl+R), Rotated box (O), Polygon (P or Ctrl+N). Key 1 selects class NG, key 2 selects class OK.
- Enter finishes a polygon, Backspace or Ctrl+Z removes the last point while drawing, Esc cancels.
- Undo Ctrl+Z, redo Ctrl+Y, copy Ctrl+C, paste Ctrl+V, duplicate Ctrl+D, delete shape Del.
- Fit window: F or Ctrl+F. Actual size: Ctrl+0 or Ctrl+1. Middle mouse button pans.
- The status bar shows the mode (EDIT or CREATE) and the image pixel under the pointer.

### Saving
- **Auto-save** (on by default): every edit is written at once.
- With **Auto-save** off, edits stay in memory and the window title shows *. **Save (Ctrl+S)** writes the current image. Switching images, closing the image or exiting with unsaved edits asks Save, Discard or Cancel.
- **Save As (Ctrl+Shift+S)** writes the LabelMe file to a path you choose; the project is unchanged.
- **Save With Image Data** stores the image (imageData) in the .json file; files become larger.
- **Change Output Dir** also writes a copy of the LabelMe file to that folder (same relative path). The project files stay beside the images.
- **Close (Ctrl+W)** closes the current image. **Delete File (Ctrl+Del)** deletes the label files of the current image after a confirmation; the image itself is never deleted.

### Settings (Ctrl+Shift+,)
- Color theme (System, Light, Dark), Language (applies after restart), Auto-save, Embed image data, Output directory.
- Dimmed rows are not available in this version; hover for the reason. AI support shows "Unavailable" when the model files are missing.
- **Open config file as text** opens settings.json in the default editor.

### EasyAI steps
- Data: images, Train/Val mode (Space), dataset import.
- Label: auto-label with a model, accept or reject suggestions.
- Train: start, stop, compare and use a model.
- Dimmed menu items (circle, point, line, merge shapes, zoom, brightness, AI...) are enabled when the feature is added.
