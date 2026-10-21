# EasyAI LOGO project handoff to Claude Code

Date: 2026-10-15, Asia/Saigon. Task reference: **EAI-LOGO**. Recipient: Claude Code.

Continue the user's LOGO defect labelling, training and inference optimization task from the evidence below. The diagnostic harness is independently accepted, but the original objective is **not achieved**: agent labels fail the sample agreement gates, the single trained candidate fails validation, and both measured configurations exceed 200 ms. Keep the candidate inactive and agent proposals separate from accepted training labels.

All repository-relative paths below resolve under `E:\Code\EasyVision_Unisen - NoService`.

## User requirements and boundaries

- Source project: `E:\Training\TrainLOGONEW\LOGO_SegMent.easyai`; its sidecar is `LOGO_SegMent.easyai.data`, with source images and annotations under the same training directory.
- Detect curled/bulged/wrinkled/torn/scratched labels and dented bottles. The user explicitly chose **one class NG**, preserving existing class naming and sample geometry.
- Use **rotated boxes / oriented_rectangle**, following the user's human examples. Preserve originals and keep a separate evaluation group.
- The CT target is **the whole processing of one input image until its result**, below approximately 200 ms on this machine, not native inference alone.
- The user explicitly requires **harness verification, no User Computer / Computer Use / UI automation**.
- The original task authorizes relevant software fixes and Train/Check through the harness. This handoff does not add permission for a full solution build, production deployment, hardware, model activation, push, branch/worktree creation or unrelated maintenance.
- Use only the NoService workspace. The sibling `E:\Code\EasyVision_Unisen` is read-only and must not be built or run.
- Preserve concurrent dirty work. The final independent review observed 180 unrelated tracked dirty files; this task changed no tracked production source. No commit, push, deployment or tracking update was performed for EAI-LOGO.
- All agent-authored C# must remain C# 7.3. The standalone harness targets .NET 9 Windows but explicitly sets `LangVersion=7.3`.

This document is the requested handoff, not a newly approved execution plan. Retain actual current-task user authorization and repository stop conditions; do not import authorization from another dataset's contract or fabricate Work approval fields. EAI-LOGO is a local task reference, not a newly registered map ID. The old resume point in `.codex-session.md` concerns V175 and does not authorize unrelated work here.

## Read first

1. `AGENTS.md`, `CLAUDE.md`, and the backend-appropriate `.agents/skills/eroute` instructions. Read only tails of large history/session files.
2. `artifacts/EAI-LOGO/seed-train-r3/check-launch-evidence.json`, using a filter rather than printing the whole JSON. Relevant keys: `final_outcome`, `final_independent_review`, `code_change_audit`, `cpu_check_summary`, `gpu_requested_check_summary`, `configuration_output_parity`, `claude_resume_result`, `original_annotation_change`.
3. Active related contracts: `.claude/task-contract.EAI-ALL.toml`, `.claude/task-contract.EAI-LABEL.toml`, `.claude/task-contract.EAI-POS.toml`. EAI-LABEL targets a different dataset, `E:\EAI_TEST`; its permissions and goals do not automatically apply to LOGO. EAI-POS has a stale top-level note saying the plan is not approved while its structured approval names `EAI-POS-r1-plan-v1`; reconcile structured fields and current task scope instead of relying on that note.
4. Relevant plans referenced by those contracts, if present: `tmp/EAI-PROJ/plan-v1.md`, `tmp/EAI-CACHE/plan-v1.md`, `tmp/EAI-AGENT/plan-v1.md`, `tmp/EAI-CAM/plan-v1.md`, and their archived contracts under `.claude/archive/EAI-ALL/`. Do not recreate missing plans from memory or take unrelated stream work.
5. For Segment AI architecture, `docs/AiPlan/SegAI-V2-BeeLearn-Plan.md` revision 6 supersedes `ToolSegmentAI-Plan.md`. These architecture documents do not substitute for inspecting the current EasyLabel runtime and EAI-POS contracts.

## Dataset and frozen inputs

