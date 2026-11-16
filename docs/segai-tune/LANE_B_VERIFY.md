# Lane B — kiểm chứng .NET, app, rollback và giấy phép

Ngày 2026-11-11. Thực hiện trên gateway host; không sửa native deployment hoặc mã hiện có. Artifacts/log riêng tại `tmp/EAI-SEGAI-CUDA/laneB/`.

## Tiến độ

- Đã đọc nguyên văn `.openclaw/tmp/lane_B.task.txt`; `C:\Demo` là junction sang `D:\Demo`.
- `rg` không có trong PATH; dùng git ls-files, Select-String và Python cho tìm kiếm có giới hạn.

- tests: exit=1, 404.1s; log `tmp/EAI-SEGAI-CUDA/laneB/tests.log`.

- harness-build: exit=0; log `tmp/EAI-SEGAI-CUDA/laneB/harness-build.log`.

- app-build: exit=0, 610.7s; log `tmp/EAI-SEGAI-CUDA/laneB/app-build.log`.

## Full .NET tests / recommendation root cause

Command: `dotnet test EasyLabel/EasyLabel.sln --artifacts-path tmp/EAI-SEGAI-CUDA/laneB/tests-artifacts -c Release --logger trx --results-directory tmp/EAI-SEGAI-CUDA/laneB/test-results`.

| Assembly | Pass | Fail | Skip |
|---|---:|---:|---:|
|D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\laneB\tests-artifacts\bin\EasyLabel.Core.Tests\release\EasyLabel.Core.Tests.dll|441|0|0|
|D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\laneB\tests-artifacts\bin\EasyLabel.Online.Tests\release\EasyLabel.Online.Tests.dll|154|0|0|
|D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\laneB\tests-artifacts\bin\EasyLabel.Commands.Tests\release\EasyLabel.Commands.Tests.dll|275|0|0|
|D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\laneB\tests-artifacts\bin\EasyLabel.Camera.Tests\release\EasyLabel.Camera.Tests.dll|102|2|0|
|D:\CODE\EAI\tmp\EAI-SEGAI-CUDA\laneB\tests-artifacts\bin\EasyLabel.SegAI.Tests\release\EasyLabel.SegAI.Tests.dll|1612|1|21|

Solution total: 2584 passed, 3 failed, 21 skipped.

- EasyLabel.Camera.Tests.LabelHarnessTests.WritesOverlayEvidence: Assert.NotNull() Failure: Value is null
- EasyLabel.Camera.Tests.FakeCameraTests.SimulatedFault_RaisesEvent_FailsStream_AndAllowsReopen: System.InvalidOperationException : The previous grab thread has not ended yet; close the camera and try again later.
- EasyLabel.SegAI.Tests.TrainRecommendationTests.Apply_DoesNotOverwriteUserChangedOptions: Assert.Same() Failure: Values are not the same instance
 Expected: TrainOptionValues { Batch = null, Epochs = 200, InputSize = 800, IsUltra = False }
 Actual:   TrainOptionValues { Batch = null, Epochs = 20, InputSize = 640, IsUltra = False }

CaptureServiceTests.ReplaceImage(a.jpg) passed in this full run; no claim of fixing or excluding its flakiness.

Root cause of TrainRecommendationTests.Apply_DoesNotOverwriteUserChangedOptions: commit `1eccd6b` changed `TrainStepRules.DefaultInputSize` from 640 to 800, but the supposedly user-changed size fixture still hard-codes 800. Thus AreDefaults correctly returns true and Apply correctly creates the recommended 20-epoch/640 run. This is a stale fixture, not an overwrite of a changed input. Proposed test-only patch sets the changed size to DefaultInputSize + InputSizeStep. Patch at `tmp/EAI-SEGAI-CUDA/laneB/TrainRecommendationTests.proposed.patch`; not applied to any existing source.

## App build

