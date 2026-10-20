# EAI-ONL P3b: train 512 and compare with v002

Windows x64 console harness using the real `SegAiSession.Train` pipeline and
`SegAiInferer.Predict`. Defaults: dataset `E:\EAI_TEST`, V2, `InputSize=512`,
`Epochs=60`, output `tmp/EAI-ONL3/train512` under this repository, comparison model
`<dataset>/.easyai/models/v002.segai` (the existing 960 model).

The project follows OnlineBench's SDK layout, `net9.0-windows`, C# 12, nullable,
implicit usings and x64 target. It references SegAI directly and adds no packages.
Its transitive production dependencies supply OpenCvSharp and its Windows runtime.

From `E:\Code\EasyVision_Unisen - NoService`, build and run separately:

```powershell
dotnet build .\EasyLabel\tests\EasyLabel.Train512Bench\EasyLabel.Train512Bench.csproj -c Release -p:Platform=x64 -v:minimal *> .\EasyLabel\tests\EasyLabel.Train512Bench\build.log
dotnet run --project .\EasyLabel\tests\EasyLabel.Train512Bench\EasyLabel.Train512Bench.csproj -c Release -p:Platform=x64 --no-build -- --dataset E:\EAI_TEST --out tmp/EAI-ONL3/train512 --epochs 60 --size 512
```

No build, training, benchmark, harness or Git commands were executed when creating
these files. The leader must verify compilation, native compatibility and behavior.

## Arguments and native runtime

| Argument | Default / behavior |
| --- | --- |
| `--dataset <dir>` | `E:\EAI_TEST`; source files are only read/copied. |
| `--out <dir>` | `tmp/EAI-ONL3/train512`; relative paths resolve from the repo root, not the shell directory. Must stay strictly inside the repo, separate from the dataset. Junction/symlink output paths are rejected. |
| `--epochs <int>` | `60`; validated by production options (1..2000). |
| `--size <int>` | `512`; production validation requires 160..2560, multiple of 32. Saved filename remains `v512.segai`. |
| `--skip-train` | Reuse `<out>/v512.segai`; its network side must match `--size`. Never resumes training. Creates a fresh dataset snapshot for measurement. |
| `--compare <model.segai>` | `<dataset>/.easyai/models/v002.segai`; read-only SegAI V2 reference. |
| `--help` | Print arguments and exit. |

Native loading uses exactly the OnlineBench default
`NativeSegAiLibrary.TryLoad(new NativeSegAiOptions(null, Array.Empty<string>()))`.
The production resolver honors `EASYAI_SEGAI_DIR` and its usual application/ancestor
native-folder search. If necessary, set `$env:EASYAI_SEGAI_DIR` to an **existing**
compatible native distribution before running. No DLL is copied, deployed or built.
V2 training requires `Models\DefectV2\mobilenetv2-12.onnx` beside the loaded
`BeeNativeSegAI.dll`, plus its production dependencies. The selected folder is logged.
The built executable must remain under the repo so root discovery works.

## Training and isolation

Training requires a new/empty output directory. It copies images with adjacent
LabelMe JSON or cached label rasters, preserving relative paths, JSON Train/Val
flags, matching SegAI sidecars/rasters, `classes.txt` and optional
`.easyai/advanced.json` into `<out>/project`. Hidden metadata trees are skipped
during image discovery. The default NG/OK class table is required. Unlabelled JSON
files may be copied, but production `session.Samples` decides which images have
usable labels; every recognized labelled image is measured, with no image cap.
No writable session is opened on `E:\EAI_TEST`. Source registries, models,
project file, recovery, runs and agent history are never imported. `TEMP` and `TMP`
are redirected to `<out>/temp` before native loading.

The request explicitly sets `Version=2`, `InputSize`, `Epochs`, `Retrain=false`
and `ResumeFromModel=null`. This trains a fresh head with the production pretrained
backbone; it does **not** resume the incompatible 960 v002 head. Other options use
production defaults, except early-stop patience is set to the requested epoch
budget, clamped to 5..200, so the default 60 epochs are not shortened by the
automatic window. Larger epoch budgets can still stop early. Epoch metrics and
percentage progress are logged synchronously and flushed after each line.

