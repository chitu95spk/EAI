# EAI-LABEL status
- S0 baseline DONE (artifacts/EAI-LABEL/s0-baseline): 84 images, 1938.99 s wall (32.3 min), 7.827 USD, median 20.5 s and 0.065 USD per image, all Success, 0 cached. 51 NG-named images 5 shapes each, 33 OK-named 17-45 shapes. Quality vs 18 GT (first 18 by name, 5 'OK' oriented_rectangle each): TP 90 FP 0 FN 0, mean IoU 0.975. Caveat: the GT may itself derive from an agent run; the user called it hand-made.
- Targets from baseline: <= 485 s and <= 3.91 USD for all 84.
- S1 offline replay test being written (BootstrapReplayTests). Backbone V2 present at Models/DefectV2/mobilenetv2-12.onnx.
- Commit S0 done. Next: build+run S1, decide go/no-go vs break-even, then S2-S4.
- S1 DONE (artifacts/EAI-LABEL/s1-replay, committed): needs a NEW native DLL (BeeNativeSegAI built isolated -> artifacts/EAI-LABEL/native-run, run with EASYAI_SEGAI_DIR=that folder; old bin/Release DLL lacks SEGAI_TrainerSetClassTable so named-class V2 training fails: NOT deployed in the app).
  Results k=2..20 (V2 class OK): train 6-30 s, infer ~0.15-0.2 s/img, all images bucket Sure (uncertainty useless), recall vs GT 0.33-0.53, vs baseline ~0.08-0.14. Cause: semantic mask merges touching objects: OK-named images baseline median 28 shapes vs model median 1-2 regions; NG-named 5 vs 3. More seeds (k=20) does not help.
  Verdict: plain Segment AI bootstrap = NO-GO. Candidate fixes: (A) seed-calibrated instance split (watershed using seed object size), (B) agent concurrency 2-4 + Economy turns (4x on wall time, cost -50% unproven), (C) hybrid.
- S1b DONE: threshold sweep (artifacts/EAI-LABEL/s1b-sweep, s1b-sweep2): raising V2 Foreground to 0.95-0.97 fixes most blob merging (k5-stratified fg0.97: GT R 0.88 P 0.93; NG vs baseline R .75 P .85) but OK-named images (28 touching objects) stay R<=0.56 P~0.65 => local model cannot replace agent on OK images.
- Agent levers on 12 images (baseline subset 315 s, 1.30 USD): Economy effort -68% cost (0.42), -36% time; haiku -82% cost but no speedup and recall .94 with eco-par2; 4 parallel Normal 156 s but costs more (CPU contention, SAM2 local 2 cores); Economy + 3 workers 81 s / 0.45 USD, P .987 R .991.
- FULL 84 run Economy + 3 workers (artifacts/EAI-LABEL/s2-eco-par3-full): wall 466.5 s (4.16x faster than 1939 s), 2.598 USD (-66.8% vs 7.827), all Success. Quality vs 18 real GT: TP90 FP0 FN0 at IoU 0.5 but mean IoU 0.785 (baseline 0.975): boxes less tight. vs baseline labels 84 imgs: P .995 R .968 IoU .771. Only ONE run so far (plan wants two); extra USD budget used about 7.7 of 10.
- Harness: EAI_LABEL_PARALLEL / EFFORT / MODEL / DISABLED_SKILLS env vars in BootstrapBaselineTests (uncommitted S1b test changes). Native DLL needed for replay/sweep only: artifacts/EAI-LABEL/native-run.
- Next: productize parallel workers + Economy preset in the real batch (AgentBatchRunner), decide on second confirm run, IoU tightness, local model only for NG-type images later.

## 2026-10-13 S2 productize
Committed parallel workers (setting AgentBatchWorkers 1-3, default 1). Effort already a setting (low=Economy), no new preset. Full suite 1318 pass, build 0 errors. Owed: UI smoke 100/150% en/vi, 2nd full run (needs USD), IoU tightness.

