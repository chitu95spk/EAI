# Online benchmark (EAI-ONL P3a)

Windows x64 console, .NET 9 (`net9.0-windows`, required by SegAI/Camera), C# 12, nullable enabled.
No new package references. SegAI, Camera and Ai.Onnx project references match SegAI.Tests;
OpenCV and the existing DirectML ONNX Runtime package flow transitively. CPU execution is the default.

From `E:\Code\EasyVision_Unisen - NoService` (commands for the verifier; not executed during authoring):

```powershell
dotnet build EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64
dotnet run --project EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64 --no-build -- --help
# Model-free deterministic native-frame generator check: 5472x3648 Mono8 raw/PNG equality only
dotnet run --project EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64 --no-build -- --synthetic-smoke
# Future synthetic inference requires an explicit production model; it never probes datasets or trains a fallback model
dotnet run --project EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64 --no-build -- --synthetic-frame --model E:\path\to\model.segai --threads 1
dotnet run --project EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64 --no-build -- --selftest --threads 1
dotnet run --project EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64 --no-build -- --warmup 20 --iterations 200 --threads 1,2,4,all
# P3a: CPU versus GPU, each with whole/512/960/half native ROIs
dotnet run --project EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64 --no-build -- --gpu both --roi all --threads all --model E:\EAI_TEST\.easyai\models\v002.segai
# Proxy mode: original labelled images, all four ROIs, up to 40 NG-labelled images
dotnet run --project EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64 --no-build -- --accuracy --gpu both --threads all --model E:\EAI_TEST\.easyai\models\v002.segai
# Tiny real-native smoke of the ROI matrix (not 5 MP evidence)
dotnet run --project EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64 --no-build -- --selftest --threads 1 --gpu off --roi all
```

`--help` exits before repository, model or dataset discovery. `--synthetic-smoke` creates deterministic
5472x3648 Mono8 pixels (19,961,856 bytes), writes no
dataset or evidence artifact, and validates a managed lossless PNG encode/decode against the raw
SHA256. It does not load a SegAI model, invoke SegAI/ONNX inference, call `ModelFinder`, train, or
read a dataset. It is a generator correctness check, not a latency or throughput measurement.

`--synthetic-frame` is the later native-size inference mode. It uses the same raw Mono8 data and
encoded PNG decode path, creates its JPEG only after the explicitly requested real inference path
has started, and requires `--model <file.segai>`. It rejects `--accuracy`, `--selftest`, `--onnx`,
and `--dataset`; therefore it cannot fall through to model discovery or `TrainMinimal`. Its PNG-only
decode selection avoids describing PNG bytes as JPEG bytes. No full inference or benchmark result is
claimed by this change.

`--gpu on|off|both` defaults to `off`; `--roi whole|512|960|half|all` defaults to `whole`.
`--dataset <dir>` defaults to `E:\EAI_TEST`; relative paths resolve from the repository root.
It supplies the read-only PNGs, accuracy labels, dataset model registry/candidates and fallback
training source images. `--model <file.segai>` still pins the exact model and is passed to workers.
The report states the dataset and selected SegAI model, including when configurations fail.
With an explicit `--dataset`, latency frames use the first reliably rasterized **NG-labelled** PNG
in ordinal path order (same production labels as the accuracy proxy), skipping images without NG;
missing/invalid labels are skipped; finding no eligible NG image fails setup. Without `--dataset`,
the original ordinal-first PNG selection remains. Tiling/resizing and the full latency chain remain
unchanged: encoded-frame decode, production host preparation, Predict with ROI, postprocessing,
JSON and one transport choice.

Side-by-side datasets are supported: `<dir>/<stem>.png`, `<dir>/<stem>.json` (LabelMe), and
`classes.txt` containing `NG` and `OK`. NG `oriented_rectangle` shapes with four points use the
existing production LabelMe converter/rasterizer; OK shapes are preserved as OK. If the matching
JSON is absent, the existing `<dir>/.easyai/cache/<stem>.labels.png` fallback is used. Matching
`.seg.json` sidecars retain the existing production hole handling. Dataset files are never written.
For the staged 18-image 5472x3648 dataset, add `--dataset tmp/EAI-ONL4/dataset` and
`--model <path-to-v512.segai>` to the latency or `--accuracy` command. These source dimensions do
not change the 2592x1944 latency frame size; accuracy uses original dimensions.

