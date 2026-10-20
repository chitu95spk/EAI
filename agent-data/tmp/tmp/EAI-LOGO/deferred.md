# Deferred work: EAI-LOGO

Written in English by /epause. Resume with /eresume EAI-LOGO. Append new sections; never rewrite earlier ones.

## Pause 2026-10-15 20:21 - User asked to pause

### Facts (filled by script)
- session: de50294f-4e69-490b-98ed-8bd6ae8f78c4 (started 2026-10-15 12:25)
- usage: snapshot age 17695 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: none
- commits since session start (any session): 8
  - a01234ee6 [MISSING-TOOTH] Profile check: crest (tooth tip, rough edge) vs root (smooth bright band) detection; crest lines carry the score, roots are drawn dotted
  - 4c8ddbd00 [MISSING-TOOTH] Profile check result: draw a line at the crest of every detected tooth with its score, also when OK and in Run mode
  - 8728ee20c [MISSING-TOOTH] Profile check: Max distance + Slide replaced by one 'Gap vs trained (%)', both learned from the taught images
  - 33011abad [MISSING-TOOTH] Log every ERR of the Missing tool to the Logs dashboard with the reason, frame, area and library
  - 47348dcc6 [MISSING-TOOTH] Profile check: always show why a run is ERR; convert colour frames to gray; trace the stage result
  - 546971763 [MISSING-TOOTH] Train removes images that are the same as another image from the list
- files edited by this session: 8; still uncommitted: 2
  - EasyLabel\src\EasyAI.Wpf\MainWindow.xaml.cs [M]
  - EasyLabel\src\EasyAI.Wpf\ViewModels\MainViewModel.cs [M]

