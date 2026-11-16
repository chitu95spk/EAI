# Bàn giao: dựng video đếm lục giác (Counter, Segment AI) — phiên mới

Ngày: 2026-11-11. Người giao: chitu. Phiên cũ chuyển sang tối ưu code, KHÔNG làm video nữa.

## Mục tiêu
Dựng video demo EAI đếm vít lục giác bằng mode **Counter** (Segment AI, instance head), theo kịch bản và quy trình như các video trước (AutoLoop Agent SAM3, bản v4).

## Thư mục làm việc RIÊNG (chỉ ghi trong đây)
`D:\CODE\EAI\artifacts\EAI-COUNTER-VIDEO\` (tự tạo; chia con: `rec\`, `frames\`, `review\`, `out\`). Không ghi vào `artifacts\EAI-AUTOLOOP-VIDEO` (giữ làm tham chiếu, chỉ đọc/copy).

## Tài liệu bắt buộc đọc trước
- Kịch bản gốc: `docs\video\SCRIPT_Video_AutoLoop_Agent_SAM3.md`
- Checklist quay từng cảnh: `docs\video\CHECKLIST_Quay_Tung_Canh.md`
- Kế hoạch: `docs\video\PLAN_Video_AutoLoop_Agent_SAM3.md`
- Review v4 (các điểm sai/sót cần rút kinh nghiệm): `docs\video\QA_REVIEW_v4.md`
- Nhạc: `docs\video\music-license.txt`
- Script quay/UI: `tools\video\` (grab.py, uia.py, click_test.py, drag_test.py, key_test.py); mẫu: `tmp\EAI-AUTOLOOP-VIDEO\rec.py`, `drv.py`
- Kết quả Counter/Segment AI: `docs\segai-tune\QA_FINAL_20261111.md`, `docs\segai-tune\REPORT.md`

## Lỗi video v4 phải tránh (chitu phản hồi)
1. Chưa thấy gán label cho các ảnh khác nền.
2. Chưa thấy chỗ sinh ảnh thật (synthetic/real) trên phần mềm.
3. Phần detect chưa đếm đủ lục giác, còn sót nhiều.
=> Video mới phải thể hiện đủ 3 điểm này, trên chính phần mềm EAI (không dựng giả).

## Dữ liệu và model
- Ảnh thật: `Cam_20261019_160350_387.bmp` (20 lục giác).
- Project Counter: `C:\Demo\LucGiac.counter3\c2d.easyai` (manifest; imageRoot `c2d.images`, dataDir `c2d.easyai.data`). Model: `...\models\v001.segai` — KHÔNG ghi đè, làm việc trên bản copy.
- DLL app hiện tại = build4 (`EasyLabel\Lib\native\segai\BeeNativeSegAI.dll`, SHA256 40824FFF…1EBC). Không thay DLL khi chưa hỏi chitu (phiên cũ đang tối ưu code/DLL build5 song song).
- Số liệu thật đã QA (dùng đúng, không làm đẹp): tune40 33/40 (TP615/FP32/FN27); final100 85/100 (1232/63/61, không phải holdout sạch); ảnh thật 17/3/3 trên 20 lục giác. CPU p50 ~21 ms vs CUDA ~8 ms (2,5–2,7×).
- Chỉ dùng GPU RTX 5060.

## Quy tắc
- Tiếng Việt. Không commit/push. Số liệu trung thực, nêu rõ hạn chế (chưa đếm đủ hết thì nói thẳng).
- Không tự tick "done": phải giao QA độc lập nghiệm thu video (xem theo QA_REVIEW_v4) rồi mới báo xong.
- Không xóa `D:\Demo\LucGiac.segai-tune\dataset` (chờ chitu xác nhận riêng).
- Máy: chạy trên Master (gateway host) trừ khi chitu chọn node.
- Nếu gateway restart: chỉ đọc lại lịch sử phiên này để tiếp tục.

## Việc cho phiên mới
1. Đọc tài liệu trên, lập storyboard Counter (ngắn, dùng kịch bản cũ làm khung).
2. Quay trên phần mềm: nạp ảnh → gán label (kể cả ảnh khác nền) → sinh ảnh thật/synthetic → train Counter bằng Segment AI → detect đếm 20 lục giác ảnh thật.
3. Dựng video, xuất vào `out\`, tự review từng khung so với QA_REVIEW_v4.
4. Giao QA độc lập, báo chitu kết quả kèm lỗi còn lại.
