# QA độc lập: Counter + CUDA (Segment AI + instance head)

Ngày chạy: 2026-11-10 (18:24–18:50, GMT+7). Người kiểm: subagent QA độc lập. Không sửa mã sản phẩm, không commit, không push.
Repo `D:\CODE\EAI`, HEAD `1eccd6b`, gốc so sánh `d7516eb`. DLL thử: `tmp\EAI-SEGAI-CUDA\integrated-instance\BeeNativeSegAI.dll` (764 928 byte, SHA256 `4031C255FD17998E…`, không ghi đè). `EasyLabel\Lib\native\segai\*` không bị đụng.

## Bảng kết luận

| # | Mục | Kết quả | Bằng chứng ngắn |
|---|-----|---------|-----------------|
| 1 | git status/diff: phạm vi, file nhị phân, rác | **FAIL một phần** | Mã Counter/CUDA nằm đúng phạm vi và `Lib\native\segai` sạch (0 thay đổi, file ngày 10/16). Nhưng cây làm việc lẫn thay đổi ngoài phạm vi và rác chưa ignore (xem §1). |
| 2a | Build `EasyLabel.sln` Release | **PASS** | 0 lỗi, 18 cảnh báo, 16 s (`tmp\qa_counter_cuda\build.txt`). |
| 2b | Chạy toàn bộ test | **FAIL (1 test)** | SegAI.Tests 1593 pass / 1 fail / 21 skip; Commands 275, Online 154, Core 441, Camera 104 đều pass. |
| 2c | 2 test fail "đã biết" có từ trước không | **Một nửa** | `TrainRecommendationTests.Apply_DoesNotOverwriteUserChangedOptions`: pass ở `d7516eb`, **fail ở HEAD sạch `1eccd6b`** (worktree riêng, lặp lại được) ⇒ không do thay đổi Counter/CUDA chưa commit, mà do commit `953ee91` hoặc `1eccd6b`. `CaptureServiceTests.ReplaceImage_KeepsExtensionAndWritesNewPixels(a.jpg)`: **không tái hiện** (pass trong lần chạy đầy đủ, 5 lần chạy lại, và ở cả 2 worktree) ⇒ flaky/phụ thuộc môi trường, không kết luận là lỗi mới. |
| 3 | Review mã C++/C# | **PASS có lỗi nhỏ** | TF32 tắt, `disable_cpu_ep_fallback=1`, fallback CPU có ghi chú. Các điểm cần sửa ở §3. |
| 4a | Độ chính xác CPU vs CUDA, 100 ảnh final | **PASS** | CPU và CUDA đều ra đúng TP 1158 / FP 152 / FN 135, count_exact 77/100, all_matched 35 (khớp tài liệu). |
| 4b | Độ trễ CPU vs CUDA | **PASS, số khác tài liệu** | CUDA nhanh hơn CPU ~3,4× (đo thật), nhưng số tuyệt đối khác 24,6/8,2 ms trong tài liệu vì máy bị tải (xem §4). |
| 4c | Provider CUDA thật sự gắn, không rơi im lặng về CPU | **PASS** | `backend=1`, `backend_note=''`, nạp `onnxruntime_providers_cuda.dll`, lần gọi đầu 1,57 s (dựng session + cuDNN EXHAUSTIVE) rồi 11–12 ms (xem §4). |
| 5 | Counter UI (RadioButton thứ 3 ở ProjectDialog) | **PASS (đọc mã), KHÔNG KIỂM ĐƯỢC khi chạy app** | Không mở được app WPF thật trong phiên này (xem §5). |
| 6 | Hash DLL rebuilt ≠ integrated | **Giải thích được, KHÔNG so byte được** | Không dùng `/Brepro`; mỗi lần link tạo TimeDateStamp và GUID RSDS mới (xem §6). |

## 1. git status / diff

