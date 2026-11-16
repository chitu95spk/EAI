# Segment AI hex tuning — trạng thái điều tra 2026-11-10

## Trạng thái

CHƯA ĐẠT / CHƯA NGHIỆM THU. Chưa chạy được baseline, train hay tuning. Không có số đo để kết luận tăng score hoặc nhanh 2x. Không thay kích thước input, không ghi đè model, không sửa native, không commit/stash/push.

## Đã xác minh

- Repo D:\\CODE\\EAI; `git status --short` có thay đổi trước đó ở SelfLearn UI/tests và tools/autoloop, không thuộc tác vụ này; để nguyên.
- `git diff -- BeeNativeSegAI/SegBackboneFineTune.cpp BeeNativeSegAI/SegV2Core.cpp` trả rỗng: hai file native này hiện không có diff.
- Ảnh thật: C:\\Demo\\LucGiac\\Cam_20261019_160350_387.bmp và .json LabelMe. Cache/meta cũng tồn tại; chưa đọc nội dung nhãn để xác nhận số instance.
- C:\\Demo\\LucGiac\\.easyai\\models tồn tại nhưng rỗng: chưa tìm được model Segment AI lục giác gốc / input size hiện tại.
- advanced.json có classes NG, OK, LucGiac. project.json schema 2, không chứa tham số input.
- Run autoloop mới nhất tìm thấy C:\\Demo\\LucGiac.autoloop\\run-20261108-091213, có STOPPED_by_user. report.json là YOLO11n-obb, 300 ảnh train, 2 val, test_evaluated=5, test_total=5. Không dùng kết quả này làm Segment AI hoặc bộ test 100 ảnh.
- Những run r0 khác trong LucGiac.autoloop có 300/600 train, 1/2 val; chưa tìm được bộ test 100 nhãn trong đường dẫn này.
- Model Segment AI sản xuất khác tìm được: D:\\CODE\\EAI\\tmp\\EAI-SEG960\\snapshots\\20261023-960-r1-snapshot2\\{ESTCSEGAI,ESTEC_2,ESTEC_L3}.easyai.data\\models\\v001.segai (~12MB); D:\\CODE\\EAI\\tmp\\EAI-ONL3\\train512b\\project\\.easyai\\models\\v001.segai. Đây không phải lục giác, chưa dùng thay baseline.
- Native distribution EasyLabel/Lib/native/segai có BeeNativeSegAI.dll, OpenVINO CPU/GPU plugin, libtorch CPU DLL và Models. Chưa load chạy nên chưa xác nhận backend khả dụng trên máy.
- Read HANDOFF mục 1-3, memory segai-v2-trainer-facts / prior-shift-box-overextension / easyai-project-plan. SegAI V2 là native OpenVINO frozen MobileNetV2 + CPU head; libtorch CPU chỉ dành fine-tune option. Không coi NVIDIA P5000 là OpenVINO Intel GPU.

## API để tiếp tục

BeeNativeSegAI/SegAINativeExport.h: TrainerCreate/ConfigureV2/AddSample/Train/Save; InferCreate/Load/Predict hoặc PredictInstances; InferGetLastBackend; InferGetPhaseTimings; InferGetSplitTimings. PredictInstances yêu cầu model có instance head; có thể dùng mask + connected components nếu model semantic nhưng phải ghi khác biệt. Không được kích hoạt V2SpeedScale<1 vì yêu cầu giữ input size.

## Bảng kết quả bắt buộc (chưa đo)

| Phương pháp | Input | Count đúng/sai | Precision/Recall IoU .5 | Mean score | p50/p95 ms |
|---|---|---|---|---|---|
| Count/Couter | Chưa xác minh | N/A | N/A | N/A | N/A |
| Segment AI gốc | Chưa có model gốc | N/A | N/A | N/A | N/A |
| Segment AI train/tune | Chưa tạo | N/A | N/A | N/A | N/A |

## Blocker thực thi

Nhiều tool exec/read bị treo và OpenClaw báo: `OpenClaw dynamic tool call timed out after 90000ms before sending a response to Codex.` Kiểm tra tối thiểu `python -c print(...)` có lần mất nhiều phút mới trả về, sau đó đọc source lại timeout. Chưa có bằng chứng các lệnh không trả về đã hoàn thành; không khởi chạy train trong trạng thái không quan sát được.

Memory search cũng unavailable: index fts-only không khớp text-embedding-3-small; đã dùng tài liệu repo thay recall. rg không có trong PATH nên fallback Python đọc phạm vi/tìm dòng.

## Công việc còn lại (không tự tick hoàn thành)

1. Phục hồi đường tool Gateway; tìm chính xác model Segment AI gốc và input size từ payload/model registry.
2. Định vị bộ 100 test nhãn đã yêu cầu hoặc sinh bộ mới cố định seed, lưu manifest và tách train/tune/test chống rò rỉ.
3. Chạy Count thật nếu có runtime; native Couter độc lập được giao worker riêng, kết quả chưa nhận ở thời điểm ghi báo cáo này.
4. Lập script native ctypes/C# dùng engine SegAI, baseline warmup + p50/p95 trên đúng cùng manifest.
5. Train vào C:\\Demo\\LucGiac.segai-tune, giữ input size; OFAT train/threshold/prior/thread/precision hợp Pascal; không lấy tốc độ bằng đổi input.
6. Bổ sung bảng cuối và hash model, script, raw metrics; chỉ báo đạt sau khi số đo xác nhận.

## Luot 2 - 2026-11-10 execution evidence
- Workspace resolved to session default Master per authenticated personal instructions; CHITU-PC does not contain D:/CODE/EAI or C:/Demo/LucGiac and is not used for this task.
- Gateway native ABI read successfully: ConfigureV2, AddInstances, PredictInstances, GetPhaseTimings available in header. Trivial execution probe eventually exited code 0; call dispatch remains intermittently delayed (several minutes), not a native model failure.
- Count harness delegated as an independent lane; no baseline metric yet.
- Memory recall unavailable due fts-only/text-embedding-3-small mismatch; using project documentation.

### Dataset frozen
{
 "source": "C:\\Demo\\LucGiac\\Cam_20261019_160350_387.bmp",
 "source_sha256": "aca08d7f13b7ebbac6240165a3583063a820adc6601f065aa212925eeeb240f1",
 "synthgen_sha256": "542553bd11fb79f6f5d190e3e6b63ba2c2e92c062cccf506f2133e56f91d2c7b",
 "generator_sha256": "711050a3574c1a165561a0ed064199c2ab2216a015197bc51f6343b4f5adb090",
 "isolated_ids": [
  11,
  0,
  17,
  1,
  3,
  2,
  4
 ],
 "dropped_touching": [
  5,
  6,
  7,
  8,
  9,
  10,
  12,
  13,
  14,
  15,
  16,
  18,
  19
 ],
 "source_groups": {
  "train": [
   11,
   0,
   17,
   1
  ],
  "val": [
   11,
   0,
   17,
   1
  ],
  "tune": [
   3
  ],
  "final": [
   2,
   4
  ]
 },
 "leakage_checks": "passed (source identities, image hashes, seeds); train/val share identities intentionally",
 "counts": {
  "train": 120,
  "val": 30,
  "tune": 40,
  "final": 100
 }
}

