# Kết luận nhanh - thí nghiệm decode (Lane C, 2026-11-11)

**Không xuất `decode.patch`.** Thử 70 cấu hình chỉ trên tune/ov_tune, khóa đúng một cấu hình rồi chạy final đúng một lần. Tinh chỉnh tâm cải thiện IoU nhưng chưa chứng minh cải thiện đếm: final100 không tăng; ov_final vẫn kém baseline 0.25; ảnh thật đếm 21 thay vì 20. Không train, không sửa mã nguồn production, không commit/push.

## Phân loại FN đã khử nhầm lẫn giữa mất đỉnh và lệch hộp

L = đã có đỉnh được giữ, nhưng hộp không đạt IoU 0.5; không thể trung thực ép trường hợp này vào (i)/(ii)/(iii). Bảng đầu tiên bên dưới là phân loại sơ bộ theo regression-IoU, đã được bảng không gian này hiệu chỉnh. Ngưỡng đỉnh có ý nghĩa cho chẩn đoán là 0.05.

|Tập / ngưỡng|(i) NMS/đáp ứng gộp|(ii) dưới ngưỡng|(iii) không có đỉnh riêng|L: đỉnh đã giữ nhưng lệch hộp|Tổng FN|
|---|---:|---:|---:|---:|---:|
|tune @0.25|4|0|1|22|27|
|tune @0.325|4|1|1|21|27|
|ovtune @0.25|42|13|9|54|118|
|ovtune @0.325|32|31|19|50|132|

|Tập / ngưỡng|FP trên ốc bị lệch hộp|Trùng/bridge trên ốc đã khớp|Nền hoặc đáp ứng ngoài GT|
|---|---:|---:|---:|
|tune @0.25|23|3|6|
|tune @0.325|21|2|4|
|ovtune @0.25|52|4|1|
|ovtune @0.325|46|2|0|

## Cấu hình khóa và trước/sau

Ứng viên: peak 3x3; tâm quadratic trên logit heatmap, alpha=1, delta từng trục kẹp [-0.5,+0.5] ô; rotated-IoU NMS 0.5; ngưỡng 0.30. Không đổi w/h/góc. **Chỉ là ứng viên thí nghiệm, không được khuyến nghị đưa vào production.**

|Tập|Baseline @0.25: exact; TP/FP/FN|Baseline @0.325: exact; TP/FP/FN|Ứng viên khóa: exact; TP/FP/FN|
|---|---|---|---|
|tune|33/40; 615/32/27|36/40; 615/27/27|36/40; 631/13/11|
|ovtune|14/40; 523/57/118|11/40; 509/48/132|14/40; 526/43/115|
|final|85/100; 1232/63/61|84/100; 1226/53/67|84/100; 1255/28/38|
|ovfinal|22/60; 804/96/224|16/60; 789/83/239|20/60; 830/54/198|
|real|1/1; 17/3/3|0/1; 16/3/4|0/1; 19/2/1|

Baseline final lấy từ JSON lịch sử, không chạy lại. Ứng viên được đánh giá đúng một lần sau khóa. Final100 đã được xem qua nhiều vòng nên không còn holdout sạch; ảnh thật dùng chung bốn ốc nguồn với train, chỉ dùng chẩn đoán. Tính toán lần này dùng graph OpenVINO CPU FP32 dựng đúng theo source vì native Load bị treo. Tune tái lập đúng các TP/FP/FN baseline; raw peak ảnh thật khớp vị trí native lịch sử trong 0.000017 px network. Chưa chứng minh parity toàn bộ tensor CUDA.

Paired exact-count so với baseline @0.325: {"0.25_final": {"wins": 5, "losses": 6, "mcnemar_exact_p": 1.0}, "0.25_ovfinal": {"wins": 2, "losses": 4, "mcnemar_exact_p": 0.6875}, "0.25_real": {"wins": 0, "losses": 1, "mcnemar_exact_p": 1.0}, "0.325_final": {"wins": 3, "losses": 3, "mcnemar_exact_p": 1.0}, "0.325_ovfinal": {"wins": 5, "losses": 1, "mcnemar_exact_p": 0.21875}, "0.325_real": {"wins": 0, "losses": 0, "mcnemar_exact_p": 1.0}}. Không có bằng chứng count gain vượt nhiễu.

