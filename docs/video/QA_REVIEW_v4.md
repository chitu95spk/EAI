# QA độc lập video AutoLoop v4

Ngày kiểm: 08/11/2026. Chỉ đọc video, tài liệu và dữ liệu; không sửa app/video/dataset, không can thiệp tiến trình train. Chỉ ghi bằng chứng trong `.openclaw\tmp\qa-v4` và báo cáo này. Không tham khảo kết luận của người dựng.

**QA VERDICT: FAIL** — mất toàn bộ cảnh 8 trong MP4 giao, cảnh sinh/train là đồ họa thay cho thao tác app, không đạt yêu cầu 300 train/100 test và nền khác ở cảnh 3–4; còn lỗi Major về box, số cũ, che đường dẫn và nhạc.

## 1. Phạm vi và bằng chứng

- File nghiệm thu: `D:\CODE\EAI\artifacts\EAI-AUTOLOOP-VIDEO\EAI_AutoLoop_Agent_SAM_final_v4.mp4`.
- SHA-256: `ff57f1fa4a16d58028def020f0e42776f81c3b36dd3ba15e917ae502c68005c4`.
- Đã đọc đủ SCRIPT, PLAN và CHECKLIST cùng thư mục. SCRIPT v4 là chuẩn 8 cảnh/10 quy tắc. Có mâu thuẫn tài liệu: SCRIPT cho phép đồ họa cảnh 1–2 trong bảng nhưng QT1 viết “mọi cảnh”; CHECKLIST đổi cảnh 5 sang frame thật và cho phép đồ họa dữ liệu ở cảnh 6, trong khi SCRIPT vẫn yêu cầu quay app và synthetic 300+100. Áp dụng ngoại lệ rõ ràng cho cảnh 1–2; cảnh 5 không đạt theo cả hai hướng; cảnh 6 không đạt chuẩn SCRIPT. Không lấy mâu thuẫn làm lý do PASS.
- Trích **64 khung trực tiếp từ final**, đã xem từng ảnh bằng công cụ đọc ảnh; thêm 5 crop đã xem, tổng 69 PNG. Cảnh 1–6: mỗi cảnh 8 khung gồm đầu/cuối. Cảnh 7: 16 khung gồm đầu/cuối. **Cảnh 8 không có trong final nên không thể trích 4–6 khung; không lấy clip rời thay cho bằng chứng giao hàng.**
- Gốc khung **F** = `D:\CODE\EAI\.openclaw\tmp\qa-v4\frames\`. Tên `s07_extra_*` là các khung cuối cảnh 7, không phải cảnh 8. Danh mục: `..\frame-index.txt`; thông số: `..\ffprobe-final.json`, `..\final-scene-timing.json`; âm thanh: `..\loudness.json`, `..\audio-analysis.json`.
- Các script dựng chỉ dùng kiểm tra provenance, không coi caption/script là chứng cứ một tính năng đã chạy.

## 2. Nghiệm thu từng cảnh và thời lượng

| Cảnh | Khoảng final (phút:giây) | Thực tế / SCRIPT (s) | Kết quả, bằng chứng tiêu biểu trong F | Mức độ |
|---|---|---:|---|---|
| 1 Vấn đề | 00:00.000–00:09.133 | 9,133 / 6,9 | Đồ họa có nhãn Minh họa; đúng ý vấn đề. Bộ đếm 0/21 và thời gian bị đè chữ lúc đầu: `s01_00_t0000.000.png`; đúng bố cục về cuối `s01_06_t0008.677.png`. 60 phút là minh họa, không có log đo thao tác. | Minor |
| 2 Giải pháp | 00:09.133–00:18.267 | 9,133 / 6,9 | Logo/Agent/AI-Box SAM2/Auto loop, caption Việt + Anh: `s02_04_t0014.613.png`. Không ghi sai SAM3; đạt nội dung, lệch độ dài. | Minor |
| 3 Gán nhãn 1 ảnh | 00:18.267–00:47.967 | 29,700 / 22,9 | Có screenshot EasyAI thật, hộp và mask phủ đầu + thân một ốc; UI SAM2/397 ms: `s03_04_t0036.087.png`, `s03_05_t0042.027.png`. Nhưng đây là slideshow 7 PNG có blend/zoom, không phải clip liên tục; không có ảnh khác nền; thiếu phụ đề Anh. | Major về bằng chứng thao tác; Minor phụ đề |
| 4 Agent nhân rộng | 00:47.967–01:17.667 | 29,700 / 25,1 | Có app/chat và 19 đề xuất; sau đó chấp nhận tất cả. Chỉ cùng ảnh/nền của cảnh 3, không chuyển sang ảnh KHÁC NỀN. Box/nhãn chồng dày; không thấy duyệt xóa lỗi. Chat để mở suốt, lộ số cũ và đường dẫn: `s04_03_t0059.847.png`, `s04_06_t0076.182.png`. | Major |
| 5 Sinh dữ liệu | 01:17.667–01:40.533 | 22,867 / 22,9 | Đồ họa lưới/ảnh từ ổ đĩa, bộ đếm 600, không UI app/Agent gọi sinh. Chủ yếu ảnh gốc bị xoay/cắt và mảng đệm xám, không minh chứng tách từng ốc rồi hòa nền khó. `s05_02_t0082.240.png`, `s05_04_t0091.387.png`, `s05_06_t0099.390.png`. | Blocker |
| 6 Train | 01:40.533–02:05.667 | 25,133 / 25,1 | Đồ họa biểu đồ từ CSV, không màn hình train app. Ghi đúng 600/1000 ảnh, 120/180 epoch, chưa đạt; nhãn tua x921 ngoài dải x20–60 yêu cầu. `s06_03_t0110.587.png`, `s06_06_t0124.410.png`. | Major |
| 7 Detect | 02:05.667–02:39.967, hết file | 34,300 / 25,1 | App thật chạy đến summary 100 ảnh/579 phát hiện/56 ms; có đồng hồ không tua. Chỉ xem được 3 ảnh, rất nhiều vật sót; không có ví dụ phát hiện đủ trong các ảnh final. `s07_06_t0140.867.png`, `s07_extra_02_t0145.327.png`, `s07_extra_05_t0156.307.png`, `s07_extra_07_t0159.933.png`. | Major |
| 8 Kết quả | Không có | 0 / 16,0 | Final kết thúc khi vẫn đang ở UI detect; `s07_extra_07_t0159.933.png`. Cảnh rời `v4_scene8.mp4` có trên ổ nhưng bắt đầu ở 161,667 s trong bản silent, nằm ngoài final 159,967 s. | Blocker |

Tổng final **159,966667 s, 4.799 khung**, so với SCRIPT 150,9 s: dài hơn **9,067 s**, nhưng vẫn thiếu kết quả. Tổng các độ dài làm tròn ở bảng SCRIPT là 150,9 s; 66 ô nhịp chính xác ở 105 BPM là 150,857 s. Clip silent 179,966667 s/5.399 khung; final hụt đúng **600 khung = 20 s**, gồm 1,7 s cuối cảnh 7 và toàn bộ 18,3 s cảnh 8.

## 3. Kiểm tra đủ quy tắc 1–10

| QT | Kết quả | Bằng chứng và lý do | Mức độ nếu không đạt |
|---|---|---|---|
| 1 App thật, không dựng thay tính năng | Không đạt | C3 lấy screenshot app; C4/C7 lấy recording/screenshot app. C5/C6 là canvas PIL và dữ liệu/CSV, không thao tác app; `build_v4.py:93`, `:120`, khung 01:22.240 và 01:50.587. Không gọi toàn video là quay app. | Blocker |
| 2 Agent tự sinh/chia 300 train +100 test trong app | Không đạt | Run train thật có 600 rồi 1000 ảnh; validation chỉ 2 tile/5 vật. 100 ảnh detect lấy từ bộ ds5 khác. C5 không có Agent/Counter/chia tập. Tính tách biệt vật test với chính run cuối chưa kiểm chứng được. | Blocker |
| 3 Đa dạng số vật/tư thế/nền khó | Chưa đạt đầy đủ | Có xoay, sáng/tối; C7 có nền sọc/đen/nhiễu. Nhưng C5 chỉ trình bày biến đổi toàn cảnh, nhiều vùng đệm, không chứng minh train nhiều nền khó và nền lạ chỉ ở test so với run 600/1000. C3–4 không có nền khác. | Major |
| 4 Tách vật, hòa nền tự nhiên | Không đạt bằng chứng | C5 có đường biên hình chữ nhật/đệm xám lớn, không có bước tách vật hay khử viền/hòa màu/bóng tiếp xúc trong app. Không suy đoán pipeline đã thực hiện. `s05_02_t0082.240.png`, `s05_04_t0091.387.png`. | Major |
| 5 Train 300 ảnh thật, tua có nhãn/đồng hồ; detect100 trong app, không tua | Không đạt toàn bộ | Có train thật nhưng 600/1000, dựng lại biểu đồ x921 chứ không quay train; C7 có lượt detect100 thật và đồng hồ 0:00→0:34. | Major |
| 6 Không hộp thoại gửi dịch vụ AI | Đạt trong mẫu kiểm | Không thấy hộp thoại hay caption nhắc việc gửi dịch vụ ở toàn bộ 64 khung. Không khẳng định đã rà mọi frame 30 fps. | — |
| 7 Box đúng, không lệch/chồng; duyệt lỗi C3–4 | Không đạt | C3 mask một ốc đạt bằng mắt. C4 nhiều OBB/nhãn chồng ở cụm ốc, chỉ thấy Chấp nhận tất cả, không cảnh sửa/xóa. Vật thật có chồng nhau nên không quy mọi giao box là duplicate sai, nhưng không đạt yêu cầu hình ảnh sạch/duyệt lỗi. `crop_s04_06_boxes.png`. C7 số sót rất lớn, bảng dưới. | Major |
| 8 Caption đúng SAM2/SAM3 | Đạt | Caption C2–4 ghi SAM2, UI báo SAM2; không dùng nút SAM3 có sẵn trên toolbar làm bằng chứng provider. `provider-check.txt` vẫn ghi CHƯA kiểm SAM3 trong app, video không tuyên bố SAM3. | — |
| 9 Chỉ số lần chạy cuối, không số cũ | Không đạt | C5–6 số mới khớp nguồn, nhưng chat C4 vẫn hiện run cũ “300 ảnh/40 epoch/mAP50 0.995/đạt”; `s04_00_t0047.967.png`, `s04_04_t0065.787.png`. Bảng kết quả mới C8 không được giao. | Major |
| 10 Che đường dẫn, ẩn Sticky Notes, chat đúng lúc, khóa độ dài rồi nhạc | Không đạt | C3 title bar lộ `C:\Demo\LucGiac`; C4 title/chat lộ đường dẫn run và chat mở suốt; nhạc ngắn hơn video. Không thấy email/key/Sticky Notes/cửa sổ ngoài app trong khung mẫu. | Major |

Chỉ QT6 và QT8 đạt trong phạm vi chứng cứ; không đủ điều kiện PASS mọi QT1–10.

## 4. Danh sách lỗi

| ID | Cảnh | Mốc giờ | Quy tắc/chuẩn | Bằng chứng | Mức độ |
|---|---|---|---|---|---|
| V4-01 | 8 | 02:39.967, EOF | Bảng cảnh 8; QT9 | Final dừng ở detect, `s07_extra_07_t0159.933.png`; ffprobe 159,967 s, silent 179,967 s. Không có bảng mAP/recall/precision/kết luận. | Blocker |
| V4-02 | 5 | 01:17.667–01:40.533 | QT1,2 | `s05_00_t0077.667.png`–`s05_07_t0100.500.png`; lưới/counter dựng bằng `Image.new`/`im.paste`, không sinh/chia tập trong app. | Blocker |
| V4-03 | 5–6 | 01:39.390; 02:04.410 | QT2,5 | 600/1000 thay 300, validation2 tile/5 vật thay100 ảnh test; test100 là ds5 riêng. | Major |
| V4-04 | 3–4 | 00:42.027; 01:16.182 | Yêu cầu KHÁC NỀN; QT3, bảng C4 | Cùng ảnh `Cam_20261019_160350_387.bmp`/cùng nền xám; không lan truyền sang ảnh/nền khác. | Major |
| V4-05 | 4 | 01:11.727–01:17.633 | QT7; checklist C4 | `s04_05_t0071.727.png`, `crop_s04_06_boxes.png`; chấp nhận toàn bộ, không duyệt xóa, OBB/nhãn chồng. | Major |
| V4-06 | 5 | 01:22.240; 01:31.387 | QT3,4 | `s05_02_t0082.240.png`, `s05_04_t0091.387.png`; nền gốc xoay/đệm xám, không bằng chứng tách vật/hòa nền tự nhiên. | Major |
| V4-07 | 6 | 01:50.587–02:05.633 | QT1,5; bảng C6 | `s06_03_t0110.587.png`, `s06_07_t0125.633.png`; CSV dựng thành đồ họa, tua x921, không quay train x20–60. | Major |
| V4-08 | 4 | 00:47.967–01:16.182 | QT9 | Chat đọc được mAP0.995 và “đạt” của run cũ; `s04_04_t0065.787.png`. | Major |
| V4-09 | 3–4 | 00:18.267; 00:59.847 | QT10 | Title bar `C:\Demo\LucGiac`, chat đường dẫn run; `s03_00_t0018.267.png`, `s04_03_t0059.847.png`. | Major |
| V4-10 | 7 | 02:25.327; 02:36.307; 02:39.052 | Chuẩn kết quả C7/QT7 | Preview20 ốc/1box, 25ốc/10box, ≥28ốc/0box; ba crop bảng dưới. Không có ví dụ đếm đủ như caption gợi ý. | Major |
| V4-11 | Nhạc/cắt | 02:28.571–02:39.967 | QT10; PLAN B6 | Audio kết thúc148,571 s, video159,967 s: thiếu11,395 s nhạc. Nhạc vẫn dùng bố cục7 cảnh cũ. | Major |
| V4-12 | 3 | 00:18.267–00:47.933 | Brief quay thật; Việt + Anh | `build_scene3v3.py:17–31` ghép7 screenshot/blend, không recording liên tục; caption chỉ Việt `crop_s03_caption.png`. | Major về chứng cứ thao tác; Minor phụ đề |
| V4-13 | 1 | 00:00.000; 00:07.307 | Chất lượng chữ | `s01_00_t0000.000.png`, `s01_05_t0007.307.png`; bộ đếm/thời gian đè nhau một số khung. | Minor |
| V4-14 | 1–4,7–8 | Các điểm cắt | Bảng thời lượng SCRIPT | Bảng mục2; lệch nhiều cảnh, không chốt lại với8 cảnh/66 ô nhịp. | Minor riêng lẻ; hệ quả V4-01/V4-11 là lỗi nặng |

## 5. Đối chiếu số liệu với nguồn thật

Gốc **R** = `C:\Demo\LucGiac.autoloop\run-20261107-135823`; gốc **A** = `D:\CODE\EAI\artifacts\EAI-AUTOLOOP-VIDEO`.

| Số hiển thị / tuyên bố | Nguồn thật | Kết quả |
|---|---|---|
| 600 ảnh/2.204 nhãn vòng0; 1.000 ảnh/3.637 nhãn vòng1 | `R\autoloop.log`, đếm `r0/r1\data\images\train`; `report.json` | Khớp run; **lệch brief 300**. Không nói số600/1000 là bịa. |
| 120/180 epoch | `R\r0/r1\train\results.csv`,120/180 dòng | Khớp; tổng300 epoch, không phải300 ảnh. |
| 1:28:24 và3:45:13; tổng5:13:38 | CSV thời gian cuối5304,27 và13513,8 s | Khớp làm tròn tổng18818,07 s. Đây là tổng thời gian train CSV, không toàn pipeline; log toàn run5:16:01. |
| C6 kết quả r0 mAP0,831/R1,00/P0,499; r1 mAP0,862/R1,00/P0,551 | `R\report.json`: r0=0,8307142857/1/0,4986448274; r1=0,8616666667/1/0,5507377903 | Khớp làm tròn. Đây là đánh giá weights chọn trên5 vật/2tile, **không phải mAP trên100 ảnh detect**. |
| Đồ thị cuối r0=0,740; r1=0,789 | CSV epoch cuối0,7404/0,78944 | Khớp. Khác report do cuối epoch vs weights chọn, không tự gắn là số sai. Cần nhãn giải thích rõ trong bản sau. |
| “Chưa đạt mục tiêu” ởC6 | mAP tốt nhất0,8616667<0,95; recall1≥0,98; log `max rounds per cycle reached` | Đúng. Report không có trường `achieved`; không tự tạo trường đó. |
| 100 ảnh,579 vật,56ms/ảnh | `A\real-20261107h\detect_summary.json`, `driver.log`; UI02:25.327 | Khớp summary thật. 579 là **số phát hiện/boxes**, không chứng minh579 vật đúng. Không có JSON detections từng ảnh của lượt này để đối chiếu toàn bộ. |
| Model detect từ run cuối | summary chọn `R\best.pt`; hash bằng `R\r1\train\weights\best.pt` | Khớp; SHA256 `61603761c2201ad6c61fd6a276ee644801cac3f4e8e08112afa3e4c05b65fd07`. |
| Nguồn100 ảnh test | `C:\Demo\LucGiac_test100`:100JPG, hash100/100 trùng `D:\CODE\EAI\tmp\synth\ds5\images\val`; `ds5\gen_meta.json`, nhãn tổng1505 vật | Là100 **ảnh tổng hợp** từ cùng ảnh nguồn, không100ảnh camera độc lập. Có lượt inference thật trong app. Meta ds5 có disjoint đối với train300 của ds5; không tự suy ra disjoint với train600/1000 củaR. |
| mAP0,995/300ảnh/40epoch/đạt trong chatC4 | Run cũ hiển thị trong chat, không phảiR | Vi phạmQT9; số cũ vẫn nằm trong video dù caption chính sau đó trung thực. |
| 19đề xuất/$0,102/5lượt/33giây C4 | UI app và caption khớp nhau | Chưa kiểm chứng độc lập bằng `mcp.log` đúng lượt; không dùng số lượt này để bảo chứng provider/độ chính xác. |
| 397ms/SAM2 C3 | UI `SAM2 trên DirectML:1 đề xuất trong397ms`, screenshot36,087s | Khớp UI; không bằng chứng SAM3. |

Không trộn `det7.json`/`det7n.json` vào v4: đó là model cũ, một ảnh chạy3 kích thước (19/19/19 và20/21/20 boxes), không phải lượt100ảnh củaR. Không suy recall=579/1505 vì chưa có matching TP/FP và bộ nhãn được app đánh giá.

### Đếm bằng mắt các ảnh thật sự hiển thị ở cảnh7

Đã zoom crop trực tiếp từ final; đếm vật nhìn thấy, kể cả vật bị cắt ở rìa. Không suy số vật từ con số vàng (đó là sốbox).

| Ảnh/UI; thời gian | Vật nhìn bằng mắt | Box thấy | Sót nhìn thấy | Box sai/quá lớn/chồng | Bằng chứng F |
|---|---:|---:|---:|---|---|
| test_00000, nền sọc;02:25.327 | 20 | 1 | 19 | Không thấy false positive hayboxquálớn/chồng trong1box hiện; box ômốc sát mép trái | `crop_s07_extra_02_t0145.327.png` |
| test_00001, nền đen;02:36.307 | 25 | 10 | 15 | 10box có vật, không thấy boxquálớn hoặcboxchồng rõ; nhiềuốc ngoài mọi box | `crop_s07_extra_05_t0156.307.png` |
| test_00002, nền nhiễu;02:39.052 | **Ít nhất28 phân biệt được**, vàiốc dính/rìa không đủ rõ để chốt31 bằng mắt | 0 | **Ít nhất28**; nhãn nguồn có31 | Khôngbox nên không cófalsepositive; thất bại phát hiện toànảnh | `crop_s07_extra_06_t0159.052.png` |

Đếm file nhãn ds5 độc lập lần lượt **20/25/31**. Chênh ≥28 mắt vs31nhãn ởảnhnhiễu được giữ nguyên, không biến sốnhãn thành “đã đếm mắt31”. Preview nhỏ/mờ/cắt rìa giới hạn kiểm chứng. Cảnh4 có nhiều box giao nhau thật; không đủ bằng chứng nói chúng đều làboxduplicate hayboxsai.

## 6. Nhạc, hình và nội dung còn thiếu

| Hạng mục | Kết quả |
|---|---|
| Hình xuất | H.264/yuv420p, 1920×1080, 30 fps: đạt. Screenshot app bị thu nhỏ; preview detect khoảng 440×350 px, chữ chat nhỏ khó đọc; zoom C3 mềm do phóng ảnh chụp. |
| Tiếng Việt | Không thấy lỗi font/mất dấu trong caption đã xem. C1 có đè chữ; C3 thiếu tiếng Anh. Không thấy caption chính cắt ngoài khung. |
| Email/API key/Sticky Notes/cửa sổ lạ | Không thấy trong 64 khung; **không đồng nghĩa đã rà hết 4.799 khung**. Đường dẫn chắc chắn lộ C3–4; C7 đã blur ô đường dẫn. |
| 105 BPM | `music\gen_music.py` đặt 105 BPM/4⁄4; audio final 10–20 s tương quan 0,99878 với `bgm_105bpm.mp3` cùng đoạn, phù hợp mux không đổi tốc. Ước lượng onset RMS thô không ổn định, không coi là phép đo BPM độc lập. |
| Âm lượng | ffmpeg loudnorm đo **−16,28 LUFS**, true peak **−3,12 dBTP**, LRA 2,90 LU: trong khoảng −18 đến −14 LUFS, không có dấu clipping theo peak. Chưa nghe kiểm âm sắc/trải nghiệm bằng tai. |
| Đồng bộ cắt | Biên 9,133/18,267/47,967/77,667/100,533/125,667 s gần phách 1 ở 105 BPM (sai ≤0,048 s). Nhưng đổi cấu trúc nhạc C5/C6/C7 vẫn theo bố cục cũ, gồm biên 98,286/128 s, lệch cảnh mới 100,533/125,667 s. **11,395 s cuối không nhạc.** |
| Điểm sót | Không nền khác C3–4; không Agent/Counter sinh và chia 300+100 trong app; không tách vật/hòa nền; không duyệt xóa box lỗi; không train UI; không ví dụ detect đủ trong 3 ảnh hiện; không C8; không giải thích trong final rằng mAP chỉ trên 5 vật/2 tile và 100 ảnh detect là synthetic, chưa được tính mAP. |

## 7. Nguyên nhân gốc và bài học rút kinh nghiệm

1. **Đặc tả chưa thống nhất:** SCRIPT/PLAN/CHECKLIST lệch 7/8 cảnh, synthetic/frame thật, 300/600/1000 và độ dài. Cần một bảng nghiệm thu versioned, ghi ngoại lệ được duyệt trước quay; QA không tự hợp thức hóa ngoại lệ.
2. **Đồng nhất dữ liệu thật với tính năng đã chạy trong app:** CSV/ảnh trên ổ không chứng minh Agent gọi sinh/train trong app. Giữ raw clip và run ID cho chuỗi prompt → tool → dữ liệu → train → detect. Slideshow phải ghi rõ ảnh chụp, không thay bằng chứng thao tác.
3. **Lỗi mux, không kiểm file giao:** `assemble_v4.py` dùng `-shortest` với nhạc 65 ô/148,571 s trong khi silent 179,967 s. Final thực tế hụt 20 s và mất C8. Khóa độ dài cảnh, dựng nhạc khớp, sau mux kiểm cả audio/video bằng ffprobe và xem đầu/cuối **chính file giao**.
4. **Provenance không đi cùng tuyên bố:** Phân biệt train 600/1000, validation 5 vật/2 tile, test 100 synthetic và run chat cũ. Gắn số liệu với run/model/dataset/hash; lưu detection từng ảnh và matching TP/FP/FN. Không gọi số box là số vật đúng hoặc áp recall 1,0 cho 100 ảnh chưa đánh giá.
5. **Thiếu cổng duyệt hình:** Kiểm mask đầu/thân, box chồng, ảnh khác nền, ảnh khó và số sót trước quay. Không đạt thì sửa pipeline; không che bằng slideshow/caption. Có người đếm mắt độc lập.
6. **Vệ sinh màn hình/phụ đề thiếu:** Chat/project sạch, đóng chat khi không cần; che cả title và chat; Việt + Anh ở mọi cảnh. Kiểm ở độ phân giải xuất và kích thước xem thật để phát hiện đè chữ/preview quá nhỏ.

**QA VERDICT: FAIL** — còn Blocker V4-01/V4-02 và nhiều Major; chưa đạt mọi QT1–10. Lượt train/detect có thật, nhưng bản video giao chưa đáp ứng kịch bản và chất lượng nghiệm thu.