### Native model saved: baseline
{
 "model": "C:\\Demo\\LucGiac.segai-tune\\baseline\\model.segai",
 "sha256": "e58ed1775577ba7a2718c5af841c5fa6a986d4ed695fa8f92fb5651de9f5dbf8",
 "input_size": [
  640,
  640
 ],
 "train_seconds": 289.9239354133606,
 "config": {
  "backbone": "D:/CODE/EAI/EasyLabel/Lib/native/segai/Models/DefectV2/mobilenetv2-12.onnx",
  "logs": "C:/Demo/LucGiac.segai-tune/baseline",
  "side": 640,
  "epochs": 200,
  "learningRate": 0.015,
  "regularization": 0.001,
  "rotation": 15.0,
  "scaleVariation": 0.15,
  "brightness": 0.15,
  "augment": 1,
  "gpu": 0,
  "originalInput": 0,
  "earlyStop": 1,
  "earlyStopMinGain": 0.02,
  "validation": [
   120,
   121,
   122,
   123,
   124,
   125,
   126,
   127,
   128,
   129,
   130,
   131,
   132,
   133,
   134,
   135,
   136,
   137,
   138,
   139,
   140,
   141,
   142,
   143,
   144,
   145,
   146,
   147,
   148,
   149
  ]
 },
 "native_sha256": "85b6dfa8f9d5168265abea82b2bc8680623ef364e19631640ba42e8347358d22"
}

## Baseline ready / OFAT begins
Native CPU; fixed 640x640, speed scale1.0. Tune40 and final100 have distinct source identities, seeds and hashes. Final is not consulted for choices.

## Pipeline exception (actual evidence)

Traceback (most recent call last):
  File "D:\CODE\EAI\tools\segai_tune\run_experiment.py", line 142, in <module>
    try:main()
        ~~~~^^
  File "D:\CODE\EAI\tools\segai_tune\run_experiment.py", line 91, in main
    ref=trial("baseline_tune",baseline,{})
  File "D:\CODE\EAI\tools\segai_tune\run_experiment.py", line 87, in trial
    try:s=evaluate(e,tune,OUT/"metrics"/(name+".json"))
          ~~~~~~~~^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
  File "D:\CODE\EAI\tools\segai_tune\run_experiment.py", line 40, in evaluate
    with path.with_suffix(".jsonl").open("a",encoding="utf-8") as f:f.write(json.dumps(row)+"\n")
         ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~^^^^^^^^^^^^^^^^^^^^^^
  File "C:\Users\TEAM\AppData\Local\Programs\Python\Python314\Lib\pathlib\__init__.py", line 771, in open
    return io.open(self, mode, buffering, encoding, errors, newline)
           ~~~~~~~^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
FileNotFoundError: [Errno 2] No such file or directory: 'C:\\Demo\\LucGiac.segai-tune\\metrics\\baseline_tune.jsonl'


## Baseline ready / OFAT begins
Native CPU; fixed 640x640, speed scale1.0. Tune40 and final100 have distinct source identities, seeds and hashes. Final is not consulted for choices.

### Measurement baseline_tune
Raw: C:\Demo\LucGiac.segai-tune\metrics\baseline_tune.json
{"images": 40, "objects": 642, "tp": 190, "fp": 294, "fn": 452, "precision": 0.3925619834710744, "recall": 0.29595015576323985, "f1": 0.33747779751332146, "count_exact": 12, "all_objects_matched": 8, "mean_score": 0.7738778155890457, "mean_tp_score": 0.7966976264189887, "p50_ms": 273.4402000060072, "p95_ms": 381.17449499914073, "native_p50_ms": 230.83939999196446, "native_p95_ms": 321.9189500072389, "phase_p50_ms": {"device": 0.0006, "prepare": 1.05255, "tensor": 1.5246, "infer": 213.079, "upsample": 2.0667, "compile": 0.0, "ccscore": 0.43995, "nearest": 2.1734999999999998, "v2_total": 222.0051, "inferer_tail": 0.68785, "inferer_total": 228.72355, "export_copy": 0.39825, "export_total": 230.11745}}

### Native model saved: prior_balance
{
 "model": "C:\\Demo\\LucGiac.segai-tune\\prior_balance\\model.segai",
 "sha256": "3b669a7cb2041a6438644d54d75caebc4385a4961e6348142950a7adfad09be8",
 "input_size": [
  640,
  640
 ],
 "train_seconds": 275.39692878723145,
 "config": {
  "backbone": "D:/CODE/EAI/EasyLabel/Lib/native/segai/Models/DefectV2/mobilenetv2-12.onnx",
  "logs": "C:/Demo/LucGiac.segai-tune/prior_balance",
  "side": 640,
  "epochs": 200,
  "learningRate": 0.015,
  "regularization": 0.001,
  "rotation": 15.0,
  "scaleVariation": 0.15,
  "brightness": 0.15,
  "augment": 1,
  "gpu": 0,
  "originalInput": 0,
  "earlyStop": 1,
  "earlyStopMinGain": 0.02,
  "validation": [
   120,
   121,
   122,
   123,
   124,
   125,
   126,
   127,
   128,
   129,
   130,
   131,
   132,
   133,
   134,
   135,
   136,
   137,
   138,
   139,
   140,
   141,
   142,
   143,
   144,
   145,
   146,
   147,
   148,
   149
  ],
  "priorBalance": 1
 },
 "native_sha256": "85b6dfa8f9d5168265abea82b2bc8680623ef364e19631640ba42e8347358d22"
}

### Measurement prior_balance_tune
Raw: C:\Demo\LucGiac.segai-tune\metrics\prior_balance_tune.json
{"images": 40, "objects": 642, "tp": 159, "fp": 311, "fn": 483, "precision": 0.3382978723404255, "recall": 0.24766355140186916, "f1": 0.28597122302158273, "count_exact": 16, "all_objects_matched": 8, "mean_score": 0.7620191906549854, "mean_tp_score": 0.7879393266740657, "p50_ms": 244.86944999807747, "p95_ms": 357.3946150034316, "native_p50_ms": 209.890500002075, "native_p95_ms": 298.3414349888335, "phase_p50_ms": {"device": 0.0005, "prepare": 1.0204499999999999, "tensor": 1.2863, "infer": 194.642, "upsample": 1.9603, "compile": 0.0, "ccscore": 0.4259, "nearest": 2.14565, "v2_total": 207.6791, "inferer_tail": 0.6376, "inferer_total": 208.3922, "export_copy": 0.37765, "export_total": 209.12555}}

### Native model saved: regularization_0003
{
 "model": "C:\\Demo\\LucGiac.segai-tune\\regularization_0003\\model.segai",
 "sha256": "bfbe971620639fee4fcbaa940091d15e60d27e60e5e4db183309082cd1f1e624",
 "input_size": [
  640,
  640
 ],
 "train_seconds": 289.33964467048645,
 "config": {
  "backbone": "D:/CODE/EAI/EasyLabel/Lib/native/segai/Models/DefectV2/mobilenetv2-12.onnx",
  "logs": "C:/Demo/LucGiac.segai-tune/regularization_0003",
  "side": 640,
  "epochs": 200,
  "learningRate": 0.015,
  "regularization": 0.0003,
  "rotation": 15.0,
  "scaleVariation": 0.15,
  "brightness": 0.15,
  "augment": 1,
  "gpu": 0,
  "originalInput": 0,
  "earlyStop": 1,
  "earlyStopMinGain": 0.02,
  "validation": [
   120,
   121,
   122,
   123,
   124,
   125,
   126,
   127,
   128,
   129,
   130,
   131,
   132,
   133,
   134,
   135,
   136,
   137,
   138,
   139,
   140,
   141,
   142,
   143,
   144,
   145,
   146,
   147,
   148,
   149
  ]
 },
 "native_sha256": "85b6dfa8f9d5168265abea82b2bc8680623ef364e19631640ba42e8347358d22"
}