With neither `--gpu` nor `--roi`, the previous CPU/whole-frame instance-or-mask selection remains. Supplying either
flag explicitly selects the production **mask Predict** API, including on instance-head models, for
comparable CPU/GPU/ROI measurements. Names include `gpuoff`/`gpuon` and `roiwhole`/`roi512`/`roi960`/
`roihalf`; `summary.csv` also has GPU, ROI, actual-device and ROI-rectangle columns. YOLO remains
CPU/whole-frame and runs once per thread setting, independently of the SegAI matrix.

`on` sets the same `SegInferenceSettings.EnableGpu` flag as the app, through its production
`ConfigureInferer`; it does not select DirectML or an ONNX replacement. For V2, native selection
requires an Intel OpenVINO GPU. Every mask prediction checks `UsedGpu` and directly reads the
production native last-backend export (whose absence is a failure, not assumed CPU). CPU fallback,
unavailable GPU/OpenVINO, model initialization and prediction failures appear in report setup notes
and return exit 2; failed configurations have no partial summaries. Actual device is reported as
CPU or Intel GPU/OpenVINO (V1: GPU/OpenCL matching); the production API does not export the physical
adapter name/ID. The legacy default instance path cannot reliably report actual device and says so.
The generic `IsGpuAvailable` probe tests structural OpenCL, so it is not used to gate V2 OpenVINO.

Latency ROIs are image-centred: 512x512, 960x960, and `half` = 1296x972. The latter is half the
reference frame's width and height, **one quarter of its pixels**. The complete 2592x1944 Mat and
ROI rectangle reach `SegAiInferer.Predict` / native `SEGAI_InferPredict(roiX,roiY,roiW,roiH)`.
No host crop or extra resize replaces it. ROI rows exclude nothing else: acquisition, decode,
host preparation, JSON and transport retain their previous boundaries and full-frame payloads;
native full-frame output copies and the production valid-ROI postprocessing mask remain included.
The serial-budget table still sums same-iteration processing plus one transport and applies the
unchanged nearest-rank percentiles. Selftest clamps oversized ROIs to its 64x48 image and retains
exactly three measured iterations per config; it requires the same real PNG/native prerequisites.

`--accuracy` adds an optional SegAI-only proxy after the ordinary latency matrix: original PNGs, no
tiling/JPEG/mono conversion, all four ROIs regardless of `--roi`, and selected GPU modes/thread
settings. It scans read-only matching
LabelMe JSON and `.easyai/cache/<stem>.seg.json` / `<stem>.labels.png`, using the production
`LabelMeIO`, `SegLabelMeConverter` and `SegLabelRaster` implementations. JSON is authoritative;
hash-matched sidecars preserve holes, otherwise embedded JSON holes apply. Cache-only labels use
production normalization. Unknown/unconverted shapes, nonfinite geometry, unsupported class maps
and dimension mismatches are rejected and disclosed; source data is never changed.

Up to 40 images with NG pixels are selected in ordinal path order. Each ROI centres on the union
bbox of **all labelled NG pixels**; borders shift the ROI to fit, and smaller images shrink it.
`half` remains 1296x972 even for other frame dimensions. After production threshold/area filtering,
recall proxy = predicted NG pixels overlapping labels / **all labelled NG pixels in the image**
(including outside the ROI). The requested precision proxy = predicted pixels outside NG labels /
total predicted pixels, **lower is better**; precision is its complement. Zero predictions makes
that fraction N/A, with recall zero. Summary ratios pool pixel counts; `accuracy.csv` includes each
image's counts, ratios, ROI, source, model SHA256, device and latency. `summary.csv` and `report.md`
include per-mode nearest-rank p50 inference latency across images. This mode warms up on the first
image per GPU/ROI, then measures **one inference per image**; `--iterations` does not multiply it.
Timing excludes decode/host preparation/postprocessing/scoring; first unseen image dimensions may
still incur native compilation. A failed GPU/ROI mode contributes no partial proxy summaries.
`--accuracy --selftest` is rejected: tiny smoke images cannot provide real-label proxy evidence.

