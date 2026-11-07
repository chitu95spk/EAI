# Plan dựng video: EasyAI Auto loop với Agent và SAM3 (bản r2)

Ngày: 2026-11-06. Project demo: "Counter Day dien DEMO". Ảnh mẫu: `Cam_20261019_160350_387.bmp`.
Thay đổi so với r1: (a) thêm kiểm tra provider SAM3/SAM2 trước khi quay, (b) thêm time-lapse, (c) Bước 6 đổi từ giọng đọc sang dựng nhạc không lời theo nhịp video.

## Thành phần dùng lại
- Agent và công cụ: `EasyLabel\src\EasyLabel.Ai.Mcp\EasyAiTools.cs` (`get_reference`, `detect_points`, `propagate_labels`, `synthesize_samples`, `auto_train_loop`, `propose_shapes`, `pick_candidates`, `sam2_segment`...).
- SAM3: `AiBackendService.cs:96-108` dùng SAM3 khi có `models\sam3`, tự quay về SAM2 nếu thiếu hoặc lỗi.
- Auto loop: `AutoLoopRunner.cs` gọi `tools\autoloop\autoloop.py` (labelme → synthetic → train YOLO-OBB → đánh giá held-out → lặp tới mAP50 ≥ 0.95, recall ≥ 0.98).

## Bước 0. Chốt brief (~30 phút)
1. Người xem, độ dài (đề xuất 2–3 phút), ngôn ngữ chú thích (Việt + phụ đề Anh).
2. Thông điệp: "Gán nhãn 1 ảnh, Agent và SAM3 làm phần còn lại, vòng lặp tự train đến khi đạt mục tiêu".
3. Vật thể demo: ốc lục giác, lớp `LucGiac` (cần tạo trước, project hiện chỉ có NG/OK).
4. Chốt nhịp video: BPM nhạc (đề xuất 100–110) và số ô nhịp mỗi cảnh, để Bước 6 và dựng cắt khớp nhau.

## Bước 1. Kiểm kê tính năng và KIỂM TRA PROVIDER (~1,5 giờ)
1. Đọc `EasyAiTools.cs`, `AutoLoopRunner.cs`, `autoloop.py`, `Sam3Backend.cs`. Với mỗi tính năng đưa vào video, ghi input, output, UI.
2. **Cổng kiểm tra SAM3 (bắt buộc, không qua thì không quay):**
   - Thư mục `models\sam3` tồn tại trên máy demo và đủ file model.
   - Mở project, chạy AI-Box trên ảnh mẫu, xác nhận log/UI báo provider là SAM3, không phải SAM2 fallback (mask phủ đủ đầu và thân ốc, score ~0,97 như test).
   - Nạp sẵn model trước khi quay (lần đầu ~20 giây vì SAM3 chạy CPU, DirectML lỗi).
   - Ghi lại tên provider, thời gian nạp, ngày kiểm tra vào `docs\video\provider-check.txt`. Lưu ý AI-Points vẫn dùng SAM2, không gọi nó là SAM3 trong lời chú thích.
3. Chạy thử một vòng đầy đủ, ghi số liệu thật (mAP, recall, thời gian mỗi vòng, số ảnh sinh). Số trong video lấy từ đây.
4. Ghi giới hạn thật để không nói quá (đánh giá ở mức tile, Auto loop qua Agent chưa chạy ổn: lượt `auto_train_loop` từ chat từng trả 0 đề xuất, cần xử lý ở `AgentRunFiles.cs:294` hoặc chạy `autoloop.py` trực tiếp và nói đúng như vậy).
5. Chi phí: ước tính ban đầu ~$1/lệnh, nhưng lần chạy thật cảnh 4 chỉ tốn $0,06 (34 s). Người dùng đã duyệt ngân sách $10.

## Bước 2. Khung câu chuyện (~1 giờ)
| Cảnh | Nội dung | Thời lượng | Time-lapse |
|---|---|---|---|
| 1. Vấn đề | Gán nhãn thủ công hàng trăm ảnh rất chậm | 10 s | Không |
| 2. Giải pháp | Giới thiệu EasyAI, Agent, SAM3 | 10 s | Không |
| 3. Gán nhãn thông minh | Vẽ 1 hộp, SAM3 tách biên | 30 s | Không |
| 4. Agent nhân rộng | `get_reference`, `detect_points`, `propagate_labels` | 30 s | x2–x4 phần Agent chờ |
| 5. Tự sinh dữ liệu | `synthesize_samples` tạo ảnh synthetic | 20 s | x4–x8 lướt ảnh sinh ra |
| 6. Auto loop | Train YOLO-OBB (n→s→m), đánh giá held-out, lặp tới mục tiêu | 30 s | x16–x60 phần train, hiện đồng hồ thật |
| 7. Kết quả | Overlay đếm ốc, bảng mAP/recall, thời gian thật | 20 s | Không |