### Measurement regularization_0003_tune
Raw: C:\Demo\LucGiac.segai-tune\metrics\regularization_0003_tune.json
{"images": 40, "objects": 642, "tp": 191, "fp": 288, "fn": 451, "precision": 0.3987473903966597, "recall": 0.29750778816199375, "f1": 0.34076717216770736, "count_exact": 10, "all_objects_matched": 8, "mean_score": 0.7741045478734292, "mean_tp_score": 0.7975977825685248, "p50_ms": 275.7970499951625, "p95_ms": 359.51151499466505, "native_p50_ms": 221.41615000145976, "native_p95_ms": 314.1985800102702, "phase_p50_ms": {"device": 0.0006, "prepare": 1.2243499999999998, "tensor": 1.43795, "infer": 207.6385, "upsample": 2.1675500000000003, "compile": 0.0, "ccscore": 0.45725, "nearest": 2.2131999999999996, "v2_total": 217.3647, "inferer_tail": 0.7201, "inferer_total": 218.0285, "export_copy": 0.40545, "export_total": 220.65375}}

### Measurement ofat_threshold_0p35
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_threshold_0p35.json
{"images": 40, "objects": 642, "tp": 190, "fp": 295, "fn": 452, "precision": 0.3917525773195876, "recall": 0.29595015576323985, "f1": 0.33717834960070986, "count_exact": 12, "all_objects_matched": 8, "mean_score": 0.7733009904992926, "mean_tp_score": 0.7966976264189887, "p50_ms": 208.95980000204872, "p95_ms": 376.7013750031765, "native_p50_ms": 169.69040000549285, "native_p95_ms": 326.53792999772116, "phase_p50_ms": {"device": 0.0005499999999999999, "prepare": 1.0293999999999999, "tensor": 1.4254, "infer": 160.2673, "upsample": 2.0661, "compile": 0.0, "ccscore": 0.442, "nearest": 1.7603, "v2_total": 167.4844, "inferer_tail": 0.6234, "inferer_total": 168.1397, "export_copy": 0.384, "export_total": 168.82065}}

### Measurement ofat_threshold_0p65
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_threshold_0p65.json
{"images": 40, "objects": 642, "tp": 189, "fp": 285, "fn": 453, "precision": 0.3987341772151899, "recall": 0.29439252336448596, "f1": 0.33870967741935487, "count_exact": 12, "all_objects_matched": 8, "mean_score": 0.776983536030446, "mean_tp_score": 0.7975101151571741, "p50_ms": 183.89195000054315, "p95_ms": 264.9623200093629, "native_p50_ms": 148.04274999914924, "native_p95_ms": 229.77395000489196, "phase_p50_ms": {"device": 0.0005, "prepare": 1.0025, "tensor": 1.3863500000000002, "infer": 138.86185, "upsample": 1.8486, "compile": 0.0, "ccscore": 0.43345, "nearest": 1.6839499999999998, "v2_total": 145.98985, "inferer_tail": 0.6325000000000001, "inferer_total": 146.6702, "export_copy": 0.3771, "export_total": 147.3547}}

### Measurement ofat_threshold_0p8
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_threshold_0p8.json
{"images": 40, "objects": 642, "tp": 101, "fp": 50, "fn": 541, "precision": 0.6688741721854304, "recall": 0.1573208722741433, "f1": 0.2547288776796973, "count_exact": 8, "all_objects_matched": 8, "mean_score": 0.8280223347617192, "mean_tp_score": 0.835022325761988, "p50_ms": 251.01255000481615, "p95_ms": 332.4627900023188, "native_p50_ms": 207.84219999040943, "native_p95_ms": 277.59353501169244, "phase_p50_ms": {"device": 0.0005, "prepare": 1.07735, "tensor": 1.3062, "infer": 196.56779999999998, "upsample": 1.8471, "compile": 0.0, "ccscore": 0.42264999999999997, "nearest": 2.08325, "v2_total": 206.05115, "inferer_tail": 0.6024499999999999, "inferer_total": 206.49995, "export_copy": 0.3831, "export_total": 207.12369999999999}}

### Measurement ofat_foreground_0p4
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_foreground_0p4.json
{"images": 40, "objects": 642, "tp": 190, "fp": 294, "fn": 452, "precision": 0.3925619834710744, "recall": 0.29595015576323985, "f1": 0.33747779751332146, "count_exact": 12, "all_objects_matched": 8, "mean_score": 0.7738778155890457, "mean_tp_score": 0.7966976264189887, "p50_ms": 234.73114999796962, "p95_ms": 324.5810750035161, "native_p50_ms": 198.6073000007309, "native_p95_ms": 278.21310501021793, "phase_p50_ms": {"device": 0.0005, "prepare": 1.0565, "tensor": 1.417, "infer": 189.26555, "upsample": 1.97805, "compile": 0.0, "ccscore": 0.422, "nearest": 2.093, "v2_total": 196.559, "inferer_tail": 0.6395, "inferer_total": 197.18959999999998, "export_copy": 0.39995, "export_total": 197.8866}}

### Measurement ofat_foreground_0p6
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_foreground_0p6.json
{"images": 40, "objects": 642, "tp": 136, "fp": 319, "fn": 506, "precision": 0.2989010989010989, "recall": 0.2118380062305296, "f1": 0.24794895168641753, "count_exact": 14, "all_objects_matched": 8, "mean_score": 0.8244688644688645, "mean_tp_score": 0.8461937716262975, "p50_ms": 232.51010000240058, "p95_ms": 337.9856599953199, "native_p50_ms": 196.3525999963167, "native_p95_ms": 311.84043500179536, "phase_p50_ms": {"device": 0.0005, "prepare": 1.06905, "tensor": 1.34905, "infer": 187.5011, "upsample": 1.96475, "compile": 0.0, "ccscore": 0.43779999999999997, "nearest": 2.1614, "v2_total": 194.2362, "inferer_tail": 0.7037, "inferer_total": 194.93535, "export_copy": 0.39255, "export_total": 195.6285}}

### Measurement ofat_foreground_0p7
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_foreground_0p7.json
{"images": 40, "objects": 642, "tp": 81, "fp": 326, "fn": 561, "precision": 0.19901719901719903, "recall": 0.1261682242990654, "f1": 0.1544327931363203, "count_exact": 7, "all_objects_matched": 6, "mean_score": 0.8712627065568243, "mean_tp_score": 0.8925683853788431, "p50_ms": 211.2842999995337, "p95_ms": 293.62398499943083, "native_p50_ms": 163.31419999187347, "native_p95_ms": 253.82511500138204, "phase_p50_ms": {"device": 0.0005, "prepare": 1.0441500000000001, "tensor": 1.3834, "infer": 154.27319999999997, "upsample": 1.9298, "compile": 0.0, "ccscore": 0.43235, "nearest": 2.08115, "v2_total": 161.4188, "inferer_tail": 0.5673, "inferer_total": 161.99505, "export_copy": 0.37995, "export_total": 162.66425}}

