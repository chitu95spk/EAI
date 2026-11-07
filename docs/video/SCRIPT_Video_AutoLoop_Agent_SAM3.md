# Kịch bản chi tiết v4 (2026-11-06) - viết lại theo quy tắc mới

Brief: ~2:30, caption Việt + phụ đề Anh, 105 BPM (1 ô nhịp = 2,286 s), 1920x1080, 30 fps, lớp `LucGiac`, ảnh gốc `Cam_20261019_160350_387.bmp`.

## Quy tắc mới (chốt từ yêu cầu của chitu trong phiên này)
1. **Video sao thì phần mềm phải thật như vậy.** Mọi cảnh phải quay/chụp từ chính app EasyAI. Không dựng đồ họa thay cho tính năng chưa có. Tính năng chưa chạy được trong app thì cảnh đó chưa được quay.
2. **Tự sinh ảnh và tự chia 300 train / 100 test phải chạy thật trong app (Agent tự gọi, chế độ Counter)**, sau đó mới chụp màn hình. 100 ảnh test sinh riêng, khác 300 ảnh train, vật thể test không xuất hiện trong train.
3. **Ảnh sinh phải đa dạng:** số ốc mỗi ảnh nhiều/ít khác nhau, tư thế (xoay, lật, tỉ lệ) đa dạng; nền khó: không đều, sáng/tối, tương phản kém, khác màu, có nền lạ chỉ dùng ở test.
4. **Vật thể được tách nền trước khi sinh ảnh, rồi hòa quyện vào nền** (khử viền, hòa màu, bóng tiếp xúc, làm mềm mép). Cảnh 5 phải cho thấy ảnh sinh trông tự nhiên, không phải dán cắt.
5. **Train thật trên 300 ảnh, quay tua nhanh** (nhãn "Tua nhanh xN" + đồng hồ thực). **Detect thật trên 100 ảnh test trong app**, không tua.
6. Bỏ hộp thoại "Gửi đến dịch vụ AI?": đã nằm trong chính sách phần mềm, không quay, không nhắc.
7. **Box phải đúng:** SAM phủ trọn con ốc (đầu và thân), các box gần nhau không lệch, không chồng. Cảnh 3, 4 chỉ dùng khi đạt; nếu chưa đạt thì chụp lại hộp vẽ quanh cả con ốc và duyệt xóa box lỗi.
8. Caption trung thực: AI-Points là SAM2, không gọi là SAM3. Chỉ ghi SAM3 khi log báo SAM3 (cổng `provider-check.txt`).
9. Số liệu trong video chỉ lấy từ lần chạy thật sau cùng; chưa có thì để trống, không dùng số cũ (mAP 0,995 cũ chỉ đo trên 4 vật).
10. Che đường dẫn, email, API key; ẩn Sticky Notes. Chat Agent chỉ mở lúc gõ lệnh/đọc kết quả. Chốt độ dài cảnh trước khi dựng nhạc.

## Bảng cảnh (66 ô nhịp = 150,9 s)