## Các ốc người dùng chỉ trên ảnh thật

G8 là ốc dựng đứng ở giữa ảnh: **đỉnh 0.387430 đã qua peak-NMS và được giữ**, không phải (i)/(ii)/(iii). G12 và G15 trong cụm dưới-giữa cũng có đỉnh được giữ; đây là ba FN do IoU/định vị hộp của baseline @0.25. Hạ ngưỡng hay nới NMS không tạo thêm ba ốc thật này.

|Ốc|Đỉnh heatmap|IoU hộp gốc|IoU ứng viên tại cùng đỉnh|Kết luận baseline @.25|
|---|---:|---:|---:|---|
|G8|0.387430|0.4350|0.5861|L: đỉnh đã giữ, hộp lệch|
|G12|0.463908|0.4805|0.5144|L: đỉnh đã giữ, hộp lệch|
|G15|0.373998|0.4949|0.5330|L: đỉnh đã giữ, hộp lệch|

G13 trong cụm dưới có đỉnh **0.307191**: giữ được tại 0.25, thuộc (ii) tại 0.325. Các ốc cạnh đó: G14 0.475623, G16 0.332976, G17 0.434737, G9 0.509962, G10 0.421126, G18 0.435200 đều được giữ tại 0.325. Ốc dựng đứng tách riêng bên trái giữa G3 có đỉnh 0.544674 và đã đếm được. Bảng đầy đủ 20 ốc, tọa độ và phân loại nằm trong phần evidence phía dưới; ảnh `real_annotated.jpg` và `real_heatmap.jpg` ở thư mục exp-decode.

## Hiệu năng và điều chưa chứng minh

C++ standalone từ bản sao source, MSVC v143 Release x64: decode p50 **0.12175 -> 0.12595 ms** (+0.00420 ms); p95 0.68153 -> 0.65393 ms. 80 ảnh tune, mỗi ảnh 20 vòng warm; không chạy GPU song song. Python/C++ không có lệch số lượng trên 80 ảnh ở cả hai mode; sai khác hình học tối đa 0.000032 px. Build pass, một warning MSB8029 vì TEMP đặt trong project. Không bao gồm sigmoid/unletterbox/inference; không phải benchmark end-to-end production.

Đa số FN không phải (iii), vì nhiều FN là cặp lệch hộp/FP tương ứng. Tuy nhiên mất đỉnh độc lập hoặc đáp ứng nhập trong ảnh dày vẫn chưa được hậu xử lý giải quyết ổn định; bỏ peak-NMS làm tăng FP mạnh. Ưu tiên thêm ốc nguồn độc lập, ốc dựng đứng và ảnh dày/chồng lấn có nhãn; không ép thêm count bằng hạ ngưỡng/nới NMS. Không chứng minh generalization sang nguồn mới, production/CUDA end-to-end, hoặc lợi ích count có ý nghĩa thống kê. Không tạo patch vì lợi ích đếm chưa đạt.

---

## Nhật ký và evidence thí nghiệm (ghi sau từng thí nghiệm)
# Decode experiment - Lane C

## Scope and progress

2026-11-11: No training; repository source untouched; scratch in tmp/EAI-SEGAI-CUDA/exp-decode. C:/Demo junction is absent in this runtime; read D:/Demo directly. Existing scratch scripts found but no tensors/results; starting fresh. Tune-only configuration selection; final sets evaluated once after lock. Final100 has been repeatedly viewed and is not clean holdout; real image shares four source screws with train, diagnostic only.

### Environment probe
- The existing pyenv Python 3.14 stalled before first print, with ~63 MB resident memory and one thread; stopped the empty first dump. No data produced and no model train occurred. System Python imports NumPy/OpenCV immediately; restarted raw-tensor dump with system Python.

### Runtime startup workaround
Python site bootstrap is intermittent/stalls (including venv); all further helper commands use python -S with explicit site-packages, avoiding .pth bootstrap. Raw dump now passed NumPy/OpenCV imports and is loading native DLL. No final-set evaluation performed.