The audit found 143 PNG images, each 5472 x 3648, approximately 4 GiB total. There were 18 uniquely annotated human images with 27 NG oriented boxes. The 36 annotation JSON files include duplicate `images/*.json` and `json/*.json` representations; they are not 36 independent labelled images.

Canonical annotations were taken from `images/*.json`. For the conflicting `NhanL1 (8)` copies, that version matches the current sidecar manual cache. Preserve both source representations rather than silently reconciling originals.

Important limitations:

- Human samples may be partial rather than exhaustive. No confirmed OK images were supplied. Missing labels and zero agent proposals must never be converted into verified OK examples.
- No exact duplicate images were found, but similar layouts exist. Filename prefixes are only capture-group proxies; physical-part independence is unproven.
- `NhanL1 (13)` has a human box with the short edge first. The whole image was excluded from the diagnostic training set; no human corners were rewritten. Most other human examples use the long edge first.
- The user intentionally edited/saved original `images/NHAN_PHONG_CHONG (20).json` at 11:40:24 local during the work. The user confirmed this in chat. Preserve that newer annotation. Its approximately 4 px geometry change is not an unexplained agent write; the earlier frozen snapshot remains recorded. It is outside the train/timing inputs.

| Input or evidence | Purpose |
| --- | --- |
| `tmp/EAI-LOGO/dataset-audit.json` | Original annotation hashes and geometry |
| `tmp/EAI-LOGO/manifest.json` | Image identities and original train/validation/held-out grouping |
| `tmp/EAI-LOGO/project/` | Frozen working copy, 143 images and compact canonical annotations |
| `tmp/EAI-LOGO/seed-diagnostic-manifest.json` | Exact human-only diagnostic train allowlist, exclusions and gates |
| `tmp/EAI-LOGO/seed-source-identities.json` | Canonical image/annotation identities for training |
| `tmp/EAI-LOGO/timing-inputs.json` | Exact 20 full-resolution, non-held-out timing fixtures |
| `tmp/EAI-LOGO/timing-source-identities.json` | Timing fixture provenance and original identities |
| `artifacts/EAI-LOGO/timing-project-r1/input-identities.json` | Verified timing copy identities |
| `artifacts/EAI-LOGO/claude-comparison-inputs.json` | Frozen four-image agent comparison, prompt and reference hashes |

The diagnostic training clone contains exactly **11 train images / 15 boxes and 1 validation image / 1 box**. Validation is `NhanL2 (1).png`. The entire `NHAN_PHONG_CHONG` group remains outside training and timing. Five human-held-out examples remain reserved. Do not use them for repeated parameter tuning. The 20-image timing set is a latency fixture, not an accuracy test.

Frozen SHA-256 identities:

```text
seed-diagnostic-manifest.json  b2275b9d0c6c29434296b7d441c11b93a8d6b7c98baf8a68df60ed05e1e66095
seed-source-identities.json    f240c96e3ee3cf15d5284b441c5de20df713130faa66ef9fef51c377ba18cef2
timing-inputs.json             e843d47a570d30c96bbad7e943b2132cc5ea612de103e470b68639b35f67237e
timing-source-identities.json  845d74cb009d3dfac38203f23c78e225a05becf83473f2b22ac345ae236ff694
```

## Agent labelling results

The harness invokes the application's real `AgentRunner` and MCP tools through the existing compiled test `EasyLabel.SegAI.Tests.BootstrapBaselineTests.MeasureAgentPromptBatchLabelling`. Target annotations are hidden from the agent and proposals are persisted only in an isolated output copy. These are draft labels, not accepted original annotations.

