# Kết quả train/test thật trên bộ ds5 (2026-11-06)
Bộ: tmp\synth\ds5 (synthgen.py, seed 3). 300 train (4187 nhãn), 100 test (1505 nhãn), vật train id 0,1,3,11,17; vật test id 2,4; không rò rỉ (kiểm tra trong gen_meta.json).
Train: YOLO11n-OBB, 40 epoch, imgsz 1024, batch 8, GPU, ~6 phút (log: tmp\synth\train_ds5.log, runs\ds5_n).
Đo trên 100 ảnh test (conf 0,25, IoU 0,5):
| Tập | Ảnh | Precision | Recall | mAP50 |
|---|---|---|---|---|
| Toàn bộ test | 100 | 0,968 | 0,823 | 0,879 |
| Nền đã gặp khi train (8 loại) | 50 | 0,999 | 0,999 | 0,995 |
| Nền lạ (stripes, grid, granite) | 50 | 0,929 | 0,673 | 0,709 |
Lưu ý: tập test cũng là tập val của ultralytics nên best.pt chọn theo chính nó (lệch nhẹ). Chỉ có 2 vật gốc trong test.