Box-only annotations (including the current oriented-box dataset) can enable the
production instance head and its default instance-training budget, independently
of the semantic `Epochs=60`. The harness preserves that decision. The comparison
always measures the **mask Predict API**, including models with an instance head.
The pipeline's registered candidate stays in the copied project and is also
published as `<out>/v512.segai` by a staging copy and rename after training succeeds.
No model is activated or deployed. Failure/cancellation never publishes a partial model.

For a repeat measurement without training:

```powershell
dotnet run --project .\EasyLabel\tests\EasyLabel.Train512Bench\EasyLabel.Train512Bench.csproj -c Release -p:Platform=x64 --no-build -- --out tmp/EAI-ONL3/train512 --skip-train
```

`--skip-train` creates `<out>/measure-<run-id>` from the requested dataset. It
appends CSV/log evidence and replaces `compare.md` with the latest run summary.
Each invocation has a unique run ID. An exclusive `run.lock` handle prevents
concurrent invocations from interleaving results; the lock file may remain afterward.

## Measurements and limitations

- For each model and each device request, perform three excluded warm-ups on the
  first labelled image, then one measured `Predict` on every labelled image.
  Both models receive the same decoded original-resolution BGR image and full-frame
  ROI; their order alternates. No host crop or resize substitutes for native inference.
- Reuse the actual production model-loading/configuration path through the same
  reflection binding used by OnlineBench. Both models use the default session
  object scales and inference settings, with each model's own legacy prior correction.
  Embedded report options are not imported into the session. HighSpeed is off and
  the inference threshold defaults to 0.5.
- Time only `SegAiInferer.Predict`, including its native work and managed output
  copies. Exclude loading, decoding, configuration, mask comparison and file I/O.
  p50/p95 use nearest rank `ceil(p*N)` over paired successful images. These are
  per-image latency distributions, not a throughput or end-to-end application benchmark.
- Request CPU and then GPU through `SegInferenceSettings.EnableGpu`. Check both
  `UsedGpu` and `SEGAI_InferGetLastBackend` after every call. An unavailable GPU,
  unsupported native export or fallback is recorded explicitly; fallback latency
  never appears as GPU latency. GPU coverage can be partial. OpenVINO GPU selection
  does not prove a specific physical/iGPU adapter identity; no adapter-ID API is exposed.
- Compare the raw predicted NG mask bytes (`nonzero = NG`), without extra host
  filtering, at matching original-image dimensions. v002 is the reference. CSV
  records latency for both models, both NG areas, intersection, union and IoU per
  image. The report includes mean/pooled IoU, both-empty counts, nonempty-only IoU
  and mean absolute NG-area fraction difference. Both-empty per-image IoU is 1;
  pooled IoU is unavailable if all unions are empty.
- **This is not accuracy.** `E:\EAI_TEST` has only OK labels, no NG ground-truth
  pixels. High agreement (especially empty masks) proves neither defect recall
  nor segmentation quality. The reference predictions are not ground truth.
  Train and Val images both participate; no held-out accuracy is claimed.

Output paths print before training. `train.log` and `compare.csv` are flushed
incrementally; `compare.md` is atomically refreshed after each successful pair and
on cancellation/failure. A GPU failure adds an unavailable CSV row and report note.
CPU failure exits 1; GPU unavailability alone retains CPU results and exits 0.
Ctrl+C requests cooperative cancellation, flushes its log message immediately,
waits for the current native call to return, and exits 130 with completed evidence
retained. A second invocation after interrupted training needs a new empty output
directory unless `v512.segai` was already published and `--skip-train` is selected.
Hard process termination/power loss cannot guarantee a final summary; flushed CSV
rows remain the recovery evidence. No executable verification has yet been performed.