This is a **TRAINING-SET recall proxy, not held-out accuracy**: v512 will be trained on the same
images in `tmp/EAI-ONL4/dataset`. It cannot establish generalization to unseen images. Historically,
v002 was trained on the default `E:\EAI_TEST`. Prior read-only inspection of that default found
84 root PNGs, 55 matching root `E:\EAI_TEST\*.json` files and
55 `.easyai/cache/<stem>.labels.png` plus 55 matching `.seg.json` sidecars. Those 55 JSON files are
2448x2048, contain 361 `OK` shapes and zero `NG` shapes; their sidecars contain 361 class-2 regions.
The decoded cache masks contain only 0 and 2, never NG=1. NG filename suffixes do not label defect
pixels. These images cannot define the requested defect-centred ROI/recall denominator. On this
layout, `--accuracy` records the missing NG labels as setup notes and still returns ordinary latency
rows. A future matching NG-labelled JSON or same-size NG mask enables scoring without harness
changes. OK-only images are counted as skipped.

Build tools generate their usual bin/obj folders beneath the new project; harness runtime data stays
in `tmp/EAI-ONL/` and results in `artifacts/EAI-ONL/<run-id>/`. To choose a run directory, append
`--out artifacts/EAI-ONL/my-unique-run` (must be empty/new). Junction/symlink ancestors are rejected.
Preserve earlier evidence. `--selftest` forces 0 warm-up and 3 measured iterations on 64x48 real-texture
frames and, unless `--model` is supplied, the cached tiny V1 model. It still requires real PNG input
and the real native DLL; there is no fake inference fallback. Its timings are not 5 MP evidence.

Native DLL location is **exactly the test mechanism**: `NativeSegAiOptions(null, Array.Empty<string>())`
with `NativeSegAiLibrary`/`NativeSegAiFolderResolver`: `EASYAI_SEGAI_DIR`, app output, ancestor
`bin/Release`; the resolver also supplies the native dependency search directories. Nothing is copied
into a native DLL folder. If necessary set `$env:EASYAI_SEGAI_DIR` to an existing compatible native
folder before running. The nearest ancestor containing `EasyLabel` and `BeeNativeSegAI` is the repo
root, as in `SegAiFixtures`. DLL compatibility is checked by production loading, not file existence.

Model search: active entry of `<dataset>/.easyai/models/registry.json`, other read-only dataset
models, the exact `SegAiFixtures` candidates, `tmp/EAI*/`, then EasyLabel/Models/artifacts fixtures.
The first native-loadable model is used; `--model <file.segai>` pins it. Prior default-dataset inspection found
active `v002.segai`, with `side: 960` (960x960 neural input), but it has **not been executed** here.
The runtime report records the actual selected model, SHA256, format and network side; no silent
input-size reduction is performed. Failed predictions are reported as failed configurations.

If no loadable model exists, train via the real `SegAiSession.SetAnnotations`, `SetMode`, `Train`
API used by `SegAiTrainingServiceTests`, with its default native backend: three real PNGs from the
selected dataset resized to
64x64, two Train/one Val, benchmark-only synthetic NG/OK square labels, V1 structural profile,
8 trees/depth 4/min sample 2. Cache in `tmp/EAI-ONL/model/minimal-v1.segai`; an explicit `--dataset`
uses a dataset-path-hashed subdirectory to avoid reusing the default dataset's minimal model.
Reuse on later runs with the same dataset path.
With fewer than three images, reuse images and disclose it. This is a smoke/latency baseline only;
its model complexity and artificial labels cannot establish real defect quality or V2 latency.
Training and loading are excluded from all samples. Failed training stops with a diagnostic report.

