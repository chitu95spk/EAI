# Checklist quay riêng từng cảnh (2026-11-06)

Căn cứ: `SCRIPT_Video_AutoLoop_Agent_SAM3.md`, `PLAN_Video_AutoLoop_Agent_SAM3.md`, `provider-check.txt`.
Chỉ đạo mới của chitu: quy trình Agent tự gán nhãn và sinh ảnh để gán nhãn phải **quay màn hình thật**. Ảnh tự dựng đổi nền làm Agent học kém trên nền thật. Vì vậy cảnh 3, 4, 5 quay thật; cảnh 1, 2 giữ đồ họa; cảnh 6, 7 xem ghi chú riêng.

## A. Chung cho mọi cảnh (làm một lần trước khi quay)

- [ ] Màn hình 1920x1080, scale 100%. Đóng mọi cửa sổ không liên quan, tắt thông báo Windows (Focus assist).
- [ ] Ẩn Sticky Notes (có chuỗi giống mật khẩu). Che/đóng email, API key, đường dẫn cá nhân, thanh tab trình duyệt.
- [ ] Project sạch: chỉ 1 ảnh mẫu `Cam_20261019_160350_387.bmp`, lớp `LucGiac`, chưa có hộp nào (trừ khi cảnh yêu cầu).
- [ ] Model: chạy AI-Box một lần để nạp trước (SAM3 trên CPU lần đầu ~25 s). Chỉ quay lần chạy sau khi đã nạp.
- [ ] Xác nhận provider thật trong app (AI-Box báo SAM3 hay SAM2) và ghi vào `provider-check.txt`. Chưa xác nhận thì caption chỉ ghi SAM2 hoặc "AI tách biên", không ghi SAM3.
- [ ] App đã chuyển sang model Claude Code; kiểm tra Agent trả lời được một lệnh thử trước khi quay cảnh 4, 5.
- [ ] Quay bằng ffmpeg, mỗi cảnh một file `sceneN_raw.mp4`:
  `ffmpeg -f gdigrab -framerate 30 -offset_x 0 -offset_y 0 -video_size 1920x1080 -i desktop -c:v libx264 -preset ultrafast -crf 18 -pix_fmt yuv420p sceneN_raw.mp4`
  (xác nhận đường dẫn ffmpeg trên máy quay trước).
- [ ] Đệm 2–3 s đầu và cuối mỗi clip, không chạm chuột/bàn phím lúc đệm.
- [ ] Dừng ffmpeg bằng phím `q`, mở thử file xem có tiếng/hình đủ, độ dài đúng.
- [ ] Tua nhanh chỉ làm ở bước dựng (`setpts=PTS/N` trên clip riêng), không tua lúc quay. Mọi đoạn tua có nhãn "Tua nhanh xN" và đồng hồ thật.
- [ ] Chạy thử khô (không ghi) một lượt thao tác của cảnh trước khi bấm ghi thật.

## B. Từng cảnh

### Cảnh 1 - Vấn đề (9,1 s, đồ họa)
Chuẩn bị
- [ ] Lưới ảnh ốc (lấy từ ảnh thật của project), con trỏ vẽ hộp thủ công.
- [ ] Caption: "Gán nhãn hàng trăm ảnh bằng tay mất hàng giờ" (Việt + Anh).
Kiểm tra
- [ ] Đúng 4 ô nhịp (9,1 s), không có số liệu bịa.

### Cảnh 2 - Giải pháp (9,1 s, đồ họa)
Chuẩn bị
- [ ] Logo EasyAI + 3 biểu tượng: Agent, AI tách biên, Auto loop.
- [ ] Caption: "Gán 1 ảnh. Phần còn lại giao cho Agent".
Kiểm tra
- [ ] Nhãn "SAM3" chỉ xuất hiện nếu cổng provider ở mục A đã đạt.

### Cảnh 3 - Gán nhãn thông minh (29,7 s, quay thật)
Trước khi quay
- [ ] Mở project, ảnh mẫu đã hiện, AI-Box đã nạp model (đã chạy thử 1 lần).
- [ ] Hộp vẽ quanh **cả con ốc (đầu và thân)** để mask phủ trọn, không dính ốc bên cạnh. Lần quay cũ chỉ phủ đầu ốc.
- [ ] Dùng chuột thật (lần trước dùng chuột thật thì SAM2 ra mask tốt).
Thao tác (theo thứ tự)
- [ ] Mở ảnh -> chọn AI-Box -> kéo 1 hộp -> chờ đề xuất (NG ~0,90) -> chấp nhận -> thấy 1 polygon.
- [ ] Giữ khung kết quả 2–3 s.
Sau quay
- [ ] Caption khớp provider thực tế trên UI. Cắt đoạn chờ nạp model nếu còn.
- [ ] Mask không dính vật bên cạnh; nếu dính, quay lại.