- 44 file tracked bị sửa (+779/−62) và các file mới trong phạm vi: `BeeNativeSegAI/OrtCudaBackend.h`, `OvToOnnx.h`, `Infrastructure/SegAiDeviceRules.cs`, `SegAiGpuBackendTests.cs`, `SegAiInstanceCudaWiringTests.cs`, `docs/segai-tune/`, `tools/segai_tune/`.
- **Ngoài phạm vi** (thay đổi ngày 11/08 của việc khác, nên tách commit): `SelfLearnViewModel.cs` (+84), `SelfLearnWindow.xaml` (+28), `SelfLearnViewModelTests.cs` (+33), `tools/autoloop/autoloop.py`, `synthgen.py`, và `tools/autoloop/__pycache__/*.pyc` đang bị **tracked**.
- **Rác/nhị phân chưa ignore** (đừng `git add -A`): `runs/` (22,8 MB), `weights/yolo26n.pt`, `tools/autoloop/weights/yolo26n.pt`, nhiều `.openclaw/tmp/*` (có .pt đến 78 MB, `OpenCvSharpExtern.dll` 68 MB), `tools/segai_tune/CounterTool/obj/*` (apphost.exe…), `tools/segai_tune/__pycache__`, `docs/video/QA_REVIEW_v4.md`. `EasyLabel/Lib/` và `/tmp/` đã nằm trong .gitignore.
- Trong lúc QA chạy, một tiến trình khác dựng `tmp\EAI-SEGAI-CUDA\production-build` (18:34–18:40, DLL 944 640 byte) và dọn `buildroot\artifacts` lúc 18:33. Đây là nguồn tải máy ở §4 và là lý do không còn DLL "rebuilt" để so (§6).

## 2. Build và test

- Build Release `EasyLabel.sln`: 0 lỗi, 18 cảnh báo.
- Test: xem bảng. Một fail còn lại là `TrainRecommendationTests.Apply_DoesNotOverwriteUserChangedOptions` (xác định bằng worktree `d7516eb` và `1eccd6b` ở `tmp\qa_counter_cuda`, nay đã gỡ; `git worktree list` chỉ còn cây chính; cây chính không bị đụng).
- Lưu ý chất lượng test: `SegAiInstanceCudaWiringTests` chỉ **kiểm văn bản mã nguồn** (đọc `SegV2Core.cpp`, `Assert.Contains("instanceCuda"…)`, đếm `instanceCuda.reset()` ≥ 4). Nó không chạy suy luận, nên không chứng minh CUDA hoạt động; chứng cứ hành vi chỉ đến từ §4.

## 3. Review mã (kết luận theo bằng chứng đọc mã)

Đạt:
- TF32 tắt: `OrtCudaBackend.h:94` `use_tf32 = "0"` (mặc định `useTf32=false`; `SegV2Core.cpp:2065` truyền `false`).
- Không rơi CPU im lặng ở tầng ORT: `OrtCudaBackend.h:92` `session.disable_cpu_ep_fallback=1`.
- Fallback có chủ đích: `CompileCuda` (`SegV2Core.cpp:806-824`) đặt `cudaFailed` (dính) + `cudaNote` và ghi `cerr`; `ReportUsedGpu()` (`SegInferCore.h:122-125`) trả `false` kèm `backendNote_` khi CUDA được chọn nhưng chạy CPU; C# xử lý `EntryPointNotFoundException` cho DLL cũ.
- Ghi chú backend hiển thị được qua `SEGAI_InferGetBackendNote`.

Cần sửa / theo dõi (không nghiêm trọng, chưa làm hỏng đo đạc):
1. `OrtCudaBackend.h:64` `SetDllDirectoryW` đặt thư mục tìm DLL **toàn tiến trình** (ảnh hưởng host khác); `:63` kết quả `AddDllDirectory` không kiểm tra. Nên dùng `LOAD_LIBRARY_SEARCH_*` / `AddDllDirectory` có kiểm lỗi, bỏ `SetDllDirectoryW`.
2. `SegInferCore.cpp:337-355` `PredictInstances` không xoá `backendNote_` ở đầu hàm (các hàm khác có: `:207`, `:365`, `:175`) ⇒ ghi chú cũ có thể còn sót sang lần gọi sau.
3. `SegAINativeExport.cpp` ~591-628 dùng biến static `CudaProbeReason()` dùng chung giữa các handle ⇒ race nếu nhiều luồng gọi cùng lúc.
4. Lỗi chạy CUDA lúc `Run` ở nhánh instance đi vào `catch` ngoài và trả rc −8 thay vì tự rơi về CPU cho lần gọi đó (khác với lỗi lúc compile).
5. `Training/SegAiTrainBackend.cs:127` bỏ qua giá trị trả về của `SetGpuBackend`.
6. `SegAiPredictor.cs:106-131` `ConfigureInferer` chỉ cấu hình nhánh CUDA, không đặt lại backend về 0 khi người dùng đổi từ NVIDIA sang Intel/CPU trên cùng handle.
7. `BeeNativeSegAI.vcxproj`: một chỗ thẻ đóng và chú thích dính trên cùng dòng (thẩm mỹ, vẫn build được).
8. Chưa kiểm tra sâu: `OvToOnnx.h` (chỉ xem tổng thể), `SettingsModel/AppSettings/SettingsViewModel`. Không có kết luận về hai phần này.