### Task and scope
Goal (user, 2026-10-15): detect ALL defects in the LOGO label/bottle images in the real project E:\Training\TrainLOGONEW (143 images 5472x3648, one class NG, oriented_rectangle), with a score threshold of 0.9 and no false alarms, whole-image CT under 200 ms. Also asked this session: EasyAI UI work (Delete key, compact popup, Space/Up/Down hotkeys, list sort toggles, multi-select context menu). Contract: none (ad-hoc, task ref EAI-LOGO; docs/easyai/EAI-LOGO-Claude-Code-Handoff-2026-10-15.md is the Codex handoff). Scope: ONLY the project E:\Training\TrainLOGONEW (user rule: work on the main project path, never on clones/tmp copies) and EasyLabel/** code. Never EasyVision runtime, BeeNativeSegAI, hardware.

### Done (verified)
- Dataset: 88 images now have NG oriented_rectangle labels in E:\Training\TrainLOGONEW\images\*.json (18 original user labels untouched; 70 drafts by Claude, description starts "claude-draft"). Backup of all JSON before the re-save: artifacts/EAI-LOGO/resave1/run/json-backup/. 85 images re-saved through the app API so the Stale flag is gone (artifacts/EAI-LOGO/resave1/run/resave.json: shapes unchanged).
- Models (candidates only, none active): E:\Training\TrainLOGONEW\LOGO_SegMent.easyai.data\models\v001..v005.segai. Latest v005 (train 79 / val 9, 1484 s). Result at threshold 0.9 with calibration anchor 0.45: 93/106 boxes found, 79/88 labelled images flagged, held-out 5/10, 1 unlabelled image flagged (Phong_L1 (8) 0.92) (artifacts/EAI-LOGO/real-pred7/run/predictions.json). Still missed at 0.9: MopL2 (3), NhanL2 (6), NHAN_PHONG (17),(18),(22),(33),(36),(41),(43),(50), NHAN_PHONG_CHONG (11),(13), TRAYL2 (3).
- Latency (artifacts/EAI-LOGO/rt-false, rt-true): camera frame (no PNG decode) inference p50/p95 = 66/79 ms CPU, 25/27 ms Intel GPU; PNG decode alone ~265-330 ms (so file input fails 200 ms, camera path passes; camera path itself NOT measured).
- Score calibration (new code, builds, 5 unit tests pass): SegInferenceSettings.CalibrateInstanceScore + SegProjectStore.Load/SaveInstanceScoreAnchor (advanced.json "instanceScoreAnchor": 0.45 set in the real project), applied in SegAiPredictor.PredictInstances, anchor in AutoLabelService cache key.
- EasyAI UI (build 0 errors into bin\Release 2026-10-15 20:08; SegAI.Tests 1481 pass / 12 skipped): Delete key on image list (Recycle Bin, compact Yes/No popup with trash icon placed one row below the selected row, above when near the form bottom), Space = fit (ShortcutId.FitSpace), Up/Down = previous/next image in the SHOWN order (NextImageDown/PreviousImageUp), list toggles (3 check boxes: Unlabelled first, Train first, Val first; off = natural name order), multi-select Ctrl/Shift + right-click menu (Delete, Train, Val, Remove label), prefetch of 3 neighbours in shown order. Smoke on a fixture project (artifacts/EAI-LOGO/delsmoke/smoke-*.ps1): single/multi delete, Up/Down, popup size/position verified once.
- ebuild skill updated (.claude/skills/ebuild/SKILL.md): EasyAI.Wpf = Any CPU only, run bin\Release, never bin\x64. Memory feedback-easyai-wpf-anycpu-only saved.

### Remaining (in order)
1. (start here) Ask the user to reopen bin\Release\net9.0-windows\EasyAI.exe and confirm in the REAL app: three tick boxes, Delete key + popup on the LOGO project, Space/Up/Down, right-click menu on a multi-selection, and whether Up/Down is still slow (if so measure inside the app: time from selection change to Canvas.ShowAsync end; PNG 20 MP decode is ~270 ms when not prefetched).
2. Commit the finished code with /ecommit (explicit paths only; SegAI score calibration + tests; EasyAI.Wpf UI changes + tests; ebuild skill is process). Nothing of this session is committed.
3. Improve detection toward "all defects, 0 false alarms at 0.9": review the 13 still-missed images and tighten their draft boxes; ask the user to confirm/correct drafts in the app; mark confirmed-clean images OK is NOT usable (Segment AI ignores unlabelled images, so clean images do not teach the model); consider more epochs or a different calibration anchor; re-check Phong_L1 (8) (0.92 false alarm, not yet viewed full-res), Phong_L1 (4),(9) looked clean.
4. Optional: fix SegAiTrainingEvaluation.cs so the training report mAP50 uses oriented boxes (report says mAP50 0 although boxes match, IoU .75-.93) - the user was asked, no answer yet; no native bug was found.
5. Optional: measure real camera-path latency (needs camera/live app); user wants < 200 ms.
6. Unlabelled images the user believes contain defects were reviewed only at low zoom: remaining unlabelled (about 55): NHAN_PHONG 1-13,19-21,30,45-49,52,53,55,56,58,59,62,63; Phong_L1 (9); TRAY 3,4,7,8,10; TRAYL2 4,5,8,9; NhanL2 2,4,5,7; NhanL1 (1); NHAN_PHONG_CHONG 5,12. Twice the user corrected missed defects (MopL2 dent, NHAN_PHONG (37) large crumple), so ask before treating them as clean.

### Decisions already made
- Work only on E:\Training\TrainLOGONEW, never on clones (user, repeated). The earlier clones under artifacts/EAI-LOGO/ (claude-proj2, claude-allproj, claude-train*) are obsolete evidence, do not use.
- Defect families confirmed by the user: MopL2 = dent (movable), NHAN_PHONG (37) = whole-label crumple region; faint scratches and faint bulges ARE defects the user wants labelled.
- Threshold 0.9 is achieved by score calibration (monotone map, anchor raw 0.45 -> 0.90), not by a retrained score; the instance score approximates box IoU so raw scores rarely exceed 0.7. User chose "Hieu chinh diem" over relabelling OK images. Calibration does not remove false alarms, it only renames the number.
- Delete in the app moves files to the Recycle Bin (not permanent) and the Delete key only acts when the image list has focus; Ctrl+Delete / toolbar Delete File still delete label files only.
- Up/Down keys step through the SHOWN list order and never consume the key when stepping is unavailable (Inspect tab).
- Popup: compact icon + Yes/No, no message text (user request); the old big popup was rejected.
- Build EasyAI.Wpf Any CPU only (user rule); never git stash; stage explicit paths.

### Do NOT redo
- Do not re-view MopL2, TRAYL2, NhanL2, NHAN_PHONG 45-63 rose area, TRAY, Phong_L1 sheets: judgement recorded above (artifacts/EAI-LOGO/review/*.jpg).
- Do not retrain just to re-test calibration; v005 predictions are in artifacts/EAI-LOGO/real-pred7. Harness for train/predict/resave/time on the real project: tmp/EAI-LOGO/claude/harness2/Program.cs (modes realtrain, realpredict, resave, realtime), built at artifacts/EAI-LOGO/claude-build/bin/Release/net9.0-windows/EasyLabel.SegAI.Tests.dll. realtrain takes --val "name;name" (semicolons).
- UI features above were smoke-tested once; do not rebuild/retest them unless the user reports a problem.

### In-flight agents and background work
None running. No agents were used. If dotnet processes named EasyLabel.SegAI.Tests.dll are still running (training), they are stale; the last training run finished (v005).

### Next commands
Authorization: build and focused tests were permitted by the user this session; commit is allowed on main by the standing rule (CLAUDE.md 12.1) once checks passed, but nothing was committed yet. Push/deploy/hardware false.
- Close EasyAI.exe first, then: dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release -v:minimal > artifacts/EAI-LOGO/calib/wpf-build-next.log 2>&1 ; grep -c ": error " artifacts/EAI-LOGO/calib/wpf-build-next.log
- Tests: dotnet test EasyLabel/tests/EasyLabel.SegAI.Tests/EasyLabel.SegAI.Tests.csproj -c Release "-p:OutDir=<project>/artifacts/EAI-LOGO/test-out/" (use OutDir so the running app is not disturbed)
- Commit with /ecommit: stage only EasyLabel/src/EasyLabel.SegAI/{AutoLabel/AutoLabelService.cs,Inspection/SegAiPredictor.cs,Options/SegInferenceSettings.cs,Project/SegProjectStore.cs,SegAiSession.cs}, EasyLabel/src/EasyAI.Wpf/** files listed by git status, EasyLabel/tests/EasyLabel.SegAI.Tests/{InstanceScoreCalibrationTests.cs,UnlabelledFirstOrderTests.cs,ShortcutTableTests.cs}, and .claude/skills/ebuild/. The worktree has many other sessions' dirty files: never git add . and never stash.

### Risks and unverified
- UI evidence is from a fixture project and UIA scripts; the right-click menu, the three tick boxes on a labelled list, ordering with real Train/Val data, Space fit, and the compact popup below-row placement were NOT exercised in the real app.
- Up/Down latency on 5472x3648 PNG not fixed with certainty (only prefetch order fixed); measured ~450-660 ms per step including test overhead.
- Known flaky test: AutoLabelServiceTests.NativeModel_ImportModel_KeepsLoadedModelAndDoesNotActivate (passed in the last full run).
- bin\x64 under EasyAI.Wpf is stale (Oct 8), left in place on purpose.
- Detection quality: held-out is only 10 boxes; false-alarm rate on truly clean images is unknown (no confirmed OK images exist).
- Real project files were modified directly (labels, models, advanced.json instanceScoreAnchor/angleSymmetry/instanceQuality, sidecar/meta, train/val flags in JSON); they are not in this repo's git.

### Acceptance still owed
- "Detect all defects with threshold 0.9 and no false alarms": NOT met (79/88 labelled images flagged, 1 unlabelled image flagged, 13 labelled boxes missed, held-out 5/10).
- CT < 200 ms: met for the inference part on a camera-style frame (CPU 66 ms, GPU 25 ms), not measured on the real camera path; not met for PNG file input (decode alone ~300 ms).
- Real-app confirmation of all UI changes by the user.

## Pause 2026-10-16 10:20 - User asked to pause

### Facts (filled by script)
- session: 7d961309-c562-479d-bb16-df7d47eb7eed (started 2026-10-16 00:07)
- usage: snapshot age 18534 min; 5h window used 34% (resets 2026-09-21 21:13); context used 12% [STALE snapshot: do not trust; ask the user for /usage]
- contracts edited this session: none
- commits since session start (any session): 0
- files edited by this session: 15; still uncommitted: 12
  - EasyLabel\src\EasyAI.Wpf\Infrastructure\AppSettings.cs [M]
  - EasyLabel\src\EasyAI.Wpf\MainWindow.xaml.cs [M]
  - EasyLabel\src\EasyAI.Wpf\Services\Agent\AgentPrices.cs [??]
  - EasyLabel\src\EasyAI.Wpf\Services\Agent\AgentPromptRefiner.cs [??]
  - EasyLabel\src\EasyAI.Wpf\Services\AgentRunner.cs [M]
  - EasyLabel\src\EasyAI.Wpf\Services\ImagePreviewLoader.cs [M]
  - EasyLabel\src\EasyAI.Wpf\ViewModels\InspectViewModel.cs [M]
  - EasyLabel\src\EasyAI.Wpf\ViewModels\LabelStepViewModel.AgentChat.cs [M]
  - EasyLabel\src\EasyAI.Wpf\ViewModels\MainViewModel.cs [M]
  - EasyLabel\src\EasyAI.Wpf\ViewModels\PanelViewModels.cs [M]
  - EasyLabel\src\EasyLabel.SegAI\Options\SegTrainingOptions.cs [M]
  - EasyLabel\src\EasyLabel.SegAI\Training\SegAiTrainingPipeline.cs [M]

### Task and scope
Goal (user, 2026-10-16): detect LOGO label defects (bulge "Phong", scratch "Xuoc", dent/crease) in E:\Training\TrainLOGONEW (126 images 5472x3648, 189 oriented_rectangle NG boxes, 8 families by file-name prefix) with high recall on a leak-free val set and detect time < 0.3 s per image on the Intel Iris Xe GPU (machine: i7-1360P, 15.7 GB). User gave full freedom to choose the direction ("tu quyet dinh"). Also done this session: EasyAI UI work (see Done). No task contract (ad-hoc, ref EAI-LOGO). Scope: the project E:\Training\TrainLOGONEW only, never clones; EasyAI.Wpf + EasyLabel.SegAI + BeeNativeSegAI.

### Done (verified)
- Measurement tooling (project-local): tmp/EAI-LOGO/score_boxes.py (rotated-box IoU recall/precision, train and val by JSON flags), bias_boxes.py, sweep_val.py (threshold sweep), resplit.py (stratified leak-free split), harness tmp/EAI-LOGO/claude/harness2/Program.cs (modes realtrain, realpredict --quality, setsplit); harness builds: artifacts/EAI-LOGO/claude-build4 (current, has torch DLLs copied in).
- Results (rotated-box IoU, threshold 0.45): v005 old labels val recall .56 IoU .72; v006 (960, 104/22) train .88 / .71, val .44 / .68; v007 (1280) same ~.71; v008 (1280, split 94/32) val recall .28 prec .20 IoU .65. Quality refine mode did not help (.71). Size 960->1280 did not change IoU or val recall. Predicted box area/label = 0.95 (no bias), centre offset ~5%. Threshold sweep of v008 on val: even at 30 candidates/image recall only .53 (IoU .3) / .40 (IoU .5): the first stage misses >half the val defects, so a stage-2 filter alone cannot fix it (artifacts/EAI-LOGO/pred-r4).
- The training-curve 'IoU 0.14' in the Train tab is the semantic head cell IoU (SegV2Core.cpp HeadPass), not box IoU.
- Split rule applied to the real project (97 train / 29 val, 34 val boxes): distinct NG defects (size/shape outliers per family, >=3 boxes, all MopL2) stay in Train; near-duplicate images (dHash<=2) kept on one side; list in tmp/EAI-LOGO/val-names.txt. Applied through the app API (harness setsplit).
- Native: BeeNativeSegAI/SegV2Core.cpp patched (tmp/EAI-LOGO/patch_finetune_instance.py): input side limit raised 960 -> 2560 (also SegBackboneFineTune.cpp), and instanceHead + backboneEpochs allowed via a second feature pass after FineTuneBackbone (deferInstance). Isolated build 0 errors (artifacts/EAI-FT/native-build.log); DLL copied to EasyLabel/src/EasyAI.Wpf/bin/Release/net9.0-windows and the torch/openvino/opencv/tbb DLLs + Models/DefectV2 copied there by hand.
- Managed: max input 2560 (SegTrainingOptions.cs, SegAiModelFile.cs, TrainStepRules.cs), new options BackboneEpochs/BackboneTorchPath written to native settings (SegAiTrainingPipeline.cs BuildV2SettingsJson).
- EasyAI UI (build 0 errors artifacts/ebuild-run/wpf.log; tests 1496 pass, 12 skipped: artifacts/EAI-SUBAGENT/test.log): preview prefetch 6 images x2 parallel + Check tab prefetch (ImagePreviewLoader.cs, MainViewModel.cs, InspectViewModel.cs); Train tab hides Files panel (MainWindow.xaml.cs, bug fixed 10:19: hide event no longer clears the View-menu choice); AI annotate bar removed (MainWindow.xaml); delete no longer rebuilds the list (PanelViewModels.cs OnImagesCollectionChanged); Enter approves the label popup (LabelDialog.xaml.cs); chat image paths are buttons -> OpenChatImageCommand (AgentChatWindow.xaml, LabelStepViewModel.AgentChat.cs); Refresh price block in Settings Claude Code & Codex page (AgentPrices.cs, AgentsSettingsViewModel.cs AgentPricesViewModel, tests AgentPriceTests.cs, real pages parse: 20 Claude + 3 OpenAI models); prompt sub-agent (AgentPromptRefiner.cs, AgentRunner.RefinePromptAsync, AppSettings AgentAutoRefinePrompt/AgentRefineHint, PromptSubAgentViewModel, wand toggle left of Send, tests AgentPromptRefinerTests.cs).
- Delegated read-only analysis: easyvision-algorithm (3 defect groups: A bulge ~83 imgs, B dent MopL2, C scratch/crease; ranked ideas: label ROI crop, hard negatives + stage-2 classifier, augmentation, per-family anomaly) and easyvision-profiler (GPU infer 25 ms p50 at 960, PNG decode 330-363 ms so file input cannot meet 0.3 s, camera frames can; EnableGpu defaults false; Quality refine cost unmeasured). Both reports were partly unverified (codegraph was down).

### Remaining (in order)
1. (start here) Read the result of the backbone fine-tune run train-ft3 (see In-flight): artifacts/EAI-LOGO/train-ft3/train-result.json. Run train-ft2 finished the fine-tune (15 epochs, val loss .066 -> .039, parity 3e-5) at 10:36 but the process then vanished with no result and no Windows error event; cause unknown (killed externally, or crash in my deferInstance second feature pass in SegV2Core.cpp). If ft3 also dies right after fine-tune.txt appears, debug the second pass alone. If it succeeds: realpredict newest models*.segai then score_boxes.py / sweep_val.py.
2. Compare fairly: train a no-fine-tune baseline on the SAME split (97/29): harness realtrain --sem-epochs 60 --instance-epochs 300 --side 960 (no backbone args), ~35 min, then score both with score_boxes.py and sweep_val.py. Fine-tune counts as useful only if val recall at IoU 0.5 beats that baseline. (The v008 numbers on the new split are leaked, do not compare.)
3. If fine-tune helps: expose BackboneEpochs/BackboneTorchPath in the Train tab (needs a packaged taps.pt + .map.txt, +~357 MB torch DLLs shipped with the app; currently torch DLLs are copied by hand), update the native test case backbone-finetune in BeeNativeSegAI/test/SegAITest.cpp:4294 (it still expects the old rejection), rebuild and run native tests.
4. CT < 0.3 s on Iris Xe: extend harness realtime mode (quality, side, ROI, pre-decoded BGR8 frames), measure per stage, CPU vs GPU, with EnableGpu true; only the camera-frame path can meet 0.3 s (PNG decode alone 330-363 ms).
5. Data: ask the user for defect-free images and more MopL2/TRAYL2/NhanL2/NhanL1 images, and which images are the same physical bottle (split by bottle).
6. Commit with /ecommit (explicit paths only): EasyLabel UI/price/sub-agent files, SegAI options/pipeline, BeeNativeSegAI/SegV2Core.cpp + SegBackboneFineTune.cpp, tests AgentPriceTests.cs/AgentPromptRefinerTests.cs/TrainStepRulesTests.cs + csproj link lines, strings resx (Strings.resx, Strings.vi.resx), AgentsSettingsView.xaml, AgentChatWindow.xaml, LabelDialog.xaml(.cs), MainWindow.xaml(.cs). Nothing is committed.

### Decisions already made
- Label ROI crop was NOT built: all boxes sit in 64-72% of the frame so a fixed ROI gives only ~1.25x resolution, and 960->1280 (1.33x) gave no gain; speed is not the blocker (camera path < 0.3 s). Reasoning only, not tested.
- Do not use IoU mean > 0.9 as the target: hand-drawn oriented boxes on faint defects cannot reach it (measured ~0.71-0.73 on train too). Proposed criteria: IoU 0.5 with recall >= 0.9 and <= 1 false alarm per 10 images, per group.
- Fine-tune was chosen next (user: 'tinh chinh backbone truoc'); instance head cannot use frozen-feature fine-tune without the second pass, hence the native patch.
- Split by physical bottle is unknown; near-duplicates used as proxy (dHash<=2). MopL2 treated as one distinct defect, all in Train (no dent val estimate possible).
- Never git stash, never build x64 for EasyAI.Wpf (Any CPU only, output bin\Release), do not overwrite bin\Release\BeeNativeSegAI.dll of EasyVision.

### Do NOT redo
- Do not re-run size 1280/960 comparisons, Quality-mode predict, ROI analysis, taxonomy or latency code reading; results above.
- Do not rebuild BeeNativeSegAI or the app unless the native/managed source changes again (builds are current at 10:19; native DLL 09:54).
- Do not re-extract hardware facts (i7-1360P, Iris Xe, RTX A500, 15.7 GB).

### In-flight agents and background work
- Background training run train-ft3 may STILL BE RUNNING at pause time (EasyLabel.SegAI.Tests.exe realtrain, output artifacts/EAI-LOGO/train-ft3, log run.log, started 2026-10-17 morning, 97/29, side 960, sem 60, backbone 15, instance 300, ~60-70 min; do not let the PC sleep). No watcher is armed; in a new session check with tasklist, then run by hand: artifacts/EAI-LOGO/claude-build4/EasyLabel.SegAI.Tests.exe realpredict --model <newest models\v*.segai> --out <artifacts/EAI-LOGO/pred-ft3> then python tmp/EAI-LOGO/score_boxes.py <predictions.json> 0.45 and sweep_val.py.
- The user may have EasyAI.exe open: do not train from the app while the harness runs; models/registry of the project are written by the harness.
- No agents running (both vision agents finished).

### Next commands
Authorization: build and focused tests were allowed by the user this session (/ebuild used once); training/prediction runs on the real project were allowed ('tu quyet dinh', 'tinh chinh backbone truoc'). Commit is allowed on main by the standing rule once checks pass, nothing committed yet; push/deploy/hardware false.
- Check training: tasklist | grep SegAI.Tests ; cat artifacts/EAI-LOGO/train-ft3/train-result.json
- Failure details: newest folder in E:\Training\TrainLOGONEW\LOGO_SegMent.easyai.data\runs, file failure.json
- Baseline run (no fine-tune): VAL=$(cat tmp/EAI-LOGO/val-names.txt); artifacts/EAI-LOGO/claude-build4/EasyLabel.SegAI.Tests.exe realtrain --out "E:/Code/EasyVision_Unisen - NoService/artifacts/EAI-LOGO/train-base" --val "$VAL" --sem-epochs 60 --instance-epochs 300 --side 960   (--out must be under artifacts/EAI-LOGO)
- App build: dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release -v:minimal > artifacts/<run>/wpf.log 2>&1 (close EasyAI.exe first or use -p:OutDir=<project-local>/)
- Tests: dotnet test EasyLabel/tests/EasyLabel.SegAI.Tests/EasyLabel.SegAI.Tests.csproj -c Release "-p:OutDir=<project>/artifacts/<run>/test-out/"

### Risks and unverified
- UI changes (Enter approves popup, chat path buttons, wand toggle + sub-agent run, Refresh price button over the network, Files panel restore after the Train tab, delete without list jump, Check/Label tab speed) were built and unit tested but NOT run in the app. The Files panel was lost for the user until the View-menu entry is re-ticked once (old bug, persisted).
- Sub-agent prompt refine and price refresh were never run against a real Claude/Codex or the network from the app.
- Fine-tune: first run failed with SEHException (cause unproven); second run result unknown; torch CPU time unknown; the combined path has no native test; native test case backbone-finetune is stale.
- Detection quality: val is small (34 boxes) and not split by physical bottle; no defect-free images exist, so false alarms per clean image are unknown; labels are hand-drawn and noisy.
- Latency: no Iris Xe measurement at 1280/1920, Quality-refine cost unmeasured, EnableGpu default false; product path must use camera frames not PNG.
- Native/torch DLLs and Models\DefectV2 in the EasyAI output were copied by hand (lost on a clean build); the packaged app has no fine-tune assets.
- Worktree has many other sessions' dirty files; stage explicit paths only.

### Acceptance still owed
- Recall >= 0.9 at IoU 0.5 with <= 1 false alarm per 10 images on a leak-free val: NOT met (best seen val recall .44-.56 on mixed splits, .28 leak-free earlier split).
- Detect < 0.3 s per image on Intel Iris Xe: not measured end to end (inference part ~25 ms at 960 on an old model; PNG decode alone exceeds it).
- Real-app confirmation of all UI changes by the user.