### Measurement ofat_ratio_0p1
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_ratio_0p1.json
{"images": 40, "objects": 642, "tp": 190, "fp": 294, "fn": 452, "precision": 0.3925619834710744, "recall": 0.29595015576323985, "f1": 0.33747779751332146, "count_exact": 12, "all_objects_matched": 8, "mean_score": 0.7738778155890457, "mean_tp_score": 0.7966976264189887, "p50_ms": 218.851050005469, "p95_ms": 307.3604299970611, "native_p50_ms": 188.54894999822136, "native_p95_ms": 270.4468050011201, "phase_p50_ms": {"device": 0.0005, "prepare": 1.02365, "tensor": 1.3356, "infer": 179.67955, "upsample": 1.9209999999999998, "compile": 0.0, "ccscore": 0.41955, "nearest": 2.11135, "v2_total": 186.5514, "inferer_tail": 0.6112500000000001, "inferer_total": 187.1847, "export_copy": 0.36219999999999997, "export_total": 187.82145}}

### Measurement ofat_ratio_0p4
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_ratio_0p4.json
{"images": 40, "objects": 642, "tp": 190, "fp": 294, "fn": 452, "precision": 0.3925619834710744, "recall": 0.29595015576323985, "f1": 0.33747779751332146, "count_exact": 12, "all_objects_matched": 8, "mean_score": 0.7738778155890457, "mean_tp_score": 0.7966976264189887, "p50_ms": 227.17904999444727, "p95_ms": 337.7006899936532, "native_p50_ms": 186.50815000000875, "native_p95_ms": 282.99198499735206, "phase_p50_ms": {"device": 0.0005, "prepare": 1.08, "tensor": 1.3417, "infer": 177.37060000000002, "upsample": 1.77595, "compile": 0.0, "ccscore": 0.43055, "nearest": 2.1582, "v2_total": 184.59390000000002, "inferer_tail": 0.6294, "inferer_total": 185.211, "export_copy": 0.3943, "export_total": 185.83615}}

### Measurement ofat_ratio_0p5
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_ratio_0p5.json
{"images": 40, "objects": 642, "tp": 190, "fp": 294, "fn": 452, "precision": 0.3925619834710744, "recall": 0.29595015576323985, "f1": 0.33747779751332146, "count_exact": 12, "all_objects_matched": 8, "mean_score": 0.7738778155890457, "mean_tp_score": 0.7966976264189887, "p50_ms": 208.52985000237823, "p95_ms": 352.92351500029326, "native_p50_ms": 180.6906500059995, "native_p95_ms": 299.0917499897477, "phase_p50_ms": {"device": 0.0005, "prepare": 1.03815, "tensor": 1.33895, "infer": 171.5193, "upsample": 1.9014000000000002, "compile": 0.0, "ccscore": 0.42325, "nearest": 2.11785, "v2_total": 178.6898, "inferer_tail": 0.61985, "inferer_total": 179.4093, "export_copy": 0.33665, "export_total": 180.0041}}

### Measurement ofat_morphology_0
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_morphology_0.json
{"images": 40, "objects": 642, "tp": 425, "fp": 139, "fn": 217, "precision": 0.7535460992907801, "recall": 0.661993769470405, "f1": 0.7048092868988391, "count_exact": 13, "all_objects_matched": 13, "mean_score": 0.7274092615769712, "mean_tp_score": 0.7411487889273357, "p50_ms": 223.798999999417, "p95_ms": 320.3269699988595, "native_p50_ms": 186.42035000084434, "native_p95_ms": 280.0272400090762, "phase_p50_ms": {"device": 0.0005, "prepare": 1.01885, "tensor": 1.3321999999999998, "infer": 177.62115, "upsample": 1.79265, "compile": 0.0, "ccscore": 0.36050000000000004, "nearest": 2.0984, "v2_total": 184.36455, "inferer_tail": 0.6135999999999999, "inferer_total": 185.0573, "export_copy": 0.3768, "export_total": 185.73705}}

## Pipeline exception (actual evidence)

Traceback (most recent call last):
  File "D:\CODE\EAI\tools\segai_tune\run_experiment.py", line 142, in <module>
    try:main()
        ~~~~^^
  File "D:\CODE\EAI\tools\segai_tune\run_experiment.py", line 105, in main
    s=trial("ofat_"+key+"_"+str(v).replace(".","p"),selected,opts)
  File "D:\CODE\EAI\tools\segai_tune\run_experiment.py", line 86, in trial
    e=SegNative(model,**opts)
  File "D:\CODE\EAI\tools\segai_tune\seg_native.py", line 84, in __init__
    check(opts(self.h,foreground,ratio,morphology),self.h)
    ~~~~~^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
  File "D:\CODE\EAI\tools\segai_tune\seg_native.py", line 39, in check
    raise RuntimeError(f"native rc={rc}: "+buf.value.decode("utf-8",errors="replace"))
RuntimeError: native rc=-2: 


## Baseline ready / OFAT begins
Native CPU; fixed 640x640, speed scale1.0. Tune40 and final100 have distinct source identities, seeds and hashes. Final is not consulted for choices.

## Pipeline exception (actual evidence)

Traceback (most recent call last):
  File "D:\CODE\EAI\tools\segai_tune\run_experiment.py", line 142, in <module>
    try:main()
        ~~~~^^
  File "D:\CODE\EAI\tools\segai_tune\run_experiment.py", line 105, in main
    s=trial("ofat_"+key+"_"+str(v).replace(".","p"),selected,opts)
  File "D:\CODE\EAI\tools\segai_tune\run_experiment.py", line 86, in trial
    e=SegNative(model,**opts)
  File "D:\CODE\EAI\tools\segai_tune\seg_native.py", line 84, in __init__
    check(opts(self.h,foreground,ratio,morphology),self.h)
    ~~~~~^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
  File "D:\CODE\EAI\tools\segai_tune\seg_native.py", line 39, in check
    raise RuntimeError(f"native rc={rc}: "+buf.value.decode("utf-8",errors="replace"))
RuntimeError: native rc=-2: 


## Baseline ready / OFAT begins
Native CPU; fixed 640x640, speed scale1.0. Tune40 and final100 have distinct source identities, seeds and hashes. Final is not consulted for choices.

### Measurement ofat_morphology_5
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_morphology_5.json
{"images": 40, "objects": 642, "tp": 23, "fp": 150, "fn": 619, "precision": 0.1329479768786127, "recall": 0.03582554517133956, "f1": 0.056441717791411036, "count_exact": 8, "all_objects_matched": 5, "mean_score": 0.8031961917715063, "mean_tp_score": 0.8521739130434784, "p50_ms": 79.5580499980133, "p95_ms": 100.35939000808867, "native_p50_ms": 60.723750000761356, "native_p95_ms": 80.02330500312382, "phase_p50_ms": {"device": 0.0005, "prepare": 0.6726000000000001, "tensor": 1.17425, "infer": 54.16475, "upsample": 1.8045499999999999, "compile": 0.0, "ccscore": 0.37975000000000003, "nearest": 0.8153, "v2_total": 59.0433, "inferer_tail": 0.34595, "inferer_total": 59.407399999999996, "export_copy": 0.33425000000000005, "export_total": 60.0108}}

