# SegAI vs Count (hex) - verified report 2026-11-10

## Status

**CHUA DAT TOAN BO / CHUA NGHIEM THU.** Native Segment AI training, tuning and paired measurements completed. Native Couter Count cannot be measured from this checkout because its implementation/runtime is absent. No completion ticks, commit or push.

## Final comparison - same frozen 100-image manifest

|Method|Train input|Exact count /100|Precision IoU .5|Recall IoU .5|Mean score|Cycle p50 / p95 ms|Native p50 / p95 ms|
|---|---|---|---|---|---|---|---|
|Count / native Couter|N/A|N/A|N/A|N/A|N/A|N/A|N/A|
|SegAI baseline|640x640|45/100|0.448008|0.356535|0.777614|43.889 / 54.413|30.235 / 36.212|
|SegAI tuned|640x640|76/100|0.903605|0.891725|0.517762|28.301 / 33.497|27.835 / 32.922|

Paired verification speedup: **1.5508x end-to-end**, 1.0862x native only. Requested speed condition end-to-end >=2x: **False**. Do not round 1.95x into 2x.
Score increased: False. Precision non-decrease: True. Recall non-decrease: True.

## Real photo - diagnostic, 20 labelled screws

|Method|Detected / truth|TP / FP / FN IoU .5|Cycle p50 / p95 ms|
|---|---|---|---|
|Baseline|3/20|1 / 2 / 19|97.004 / 120.552|
|Tuned|17/20|15 / 2 / 5|27.717 / 30.603|

The real photo supplied the source crops, so it is NOT independent validation. Synthetic holdout improvement is not a production/generalization guarantee.

## Baseline and unchanged input evidence

- No original LucGiac Segment AI model existed in C:/Demo/LucGiac/.easyai/models. A native-default baseline was trained rather than substituting YOLO or a different product's model.
- BeeNativeSegAI C API: TrainerCreate, ConfigureV2, AddSample, Train, Save, InferLoad/Predict, GetConfidence and InferGetPhaseTimings. Instance-head factor additionally used AddInstances/PredictInstances.
- Fixed Side640 / 640x640 is checked from the actual saved native YAML payload; originalInput=0 and V2SpeedScale=1.0 throughout. No resizing shortcut was used.
- Default baseline settings: epochs200 cap, lr0.015, regularization0.001, rotation15, scaleVariation0.15, brightness0.15, augmentation on, earlyStop on. Train backend CPU: the default Intel GPU request returned -8 (Intel GPU unavailable); Quadro P5000 is not an Intel OpenVINO GPU.
- Baseline native saved metadata: C:\Demo\LucGiac.segai-tune\baseline\model.json. Comparison is relative to this newly trained default baseline, not to an unavailable historical deployment model.

## Data isolation

- 120 train +30 internal validation +40 tuning +100 final synthetic images. Training and validation intentionally share source identities; tune and final identities are separately held out.
- Source groups: {"train": [11, 0, 17, 1], "val": [11, 0, 17, 1], "tune": [3], "final": [2, 4]}.
- Fixed seeds and frozen SHA256 image hashes; disjoint train/tune/final source identities, seeds and image hashes asserted by dataset.py. Train/val backgrounds use the 11 known families. Final has 50 known +50 unseen wood/dots/carbon backgrounds; tuning never contains those unseen background families.
- Exact selected alpha>128 semantic masks are emitted from a derived in-memory synthgen.compose; original synthgen.py is not edited by this task. Bounding boxes come from the same accepted objects.
- Limits: single source photo, only 2 distinct source objects in final and 1 in tuning. This is synthetic object-identity holdout, not fresh-camera validation.
- Manifest: C:\Demo\LucGiac.segai-tune\dataset\manifest.json; SHA256 7cc63012bb389c63146ea5f9dedff345bb7d30e552464b62a7b49e8f1092f3eb.

## Tuning and model artifacts

- OFAT from default: priorBalance=1; regularization=.0003; threshold .35/.65/.8; foreground .4/.6/.7; ratio .1/.4/.5; supported morphology0/5; winner-class boundary1; shape cleanup .03/.06; process affinity2/4/8 CPUs.
- Additional declared native factor: instanceHead=1 (default60 head epochs), then centre thresholds .1/.2/.35/.65, quality refinement1, CPU affinity2/4/8. Only 40-frame tuning metrics were used to choose parameters; final files were not inputs to selection.
- Native options morphology1/7 were rejected (actual -2) and were not treated as measured configurations. Supported source contract was then read and used: 0,3,5.
- Process affinity is not claimed to equal OpenVINO inference_num_threads. Native Compile exposes LATENCY only; no precision/thread API. No unsupported FP16/Pascal speed claim. Score values from semantic components and centre heads are not calibrated interchangeable probabilities.
- Selected frozen settings: {"model": "C:\\Demo\\LucGiac.segai-tune\\instance_head\\model.segai", "sha256": "076ab3b7a525eedf44815d11c41b03b4a7cbf671af667ab5b39bfa59b02e4fcb", "settings": {"instance_mode": true, "threshold": 0.35, "quality": 1}, "affinity": 65535, "baseline_sha256": "e58ed1775577ba7a2718c5af841c5fa6a986d4ed695fa8f92fb5651de9f5dbf8", "input": [640, 640], "speed_scale": 1.0, "tuning_only": true, "additional_instance_factor_examined": true}.
- Original models untouched. New models under C:\Demo\LucGiac.segai-tune. The tuned copy was reloaded through the native API and its real-frame count matched saved raw output.
- Baseline SHA256: e58ed1775577ba7a2718c5af841c5fa6a986d4ed695fa8f92fb5651de9f5dbf8.
- Tuned SHA256: 076ab3b7a525eedf44815d11c41b03b4a7cbf671af667ab5b39bfa59b02e4fcb.
- Native DLL SHA256: 85b6dfa8f9d5168265abea82b2bc8680623ef364e19631640ba42e8347358d22. git diff BeeNativeSegAI is empty.
- Script hashes: C:\Demo\LucGiac.segai-tune\script_hashes.json.

## Measurement contract / phase bottleneck

- Same image manifest for baseline/tuned. Five warmups, three serial repeats per final image; real-frame diagnostic uses15 repeats. BGR decode excluded; native inference, confidence extraction and required instance extraction included; IoU matching excluded. One-to-one greedy score-descending convex OBB IoU>=.5.
- Native-only and whole-cycle times are both reported. Do not substitute native-only speedup for the requested whole-cycle target.
- Baseline phase p50 ms: {"device": 0.0005499999999999999, "prepare": 0.61145, "tensor": 0.9822500000000001, "infer": 24.5413, "upsample": 1.4376, "compile": 0.0, "ccscore": 0.2558, "nearest": 0.60395, "v2_total": 28.691650000000003, "inferer_tail": 0.29275, "inferer_total": 28.97045, "export_copy": 0.31585, "export_total": 29.46165}.
- Tuned phase p50 ms: {}.
- Total phase fields overlap subphases. OpenVINO infer is the semantic backbone/head phase; full-resolution confidence/component extraction is visible in whole-cycle minus native.
- PredictInstances currently does not populate InferGetPhaseTimings (queried but zeros are NOT measurements). Instance timing is actual wall-clock native call; no false backbone/head phase attribution.
- Initial semantic-only verification is retained in metrics/segai_*_final.json. Its measured whole-cycle ratio was 1.9552494076339635. The second paired verification followed a genuinely new native instance-head experiment, not repeated trials to cross2x.

## Count / Couter blocker (not replaced by a different algorithm)

The assigned EAI repository is a split-out EasyAI/EasyLabel + BeeNativeSegAI checkout. Verified EasyLabel/Lib/native has only cuda13 and segai; there is no BeeCpp.dll anywhere under EasyLabel, and no Count/Couter implementation under EasyLabel/src. Historical documentation references EasyVision/Pattern/BeeCpp in another codebase, not an executable Couter here. The independent Count worker hit its15-minute runtime limit without producing a wrapper. A real Count result requires the actual Couter runtime/API and its settings; plain connected components would not satisfy this engine requirement. Therefore Count precision, score, and cycle remain N/A, not fabricated.

## Raw files / reproduce

- Native models/config/logs: C:\Demo\LucGiac.segai-tune.
- Per-image raw predictions, matches, timings and summary JSON/JSONL: C:\Demo\LucGiac.segai-tune\metrics.
- Final verification bundle: C:\Demo\LucGiac.segai-tune\verified_completion.json; background split C:\Demo\LucGiac.segai-tune\background_results.json.
- Scripts: tools/segai_tune/dataset.py, seg_native.py, train_baseline.py, run_experiment.py, instance_experiment.py, finalize_report.py.
- Generate frozen data: python -X utf8 tools/segai_tune/dataset.py
- Native baseline: python -X utf8 tools/segai_tune/train_baseline.py
- OFAT and sealed report: python -X utf8 tools/segai_tune/run_experiment.py; then instance_experiment.py; then finalize_report.py.
- Full chronological attempts and immediately appended results retained in REPORT.history.md. Failed first GPU request and wrapper directory/morphology errors were recovered; old pipeline_error.json is historical, not a claim that verified models failed.

No commit, push, native edits, original model overwrite or task completion ticks.

## Final QA / interpretation

- Latest verified timing is43.8887 ->28.3006ms (1.550805x). Earlier semantic1.955x used a different measurement; its baseline must not be combined with the current tuned timing. Inter-run baseline timing changed materially; no cause is established and no noise-free speed guarantee is claimed.
- All-objects-matched frames are35/100 baseline versus31/100 tuned, despite improved aggregate precision/recall and exact counts45 ->76. Equal object counts can hide cancelling FP/FN; strict per-frame accuracy did not improve on every criterion.
- Cross-head confidence means are not calibrated or directly comparable. Raw mean score did not increase.
- Partial independent QA was recovered from sanitized session history after its file-writing attempt timed out, then reconciled by the coordinator with current verified metrics. It is not an independent PASS. See QA.md for findings and resume-cache limitations.

## Task audit

Task: SegAI tune vs Count (hex), continuation2. Authorized scope: native API training/benchmark harnesses, report and new models; fixed640x640, no original model overwrite, no commit/push, no task completion ticks. Existing unrelated dirty application/autoloop files were preserved.

Task files: tools/segai_tune/{dataset.py,discover.py,seg_native.py,train_baseline.py,run_experiment.py,instance_experiment.py,finalize_report.py}; docs/segai-tune/{REPORT.md,REPORT.history.md,BASELINE-ATTEMPT.md,COUNT-NOTES.md,QA.md,assets/baseline_verified_real.jpg,assets/tuned_verified_real.jpg}. New model/data/metrics outputs are under C:/Demo/LucGiac.segai-tune/ as authorized.

Verification: AST parse passed for all7 Python scripts; conflict-marker and trailing-whitespace scans found none. git diff --check -- tools/segai_tune docs/segai-tune BeeNativeSegAI returned0; git diff --numstat -- BeeNativeSegAI was empty. Native instance experiment and report finalizer both exited0. Finalizer reloaded tuned/model.segai through the native API, asserted its hash against the frozen selected model, and reproduced17 detections on the real frame (review_verification.json). Native DLL hash matches both training metadata records. No application/native build was needed because no C#/C++ implementation changed; those builds remain unrun, not claimed PASS. Python3.14 on Windows; CPU12th Gen Intel Core i5-12600KF,10 cores/16 logical processors. C#12/net9 and native ABI assumptions unchanged. First unresolved compile/runtime errors: none in completed verification; historical errors retained in REPORT.history.md. Residual blockers: absent Couter runtime/API/settings, speed<2x, confidence increase unproven, strict fully-correct-frame reduction and limited source-photo validation.