| Run | Actual result | Acceptance |
| --- | --- | --- |
| `pilot-r1` | Codex, 8 images, about 365.6 s, 10 proposals. Six images were previously unlabelled; four of those received 7 draft boxes, two received none. Other images were blind sample comparisons. | Boxes often covered only the defect core. No bulk expansion or training acceptance. |
| `pilot-r2` | Cancelled after detecting a coordinator reference-coordinate formula error. | Invalid/incomplete experiment; do not use. |
| `pilot-r3` | Codex, two blind samples, 3 proposals, about 122.2 s. Corrected original-coordinate and full-defect-extent prompt. | Only 1/3 human box extent gates passed. |
| `claude-pilot-r1` | Default Claude backend stopped on a session quota response before any successful label. | No quality result. |
| `claude-pilot-r2` | One resumption after the advertised 12:20 local reset. Four of four calls succeeded, 46.21 s, reported cost USD 0.15294, 2 draft proposals. | Only 1/6 known human box extent gates passed; overall geometry gate failed. |

The unchanged minimum agreement gate is **rotated IoU >= 0.50 and human-area coverage >= 0.70 for every known human box**. Generated boxes also need valid NG/oriented_rectangle geometry, finite in-frame points and long-edge-first order. Human labels themselves must remain unchanged. This agreement is not exhaustive recall or precision because human annotations may be incomplete.

Claude's final four-image result:

- `NhanL1 (10)`: one proposal in a different region, IoU 0 against the authoritative sample; also short-edge-first.
- `NhanL1 (12)`: barcode-adjacent box passes, IoU 0.7740 / coverage 0.7767; the broad logo defect is missed.
- `NhanL1 (2)`: no proposal for its one known box.
- `NhanL1 (5)`: no proposals for its two known boxes.

Evidence: `artifacts/EAI-LOGO/claude-pilot-r2/sample-box-agreement.json`, `comparison-r7.jpg`, `project/*.json`, `per-image.csv`, `summary.json`, and `artifacts/EAI-LOGO/claude-pilot-r2-review/crops-01.jpg`. Green in `comparison-r7.jpg` is human, red is Claude. Geometry really is persisted in the output copy; an early verifier CSV-only statement that geometry was unavailable was corrected. Originals were not merged or overwritten.

Only `tmp/EAI-LOGO/references-r3/` is the corrected full-damage reference set. **Do not use references-r2**: its crop-scale instruction was wrong. Crop metadata uses `scaleX = outputWidth / cropWidth`; convert crop pixels with `originalX = cropOriginX + cropX / scaleX`, likewise Y. The helper filename `prepare_references_r2.py` is historical; its corrected output is references-r3.

Both backends failed the quality gate. The final r7 plan therefore stopped paid annotation expansion. Do not simply rerun the same prompt or treat successful CLI completion as annotation acceptance. The USD 2 experimental threshold was soft, enforced between calls, not a guaranteed hard ceiling. Codex's zero cost/token telemetry is unknown billing, not evidence of free execution. The quota failure was subsequently overcome; quota is not the final blocker.

## Trained diagnostic candidate

Candidate:

```text
artifacts/EAI-LOGO/seed-train-r3/training-project/.easyai/models/v001.segai
SHA256 AC6759479380DC378FA171B43AA73DCF06FC02CF2F1619B4D97736DDBDDF8CDE
Size   11,936,060 bytes
```

The one human-only candidate trained successfully in 233.75 s, approximately 237.25 s wall time. Artifact resource sampling recorded 1,094.4 MB peak working set. No agent proposals were used for training.

Configuration: V2 Detect, input side 960, semantic epochs 60, instance epochs 300, fixed backbone, learning rate 0.015, angle symmetry k=2, instance quality Performance. The symmetry override is persisted through `SegProjectStore.SaveAngleSymmetryOverride(2)` / `.easyai/advanced.json`, not a `SegTrainingOptions` property.

The embedded training report exists even though there is no separate report artifact in the run folder. It was decoded read-only into `check-launch-evidence.json`. It confirms `OutputShape=oriented_rectangle`, k=2 in the report note, and the configuration above. Actual Check loaded the model and required an instance head.

Validation on the single held-out validation image gives **mAP50=0, TP=0, FP=3, FN=1**. This is a diagnostic candidate, not an accepted detector. `registry.json` has `active=null`, verified at handoff. Do not use the report's `MedianMilliseconds=96.2484` as full-image CT; the real service measurements below are much higher.

## Full-image latency evidence