| Cảnh | Ô nhịp | Thời lượng | Loại | Hình / thao tác | Caption (Việt) | Nguồn số liệu |
|---|---|---|---|---|---|---|
| 1 Vấn đề | 3 | 6,9 s | Đồ họa | Lưới ảnh ốc, hộp vẽ tay từng ảnh | "Gán nhãn hàng trăm ảnh bằng tay mất hàng giờ" | Không số liệu |
| 2 Giải pháp | 3 | 6,9 s | Đồ họa | Logo EasyAI, 3 biểu tượng Agent / SAM / Auto loop | "Gán 1 ảnh. Phần còn lại giao cho Agent" | Không số liệu |
| 3 Gán nhãn 1 ảnh | 10 | 22,9 s | Quay app thật | Mở ảnh mẫu, vẽ hộp quanh cả con ốc, SAM tách biên, chấp nhận polygon | "Một hộp, SAM tách biên vật" | Log provider (SAM2/SAM3 ghi đúng theo log) |
| 4 Agent nhân rộng | 11 | 25,1 s | Quay app thật, tua x2-x4 phần chờ | Gõ lệnh vào chat Agent, hiện tool đang gọi, box xuất hiện trên ảnh, duyệt xóa box lỗi | "Agent tìm các vật còn lại và gán nhãn" | `mcp.log` của lần chạy quay; chi phí lấy từ log |
| 5 Tự sinh 300 + 100 ảnh | 10 | 22,9 s | Quay app thật (Counter), tua x3-x6 | Agent chạy sinh ảnh: vật tách nền, hòa vào nền khó; lướt lưới ảnh nhiều/ít ốc, nền sáng/tối/lạ; bộ đếm 300 train, 100 test | "Từ vài vật đã gán nhãn, Agent tự sinh 300 ảnh train và 100 ảnh test khác biệt" | `events.jsonl` / manifest của lần chạy quay (số ảnh, số nhãn, số vật train/test) |
| 6 Train thật | 11 | 25,1 s | Quay app thật, tua x20-x60, hiện đồng hồ thực | Màn hình train 300 ảnh: loss, mAP theo epoch, vòng auto loop (nếu có) | "Tự train, tự đánh giá, lặp tới khi đạt mục tiêu" | `results.csv` + `report.json` của lần train quay |
| 7 Detect thật 100 ảnh test | 11 | 25,1 s | Quay app thật, không tua | Chạy detect trên 100 ảnh test trong app, overlay OBB, đếm ốc mỗi ảnh | "Phát hiện thật trên 100 ảnh chưa từng thấy" | Kết quả detect của app trên 100 ảnh test |
| 8 Kết quả | 7 | 16,0 s | App + đồ họa số liệu | Bảng mAP50, recall, precision, thời gian thực; vài ảnh test tiêu biểu | "mAP50 ___, recall ___ (100 ảnh test)" | `report.json` lần chạy cuối. **Điền sau khi chạy** |

## Trạng thái từng cảnh (trung thực)
| Cảnh | Sẵn sàng quay? | Còn thiếu |
|---|---|---|
| 1, 2 | Có | Chỉ cần dựng đồ họa |
| 3 | Chưa chắc | Ảnh cũ chỉ phủ đầu ốc; cần quay lại hộp vẽ cả con ốc, xác nhận provider |
| 4 | Chưa | Box từng lệch/chồng. Cần chạy lại, duyệt xóa box lỗi, quay trực tiếp (bản cũ là dựng lại từ log) |
| 5 | Chưa | `synthgen.py` đã có tách nền + hòa nền (mẫu `tmp\synth\ds4`, mới duyệt bằng mắt), chưa nối vào Agent/app, chưa sinh đủ 300 + 100 |
| 6 | Chưa | Chưa train trên bộ mới; cần màn hình train trong app (hiện app chưa có màn "Tự học" đủ để quay) |
| 7 | Chưa | Chưa có detect OBB chạy trong app trên 100 ảnh test |
| 8 | Chưa | Số liệu chờ lần chạy thật |

Ghi chú: gốc chỉ có 7/20 ốc tách riêng được (train: id 0,1,3,11,17; test: id 2,4). Test có ít vật gốc nên số đo vẫn bị giới hạn; nên nói rõ trong video hoặc bổ sung thêm ảnh gốc nếu muốn số liệu thuyết phục khách.

## Thứ tự làm để quay được
1. Chốt `ds4` (hoặc chỉnh tiếp), sinh bộ 300 train / 100 test đầy đủ.
2. Train thử YOLO-OBB trên bộ đó, đo thật trên 100 ảnh test; nếu kém thì chỉnh generator.
3. Nối vào app: Agent gọi sinh ảnh, màn hình train, detect 100 ảnh (`autoloop.py` ghi `events.jsonl`, worker Python detect OBB, màn "Tự học"). Chạy `dotnet test`.
4. Qua cổng provider, quay cảnh 3, 4.
5. Quay cảnh 5, 6, 7 trong một lần chạy thật liên tục; lấy số liệu cảnh 8.
6. Khóa độ dài cảnh, dựng nhạc 105 BPM theo ô nhịp, ghép, đối chiếu số liệu với log.

## Chưa chốt
- Có dùng ảnh gốc thêm để test đáng tin hơn không (hiện test chỉ 2 vật gốc).
- Nếu cảnh 6 chạy một vòng là đạt mục tiêu thì ghi đúng "đạt ngay vòng 0", không dựng nhiều vòng giả.