## Bước 3. Kịch bản chi tiết (~2 giờ)
1. Mỗi cảnh: số cảnh, thời lượng, hình, caption, thao tác chuột, số liệu, nguồn dữ liệu (log nào).
2. Đánh dấu cảnh quay màn hình thật và cảnh đồ họa.
3. **Quy tắc time-lapse:** mọi đoạn tua nhanh phải có nhãn góc hình "Tua nhanh xN" và đồng hồ thời gian thực (ví dụ "Thực tế: 18 phút"). Không tua đoạn có kết quả quan trọng (mAP, recall cuối). Tua bằng ffmpeg `setpts=PTS/N` trên clip riêng, không tua lúc quay.
4. Chat Agent chỉ mở khi gõ lệnh hoặc đọc kết quả, rồi thu lại để thấy trọn ảnh chính.

## Bước 4. Chuẩn bị tư liệu quay (~2 giờ)
1. Project sạch, có đúng 1 hộp xác nhận lớp `LucGiac`. Ẩn Sticky Notes (có chuỗi giống mật khẩu).
2. Chạy `autoloop.py` lần cuối, lưu log, ảnh overlay, biểu đồ mAP theo vòng.
3. Ảnh before/after, đặt tên theo số cảnh.
4. 1920×1080, 30 fps. Quay các đoạn dài (train, sinh ảnh) ở tốc độ thật để tua sau.

## Bước 5. Quay màn hình (~2 giờ)
1. Qua cổng kiểm tra provider ở Bước 1 trước, rồi mới quay.
2. Mỗi cảnh một clip; thêm 2–3 giây đệm đầu và cuối.
3. Che đường dẫn, email, API key. Không chạm chuột/bàn phím khi quay. Dừng ffmpeg bằng `q` và mở thử file.
4. Hộp thoại "Gửi đến dịch vụ AI?" cần người dùng quyết định cho phép hay không trước khi quay cảnh Agent.

## Bước 6. Dựng nhạc không lời hợp nhịp video (~2–3 giờ) — thay cho bước giọng đọc cũ
1. Lấy độ dài từng cảnh sau time-lapse (Bước 3) và BPM đã chốt ở Bước 0. Tính điểm nhịp: mỗi cảnh làm tròn thành số ô nhịp nguyên để điểm cắt trùng phách.
2. Cấu trúc nhạc theo cảnh:
   - Cảnh 1–2: intro nhẹ, synth pad hoặc piano thưa, tiết tấu chậm.
   - Cảnh 3–4: thêm bass và nhịp đều, tăng dần năng lượng.
   - Cảnh 5–6: tiết tấu dày hơn (arpeggio, hi-hat), tăng nhịp khi time-lapse chạy.
   - Cảnh 7: cao trào rồi kết gọn, nốt cuối rơi đúng khung kết.
3. Tạo nhạc gốc không lời (không dùng bản nhạc có bản quyền): dựng bằng script (Python/numpy hoặc MIDI → WAV) hoặc nguồn nhạc miễn phí bản quyền có giấy phép rõ ràng. Ghi nguồn và giấy phép vào `docs\video\music-license.txt`.
4. Xuất WAV 48 kHz, kiểm tra độ dài khớp tổng video (sai số ≤ 1 phách).
5. Dựng khớp: cắt, chuyển cảnh và zoom rơi vào phách mạnh. Giữ nhạc nền ở mức vừa (khoảng -18 đến -14 LUFS) để không lấn âm thanh nào khác nếu có.
6. Không có giọng đọc: ý nghĩa truyền qua caption, nhãn tool Agent đang gọi, và phụ đề. Nếu sau này muốn thêm giọng đọc, đó là một bước riêng.

## Bước 7. Ghép, kiểm tra và xuất bản (~1,5 giờ)
1. Ghép clip, caption, highlight, nhãn time-lapse và track nhạc.
2. Đối chiếu từng con số với log thật. Đối chiếu caption với giới hạn ở Bước 1, đặc biệt tên provider (SAM3 hay SAM2).
3. Xem thử với một người chưa biết sản phẩm.
4. Xuất MP4 và bản phụ đề rời; mở thử file trước khi giao.

## Tổng ước tính
Khoảng 12–14 giờ, chưa tính chi phí Agent (~$1 mỗi lệnh). Rủi ro chính: thiếu `models\sam3` (video sẽ quay SAM2), Auto loop qua Agent chưa chạy ổn, nhạc lệch nhịp nếu đổi độ dài cảnh sau khi đã dựng nhạc (nên khóa độ dài cảnh trước Bước 6).