Raw tensor dump tune: 40 images, shape (9, 80, 80), OpenVINO CPU FP32 source-equivalent graph (native model load stalled). Saved successfully; no training.

### Successful runtime workaround and important tensor layout correction
Native Load remained stalled inside InferLoad. Python OpenVINO Core/read_model works. Reconstructed the exact BuildInstanceNetwork graph from embedded network_xml/network_bin and instance weights, CPU FP32, one stream/thread. cv2.imread was also stalling (faulthandler confirmed line 31); using Path.read_bytes + cv2.imdecode fixes it. Model has TWO heatmap channels plus seven regression channels (9 total), symmetry k=2 for both classes. Existing scratch lib incorrectly assumed one heat channel/k=1; corrected the scratch-only decoder. Class 0 is counted, matching seg_native.py. Baseline parity to historical metrics remains to be checked; no claims of CUDA numerical identity.

Raw tensor dump ovtune: 40 images, shape (9, 80, 80), OpenVINO CPU FP32 source-equivalent graph (native model load stalled). Saved successfully; no training.

## FN / FP raw-heat classification

```text
== tune: model sha 874cec6995c8 dll sha not-used-nat dumped 2026-11-11 10:36:12
PARITY native unavailable: legacy native model Load stalled. Source-equivalent FP32 graph used; baseline count check required.
-- tune thr=0.25: exact 33/40 TP/FP/FN 615/32/27 (gt 642) count errors {'count_err=+1': 4, 'count_err=-1': 2, 'count_err=+3': 1}
   FN: i_b_window3x3_swallowed=22, o_detected_but_iou_lt_0.5=5
   FP: background=2, dup_or_bridge_on_matched_screw=4, offset_on_missed_screw(pairs_with_FN)=26
-- tune thr=0.325: exact 36/40 TP/FP/FN 615/27/27 (gt 642) count errors {'count_err=-1': 3, 'count_err=+3': 1}
   FN: i_b_window3x3_swallowed=20, iii_a_weak_shoulder_no_own_peak=1, o_detected_but_iou_lt_0.5=6
   FP: dup_or_bridge_on_matched_screw=3, offset_on_missed_screw(pairs_with_FN)=24
== ovtune: model sha 874cec6995c8 dll sha not-used-nat dumped 2026-11-11 10:36:40
PARITY native unavailable: legacy native model Load stalled. Source-equivalent FP32 graph used; baseline count check required.
-- ovtune thr=0.25: exact 14/40 TP/FP/FN 523/57/118 (gt 641) count errors {'count_err=-3': 4, 'count_err=-2': 7, 'count_err=-1': 7, 'count_err=+1': 3, 'count_err=-10': 1, 'count_err=-4': 2, 'count_err=-5': 1, 'count_err=-8': 1}
   FN: i_a_nms_iou_swallowed=1, i_b_window3x3_swallowed=48, ii_peak_below_thr=6, iii_a_weak_shoulder_no_own_peak=10, iii_b_heat_ge_thr_but_no_usable_box=14, iii_c_weak_heat_no_usable_box=3, o_detected_but_iou_lt_0.5=34, x_kept_det_took_other_gt=2
   FP: background=2, dup_iou_ge_0.5_on_matched_screw=1, dup_or_bridge_on_matched_screw=3, offset_on_missed_screw(pairs_with_FN)=51
-- ovtune thr=0.325: exact 11/40 TP/FP/FN 509/48/132 (gt 641) count errors {'count_err=-6': 2, 'count_err=-3': 4, 'count_err=-1': 11, 'count_err=-2': 6, 'count_err=-4': 3, 'count_err=-10': 1, 'count_err=-7': 1, 'count_err=-8': 1}
   FN: i_b_window3x3_swallowed=33, ii_peak_below_thr=21, iii_a_weak_shoulder_no_own_peak=20, iii_b_heat_ge_thr_but_no_usable_box=12, iii_c_weak_heat_no_usable_box=5, o_detected_but_iou_lt_0.5=39, x_kept_det_took_other_gt=2
   FP: dup_iou_ge_0.5_on_matched_screw=1, dup_or_bridge_on_matched_screw=1, offset_on_missed_screw(pairs_with_FN)=46

```

