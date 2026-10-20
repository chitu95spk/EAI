# EAI-CLS independent review (2026-10-10), read-only, two Opus reviewers. Nothing built or run.

Patches reviewed: p2c-p2h.patch (EasyLabel side), p3-s1-s2 / s3d / s4 / s5 .patch (EasyVision side).

## A. P2c-P2h (EasyLabel): FIX_REQUIRED
1. HIGH SegAiSession.cs:560 UseClassPrediction returns false when _map.Count <= 2 -> named 1-2 class tables (renamed OK->Dent, swapped kinds, single class) predict via plain Predict, skip SameClasses (lines 1025, 1068). N-class model in a default NG/OK project is also predicted silently. Fix: use class prediction + SameClasses whenever HasNamedTable unless table == SegAiClassTable.Legacy(); reject model with >2 classes when project has no named table.
2. MED ClassTableViewModel.cs:135-154 + SegAiSession.cs:302 SaveClassTable: rename does not relabel LabelMe shapes; LoadState (1388) drops unresolved labels -> training drops regions / "No class labels in image". Fix: relabel all JSONs by position or block rename of a class with shapes.
3. LOW SegAiTrainingEvaluation.cs:288 metrics include disabled classes.
4. LOW SegAiSession.cs:537 RequireMatchingClasses rejects legacy dataset (null table) when project has NG/OK-equivalent named table.
5. LOW SegProjectStore.cs:142-154 invalid segClasses silently falls back to NG/OK; kind parse case-sensitive.

## B. P3-S1,S2,S3d,S4,S5 (EasyVision): ACCEPT-WITH-CARRYOVERS (2-class legacy unchanged)
1. MED hot path: NativeSegAI.cs:617-618 / SegmentAI.Inspection.cs ~478-480 PredictNamedClasses allocates 4 full-image arrays per frame (LOH) + N Compare/Analyze passes; 2-core 8 GB PC. Fix: reuse buffers, run on ROI submat.
2. MED v4 named model importable into a project with no table (adoption only in AdoptRetrainClassTable, TrainingEvaluation.cs:90) -> Check shows named classes, Train silently produces 2-class. Fix: adopt on import or warn.
3. MED PredictNamedClasses forces CompareNormal=false (mutates saved setting); "OK Counter" recipes miscount. Fix: do not mutate; block with message or count Good class.
4. LOW NamedClassIdAt (SegmentAI.Inspection.cs ~94-99) lacks index < labels.Length check (UI race after image size change).
5. LOW corrupt classTableBytes: GetClassTable null but NormalizeMask named=true (SegmentAI.cs:869).
6. LOW DefectLabelCanvas.cs:182 RebuildClassButtons leaks a Font per button per image load.
7. LOW N-class PredictNamedClasses never sets lastPhaseTimings.
FormSegmentTraining 2-class painter: constructor has no caller, cannot corrupt named masks.

## Still owed (not code defects)
UI smoke 1024x768/125% (class picker, editor); save/reload with adopted table; N-class latency on the 2-core target; P4 hardware; P5 on separate OK.