### Measurement ofat_boundary_mode_1
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_boundary_mode_1.json
{"images": 40, "objects": 642, "tp": 425, "fp": 139, "fn": 217, "precision": 0.7535460992907801, "recall": 0.661993769470405, "f1": 0.7048092868988391, "count_exact": 13, "all_objects_matched": 13, "mean_score": 0.7274092615769712, "mean_tp_score": 0.7411487889273357, "p50_ms": 86.8709500064142, "p95_ms": 107.54040000610985, "native_p50_ms": 62.80890000198269, "native_p95_ms": 79.11835000049905, "phase_p50_ms": {"device": 0.0004, "prepare": 0.7179, "tensor": 1.1904499999999998, "infer": 55.80205, "upsample": 1.7793, "compile": 0.0, "ccscore": 0.33285, "nearest": 0.8601000000000001, "v2_total": 61.08225, "inferer_tail": 0.35850000000000004, "inferer_total": 61.43615, "export_copy": 0.35325, "export_total": 62.067949999999996}}

### Measurement ofat_shape_cleanup_0p03
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_shape_cleanup_0p03.json
{"images": 40, "objects": 642, "tp": 425, "fp": 139, "fn": 217, "precision": 0.7535460992907801, "recall": 0.661993769470405, "f1": 0.7048092868988391, "count_exact": 13, "all_objects_matched": 13, "mean_score": 0.7274092615769712, "mean_tp_score": 0.7411487889273357, "p50_ms": 97.80874999705702, "p95_ms": 132.7384850119415, "native_p50_ms": 73.76814999588532, "native_p95_ms": 101.79868499762959, "phase_p50_ms": {"device": 0.0005, "prepare": 0.7229, "tensor": 1.1925, "infer": 53.82585, "upsample": 1.74895, "compile": 0.0, "ccscore": 0.31020000000000003, "nearest": 13.9916, "v2_total": 72.0864, "inferer_tail": 0.4112, "inferer_total": 72.45875, "export_copy": 0.37795, "export_total": 73.1283}}

### Measurement ofat_shape_cleanup_0p06
Raw: C:\Demo\LucGiac.segai-tune\metrics\ofat_shape_cleanup_0p06.json
{"images": 40, "objects": 642, "tp": 425, "fp": 139, "fn": 217, "precision": 0.7535460992907801, "recall": 0.661993769470405, "f1": 0.7048092868988391, "count_exact": 13, "all_objects_matched": 13, "mean_score": 0.7274092615769712, "mean_tp_score": 0.7411487889273357, "p50_ms": 108.10920000221813, "p95_ms": 123.43134499678852, "native_p50_ms": 83.84460000524996, "native_p95_ms": 94.7748599901388, "phase_p50_ms": {"device": 0.0005, "prepare": 0.7220500000000001, "tensor": 1.23575, "infer": 60.8471, "upsample": 1.70275, "compile": 0.0, "ccscore": 0.33225, "nearest": 15.891449999999999, "v2_total": 81.9721, "inferer_tail": 0.43365, "inferer_total": 82.4367, "export_copy": 0.34240000000000004, "export_total": 82.97335000000001}}

### Measurement cpu_affinity_2
Raw: C:\Demo\LucGiac.segai-tune\metrics\cpu_affinity_2.json
{"images": 40, "objects": 642, "tp": 425, "fp": 139, "fn": 217, "precision": 0.7535460992907801, "recall": 0.661993769470405, "f1": 0.7048092868988391, "count_exact": 13, "all_objects_matched": 13, "mean_score": 0.7274092615769712, "mean_tp_score": 0.7411487889273357, "p50_ms": 82.67255000100704, "p95_ms": 95.709289998922, "native_p50_ms": 62.88460000359919, "native_p95_ms": 76.37806999628081, "phase_p50_ms": {"device": 0.0006, "prepare": 0.7523, "tensor": 0.98685, "infer": 55.98865, "upsample": 1.4819499999999999, "compile": 0.0, "ccscore": 0.59535, "nearest": 0.9298500000000001, "v2_total": 61.362899999999996, "inferer_tail": 0.34655, "inferer_total": 61.831649999999996, "export_copy": 0.31384999999999996, "export_total": 62.2843}}

### Measurement cpu_affinity_4
Raw: C:\Demo\LucGiac.segai-tune\metrics\cpu_affinity_4.json
{"images": 40, "objects": 642, "tp": 425, "fp": 139, "fn": 217, "precision": 0.7535460992907801, "recall": 0.661993769470405, "f1": 0.7048092868988391, "count_exact": 13, "all_objects_matched": 13, "mean_score": 0.7274092615769712, "mean_tp_score": 0.7411487889273357, "p50_ms": 55.690499997581355, "p95_ms": 69.5428049941256, "native_p50_ms": 40.20769999624463, "native_p95_ms": 48.94001499415025, "phase_p50_ms": {"device": 0.0006, "prepare": 0.6368, "tensor": 0.881, "infer": 34.5723, "upsample": 1.4703, "compile": 0.0, "ccscore": 0.23155, "nearest": 0.6809499999999999, "v2_total": 38.82585, "inferer_tail": 0.2949, "inferer_total": 39.10165, "export_copy": 0.32355, "export_total": 39.6967}}

### Measurement cpu_affinity_8
Raw: C:\Demo\LucGiac.segai-tune\metrics\cpu_affinity_8.json
{"images": 40, "objects": 642, "tp": 425, "fp": 139, "fn": 217, "precision": 0.7535460992907801, "recall": 0.661993769470405, "f1": 0.7048092868988391, "count_exact": 13, "all_objects_matched": 13, "mean_score": 0.7274092615769712, "mean_tp_score": 0.7411487889273357, "p50_ms": 41.871749999700114, "p95_ms": 52.80711499217431, "native_p50_ms": 27.73180000076536, "native_p95_ms": 32.51878500523161, "phase_p50_ms": {"device": 0.0006, "prepare": 0.5621, "tensor": 0.9003, "infer": 22.2035, "upsample": 1.55575, "compile": 0.0, "ccscore": 0.21665, "nearest": 0.49115, "v2_total": 26.10865, "inferer_tail": 0.25545, "inferer_total": 26.3638, "export_copy": 0.2968, "export_total": 26.89015}}

### Precision/thread limits
Native Compile exposes LATENCY only, no thread/precision API. CPU affinity is an OS scheduling experiment, not proof of an INFERENCE_NUM_THREADS setting. No NVIDIA FP16 path exists in this V2 backend; no unsupported precision claim or input reduction is used.

### Selection frozen before final evaluation
{"model": "C:\\Demo\\LucGiac.segai-tune\\baseline\\model.segai", "sha256": "e58ed1775577ba7a2718c5af841c5fa6a986d4ed695fa8f92fb5651de9f5dbf8", "settings": {"foreground": 0.4, "ratio": 0.5, "morphology": 0, "boundary_mode": 1}, "affinity": 255, "baseline_sha256": "e58ed1775577ba7a2718c5af841c5fa6a986d4ed695fa8f92fb5651de9f5dbf8", "input": [640, 640], "speed_scale": 1.0, "tuning_only": true}

### Measurement segai_baseline_final
Raw: C:\Demo\LucGiac.segai-tune\metrics\segai_baseline_final.json
{"images": 100, "objects": 1293, "tp": 461, "fp": 568, "fn": 832, "precision": 0.4480077745383868, "recall": 0.3565351894818252, "f1": 0.39707149009474596, "count_exact": 45, "all_objects_matched": 35, "mean_score": 0.7776139027039388, "mean_tp_score": 0.8045255412360172, "p50_ms": 83.91295000910759, "p95_ms": 109.22345999861136, "native_p50_ms": 61.51309999404475, "native_p95_ms": 84.2757000034908, "phase_p50_ms": {"device": 0.0006, "prepare": 0.7034, "tensor": 1.1905000000000001, "infer": 54.715149999999994, "upsample": 1.72415, "compile": 0.0, "ccscore": 0.35365, "nearest": 0.858, "v2_total": 59.6751, "inferer_tail": 0.3556, "inferer_total": 60.053, "export_copy": 0.35505, "export_total": 60.68005}}