## Tune-only experiments

Frozen raw tensors, original greedy rotated-IoU >=0.5 matching; all configurations below selected only from tune40 and ov_tune40. Native duplicate suppression already uses rotated IoU, not centre distance.

|Configuration|tune exact, TP/FP/FN|ov_tune exact, TP/FP/FN|Python total ms|
|---|---|---|---|
|base_t0.1|20/40, 615/71/27|8/40, 529/149/112|343.0|
|base_t0.125|26/40, 615/58/27|9/40, 529/110/112|8.5|
|base_t0.15|27/40, 615/48/27|11/40, 528/85/113|8.3|
|base_t0.175|31/40, 615/41/27|13/40, 528/72/113|8.9|
|base_t0.2|32/40, 615/37/27|15/40, 528/65/113|8.8|
|base_t0.225|33/40, 615/34/27|15/40, 527/60/114|7.9|
|base_t0.25|33/40, 615/32/27|14/40, 523/57/118|8.0|
|base_t0.275|35/40, 615/30/27|13/40, 520/56/121|8.2|
|base_t0.3|36/40, 615/29/27|12/40, 514/53/127|8.1|
|base_t0.325|36/40, 615/27/27|11/40, 509/48/132|8.4|
|base_t0.35|31/40, 610/26/32|7/40, 497/42/144|8.0|
|base_t0.375|23/40, 595/24/47|6/40, 484/38/157|8.7|
|base_t0.4|14/40, 578/21/64|4/40, 454/33/187|7.3|
|base_t0.45|3/40, 485/16/157|1/40, 334/18/307|5.9|
|base_t0.5|0/40, 328/12/314|0/40, 164/6/477|3.6|
|peak_r0_square_t0.2|10/40, 629/274/13|6/40, 566/391/75|1155.6|
|peak_r0_square_t0.25|14/40, 629/199/13|9/40, 557/263/84|17.3|
|peak_r0_square_t0.325|19/40, 626/129/16|13/40, 529/144/112|13.0|
|peak_r1_plus_t0.2|23/40, 615/63/27|17/40, 538/122/103|15.9|
|peak_r1_plus_t0.25|27/40, 615/52/27|17/40, 533/101/108|10.9|
|peak_r1_plus_t0.325|30/40, 615/40/27|13/40, 517/72/124|8.6|
|peak_r2_square_t0.2|30/40, 606/27/36|10/40, 474/36/167|34.7|
|peak_r2_square_t0.25|30/40, 606/25/36|9/40, 471/34/170|7.9|
|peak_r2_square_t0.325|31/40, 606/22/36|8/40, 468/32/173|7.3|
|nms0.3_t0.2|33/40, 615/34/27|13/40, 523/59/118|8.1|
|nms0.3_t0.25|34/40, 615/29/27|12/40, 518/53/123|8.5|
|nms0.3_t0.325|36/40, 615/25/27|10/40, 505/45/136|7.4|
|nms0.4_t0.2|32/40, 615/36/27|14/40, 527/60/114|8.6|
|nms0.4_t0.25|33/40, 615/31/27|14/40, 522/53/119|7.6|
|nms0.4_t0.325|36/40, 615/26/27|11/40, 509/45/132|7.4|
|nms0.6_t0.2|30/40, 615/39/27|13/40, 529/68/112|12.3|
|nms0.6_t0.25|31/40, 615/34/27|13/40, 524/60/117|12.9|
|nms0.6_t0.325|34/40, 615/29/27|11/40, 509/51/132|11.3|
|nms0.7_t0.2|27/40, 615/45/27|11/40, 529/70/112|10.0|
|nms0.7_t0.25|28/40, 615/38/27|13/40, 524/62/117|11.6|
|nms0.7_t0.325|32/40, 615/32/27|10/40, 509/52/132|8.3|
|nms0.8_t0.2|27/40, 615/46/27|11/40, 529/70/112|10.7|
|nms0.8_t0.25|28/40, 615/39/27|13/40, 524/62/117|11.8|
|nms0.8_t0.325|32/40, 615/33/27|10/40, 509/52/132|8.5|
|nms1.0_t0.2|27/40, 615/46/27|11/40, 529/70/112|8.2|
|nms1.0_t0.25|28/40, 615/39/27|13/40, 524/62/117|8.1|
|nms1.0_t0.325|32/40, 615/33/27|10/40, 509/52/132|7.5|
|density_low0.15_d0.6|27/40, 615/47/27|11/40, 528/83/113|9.9|
|density_low0.15_d1.0|27/40, 615/47/27|11/40, 528/83/113|8.9|
|density_low0.2_d0.6|32/40, 615/37/27|15/40, 528/65/113|8.5|
|density_low0.2_d1.0|32/40, 615/37/27|15/40, 528/65/113|9.1|
|density_low0.225_d0.6|33/40, 615/34/27|15/40, 527/60/114|9.3|
|density_low0.225_d1.0|33/40, 615/34/27|15/40, 527/60/114|8.2|
|density_low0.25_d0.6|33/40, 615/32/27|14/40, 523/57/118|8.6|
|density_low0.25_d1.0|33/40, 615/32/27|14/40, 523/57/118|8.1|
|soft_linear_t0.15|26/40, 615/49/27|11/40, 528/87/113|11.4|
|soft_linear_t0.2|32/40, 615/37/27|15/40, 528/65/113|11.7|
|soft_linear_t0.25|33/40, 615/32/27|14/40, 523/57/118|12.0|
|soft_linear_t0.325|36/40, 615/27/27|11/40, 509/48/132|12.0|
|soft_gaussian_t0.15|24/40, 615/50/27|11/40, 528/81/113|13.1|
|soft_gaussian_t0.2|34/40, 615/34/27|17/40, 527/60/114|11.3|
|soft_gaussian_t0.25|34/40, 615/30/27|12/40, 519/52/122|10.8|
|soft_gaussian_t0.325|36/40, 614/24/28|10/40, 501/41/140|12.2|