For ONNX, supply `--onnx <trained-yolo-detect.onnx>` or `EASYAI_CUSTOM_ONNX`, or put a model with a
`.classes.txt` sidecar/YOLO name in the searched folders. Only the existing YOLO detect input/output
contract is accepted: 1x3x640x640 float, output [1,4+classes,N], metadata names or classes sidecar,
as in `CustomDetectModelTests`. It uses production `OnnxSessionFactory` with CPU intraOpThreads=N
and production `YoloDecoder.Preprocess/Decode` plus `Nms.Apply`. SAM2, OCR and classifier backbones
are different production contracts and are explicitly not substituted. Missing compatible YOLO is
reported as unavailable, not measured with a dummy graph.

SegAI loads through a scratch `SegAiSession`, binds its production predictor's preprocessing,
configuration and NG/OK analysis methods once, then times `SegAiInferer.Predict` (or the actual
instance-head `PredictInstances`). Internal method names are intentionally fail-fast reflection
bindings so production refactors cannot silently benchmark a substitute implementation. Instance
coordinates are produced inside native inference; the external postprocess stage simply returns
them. For mask models, the exact production analyzers produce centroid/bounds/size/angle metrics.
JSON serializes these positions/metrics and scores, not image-sized masks.

SegAI has no supported inference-thread setter. The harness truthfully labels its N sweep as
**EMULATED affinity-only**, and sets OpenCV's own thread option separately. Fresh workers apply
`SetProcessAffinityMask` before native initialization. ONNX additionally sets its actual intra-op
thread option. The first N available *logical* CPUs do not guarantee N physical cores; the 2-core
i7-7600U has four logical CPUs, and affinity does not reproduce that CPU's performance.
No unsupported environment variable is presented as a native thread control.

All outer measurements use `Stopwatch.GetTimestamp`; optional SegAI native phase diagnostics are
nested and overlap `infer`. When the loaded DLL exposes the split timing API and
`EASYAI_SEGAI_SPLIT_TIMING=1` is present at worker startup, `backbone_ms` and `head_ms` are emitted
as diagnostic stages with the basis `native split diagnostic - overlaps infer, do not add`.
Native instrumentation performs an extra backbone pass after the full graph and derives
`head_ms = max(0, native_full_graph_ms - backbone_ms)` from the third native API value. Measured
`infer` and serial budgets therefore include that added backbone work and are instrumented figures;
the diagnostic stages must never be added to `infer` or a serial budget. SegAI network
resize/normalise is encapsulated by native inference; do not add a duplicate host resize. Missing or
invalid split diagnostics are setup notes only. See generated `report.md` for every boundary,
exclusion and EMULATED-machine limitation.

The persistent TCP loopback sink receives a length-prefixed frame and echoes its byte count as an
ack. Each raw/JPEG/mono-PNG transfer is timed independently. `bytes_on_wire` counts application
payload + 8-byte length + 8-byte ack; actual TCP/IP/Ethernet overhead is unknown. Theoretical 1 Gbps
time is a lower-bound byte calculation, not a measured network speed. JPEG quality 90 and mono PNG
are encoded once outside timing. A colour frame's mono PNG is reported as a projection, with no
colour-preserving end-to-end claim. Serial budget rows sum same-iteration processing plus **one**
transport option, then calculate p50/p95; they do not sum percentiles or mutually exclusive options.

`timings.csv`, `summary.csv`, `report.md`, `command.txt` and per-worker logs are generated per run;
`--accuracy` also writes `accuracy.csv` (header only if unavailable). Split stages appear in all
three timing outputs when the production native API returns three finite nonnegative values.
Warm-ups never enter CSVs. Configs failing mid-run contribute no partial latency summaries; the
process returns 2 for setup/config failures, or 0 when requested SegAI latency configs complete
(an unavailable optional ONNX model is documented). An unavailable NG proxy is a setup note only.
The report reads HEAD/refs directly and does not invoke git.

Authoring verification: source/static inspection only; no build, test, harness execution or git
command was authorized. Runtime measurements, compilation and native compatibility remain unverified.