Machine: Intel i7-1360P, 16 logical processors, approximately 15.7 GiB RAM, RTX A500 Laptop 4 GiB and Intel Iris Xe. RTX driver observed during discovery: 596.58; recheck if needed rather than assuming it stays unchanged.

Each configuration used the same candidate, 20 frozen non-held-out PNG images, five repetitions per image, threshold 0.3 and Performance instance quality. That gives 100 AutoLabel and 100 Inspect service calls per configuration, 400 calls total. Model loading is timed separately. The first call is separate from 80 subsequent uncached measurements per service/configuration. All 20 decoded at 5472 x 3648; service errors and cache hits were zero.

The stopwatch includes the real file-input service call and result construction; AutoLabel also includes score filtering. Cache maintenance and waiting for prior disk writes are outside the stopwatch. The production cache enqueue overhead stays inside the call. Do not compare this with native-only timings or a predecoded-image loop without reporting the changed boundary.

| Requested configuration | Service | Warm p50 ms | Warm p95 ms | Warm maximum ms |
| --- | --- | ---: | ---: | ---: |
| GPU disabled / CPU requested | AutoLabel rotated boxes | 684.4490 | 948.1451 | 1336.0841 |
| GPU disabled / CPU requested | Inspect | 862.3573 | 1165.4313 | 1447.2771 |
| GPU enabled / Intel GPU requested | AutoLabel rotated boxes | 468.5463 | 669.2936 | 784.2589 |
| GPU enabled / Intel GPU requested | Inspect | 637.6951 | 838.5038 | 1065.2088 |

Both configurations fail the 200 ms gate. The GPU-requested first AutoLabel call reached 9276 ms and first Inspect reached 5009 ms. The faster requested setting is only a diagnostic observation: actual instance backend is **unverified**, and CPU-then-GPU run order, caches and thermal state may confound the relative speed.

Output parity is close but not exact. AutoLabel had 90/100 identical rows, all empty; the two positive fixtures account for the 10 differing rows. Their box IoUs across configurations were 0.9960 and 0.9947. Inspect verdicts matched on all 100 rows, but region counts differed on 10 rows and maximum score delta was 0.0156863. Sparse outputs from an inaccurate model can make postprocessing time optimistic.

Raw outputs: `artifacts/EAI-LOGO/seed-check-r3/` and `artifacts/EAI-LOGO/seed-check-gpu-r1/`, each with `check-result.json`, `per-image.csv`, `harness-run.log`. Use `kind=warm_uncached` to compute Inspect warm statistics; its top-level aggregate includes first calls.

## Runtime facts and fixed harness failures

- AutoLabel uses `SegAiSession.PredictInstancesForAutoLabel`; Inspect still follows the semantic mask path. Do not transfer performance, geometry or backend claims between them.
- `ToInstances` derives width/angle from the first point edge; changing Direction alone does not fix point ordering.
- V2 GPU selection supports an Intel OpenVINO GPU. It does not select the RTX A500. `Inference.EnableGpu` is a request, not execution proof. No supported inference CPU-thread setter was found; `backboneThreads` concerns training fine-tuning.
- AutoLabel/Inspect result types drop backend/phase information. The session's predictor/inferer fields are private. More importantly, the native instance path does not update last-backend/phase telemetry; reading those getters would be stale even through the correct handle. Do not add reflection, duplicate predictions or infer actual GPU use from these values.
- Source pointers: `EasyLabel/src/EasyLabel.SegAI/AutoLabel/AutoLabelService.cs`, `Inspection/InspectService.cs`, `Inspection/SegAiPredictor.cs`, `Engine/SegAiInferer.cs`, `SegAiSession.cs`; native `BeeNativeSegAI/SegInferCore.cpp` and `SegV2Core.cpp`.
- Standalone build failures from path quoting/trailing separators and C# CS0173/CS0246 were fixed. The final build is 0 errors / 0 warnings.
- An early BackboneMissing block was due to the harness preflight omitting the explicit backbone override, not missing trained model output. The corrected Check and Start calls use the same explicit backbone.
- OpenCV failed initially because native dependencies were missing/shadowed by an older 4.11 `OpenCvSharpExtern.dll` in the extra runtime directory. The harness copies the app-matched 4.13 extern DLL into its output, absolutely preloads it before BeeNative/OpenCV, holds the handle through the mode and frees it once after resources dispose. Do not overwrite production binaries to fix this.
- `seed-check-r1` failed before inference because the coordinator supplied comma-separated image names. **run-check --test uses semicolons**. The run-label image selector uses commas/arrays; do not confuse them.
- `seed-check-r2` hit an IOException deleting a cache directory while service disk writes were pending. The final harness calls both services' `FlushDiskWrites()` before every task-cache deletion and in finally before session disposal. Caches remain enabled and cache-hit rows are rejected. Do not suppress this race with blind retries or disable caching silently.