Tune-only best global threshold: {"name": "base_t0.3", "thr": 0.3, "nms": 0.5}; best FP-constrained candidate: {"name": "base_t0.3", "thr": 0.3, "nms": 0.5}. Final sets remain unopened at selection.


EXTRA {"cfg": {"name": "nopeak_nms0.2_t0.25", "thr": 0.25, "nms": 0.2, "radius": 0}, "tune": {"exact": 33, "tp": 617, "fp": 33, "fn": 25}, "ovtune": {"exact": 12, "tp": 513, "fp": 56, "fn": 128}}

EXTRA {"cfg": {"name": "nopeak_nms0.2_t0.325", "thr": 0.325, "nms": 0.2, "radius": 0}, "tune": {"exact": 36, "tp": 617, "fp": 26, "fn": 25}, "ovtune": {"exact": 11, "tp": 500, "fp": 44, "fn": 141}}

EXTRA {"cfg": {"name": "nopeak_nms0.3_t0.25", "thr": 0.25, "nms": 0.3, "radius": 0}, "tune": {"exact": 28, "tp": 619, "fp": 55, "fn": 23}, "ovtune": {"exact": 15, "tp": 536, "fp": 92, "fn": 105}}

EXTRA {"cfg": {"name": "nopeak_nms0.3_t0.325", "thr": 0.325, "nms": 0.3, "radius": 0}, "tune": {"exact": 32, "tp": 617, "fp": 38, "fn": 25}, "ovtune": {"exact": 13, "tp": 514, "fp": 58, "fn": 127}}

EXTRA {"cfg": {"name": "nopeak_nms0.4_t0.25", "thr": 0.25, "nms": 0.4, "radius": 0}, "tune": {"exact": 22, "tp": 625, "fp": 97, "fn": 17}, "ovtune": {"exact": 15, "tp": 544, "fp": 141, "fn": 97}}