## 2026-10-13 run 2 + IoU variants (budget 5 USD, ~1.5 left)
- Run 2 Economy+3 workers, 84 imgs (artifacts/EAI-LABEL/s2-eco-par3-run2): 494 s (3.92x), 2.873 USD (-63.3%), 82/84 ok (2 dense OK imgs Failed at turn limit 7, 0.13 USD each). vs GT TP90 FP0 FN0 IoU .759; vs baseline P .985 R .988. Target 4x/-50%: cost met in 2 runs, speed 4.16x then 3.92x (just under).
- 6 GT imgs (iou-eco-tight, iou-normal): Economy+"fit tightly" prompt IoU .752 (no gain, 0.154 USD, 41 s); Normal IoU .969, 0.439 USD, 95.7 s (about 0.073 USD/img, extrapolated 84 imgs about 6.1 USD, about 1.45x). Normal fixes tightness but misses the targets. Untested: Economy then Normal refine only for low-IoU.
- Productized: commit a77b86a5b (AgentBatchWorkers). UI smoke still owed.

## 2026-10-13 Codex measurements + chat UI
- Codex (ChatGPT login) Economy, 3 GT imgs sequential: ~38 s/img, IoU .974, no USD/tokens reported. 9 imgs, 3 workers: 132 s (14.7 s/img effective, 2.9x), TP45 FP0 FN0 IoU .959. artifacts/EAI-LABEL/codex-seq3, codex-par3-9.
- Chat UI: compact composer card + attachments + Parallel runs 1/2/3 (commit b53697b0f); UI smoke screenshots artifacts/EAI-LABEL/ui-smoke1 (vi, 100%). Attachment E2E with Codex: get_reference called first (artifacts/EAI-LABEL/codex-ref2). Owed: 150% DPI smoke, attachment chips visual, Claude backend attachment run.
- git stash accident recovered, stash@{0} left as safety copy (see memory).

## 2026-10-13 refine experiment (Economy + Normal retry)
- Selector analysis (run 2 vs 18 GT): Economy IoU is low on EVERY image (.58-.86, mean .76), so no GT-free selector exists for "IoU low"; refine-by-IoU = Normal on all = 6.1 USD, no gain. Only selector found: outcome Failed (turn limit).
- Normal retry of the 2 Failed images (artifacts/EAI-LABEL/refine-failed2-normal): both Success (4 and 5 turns, 29 and 31 proposals), 0.234 USD, 52 s wall with 2 workers. Combined Economy run 2 + retry = 84/84 Success, about 2.84 USD (-63.7%), about 546 s if the retry runs after the pool (3.6x), would be hidden if retried inside the pool. IoU of the 2 retried images unmeasurable (no GT, OK-type).
- Not productized: retry-on-Failed at Normal in AgentBatchRunner is a candidate change (not written).

## 2026-10-13 retry productized + local box tightening (offline)
- Committed e290cca7e: AgentBatchRunner retry (Economy failed image / time-out -> one run at Normal on the same worker slot, costs add, never on limit/sign-in), setting AgentBatchRetryNormal (default on, Settings > Agent run toggle, en+vi). Tests 1326 pass, build 0 errors. Not smoked in UI / not run against the real agent in the app.
- Local tightening, offline on 18 GT images (90 shapes), scripts tmp/EAI-LABEL/refine/{bias,tight,tight2,tight3,tight4}.py (python+opencv, no repo change):
  * Economy error is random, not a bias: size ratio mean 1.00 (std .10), centre scatter std ~14 px on 270 px objects; no global scale fix (best scale .95-1.0).
  * Otsu threshold in the proposal window + connected component (clipped to proposal grown 15%) + cv2.minAreaRect: mean IoU .760 -> .829 (run2), .785 -> .838 (run1); 64% of shapes improve. Normal agent reaches .969.
  * Orientation by straight-edge support (disc with one squared corner; GT aligns to the flat sides) did NOT beat plain minAreaRect (.80-.82), restricting the angle to the proposal +-10..30 deg was worse (.75-.81).
  * Not validated on OK-type images (28 touching objects, no GT): the component can merge neighbours. Not ported to C#. Verdict: partial gain (+.07), not enough to reach .97; do not productize without a GT set for OK-type.