Pinned runtime identities:

```text
Native DLL: artifacts/verification/Release/x64/bin/BeeNativeSegAI/BeeNativeSegAI.dll
SHA256 7bf4f23e6224bf9ef54d10fc925f9e5194b358f16f667a0e2cd496a79bfa45c0
Extra runtime: artifacts/EAI-LABEL/native-run
Backbone: Models/DefectV2/mobilenetv2-12.onnx
SHA256 c0c3f76d93fa3fd6580652a45618618a220fced18babf65774ed169de0432ad5
Matched OpenCvSharpExtern.dll SHA256
41E6A418637B7992814A668272E3197306F75C01B151DDB24AC2DE183FB9C3F3
EasyLabel.SegAI.dll SHA256
7aeedc948ab000d0ff04a305cb1278275ea71030d5898fef193714328fe79db7
```

These are isolated harness runtime measurements, not validation of deployed live binaries.

## Reproduction commands

These are reference commands, not instructions to spend money or repeat failed experiments immediately. Wrappers refuse existing batch output directories, so a deliberate rerun needs a new unique batch name. All logs, temp paths and build products stay inside the workspace.

```powershell
Set-Location 'E:\Code\EasyVision_Unisen - NoService'

# Last successful standalone build and runtime preflight; do not rebuild the solution.
& '.\tmp\EAI-LOGO\run-check.ps1' -Mode preflight -Batch 'preflight-r10' -BuildHarness

# Last successful one-candidate training run; no immediate retraining is justified.
& '.\tmp\EAI-LOGO\run-check.ps1' -Mode train -Batch 'seed-train-r3' `
    -Project 'E:\Code\EasyVision_Unisen - NoService\tmp\EAI-LOGO\project'

$timing = Get-Content 'tmp/EAI-LOGO/timing-inputs.json' -Raw | ConvertFrom-Json
$names = [string]::Join(';', @($timing.images.rel))
$project = 'E:\Code\EasyVision_Unisen - NoService\artifacts\EAI-LOGO\timing-project-r1'
$model = 'E:\Code\EasyVision_Unisen - NoService\artifacts\EAI-LOGO\seed-train-r3\training-project\.easyai\models\v001.segai'

# Last completed requested configurations.
& '.\tmp\EAI-LOGO\run-check.ps1' -Mode check -Batch 'seed-check-r3' `
    -Project $project -Model $model -Test $names -Repetitions 5
& '.\tmp\EAI-LOGO\run-check.ps1' -Mode check -Batch 'seed-check-gpu-r1' `
    -Project $project -Model $model -Test $names -Repetitions 5 -RequestGpu

# Last completed real application-agent comparison, with frozen references and prompt.
$comparison = Get-Content 'artifacts/EAI-LOGO/claude-comparison-inputs.json' -Raw | ConvertFrom-Json
& '.\tmp\EAI-LOGO\run-label.ps1' -Batch 'claude-pilot-r2' `
    -Images @($comparison.images.rel) -ReferencePaths @($comparison.references.path) `
    -MaxUsd 2 -Tool claude