### Measurement segai_baseline_real
Raw: C:\Demo\LucGiac.segai-tune\metrics\segai_baseline_real.json
{"images": 1, "objects": 20, "tp": 1, "fp": 2, "fn": 19, "precision": 0.3333333333333333, "recall": 0.05, "f1": 0.08695652173913045, "count_exact": 0, "all_objects_matched": 0, "mean_score": 0.7843137254901961, "mean_tp_score": 0.788235294117647, "p50_ms": 188.16289999813307, "p95_ms": 228.72953999758462, "native_p50_ms": 82.41560000169557, "native_p95_ms": 108.77247000025812, "phase_p50_ms": {"device": 0.0005, "prepare": 1.0929, "tensor": 1.0989, "infer": 52.4685, "upsample": 1.8633, "compile": 0.0, "ccscore": 0.4498, "nearest": 2.5188, "v2_total": 59.58, "inferer_tail": 1.1151, "inferer_total": 60.6971, "export_copy": 1.14, "export_total": 62.8274}}

### Measurement segai_tuned_final
Raw: C:\Demo\LucGiac.segai-tune\metrics\segai_tuned_final.json
{"images": 100, "objects": 1293, "tp": 906, "fp": 257, "fn": 387, "precision": 0.7790197764402408, "recall": 0.7006960556844548, "f1": 0.7377850162866451, "count_exact": 57, "all_objects_matched": 47, "mean_score": 0.7388093672550705, "mean_tp_score": 0.7529931177769121, "p50_ms": 42.916749996948056, "p95_ms": 51.81004999685685, "native_p50_ms": 28.312799993727822, "native_p95_ms": 34.44149000424659, "phase_p50_ms": {"device": 0.0005, "prepare": 0.571, "tensor": 0.95195, "infer": 22.657049999999998, "upsample": 1.5668000000000002, "compile": 0.0, "ccscore": 0.21175, "nearest": 0.49985, "v2_total": 26.6777, "inferer_tail": 0.28985000000000005, "inferer_total": 26.9259, "export_copy": 0.2964, "export_total": 27.44725}}

### Measurement segai_tuned_real
Raw: C:\Demo\LucGiac.segai-tune\metrics\segai_tuned_real.json
{"images": 1, "objects": 20, "tp": 0, "fp": 2, "fn": 20, "precision": 0.0, "recall": 0.0, "f1": 0.0, "count_exact": 0, "all_objects_matched": 0, "mean_score": 0.8137254901960784, "mean_tp_score": null, "p50_ms": 103.60869999567512, "p95_ms": 118.03198999696177, "native_p50_ms": 36.58400000131223, "native_p95_ms": 42.17537000513403, "phase_p50_ms": {"device": 0.0006, "prepare": 0.7547, "tensor": 1.0123, "infer": 22.9175, "upsample": 1.4876, "compile": 0.0, "ccscore": 0.2353, "nearest": 1.4966, "v2_total": 27.9739, "inferer_tail": 0.9677, "inferer_total": 28.9429, "export_copy": 0.7784, "export_total": 30.3064}}

## SegAI final (selection locked)
|Method|Exact count /100|Precision|Recall|Mean score|p50 ms|p95 ms|
|---|---|---|---|---|---|---|
|segai_baseline|45/100|0.448008|0.356535|0.777614|83.913|109.223|
|segai_baseline_real|0/1|0.333333|0.050000|0.784314|188.163|228.730|
|segai_tuned|57/100|0.779020|0.700696|0.738809|42.917|51.810|
|segai_tuned_real|0/1|0.000000|0.000000|0.813725|103.609|118.032|
Speed ratio baseline/tuned=1.9552494076339635; native-only=2.172625102698138; 2x achieved=False
Phase bottleneck: [('export_total', 27.44725), ('inferer_total', 26.9259), ('v2_total', 26.6777), ('infer', 22.657049999999998), ('upsample', 1.5668000000000002), ('tensor', 0.95195), ('prepare', 0.571), ('nearest', 0.49985), ('export_copy', 0.2964), ('inferer_tail', 0.28985000000000005), ('ccscore', 0.21175), ('device', 0.0005), ('compile', 0.0)]
Real photo is source-derived diagnostic, NOT independent test evidence. Count comparison is in separate Count lane until common-manifest measurement is joined. No commit or native changes.

## Additional native factor: instanceHead=1
Declared before viewing final metrics. Selection uses only the separate 40-frame tuning split. Adds native instance head (default60 epochs) at unchanged 640x640; no native changes. Existing final files are not consulted for choices.

## Verified Count runtime blocker
# Count native Couter - confirmed dependency blocker

The assigned checkout D:\CODE\EAI contains EasyLabel/Lib/native/{cuda13,segai}, but no BeeCpp.dll under EasyLabel and no Count/Couter implementation under EasyLabel/src. Historical docs describe Count as Couter in the separate EasyVision/Pattern lineage; the runtime was not copied into this split-out repository.

The independent Count investigation run 43d00e59-ac98-4b01-9d81-b47df4837e30 reached its configured 15-minute limit and returned no wrapper. A bounded sanitized history was checked; no measured Count output or count_native.py exists.

Count accuracy, confidence and cycle time are unavailable. Do not rename connected components as native Couter or substitute it silently. Reproducing Count needs the actual Couter runtime/API plus its actual Count settings. No request for unrelated machines/workspaces was made.

## Additional native factor: instanceHead=1
Declared before viewing final metrics. Selection uses only the separate 40-frame tuning split. Adds native instance head (default60 epochs) at unchanged 640x640; no native changes. Existing final files are not consulted for choices.

## Instance experiment continuation, 2026-11-10
On continuation no segai_tune process remained and no instance_head/model.segai or model.json existed; the saved epoch log ended at 124. The existing instance_experiment.py was restarted with the same predeclared configuration, not parameters selected from final metrics. Native training reported early stop at epoch124 and is now training the instance head. Exact command: python -X utf8 tools/segai_tune/instance_experiment.py; execution handle salty-atlas; timeout3600s; log C:/Demo/LucGiac.segai-tune/instance_extension_resume.log. No cause of the earlier interruption is asserted.

### Native model saved: instance_head
{
 "model": "C:\\Demo\\LucGiac.segai-tune\\instance_head\\model.segai",
 "sha256": "076ab3b7a525eedf44815d11c41b03b4a7cbf671af667ab5b39bfa59b02e4fcb",
 "input_size": [
  640,
  640
 ],
 "train_seconds": 386.2863345146179,
 "config": {
  "backbone": "D:/CODE/EAI/EasyLabel/Lib/native/segai/Models/DefectV2/mobilenetv2-12.onnx",
  "logs": "C:/Demo/LucGiac.segai-tune/instance_head",
  "side": 640,
  "epochs": 200,
  "learningRate": 0.015,
  "regularization": 0.001,
  "rotation": 15.0,
  "scaleVariation": 0.15,
  "brightness": 0.15,
  "augment": 1,
  "gpu": 0,
  "originalInput": 0,
  "earlyStop": 1,
  "earlyStopMinGain": 0.02,
  "validation": [
   120,
   121,
   122,
   123,
   124,
   125,
   126,
   127,
   128,
   129,
   130,
   131,
   132,
   133,
   134,
   135,
   136,
   137,
   138,
   139,
   140,
   141,
   142,
   143,
   144,
   145,
   146,
   147,
   148,
   149
  ],
  "instanceHead": 1
 },
 "native_sha256": "85b6dfa8f9d5168265abea82b2bc8680623ef364e19631640ba42e8347358d22"
}