## 2026-10-13 S3 SAM2 refine committed
- Local SAM2 box tightening after each agent run (AgentSam2Refiner, setting AgentBatchSam2Refine default on) + SAM2 sessions ORT_DISABLE_ALL + NaN/Inf guard on encoder embeddings. Real models on 18 GT (90 shapes, run2 proposals): IoU .760 -> .976, 6.06 s/image (first image 21.6 s with model load). Evidence artifacts/EAI-LABEL/s3-smoke. Tests 1333 pass. Reviewer ACCEPT (5 minors noted: repeated NaN re-encode, image reload across workers, Dispose gate, IsFinite boxing, EXIF size mismatch unverified).
- Owed: full 84-image real run with retry + refine enabled (speed with SAM2 contention, cost), OK-type visual check, UI smoke 100/150% DPI en/vi, DirectML encoder NaN check.

## 2026-10-13 S3 full run + S4
- S3 full 84 run (artifacts/EAI-LABEL/s3-full-eco-par3-retry-sam2): Economy+3 workers+retry+SAM2 refine: 1038 s (1.87x), 2.654 USD (-66.1%), 84/84 Success, 0 retries, IoU vs 25 GT .974 (TP125 FP0 FN0), vs baseline P=R .989 IoU .859 (dense OK images, unchecked). Speed missed: SAM2 18 s/image under contention (6 s alone) because S3 applied ORT_DISABLE_ALL also to the DirectML session (5x slower encoder).
- S4 (artifacts/EAI-LABEL/s4-speed, tmp/EAI-LABEL/s4): ConstantFolding bug is CPU-EP only; DML + level ALL encoder 0.3-0.6 s. Fix committed: DISABLE_ALL only on CPU sessions, refine gate per image. Real-model smoke 18 GT: IoU .976, 1.89 s/image (was 6.06). Tests 1336 pass.
- Dev PC is i7-1360P + RTX A500/Iris Xe, NOT the production 2-core i7-7600U (HD 620 iGPU unmeasured, expect 2-3 s encoder). Owed: second full 84 run with the S4 build (needs user USD ok, ~2.7), production-PC timing, OK-type visual check, UI smoke.

## 2026-10-13 S4 full run INVALID (account session limit)
- artifacts/EAI-LABEL/s4-full-eco-par3-retry-sam2: wall 408 s, 1.07 USD but only 49/84 Success; 35 Failed with "You've hit your session limit · resets 9:50pm (Asia/Ho_Chi_Minh)" (HTTP 429, shared Claude account), 35 retries triggered by those failures (each failed too). NOT a speed or quality measurement. Keep as evidence, do not score. Rerun only after the limit resets (9:50pm), new OUT folder (e.g. s4b-full-...), same levers, MAX_USD 3.2 (user approved 2026-10-13 for one valid run; this spent run produced no valid result so the approval is still unused).
- Product note: a 429 "session limit" Failed result is treated by AgentBatchPlanner.LooksLikeLimit only if the text has the phrases usage limit / rate limit / quota / 429; "session limit" is NOT in LimitPhrases, so the batch did not stop and the retry-at-Normal fired on every limited image. Candidate fix: add "session limit" (and "hit your limit") to LimitPhrases so the batch stops and the retry is skipped.

## 2026-10-13 S4b VALID full run (artifacts/EAI-LABEL/s4b-full-eco-par3-retry-sam2)
- Economy+3 workers+retry+SAM2 refine, S4 build: 633.3 s (3.06x vs 1938.99), 2.697 USD (-65.5%), 84/84 Success, 1 retry, SAM2 304 s (3.6 s/image), IoU vs 25 GT .9749 (TP125 FP0 FN0), vs baseline P .995 R .972 IoU .861. Cost target met, quality met, speed 633 s vs 485 s target NOT met (3.06x, not 4x). Dev PC (i7-1360P), not the 2-core production PC.

## 2026-10-13 S5 Speed/Effort sliders (Agent chat composer)
- Speed slider x1..x32 shown only up to the machine cap (AgentSpeedRules: largest stop <= min(logical cores, RAM GB/1.5); 2c/8GB -> x2), Effort slider with the model's levels; both next to the model pill, two theme brushes (accent / caution), Parallel runs 1/2/3 removed. AgentBatchWorkers stored up to 32, capped at batch time. Build 0 errors, tests 1354 pass. Owed: UI smoke at 440 px, 100/150% DPI, en/vi; slider track/thumb colour may stay default accent (only Foreground/caption coloured); effort slider shows fewer than 5 stops for models with fewer levels.
