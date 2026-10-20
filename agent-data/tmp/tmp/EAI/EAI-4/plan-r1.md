# EAI-4 plan r1 (draft, awaiting user approval) - Step 3 Train: train, registry, candidate vs active, "Use this model"

Master plan: tmp/EAI/plan.md r4 sections 6 (row 3), 8. Depends on EAI-3 (417ff55d8). Facts from explorer (2026-10-08):
- `SegAiTrainingPipeline.Run(SegAiTrainJob)` returns `SegAiTrainResult` {Success, Cancelled, CandidatePath, Entry, Report, Message, Elapsed, ValidationSummary, RunFolder}.
  Progress = Action<int>, live epochs = Action<SegEpochMetric> (Epoch, Seconds, TrainLoss, ValidationLoss, LearningRate, TrainIou, ValidationIou), cancel = CancellationToken.
- Needs >=1 Train image with an NG/OK label; Val images are user-tagged (Space), not a percentage split. Outputs: embedded report in the .segai + `.easyai/runs/<utc>/`.
- Registry: `StageCandidate` registers a TRAINED entry, never active; `SetActiveModel(file)` activates (EAI-3 added IMPORTED entries).
- `SegTrainingReport`: Map50, Map5095, PixelIou/Precision/Recall, MeanBoxAreaRatio, EffectiveForeground, PriorBalanced, per-class AP50/Precision/Recall, worst-first image results with Status. `SegAiModelFile.ReadReportJson()` reads it from any model.
- NOT present: candidate-vs-active comparison type, Step 3 UI, StepTrain string, any chart control (ScottPlot.WPF 5 was approved by the user on 2026-10-05 but is not referenced yet). Defaults to respect: V2LegacyPrior=false (prior-balanced), Foreground 0.5, Epochs 200, InputSize 640.
- Existing train tests are opt-in (EASYAI_RUN_TRAIN=1) and use a fake backend for logic; the runtime of a real run is not documented (must be measured).

## 1. Scope (two commits)
### Commit A - backend (UI-free, tested)
1. `SegAiTrainingService` (EasyLabel.SegAI/Training): validates prerequisites (>=1 Train image with regions, >=1 Val image; reasons returned as a typed enum so the UI can disable the button without help text), runs the pipeline on a background Task with IProgress<SegAiTrainProgress> {Percent, EpochMetric?} and a CancellationToken, and returns SegAiTrainResult. One run at a time per session; inference (auto-label) is refused while a run is active.
2. `SegAiModelComparison` (pure, no native): builds rows from two SegTrainingReport (candidate, active): mAP50, mAP50-95, pixel IoU/precision/recall, per-class AP50, mean box-area ratio, image-status counts (OK/FalseAlarm/Partial/Missed), val image counts; delta and better/worse/equal per metric; flags `DifferentValidationSet` (val count or dataset fingerprint differ), `BoxOverextension` (Map50 high but Map5095 low, or MeanBoxAreaRatio > 1.3: the V2 prior-shift symptom), `NoActiveModel`.
3. `SegAiSession` read helpers (additive): candidate list with parsed report summary (registry entries + SegAiModelFile.ReadReportJson, cached) and run history from `.easyai/runs/*`.
4. Tests: comparison math and flags on constructed reports; service prerequisites, cancel and progress through the existing fake backend; opt-in real native smoke (EASYAI_RUN_TRAIN=1, tiny fixture, few epochs) with the measured duration recorded in the evidence.

### Commit B - WPF Step 3 panel
1. Register `WorkflowStep.Train` (after Label, when a project is open); `TrainStepView/ViewModel` hosted like Step 2; StepTrain strings vi/en.
2. Panel (WPF-UI controls, no help text): dataset summary line (Train/Val counts, same summary as Step 1); options Epochs and Input size only (the rest stays at the pipeline defaults until the Advanced drawer, EAI-8); Primary button "Start training" (disabled, with the reason as tooltip from the typed prerequisite reason); Stop; progress bar + epoch/ETA text; live chart (TrainLoss, ValidationLoss, ValidationIou vs epoch) with ScottPlot.WPF 5 (see Q3).
3. Candidate list from the registry (file, state TRAINED/IMPORTED/ACTIVE, created, mAP50, elapsed); selecting one shows the comparison table against the active model (deltas coloured, warnings as InfoBar rows from the flags); "Use this model" activates the selected candidate through SetActiveModel (reversible, the previous model stays in the registry, so no confirmation).
4. While a run is active, Step 2 auto-label/Accept and Step 1 import are disabled with a real CanExecute; closing the app during a run asks to stop (CancellationToken) and waits for the pipeline to unwind.
5. UI smoke (UI Automation harness as in EAI-3, en + vi, 100% DPI): tiny labelled project, real 2-epoch / 160 px training, chart updates, compare with an imported model, Use this model, Stop mid-run.

## 2. Out of scope
Advanced options drawer (EAI-8), inspection/eval list (EAI-5), camera (EAI-6), statistics (EAI-7), INT8, export parity beyond the registry file, hardware, any EasyVision file, EasyLabel.App.

## 3. Verification
Build 0 errors; existing tests stay green (Core 86, SegAI 142 + 2 skip) plus new tests; comparison unit tests; opt-in real train run on a tiny fixture (duration and result saved under artifacts/EAI/EAI-4/); UI smoke screenshots vi/en including Stop and Use-this-model. 150% DPI stays owed unless the user sets scaling.

## 4. Risks
- Real training time on this PC (the production target is a 2-core class CPU): unmeasured. The tiny fixture run is bounded by Epochs=2, InputSize=160 and the evidence records the seconds.
- Native trainer and inference in one process: the service blocks auto-label while training (documented behaviour, not a race fix).
- ScottPlot.WPF 5 is a NuGet dependency (stop-and-ask rule 0.3): approved in plan decision 2 but needs a network restore; if unavailable, the chart is the one item that stops and returns for a scope decision (no hand-drawn substitute).
- Comparing reports from different validation sets is misleading: surfaced by the DifferentValidationSet flag, not hidden.

## 5. Open questions (defaults if unanswered)
Q1 "Retrain" = start from the active model's weights? A warm start is not known to exist: default = only "Train new" in EAI-4 (a Retrain button appears only if the pipeline really supports it). Say if warm start is required.
Q2 Start gate: require >=1 Train and >=1 Val image (default) vs Train only.
Q3 Chart: add ScottPlot.WPF 5 (default, approved 2026-10-05) vs no live chart (progress bar + epoch table only).
Q4 Comparison basis: each model's embedded report on its own validation set with a warning when the sets differ (default) vs re-evaluating both models on the current Val set (true like-for-like, needs an evaluation API and extra inference; would move to EAI-5/7).
Q5 Closing the app during training: ask to stop and wait (default); keeping the run alive is not possible (in-process).

Estimate: about 1.2M tokens (explorer done, architect pass, A, B, review, smoke, one real native run).