Tài liệu: `CUDA_BACKEND.md` §10-11 và `REPORT.md` khớp số đo của tôi về độ chính xác. Dòng 141 của `CUDA_BACKEND.md` nói không rõ nguyên nhân hash khác nhau và "kể cả bỏ qua timestamp"; điều đó chưa đầy đủ (xem §6).

## 4. Đánh giá CPU vs CUDA (số đo thật)

Dữ liệu: 100 ảnh final của `C:\Demo\LucGiac.counter` (1293 vật thể), model `v004.segai`, `instance_mode`, threshold 0,2, quality 0, `-Repeats 3`. Kết quả ghi mới vào `C:\Demo\LucGiac.counter\qa_counter_cuda\` (`qa_cpu_a_final.json`, `qa_cuda_a_*`, `qa_cpu_b_*`, `qa_*_c*`), không ghi đè metrics cũ.

| Lần chạy | Backend | TP/FP/FN | count_exact | p50 (ms) | p95 (ms) | native p50 |
|---|---|---|---|---|---|---|
| qa_cpu_a (máy ~99% CPU) | CPU | 1158/152/135 | 77/100 | 47,88 | 151,29 | 46,77 |
| qa_cuda_a | CUDA | 1158/152/135 | 77/100 | 9,81 | 11,24 | 8,65 |
| qa_cpu_b | CPU | 1158/152/135 | 77/100 | 33,17 | 38,79 | 32,21 |
| qa_cpu_c (1) | CPU | 1158/152/135 | 77/100 | 32,15 | 42,95 | 31,22 |
| qa_cuda_c (1) | CUDA | 1158/152/135 | 77/100 | 9,02 | 10,38 | 8,01 |
| qa_cpu_c (2) | CPU | 1158/152/135 | 77/100 | 30,52 | 36,15 | 29,66 |
| qa_cuda_c (2) | CUDA | 1158/152/135 | 77/100 | 8,74 | 10,16 | 7,82 |

Tập "real" (1 ảnh, 20 vật thể): TP 16 / FP 5 / FN 4, p50 9,66 ms (CUDA) và 52,65 ms (CPU, lúc máy quá tải).

So với tài liệu (CPU p50 ≈ 24,6 ms, CUDA ≈ 8,2 ms, 77/100, 1158/152/135):
- Độ chính xác: **khớp tuyệt đối** ở mọi lần chạy, cả CPU lẫn CUDA, nên CUDA không làm đổi kết quả đếm.
- Độ trễ: CUDA tốt nhất đo được 8,74 ms (tài liệu 8,2 ms, chênh ~7%). CPU tốt nhất 30,5 ms, cao hơn tài liệu 24,6 ms (~24%). Tỉ lệ tăng tốc đo được ≈ 3,4–3,5× (tài liệu ≈ 3×). Con số 24,6 ms **không tái hiện được** trên máy ở trạng thái hiện tại.
- Nhiễu: toàn máy chạy 57–77% CPU suốt các lần đo (lần đầu ~99%), do `node` (pid 28008, ~36 000 s CPU tích luỹ), `CounterTool` (pid 21584/43136), `tsc`, tiến trình dựng `production-build` lúc 18:34–18:40 và nhiều `python` của phiên "elec". Nhánh CPU nhạy với tải này (47,9 → 30,5 ms theo mức tải); nhánh CUDA ổn định (8,7–9,8 ms) vì GPU không bị chiếm: `nvidia-smi` báo GPU 0%, 1329 MiB lúc idle. Chưa có lần đo máy hoàn toàn yên tĩnh.

Xác minh CUDA thật sự được dùng (probe riêng `tmp\qa_counter_cuda\probe_cuda.py`, 1 ảnh, 3 lần gọi, tiến trình mới cho mỗi backend):
- `SEGAI_BACKEND=cuda`: `cuda_available()=1`; mọi lần gọi `backend=1`, `backend_note=''`; lần 1 `native_ms=1569` (`infer` 1376 ms + `compile` 183 ms = dựng session ORT + tìm thuật toán cuDNN), lần 2–3 chỉ 12,5 và 11,2 ms (`infer` ≈ 6,4 ms); module đã nạp có `onnxruntime_providers_cuda.dll`, `cudnn_engines_*`, `cublas*`, `cudart64_13`; PID của tiến trình hiện trong `nvidia-smi --query-compute-apps`.
- `SEGAI_BACKEND=cpu` (đối chứng): `backend=0`, **không** nạp `onnxruntime_providers_cuda.dll`, có `openvino_intel_cpu_plugin.dll`; `infer` ≈ 253–257 ms ở lần đo đơn lẻ lúc máy tải nặng (lần compile đầu 168 s, bị nhiễu mạnh; đừng dùng số này làm benchmark, chỉ để chứng tỏ hai nhánh khác nhau).
- Lưu ý: cột PID của `nvidia-smi` cũng liệt kê tiến trình CPU (vì `cuda_available()` đã nạp `nvcuda`), nên nó **không** là bằng chứng riêng; bằng chứng là `backend=1` + module provider CUDA + thời gian `infer`.
- Độ trễ lần gọi đầu CUDA ≈ 1,6 s là chi phí một lần, nên báo cho người dùng cuối (nên "làm nóng" lúc nạp model).
- Chưa kiểm: nhánh chạy khi thiếu DLL CUDA/GPU (không có máy phù hợp trong phiên này); chỉ xác nhận bằng đọc mã.

## 5. Counter UI

- `Views/ProjectDialog.xaml:107-110`: RadioButton thứ 3, `GroupName="ProjectKind"`, `IsChecked="{Binding IsCounterKind, Mode=TwoWay}"`, `AutomationId="ProjectDialogKindCounter"`, chữ `Strings.ProjKindCounter`.
- `ProjectDialogViewModel.cs:54-67,103-114,217`: `IsCounterKind` loại trừ `IsPositionKind` hai chiều, `IsDetectKind` = không Position và không Counter, kết quả `NewProjectInput(... ProjectKind.Counter)`; `[NotifyPropertyChangedFor(IsDetectKind)]` có cho cả hai thuộc tính.
- Chuỗi: `Strings.resx:768` "Counter", `Strings.vi.resx:768` "Đếm (Counter)", `Strings.Designer.cs:923`.
- Counter dùng `IsInstanceKind` ở `ProjectKindStore`, `ProjectKindToolRules`, `LabelMeFolderImporter`, `SegAiSession`, `CanvasViewModel`, `MainViewModel`; build và 441 test Core pass.
- **KHÔNG KIỂM ĐƯỢC**: tôi không khởi động ứng dụng WPF và không bấm thử dialog/tạo dự án Counter (phiên chạy ngầm không có thao tác giao diện). Kết luận chỉ dựa vào đọc XAML/ViewModel và test tự động; ảnh chụp hay thử tay chưa có.

## 6. Hash DLL

- `integrated\BeeNativeSegAI.dll` (761 344 B): TimeDateStamp 1794298081, GUID RSDS `c1d2f920…`.
- `integrated-instance\BeeNativeSegAI.dll` (764 928 B): TimeDateStamp 1794300668, GUID RSDS `f5658549…`.
- `production-build\bin\…` (944 640 B, build khác, dựng 18:35): TimeDateStamp 1794310520, GUID `1a6dfa84…`.
- `BeeNativeSegAI.vcxproj` có `GenerateDebugInformation=true` và **không** có `/Brepro`, nên mỗi lần link ghi TimeDateStamp mới và GUID RSDS ngẫu nhiên mới ⇒ hai bản dựng từ cùng mã vẫn khác hash. Nhận định trong `CUDA_BACKEND.md:141` ("kể cả bỏ qua timestamp"): bỏ timestamp vẫn còn GUID RSDS, nên cách giải thích ở đó thiếu chứ không phải dấu hiệu mã khác nhau.
- Không so từng byte được giữa bản rebuilt và `integrated`, vì `buildroot\artifacts` đã bị dọn lúc 18:33 (chỉ còn `build.log`). Nên xác nhận bằng dựng lại với `/Brepro` hoặc so phần `.text`/`.rdata` sau khi bỏ header và debug directory.

## Việc nên làm trước khi merge
1. Tách thay đổi ngoài phạm vi (SelfLearn, autoloop) và bỏ file rác; thêm ignore cho `runs/`, `weights/`, `*.pt`, `tools/**/obj`, `__pycache__`.
2. Xử lý `Apply_DoesNotOverwriteUserChangedOptions` (hỏng từ commit `953ee91`/`1eccd6b`, không thuộc việc này).
3. Sửa các mục §3 (1, 2, 3, 6 ưu tiên).
4. Chạy tay Counter UI và đo lại độ trễ khi máy yên tĩnh.
