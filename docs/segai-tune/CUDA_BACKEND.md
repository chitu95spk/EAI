# Segment AI – backend NVIDIA CUDA: khảo sát + prototype

Ngày: 2026-11-10 · Phạm vi: chỉ khảo sát/prototype trong bản sao `D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\`.
**Không sửa** `D:\CODE\EAI\BeeNativeSegAI`, không ghi đè `BeeNativeSegAI.dll`, không commit/push.

## 1. Kết luận ngắn

- Chạy được SegAI V2 trên GPU NVIDIA bằng **ONNX Runtime (ORT) CUDA Execution Provider**, là backend thứ ba bên cạnh CPU và Intel GPU/OpenVINO. Head và hậu xử lý C++/CPU giữ nguyên.
- Kết quả trùng CPU **tới ~3e-6 (FP32, TF32 tắt)**, không lệch argmax pixel nào trên 3 cỡ ảnh đã thử.
- Tốc độ (RTX 5060, mỗi ảnh, đã gồm copy host↔device): **~4,5× ở 960, ~5× ở 640, ~4,4× ở 512** so với OpenVINO CPU (bảng mục 4).
- Prototype là chương trình độc lập; **chưa** tích hợp/biên dịch vào BeeNativeSegAI.dll thật. Phần tích hợp chỉ ở dạng diff đề xuất (mục 7).

## 2. Định dạng model

| Mục | Phát hiện |
|---|---|
| Backbone gốc | MobileNetV2 ONNX opset 12 (`EasyLabel\Lib\native\segai\Models\DefectV2\mobilenetv2-12.onnx`) – chỉ là nguồn khởi tạo. |
| Model thực sự chạy | File `.segai` chứa **OpenVINO IR** (`network_xml` + `network_bin`) gồm backbone + 3 tap upsample về Grid = Side/8, Concat 448 kênh, Dense1x1 → Relu → Dense1x1 → Softmax (→ Multiply class-mask tuỳ chọn). Output `[1, classes, H/8, W/8]`. Trọng số có thể đã fine-tune. |
| Op sau `ConstantFolding` | Convolution, GroupConvolution, Add, Maximum, Minimum, Interpolate (linear_onnx, half_pixel, sizes), Concat, Relu, Softmax, Multiply → ánh xạ 1:1 sang ONNX opset 17 (Conv, Conv group, Add, Max, Min, Resize linear/half_pixel, Concat, Relu, Softmax, Mul). |
| Tiền xử lý | Letterbox pad 114, planar RGB, `(v/255 − mean)/sd` ImageNet – giữ nguyên, tensor đầu vào dùng chung với OpenVINO. |

Vì model thật là OpenVINO IR (đã nạp vào bộ nhớ, kể cả trọng số fine-tune), prototype **chuyển `ov::Model` đang nạp sang ONNX trong bộ nhớ** (≈7,5 MB, không phụ thuộc protobuf; header `OvToOnnx.h`), rồi chạy bằng ORT CUDA EP. Không cần file ONNX riêng, không cần engine build, tự theo trọng số của từng `.segai`. Op/dtype lạ → ném ngoại lệ → caller fallback OpenVINO.

## 3. Phương án chọn và lý do

**Chọn: ORT CUDA EP** (`Ort::CUDAProviderOptions` + `AppendExecutionProvider_CUDA_V2`, `use_tf32=0`, `cudnn_conv_algo_search=EXHAUSTIVE`).

- Đồ thị nhỏ, toàn op chuẩn, ánh xạ 1:1 → chi phí tích hợp thấp, kiểm chứng số học dễ.
- Có sẵn gói NuGet `Microsoft.ML.OnnxRuntime.Gpu.Windows 1.31.1` (C++ API, miễn phí).
- Phương án khác đã cân nhắc:
  - **TensorRT (EP/trực tiếp):** có thể nhanh hơn nữa nhưng cần build engine theo từng model/kích thước (mất giây–phút, cache theo GPU), phụ thuộc phiên bản TensorRT; đồ thị đã chỉ ~12 ms nên lợi ích biên không đủ bù độ phức tạp. Gói ORT có `onnxruntime_providers_tensorrt.dll` nhưng **chưa thử**. Có thể là bước tối ưu sau.
  - **cuDNN trực tiếp:** phải tự viết từng lớp, quản lý bộ nhớ, resize, softmax; không đáng công bảo trì.
  - **OpenVINO cho NVIDIA:** không có plugin chính thức (đã xác nhận ở đề bài).

## 4. Số đo thật (RTX 5060, driver 621.14, compute cap 12.0, ORT 1.31.1 CUDA 13)

Harness C++ `native\cuda_parity.exe` (MSVC 2022, OpenVINO 2025.4, OpenCV 4.5.5), 8 ảnh thật (`EAI-SEG960-OPT\...\ESTCSEGAI\images`), letterbox đúng như SegAI. Thời gian là **median 30 lần sau 5 lần warm-up, ms/ảnh, chỉ phần mạng (tensor đã chuẩn bị)**, gồm copy H2D/D2H của ORT. OpenVINO CPU dùng `PerformanceMode::LATENCY`. Đo chạy lại ngày 2026-11-10.

| Model / cỡ | OpenVINO CPU (ms) | ORT CUDA TF32 tắt (ms) | Tăng tốc | max \|Δ\| so với OV CPU | argmax lệch |
|---|---|---|---|---|---|
| ESTC 960 (model thật, `baseline.segai`) | 58,9 | 12,7 | 4,6× | 3,0e-6 | 0 / 115 200 px |
| 640 *(emulated: IR 960 reshape về 640)* | 29,6 | 5,84 | 5,1× | 2,9e-6 | 0 / 115 200 px |
| ONL 512 (model thật, `v001.segai`) | 17,3 | 3,94 | 4,4× | 6,3e-6 | 0 / 32 768 px |

Dải dao động quan sát qua nhiều lần chạy: 960 → OV 59–75 ms, CUDA 11,5–13 ms; 640 → OV 29–33, CUDA 5,1–6,3; 512 → OV 10–17,5, CUDA 3,4–4,2 (CPU dao động theo tải máy).

- **Dung sai khuyến nghị:** `max |Δ| ≤ 1e-5` trên output softmax FP32 và 0 pixel lệch argmax (đạt trên 24 ảnh-chạy ở 3 cỡ).
- **TF32:** bật `use_tf32=1` (mặc định ORT trên Ampere+) làm lệch lớn hơn nhiều (đo 960: max 4,2e-3 trong lần này; các lần trước tới ~1e-2) và đã gặp 1 pixel lệch argmax / 115 200; chỉ nhanh hơn ≈1 ms (11,7 vs 12,7 ms). **Không đáng bật** – đề xuất cố định `use_tf32=0`.
- Chi phí dựng session (chuyển ONNX + khởi tạo cuDNN, chọn thuật toán EXHAUSTIVE): ~0,3–0,8 s khi đã nóng, ~1,8 s lần đầu tiến trình. Phải tính vào "warm-up" như `Compile` OpenVINO.
- Mức "đếm/điểm" ở cấp kết quả cuối (số vùng NG, score) **chưa đo** end-to-end vì head/hậu xử lý chưa nối vào DLL thật; thay bằng so sánh tensor đầu ra từng pixel (chặt hơn): output trùng ~1e-6 nên bước hậu xử lý C++ nhận gần như cùng đầu vào, kết quả đếm/score sẽ trùng trừ trường hợp ngưỡng nằm sát giá trị biên.

## 5. Môi trường build/chạy đã kiểm tra

- MSVC 2022 Professional (qua `vcvars64.bat`), OpenCV 4.5.5 (`C:\OpenCV4.5`), OpenVINO SDK 2025.4.0 (`C:\Program Files (x86)\Intel\openvino_2025.4.0`) – đủ để biên dịch.
- ORT GPU 1.31.1 đã tải về NuGet cache (`C:\Users\TEAM\.nuget\packages\microsoft.ml.onnxruntime.gpu.windows\1.31.1`): `onnxruntime.dll` 16,9 MB, `onnxruntime_providers_cuda.dll` 187,7 MB, kèm `providers_shared`, `providers_tensorrt`. Bản này build cho **CUDA 13 + cuDNN 9** → cần `cudart/cublas/cublasLt/cufft/curand` 13 và cuDNN 9 cạnh app hoặc trong PATH (đã lấy từ pip `nvidia-*-cu13` để chạy thử).
- **Bẫy nạp DLL:** `C:\Windows\System32\onnxruntime.dll` (bản của Windows) che bản đi kèm nếu để dựa vào PATH → gây lỗi `0xC000007B` khi chạy. Phải đặt ORT DLL **cạnh exe/DLL** và nạp bằng đường dẫn đầy đủ (`AddDllDirectory`/`LoadLibraryEx`) trước khi gọi ORT.
- Thiếu `cublasLt64_13.dll` trong đường dẫn → CUDA EP **âm thầm rơi về CPU** (không lỗi). Khi tích hợp phải kiểm tra rõ ràng provider thật sự đã gắn (xem mục 6).
- Máy này chỉ có **một** GPU NVIDIA: RTX 5060 (compute 12.0). Không có P5000.

## 6. Yêu cầu cho RTX 5060 (Blackwell, sm_120) và các GPU khác

- Blackwell sm_120 cần **CUDA ≥ 12.8** (toolkit/runtime) và driver tương ứng. Bản ORT 1.31.1 dùng CUDA 13 → đã **kiểm chứng thật trên RTX 5060** (số đo mục 4), driver 621.14.
- Bản CUDA 13 nhiều khả năng **không còn hỗ trợ Pascal (sm_61, Quadro P5000)** – chưa kiểm chứng vì máy này không có P5000. Nếu cần chạy cả P5000 cần ORT/CUDA 12.x (CUDA 12.x còn Pascal, sm_120 phải ≥ 12.8): dùng gói ORT build CUDA 12.8+ hoặc phát hành hai gói riêng. **Chưa kiểm chứng trên P5000.**
- Khi tích hợp nên phát hiện thiết bị trước: dùng thử tạo session nhỏ trên deviceId 0 (kết quả cache) thay vì chỉ `GetAvailableProviders()` (nó chỉ báo DLL provider có sẵn).

## 7. Đề xuất tích hợp (diff gốc; ĐÃ áp vào repo chính ở mục 10, chưa commit)

Nội dung chi tiết:
- C++: `D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\proposed\cpp_integration.diff.txt`
- C#: `D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\proposed\csharp_integration.diff.txt`

Thiết kế: giữ `bool gpu`; thêm "backend kind" (0 = Intel/OpenVINO, 1 = NVIDIA CUDA); `Device(true)` trả pseudo-device `"CUDA"` khi chọn CUDA; `PredictFrame` rẽ nhánh `OrtCudaBackend`; `UseTiledOriginalInput` tự false cho `"CUDA"` (không bắt đầu bằng "GPU"); mọi ngoại lệ CUDA → fallback OpenVINO CPU và `LastUsedGpu()` trả false để nhãn báo đúng. `onnxruntime.dll` nên delay-load để máy không có CUDA vẫn nạp được DLL.

Danh sách file đề xuất sửa/thêm:

| File | Thay đổi |
|---|---|
| `BeeNativeSegAI\OvToOnnx.h` *(mới)* | từ prototype `native\OvToOnnx.h` |
| `BeeNativeSegAI\OrtCudaBackend.h` *(mới)* | từ prototype `native\OrtCudaBackend.h` |
| `BeeNativeSegAI\SegV2Core.cpp` | include, member `cudaBackend/gpuBackend`, `Device()` (241-246), `PredictFrame` (773-784), thêm `ClassifyCuda`; giữ CPU cho train (2078) ở bước đầu; instance head đã chuyển sang CUDA ở mục 11 |
| `BeeNativeSegAI\SegInferCore.h/.cpp` | `SetGpuBackend/GpuBackend`, truyền xuống impl, `lastUsedGpu_` tính cả fallback |
| `BeeNativeSegAI\SegAINativeExport.h/.cpp` | export mới `SEGAI_InferSetGpuBackend`, `SEGAI_InferGetCudaAvailable` |
| `BeeNativeSegAI\BeeNativeSegAI.vcxproj` | macro `SEGAI_WITH_CUDA`, include/lib ORT, delay-load, copy 3 DLL ORT + runtime CUDA 13/cuDNN 9 |
| `EasyLabel.SegAI\Options\SegInferenceSettings.cs` | enum `SegGpuBackend` + `GpuBackend` (mặc định Intel → file cũ không đổi nghĩa) |
| `EasyLabel.SegAI\Interop\SegAiInfererNative.cs` | 2 DllImport mới |
| `EasyLabel.SegAI\Engine\SegAiInferer.cs` | `SetGpuBackend`, `IsCudaAvailable` (bắt `EntryPointNotFoundException`) |
| `EasyLabel.SegAI\Inspection\SegAiPredictor.cs` (:105) | gọi `SetGpuBackend` |
| `EasyLabel.SegAI\Training\SegAiTrainingEvaluation.cs` (:181, :187-198, :231, :249-250) | nhãn `report.Device`: "NVIDIA GPU / CUDA (ONNX Runtime)" hoặc "CPU (CUDA không dùng được, đã chuyển về CPU)" |
| `EasyLabel.SegAI\Training\SegAiTrainBackend.cs` (:123), `SegAiTrainingPipeline.cs` (:286) | chuyển cấu hình backend (train vẫn Intel/CPU) |
| test `ProductionPipelines.cs` (:126) | cập nhật nhãn dự kiến |
| UI cài đặt (EasyAI.Wpf) | checkbox GPU → ComboBox CPU / Intel GPU / NVIDIA CUDA |

Lưu ý C#: `SegAiInferer.IsGpuAvailable` (88-93) thực chất là `StructuralGpuAvailable()` (OpenCL) chứ không phải kiểm tra Intel OpenVINO – đừng dùng cho lựa chọn mới.

## 8. Phần CHƯA chứng minh được (nói thẳng)

1. **Chưa tích hợp/biên dịch vào `BeeNativeSegAI.dll` thật**; chỉ có harness độc lập + diff đề xuất. Đường fallback, delay-load, `lastUsedGpu_` chưa chạy thử.
2. **Chưa kiểm chứng trên Quadro P5000 (sm_61) và Intel GPU** (máy này không có); chưa rõ ORT CUDA 13 có chạy P5000 không (nhiều khả năng không).
3. Không có model 640 thật: dòng 640 là IR 960 reshape → chỉ để ước lượng tốc độ/độ khớp; độ khớp thật đã kiểm chứng ở 960 và 512.
4. Mới thử 2 model `.segai`; chưa thử nhiều model fine-tune khác, nhiều class, class-mask `Multiply`, model `originalInput` kích thước động (cần dựng session theo từng kích thước, giống `Compile` reshape).
5. Thời gian chỉ gồm phần mạng; chưa đo end-to-end gồm tiền xử lý, head, hậu xử lý, resize bản đồ về ảnh gốc. CPU dao động theo tải máy.
6. Chưa thử TensorRT EP, chưa thử đa luồng/nhiều session đồng thời trên một GPU, chưa đo VRAM/độ trễ lần đầu khi GPU đang bận.
7. (Instance head đã làm ở mục 11.) Huấn luyện/trích đặc trưng (~2078) chưa hỗ trợ CUDA; FeatureModel có vẻ chuyển được bằng cùng bộ chuyển đổi nhưng chưa thử.
8. Chưa đo kích thước/độ phức tạp phát hành: ORT CUDA + cuDNN + cuBLAS CUDA 13 cộng vài trăm MB; cần quyết định đóng gói tuỳ chọn.

## 9. Cách tái lập

```
cd D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\native
build.cmd                                                   # biên dịch cuda_parity.exe (MSVC + OpenVINO + OpenCV + ORT)
powershell -File run.ps1 -Model estc    -Side 960 -N 8 -Tf32 0
powershell -File run.ps1 -Model estc640 -Side 640 -N 8 -Tf32 0 -Reshape 1
powershell -File run.ps1 -Model onl512  -Side 512 -N 8 -Tf32 0
```

Phiên bản Python (kiểm tra chéo): `scripts\parity_bench.py` (env `SIDE`, `TF32`), kết quả cũ ở `results\py_*.json`. Chuyển đổi tham chiếu OpenVINO→ONNX bằng Python: `scripts\ov2onnx.py`.

Thư mục sản phẩm: `D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\` (`native\`, `scripts\`, `results\`, `ir\`, `proposed\`, `pyenv\`).

## 10. Tích hợp (đã áp vào repo chính, 2026-11-10, CHƯA commit, CHƯA QA độc lập)

### 10.1 File đã sửa / thêm

**C++ (`BeeNativeSegAI\`)**: `OvToOnnx.h` *(mới)*, `OrtCudaBackend.h` *(mới)*, `SegV2Core.h/.cpp`, `SegInferCore.h/.cpp`, `SegAINativeExport.h/.cpp`, `BeeNativeSegAI.vcxproj`.
- Export mới: `SEGAI_InferSetGpuBackend(void*, int32_t)` (0 = Intel/OpenVINO, 1 = NVIDIA CUDA), `SEGAI_InferGetCudaAvailable()` (thăm dò thật: nạp ORT + CUDA EP + kiểm tra DLL runtime), `SEGAI_InferGetBackendNote(void*, char*, int32_t)` (lý do fallback). `SEGAI_InferGetLastBackend` đã có sẵn.
- Mặc định vẫn Intel/CPU. CUDA lỗi (thiếu DLL, không có GPU, ORT lỗi) → tự chạy OpenVINO CPU, `UsedGpu=false` và `BackendNote` nêu lý do (ví dụ thiếu `cublasLt64_13.dll`); lỗi được ghi nhớ (sticky) để không thử lại mỗi ảnh.
- Phạm vi CUDA: suy luận/đánh giá và (từ mục 11) instance head của Counter/Position. Huấn luyện vẫn OpenVINO/CPU.
- vcxproj: `SEGAI_WITH_CUDA=1` chỉ khi có header ORT (ThirdParty/đường dẫn ORT); tắt hẳn bằng `-p:SegAiCudaDisable=true`. ORT được nạp động từ thư mục chứa `BeeNativeSegAI.dll`, nên máy không có CUDA vẫn nạp DLL bình thường.

**C# lõi (`EasyLabel\src\EasyLabel.SegAI\`)**: `Options\SegInferenceSettings.cs` (enum `SegGpuBackend`, `GpuBackend`), `Interop\SegAiInfererNative.cs`, `Engine\SegAiInferer.cs` (`SetGpuBackend`, `IsCudaAvailable`, `BackendNote`; DLL cũ → -99 / `EntryPointNotFoundException` được bắt), `Inspection\SegAiPredictor.cs`, `Inspection\SegAiResult.cs`, `Training\SegAiTrainRequest.cs`, `Training\SegAiTrainBackend.cs`, `Training\SegAiTrainingEvaluation.cs` (nhãn thiết bị), `Training\SegAiTrainingPipeline.cs` (:287, train chỉ dùng GPU Intel).
- Chọn NVIDIA mà DLL không hỗ trợ (DLL cũ) → `SegAiPredictor` chạy CPU kèm ghi chú, không văng lỗi.

**UI / cài đặt (`EasyLabel\src\EasyAI.Wpf\`)**: `Infrastructure\SegAiDeviceRules.cs` *(mới)*, `AppSettings.cs` (`SegAiDevice`: "cpu" | "intel" | "nvidia", mặc định cpu), `SettingsAccessor.cs`, `SettingsModel.cs` (dòng chọn 3 giá trị trong hộp thoại Cài đặt), `Resources\Strings.resx` + `Strings.vi.resx` (5 khoá `SetSegAiDevice*`), `ViewModels\SettingsViewModel.cs`, `ViewModels\MainViewModel.cs` (`NotifySegAiDeviceChanged`: lưu, áp vào `session.Inference`, nạp lại model đang mở; áp thiết bị khi mở project).
- Trước đó **không có** checkbox GPU cho Segment AI nên UI mới là một dòng Choice trong Cài đặt. `session.Inference` không lưu trong `project.json` → project cũ không bị ảnh hưởng; `settings.json` cũ thiếu khoá → "cpu"; giá trị lạ → "cpu".
- `Resources\Strings.Designer.cs` không đụng (đã dirty từ việc khác; các dòng Cài đặt tra khoá theo tên).

**Test**: `EasyLabel.SegAI.Tests\SegAiGpuBackendTests.cs` *(mới, 9 test / 13 ca: mặc định + số enum, ánh xạ thiết bị, Normalize, dòng cài đặt, vòng JSON, file cũ thiếu khoá, thiết bị lạ, Snapshot, nhãn CUDA/fallback/cũ)*, `EasyLabel.SegAI.Tests.csproj` (link `SegAiDeviceRules.cs`), `EditorShellRulesTests.cs` (thêm `RowSegAiDevice`), `EasyLabel.OnlineBench\ProductionPipelines.cs` (:126 nhãn CUDA).

### 10.2 Kết quả kiểm chứng thật (RTX 5060, ORT 1.31.1 CUDA 13, TF32 tắt)

- **Kiểm chứng lại 2026-11-10** (`dotnet build EasyLabel\EasyLabel.sln`, Debug): **0 lỗi**, 64 cảnh báo (báo cáo cũ ghi 49). Build C++ lại từ `buildroot\` (đã đối chiếu: 0 file khác với `BeeNativeSegAI\` trong repo), `Rebuild` đầy đủ: thoát 0, 0 lỗi, 0 cảnh báo C++ trong log. DLL mới trong `buildroot\artifacts\...\bin\` có cùng kích thước (761344 byte) nhưng **không cùng hash** với `integrated\BeeNativeSegAI.dll` (ngày 15:08:01), kể cả khi bỏ qua trường timestamp trong PE header. Chưa rõ nguyên nhân (có thể do debug/PDB record hoặc do `integrated\` build từ trạng thái khác). Vì vậy số đo E2E ở mục dưới là của `integrated\BeeNativeSegAI.dll`, không phải của bản rebuild này, và `integrated\` chưa được ghi đè.
- Test:
  - `EasyLabel.SegAI.Tests` `SegAiGpuBackendTests`: **13/13 đạt**.
  - `EasyLabel.Core.Tests`: 441/441 đạt. `EasyAI.Wpf.Tests`: 17/17 đạt.
  - `EasyLabel.SegAI.Tests` toàn bộ: 1611 ca, 1588 đạt, 21 bỏ qua, **2 lỗi**:
    - `TrainRecommendationTests.Apply_DoesNotOverwriteUserChangedOptions`: lỗi xác định (assert trả về 20/640 thay vì 200/800). Code và test này **không đổi** so với commit nền `d7516eb` (không có trong `git status`), không thuộc phần tích hợp CUDA. Vẫn chưa chạy trên cây sạch để xác nhận 100%.
    - `CaptureServiceTests.ReplaceImage_KeepsExtensionAndWritesNewPixels(a.jpg)`: `UnauthorizedAccessException` khi di chuyển file. Chạy lại riêng thì **đạt** → test chập chờn do khoá file, không thuộc phần đã sửa.
- DLL mới dựng ra `tmp\EAI-SEGAI-CUDA\integrated\` (không đụng `Lib\native\segai`).
- **End-to-end thật, đo lại 2026-11-10** (harness `tmp\EAI-SEGAI-CUDA\e2e\E2e.exe`: C# `SegAiInferer.Predict` → `tmp\EAI-SEGAI-CUDA\integrated\BeeNativeSegAI.dll`; model `artifacts\EAI-SEG960\20261023-960-r1\ESTCSEGAI\baseline.segai`; 6 ảnh đầu của `tmp\EAI-SEG960-OPT\native-snapshots\unchanged-native\ESTCSEGAI\images`; mỗi ảnh lấy tốt nhất trong 3 lần sau warm-up; thời gian gồm tiền xử lý + mạng + hậu xử lý; GPU RTX 5060, driver 621.14):

| Lần chạy | CPU (OpenVINO), TB/ảnh | NVIDIA CUDA, TB/ảnh | Tăng tốc |
|---|---|---|---|
| 1 (15:20) | 99,96 ms | 32,76 ms | 3,05× |
| 2 | 65,03 ms | 24,45 ms | 2,66× |

  - Score và số vùng **giống hệt** CPU ở cả 6 ảnh; **0 pixel mask lệch** ở mọi lần chạy ảnh; `usedGpu=True` ở mọi ảnh; `anyFellBack=False`.
  - CPU dao động 60–100 ms theo tải máy. Số đo cũ của báo cáo (60,2 → 23,9 ms, 2,52×) nằm trong khoảng này.
  - Mạng riêng lẻ ở prototype nhanh 4,4–5,1×; end-to-end thấp hơn vì tiền/hậu xử lý còn ở CPU.
  - Log thô: `.openclaw\tmp\e2e_integrated.txt`, `.openclaw\tmp\e2e_integrated_run2.txt`. Kết quả cũ: `tmp\EAI-SEGAI-CUDA\results\e2e_cuda.txt`.
- **Fallback thật, kiểm lại** (`integrated_nocublaslt`, thiếu `cublas64_13.dll`, 2 ảnh): `IsCudaAvailable=False`, `UsedGpu=False`, `BackendNote` = "CUDA runtime file missing or not loadable: cublas64_13.dll (put it next to BeeNativeSegAI.dll or install the CUDA 13 runtime)", score và mask giống CPU.
- **DLL production cũ** (`EasyLabel\Lib\native\segai`, chỉ đọc, không ghi): `SetGpuBackend` trả -99, `IsCudaAvailable=False`, không crash. Trong app, `SegAiPredictor.ConfigureInferer` (dòng 112-119) hạ về CPU kèm ghi chú khi gặp -99, không rơi về Intel GPU. Harness còn lỗi `-8` ở nhánh `SetGpu(true)` do máy không có Intel GPU; đây là hành vi có sẵn của DLL cũ và không đi qua đường app.

### 10.3 Thay DLL (người yêu cầu tự làm, tôi KHÔNG thực hiện)

> **Cập nhật 2026-11-10 (mục 12): đã có bản dựng production CÓ libtorch trong `tmp\EAI-SEGAI-CUDA\production-build\bin` (đã kiểm Counter CPU/CUDA và fine-tune ngắn). Phần CHẶN bên dưới là ghi nhận lịch sử; việc thay DLL production vẫn CHƯA làm, cần QA + sao lưu trước.**
>
> **CHẶN (lịch sử, trước khi có mục 12) – chưa thay DLL production.** DLL trong `integrated\` dựng với `BEESEGAI_TORCH=0`, không có libtorch. Đã kiểm 2026-11-10: repo **không có** `ThirdParty\libtorch` (không có `torch.lib`), nên máy này không dựng được bản có libtorch. DLL production hiện tại phụ thuộc `c10/torch_cpu/torch*/libiomp5md/shm/uv.dll` (chỉ có trong `Lib\native\segai`). Thay DLL này sẽ mất chức năng huấn luyện fine-tune backbone. Để thay thật cần một máy có libtorch (cấu hình Release|x64 như production, `ThirdParty\libtorch\lib\torch.lib` tồn tại) để dựng lại `BeeNativeSegAI.dll` có CUDA + libtorch; sau đó mới chép. **Đừng** chép đè bản `integrated\` khi chưa làm bước này, trừ khi chỉ cần suy luận.
> Kết quả DLL thử nghiệm đã kiểm (E2E, fallback, DLL cũ) đúng cho phần suy luận; chưa kiểm phần huấn luyện với DLL mới.

Các bước (PowerShell, đóng EasyAI trước):

```powershell
$src = 'D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\integrated'      # nên thay BeeNativeSegAI.dll bằng bản dựng có libtorch
$dst = 'D:\CODE\EAI\EasyLabel\Lib\native\segai'
# 1) sao lưu
Copy-Item $dst "$dst.bak-$(Get-Date -f yyyyMMdd)" -Recurse
# 2) chép DLL C++ + ORT + runtime CUDA/cuDNN (22 file; KHÔNG đụng c10/torch*/libiomp5md/shm/uv/openvino*/opencv*/tbb12)
'BeeNativeSegAI.dll','onnxruntime.dll','onnxruntime_providers_cuda.dll','onnxruntime_providers_shared.dll',
'cudart64_13.dll','cublas64_13.dll','cublasLt64_13.dll','cufft64_12.dll','curand64_10.dll','nvJitLink_130_0.dll',
'nvrtc64_130_0.dll','nvrtc-builtins64_135.dll' | % { Copy-Item "$src\$_" $dst -Force }
Get-ChildItem $src -Filter 'cudnn*64_9.dll' | % { Copy-Item $_.FullName $dst -Force }
# 3) kiểm tra: E2e.exe (hoặc mở app, Cài đặt → Segment AI → NVIDIA)
& 'D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\e2e\bin\x64\Release\net9.0-windows\E2e.exe' $dst <model.segai> <thư_mục_ảnh> 6 3
```

Hoàn tác: đóng app, xoá `Lib\native\segai`, đổi tên bản sao lưu `…segai.bak-<ngày>` về `segai`. Kiểm thử không cần thay file: đặt `EASYAI_SEGAI_DIR=<thư mục>` hoặc `NativeSegAiOptions.PreferredFolder`.

Bẫy cần nhớ:
- `C:\Windows\System32\onnxruntime.dll` (Windows ML) nếu bị nạp trước sẽ chặn bản đi kèm → lỗi 0xC000007B; DLL mới nạp ORT bằng đường dẫn tuyệt đối từ thư mục của nó, nhưng khi chẩn đoán lỗi này hãy kiểm tra trước.
- Thiếu `cublasLt64_13.dll` (hoặc DLL runtime khác) → CUDA không dùng được, **tự về CPU** (không báo lỗi) – nhìn `UsedGpu` / `BackendNote`.
- ~2,1 GB tổng (cublasLt 476 MB, cufft 225 MB, cudnn_engines_precompiled 209 MB…). Các DLL CUDA/cuDNN lấy từ gói pip `nvidia-*-cu13` (`tmp\EAI-SEGAI-CUDA\pyenv`): **cần kiểm tra giấy phép tái phân phối NVIDIA** và quyết định đóng gói tuỳ chọn trước khi phát hành. Có thể lược bớt (ví dụ cufft/curand/nvrtc) nhưng chưa thử loại trừ.

### 10.4 Còn chưa chứng minh

1. **Chưa thử trên Quadro P5000 (Pascal, sm_61)** – CUDA 13 nhiều khả năng không hỗ trợ; cần ORT/CUDA 12.x riêng nếu muốn.
2. **Đường Intel GPU**: máy này không có Intel GPU nên không chạy được (DLL cũ trả -8 khi chọn GPU mà không có Intel GPU – hành vi có từ trước, không phải do thay đổi này).
3. Huấn luyện trên CUDA: chưa hỗ trợ (cố ý), chưa thử. (Instance head: đã làm, xem mục 11.)
4. Chưa thử nhiều model/nhiều class/class-mask/kích thước động; chưa thử đa luồng hay nhiều session cùng lúc trên một GPU; chưa đo VRAM.
5. `session.disable_cpu_ep_fallback=1`: mới thử qua nhánh kiểm tra preload, chưa thử các tình huống lỗi runtime khác.
6. **UI chưa chạy thử bằng mắt**: dòng Cài đặt chỉ được biên dịch + test đơn vị (quy tắc/lưu/đọc); chưa mở app thật để bấm chọn và xem model nạp lại.
7. DLL tích hợp `integrated\` chưa có libtorch (xem 10.3); bản dựng có libtorch nằm ở mục 12 (đã chạy fine-tune ngắn qua DLL đó, chưa train đầy đủ).
8. ORT in nhiều cảnh báo ra stderr ("Removing initializer…") – vô hại nhưng ồn.
9. Chưa có QA độc lập, chưa commit. Chưa tick hoàn thành (cần QA độc lập).
10. `TrainRecommendationTests` (1 ca) lỗi xác định nhưng code có sẵn từ commit nền; chưa chạy trên cây sạch để xác nhận.
11. Chưa đo tiền/hậu xử lý riêng lẻ, chưa đo VRAM, chưa đo độ trễ lần đầu khi GPU bận.

### 10.5 Ghi chú kiểm chứng ngày 2026-11-10

- Đã xoá file rỗng `D:\CODE\EAI\SegV2Core.cpp` ở thư mục gốc repo (0 byte, tạo lúc 15:02 cùng ngày, chưa theo dõi bởi git, không có nội dung; nguồn tạo chưa rõ). Mã thật nằm ở `BeeNativeSegAI\SegV2Core.cpp`.
- Không đụng: `EasyLabel\Lib\native\segai` (đọc DLL cũ để kiểm thử, không ghi), `tools\segai_tune`, `C:\Demo\LucGiac.counter`. Không commit, không push.

## 11. Instance head (Counter/Position) trên CUDA – 2026-11-10, CHƯA commit, CHƯA QA độc lập

### 11.1 Vì sao chu kỳ Counter ~25 ms và CUDA có áp dụng không

- Nhánh instance KHÔNG phải mạng riêng: `BuildInstanceNetwork()` cắt backbone V2 (MobileNetV2) tại `FindFeatureConcat` (lưới 448 kênh, stride 8; Side 640 → 80×80) rồi thêm Dense1x1 448→128 → ReLU → Dense1x1 → (C+7) kênh (heat C lớp + 7 kênh hồi quy: offset x/y, log w/8, log h/8, sin kθ, cos kθ, cờ tròn). Một input, một output → khớp `OrtCudaBackend`. Mọi op đã có trong `OvToOnnx.h` (không cần map thêm op).
- Nguyên nhân 25 ms: `PredictInstances` gọi `Device(gpu,false)` (không cho CUDA) nên khi chọn CUDA vẫn trả "CPU". **Đo thật: DLL cũ chọn CUDA vẫn chạy 22,7 ms = tốc độ CPU** (tức bật CUDA ở bước trước không giúp Counter chút nào).
- Phần C++ thuần (không OpenVINO): `Prepare` (letterbox resize, nền xám 114), dựng tensor (tách kênh + chuẩn hoá), sigmoid heat, `DecodeInstances` (decode + NMS), `RefineInstance` (chỉ khi quality = 1), đổi toạ độ ngược letterbox.

### 11.2 Thay đổi

- `SegV2Core.cpp`: `PredictInstances` gọi `Device(gpu,true)`; khi CUDA, tạo lười `instanceCuda` (`OrtCudaBackend`) từ `BuildInstanceNetwork()`, chạy `Run()` rồi kiểm kích thước output `(C + kInstanceRegChannels) × cells`. Lỗi (thiếu DLL, không có GPU…) → `cudaFailed` (sticky) + `BackendNote` "instance head: …" + chạy OpenVINO CPU như cũ. `instanceCuda.reset()` ở `SetGpuBackend`, `ClearSamples`, sau train và `Load` (không giữ session cũ khi đổi model/backend). Ghi thời gian từng pha vào `timings` (prepare, tensor, infer, compile, decode, total).
- `SegInferCore.cpp/.h`: `lastUsedGpu_ = ReportUsedGpu()` sau `PredictInstances` (kết quả báo đúng CUDA hay fallback CPU).
- `SegAINativeExport.cpp/.h`: export chẩn đoán `SEGAI_InferGetLastInstanceTensor` – chỉ có dữ liệu khi đặt biến môi trường `EASYAI_SEGAI_INSTANCE_TENSOR` (mặc định tắt, không tốn bản sao).
- Không sửa C#: `SegAiPredictor.PredictInstances` đã gọi `ConfigureInferer` (SetGpu + SetGpuBackend) trước khi suy luận, nên chọn "NVIDIA" trong Cài đặt có hiệu lực cho Counter khi dùng DLL mới.
- Công cụ đo (`tools\segai_tune`): `seg_native.py` (`SEGAI_NATIVE_DIR`, `SEGAI_BACKEND=cuda`, trả pha/backend/ghi chú), `counter_eval.py` (`CE_OUT` để ghi ra thư mục khác, không đè metrics gốc), `counter_instance_parity.py` (mới). Wrapper `tmp\EAI-SEGAI-CUDA\run_instance_eval.ps1`.
- Test không cần GPU: `EasyLabel\tests\EasyLabel.SegAI.Tests\SegAiInstanceCudaWiringTests.cs` (4 ca, kiểm cấu trúc mã nguồn native: cho phép CUDA, fallback, reset session, chẩn đoán mặc định tắt, báo backend). Chỉ là kiểm tra nối dây; độ khớp số học đo bằng `counter_instance_parity.py` ở dưới.
- DLL thử: `tmp\EAI-SEGAI-CUDA\integrated-instance\BeeNativeSegAI.dll` (dựng riêng, không libtorch). KHÔNG ghi đè `integrated\BeeNativeSegAI.dll` và `EasyLabel\Lib\native\segai\*`.

### 11.3 Số đo thật (RTX 5060, ORT 1.31.1 CUDA 13, TF32 tắt; model `v004.segai`, Side 640, quality 0, ngưỡng 0,2; 5 lần khởi động bỏ ra)

Cùng cách chấm `counter_eval.evaluate` (IoU convex hull ≥ 0,5, ghép tham lam theo score). Chu kỳ = toàn bộ từ ảnh đến danh sách đối tượng, gồm ctypes/Python.

| Tập | Backend | p50 | p95 | native p50 | exact / ảnh | tp / fp / fn | P | R | all_matched |
|---|---|---|---|---|---|---|---|---|---|
| 100 ảnh test (1293 vật) | CPU (OpenVINO) | 24,58 ms | 31,26 ms | 23,84 | 77/100 | 1158 / 152 / 135 | 0,8840 | 0,8956 | 35 |
| 100 ảnh test | CPU (lần chạy lại) | 24,48 ms | 30,31 ms | 23,80 | 77/100 | 1158 / 152 / 135 | 0,8840 | 0,8956 | 35 |
| 100 ảnh test | **CUDA** | **8,21 ms** | **9,26 ms** | 7,50 | 77/100 | 1158 / 152 / 135 | 0,8840 | 0,8956 | 35 |
| Ảnh thật `Cam_20261019_160350_387.bmp` (20 ốc) | CPU | 24,79 ms | 28,87 ms | 24,00 | 0/1 | 16 / 5 / 4 | 0,762 | 0,800 | 0 |
| Ảnh thật | **CUDA** | **8,53 ms** | **10,70 ms** | 7,75 | 0/1 | 16 / 5 / 4 | 0,762 | 0,800 | 0 |
| Ảnh thật, DLL cũ + chọn CUDA | (thực chạy CPU) | 22,73 ms | 26,69 ms | 22,10 | 0/1 | 16 / 5 / 4 | 0,762 | 0,800 | 0 |

- Nhanh hơn ~3,0× (p50 24,58 → 8,21 ms; p95 31,26 → 9,26 ms). Đếm, tp/fp/fn, P, R, exact, all_matched **giống hệt** CPU. mean_score chỉ lệch ~7e-9 (0,507627015 CPU vs 0,507627022 CUDA).
- Ảnh thật: cả CPU lẫn CUDA đều ra 21 phát hiện (16 TP, 5 FP, 4 FN) trên 20 ốc thật → không đếm đúng (đây là chất lượng model v004, không liên quan backend).
- **Độ khớp tensor** (`counter_instance_parity.py`, 101 ảnh = `final` + `real`, 5 817 600 giá trị): tensor đầu ra mạng instance (heat logit + 7 hồi quy) lệch tối đa tuyệt đối 2,57e-5 (logit heat 2,57e-5; hồi quy 7,5e-6; sigmoid heat 8,9e-7; trung bình 8e-7; dải giá trị −18,5…4,9). Detection sau decode+NMS: số lượng khác nhau ở 0/101 ảnh; lệch tâm ≤ 1,2e-4 px, kích thước ≤ 3,1e-4 px, góc ≤ 1,9e-6 rad, score ≤ 6e-7.
- **Fallback** (đã thử): xoá/đổi tên `onnxruntime.dll` khỏi thư mục DLL → kết quả vẫn ra bằng CPU, `LastBackend` = 0, `BackendNote` nêu lý do, cùng số đếm.
- Lần gọi đầu CUDA ~0,95 s (tạo session ORT + dò cuDNN EXHAUSTIVE) so với ~0,19 s CPU; nạp model ~0,49 s cả hai (session CUDA tạo lười ở ảnh đầu). Cần khởi động trước (warm-up) trong ứng dụng thật.

### 11.4 Thời gian từng pha và nút cổ chai tiếp theo (trung vị, `final`, 100 ảnh)

| Pha | CPU | CUDA |
|---|---|---|
| Prepare (letterbox) | 0,56 ms | 0,51 ms |
| Dựng tensor | 0,87 ms | 0,86 ms |
| Mạng (infer) | 22,43 ms | 5,64 ms |
| Decode + NMS | 0,09 ms | 0,09 ms |
| Tổng native V2 | 24,04 ms | 7,25 ms |
| Cả chu kỳ (kể cả ctypes/Python) | 24,58 ms | 8,21 ms |

- Nút cổ chai tiếp theo: pha **infer 5,6 / 8,2 ms** của CUDA, gồm tính toán GPU, chép H2D 4,9 MB đầu vào, chép D2H đầu ra và overhead ORT (`Run()` hiện trả `std::vector<float>` mới, chưa dùng IOBinding/bộ nhớ pinned). Sau đó là dựng tensor 0,86 ms, prepare 0,5 ms, ctypes/Python ~0,7 ms.
- Chưa tối ưu thêm: cách rõ ràng còn lại là fp16/TF32 (sẽ làm lệch kết quả so với CPU – trái yêu cầu khớp) hoặc IOBinding + pinned memory (chưa thử, lợi ích chưa đo). Không làm vì chưa có bằng chứng lợi ích an toàn.

### 11.5 Còn chưa chứng minh / hạn chế (nói thẳng)

1. **DLL production `EasyLabel\Lib\native\segai\*` CHƯA được thay**: nó chưa có thay đổi này. Muốn Counter nhanh ~3× trong app phải thay DLL bằng bản dựng có libtorch ở mục 12 (bản `integrated-instance` không có libtorch nên không dùng được cho production).
2. **Chưa thử huấn luyện trên CUDA**, **chưa thử Pascal P5000 (sm_61)**, **chưa thử Intel GPU**.
3. `quality = 1` (`RefineInstance`) chưa đo trên CUDA; phần này là C++ thuần sau mạng nên dự kiến không đổi, nhưng chưa kiểm.
4. Chỉ thử model `v004.segai` (Side 640) và 1 ảnh thật; chưa thử Side khác, nhiều class hay nhiều session đồng thời.
5. RTX 5060 đang dùng chung với tiến trình khác (~2,8 GB VRAM đã bị chiếm khi đo); CPU cũng ồn vì các agent khác chạy Python → các số có thể dao động; hai lần đo CPU cho p50 24,58 và 24,48 ms.
6. Mã C# không đổi nhưng chưa mở app thật bấm chọn NVIDIA rồi chạy Counter bằng mắt.
7. Build `EasyLabel.sln` Release: 0 lỗi (25 cảnh báo). `EasyLabel.SegAI.Tests` toàn bộ: 1588 qua / 2 lỗi / 21 bỏ qua. 2 ca lỗi nằm ngoài phạm vi thay đổi này: `TrainRecommendationTests.Apply_DoesNotOverwriteUserChangedOptions` (đã ghi ở 10.4 mục 10) và `CaptureServiceTests.ReplaceImage_KeepsExtensionAndWritesNewPixels(a.jpg)` (`UnauthorizedAccessException` khi đổi tên file ở `SegAiSession.ReplaceImage`; chưa điều tra nguyên nhân, chưa chạy trên cây sạch). 4 ca mới + `SegAiGpuBackendTests` + `SegInstanceWiringTests`: 24/24 qua.
8. Chưa có QA độc lập, chưa commit, chưa tick hoàn thành.

## 12. Dựng DLL production có libtorch – 2026-11-10, CHƯA thay DLL production, CHƯA commit, CHƯA QA độc lập

Mục tiêu: có `BeeNativeSegAI.dll` dựng **có libtorch** (giữ huấn luyện fine-tune backbone) **và** có backend CUDA + instance head CUDA, đặt ở thư mục riêng để thay vào app sau QA. **Không** đụng `EasyLabel\Lib\native\segai\*` (kích thước DLL cũ vẫn 884.224 B, SHA256 `85B6DFA8…358D22`), không sửa mã C++/C#, không commit/push.

### 12.1 Phiên bản libtorch đã chọn và lý do

- **libtorch 2.14.1, bản CPU, Windows shared-with-deps, Release (MSVC)**. Căn cứ: chuỗi `2.14.1` trong `torch_cpu.dll` đang ship ở `Lib\native\segai`; `c10/libiomp5md/shm/uv/torch.dll` cùng kích thước với gói pip `torch 2.14.1+cu130`; `torch_cpu.dll` cùng kích thước (305.887.744 B) với bản libtorch CPU chính thức. Hash `torch_cpu.dll` đang ship **khác** hash bản tải về (nguyên nhân chưa điều tra) – hai bản cùng phiên bản/kích thước nhưng không giống từng byte.
- Máy đã kiểm trước: repo không có `ThirdParty\libtorch`; không có libtorch nào khác trên máy dùng được cho C++ (chỉ có gói pip Python).
- Tải bản chính thức từ PyTorch, không tốn phí: `https://download.pytorch.org/libtorch/cpu/libtorch-win-shared-with-deps-2.14.1%2Bcpu.zip` (201.165.149 B) vào thư mục MỚI, sạch, nằm dưới `tmp\` (đã gitignore): `tmp\EAI-SEGAI-CUDA\libtorch-dl\`.
- **SHA256 gói tải về (tự tính): `28FFDDE0A7FF3B431139709B4A91B8B6D59FDE3BE67B76FD88D0C913EF8973F7`.** PyTorch **không công bố** checksum cho gói này (`.sha256` trả 403; ETag là multipart, không phải MD5) nên không so được với giá trị chính thức; chỉ khẳng định được là tải qua HTTPS từ tên miền chính thức.
- Giải nén vào `tmp\EAI-SEGAI-CUDA\production-build\root\ThirdParty\libtorch` (có `lib\torch.lib`, `build-version` = `2.14.1+cpu`), nên `BEESEGAI_TORCH=1` bật (xem `BeeNativeSegAI.vcxproj` dòng 200-241: `SegTorchRoot` mặc định `..\ThirdParty\libtorch`, Release|x64, `torch_cpu.dll` nạp trễ).