EXTRA {"cfg": {"name": "nopeak_nms0.4_t0.325", "thr": 0.325, "nms": 0.4, "radius": 0}, "tune": {"exact": 27, "tp": 621, "fp": 58, "fn": 21}, "ovtune": {"exact": 13, "tp": 522, "fp": 79, "fn": 119}}

EXTRA {"cfg": {"name": "subpixel_a0.25_t0.25", "thr": 0.25, "nms": 0.5}, "tune": {"exact": 33, "tp": 621, "fp": 26, "fn": 21}, "ovtune": {"exact": 14, "tp": 533, "fp": 47, "fn": 108}}

EXTRA {"cfg": {"name": "subpixel_a0.25_t0.3", "thr": 0.3, "nms": 0.5}, "tune": {"exact": 36, "tp": 621, "fp": 23, "fn": 21}, "ovtune": {"exact": 12, "tp": 524, "fp": 43, "fn": 117}}

EXTRA {"cfg": {"name": "subpixel_a0.25_t0.325", "thr": 0.325, "nms": 0.5}, "tune": {"exact": 36, "tp": 621, "fp": 21, "fn": 21}, "ovtune": {"exact": 11, "tp": 519, "fp": 38, "fn": 122}}

EXTRA {"cfg": {"name": "subpixel_a1.0_t0.25", "thr": 0.25, "nms": 0.5}, "tune": {"exact": 33, "tp": 631, "fp": 16, "fn": 11}, "ovtune": {"exact": 14, "tp": 534, "fp": 48, "fn": 107}}

EXTRA {"cfg": {"name": "subpixel_a1.0_t0.3", "thr": 0.3, "nms": 0.5}, "tune": {"exact": 36, "tp": 631, "fp": 13, "fn": 11}, "ovtune": {"exact": 14, "tp": 526, "fp": 43, "fn": 115}}

EXTRA {"cfg": {"name": "subpixel_a1.0_t0.325", "thr": 0.325, "nms": 0.5}, "tune": {"exact": 36, "tp": 630, "fp": 12, "fn": 12}, "ovtune": {"exact": 12, "tp": 520, "fp": 39, "fn": 121}}

Additional tune-only tests complete. Selection remains conservative: no source change; native 3x3 peak, rotated-IoU NMS 0.5. A provisional threshold 0.30 is compared only after reviewing all tune tests.


## CONFIGURATION LOCK BEFORE FINAL ACCESS

Selected exactly one candidate: 3x3 square local peak, heat-logit quadratic subpixel centre (alpha=1; each axis clamped +/-0.5 cell, centre at cell + 0.5 + delta), rotated IoU NMS 0.5, threshold 0.30, unchanged log-width/height and angle. Tune-only: tune40 36 exact, 631/13/11; ov_tune40 14 exact, 526/43/115. Versus unchanged decoder at 0.30: 36 exact,615/29/27 and 12 exact,514/53/127. Count benefit only +2/80; localization TP +28, FP -26. This is NOT yet a demonstrated count improvement; no patch decision until the single final run. Soft Gaussian 0.20 gave 51/80 but increased FP; rejected. Disabling peak NMS recovers TP but adds many duplicates; rejected.

Frozen configuration recorded in locked_config.json. No final/ov_final/real raw tensor has been dumped or evaluated in this run before this lock.

## Standalone C++ verification and postprocess timing

Built ONLY the copied pure-C++ SegInstanceTargets decoder + ctypes wrapper under exp-decode/standalone; MSVC v143 Release x64, build success with one MSB8029 warning (intermediate dir deliberately under project-local TEMP). No installed native DLL modified. Baseline and selected code are selectable in this prototype only; no production ABI change. Timing excludes sigmoid, un-letterbox and inference, measures DecodeInstances itself; 20 warm repetitions per tune image.

```json
{
  "n": 80,
  "count_mismatches": {
    "0": 0,
    "1": 0
  },
  "max_abs_geometry_or_score_diff": {
    "0": 3.0517578125e-05,
    "1": 3.132285837637028e-05
  },
  "timing": {
    "0": {
      "p50": 0.12175,
      "p95": 0.6815349999999999,
      "mean": 0.18982312499999998,
      "n": 1600
    },
    "1": {
      "p50": 0.12595,
      "p95": 0.65393,
      "mean": 0.1936140625,
      "n": 1600
    }
  }
}
```