### Measurement selected_semantic_recheck
Raw: C:\Demo\LucGiac.segai-tune\metrics\selected_semantic_recheck.json
{"images": 40, "objects": 642, "tp": 425, "fp": 139, "fn": 217, "precision": 0.7535460992907801, "recall": 0.661993769470405, "f1": 0.7048092868988391, "count_exact": 13, "all_objects_matched": 13, "mean_score": 0.7274092615769712, "mean_tp_score": 0.7411487889273357, "p50_ms": 40.23425000195857, "p95_ms": 46.41942999805906, "native_p50_ms": 25.964400010707323, "native_p95_ms": 28.824074989097426, "phase_p50_ms": {"device": 0.0005, "prepare": 0.6051, "tensor": 0.97305, "infer": 20.8495, "upsample": 1.4412500000000001, "compile": 0.0, "ccscore": 0.19765, "nearest": 0.4869, "v2_total": 24.84225, "inferer_tail": 0.2924, "inferer_total": 25.1439, "export_copy": 0.3167, "export_total": 25.66465}}

### Measurement instance_default
Raw: C:\Demo\LucGiac.segai-tune\metrics\instance_default.json
{"images": 40, "objects": 642, "tp": 320, "fp": 38, "fn": 322, "precision": 0.8938547486033519, "recall": 0.4984423676012461, "f1": 0.64, "count_exact": 1, "all_objects_matched": 1, "mean_score": 0.5542030123026012, "mean_tp_score": 0.5532469322904945, "p50_ms": 22.942000003240537, "p95_ms": 28.767924993007906, "native_p50_ms": 22.516549994179513, "native_p95_ms": 28.342260007048022, "phase_p50_ms": {}}

### Measurement instance_threshold_0p1
Raw: C:\Demo\LucGiac.segai-tune\metrics\instance_threshold_0p1.json
{"images": 40, "objects": 642, "tp": 565, "fp": 135, "fn": 77, "precision": 0.8071428571428572, "recall": 0.8800623052959502, "f1": 0.842026825633383, "count_exact": 13, "all_objects_matched": 6, "mean_score": 0.4778231821528503, "mean_tp_score": 0.5071522251694603, "p50_ms": 25.09845000167843, "p95_ms": 32.52384000807069, "native_p50_ms": 24.707149997993838, "native_p95_ms": 32.136914999136934, "phase_p50_ms": {}}

### Measurement instance_threshold_0p2
Raw: C:\Demo\LucGiac.segai-tune\metrics\instance_threshold_0p2.json
{"images": 40, "objects": 642, "tp": 565, "fp": 89, "fn": 77, "precision": 0.863914373088685, "recall": 0.8800623052959502, "f1": 0.8719135802469136, "count_exact": 27, "all_objects_matched": 12, "mean_score": 0.5013844356031958, "mean_tp_score": 0.5071522251694603, "p50_ms": 27.2387999939383, "p95_ms": 30.60040500713512, "native_p50_ms": 26.837699995667208, "native_p95_ms": 29.959025003336137, "phase_p50_ms": {}}

### Measurement instance_threshold_0p35
Raw: C:\Demo\LucGiac.segai-tune\metrics\instance_threshold_0p35.json
{"images": 40, "objects": 642, "tp": 557, "fp": 75, "fn": 85, "precision": 0.8813291139240507, "recall": 0.867601246105919, "f1": 0.8744113029827316, "count_exact": 27, "all_objects_matched": 12, "mean_score": 0.5092003685009631, "mean_tp_score": 0.5099576706304156, "p50_ms": 26.538949998212047, "p95_ms": 29.64484999902197, "native_p50_ms": 26.144750001549255, "native_p95_ms": 29.20529000548413, "phase_p50_ms": {}}

### Measurement instance_threshold_0p65
Raw: C:\Demo\LucGiac.segai-tune\metrics\instance_threshold_0p65.json
{"images": 40, "objects": 642, "tp": 9, "fp": 2, "fn": 633, "precision": 0.8181818181818182, "recall": 0.014018691588785047, "f1": 0.02756508422664625, "count_exact": 0, "all_objects_matched": 0, "mean_score": 0.6706572120839899, "mean_tp_score": 0.6680025789472792, "p50_ms": 24.74554999935208, "p95_ms": 26.88804000645177, "native_p50_ms": 24.392300001636613, "native_p95_ms": 26.445735002926085, "phase_p50_ms": {}}

### Measurement instance_quality1
Raw: C:\Demo\LucGiac.segai-tune\metrics\instance_quality1.json
{"images": 40, "objects": 642, "tp": 557, "fp": 75, "fn": 85, "precision": 0.8813291139240507, "recall": 0.867601246105919, "f1": 0.8744113029827316, "count_exact": 27, "all_objects_matched": 12, "mean_score": 0.5092003685009631, "mean_tp_score": 0.5099576706304156, "p50_ms": 25.53365000494523, "p95_ms": 28.746900003170595, "native_p50_ms": 25.155700001050718, "native_p95_ms": 28.07975499163149, "phase_p50_ms": {}}

### Measurement instance_affinity_2
Raw: C:\Demo\LucGiac.segai-tune\metrics\instance_affinity_2.json
{"images": 40, "objects": 642, "tp": 557, "fp": 75, "fn": 85, "precision": 0.8813291139240507, "recall": 0.867601246105919, "f1": 0.8744113029827316, "count_exact": 27, "all_objects_matched": 12, "mean_score": 0.5092003685009631, "mean_tp_score": 0.5099576706304156, "p50_ms": 59.483400007593445, "p95_ms": 65.70081499085063, "native_p50_ms": 58.96480000228621, "native_p95_ms": 65.10358500745497, "phase_p50_ms": {}}

### Measurement instance_affinity_4
Raw: C:\Demo\LucGiac.segai-tune\metrics\instance_affinity_4.json
{"images": 40, "objects": 642, "tp": 557, "fp": 75, "fn": 85, "precision": 0.8813291139240507, "recall": 0.867601246105919, "f1": 0.8744113029827316, "count_exact": 27, "all_objects_matched": 12, "mean_score": 0.5092003685009631, "mean_tp_score": 0.5099576706304156, "p50_ms": 37.24234999390319, "p95_ms": 42.974584993498866, "native_p50_ms": 36.82299999491079, "native_p95_ms": 42.29135999921709, "phase_p50_ms": {}}

### Measurement instance_affinity_8
Raw: C:\Demo\LucGiac.segai-tune\metrics\instance_affinity_8.json
{"images": 40, "objects": 642, "tp": 557, "fp": 75, "fn": 85, "precision": 0.8813291139240507, "recall": 0.867601246105919, "f1": 0.8744113029827316, "count_exact": 27, "all_objects_matched": 12, "mean_score": 0.5092003685009631, "mean_tp_score": 0.5099576706304156, "p50_ms": 26.70774999569403, "p95_ms": 30.16195999807678, "native_p50_ms": 26.266350003425032, "native_p95_ms": 29.659074995288393, "phase_p50_ms": {}}

