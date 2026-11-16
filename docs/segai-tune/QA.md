# SegAI tune: bounded QA and reconciliation

Date: 2026-11-10. The independent review timed out while writing its report (shell quoting failure). Its bounded sanitized history contained the findings below; the coordinator recovered them and reconciled measured values against verified_completion.json. Do not label this an independently completed PASS.

## Findings

- HIGH: Real-world validity remains unproven. Every synthetic split derives from one camera photo; tuning has one distinct source object and final has only two. Source-ID isolation is not scene/camera/population isolation. The original real image supplied source crops and is diagnostic, not independent validation.
- MEDIUM: Confidence means are not cross-head calibrated. Semantic scores are maximum quantized component confidence /255; instance scores are native detection.score. Final raw means are baseline .777614 and tuned .517762. A higher numerical mean is not demonstrated and these different statistics are not interchangeable probabilities.
- MEDIUM: Fully correct frames decreased despite better aggregate accuracy: all_objects_matched is 35/100 baseline versus31/100 tuned. Exact counts45 versus76 can hide cancelling false positives and false negatives. Thus no claim that every accuracy criterion is non-decreasing is justified.
- MEDIUM: Resume caches return existing metrics by filename without checking current model/settings/manifest/repeats (run_experiment.py:20); model/dataset creation also returns on file existence. This is a reproducibility risk, not evidence that this run is stale. Freeze files, model hashes, manifest hashes, trial records and the latest raw bundles are retained. Do not change settings and reuse output filenames as if they were fresh measurements.
- LOW: IoU matching is confidence-descending one-to-one greedy polygon/OBB matching at.5, not global optimal assignment, pixel-mask IoU or COCO AP. No prediction/GT is counted twice. Convex hulls and minimum-area rectangles measure localization, not segmentation fidelity.

## Isolation review

The inspected code uses train120/val30/tune40/final100 with separate seed ranges. Train/val intentionally share source identities; tuning/final source groups, seeds and image hashes have pairwise assertions. Only train/val enter training and validation indexes are explicit. Final backgrounds are half known/half unseen. Main selection freezes from tuning before evaluating final. The predeclared instance extension likewise uses tuning for selection and freezes before its verification. It reuses the original final set; it is not a second independent holdout. Code supports no direct final-summary input to selection, but cannot prove external human predeclaration or absence of feedback.

## Reconciled latest results

The earlier review snapshot contained only semantic metrics, which are superseded for selected-model conclusions by verified_completion.json:

|Metric|Baseline|Tuned instance head|
|---|---|---|
|Precision IoU.5|.448008|.903605|
|Recall IoU.5|.356535|.891725|
|F1|.397071|.897626|
|Exact count /100|45|76|
|All objects matched /100|35|31|
|Cycle p50 ms|43.8887|28.3006|
|Cycle p95 ms|54.4129|33.4970|
|Real frame detected /20|3|17|
|Real TP / FP / FN|1 /2 /19|15 /2 /5|

Latest paired speedup1.550805x; native-only1.086195x. Neither meets2x. Earlier semantic-only1.955x belongs to a separate measurement; do not mix its slower baseline with the latest tuned timing. No cause of the inter-run timing variation is established. Five warmups, three serial repeats per synthetic frame and15 for the real diagnostic; disk decode/IoU excluded, mandatory extraction included. Instance phase timings are unavailable, not measured zeros.

Count/Couter comparison remains unavailable because the required runtime/API and Count settings are absent from this checkout. No substitute is called Couter. No full acceptance/production or completion tick is supported.