### 12.2 Đường dẫn

| Thứ | Đường dẫn (dưới `D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\`) |
|---|---|
| Gói libtorch tải về | `libtorch-dl\libtorch-win-shared-with-deps-2.14.1+cpu.zip` |
| Gốc dựng riêng | `production-build\root\` (`BeeNativeSegAI\` = bản sao mã nguồn repo, `BuildSupport\`, `ThirdParty\libtorch`, `ThirdParty\OpenCV455Contrib` là junction sang `buildroot`) |
| Lệnh dựng + log | `production-build\root\build.cmd`, `production-build\root\build.log` |
| **Thư mục chạy hoàn chỉnh (để thay vào app)** | `production-build\bin\` – 47 file, ~2.333 MB (`BeeNativeSegAI.dll` 944.640 B, SHA256 `1CC2A4EB…43E9D`) |
| Kết quả Counter | `production-build\results\cpu`, `production-build\results\cuda` |
| Script | `run_eval.ps1`, `cuda_probe.py`, `train_smoke.py`, `export_taps.py` (cùng thư mục `production-build\`) |
| Backbone TorchScript tự dựng (chỉ để thử) | `production-build\taps\mobilenetv2-12.taps.pt` + `.taps.map.txt` |

`bin\` = bộ file production hiện tại (26 file) + `BeeNativeSegAI.dll` mới + libtorch 2.14.1 + 21 file mới: `onnxruntime.dll`, `onnxruntime_providers_cuda.dll`, `onnxruntime_providers_shared.dll`, `cublas64_13`, `cublasLt64_13`, `cudart64_13`, `cudnn*64_9` (10 file), `cufft64_12`, `curand64_10`, `nvJitLink_130_0`, `nvrtc64_130_0`, `nvrtc-builtins64_135`. Không file nào của bộ production hiện tại bị thiếu trong `bin\`. **Giấy phép tái phân phối DLL NVIDIA vẫn cần kiểm tra trước khi phát hành** (xem 10.3).

### 12.3 Cách dựng (đã chạy thật)

Mã nguồn cây làm việc hiện tại (**gồm các thay đổi CUDA chưa commit** trong `BeeNativeSegAI\*.cpp/.h/.vcxproj`) được chép sang gốc dựng riêng, nên không đụng bản dựng cũ `buildroot` (kịch bản `EasyVisionIsolatedVerification.props/EasyVisionSegAIRuntime.targets` ở đó **cấm** torch: lỗi `Private build requires no deployment and no torch`). Gốc dựng riêng dùng bản nới `EasyVisionSegAIRuntime.targets` (chỉ báo lỗi khi `EasyVisionDisableNativeDeployment != 'true'`) và props không đặt `SegTorchEnabled=false`.

```cmd
call "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
set TEMP=D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\production-build\root\temp
msbuild D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\production-build\root\BeeNativeSegAI\BeeNativeSegAI.vcxproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m /v:minimal /nologo
```

Kết quả: EXIT=0; DLL nằm ở `root\artifacts\verification\Release\x64\bin\BeeNativeSegAI\`. Phần phụ thuộc CUDA (ORT 1.31.1 GPU + cuDNN 9) lấy từ `integrated\` như mục 5; ORT nạp từ chính thư mục của DLL.

### 12.4 Kết quả kiểm chứng thật

**(a) Counter `v004.segai`, 100 ảnh `final`**, `instance_mode=true`, ngưỡng 0,2, quality 0, 3 lần lặp, dùng `tools\segai_tune\counter_eval.py` với `SEGAI_NATIVE_DIR=production-build\bin` (`run_eval.ps1`):

| Backend | p50 | p95 | TP / FP / FN | Đếm đúng | Khớp hết |
|---|---|---|---|---|---|
| CPU (OpenVINO) | 31,3 ms | 37,4 ms | 1158 / 152 / 135 | 77/100 | 35 |
| CUDA (ORT) | 9,0 ms | 11,3 ms | 1158 / 152 / 135 | 77/100 | 35 |

Ảnh thật (`real`, 1 ảnh, 15 lần): CPU 30,5 / 36,3 ms; CUDA 10,0 / 13,5 ms; cả hai đều TP/FP/FN = 16/5/4. CUDA **giống hệt** CPU về phát hiện, nhanh ~3,5×. Tham chiếu máy yên: CPU 24,6 ms, CUDA 8,2 ms, 77/100 (mục 11.3). Lần đo này CPU chậm hơn vì máy đang ồn (các agent khác chạy), nên **dùng tỉ lệ chứ không dùng số tuyệt đối**; hai lần đo CPU đầu cho 74 ms rồi 31,3 ms, chỉ giữ lần sau.

**(b) Bằng chứng CUDA thật sự gắn vào** (`cuda_probe.py`, 5 ảnh `final`): `InferGetCudaAvailable=1`, `InferGetLastBackend=1` ở cả 5 ảnh, `BackendNote` rỗng, ~7,5-8 ms/ảnh (ảnh đầu 973 ms là khởi tạo phiên). Đối chứng âm: bản sao `bin\` (hard-link) **bỏ riêng `cublasLt64_13.dll`** cho `CudaAvailable=0`, `LastBackend=0` và ghi chú `instance head: CUDA runtime file missing or not loadable: cublas64_13.dll (put it next to BeeNativeSegAI.dll or install the CUDA 13 runtime)`, ~30 ms/ảnh (rơi về CPU, không im lặng, không lỗi). Đối chứng CPU: `SEGAI_BACKEND=cpu` cho `LastBackend=0`, ~30-34 ms. Thư mục thử đã xoá sau khi kiểm.

**(c) libtorch hoạt động – fine-tune backbone ngắn qua DLL mới** (`train_smoke.py`): 12 ảnh train + 4 ảnh val của dataset `C:\Demo\LucGiac.segai-tune`, Side 640, `epochs=3`, `backboneEpochs=2`, `backboneThreads=4`, `backboneTorch=taps\mobilenetv2-12.taps.pt`, CPU. Kết quả: lưu được model (11.453.872 B), tổng 14,2 s trong đó `finetune_backbone` 10,8 s; `finetune.txt`: `epochs=2 best_epoch=1 best_loss=0.0484 parameters=102 parity_max_diff=2.32e-05 parity_ref_max=8.65` (đặc trưng OpenVINO sau khi ghi trọng số = đặc trưng torch); `finetune_epochs.csv`: epoch 1 val 0,0484, epoch 2 val 0,0517. Ca đối chiếu `backboneEpochs=0` (cùng dữ liệu) chạy 4,5 s, không sinh `finetune.txt`/`.csv`. Đây là **bằng chứng libtorch nạp được và fine-tune chạy hết**, không phải thử chất lượng (16 ảnh, 3 epoch).
  - Repo **không** có sẵn file `*.taps.pt`, nên tôi tự dựng từ `mobilenetv2-12.onnx` bằng `export_taps.py` (venv `production-build\venv-export`, cài `onnx` + `onnx2torch`; 3 tap 32@80, 96@40, 320@20; 102/102 tham số khớp tên initializer ONNX theo giá trị). Bản này chỉ để thử, **chưa phải bản đóng gói chính thức**; app cần có quy trình tạo/đóng gói `taps.pt` + `.map.txt` riêng (đã ghi ở `deferred.md` của dự án EAI-LOGO).
  - Đối chứng: bộ DLL production hiện tại (`Lib\native\segai`, chỉ đọc) khi chạy cùng cấu hình `backboneEpochs=1` **dừng bằng lỗi Windows `0xC06D007E`** (delay-load không nạp được module) ở `TrainerTrain`. Tôi chưa điều tra nguyên nhân (không khẳng định là do thiếu libtorch hay thiếu một DLL phụ thuộc); chỉ ghi nhận hành vi.

### 12.5 Lệnh thay vào app (đã làm 2026-11-11, xem mục 13.6)

```powershell
$app='D:\CODE\EAI\EasyLabel\Lib\native\segai'; $new='D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\production-build\bin'
Copy-Item $app "$app.bak-$(Get-Date -f yyyyMMdd-HHmm)" -Recurse            # 1) sao lưu
Copy-Item "$new\*" $app -Recurse -Force                                    # 2) thay (đóng EasyAI trước)
# 3) kiểm: SEGAI_NATIVE_DIR trống -> counter_eval.py chạy trên bản đã thay; Cài đặt -> Segment AI -> NVIDIA
```

### 12.6 Còn chưa chứng minh / lưu ý (nói thẳng)

1. **[Đã lỗi thời - xem mục 13.6: DLL production đã thay 2026-11-11]** ~~DLL production CHƯA bị thay~~; chưa chạy test C++ gốc (`BeeNativeSegAI\test`) trên bản dựng này; chưa QA độc lập; chưa mở app thật.
2. Bản dựng lấy từ **cây làm việc chưa commit** (nhiều file `BeeNativeSegAI\*` đang sửa dở bởi các tác vụ khác tại thời điểm dựng). Trước khi thay phải dựng lại từ cây đã chốt nếu mã đổi tiếp.
3. Số p50/p95 đo khi máy ồn; chưa đo lại trên máy yên trong lần này. Chưa thử Quadro P5000 (sm_61) và Intel GPU.
4. `torch_cpu.dll` trong `bin\` là bản chính thức tải về (hash khác bản đang ship dù cùng phiên bản và kích thước; [đã giải thích ở mục 13.4: chỉ khác timestamp PE]); chưa kiểm tương thích chéo với các DLL torch ship cũ vì `bin\` dùng trọn bộ 2.14.1 mới.
5. Huấn luyện đầy đủ (nhiều epoch/nhiều ảnh), huấn luyện trên CUDA, và Retrain/delta cùng fine-tune: chưa thử (code từ chối tổ hợp Retrain + fine-tune).
6. Không có checksum chính thức của PyTorch để đối chiếu gói libtorch; giấy phép tái phân phối DLL NVIDIA chưa kiểm.
7. Dung lượng `bin\` ~2,3 GB (CUDA/cuDNN ~2,1 GB) – cần quyết định đóng gói tuỳ chọn.


## 13. QA fixes, production-build2 and DLL replacement - 2026-11-11 (NOT committed, NOT QA-signed, task NOT ticked)

Done by a sub-agent; an independent QA must re-run everything. Evidence in `tmp\EAI-SEGAI-CUDA\production-build2\results\`.

### 13.1 Fixed QA findings

| # | Where | Fix | Verification |
|---|---|---|---|
| a | `OrtCudaBackend.h` ~65-80 | `AddDllDirectory` result checked (Win32 error text in the load error + stderr). `SetDllDirectoryW` kept (cuDNN lazy-loads engine DLLs with the legacy search) but NOT applied when the host already set its own DLL directory (`GetDllDirectoryW`), and its failure is reported. | compile + code read only (a failing `AddDllDirectory` cannot be forced) |
| b | `SegInferCore.cpp` 345 | `PredictInstances` clears `backendNote_`/`lastUsedGpu_` like `Predict`. | ctypes test `bt_note2.py`: CUDA requested with `cublasLt64_13.dll` removed -> note set; then `SetGpu(0)` only -> old DLL (1CC2A4EB) keeps the stale note, new DLL returns an empty note |
| c | `SegAINativeExport.cpp` 602-610 | `CudaProbeReason` is a mutex-guarded string returned by value. | `bt_probe_threads.py`: 8 threads x 300 `InferGetCudaAvailable` + `InferGetBackendNote(null)`, no error (race-freedom itself is not proven by a test) |
| d | `SegV2Core.cpp` 2087-2100 | A CUDA `Run` exception in the instance head now sets `cudaFailed`, the note, and falls back to CPU for that call instead of returning rc -8. | compile + code read only: a CUDA Run failure cannot be forced through the public API |
| e | `SegAiTrainBackend.cs` 127 | Uses `SegAiGpuBackendSelector` (new file `Engine\SegAiGpuBackendSelector.cs`): checks the `SetGpuBackend` return; refused CUDA -> CPU + trace warning, never silently Intel GPU. | `SegAiGpuBackendSelectorTests` (fake native) |
| f | `SegAiPredictor.cs` 25, 114 | Same selector: NVIDIA -> Intel/CPU resets the native backend to 0 once (retried if the reset fails). | `SegAiGpuBackendSelectorTests` (8 cases pass) |
| g | `BeeNativeSegAI.vcxproj` 216 | `SEG-LIFECYCLE-r11` comment moved to its own line. | msbuild OK |
| h (new) | `SegBackboneFineTune.cpp` 28 | `EnsureTorchRuntimeLoaded`: preloads `torch_cpu.dll` by full path from the folder of `BeeNativeSegAI.dll` before the first torch call (see 13.3). | train smoke below |

### 13.2 Build
- `production-build2\root` (copy of the repo `BeeNativeSegAI` + `BuildSupport`, libtorch 2.14.1+cpu junction), `msbuild Release|x64 /t:Rebuild`, 0 warnings / 0 errors. Final DLL: 950,272 B, SHA256 `2431C371D43081C923245396D288FEA47D1E8AC91100A3BB26A2523A8CCCFE60`. (A first build without fix h, 947,712 B `64366F0F...`, was superseded.) `integrated\`, `integrated-instance\`, `production-build\` were not touched.
- `production-build2\bin` = 49 files (production-build\bin + new DLL).

### 13.3 0xC06D007E (ERROR_MOD_NOT_FOUND) at the training step - cause found
- Static imports of the installed set are all present by name (dumpbin `/dependents`). Only `torch_cpu.dll` is delay-loaded; the delay-load helper calls `LoadLibrary("torch_cpu.dll")` with the legacy search order, which does not include the DLL's own folder when the host loaded it by full path / `AddDllDirectory`.
- Experiments (python harness, 2 epochs + 1 backbone epoch): installed DLL 884,224 B, default -> FAILS 0xC06D007E; same DLL with its folder on PATH -> OK; first new build -> OK only because `InferGetCudaAvailable` (called by the harness) runs `SetDllDirectoryW(folder)` as a side effect; first new build with that call skipped -> FAILS; final DLL (fix h), call skipped -> OK; final DLL from the app folder -> OK.
- NOT checked: the real EasyLabel .NET app doing a training run (its loader uses `AddDllDirectory` + `LoadLibraryExW`, same class of situation, so it was probably affected too).

### 13.4 torch_cpu.dll hash difference
- Installed (old) `420CACA3C1B19812...53DA`, downloaded libtorch 2.14.1+cpu `F30ABA240ECE6889751DAE2EBEDF937E19D573AE8B85F802E201D204983E1888`, both 305,887,744 B.
- A byte comparison shows only 4 bytes differ: the PE header `TimeDateStamp` (offset 0x138) and the same timestamp repeated in `.rdata` (0x1096C4C4); values differ by 49 s. Code, data, imports are byte-identical. Conclusion: same binary, relinked/repackaged at another moment; functionally identical. Where the previously shipped copy came from is NOT known (not proven it is the pip wheel).
- Source of the new copy: `https://download.pytorch.org/libtorch/cpu/libtorch-win-shared-with-deps-2.14.1%2Bcpu.zip` (201,165,149 B), zip SHA256 `28ffdde0a7ff3b431139709b4a91b8b6d59fde3be67b76fd88d0c913ef8973f7` (computed locally; PyTorch publishes no checksum).

### 13.5 Test and parity results (new DLL)
| Check | Result |
|---|---|
| `EasyLabel.SegAI.Tests` | 1634 total: 1612 passed, 21 skipped, 1 failed = `TrainRecommendationTests.Apply_DoesNotOverwriteUserChangedOptions` (known, also fails at clean HEAD 1eccd6b, not caused here). New `SegAiGpuBackendSelectorTests`: 8/8 pass |
| `EasyLabel.Core.Tests` (ProjectKind) | 441 / 441 pass |
| `EasyLabel.Commands.Tests` | 275 / 275 pass |
| c2d (`C:\Demo\LucGiac.counter3\c2d.easyai.data\models\v001.segai`), instance_mode, thr .25, quality 0, from `production-build2\bin` | CPU and CUDA identical: final100 **85/100**, TP/FP/FN 1232/63/61 (P .9514 R .9528); tune40 33/40, 615/32/27; real exact 20 det 17/3/3 |
| Same eval from the installed app folder after replacement | CPU and CUDA final100 85/100 1232/63/61, real 17/3/3 |
| Train smoke (8 train + 3 val, 2 epochs, 1 libtorch backbone epoch), model in `production-build2\train_smoke\` | OK, no 0xC06D007E (even without `cuda_available`) |
| `dotnet build EasyLabel.sln` after replacement | 0 errors, 28 warnings |

### 13.6 Replacement in the app
- Backup (whole dir, 28 files, hash-verified identical to the original, gitignored): `EasyLabel\Lib\native\segai.bak-20261111-0818`. NEVER delete it.
- Before: `BeeNativeSegAI.dll` 884,224 B SHA256 `85B6DFA8F9D5168265ABEA82B2BC8680623EF364E19631640BA42E8347358D22` (28 files). After: 950,272 B `2431C371...CCFE60` (49 files, 2,347 MB, every file hash-equal to `production-build2\bin`). Full file lists with SHA256: `results\app_before.csv`, `results\app_after.csv`.
- No process had modules loaded from the app folder or from `tmp\EAI-SEGAI-CUDA` at the time of the copy.
- Rollback: delete the new files in `segai` and copy back from `segai.bak-20261111-0818`.

### 13.7 NOT checked / not proven
- Quadro P5000 (Pascal, sm_61) and any Intel GPU (OpenVINO GPU) with this DLL.
- CUDA training (training only ran on CPU; libtorch is the CPU build). Long/full training.
- The real EasyLabel app was not started (no UI test, no .NET training run, no .NET load of the new DLL).
- Redistribution license of the NVIDIA CUDA/cuDNN DLLs and of libtorch: NOT checked.
- Fix (a) and (d) have no behaviour test (cannot be forced); (c) is only a smoke test of concurrent calls.
- `TrainRecommendationTests.Apply_DoesNotOverwriteUserChangedOptions` still fails (pre-existing); the C++ test project `BeeNativeSegAI\test` was not run.
- Measured latencies were taken on a busy machine.