leProperty] and should not be directly referenced (use the generated property instead) (https://aka.ms/mvvmtoolkit/errors/mvvmtk0034) [D:\CODE\EAI\EasyLabel\src\EasyAI.Wpf\EasyAI.Wpf.csproj]
    92 Warning(s)
    0 Error(s)

Time Elapsed 00:10:10.45


- switch: exit=-999; log `tmp/EAI-SEGAI-CUDA/laneB/switch.log`.

## License audit / app runtime progress

License matrix: THIRD_PARTY_SEGAI.md. All 18 NVIDIA DLLs match package RECORD hashes against deployment manifest. Actual cuDNN 9.28.0.42 wheel EULA has stale explicit cudnn64_7.dll list: exact Windows v9 redistribution grant remains unverified. Intel OpenMP binary terms and full torch notices also unverified. PE version evidence in laneB/versions-small.json.

App build succeeded (0 errors, 92 warnings; 610.7s). App launched after build and stayed alive 120s, but EnumWindows found only hidden WPF infrastructure windows, no visible main UI; native BeeNativeSegAI not present in captured modules, so app DLL loading/UI remains unverified. Own app process was closed/killed after bounded smoke. App runtime logs and module list saved under laneB.

First .NET switch reached native load then exceeded 600s; a fresh retry allows 1200s. NVIDIA-SMI responds, RTX5060 driver621.14, CUDA13.5; no CUDA compute PID at 10:39:48. Missing-cublasLt copy emitted fallback reason naming cublas64_13.dll (it depends on the omitted library); waiting for CPU result. All negative tests run only on laneB temp copies.

- train-independent: exit=6; log `tmp/EAI-SEGAI-CUDA/laneB/train-independent.log`.

- system32-independent: exit=0; log `tmp/EAI-SEGAI-CUDA/laneB/system32-independent.log`.

- system32: exit=0; log `tmp/EAI-SEGAI-CUDA/laneB/system32.log`.

- train: exit=6; log `tmp/EAI-SEGAI-CUDA/laneB/train.log`.

- switch retry: exit=1; `tmp/EAI-SEGAI-CUDA/laneB/switch-retry.log`.

- fallback-independent: exit=0; log `tmp/EAI-SEGAI-CUDA/laneB/fallback-independent.log`.

- fallback-no-cublaslt: exit=0; log `tmp/EAI-SEGAI-CUDA/laneB/fallback-no-cublaslt.log`.

- harness-rebuild: exit=0; log `tmp/EAI-SEGAI-CUDA/laneB/harness-rebuild.log`.

- Additional test project outside .sln: EasyLabel.Ai.Onnx.Tests, exit=0, log `tmp/EAI-SEGAI-CUDA/laneB/EasyLabel.Ai.Onnx.Tests.log`. Native/SAM3 copy suppressed for these managed unit tests only; real app build kept full production copy.

## Inventory / rollback

{
  "added": [
    "cublas64_13.dll",
    "cublasLt64_13.dll",
    "cudart64_13.dll",
    "cudnn64_9.dll",
    "cudnn_adv64_9.dll",
    "cudnn_cnn64_9.dll",
    "cudnn_engines_precompiled64_9.dll",
    "cudnn_engines_runtime_compiled64_9.dll",
    "cudnn_engines_tensor_ir64_9.dll",
    "cudnn_ext64_9.dll",
    "cudnn_graph64_9.dll",
    "cudnn_heuristic64_9.dll",
    "cudnn_ops64_9.dll",
    "cufft64_12.dll",
    "curand64_10.dll",
    "nvJitLink_130_0.dll",
    "nvrtc-builtins64_135.dll",
    "nvrtc64_130_0.dll",
    "onnxruntime.dll",
    "onnxruntime_providers_cuda.dll",
    "onnxruntime_providers_shared.dll"
  ],
  "removed": [],
  "changed": [
    "BeeNativeSegAI.dll",
    "c10.dll",
    "shm.dll",
    "torch.dll",
    "torch_cpu.dll",
    "torch_global_deps.dll",
    "torch_python.dll"
  ],
  "current_top_files": 47,
  "backup_top_files": 26,
  "current_recursive": 49,
  "backup_recursive": 28,
  "backup_matches_before": true,
  "current_matches_after": true
}

Full SHA256/size/attributes: `tmp/EAI-SEGAI-CUDA/laneB/inventory.json`.

- switch-final: exit=0; log `tmp/EAI-SEGAI-CUDA/laneB/switch-final.log`.

- train-final: exit=0; log `tmp/EAI-SEGAI-CUDA/laneB/train-final.log`.

## .NET behavior results (final harness)

### switch-final

- {"kind": "prediction", "backend": "cuda", "count": 20, "nativeBackend": 1, "note": "", "ms": 1324.8162}
- {"kind": "prediction", "backend": "cpu", "count": 20, "nativeBackend": 0, "note": "", "ms": 217.7007}
- {"kind": "prediction", "backend": "cuda", "count": 20, "nativeBackend": 1, "note": "", "ms": 519.2388}
- {"kind": "intel-unavailable", "error": "AI instance predict failed (-8). V2 predict instances backend=Intel GPU input=2448x2048: Intel OpenVINO GPU unavailable. Select CPU or check the Intel driver.", "note": ""}
- {"kind": "prediction", "backend": "cuda", "count": 20, "nativeBackend": 1, "note": "", "ms": 487.9621}
- {"kind": "parity", "allCount20": true, "maxCoordinateOrScoreDelta": 6.103515625e-05}
### train-final

- {"kind": "train-result", "rc": 0, "error": ""}
- {"kind": "train-save", "rc": 0}
### fallback-independent

- {"kind": "prediction", "backend": "cuda", "count": 20, "nativeBackend": 0, "note": "instance head: CUDA runtime file missing or not loadable: cublas64_13.dll (put it next to BeeNativeSegAI.dll or install the CUDA 13 runtime)", "ms": 299164.9702}
### system32

- {"kind": "system32-direct-load", "path": "C:\\Windows\\system32\\onnxruntime.dll", "exists": true, "handle": 140718614183936, "win32": 0, "ntstatus": "not mapped"}
- {"kind": "prediction", "backend": "cuda", "count": 20, "nativeBackend": 1, "note": "", "ms": 417914.7579}
### system32-independent

- {"kind": "system32-direct-load", "path": "C:\\Windows\\system32\\onnxruntime.dll", "exists": true, "handle": 140718614183936, "win32": 0, "ntstatus": "not mapped"}
- {"kind": "prediction", "backend": "cuda", "count": 20, "nativeBackend": 1, "note": "", "ms": 408360.8882}

Switch uses the real internal SegAiPredictor and SegAiGpuBackendSelector through a project reference. Harness assembly is named EasyLabel.SegAI.Tests to use the existing InternalsVisibleTo; it does not replace the product predictor/selector. Reflection retrieves the real inferer handle solely for native last-backend/note inspection. All measured counters are raw threshold=.25 quality=0. Exact TP/FP/FN were not re-scored here.

The System32 trap on this host is a valid loadable ORT DLL, not a reproduced x86 0xC000007B. Deliberately preloading it and then predicting still loads the app-local ORT/providers by full path and produces 20 CUDA detections with empty note. No System32 file was changed.

The negative copy is built from the completed app output under laneB (hardlinks within tmp, not to the installed native files), omits cublasLt64_13.dll, and runs in its own process with a restricted PATH. cublas64_13.dll consequently cannot load its missing dependency; fallback returns 20 detections, native backend 0 and a clear note. This proves missing-dependency fallback, not forced AddDllDirectory/SetDllDirectory API failure injection.

Cold I/O/startup was exceptionally slow in the first processes (first switch timed out at 600s; initial train cancelled while preparing CPU features). Final warmed run evidence supersedes those attempts; timings in these logs are not a performance benchmark.

- Additional test project outside .sln: EasyLabel.Ai.Mcp.Tests, exit=0, log `tmp/EAI-SEGAI-CUDA/laneB/EasyLabel.Ai.Mcp.Tests.log`. Native/SAM3 copy suppressed for these managed unit tests only; real app build kept full production copy.

Train final: real torch_cpu.dll module loaded from installed folder. One actual fine-tune epoch completed: finetune.txt reports epochs=1 best_epoch=1 best_loss=0.584065 parameters=102 parity_max_diff=1.92225e-05. finetune_epochs.csv has one epoch row (0.240925s). Train rc=0 and Save rc=0, smoke.segai=11813253 bytes. No 0xC06D007E. Paths: laneB/train-smoke-retry/{finetune.txt,finetune_epochs.csv,smoke.segai}, train-final.log.

Inventory completed: 47/26 top files, 49/28 recursive; the two additional files are Models/DefectV2/mobilenetv2-12.onnx and mobilenetv2-12-s8s16.onnx, not hidden files. Backup matches all 28 original manifest SHA256 values; installed matches all 49 after-deployment values. Seven replacements: BeeNativeSegAI.dll,c10.dll,shm.dll,torch.dll,torch_cpu.dll,torch_global_deps.dll,torch_python.dll. Twenty-one additions; no removed files. Backup contains every old version needed for directory-set rollback; actual rollback not performed.

- Additional test project outside .sln: EasyAI.Wpf.Tests, exit=0, log `tmp/EAI-SEGAI-CUDA/laneB/EasyAI.Wpf.Tests.log`. Native/SAM3 copy suppressed for these managed unit tests only; real app build kept full production copy.

## All .NET test projects (including three outside solution)

| Project | Pass | Fail | Skip |
|---|---:|---:|---:|
|EasyLabel.Core.Tests|441|0|0|
|EasyLabel.Online.Tests|154|0|0|
|EasyLabel.Commands.Tests|275|0|0|
|EasyLabel.Camera.Tests|102|2|0|
|EasyLabel.SegAI.Tests|1612|1|21|
|EasyLabel.Ai.Onnx.Tests|51|0|0|
|EasyLabel.Ai.Mcp.Tests|139|0|0|
|EasyAI.Wpf.Tests|17|0|0|

**Total: 2791 passed / 3 failed / 21 skipped.** (Reruns not counted twice.)

TRX aggregate notExecuted counter is incorrectly zero for SegAI; skips are counted from its 21 UnitTestResult outcome=NotExecuted entries, consistent with VSTest console.

Camera failure triage: WritesOverlayEvidence searches ancestors of AppContext.BaseDirectory for EasyLabel.sln; the task-required artifacts path is under repo/tmp, not under repo/EasyLabel, so no such ancestor exists (LabelHarnessTests.cs:91-93). This is a test output-location assumption, not evidence that SegAI rendering regressed. FakeCameraTests.SimulatedFault_RaisesEvent_FailsStream_AndAllowsReopen failed in the loaded full suite but passed its isolated no-build rerun (1/1). CaptureServiceTests a.jpg passed in the full suite. No existing camera tests or production code changed.


## Final app runtime evidence (supersedes cold-start attempts)

After native files were cached, the real built EasyAI.exe stayed alive 120s and had a visible top-level window titled EasyAI (PID 20260, HWND 5245608). Its loaded module list includes BeeNativeSegAI.dll from the installed EasyLabel/Lib/native/segai directory, opencv_world455.dll and openvino.dll. Evidence: `tmp/EAI-SEGAI-CUDA/laneB/app-smoke-warm.result.json`, `app-modules-warm.json`, `app-retry-runner.log`. Settings isolated under laneB/app-settings. Own app was closed after capture. No Counter UI click/create/open-project workflow was tested; no render screenshot, so this proves real startup/window/native loading, not end-to-end UI behavior.

C:/Demo did not exist when checked on this gateway; D:/Demo contained the requested image/model, so all runtime commands used those D:/Demo paths read-only.

## Final evidence map

| Task | Proven | Limit / evidence |
|---|---|---|
| C# predictor/backend switch | CUDA -> CPU -> CUDA -> unavailable Intel -> CUDA: all successful predictions 20, last-backend 1/0/1/1, notes empty, max coordinate/score delta 6.103515625e-05 | switch-final.log; Intel GPU hardware/driver absent, explicit -8 error then CUDA recovers |
| .NET train/libtorch | 2 head epochs, 2 instance epochs, 1 actual backbone fine-tune epoch; Train=0, Save=0; no 0xC06D007E | train-final.log and train-smoke-retry files; not a quality/long-training evaluation |
| System32 ORT shadow trap | Explicitly preload System32 ORT, then real predictor still uses app-local ORT + CUDA provider and returns 20 | system32*.log; System32 file was valid x64 here, so original 0xC000007B not reproduced |
| Missing CUDA dependency | Temp copy omitting cublasLt returns CPU backend 0 with named missing/not-loadable cublas64_13.dll reason and 20 detections, exit0 | fallback-independent.log; no AddDllDirectory/SetDllDirectory API-failure injection |
| App | Build 0 errors / 92 warnings; visible real window + installed BeeNativeSegAI module | app-build.log, app-smoke-warm.result.json, app-modules-warm.json; no workflow UI interaction |
| Rollback set | All current49/backup28 hashes match after/before manifests; old versions of all 7 replacements present | inventory.json; no actual rollback performed |
| License | Full 47-DLL mapping, exact NVIDIA package RECORD provenance, documented conditions and unresolved grants | THIRD_PARTY_SEGAI.md; not blanket redistribution approval |
| Tests | 8 projects: 2791 pass / 3 fail / 21 skip; camera race isolated rerun passed | all-tests-summary.json, TRX, camera-rerun.log; test-only recommendation patch not applied |

## Audit

Task EAI-SEGAI-CUDA/laneB. Added only these reviewable sources/docs: docs/segai-tune/LANE_B_VERIFY.md, docs/segai-tune/THIRD_PARTY_SEGAI.md, tmp/EAI-SEGAI-CUDA/laneB/Harness/{LaneB.csproj,Program.cs}, plus laneB-only runner scripts/patch/evidence/build outputs. Compiler assumptions C#12/net9.0-windows x64; no product API/ABI, saved project/model format, production native files or other lanes' outputs changed. No commit/push/stage, no checklist tick. Builds/test commands and exit codes are recorded in *.result.json and logs.