### Extended selection frozen (tuning only)
{"model": "C:\\Demo\\LucGiac.segai-tune\\instance_head\\model.segai", "sha256": "076ab3b7a525eedf44815d11c41b03b4a7cbf671af667ab5b39bfa59b02e4fcb", "settings": {"instance_mode": true, "threshold": 0.35, "quality": 1}, "affinity": 65535, "baseline_sha256": "e58ed1775577ba7a2718c5af841c5fa6a986d4ed695fa8f92fb5651de9f5dbf8", "input": [640, 640], "speed_scale": 1.0, "tuning_only": true, "additional_instance_factor_examined": true}

### Measurement baseline_verified_final
Raw: C:\Demo\LucGiac.segai-tune\metrics\baseline_verified_final.json
{"images": 100, "objects": 1293, "tp": 461, "fp": 568, "fn": 832, "precision": 0.4480077745383868, "recall": 0.3565351894818252, "f1": 0.39707149009474596, "count_exact": 45, "all_objects_matched": 35, "mean_score": 0.7776139027039388, "mean_tp_score": 0.8045255412360172, "p50_ms": 43.88869999820599, "p95_ms": 54.412895000132266, "native_p50_ms": 30.234500001824927, "native_p95_ms": 36.21185499141575, "phase_p50_ms": {"device": 0.0005499999999999999, "prepare": 0.61145, "tensor": 0.9822500000000001, "infer": 24.5413, "upsample": 1.4376, "compile": 0.0, "ccscore": 0.2558, "nearest": 0.60395, "v2_total": 28.691650000000003, "inferer_tail": 0.29275, "inferer_total": 28.97045, "export_copy": 0.31585, "export_total": 29.46165}}

### Measurement baseline_verified_real
Raw: C:\Demo\LucGiac.segai-tune\metrics\baseline_verified_real.json
{"images": 1, "objects": 20, "tp": 1, "fp": 2, "fn": 19, "precision": 0.3333333333333333, "recall": 0.05, "f1": 0.08695652173913045, "count_exact": 0, "all_objects_matched": 0, "mean_score": 0.7843137254901961, "mean_tp_score": 0.788235294117647, "p50_ms": 97.00360000715591, "p95_ms": 120.55247999378479, "native_p50_ms": 33.77060001366772, "native_p95_ms": 41.821069992147386, "phase_p50_ms": {"device": 0.0006, "prepare": 0.7416, "tensor": 0.8735, "infer": 24.0831, "upsample": 1.4083, "compile": 0.0, "ccscore": 0.2477, "nearest": 1.2813, "v2_total": 28.7549, "inferer_tail": 0.916, "inferer_total": 29.6727, "export_copy": 0.7825, "export_total": 31.0976}}

### Measurement tuned_verified_final
Raw: C:\Demo\LucGiac.segai-tune\metrics\tuned_verified_final.json
{"images": 100, "objects": 1293, "tp": 1153, "fp": 123, "fn": 140, "precision": 0.9036050156739812, "recall": 0.8917246713070379, "f1": 0.897625535227715, "count_exact": 76, "all_objects_matched": 31, "mean_score": 0.5177624245469099, "mean_tp_score": 0.5209517403726255, "p50_ms": 28.30060000269441, "p95_ms": 33.497045000694925, "native_p50_ms": 27.83524999540532, "native_p95_ms": 32.92170499553322, "phase_p50_ms": {}}

### Measurement tuned_verified_real
Raw: C:\Demo\LucGiac.segai-tune\metrics\tuned_verified_real.json
{"images": 1, "objects": 20, "tp": 15, "fp": 2, "fn": 5, "precision": 0.8823529411764706, "recall": 0.75, "f1": 0.8108108108108107, "count_exact": 0, "all_objects_matched": 0, "mean_score": 0.4624682174009435, "mean_tp_score": 0.4698489089806875, "p50_ms": 27.716800002963282, "p95_ms": 30.603449993941467, "native_p50_ms": 27.307399999699555, "native_p95_ms": 30.191730000660755, "phase_p50_ms": {}}

## FINAL VERIFIED SUMMARY (not accepted / no task ticks)
|Method|Input|Count exact|Precision IoU.5|Recall IoU.5|Mean score|p50 ms|p95 ms|
|---|---|---|---|---|---|---|---|
|Count native Couter|N/A|N/A|N/A|N/A|N/A|N/A|N/A|
|baseline_verified|640x640|45/100|0.448008|0.356535|0.777614|43.889|54.413|
|baseline_verified_real|640x640|0/1|0.333333|0.050000|0.784314|97.004|120.552|
|tuned_verified|640x640|76/100|0.903605|0.891725|0.517762|28.301|33.497|
|tuned_verified_real|640x640|0/1|0.882353|0.750000|0.462468|27.717|30.603|
Speedup p50=1.550804576370377 native-only=1.0861946634866093; score increased=False; all requested targets=False
Count blocker verified: assigned EAI checkout has only Lib/native/{cuda13,segai}; no BeeCpp.dll under EasyLabel and no Count/Couter implementation in EasyLabel/src. Native Couter belongs to the separate EasyVision/Pattern lineage, not this checkout. Do not substitute connected-components and call it Couter. Independent Count worker timed out after15min without a harness.
Models and hashes: baseline=e58ed1775577ba7a2718c5af841c5fa6a986d4ed695fa8f92fb5651de9f5dbf8; tuned=076ab3b7a525eedf44815d11c41b03b4a7cbf671af667ab5b39bfa59b02e4fcb
Raw data: C:\Demo\LucGiac.segai-tune\metrics; frozen selection: C:\Demo\LucGiac.segai-tune\frozen_selection_extended.json; script hashes: C:\Demo\LucGiac.segai-tune\script_hashes.json
Timing excludes disk decode and IoU; includes native call and mandatory detection extraction; 5 warmups,3 repeats/frame (real15). Native instance head scores and semantic component scores are not calibrated probabilities. Precision/thread limitations remain as documented; no FP16/Pascal claim.
Profiling semantic baseline/tuned: {"baseline_verified": {"device": 0.0005499999999999999, "prepare": 0.61145, "tensor": 0.9822500000000001, "infer": 24.5413, "upsample": 1.4376, "compile": 0.0, "ccscore": 0.2558, "nearest": 0.60395, "v2_total": 28.691650000000003, "inferer_tail": 0.29275, "inferer_total": 28.97045, "export_copy": 0.31585, "export_total": 29.46165}, "baseline_verified_real": {"device": 0.0006, "prepare": 0.7416, "tensor": 0.8735, "infer": 24.0831, "upsample": 1.4083, "compile": 0.0, "ccscore": 0.2477, "nearest": 1.2813, "v2_total": 28.7549, "inferer_tail": 0.916, "inferer_total": 29.6727, "export_copy": 0.7825, "export_total": 31.0976}, "tuned_verified": {}, "tuned_verified_real": {}}
Native PredictInstances does not populate InferGetPhaseTimings: zeros are not claimed as measurements. Real image is source-derived diagnostic, not independent evidence; holdout100 is synthetic and has only2 source screws, tune40 only1 source screw. No real-world/general production claim.
No original models overwritten, no native changes, no commit/push or completed task ticks.