```

## Recommended continuation for Claude Code

The completed bounded revisions r4 through r7 authorized one human-only diagnostic model, cache/runtime harness repairs, two matched timing configurations and the one resumed Claude comparison. Their failures do not authorize endless prompt retries, parameter sweeps or automatic bulk labelling.

1. Verify the pinned model/runtime/input identities and current Git state. Read existing evidence first; do not repeat successful runs merely to rediscover results. Keep human labels, draft labels and prediction overlays distinct.
2. Create a new bounded continuation plan under the original user's goal and actual standing authorization. Follow the adaptive agent tree for production changes, with one writer and independent review. Document any required scope change rather than pretending r7 passed.
3. Diagnose why known human defects are missed. Review the green/red comparison, full-resolution crops, class/shape instructions, actual agent-tool calls, coordinate mappings and accepted sample completeness. Re-establish a small, complete gold training/calibration set across defect families, with confirmed OK material and capture-group separation. Preserve the final holdout. A filename is not defect evidence.
4. If changing agent behaviour or code, verify the real AgentRunner/MCP path on the frozen blind examples before expansion. Keep the existing per-box extent gates and add evidence for any revised strategy. Do not train on failed proposals or treat no proposals as OK.
5. Investigate full-service latency with a quality-accepted candidate and truthful timing boundaries. Separate PNG decode, preprocessing, native semantic/instance work, mask transfer, managed region analysis and result construction. Existing instance phase telemetry cannot support that decomposition. Any telemetry fix or native optimization needs its own scoped source plan and relevant authorized verification. Do not claim RTX acceleration or change input resolution/threshold just to obtain a fast number without quality evidence.
6. Train and compare further candidates only after data readiness is established. Report detection quality and full-image cold/warm p50/p95/max together; optimize without sacrificing required defect coverage. Candidate export/load parity and native production-path checks precede any activation decision.

There is no accepted “best configuration” yet. The next useful outcome is a justified, reviewable data/agent strategy and a measured optimization plan, not promotion of this candidate. The user prefers continuing normal authorized work without repetitive permission questions; ask only for a real repository stop condition or necessary scope/authorization decision.

## Code change audit and verification handoff

The detailed prior-task audit is `code_change_audit` in the existing launch evidence. It includes source hashes, exact commands/results, fixed root errors, C# assumptions, residual risks and an exact 751-file task artifact inventory at completion. This newly requested handoff document is additional to that historical inventory.

Task-owned source files from the diagnostic work:

```text
tmp/EAI-LOGO/audit_dataset.py
tmp/EAI-LOGO/prepare_copy.py
tmp/EAI-LOGO/audit_near_duplicates.py
tmp/EAI-LOGO/review_proposals.py
tmp/EAI-LOGO/prepare_references_r2.py
tmp/EAI-LOGO/compare_sample_boxes.py
tmp/EAI-LOGO/run-check.ps1
tmp/EAI-LOGO/run-label.ps1
tmp/EAI-LOGO/harness/EaiLogoHarness.csproj
tmp/EAI-LOGO/harness/Program.cs
```

Final verification: six Python AST checks, PowerShell parsing, conflict/whitespace checks, scoped declaration review and C# 7.3 compilation passed. Standalone `preflight-r10` build had 0 errors / 0 warnings; BeeNative API 1, OpenCV full-resolution decode and backbone readiness passed. Final source hashes matched the static audit. Independent review accepted the harness/evidence with no remaining source finding, while the original goal remains blocked on empirical quality and CT gates.

```text
Final Program.cs SHA256
3E5C04EF641B2A4C667B661C7DB8EE4F59E7E46A314517B9FA1A94A9608923C7
Final harness DLL SHA256
30E2EFD51C240217A3AA97C30A68E5813C7ADDBBC9210F017DDD160A176A6C16
preflight-r10 MSBuild binlog SHA256
2772BD9FE29443451E74EF23EE0C54DC4301CCBD3E781ADC66CAF7A14DDD2D49
```

No full solution/production build, live UI verification, hardware run, production activation, deployment or push was performed. The current user request only writes this handoff: no new model run, paid agent call or executable verification is part of the documentation handoff.
