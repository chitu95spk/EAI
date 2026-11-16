# libtorch CUDA cho train Segment AI (RTX 5060) – nhật ký tiến độ

Bắt đầu 2026-11-11. Không commit/push. Không ghi đè `EasyLabel\Lib\native\segai` (QA đang chạy trên build4).

## Bước 1 – đọc hiện trạng (xong)
- Mã train: `BeeNativeSegAI\SegBackboneFineTune.cpp` (`Run`), preload `torch_cpu.dll` ở `EnsureTorchRuntimeLoaded` (sửa 0xC06D007E, CUDA_BACKEND.md mục 13.3).
- vcxproj: `SegTorchRoot` mặc định `..\ThirdParty\libtorch`; chỉ link `torch.lib;torch_cpu.lib;c10.lib`, delay-load `torch_cpu.dll`; copy `lib\*.dll` sang OutDir. Không cần đổi vcxproj cho CUDA vì không link torch_cuda (nạp lúc chạy bằng LoadLibrary).
- GPU thấy được: 1 GPU `NVIDIA GeForce RTX 5060`, driver 621.14, 8151 MiB.

## Bước 2 – tải libtorch CUDA (đang chạy)
- URL: `https://download.pytorch.org/libtorch/cu130/libtorch-win-shared-with-deps-2.14.1%2Bcu130.zip` (301 -> `download-r2.pytorch.org`), HEAD trả 200, Content-Length 3.934.297.699 B. Cùng phiên bản 2.14.1 với bản CPU, nên ABI header khớp.
- Đích: `tmp\EAI-SEGAI-CUDA\libtorch-cu130-dl\libtorch-cu130.zip` (thư mục mới, chỉ chứa zip).

## Bước 3 – sửa mã (xong, chưa biên dịch)
- `SegBackboneFineTune.cpp`: `EnsureTorchCudaLoaded` (nạp `c10_cuda.dll` + `torch_cuda.dll` bằng đường dẫn đầy đủ, `LOAD_WITH_ALTERED_SEARCH_PATH`), `ChooseTrainDevice` (env `BEESEGAI_TRAIN_DEVICE=cpu|cuda|auto`, mặc định auto), `RunOn(device,…)` chuyển backbone/head/classWeight/dữ liệu sang device, snapshot/ToMat qua `.cpu()`, `Run` bọc: nếu CUDA ném exception -> chạy lại toàn bộ trên CPU. In `[SegFineTune] device=… train_seconds=…` ra stderr; `Result::device` (header) giữ chuỗi device.

## Bước 4 - smoke train CUDA vs CPU (2026-11-11, xong)
- Build: 	mp\EAI-SEGAI-CUDA\build5-gpu-clean\runbin (DLL build5 SHA256 B46FC6CF747DDB37E64A17852181EDA3AB036A3E6FD7A72CCD0BC870F46DEDE0, torch_cpu.dll 306.006.528 B, torch_cuda.dll 422.656.512 B). CHƯA copy vào app EasyLabel\Lib\native\segai.
- Harness: production-build\train_smoke.py, backbone 	aps\mobilenetv2-12.taps.pt, 4 epoch backbone, 3 instance epoch, rotation 12, batch 4.
- GPU (BEESEGAI_TRAIN_DEVICE=cuda): log device=cuda:0, finetune_backbone 4.97 s, train_total 8.37 s, train_seconds 9.1 (ok=1).
- CPU (BEESEGAI_TRAIN_DEVICE=cpu): finetune_backbone 21.27 s, train_total 24.16 s, train_seconds 24.8 (ok=1).
- Loss epoch 1 giống nhau: train 0.854917, validation 0.645657. Model size 11.453.872 B cả hai.
- Caveat: máy đang chạy tiến trình bench YOLO (	ools\autoloop\bench_iou.py, không phải của phiên này) nên số đo chỉ là ước lượng. Chưa kiểm chứng bằng nvidia-smi trong lúc train.
- Chưa đo: Counter c2d trên DLL build5, SegAI.Tests đầy đủ, UI, license cuDNN/cublas/libtorch/OpenMP.