Raw tensor dump final: 100 images, shape (9, 80, 80), OpenVINO CPU FP32 source-equivalent graph (native model load stalled). Saved successfully; no training.

Raw tensor dump ovfinal: 60 images, shape (9, 80, 80), OpenVINO CPU FP32 source-equivalent graph (native model load stalled). Saved successfully; no training.

Raw tensor dump real: 1 images, shape (9, 80, 80), OpenVINO CPU FP32 source-equivalent graph (native model load stalled). Saved successfully; no training.

SINGLE LOCKED FINAL EVALUATION final: exact 84/100; TP/FP/FN 1255/28/38

SINGLE LOCKED FINAL EVALUATION ovfinal: exact 20/60; TP/FP/FN 830/54/198

SINGLE LOCKED FINAL EVALUATION real: exact 0/1; TP/FP/FN 19/2/1

## Corrected, spatially disambiguated FN classification

IMPORTANT: the initial regression-IoU-only classification above overstates type (i): a shoulder cell can have a better box on an already-retained screw; removing 3x3 NMS then adds duplicates, not a genuinely missing screw. Corrected table assigns each raw heat cell to the closest normalized GT centre among GT polygons containing that cell. A retained peak owned by an IoU-unmatched GT is localization category L, outside the requested (i)/(ii)/(iii) trichotomy. Thus no actual FN is silently forced into an incorrect heat category. A meaningful own peak is a 3x3 local maximum >=0.05. Types (i), (ii), (iii) refer only to FN without an already-retained own response. Per-FN details: classification_spatial.csv. FP localization pairs are not mislabeled as duplicate/background.

```json
[
  {
    "set": "tune",
    "threshold": 0.25,
    "fn": {
      "L_retained_peak_but_IoU_lt_0.5": 22,
      "iii_no_meaningful_own_peak": 1,
      "i_3x3_merged_response": 4
    },
    "fp": {
      "localization_FP_on_unmatched_screw": 23,
      "duplicate_or_bridge_on_matched_screw": 3,
      "background_or_off_GT_response": 6
    }
  },
  {
    "set": "tune",
    "threshold": 0.325,
    "fn": {
      "L_retained_peak_but_IoU_lt_0.5": 21,
      "iii_no_meaningful_own_peak": 1,
      "i_3x3_merged_response": 4,
      "ii_own_peak_below_threshold": 1
    },
    "fp": {
      "localization_FP_on_unmatched_screw": 21,
      "background_or_off_GT_response": 4,
      "duplicate_or_bridge_on_matched_screw": 2
    }
  },
  {
    "set": "ovtune",
    "threshold": 0.25,
    "fn": {
      "i_3x3_merged_response": 42,
      "ii_own_peak_below_threshold": 13,
      "L_retained_peak_but_IoU_lt_0.5": 54,
      "iii_no_meaningful_own_peak": 9
    },
    "fp": {
      "localization_FP_on_unmatched_screw": 52,
      "duplicate_or_bridge_on_matched_screw": 4,
      "background_or_off_GT_response": 1
    }
  },
  {
    "set": "ovtune",
    "threshold": 0.325,
    "fn": {
      "ii_own_peak_below_threshold": 31,
      "i_3x3_merged_response": 32,
      "L_retained_peak_but_IoU_lt_0.5": 50,
      "iii_no_meaningful_own_peak": 19
    },
    "fp": {
      "localization_FP_on_unmatched_screw": 46,
      "duplicate_or_bridge_on_matched_screw": 2
    }
  },
  {
    "set": "real_historical_diagnostic",
    "threshold": 0.25,
    "fn": {
      "L_retained_peak_but_IoU_lt_0.5": 3
    },
    "fp": {
      "localization_FP_on_unmatched_screw": 3
    },
    "native_to_raw_peak_mapping_max_px": 1.637625362249252e-05,
    "baseline": {
      "count": 20,
      "tp": 17,
      "fp": 3,
      "fn": 3
    }
  },
  {
    "set": "real_historical_diagnostic",
    "threshold": 0.325,
    "fn": {
      "L_retained_peak_but_IoU_lt_0.5": 3,
      "ii_own_peak_below_threshold": 1
    },
    "fp": {
      "localization_FP_on_unmatched_screw": 3
    },
    "native_to_raw_peak_mapping_max_px": 1.637625362249252e-05,
    "baseline": {
      "count": 19,
      "tp": 16,
      "fp": 3,
      "fn": 4
    }
  }
]
```