## Counter mode (ProjectKind.Counter) re-run - 2026-11-10

Not QA-signed; task is NOT ticked done (independent QA required). Native code untouched for the CPU results below (the later CUDA section changes `BeeNativeSegAI`, uncommitted); `C:\Demo\LucGiac` untouched; no commit/push. Tools: `tools/segai_tune/CounterTool/` (C# console, references EasyLabel.Core + EasyLabel.SegAI and calls the real app path `ProjectManager.Create(..., ProjectKind.Counter)` and `SegAiSession.Train`), `counter_eval.py`, `counter_sweep.py`, `counter_profile.py`.

### Setup (evidence)
- New project `C:\Demo\LucGiac.counter\LucGiac.easyai` (copy of the frozen manifest train 120 + val 30 images; hex labels converted to `oriented_rectangle`, direction = atan2(p1-p0)); project.json has `"projectKind":"counter"`. `CounterTool verify` printed `ProjectKindStore.Read=Counter IsInstanceHead=True` (exit 0). A second project `C:\Demo\LucGiac.counter2\LucGiac.easyai` uses `circle` labels (r = (W+H)/4).
- Training = `SegAiSession.Train`, `SegTrainingOptions{Version=2, InputSize=640}`, backbone frozen, trained through the instance head (no separate hand-picked head). Input stays 640x640.
- Selection used only the 40-image tune split. The 100-image final split and the real 20-screw photo were measured once per configuration listed below.

### Training variants (obb project, models in `C:\Demo\LucGiac.counter\LucGiac.easyai.data\models\`)
| Model | Rotation aug | Instance epochs | Tune @thr0.2 P / R / exact | Note |
|---|---|---|---|---|
| v001 | 90 (app default) | 300 | 0.393 / 0.403 / 28/40 | default settings of the app give poor box geometry |
| v002 | 90 | 60 | 0.387 / 0.394 / 28/40 | same accuracy as v001, training 22 min -> ~5 min |
| v003 | 15 | 60 | 0.845 / 0.857 / 30/40 | |
| **v004** | **0** | **60** | **0.856 / 0.872 / 30/40** | selected |
| circle v001 | - | - | exact 31/40 (tune) | IoU metrics vs elongated GT boxes not comparable (square circle boxes) |

Finding: the app default Rotation=90 augmentation destroys instance box geometry for these elongated hex screws in an oriented-box task (P ~0.39 vs ~0.85). Rotation 0/15 fixes it; instance epochs 300 vs 60 changes nothing measurable. This is a training-default recommendation for Counter projects, not proven for other datasets.

### OFAT on tune split (v004, quality 0): threshold 0.1 -> P .813 R .872 exact 22/40; **0.2 -> .856 / .872 / 30/40**; 0.35 -> .876 / .855 / 29/40; 0.5 -> .890 / .439 / 1/40. Quality 1 (refine) at thr 0.2: identical accuracy (.856/.872/30/40), no speed gain. Selected: threshold 0.2, quality 0.

### Before / after (final split 100 images, 1293 objects; same contract as above; BGR decode excluded)
| Config | Exact count | Precision | Recall | Mean score | Cycle p50 / p95 (ms) | Native p50 (ms) | All-matched frames |
|---|---|---|---|---|---|---|---|
| Baseline (semantic head), earlier run | 45/100 | 0.448 | 0.357 | 0.778 | 43.889 / n.a. | - | 35 |
| Baseline re-measured today, unconstrained | 45/100 | 0.448 | 0.357 | 0.778 | 39.503 / 50.192 | 26.37 | 35 |
| Baseline re-measured today, affinity 0x555 | 45/100 | 0.448 | 0.357 | 0.778 | 36.734 / 46.261 | 22.65 | 35 |
| Previous tuned (instance head, thr .35, q1) | 76/100 | 0.9036 | 0.8917 | 0.52 | 28.301 | - | 31 |
| **Counter v004, thr 0.2, q0, unconstrained** | **77/100** | 0.884 | 0.896 | 0.508 | **24.87 / 27.69** | - | 35 |
| **Counter v004, thr 0.2, q0, affinity 0x555** | **77/100** | 0.884 | 0.896 | 0.508 | **19.18 / 25.42** | 18.72 | 35 |

Speedup: vs the earlier 43.889 ms baseline: unconstrained 1.76x, with affinity 2.29x. Against the baseline re-measured under the SAME conditions (today the baseline p50 itself moved 43.9 -> 39.5 ms, so the earlier figure is not reproducible to better than ~10%): unconstrained 39.503/24.87 = 1.59x, affinity 36.734/19.18 = 1.92x. So the 2x target is reached only against the old 43.889 ms figure and only with affinity; against a same-condition baseline it is 1.92x, i.e. NOT proven >= 2x.

### Real photo (20 labelled screws, 15 repeats)
| Config | Detections | TP | FP | FN | P | R | Cycle p50 (ms) |
|---|---|---|---|---|---|---|---|
| Baseline | 3 | 1 | 2 | 19 | 0.333 | 0.050 | 93.7 (aff: 91.3) |
| Previous tuned | 17 (earlier harness) | - | - | - | - | - | - |
| Counter v004 thr 0.2 q0 | 21 | 16 | 5 | 4 | 0.762 | 0.800 | 22.8 (affinity 0x555) |

Count is 21 vs 20 true: not exact.

### Bottleneck (phase timing, tune images, 40 x 3)
- Semantic path (native phase timers populated): total 26.8 ms of which OpenVINO `infer` 21.3 ms (79%), tensor 1.0, prepare 0.5, upsample 1.4, ccscore+nearest 0.8, export 0.3-0.6; the Python wrapper's contour extraction adds the remaining ~16 ms to the baseline cycle (43 vs 26.8).
- Instance path (phase timers are not populated by `PredictInstances`; derived by differencing): q0 24.5, same call with thr 0.999 (almost no decode/NMS) 23.9, q1 23.7-25.5 -> decode + NMS ~0.7 ms, refine ~0, Python wrapper ~0.4 ms. Therefore ~95% of the cycle is the CPU OpenVINO inference of the frozen MobileNetV2 backbone at 640x640.
- GPU: Quadro P5000 (Pascal) is not an OpenVINO target; Intel iGPU returned error -8 earlier. Not usable in this run. NVIDIA RTX 5060 via ONNX Runtime CUDA: see "Instance head on NVIDIA CUDA" below.
- Lever found without changing native code or input size: pin the process to one thread on each of the 6 P-cores (mask 0x555): native 22.4 (all 16 CPUs) -> 18.4-18.7 ms. Other masks (0xff, 0x3f, 0x15, 0x155, 0x55, 0xf) were slower; 0xaaa (other HT siblings) 19.4. E-cores/HT siblings slow the LATENCY-hinted CPU plugin. This is a process setting the host application must apply (SetProcessAffinityMask); it is not a change made inside BeeNativeSegAI. Run-to-run noise ~1 ms.

### Instance head on NVIDIA CUDA (RTX 5060, 2026-11-10; uncommitted trial DLL, NOT QA-signed)
The instance network is the V2 backbone (cut at the 448-channel stride-8 feature grid) plus two 1x1 dense layers, so it was moved to the ONNX Runtime CUDA backend (`OrtCudaBackend`) built for the V2 graph; no new op mapping was needed. Before this change `PredictInstances` never allowed CUDA: measured with the old DLL and CUDA selected, the cycle was 22.7 ms (= CPU speed). Details, files and tests: `docs/segai-tune/CUDA_BACKEND.md` section 11. Same model (v004, thr 0.2, q0, 640), same scoring, BGR decode excluded, 5 warm-up runs dropped, no CPU affinity set.

| Set | Backend | Cycle p50 / p95 (ms) | Native p50 (ms) | Exact | TP / FP / FN | P / R |
|---|---|---|---|---|---|---|
| 100 final images | CPU (OpenVINO) | 24.58 / 31.26 (rerun 24.48 / 30.31) | 23.84 | 77/100 | 1158 / 152 / 135 | 0.884 / 0.896 |
| 100 final images | **CUDA** | **8.21 / 9.26** | 7.50 | 77/100 | 1158 / 152 / 135 | 0.884 / 0.896 |
| Real photo (20 screws) | CPU | 24.79 / 28.87 | 24.00 | 21 detections | 16 / 5 / 4 | 0.762 / 0.800 |
| Real photo | **CUDA** | **8.53 / 10.70** | 7.75 | 21 detections | 16 / 5 / 4 | 0.762 / 0.800 |

- ~3.0x faster cycle (24.58 -> 8.21 ms p50), and ~2.9x on the real photo, with identical counts / TP / FP / FN / P / R / exact / all-matched (35). This is against the same-condition CPU baseline measured in the same session, so it is above the 2x target for the Counter cycle (on this RTX 5060 only).
- Parity (101 images, 5.8M tensor values): max |CPU - CUDA| = 2.6e-5 on the network output (heat logits 2.6e-5, regression 7.5e-6, sigmoid heat 8.9e-7); detections: 0 images with a different count, centre <= 1.2e-4 px, size <= 3.1e-4 px, angle <= 1.9e-6 rad, score <= 6e-7. Missing `onnxruntime.dll` falls back to CPU with the same count and a `BackendNote`.
- Phases (median, CPU -> CUDA): prepare 0.56 -> 0.51, tensor 0.87 -> 0.86, network infer 22.43 -> 5.64, decode+NMS 0.09 -> 0.09, native total 24.04 -> 7.25 ms. Next bottleneck: the CUDA infer phase (5.6 of 8.2 ms: GPU compute + 4.9 MB host-to-device copy + output copy + ORT overhead; no IOBinding/pinned memory yet), then tensor build 0.86 ms, prepare 0.5 ms, ctypes/Python ~0.7 ms. No further optimisation made (fp16/TF32 would break CPU parity; IOBinding gain not measured).
- Not proven: first CUDA call costs ~0.95 s (session + cuDNN tuning) vs 0.19 s on CPU, so the app must warm up; GPU was shared with other processes (~2.8 GB already used) and CPU timing was noisy; `quality 1` not measured on CUDA; training on CUDA, Pascal P5000 and Intel GPU not tried; one model, one real photo; the production DLL `EasyLabel\Lib\native\segai\*` is NOT updated, so the app still runs the old speed until the DLL (with libtorch) is rebuilt and replaced; the real-photo count (21 vs 20) is unchanged by the backend.

### Counter accuracy round 2 (count exactness, `C:\Demo\LucGiac.counter3\`, 2026-11-10) - NOT QA-signed, not ticked
Goal: raise count accuracy (>= 90/100 exact, real photo exactly 20) at 640x640 with CT not worse than v004. Every row is a NEW project/model (v004 untouched, `LucGiac.counter`/`counter2` untouched, `EasyLabel\Lib\native\segai\*` and `tmp\EAI-SEGAI-CUDA\integrated*` untouched). Scored on the same frozen manifest (tune 40 / final 100, unseen backgrounds only in final) with `counter_eval.py` (IoU >= 0.5, `instance_mode:true`, CUDA instance DLL read-only, 1 repeat). Selection on tune; final measured once per model that looked better.

Root cause (found with `tools/segai_tune/counter_errors.py`, v004 final: 152 FP / 135 FN, most are localisation errors with best IoU 0.1-0.5, not missed objects): the v004 labels were inconsistent. The long side was `p0p1` in only ~50 % of boxes and `direction` was in [0, 90 deg) with angleSymmetry 1, so the width / height / angle regression target was ambiguous for the same physical screw. A hex screw has no direction (half-turn symmetric), so the fix is canonical labels (long side = W, direction in (-90, 90]) plus `angleSymmetry = 2`.

| Model | What changed (vs previous row's recipe) | Tune exact / P / R | Final exact | Final TP / FP / FN | Final P / R | Real photo (20) |
|---|---|---|---|---|---|---|
| v004 (baseline) | rotation 0, instance epochs 60, raw labels, symmetry 1 | 30/40, .856/.872 (thr 0.2) | 77/100 | 1158 / 152 / 135 | .884 / .896 | 21 det: 16 TP / 5 FP / 4 FN |
| c1 | canonical labels, symmetry 1 (control) | 32/40, .886/.883 (thr .25) | not run | - | - | not run |
| **c2** | canonical labels + symmetry 2, rotation default | 34/40, .941/.938 | **81/100** | 1217 / 77 / 76 | .940 / .941 | 17 det: 15 / 2 / 5 |
| c2r | c2 + rotation aug 15 | 34/40, .942/.942 | not run (no gain on tune) | - | - | not run |
| c2e | c2 with `InstanceEpochs` 150 | 30/40, .932/.935 | not run | - | - | not run |
| c2d | c2 + 120 extra synthetic TRAIN images (seeds 20261150000+, balanced over the 11 seen background families) | 33/40, .951/.958 | **85/100** | 1232 / 63 / 61 | .951 / .953 | **20 det: 17 / 3 / 3 (count exact)** |

Threshold sweep on c2 (tune, quality 0): 0.20 -> 28/40 (.929/.941), **0.25 -> 34/40 (.941/.938)**, 0.30 -> 30/40 (.945/.928), 0.35 -> 26/40, 0.40 -> 15/40. `quality 1` is a pure no-op for these box models (identical accuracy at 0.3 and 0.35, slower). So exact count moves by +/-4 images between neighbouring thresholds; 0.25 was chosen on tune and the final split was NOT re-tuned.

Honest reading:
- Count accuracy went 77 -> 81 (label/symmetry fix) -> 85 (more data) on the 100-image final split; P/R .884/.896 -> .951/.953. The target of >= 90/100 was NOT reached (best 85/100, 61 FN + 63 FP remain).
- The real photo giving exactly 20 for c2d is a count coincidence (17 TP, 3 FP, 3 FN, so FP and FN cancel), not 20 correct boxes. c2 gave 17. Do not report it as "20/20 correct".
- Which of the two changes matters: c1 (canonical, symmetry 1) is clearly worse than c2 (P/R .886/.883 vs .941/.938 on tune), so the half-turn symmetry is the key factor. A control with symmetry 2 and NON-canonical labels was not run, so the separate contribution of canonicalisation is unproven (the product code canonicalises together with symmetry 2).
- Training is not bit-for-bit reproducible: c2e should have been the same model as c2 in effect (both early-stop at epoch ~110 of a 300/150 budget) but scored 30/40 vs 34/40 on tune; the run-to-run noise on tune is therefore about +/-4 exact images, and the c2 -> c2d difference on tune (34 -> 33) is inside that noise even though P/R and final improved. Single runs per row; no confidence intervals.
- Early stop (`SegV2Core.cpp` window rule, gain < 2 %) always ends the instance head at epoch 107-110 regardless of the epoch budget (300 vs 150 are the same), so epochs are not a lever.
- Invalid measurement discarded: one evaluation was run without `instance_mode:true` (the wrapper then runs the semantic branch: 13/40, 28 ms); those numbers were deleted and redone.
- Final split was seen for c2 and c2d only (and v004); c1, c2r, c2e were judged on tune and not promoted.
- Time: c2d CUDA p50 8.1 ms (same as v004 CUDA 8.2 ms); same network size, so CT is unchanged. A CPU production-DLL measurement of the new models was not made in this round (see "Not proven" below).

Product change (minimal, tests added): Counter projects now default to a half-turn (symmetry 2) canonical label rule instead of the generic rule that picks 1 for elongated boxes. `EasyLabel/src/EasyLabel.SegAI/Convert/SegBoxModeRule.cs` adds `HalfTurnSymmetry`, `CounterAngleSymmetry(...)`, `CanonicalBoxHalfTurn`, `CanonicaliseForCounter(...)` (before `ParseAngleSymmetryOverride`); `EasyLabel/src/EasyLabel.SegAI/SegAiSession.cs` (field `_counterKind`, `BuildTrainJob` around lines 1570-1590) uses them only when `ProjectKindStore.Read(paths) == Counter`, a user `advanced.json` `angleSymmetry` override still wins, non-Counter projects keep their rules. Tests: `EasyLabel/tests/EasyLabel.SegAI.Tests/SegCanonicalBoxTests.cs` (half-turn cases, idempotence, circle/NaN, `CounterAngleSymmetry`), 64/64 pass in `SegCanonicalBoxTests` + `SegBoxModeTests`. `EasyLabel.sln` Release build: 0 errors, 50 warnings. Full test run: Commands 275, Online 154, Camera 104, Core 441 all pass; SegAI.Tests 1604 pass / 1 fail / 21 skip. The one failure is `TrainRecommendationTests.Apply_DoesNotOverwriteUserChangedOptions` (`EasyAI.Wpf/ViewModels/TrainRecommendation.cs`, which I did not touch; other work in the tree changes training defaults) - not caused by this change as far as I can tell, but I did not bisect it.
Research tooling (not product): `tools/segai_tune/CounterTool/Program.cs` gained `canon`, `sym=N`, `extra=<manifest>` options; `tools/segai_tune/counter_errors.py`; `tools/segai_tune/gen_extra.py` (extra train images; asserts no seed / sha256 / source overlap with tune, final, val and train).

Leakage check for the extra data: `gen_extra.py` uses the same compose and the TRAIN source patches only (asserted equal to the frozen manifest's train group), TRAIN_BG families only (wood/dots/carbon stay unseen), new seeds (20261150000+, 20261160000+); asserts passed for seed, sha256 and source ids against train/val/tune/final; extra vs extra2 also disjoint (0 seed / 0 hash overlap).

### Counter accuracy round 3 (continuation after a timed-out sub; `C:\Demo\LucGiac.counter3\`) - NOT QA-signed, not ticked

Reconstruction of what c2a / c2f are (read from `c2a|c2f.easyai.data`, `settings.json` of the run, `*.images`, `train_*.log`; no report was written by the previous sub):

| Project | Training images | Differences vs c2 / c2d | State |
|---|---|---|---|
| c2a | 120 (the same 120 as c2, val = first 30, train 90) | **No `advanced.json`**: symmetry 2 + canonical labels come from the new PRODUCT code path (`SegAiSession.BuildTrainJob`, Counter kind), i.e. c2a is a re-run of c2 through the shipped code. Run report: instance epochs 300, early stop at epoch 113, rotation 90 (app default), 640 side, CPU trainer, 1590 s while the machine was loaded | finished, model v001.segai, `metrics\c2a_t0.25_{tune,final,real}.json`: final 81/100, TP/FP/FN 1233/76/60, P .942 R .954 (c2: 81/100, 1217/77/76) |
| c2f | 600 = c2d's 240 (120 + 120 `x####` from `extra\`) + 360 `extra2_####` synthetic (`extra2\manifest.json`, seeds 20261160000+, TRAIN source screws and TRAIN_BG families only) | `advanced.json` angleSymmetry 2, same options as c2d (epochs 200, side 640, instance epochs 300, rotation 90, priorBalance), validation = images 0..29 | `CounterTool train` PID 54452 started 19:53; head converged at epoch 113 at 19:58; still in the long post-train stage (log `progress 76`) at 20:29 |

Net: c2a confirms the product change reproduces c2 (81/100 = same exact count, localisation a bit better: FN 76 -> 60). c2f is "c2d + 360 more synthetic train images" (data-quantity test).

**Leakage check (tools/segai_tune/counter_leakcheck.py, run 2026-11-10 20:28):** for the training images of c2, c2a, c2d, c2f and the earlier `LucGiac.counter` project vs the 100 `final` and 40 `tune` images: file sha256 equal = 0, decoded-pixel sha256 equal = 0, seed overlap = 0, manifest-sha overlap = 0, source-screw id overlap train vs final = none (train-side sources 4 ids, final {2, 4}, tune {3}). File NAMES do overlap (both sets are named 0000.png..., 100 collisions) but that is only the naming scheme; the content is different, so name equality alone is not leakage. Result: **no leakage; 81/100 (c2a) and 85/100 (c2d) are valid under this test.**

Important limits of this test set (must be read with every number): all images, train and test, are synthetic composites (synthgen) cut from ONE real photo with only 7 isolated screws usable as patches (train 4, tune 1, final 2). So "final 100" = random layouts/backgrounds of 2 unseen physical screws; unseen backgrounds (wood/dots/carbon) appear in half of the images. It measures robustness to layout/background, not to a new batch of screws. The real photo (all 20 screws, 4 of whose crops are in train) is a diagnostic, not held-out.

**Experiment log round 3 (each row appended right after the run):**

| # | Experiment | Result |
|---|---|---|
| E1 | `c3a` = c2a data (120 imgs) + backbone fine-tune 10 epochs through the production-build DLL (`EASYAI_SEGAI_DIR=...\production-build\bin`, taps.pt), via `CounterTool train ... <backboneEpochs> <taps.pt>` (new optional args, `tools/segai_tune/CounterTool/Program.cs`) | **Attempt 1 and 2 (backboneEpochs 10, then 1) FAILED**: `SEHException` in `SEGAI_TrainerTrain` right after the head converged (epoch 113), `runs\...\failure.json`. Reproduced WITHOUT the product pipeline (`C:\Demo\LucGiac.counter3\analysis\ft_instance_smoke.py`, python → same DLL): plain fine-tune (no instance head) also fails with Windows `0xC06D007E` (delay-load module not found) **unless `S.cuda_available()` is called first in the process** (train_smoke.py does it; with it the same config trains and writes `finetune.txt`). I.e. the libtorch delay-load only resolves after the CUDA/ORT probe has loaded its DLLs, or when the DLL folder is on `PATH`. Workaround used for attempt 3: put `production-build\bin` on `PATH` of the CounterTool process. This is a deployment finding for the production DLL (the app would need the probe or the folder on PATH before a fine-tune), not fixed in native code. |

### Models / artifacts
- Selected: `C:\Demo\LucGiac.counter\LucGiac.easyai.data\models\v004.segai` SHA256 D874B1550D897BAF73C8912AF38BE92D6B5770DF95A556A77320923ED39F6526 (inference options: instance_mode, threshold 0.2, quality 0, input 640).
- Others (same dir): v001 321F6E18D15B733A66542E99E806CCDD0D6D18B6AB26D439BBC88BE2D7241783, v002 5FA4A2E62B4E80875D655B6BB039AEF9EE4AF336C3475B6C03488C70DEA66C80, v003 9F07A2D1A630F5FE1A66110D116F717226340ACF3FF568556E9520B4B46A26FF; circle `C:\Demo\LucGiac.counter2\LucGiac.easyai.data\models\v001.segai` 4F04AF521E3FA5725AAAE22A4D5EEA0A2E32AD9DE8FB2BBB5EED66857DF4D705. Baseline and previous tuned models in `C:\Demo\LucGiac.segai-tune\` untouched.
- Metrics/logs: `C:\Demo\LucGiac.counter\metrics\*.json` (`counter_v004_t0p2_final.json`, `counter_v004_t0p2_aff555_*.json`, `baseline_aff555_*.json`, `baseline_today_*.json`, `profile_v004*.json`, `sweep_*.json`), training/eval/sweep/profile logs in `C:\Demo\LucGiac.counter\*.log`.

### Not achieved / not proven
- Precision 0.884 is below the previous tuned model (0.9036); recall +0.004, exact 77 vs 76. Not "no-loss" on precision vs the previous tuned model (it is far above the baseline on every accuracy metric).
- Real photo count 21 vs 20 (16 TP, 5 FP, 4 FN); previous tuned model got 17 detections in the earlier harness.
- Mean score 0.508 < baseline 0.778 (different heads, uncalibrated; confidence not improved).
- Cycle 2x: 1.59x (unconstrained) / 1.92x (affinity) against a same-condition baseline; 2.29x only against the old 43.889 ms number and only with CPU affinity. Bottleneck is OpenVINO CPU inference; not reducible without changing the native/backbone/input size, which was out of scope.
- Count / BeeCpp.dll runtime is not in the repo: Count comparison still N/A.
- (CPU numbers above) Instance path had no native phase timers (differencing used); the new trial DLL fills them for both CPU and CUDA. CUDA results are in the section above, metrics in `C:\Demo\LucGiac.counter\metrics_cuda_instance\`. The circle project metrics are only tune exact-count; one dataset and one real photo; no independent QA.

### Counter accuracy round 4 (gateway-only, detached WMI) - NOT QA-signed, not ticked

| Experiment | Result |
|---|---|
| R4-1 c2f rerun, 600 train-side images, symmetry2, 640, head300, CPU trainer, production-build DLL | Detached runner started; log C:\Demo\LucGiac.counter3\train_c2f_round4.log and .err.log; no result yet. Previous run terminated without saved model; unrelated Python PIDs untouched. |
| R4-2 c3b, original120 train-side images, 1 backbone epoch + head300, production-build DLL on PATH | Detached WMI runner started (full powershell.exe path required; first c2f WMI attempt returned9, second returned0 PID36640). c3b logs train_c3b_round4.*; no result yet. |

| R4 selection rule | Predeclared tune-only thresholds .2/.225/.25/.275/.3/.325/.35, q0; rank exact count, then F1, then closest .25. Final/real never used for ranking. |

| R4 tune c2d thr=0.2 q0 CUDA640 | exact 32/40; TP/FP/FN 615/37/27; P 0.943252 R 0.957944; p50 14.695ms |

| R4 tune c2d thr=0.225 q0 CUDA640 | exact 33/40; TP/FP/FN 615/34/27; P 0.947612 R 0.957944; p50 14.355ms |

| R4 tune c2d thr=0.25 q0 CUDA640 | exact 33/40; TP/FP/FN 615/32/27; P 0.950541 R 0.957944; p50 15.178ms |

| R4 tune c2d thr=0.275 q0 CUDA640 | exact 35/40; TP/FP/FN 615/30/27; P 0.953488 R 0.957944; p50 14.458ms |

| R4 tune c2d thr=0.3 q0 CUDA640 | exact 36/40; TP/FP/FN 615/29/27; P 0.954969 R 0.957944; p50 14.888ms |

| R4 tune c2d thr=0.325 q0 CUDA640 | exact 36/40; TP/FP/FN 615/27/27; P 0.957944 R 0.957944; p50 14.307ms |

| R4 tune c2d thr=0.35 q0 CUDA640 | exact 31/40; TP/FP/FN 610/26/32; P 0.959119 R 0.950156; p50 14.939ms |
| R4 leakage recheck | NO LEAK for c2d240, c2f600, c3b120 unique project images vs tune40/final100: file hash0, decoded-pixel hash0, source overlap0, seed overlap0. Log analysis\round4_leakcheck.log. CounterTool create reports150 copies but train/val file names collide and val overwrites first30; c3b has120 unique images =90train+30val (same recipe as c2a). |
| R4-2 backbone checkpoint | SUCCESS native fine-tune stage: epochs1 best_loss0.0337685, parameters102, parity_max_diff6.93798e-05; finetune_epochs.csv elapsed105.226s, no SEHException with production-build bin on PATH. Whole instance model still training; no tune/final claim yet. |

| R4 incumbent frozen before final | c2d threshold 0.325 q0, tune36/40 F1=.957944; model SHA256 874cec6995c828282fc86bdca0b6221280c970b342fd94a812feeb91224403eb. c2f/c3b recipes remain fixed and may be promoted only on tune; this final result will not guide further thresholds/training. |

| R4 c2d frozen thr=0.325 final | exact 84/100; TP/FP/FN 1226/53/67; P 0.958561 R 0.948183; CUDA640 p50 10.773ms p95 35.156ms |

| R4 c2d frozen thr=0.325 real | exact 0/1; TP/FP/FN 16/3/4; P 0.842105 R 0.800000; CUDA640 p50 10.141ms p95 13.622ms |
| R4 interpretation correction | Prior early-stop epoch107-113 is the SEMANTIC head, not instance-head300: native TrainInstanceHead runs its own epochs after it, reports progress95..99 (product remaps to76..79). The long stage is active instance training, not necessarily a hang. Old statement that changing instanceEpochs150/300 had no effect because both early-stopped is UNPROVEN/INCORRECT. R4 watcher evaluates c2f/c3b upon process exit; finite4h watchdog, files persist even if chat times out. |
| R4-3 c2g OFAT | Same600 project images as c2f, instanceEpochs60 instead of300; independent finite budget experiment motivated by native-stage runtime, not by final metrics. Detached WMI started; c2f300 and c3b300 continue untouched. Tune thresholds/ranking stay predeclared. |
| R4-3 launch correction | First c2g runner began3s before final label copy completed (run timestamp21:05:20 vs last label21:05:23). Aborted ONLY this round4 own CounterTool PID41424 and runner47708, discarded incomplete run, restarted after create completed600 images. No supplied/protected Python process was touched. Invalid first run produces no measurements. |
| R4-4 c3c OFAT | Same120 unique images and backbone1 as c3b, instanceEpochs60 instead of300; budget control for a complete fine-tuned instance model. WMI detached, logs train_c3c_round4.*, finite tune watcher. Choice based on tune only with original seven thresholds; no final-guided model recipe. |

| R4 c2d original thr.25 real error audit | {"images": 1, "exact": 1, "tp": 17, "fp": 3, "fn": 3, "fp_classes": {"loc": 3}, "fn_classes": {"loc": 3}, "fn_border": 0, "fn_overlapping_gt": 3, "by_bg": {"real": {"n": 1, "exact": 1, "fp": 3, "fn": 3}}, "fp_score_mean": 0.40844549735387164, "tp_score_mean": 0.44684937421013327}; coords/details and overlay in analysis/round4_errors_real.json and .jpg. Diagnostic only, never selection. |

#### R4 c2d remaining-error audit (original threshold0.25, counts reproduced)

| Split | TP/FP/FN | FP localisation/background/duplicate | FN localisation/merged/miss | Smallest GT tercile among FN |
|---|---|---|---|---|
| tune40 |615/32/27|31/1/0|26/0/1|17/27|
| final100 (diagnosis only)|1232/63/61|56/6/1|51/7/3|36/61|

Raw boxes: C:\Demo\LucGiac.counter3\analysis\round4_errors_{tune,final}.json; stats round4_errors_stats.json. On tune,20/31 localisation FPs and19/26 localisation FNs have best IoU0.4-0.5. Median predicted/GT area0.875-0.937, so much of FP/FN comes from biased box geometry. Tune lowcon has11FP/12FN. Morphology/min_area/semantic boundary are NOT useful knobs for this native PredictInstances box path (the wrapper only applies min_area to semantic components); no such sweep is claimed. Do not add final-only backgrounds into tune/train after observing final, as that would break the predeclared isolation contract.

Correction to auxiliary round4_posttrain.md estimate: old c2f had completed approximately104 instance epochs total (75+30 minus startup), not30 total. 52min/104 ~30s per epoch, not1.7min per epoch; the 8-9h estimate there is unsupported. New log timestamps are being monitored directly.

#### R4 interruption checkpoint (2026-11-10 21:14 GMT+7) — NOT COMPLETE

All four CounterTool processes are now absent (CIM + Get-Process checked), with no runner exit JSON and no saved new .segai model. Latest logs stopped around21:12; this coincided with a source turn-aborted event, but the exact terminating actor is not established. WMI launches returned0 and survived their launching exec, yet did NOT survive this later interruption. Do not call c2f/c3b/c2g/c3c trained or measured. No automatic restart after the intentional interruption.

| Experiment | Data unique train-side / head / backbone | Verified outcome |
|---|---|---|
| c2f round4 |600 /300 /0|Semantic head113 finished; instance training interrupted, no model.|
| c3b round4 |120 /300 /1|Backbone fine-tune1 epoch SUCCESS (105.226s,102 parameters, parity6.94e-5); instance stage interrupted, no model.|
| c2g control |600 /60 /0|Valid restarted run interrupted during semantic head; no model. Initial creation-race run intentionally discarded.|
| c3c control |120 /60 /1|Semantic head113 finished; interrupted during backbone stage, no model.|
| c2d tune-only threshold selection |240 /300 /0 (existing model)|Tune36/40 at threshold.325 vs33/40 at.25; selected before final.|
| c2d selected.325 final100|CUDA640,q0,3 repeats|84/100 exact, TP1226 FP53 FN67, P.958561 R.948183, all-matched61, p50/p95 10.773/35.156ms under shared training load.|
| c2d selected.325 real20|CUDA640,q0,15 repeats|19 detections =16TP+3FP,4FN; not exact.|
| c2d original.25 (historical comparator, not a new final-selected config)|640,q0|Historical85/100 exact, P.951/R.953; real re-audit reproduced20 detections17TP/3FP/3FN.|

Goal exact>=90/100 NOT REACHED; no completed new model from the detached training runs. No test leakage detected in content hashes/pixel hashes/seeds/source ids. Tune-selected threshold improvement did not generalise to final. Historical final has already been inspected across several rounds; it is not a pristine single-use test. Real image is diagnostic from the source photograph, not independent validation.

Existing c2d model: C:\Demo\LucGiac.counter3\c2d.easyai.data\models\v001.segai
SHA256 874cec6995c828282fc86bdca0b6221280c970b342fd94a812feeb91224403eb.
Before/after SHA256 audit: analysis/round4_artifact_before.json and round4_artifact_after.json; original c2d model, CounterTool binaries, production-build DLL and installed Lib/native/segai DLLs unchanged. No product/native code edited by round4, no original model or Lib/native overwrite, no commit/push, no completion ticks. New scripts/logs/results only under C:\Demo\LucGiac.counter3 and report updates here.

Finite auto-tune watchers/monitor also no longer observed; do not promise they will later publish results. Resume only after explicitly checking current processes and choosing a truly durable execution mechanism. Prior WMI launcher scripts and fixed tune sweep are preserved for review; avoid concurrent creation+training and don't rerun round4_audit.py (it overwrites the BEFORE hash snapshot).
Cleanup: the tool-managed wait-only monitor keen-valley was still alive despite WMI train/watch processes disappearing; explicitly stopped that monitor after verifying no training models/processes remained. No training process or protected Python was stopped in this cleanup.

### Counter accuracy round 5 (overlap-heavy synthetic data, project `c2g_ov`) - NOT QA-signed, not ticked

Why: round-4 audit showed the residual errors are localisation FP/FN pairs on overlapping screws (real photo: 3 pairs, IoU 0.435/0.480/0.495), but the existing synthgen compose only allows 0/4/12 % overlap (sparse/medium/dense), so the train data hardly contain piled screws. Name note: `C:\Demo\LucGiac.counter3\c2g.*` already exists from the interrupted round-4 control run (no model), so the new project is called **`c2g_ov`** (c2d/c2a/c2f/c2g untouched).

| R5-1 data | `tools/segai_tune/gen_overlap.py` (new): same patches/backgrounds/lighting as synthgen.compose, new placement loop: per-image overlap cap 0.10/0.20/0.35/0.55 of the new screw's area, every earlier screw keeps >= 40 % of its pixels visible, 40-85 % of screws are dropped near an existing screw (distance 0.2-0.9 of screw length) with random / near-parallel (+-14 deg) angles, later screws on top, label = FULL oriented box of each screw (as the real labelled photo does). Overlay checked on 4 smoke images (boxes follow full screw bodies incl. the hidden part). Sets: `ov_train` 240 imgs (train source screws only, TRAIN_BG, seeds 20261190000+), `ov_tune` 40 imgs (the TUNE source screw only, seeds 20261170000+, overlap tune set), `ov_final` 60 imgs (FINAL source screws, all bg families incl. unseen wood/dots/carbon, seeds 20261180000+). Script asserts: no seed / sha256 overlap with the frozen manifest, sources inside the split's own group, train sources disjoint from tune/final. Mean overlapped screws per image: 4.2 (train) / 4.95 (tune) / 5.3 (final). Output `C:\Demo\LucGiac.counter3\ov_{train,tune,final}\manifest.json`. |
| R5-2 c2g_ov train | `CounterTool create frozen-manifest C:\Demo\LucGiac.counter3 c2g_ov obb canon sym=2 extra=extra\manifest.json extra=ov_train\manifest.json` = c2d's 240 images (120 frozen + 120 extra) + 240 ov_train = 480 unique images (val = first 30 as c2d). Train exactly as c2d: `CounterTool train c2g_ov.easyai 200 300 -1 0` (epochs 200, instanceEpochs 300, rotation app default 90, CPU trainer, 640, symmetry 2, canonical labels), production-build DLL on PATH. Launched DETACHED via Task Scheduler task `EAI_c2g_ov_train` (runs `run_c2g_ov.ps1`, full powershell.exe path required), CounterTool PID 4024 (file `train_c2g_ov.pid`), log `train_c2g_ov.log`, exit file `train_c2g_ov.exit.json`. c2d (240 imgs) took 51 min on CPU, so ~100 min expected. |
| R5-0 c2d baseline on the new overlap tune set (harness `C:\Demo\LucGiac.counter3\analysis\r5_eval.py sweep c2d <model>`, CUDA 640 q0, instance_mode, machine NOT idle: training running on CPU, p50 ms indicative only) | tune40 / ov_tune40 per threshold (exact; TP/FP/FN): .2: 32/40; 615/37/27 \| 15/40; 528/65/113. .225: 33; 615/34/27 \| 15; 527/60/114. .25: 33; 615/32/27 \| 14; 523/57/118. .275: 35; 615/30/27 \| 13; 520/56/121. .3: 36; 615/29/27 \| 12; 514/53/127. .325: 36; 615/27/27 \| 11; 509/48/132. .35: 31; 610/26/32 \| 7; 497/42/144. tune p50 9.5-10.3 ms. => the new ov_tune set is hard for c2d (exact only 7-15/40, recall ~82 %), i.e. real headroom for overlap data. Files `metrics/round5/c2d_*`. |

| R5-3 c2g_ov RESULT (measured 2026-11-11 by a sub-agent; model `C:\Demo\LucGiac.counter3\c2g_ov.easyai.data\models\v001.segai`, 12009288 bytes, written 01:29, SHA256 **45e09c445ec6a88af5c021d31b80545e18f5991993ba4d7f0ffda23067f90124**; c2d SHA256 unchanged 874cec69...403eb; not retrained, candidate "not active") | NOT QA-signed, not ticked. Harness `analysis/r5_eval.py` (CUDA 640, q0, instance_mode, production-build DLL), logs `analysis/r5_c2g_ov_sweep.log`, `r5_c2g_ov_final_0.325.log`, `r5_c2g_ov_final_0.25.log`, json `metrics/round5/c2g_ov_*`. |
| R5-3 tune-only sweep (tune40 + ov_tune40, predeclared 7 thresholds; exact; TP/FP/FN) | .2: 27+11=38/80; tune 618/47/24 ov 548/70/93. .225: 28+12=40; 618/42/24 \| 548/61/93. .25: 30+13=43; 618/38/24 \| 548/55/93. .275: 34+16=50; 617/33/25 \| 546/47/95. .3: 35+14=49; 617/28/25 \| 543/44/98. **.325: 36+15=51; 617/27/25 \| 539/39/102 (F1 .9230) = selected by tune only**. .35: 31+12=43; 612/25/30 \| 531/34/110. For reference c2d on the same sets (R5-0): ov_tune exact 11/40 at .325 (509/48/132) and 12/40 at .3 -> c2g_ov is better on the overlap tune set (+4 exact, recall 80.8->84.0 %, FN 132->102) and equal on tune40 (36/40 both at .325). |
| R5-3 FINAL100 (single run each; BGR decode excluded) | **thr .325: exact 82/100; TP/FP/FN 1237/49/56; P .9619 R .9567 F1 .9593; p50 8.40 ms.** thr .25: exact 79/100; 1242/66/51; P .9495 R .9606 F1 .9550; p50 8.21 ms. c2d for comparison: 85/100 at .25 (P .951 R .953), 84/100 at .325 (1226/53/67, P .9586 R .9482). => exact count is WORSE than c2d (-2 at .325, -6 at .25); TP/FP/FN-level P/R are slightly better (+11 TP, -4 FP, -11 FN at .325) but not enough to turn more images exact. |
| R5-3 ov_final60 (unseen screws 2,4, incl. unseen backgrounds; extra evidence, c2d NOT measured on it so no paired comparison) | thr .325: exact 18/60; 839/68/189; P .9250 R .8161 F1 .8672. thr .25: 19/60; 842/88/186; P .9054 R .8191. Recall on heavily overlapping unseen screws stays only ~82 %, i.e. the tune gain on ov_tune (same recipe, 1 source screw) only partly transfers. |
| R5-3 REAL photo Cam_20261019_160350_387.bmp (20 labelled screws, 15 repeats; source screws 0,1,11,17 of this photo ARE in the train patches, so diagnostic only) | thr .325 and .25 give the same result: 19 detections = 17 TP + 2 FP, 3 FN; P .8947 R .8500; exact 0/1 (19 != 20); p50 8.36 / 8.65 ms. c2d: .25 -> 17TP/3FP/3FN (20 det, count exact but with 3 wrong pairs), .325 -> 16/3/4. => FP/FN pairs 12 (IoU .498) and 13 (IoU .491) are still localisation pairs just under IoU .5 + 1 merged FN (gt 16, best IoU .189, i.e. one screw swallowed by a neighbour). Count is no longer exact (19 vs 20), so by the "real exact 20 at the right positions" criterion there is NO improvement. Overlay (green TP, red FP, yellow FN): `C:\Demo\LucGiac.counter3\analysis\r5_errors_real.jpg` (+ .json). All 3 errors are in the pile at the lower middle (x 900-1200, y 1300-1500 in source px). |
| R5-3 leakage (script `analysis/r5_leakcheck.py`, log `r5_leakcheck.log`) | NO LEAK: 480 unique c2g_ov.images PNG vs tune40/final100/real/ov_tune/ov_final: file-sha256 0, decoded-pixel hash 0, manifest sha 0, seed overlap 0; train-side sources {0,1,11,17} disjoint from tune {3} and final {2,4} (ov_tune {3}, ov_final {2,4}). Real photo shares sources 0,1,11,17 with train patches by construction. |
| R5-3 measurement noise | Machine not idle: CPU load ~71 % from foreign powershell/Defender/python processes during the runs, GPU 0 % before; p50 8.2-8.7 ms (c2d R4 values 10-15 ms were measured under CPU training load) so latency is NOT comparable across rounds. Each final was run once (3 repeats of timing inside); accuracy is deterministic. 'Final' has already been inspected in several rounds, so it is not a pristine test set. |
| R5-3 verdict | c2g_ov does NOT meet the success criterion (final >= 88/100 or real exactly 20 at right places): final 82/100 @.325 (79 @.25) < c2d 85/84, real 19 det with 3 FN. Overlap synthetic data (+240 imgs) improved ov_tune recall and slightly P/R on final but not per-image exact counts; do not promote over c2d. Not proven: whether the loss in exact count on final is a real regression or just image-level noise (diff of 2-6 images on one seed set), whether c2d would also reach ~82 % recall on ov_final, and whether more overlap alone (vs more source-screw diversity: only 4 train source screws) is the limiter. Suggested next steps (not started): (1) measure c2d on ov_final60 at .325 for a paired comparison (minutes, no training); (2) per-image paired diff c2d vs c2g_ov on final100 to see which images flipped and whether they are overlap images; (3) error audit on ov_final FN (merged vs loc vs truly occluded >60 %); (4) if localisation IoU .49 pairs dominate, test box-geometry fixes (instance box regression / a measured fit of predicted box to the object) rather than more data; (5) add more distinct source screws/backgrounds before adding more overlap. |

#### R5-4 c2d vs c2g_ov paired audit + box-geometry audit (eval only, NO training, NO model/DLL/segai change) - NOT QA-signed, not ticked
Scripts/logs in `C:\Demo\LucGiac.counter3\analysis\`: `r5_dump.py` (per-image dump incl. boxes), `r54_analyze.py` (log `r54_abc.log`, `r54_d.log`), `r54_trainfit.py` (`r54_trainfit.log`); data `metrics\round5\R54_{c2d,c2g_ov}_t{0.325,0.25}_{final,ovfinal,real}.json`; CSV `r54_final100_per_image_t0.325.csv`, `r54_{c2g_ov,c2d}_t0.325_errors.csv`, `r54_geometry_pairs.json`. Dump reproduces R5-3 exactly for c2g_ov (82/100, 839/68/189 etc.) and R4 for c2d (84/100 @.325, 85/100 @.25). Harness: `ce.evaluate` contract (greedy by score, IoU>=0.5), CUDA 640, q0.

| Item | Result |
|---|---|
| R5-4a ov_final60, c2d vs c2g_ov (same harness) | @.325: c2d exact **16/60** (all-matched 8) TP/FP/FN 789/83/239 P .905 R .768 F1 .831 vs c2g_ov **18/60** (13) 839/68/189 P .925 R .816 F1 .867. @.25: c2d **22/60** (9) 804/96/224 F1 .834 vs c2g_ov 19/60 (11) 842/88/186 F1 .860. Paired exact @.325: both 12, c2d-only 4, c2g-only 6, neither 38; @.25: both 13, c2d-only 9, c2g-only 6, neither 32. => c2g_ov is better on TP/FN/F1 (+50 TP, -50 FN @.325) but NOT better on exact count at its own best thr; at .25 c2d has more exact images. Difference of 2-3 images of 60 is inside noise (no CI, single dataset). |
| R5-4b final100 @.325 paired (CSV) | both exact 76, **c2d-only 8, c2g_ov-only 6**, both wrong 10 (net 14 images flip, 82 vs 84). Every flipped image is off by exactly 1 object (c2d-only: c2g_ov +-1; c2g_ov-only: c2d -1). 55/100 images have truth boxes overlapping (IoU>0). Flipped images: c2d-only 6/8 overlap, mean truth count 20.9, mean overlap pairs 4.25; c2g_ov-only 5/6 overlap, count 17.2, pairs 1.83; both-wrong 7/10 overlap, 16.9, 3.20; both-ok 37/76 overlap, count 11.2, pairs 1.05. Exact in overlap images 43 (c2d) vs 42 (c2g_ov), in non-overlap 41 vs 40. => the flips happen on DENSE images (17-21 screws vs 11 for always-correct) and mostly overlapping ones, but c2g_ov's overlap training did not buy a net gain even there; no evidence it helps overlap images on final100. |
| R5-4c error classes, c2g_ov @.325 (FP / FN) | final100 FP 49: offset(i) 43, merge(ii) 0, true FP(iv) 3, other 3; FN 56: offset 43, missed(iii) 7, other 6. ov_final60 FP 68: offset 49, merge 9, true FP 0, other 10; FN 189: offset 49, merge 16, missed 17, **other 107** (truth overlaps a pred at IoU .1-.3 that is matched to a neighbouring truth: 92/107 = neighbour absorbed/suppressed it, 15 partial). real FP 2: offset 2; FN 3: offset 2, other 1. c2d @.325: final100 FP offset 49 / true FP 2 / other 2, FN offset 49 / missed 10 / other 8; ov_final60 FP offset 60, merge 15, other 8, FN offset 60, merge 26, missed 23, other 130. Classes: (i) = unmatched pred vs unmatched truth, IoU .3-.5, 1-1 greedy; (ii) pred IoU>.3 with >=2 truth; (iii) no pred IoU>=.1; (iv) pred IoU<.1 to all truth; other = the remainder (also defined above). |
| R5-4c' what drives COUNT errors | Offset pairs (i) are count-neutral (one FP + one FN). Among the 18 count-wrong final100 images of c2g_ov: offset 12 FP+12 FN, unpaired FN 13 (missed 7, other 6) vs unpaired FP 6 => net under-count; NO image is count-wrong because of offsets alone (0/18, 0/42 on ov_final, 0/16 and 0/44 for c2d). |
| R5-4d match-IoU diagnostic (DIAGNOSTIC ONLY, NOT official, same predictions, changes only how TP is counted; count_exact does not depend on IoU) | c2g_ov final100 TP/FP/FN: IoU>=.5 1237/49/56 (all-matched images 65); >=.4 1273/13/20 (77); >=.3 1280/6/13 (82). ov_final60: >=.5 839/68/189 (13); >=.4 887/20/141 (15); >=.3 898/9/130 (16). real: .5 17/2/3; .4 19/0/1. c2d final100: .5 1226/53/67 (61); .4 1269/10/24 (79); .3 1275/4/18 (82). c2d ov_final60: .5 789/83/239 (8); .4 848/24/180 (15). I.e. ~3/4 of FP and ~2/3 of FN on final100 vanish at IoU .4: they are localisation misses, not missed screws. |
| R5-4d geometry error, c2g_ov @.325, pairs = TP + type(i) (n=2187; TP-only n=2093 in log) | centre |err| 7.7 px (median 6.7; type-i 14.6); angle |err| 6.8 deg (signed -0.1; type-i 8.0); long side |err| 11.0 px, signed **+2.1** (ratio 1.04); short side |err| 6.4 px, signed **-5.5** (ratio **0.90**); mean IoU .677. Same for c2d (n=2143): centre 7.8, angle 6.7 (+0.0), long +0.7 (1.03), short **-5.9 (0.90)**, IoU .671. Short-side shrink exists on isolated screws too (c2g_ov -6.0 / ratio .89) and less on overlapped (-4.7 / .92); mean truth L 115.6 px, S 48.0 px, aspect 2.4. Grows with width: truth S<40 px ratio .94 (-1.9 px), 40-60 ratio .895 (-5.1), >60 ratio .846 (-12.5 px). By set: final centre 7.2 / short -5.9; ov_final 8.3 / -5.0; real (n=19) centre 18.5, long **+28 px**, short +4.7 (real long side over-estimated, small n). |
| R5-4d same model on ITS OWN training images (60 of 960, every 8th, thr .325; diagnostic) | TP/FP/FN 780/47/112; short side signed **+0.5 px (ratio 1.03)**; by truth S: <40 +1.8 (1.07), 40-60 -0.4 (.99), >60 -2.6 (.97); long +1.9; centre 5.9 px; angle |err| 5.6 deg; IoU .715. => no systematic short-side shrink in the decode/encode path itself (train fit ~unbiased); the shrink appears only on unseen screws. |
| R5-4d scale distribution (short side S, px) | all 7676 train labels: p5 18, p25 26, **p50 35**, p75 47, **p95 71**, 10.8 % with S>60; final100 truth: p5 23, p25 34, **p50 42**, p75 56, **p95 86**, 21 % >60; ov_final: p50 43, p95 81, 20 % >60; tune: p50 40, p95 75, 12 % >60. Train labels are thinner than final/ov_final truth (sources 2,4 are wider than train sources 0,1,11,17; same long side ~102 vs 97 median). |

Code reading (grep only, no edit): width/height are regressed in log space at stride 8 - targets `SegInstanceTargets.cpp:233` (`logW = log(w/stride)`, written to channels 2/3 at :245-246 for the 3x3 cells around the centre), decoded `SegInstanceTargets.cpp:314-315` (`w = exp(clamp(reg[2])) * stride`), angle via atan2(sin k*theta, cos k*theta)/k at :321, loss is an L1 on regression (`SegInstanceHead.cpp:316`). Encode/decode are exact inverses, and the train-set fit shows no bias, so the mechanism is NOT a geometric decode error. Hypothesis (not proven): the head under-predicts width for screws wider than the training distribution (regression toward the training mean, p95 S 71 vs 86), i.e. a data-scale/source-diversity effect; angle (5.6 deg on train, 6.7 on unseen) and centre (5.9 -> 7.7 px) errors are mostly a resolution/capacity floor of the stride-8 head and exist on train too. Refine (`SegV2Core.cpp:2109`, quality==1) needs class templates (`instanceTemplates`); not tested here (R4 found q1 identical for the old model). Not modified.

**R5-4e Conclusion (what the numbers support / do not):**
1. Box geometry is NOT what limits exact count. Offset (IoU .3-.5) pairs account for most FP+FN on final100 (43/49, 43/56) but they cancel in the count; no count-wrong image is caused by offsets alone. Fixing box geometry (even perfect IoU>=.4) would change P/R/F1 (FP 49->13, FN 56->20 @IoU .4) but would NOT change count_exact, by construction. Count errors come from missed/absorbed objects: final100 FN missed+other 13 vs FP 6; ov_final60 FN 'other' 107 + merge 16 + missed 17 of 189 (neighbour pred absorbs the hidden screw), i.e. detection/peak separation on overlap and dense scenes.
2. A systematic box shrink exists (short side -10 %, up to -15 % on S>60 px) but only on unseen/wider screws; train-set fit is unbiased. So there is no evidence for a decode/geometry code bug; a code fix of box geometry is not supported by these numbers. The supported cause is a data one: train short-side distribution is narrower than final/ov_final (p95 71 vs 86; 11 % vs 21 % wide screws) and only 4 source screws.
3. Recommendation (priority): (a) add data - more source screws, specifically WIDER/larger screws (S>60 px) and dense 17-22-screw images with overlap, because c2g_ov's overlap synthesis at 4 sources gave only +50 TP/-50 FN on ov_final60 but no net exact-count gain on final100 (82 vs 84); (b) only if (a) is not possible, consider the detection side (peak NMS 3x3 at stride 8 / neighbour absorption), NOT box geometry. Not recommended now: sweeping IoU or editing box geometry.
Not proven: (i) that wider source screws would raise exact count (no data trained - hypothesis); (ii) that peak-NMS/3x3 window is the cause of 'absorbed' FN (only inferred from IoU .1-.3 overlap with a neighbour-matched pred; not tested); (iii) whether quality=1 helps (model has no templates verified); (iv) c2d vs c2g_ov exact-count differences (2/100, 2-3/60) are within noise, single runs, final100 already inspected in several rounds (not pristine); (v) real photo n=1 image / 20 screws: geometry stats there (long +28 px) are anecdotal.
Measurement noise: CPU load ~72 % (foreign processes) and GPU 0 % at the time of the audit; accuracy runs are deterministic (re-runs reproduced R5-3/R4 counts exactly) so only latency would be affected, and latency was not measured in R5-4.


### QA fixes + production-build2 + DLL replacement (2026-11-11) - NOT QA-signed, not ticked, not committed

Details, fixed-bug table and file:line in `CUDA_BACKEND.md` section 13. Summary of what was done and proven:

| Item | Result |
|---|---|
| QA bugs a-g + new fix h (torch_cpu preload) | code fixed; b/e/f have behaviour tests, c smoke, a/d compile+read only |
| 0xC06D007E at train | cause: delay-load of `torch_cpu.dll` does not search the DLL folder; reproduced with the installed DLL, fixed in the new DLL (train smoke OK even without `cuda_available`) |
| torch_cpu.dll hash | only the PE TimeDateStamp (2 places) differs, rest byte-identical; source URL + zip SHA256 in section 13.4 |
| New DLL | 950,272 B, SHA256 2431C371D43081C923245396D288FEA47D1E8AC91100A3BB26A2523A8CCCFE60 |
| Tests | SegAI.Tests 1612 pass / 1 known fail (Apply_DoesNotOverwriteUserChangedOptions, also at clean HEAD) / 21 skipped; Core 441/441; Commands 275/275; new selector tests 8/8 |
| c2d @.25 with new DLL | CPU = CUDA: final100 85/100 (1232/63/61), tune40 33/40, real 17/3/3 - unchanged |
| App replacement | done; backup `EasyLabel\Lib\native\segai.bak-20261111-0818` (old DLL 884,224 B SHA256 85B6DFA8...358D22); app folder now 49 files = production-build2\bin; eval from the app folder gives the same counts; `dotnet build EasyLabel.sln` 0 errors |

Not checked: Quadro P5000 / Intel GPU, CUDA training, running the real EasyLabel app, redistribution license of NVIDIA/libtorch DLLs, C++ test project.

### Lane A 2026-11-11T10:17:59
Baseline cuda: rc=1; 4 blocks x 60 = 240 measured calls after warmup. Evidence: tmp/EAI-SEGAI-CUDA/laneA/baseline_cuda.log and production-build3/results/laneA_baseline_cuda.summary.json. No source changes yet.


### Lane A 2026-11-11T10:31:31 — diagnostic checkpoint
- C:\Demo is currently absent (os.path.lexists false), although task says junction exists. Read-only harness switched to D:\Demo; manifest paths will be normalized at runtime. No dataset changed.
- Baseline cannot yet finish: real CPU and CUDA first Predict wait many minutes; Python faulthandler locates wait inside SEGAI_InferPredictInstances. cl.exe (pid 25496) has ~8 CPU seconds after many wall minutes and its only thread reports Wait/PageIn. RAM available ~26 GB; C free ~105 GB, D free ~335 GB. This is not a usable performance sample.
- Isolated production-build3 build is in progress, no app deployment. Native change staged: make repeated selection of the same healthy GPU backend idempotent (app ConfigureInferer otherwise destroys ORT session each image); compile-only CUDA Run failure hook added. No accuracy or speed claim yet.
- Added dedicated multi-handle stress and same-call fallback behaviour harnesses under BeeNativeSegAI/test.


### Lane A 2026-11-11T10:36:29 — IO finding
- D: is physical disk 0 HGST HTS721010A9E630 (rotating HDD). Disk queue measured 19, ~31 reads/s, PercentDiskTime 2342; compiler thread Wait/PageIn. CPU only ~36%, no RAM shortage. Sequential copy opencv_world455.dll 70.5 MB took 29.6 s.
- Staging a read-only copy of baseline runtime onto shared SSD temp C:\Space\workspaces\shared\main\.openclaw\tmp\EAI-SEGAI-CUDA-laneA\baseline-bin to separate disk cold-page stalls from steady-state inference. This does not touch app or production-build2. Final output remains production-build3/bin on D:. Stopped own redundant hanging diagnostic/probe workers.


### Lane A 2026-11-11T10:39:10 — completed CPU baseline / candidate prepared
- SSD baseline2, final0 640x640, 4x50=200 calls after 10 warmup/block: phase p50/p95/min ms: prepare .653/.967/.456; tensor 1.141/1.846/.717; OpenVINO infer 29.171/44.263/20.497; decode .108/.165/.081; total call 31.552/46.772/22.537. Model load p50 600 ms, first call 230 ms; CPU 86%, GPU 5–6%. Evidence laneA/ssd_baseline_cpu.log and production-build3/results/laneA_ssd_baseline_cpu.summary.json. Baseline before buffer source change.
- Candidate adds per-handle instance-only tensor/channel reuse (no math change), optional CPU thread tuning, opt-in ORT profiling; no retained performance claim until interleaved A/B + full parity. Native repeated GPU selection also made idempotent while healthy.


### Lane A 2026-11-11T10:42:48
production-build3/bin prepared: 47 files; native SHA256 31408A7BA52FC3D39181C9DC28F7390001A6990D9D6B8E79B2400E5BF68933AB; test fault marker absent. Runtime dependencies hard-linked read-only from build2; only new native DLL is copied.


### Lane A 2026-11-11T10:42:48
Evaluation uses SSD mirror C:\Space\workspaces\shared\main\.openclaw\tmp\EAI-SEGAI-CUDA-laneA\candidate-bin; native DLL SHA-identical to production-build3/bin; dependencies share the baseline SSD runtime by read-only hardlinks.


### Lane A 2026-11-11T10:43:04
Native original test project build rc=0; log laneA/native_test_build.log (isolated obj/bin SSD, no repo test outputs).


### Lane A 2026-11-11T10:43:05
Original C++ test --class-table-selftest: rc=0; log laneA/class-table-selftest.log


### Lane A 2026-11-11T10:43:06
Original C++ test --inst-targets-selftest: rc=0; log laneA/inst-targets-selftest.log


### Lane A 2026-11-11T10:43:07
Original C++ test --inst-refine-selftest: rc=0; log laneA/inst-refine-selftest.log


### Lane A 2026-11-11T10:43:09
Original C++ test --v2-shape-cleanup-selftest: rc=1; log laneA/v2-shape-cleanup-selftest.log


### Lane A 2026-11-11T10:46:33
production-build3/bin prepared: 47 files; native SHA256 31408A7BA52FC3D39181C9DC28F7390001A6990D9D6B8E79B2400E5BF68933AB; test fault marker absent. Runtime dependencies hard-linked read-only from build2; only new native DLL is copied.


### Lane A 2026-11-11T10:46:33
Evaluation uses SSD mirror C:\Space\workspaces\shared\main\.openclaw\tmp\EAI-SEGAI-CUDA-laneA\candidate-bin; native DLL SHA-identical to production-build3/bin; dependencies share the baseline SSD runtime by read-only hardlinks.


### Lane A 2026-11-11T10:46:33
prepare_mirror: rc=0; evidence tmp/EAI-SEGAI-CUDA/laneA/prepare_mirror.log


### Lane A 2026-11-11T10:46:52 — CPU A/B complete
4 rotating rounds x 50 calls/variant after warmup; all variants count 3 and identical rounded detections. Summary: {"baseline": {"cpu": 86.4, "ct": {"p50": 31.647399999201298, "p95": 47.814815006859135, "min": 19.73650002037175, "mean": 32.92556100059301}, "tensor": {"p50": 1.1723, "p95": 1.7184049999999984, "min": 0.7991, "mean": 1.310749}, "infer": {"p50": 29.29535, "p95": 44.592315, "min": 18.0559, "mean": 30.594776999999993}, "blocks": [31.647399999201298, 25.358450002386235, 31.253300010575913, 37.91934999753721]}, "reuse": {"cpu": 75.5, "ct": {"p50": 25.46565000375267, "p95": 36.58013499371007, "min": 20.648100005928427, "mean": 27.426266001275508}, "tensor": {"p50": 0.49219999999999997, "p95": 0.710545, "min": 0.3613, "mean": 0.509652}, "infer": {"p50": 24.04945, "p95": 34.74073, "min": 19.6104, "mean": 25.99106}, "blocks": [26.03389999421779, 23.860950008383952, 24.38080000865739, 30.45905001636129]}, "noreuse": {"cpu": 77.5, "ct": {"p50": 27.216199989197776, "p95": 35.920835002616506, "min": 15.848099981667474, "mean": 27.878374499850906}, "tensor": {"p50": 1.0655999999999999, "p95": 1.5304349999999993, "min": 0.7044, "mean": 1.1358840000000001}, "infer": {"p50": 25.1751, "p95": 33.536874999999995, "min": 14.3879, "mean": 25.824686999999997}, "blocks": [25.91229999961797, 28.387750004185364, 26.491349999560043, 29.54594999027904]}, "threads4": {"cpu": 74.3, "ct": {"p50": 30.010000002221204, "p95": 45.06261999777052, "min": 19.963600003393367, "mean": 31.07771250011865}, "tensor": {"p50": 0.4889, "p95": 0.7326149999999998, "min": 0.3597, "mean": 0.526282}, "infer": {"p50": 28.7806, "p95": 43.45524999999999, "min": 18.7647, "mean": 29.640166499999996}, "blocks": [24.789050003164448, 26.81624999968335, 31.460650003282353, 37.878550007008016]}, "threads8": {"cpu": 88.0, "ct": {"p50": 24.866149993613362, "p95": 45.31163000065134, "min": 18.05309997871518, "mean": 27.837982500204816}, "tensor": {"p50": 0.54335, "p95": 0.9083449999999996, "min": 0.3523, "mean": 0.5791275000000001}, "infer": {"p50": 23.3639, "p95": 43.326604999999994, "min": 17.0369, "mean": 26.299458000000005}, "blocks": [19.585250003729016, 21.616800004267134, 29.120399995008484, 36.98889999941457]}}
- Input tensor reuse cuts tensor phase by about half. Overall CT / CPU thread results are heavily confounded by CPU load 62–97%; no default CPU thread override selected. Original C++ self-tests: class table 147/147, instance targets 311/311, instance refine 46/46 (old-model case skipped, fixture absent), shape cleanup 10/10. No default test mode run because it writes/trains into repo test/data.


### Lane A 2026-11-11T10:47:27
cuda_ab_plain: rc=0; evidence tmp/EAI-SEGAI-CUDA/laneA/cuda_ab_plain.log


### Lane A 2026-11-11T10:49:39
cuda_ab_applike: rc=0; evidence tmp/EAI-SEGAI-CUDA/laneA/cuda_ab_applike.log


### Lane A 2026-11-11T10:50:11
build_timing: rc=0; evidence tmp/EAI-SEGAI-CUDA/laneA/build_timing.log


### Lane A 2026-11-11T10:50:11
production-build3/bin prepared: 47 files; native SHA256 45F39A50B0B398042C9716BBECB484DC405FD0F1000786E25D35C9487FD1A4E2; test fault marker absent. Runtime dependencies hard-linked read-only from build2; only new native DLL is copied.


### Lane A 2026-11-11T10:50:11
Evaluation uses SSD mirror C:\Space\workspaces\shared\main\.openclaw\tmp\EAI-SEGAI-CUDA-laneA\candidate-bin; native DLL SHA-identical to production-build3/bin; dependencies share the baseline SSD runtime by read-only hardlinks.


### Lane A 2026-11-11T10:50:19
parity_cpu: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/parity_cpu.log


### Lane A 2026-11-11T10:50:25
parity_cuda: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/parity_cuda.log


### Lane A 2026-11-11T10:50:26
probe_stress: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/probe_stress.log


### Lane A 2026-11-11T10:50:26
parity_and_stress: rc=0; evidence tmp/EAI-SEGAI-CUDA/laneA/parity_and_stress.log


### Lane A 2026-11-11T10:50:58 — CUDA A/B + full parity complete
- Plain fixed-handle CUDA 4x50 calls: old -> reuse p50/p95/min 8.479/9.943/7.260 -> 8.073/10.237/6.291 ms; tensor 1.054 -> .583 ms. CPU load 55% vs 69%; call p95 not improved, do not claim reliable overall 4.8% outside noise. No-reuse control tensor 1.254 ms, CT 9.447 ms.
- App-like ConfigureInferer on every call, rotating A/B4x50: old 467.361/553.996/404.153 -> new 6.793/7.598/5.948 ms; old session compile p50 114 ms + first-Run/cuDNN search every frame ~332 ms, new compile0ms after warmup. Both backend1 and detections identical. CPU load46% vs31%; >60x effect dwarfs noise, but applies to repeated configuration path, not already-configured native loop.
- Timed DLL build successful (pre-final-test SHA45F39A50B0B398042C9716BBECB484DC405FD0F1000786E25D35C9487FD1A4E2). CPU and CUDA full parity @.25 pass unchanged: tune40 33/40 615/32/27; final10085/1001232/63/61; real20 detections17/3/3.
- Multi-handle positive probe stress12threadsx1000 calls passes. Fault-build-only behaviour test is now running; final non-fault rebuild and parity will follow.


### Lane A 2026-11-11T10:51:39
build_fault: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/build_fault.log


### Lane A 2026-11-11T10:51:45
cuda_run_failure: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/cuda_run_failure.log


### Lane A 2026-11-11T10:52:01
build_final_no_fault: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/build_final_no_fault.log


### Lane A 2026-11-11T10:52:01
production-build3/bin prepared: 47 files; native SHA256 6756FF21BD684C302A51FE3E4CCAA2D041D1421D22B6A060F7A771905234183B; test fault marker absent. Runtime dependencies hard-linked read-only from build2; only new native DLL is copied.


### Lane A 2026-11-11T10:52:01
Evaluation uses SSD mirror C:\Space\workspaces\shared\main\.openclaw\tmp\EAI-SEGAI-CUDA-laneA\candidate-bin; native DLL SHA-identical to production-build3/bin; dependencies share the baseline SSD runtime by read-only hardlinks.


### Lane A 2026-11-11T10:52:10
parity_cpu: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/parity_cpu.log


### Lane A 2026-11-11T10:52:17
parity_cuda: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/parity_cuda.log


### Lane A 2026-11-11T10:52:18
probe_stress: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/probe_stress.log


### Lane A 2026-11-11T10:52:18
final_parity_and_stress: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/final_parity_and_stress.log


### Lane A 2026-11-11T10:52:19
probe_stress_negative: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/probe_stress_negative.log


### Lane A 2026-11-11T10:52:42
production-build3/bin prepared: 48 files; native SHA256 6756FF21BD684C302A51FE3E4CCAA2D041D1421D22B6A060F7A771905234183B; test fault marker absent. Runtime dependencies hard-linked read-only from build2; only new native DLL is copied.


### Lane A 2026-11-11T10:52:42
Evaluation uses SSD mirror C:\Space\workspaces\shared\main\.openclaw\tmp\EAI-SEGAI-CUDA-laneA\candidate-bin; native DLL SHA-identical to production-build3/bin; dependencies share the baseline SSD runtime by read-only hardlinks.


### Lane A 2026-11-11T10:52:42
prepare_final_bin: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/prepare_final_bin.log


### Lane A 2026-11-11T10:53:07
final_ab_cpu: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/final_ab_cpu.log


### Lane A 2026-11-11T10:53:11 — hardening proven on real runtime
- Dedicated fault build prepended compile-only macro SEGAI_TEST_INSTANCE_CUDA_RUN_FAILURE to staged SegV2Core.cpp; actual ORT CUDA session constructed, exception injected at Run; real image returns success and 20 detections identical to CPU within the SAME call. Backend first/next [0,0], reason includes CUDA Run failed and test marker; second call remains CPU with identical detections. Source staged macro restored and final DLL rebuilt; marker byte-string absent in production DLL.
- Final rebuilt DLL SHA256 6756FF21BD684C302A51FE3E4CCAA2D041D1421D22B6A060F7A771905234183B. Full CPU/CUDA parity rerun passes all aggregate targets; positive stress12threads x1000 and negative stress12threads x1000 (onnxruntime.dll omitted in a separate SSD fixture) pass. Each thread owns its handle; no unsupported concurrent Predict on one handle.
- Probe source audit: g_cudaProbeReason accessed only by locked set/copy getters; ProbeCuda ok flag and EnsureOrtLoaded loaded flag are under their own mutexes. Stress is evidence, not a formal race-freedom proof/TSAN. Final diagnostics running separately from profiling overhead.


### Lane A 2026-11-11T10:53:29
final_ab_cuda: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/final_ab_cuda.log


### Lane A 2026-11-11T10:53:31
ort_profile_real: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/ort_profile_real.log


### Lane A 2026-11-11T10:53:37
managed_cpu_reuse: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/managed_cpu_reuse.log


### Lane A 2026-11-11T10:53:45
managed_cpu_noreuse: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/managed_cpu_noreuse.log


### Lane A 2026-11-11T10:53:48
managed_cuda_reuse: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/managed_cuda_reuse.log


### Lane A 2026-11-11T10:53:52
managed_cuda_noreuse: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/managed_cuda_noreuse.log


### Lane A 2026-11-11T10:54:02
train_smoke: rc=0; log tmp/EAI-SEGAI-CUDA/laneA/train_smoke.log


### Lane A 2026-11-11T10:56:32
Test-only split IO-binding profiling: rc=0; logs laneA/split_profile_build.log and split_profile.log. Header restored byte-for-byte. Production DLL was not overwritten. This is phase attribution, not a production performance A/B.


### Lane A 2026-11-11T10:57:14
Test-only split IO-binding profiling: rc=0; logs laneA/split_profile_build.log and split_profile.log. Header restored byte-for-byte. Production DLL was not overwritten. This is phase attribution, not a production performance A/B.


### Lane A 2026-11-11T10:58:47
True file-to-detections end-to-end A/B cpu rc=0; each of 200 calls per variant includes cv2.imread/decode; 4 rotating blocks after warmup. Log laneA/file_ab_cpu.log


### Lane A 2026-11-11T10:59:15
True file-to-detections end-to-end A/B cuda rc=0; each of 200 calls per variant includes cv2.imread/decode; 4 rotating blocks after warmup. Log laneA/file_ab_cuda.log

### Lane A wrap-up 2026-11-11 bắt đầu
- Chưa có checkpoint wrap-up cũ. Đang kiểm tra source/build; chưa swap. Memory recall unavailable (index/model mismatch); dùng bằng chứng repo/log trực tiếp.

### Lane A wrap-up 2026-11-11T11:12:50 — DỪNG ở bước 1, chưa swap
- Source .cpp/.h/.vcxproj khớp staging production-build3. Đã lưu full git diff tại tmp/EAI-SEGAI-CUDA/laneA/wrapup/native.diff.
- KHÔNG thể xác nhận yêu cầu chỉ còn test hook compile-time: SegV2Core.cpp:2096–2098 vẫn có getenv EASYAI_SEGAI_DISABLE_INSTANCE_BUFFER_REUSE điều khiển A/B runtime, ngoài mọi guard test. Chuỗi control này cũng còn trong DLL build3; fault-injection marker không có trong DLL production.
- OrtCudaBackend.h khớp staging từng byte, không có SEGAI_SPLIT / split-profile IO-binding thử nghiệm. EASYAI_SEGAI_SPLIT_TIMING trong SegV2Core/SegAINativeExport là diagnostic cũ có từ HEAD, không phải header IO-binding mới bị sót.
- prepare.py dừng do assertion audit quá rộng (SEGAI_SPLIT cũng bắt tên diagnostic cũ); kiểm tra tiếp có mục tiêu phát hiện control A/B thật ở trên. Chưa chạy rebuild.
- Theo yêu cầu dừng khi một bước không đạt: không viết tổng kết tuyên bố clean source, không backup/swap, không chạy parity sau cài. App vẫn hash build2 2431C371D43081C923245396D288FEA47D1E8AC91100A3BB26A2523A8CCCFE60; không cần rollback. Chi tiết preflight.json cùng thư mục wrapup.
- Tiếp tục cần xử lý control A/B (guard compile-time hoặc bỏ), rồi tạo candidate mới và hash mới; không thể vừa tuyên bố source sạch control A/B vừa giữ nguyên build3 hash 6756FF21... có sẵn control đó.

### Lane A wrap-up 2026-11-11T11:13:02 — bổ sung preflight
- Đếm recursive: {"tmp/EAI-SEGAI-CUDA/production-build3/bin": 49, "EasyLabel/Lib/native/segai": 49}; mỗi bin có 47 file trực tiếp + Models (2 file) = 49 file, đúng yêu cầu. Đây không phải blocker số lượng.
- HEAD đã có EASYAI_SEGAI_SPLIT_TIMING: True. Blocker duy nhất đã xác nhận là control A/B runtime unguarded EASYAI_SEGAI_DISABLE_INSTANCE_BUFFER_REUSE. Không đổi source hoặc app.

### Lane A wrap-up 3 2026-11-11T11:43:17 — bước 1 source OK
- grep BeeNativeSegAI\*.cpp,*.h: không còn EASYAI_SEGAI_DISABLE_INSTANCE_BUFFER_REUSE; SEGAI_TEST_INSTANCE_CUDA_RUN_FAILURE chỉ ở SegV2Core.cpp:2104-2106 trong #ifdef (và test\cuda_run_failure.py). SegV2Core.cpp:2096-2097 = `p.Input(frame.image, true)` không getenv.
- Staging wrapup\root\BeeNativeSegAI chỉ lệch SegV2Core.cpp; đã copy (SHA256 7F68497C...8E37 khớp cả hai). Full git diff lưu tmp/EAI-SEGAI-CUDA/laneA/wrapup/native-v3.diff (50.179 B). Build đang chạy.


### Lane A wrap-up 3 2026-11-11T11:44:13 — bước 2 build OK
- wrapup\build.cmd rc=0, 0 Warning(s) 0 Error(s), 39 s (log tmp/EAI-SEGAI-CUDA/laneA/wrapup/root/build.log).
- DLL mới wrapup\root\artifacts\verification\Release\x64\bin\BeeNativeSegAI\BeeNativeSegAI.dll: 955.392 B, SHA256 40824FFF3CC9932628D20A62754D1292084ACA971352864530AA1556D9D31EBC. Chuỗi EASYAI_SEGAI_DISABLE_INSTANCE_BUFFER_REUSE và SEGAI_TEST_INSTANCE_CUDA_RUN_FAILURE KHÔNG có trong DLL; EASYAI_SEGAI_SPLIT_TIMING có (diagnostic từ HEAD).


### Lane A wrap-up 3 2026-11-11T11:49:44 — bước 3 candidate build4 OK
- tmp/EAI-SEGAI-CUDA/production-build4/bin = copy đệ quy production-build3/bin (49 file) + BeeNativeSegAI.dll mới (SHA256 40824FFF...1EBC). Đối chiếu hash với EasyLabel/Lib/native/segai: 49/49 file, chỉ BeeNativeSegAI.dll khác. App chưa đụng.


### Lane A wrap-up 3 2026-11-11T11:50:39 — bước 4 harness build4 PASS
- Script tmp/EAI-SEGAI-CUDA/laneA/wrapup/eval_build4.py (SEGAI_NATIVE_DIR=production-build4/bin, cùng counter_eval.evaluate, c2d v001 @0.25, quality 0, repeats 1); log wrapup/eval_build4.log; JSON production-build4/results/{cpu,cuda}/{tune,final,real}.json.
- tune40: CPU 33/40 615/32/27, CUDA 33/40 615/32/27. final100: CPU 85/100 1232/63/61, CUDA 85/100 1232/63/61. real: CPU 17/3/3, CUDA 17/3/3. Per-image CPU==CUDA (image,count,tp,fp,fn) trên 141 ảnh: True. Bằng build2.
- Thời gian p50 ms (cycle / native): tune CPU 17.56/16.88, CUDA 7.65/7.01; final CPU 17.93/17.13, CUDA 7.86/7.20; real CPU 24.47/23.82, CUDA 8.01/7.39. So build2 (CPU ~24.6-31, CUDA ~8.2-9) và build3 results (CPU 25.6-27.9, CUDA 8.5-8.8): CUDA không chậm hơn, CPU nhanh hơn. Xác nhận InferGetLastBackend=1 (CUDA) trên ảnh thật, count 20.


### Lane A wrap-up 3 2026-11-11T11:51:21 — bước 5 swap vào app XONG
- Backup EasyLabel/Lib/native/segai.bak-20261111-1150 (49 file, DLL cũ build2 SHA256 2431C371...FE60). Đã copy BeeNativeSegAI.dll build4 vào EasyLabel/Lib/native/segai: hash app == candidate 40824FFF3CC9932628D20A62754D1292084ACA971352864530AA1556D9D31EBC, 955.392 B, vẫn 49 file. Rollback nếu cần: copy lại DLL từ thư mục .bak trên.
- Harness chạy bằng DLL trong app (SEGAI_NATIVE_DIR=EasyLabel/Lib/native/segai, log wrapup/eval_app.log, JSON production-build4/results-app): tune40 33/40 615/32/27; final100 85/100 1232/63/61; real 17/3/3; CPU==CUDA per-image True cả 3 split. p50 ms CPU/CUDA: tune 18.07/7.82, final 17.32/7.75, real 20.47/8.55. RESULT PASS.
- Không commit/push/git add; không sửa C#; dataset không đổi. Chưa chạy: app EasyLabel thật, dotnet build, SegAI.Tests với DLL mới.