### Cảnh 4 - Agent nhân rộng (29,7 s, quay thật, tua x2–x4 phần chờ)
Trước khi quay
- [ ] **Quyết định hộp thoại "Gửi đến dịch vụ AI?"** (cho phép gửi ảnh + prompt của project ra dịch vụ AI) xong trước khi bấm ghi. chitu đã cho phép với `C:\Demo\LucGiac`; xác nhận lại nếu đổi project.
- [ ] Cửa sổ chat Agent là cửa sổ riêng: mở sẵn, xác nhận thấy được trong vùng quay (đã từng không tìm thấy trên màn hình chính).
- [ ] Chọn chế độ "Ảnh hiện tại". Câu lệnh soạn sẵn, dán vào, không gõ sai trước ống kính.
- [ ] Xóa log cũ hoặc ghi lại tên run để đối chiếu: `%LOCALAPPDATA%\EasyAI\agent-runs\<run>\mcp.log`.
Thao tác
- [ ] Dán lệnh -> Chạy -> để Agent chạy, thấy lần lượt các lệnh gọi tool (không tua đoạn hiện tool call) -> kết quả hiện hộp trên ảnh.
- [ ] Duyệt/xóa hộp lỗi trùng ngay trong app (lần trước 31 box, 12 lỗi, còn 19) và quay luôn bước này.
Sau quay
- [ ] Đối chiếu số box, số tool call, chi phí, thời gian với `mcp.log`. Chỉ ghi số có trong log.
- [ ] Caption không nêu tool/số liệu mà log không có. Lần chạy cũ chỉ có `get_crop`, `detect_points`, `render_overlay`, `detect_points`.
- [ ] Che đường dẫn trong chat nếu lộ.

### Cảnh 5 - Tự sinh dữ liệu từ màn hình thật (20,6 s, quay thật, tua x3)
Lưu ý: thay hướng ảnh synthetic đổi nền bằng ảnh chụp thật. Code đã có tool `capture_frames` và `propagate_to_frames`, nhưng **chưa chạy lần nào trên màn hình thật** (mới test logic). Phải chạy thử trước.
Trước khi quay
- [ ] Chạy thử `capture_frames` một lần, kiểm tra frame lưu ra đúng thư mục, đúng định dạng.
- [ ] Chạy thử `propagate_to_frames` từ 1 frame đã xác nhận sang các frame thật, mở xem nhãn có đúng vị trí.
- [ ] Nguồn frame: vùng màn hình hoặc video thật, nền đa dạng (nhiều nền thật khác nhau, không đổi nền ghép).
Thao tác
- [ ] Bắt đầu từ frame đã gán nhãn (kết nối cảnh 3–4) -> chạy chụp frame -> lan truyền nhãn -> lướt qua các frame có hộp/polygon.
- [ ] Hiện bộ đếm số ảnh/số nhãn và đồng hồ thật.
Sau quay
- [ ] Số ảnh, số nhãn trong video khớp thư mục dữ liệu thật, không dùng lại số 300 ảnh/965 nhãn của lần synthetic.
- [ ] Nếu kết quả lan truyền sai nhiều, không quay lại bằng cách chỉnh tay; báo lỗi để sửa pipeline.

### Cảnh 6 - Auto loop (29,7 s, tua x17)
- [ ] Ưu tiên quay màn hình thật phần chạy train/đánh giá của app nếu có hiển thị. Nếu app không hiển thị loss/mAP, dùng đồ họa từ `results.csv` của **run mới** (sau khi cảnh 5 đổi sang ảnh thật) và ghi rõ "dựng từ dữ liệu thật".
- [ ] Run train mới phải có dữ liệu thật từ cảnh 5; không dùng lại `run-20261106-144740` (đó là synthetic).
- [ ] Đồng hồ thật ở góc, nhãn "Tua nhanh x17" (hoặc hệ số mới).
- [ ] Nếu đạt mục tiêu ngay vòng 0 thì ghi đúng như vậy, không dựng nhiều vòng khi chưa có số liệu.

### Cảnh 7 - Kết quả (20,6 s, không tua)
- [ ] Chạy model `best.pt` mới (từ run ở cảnh 6) trên ảnh thật, lấy overlay đếm ốc từ chính lần chạy đó.
- [ ] Quay màn hình thật phần chạy/hiện kết quả nếu app có; bảng mAP/recall lấy từ `report.json` của run mới.
- [ ] Ghi chú tập test nhỏ (số vật test thật). Không ghi mAP/recall cũ nếu run đã đổi.
- [ ] Không tua.

## C. Sau khi quay xong tất cả
- [ ] Đối chiếu từng con số/caption với log thật, đặc biệt provider (SAM2/SAM3).
- [ ] Chốt độ dài cảnh trước khi dựng nhạc (đổi sau thì nhạc lệch).
- [ ] Xem thử với một người chưa biết sản phẩm; kiểm tra không lộ email, key, đường dẫn.
- [ ] Nhạc: nguồn và giấy phép ghi vào `music-license.txt`.

## Việc còn mở trước khi quay
1. Provider thật của AI-Box trong app (SAM2 hay SAM3): chưa kiểm.
2. `capture_frames`/`propagate_to_frames` chưa chạy trên màn hình thật.
3. SAM3 đang để chung thư mục với SAM2 (chitu báo): cần tách/kiểm lại đường dẫn model.
4. Cảnh 5–6–7 hiện trong `artifacts\EAI-AUTOLOOP-VIDEO` đều là đồ họa từ dữ liệu synthetic; sẽ phải làm lại nếu đi theo hướng quay thật.