## Real image: every screw, with explicit per-screw peak value

IDs are zero-based manifest GT IDs; coordinates below are ORIGINAL 2448x2048 pixels. The central upright screw is GT8; isolated upright left of centre is GT3; right upright is GT10. Lower-middle overlap: GT12,13,14,15,16,17 (also neighboring GT9/10/18). Historical baseline at 0.25 is 20 detections,17 TP/3 FP/3 FN; at 0.325 it is 19 detections,16 TP/3 FP/4 FN, not 17/3/3. No baseline final inference was rerun: these are stored historical detections mapped to the new raw peaks.

|GT|Original centre x,y|Category @.25|Category @.325|Owned 3x3 peak|Max owned heat|
|---|---|---|---|---|---|
|0|737.6, 1534.4|retained_TP|retained_TP|0.497363|0.497363|
|1|858.4, 955.1|retained_TP|retained_TP|0.368295|0.368295|
|2|980.0, 897.1|retained_TP|retained_TP|0.460320|0.460320|
|3|930.0, 1070.5|retained_TP|retained_TP|0.544674|0.544674|
|4|1077.7, 1016.2|retained_TP|retained_TP|0.474276|0.474276|
|5|1310.0, 942.1|retained_TP|retained_TP|0.446391|0.446391|
|6|1267.1, 1028.2|retained_TP|retained_TP|0.484711|0.484711|
|7|1216.3, 1090.0|retained_TP|retained_TP|0.406929|0.406929|
|8|1207.5, 1219.2|L_retained_peak_but_IoU_lt_0.5|L_retained_peak_but_IoU_lt_0.5|0.387430|0.387430|
|9|1140.0, 1288.4|retained_TP|retained_TP|0.509962|0.509962|
|10|1283.0, 1361.5|retained_TP|retained_TP|0.421126|0.421126|
|11|636.0, 1303.0|retained_TP|retained_TP|0.507746|0.507746|
|12|914.6, 1319.4|L_retained_peak_but_IoU_lt_0.5|L_retained_peak_but_IoU_lt_0.5|0.463908|0.463908|
|13|1169.8, 1371.9|retained_TP|ii_own_peak_below_threshold|0.307191|0.307191|
|14|932.2, 1409.6|retained_TP|retained_TP|0.475623|0.475623|
|15|1106.6, 1428.5|L_retained_peak_but_IoU_lt_0.5|L_retained_peak_but_IoU_lt_0.5|0.373998|0.373998|
|16|1144.8, 1496.9|retained_TP|retained_TP|0.332976|0.332976|
|17|781.6, 1441.2|retained_TP|retained_TP|0.434737|0.434737|
|18|1035.6, 1535.2|retained_TP|retained_TP|0.435200|0.435200|
|19|1254.7, 1584.7|retained_TP|retained_TP|0.488919|0.488919|

## Final audit

Task: Lane C decode-only. Production source and installed native DLLs untouched; all experiment code/build/artifacts are under tmp/EAI-SEGAI-CUDA/exp-decode. Only requested report added in docs. No training, no staging/commit/push, no decode.patch. Verification: 13 scratch Python scripts AST-parse PASS; no conflict markers; scoped git diff --check returned no errors; standalone MSVC Release x64 build PASS with one MSB8029 warning. Native baseline and selected decoder parity verified on 80 tune images, zero count mismatches. Final evaluation was run once after configuration lock; all subsequent summaries used saved results and historical baseline JSON only. Real diagnostic images visually inspected: G8/G12/G15 are correctly marked as the three baseline IoU FN. CUDA raw-tensor and production end-to-end timing remain unverified.
