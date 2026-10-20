# T6 — OnlineBench synthetic-frame preparation

## Files changed

- `EasyLabel/tests/EasyLabel.OnlineBench/Options.cs`
- `EasyLabel/tests/EasyLabel.OnlineBench/Frames.cs`
- `EasyLabel/tests/EasyLabel.OnlineBench/Program.cs`
- `EasyLabel/tests/EasyLabel.OnlineBench/README.md`

The pre-existing OnlineBench dirty baseline remains recorded in `tmp/EAI-ONL-MODE/codex/baseline-onlinebench-20261018.patch`; this work extends only the four allowed files and does not alter the other three baseline files.

## Results

| Command | Log / artifact | Result |
| --- | --- | --- |
| Initial `dotnet build EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64 -bl:artifacts/EAI-ONL-MODE/t6/onlinebench-build.binlog` | `tmp/EAI-ONL-MODE/t6/build.log`; `artifacts/EAI-ONL-MODE/t6/onlinebench-build.binlog` | Superseded by reviewer-cycle verification |
| Reviewer-cycle `dotnet build EasyLabel/tests/EasyLabel.OnlineBench/EasyLabel.OnlineBench.csproj -c Release -p:Platform=x64 -bl:artifacts/EAI-ONL-MODE/t6-fix1/onlinebench-build.binlog` | `tmp/EAI-ONL-MODE/t6-fix1/build.log`; `artifacts/EAI-ONL-MODE/t6-fix1/onlinebench-build.binlog` | PASS — 0 errors, 3 warnings |
| built DLL `--help` | `tmp/EAI-ONL-MODE/t6/help.log` | PASS — exits before repository/model/dataset discovery |
| built DLL `--synthetic-smoke` | `tmp/EAI-ONL-MODE/t6/synthetic-smoke.log` | PASS — `5472x3648`, raw `19961856`, encoded PNG `569449`, SHA256 `98E415705D466D8AA92B4D0A7D14C132AB9CFBC571E83AE79A8A81CAEBC55369` |
| reviewer-cycle built DLL `--help` and `--synthetic-smoke` | `tmp/EAI-ONL-MODE/t6-fix1/help.log`; `tmp/EAI-ONL-MODE/t6-fix1/synthetic-smoke.log` | PASS — same deterministic output |
| built DLL `--synthetic-frame` | `tmp/EAI-ONL-MODE/t6/synthetic-requires-model.log` | guarded exit 2: explicit `--model` required before native/model work |
| built DLL `--synthetic-frame --model tmp/EAI-ONL-MODE/missing.segai --accuracy` | `tmp/EAI-ONL-MODE/t6/synthetic-forbids-accuracy.log` | guarded exit 2: `--accuracy` forbidden before model work |
| scoped trailing-whitespace and conflict-marker scan | console | PASS — none found |

## Audit

- **Task ID:** T6.
- **Approved scope and preserved invariants:** four allowed OnlineBench files only; no packages, model, dataset, source-project, or production changes. Existing C# 12 benchmark configuration remains unchanged. No training, full inference, or latency benchmark was run.
- **Behavior added:** `--help` early exit; deterministic 5472x3648 Mono8 raw frame with lossless managed PNG encode/decode equality check; a future `--synthetic-frame` inference mode that requires an explicit SegAI model, rejects `--accuracy`, `--selftest`, `--onnx`, and `--dataset`, skips ONNX discovery, and cannot reach `TrainMinimal`.
- **Reviewer-cycle correction:** the supervisor now forwards `--synthetic-frame` to every worker before frame creation, so worker parsing preserves the guarded synthetic mode instead of silently falling back to dataset frames.
- **C# compiler assumption:** this existing project declares `LangVersion` 12.0; no project language setting changed. Newly authored code was revised to C# 7.3 syntax (explicit constructors, ordinary using blocks, and no new record-with expressions). Compilation used the existing project configuration; a whole-project C# 7.3 compile was not performed because the pre-existing benchmark uses newer syntax. No dependency was added.
- **First distinct compiler/root errors:** none found.
- **Known warnings:** the initial build emitted 69 warnings from the shared dependency/benchmark build, including nullable warnings and two OpenCvSharp analyzer warnings. Their pre-existing provenance was not independently established; no build error occurred.
- **Residual risks:** smoke proves deterministic raw/managed-PNG equality only, not independent OpenCV decoder compatibility, SegAI model loading, inference correctness, throughput, or p95. The existing generic `Measurements.Write` report introduction still describes its 2592x1944 dataset path and was outside this four-file scope. The actual synthetic inference branch was intentionally not run.

Independent reviewer: ACCEPT for the four-file preparation scope; inference, independent PNG interoperability and latency remain unverified.
