import re, os
os.chdir(r'E:\Code\EasyVision_Unisen - NoService')
p = r'tmp\EAI-LOGO\deferred.md'
s = open(p, encoding='utf8').read()
head, tail = s.rsplit('## Pause 2026-10-16 10:20', 1)

fills = {
'Task and scope': """Goal (user, 2026-10-16): detect LOGO label defects (bulge "Phong", scratch "Xuoc", dent/crease) in E:\\Training\\TrainLOGONEW (126 images 5472x3648, 189 oriented_rectangle NG boxes, 8 families by file-name prefix) with high recall on a leak-free val set and detect time < 0.3 s per image on the Intel Iris Xe GPU (machine: i7-1360P, 15.7 GB). User gave full freedom to choose the direction ("tu quyet dinh"). Also done this session: EasyAI UI work (see Done). No task contract (ad-hoc, ref EAI-LOGO). Scope: the project E:\\Training\\TrainLOGONEW only, never clones; EasyAI.Wpf + EasyLabel.SegAI + BeeNativeSegAI.""",

'Done (verified)': """- Measurement tooling (project-local): tmp/EAI-LOGO/score_boxes.py (rotated-box IoU recall/precision, train and val by JSON flags), bias_boxes.py, sweep_val.py (threshold sweep), resplit.py (stratified leak-free split), harness tmp/EAI-LOGO/claude/harness2/Program.cs (modes realtrain, realpredict --quality, setsplit); harness builds: artifacts/EAI-LOGO/claude-build4 (current, has torch DLLs copied in).
- Results (rotated-box IoU, threshold 0.45): v005 old labels val recall .56 IoU .72; v006 (960, 104/22) train .88 / .71, val .44 / .68; v007 (1280) same ~.71; v008 (1280, split 94/32) val recall .28 prec .20 IoU .65. Quality refine mode did not help (.71). Size 960->1280 did not change IoU or val recall. Predicted box area/label = 0.95 (no bias), centre offset ~5%. Threshold sweep of v008 on val: even at 30 candidates/image recall only .53 (IoU .3) / .40 (IoU .5): the first stage misses >half the val defects, so a stage-2 filter alone cannot fix it (artifacts/EAI-LOGO/pred-r4).
- The training-curve 'IoU 0.14' in the Train tab is the semantic head cell IoU (SegV2Core.cpp HeadPass), not box IoU.
- Split rule applied to the real project (97 train / 29 val, 34 val boxes): distinct NG defects (size/shape outliers per family, >=3 boxes, all MopL2) stay in Train; near-duplicate images (dHash<=2) kept on one side; list in tmp/EAI-LOGO/val-names.txt. Applied through the app API (harness setsplit).
- Native: BeeNativeSegAI/SegV2Core.cpp patched (tmp/EAI-LOGO/patch_finetune_instance.py): input side limit raised 960 -> 2560 (also SegBackboneFineTune.cpp), and instanceHead + backboneEpochs allowed via a second feature pass after FineTuneBackbone (deferInstance). Isolated build 0 errors (artifacts/EAI-FT/native-build.log); DLL copied to EasyLabel/src/EasyAI.Wpf/bin/Release/net9.0-windows and the torch/openvino/opencv/tbb DLLs + Models/DefectV2 copied there by hand.
- Managed: max input 2560 (SegTrainingOptions.cs, SegAiModelFile.cs, TrainStepRules.cs), new options BackboneEpochs/BackboneTorchPath written to native settings (SegAiTrainingPipeline.cs BuildV2SettingsJson).
- EasyAI UI (build 0 errors artifacts/ebuild-run/wpf.log; tests 1496 pass, 12 skipped: artifacts/EAI-SUBAGENT/test.log): preview prefetch 6 images x2 parallel + Check tab prefetch (ImagePreviewLoader.cs, MainViewModel.cs, InspectViewModel.cs); Train tab hides Files panel (MainWindow.xaml.cs, bug fixed 10:19: hide event no longer clears the View-menu choice); AI annotate bar removed (MainWindow.xaml); delete no longer rebuilds the list (PanelViewModels.cs OnImagesCollectionChanged); Enter approves the label popup (LabelDialog.xaml.cs); chat image paths are buttons -> OpenChatImageCommand (AgentChatWindow.xaml, LabelStepViewModel.AgentChat.cs); Refresh price block in Settings Claude Code & Codex page (AgentPrices.cs, AgentsSettingsViewModel.cs AgentPricesViewModel, tests AgentPriceTests.cs, real pages parse: 20 Claude + 3 OpenAI models); prompt sub-agent (AgentPromptRefiner.cs, AgentRunner.RefinePromptAsync, AppSettings AgentAutoRefinePrompt/AgentRefineHint, PromptSubAgentViewModel, wand toggle left of Send, tests AgentPromptRefinerTests.cs).
- Delegated read-only analysis: easyvision-algorithm (3 defect groups: A bulge ~83 imgs, B dent MopL2, C scratch/crease; ranked ideas: label ROI crop, hard negatives + stage-2 classifier, augmentation, per-family anomaly) and easyvision-profiler (GPU infer 25 ms p50 at 960, PNG decode 330-363 ms so file input cannot meet 0.3 s, camera frames can; EnableGpu defaults false; Quality refine cost unmeasured). Both reports were partly unverified (codegraph was down).""",

'Remaining (in order)': """1. (start here) Read the result of the backbone fine-tune run train-ft3 (see In-flight): artifacts/EAI-LOGO/train-ft3/train-result.json. Run train-ft2 finished the fine-tune (15 epochs, val loss .066 -> .039, parity 3e-5) at 10:36 but the process then vanished with no result and no Windows error event; cause unknown (killed externally, or crash in my deferInstance second feature pass in SegV2Core.cpp). If ft3 also dies right after fine-tune.txt appears, debug the second pass alone. If it succeeds: realpredict newest models\v*.segai then score_boxes.py / sweep_val.py.
2. Compare fairly: train a no-fine-tune baseline on the SAME split (97/29): harness realtrain --sem-epochs 60 --instance-epochs 300 --side 960 (no backbone args), ~35 min, then score both with score_boxes.py and sweep_val.py. Fine-tune counts as useful only if val recall at IoU 0.5 beats that baseline. (The v008 numbers on the new split are leaked, do not compare.)
3. If fine-tune helps: expose BackboneEpochs/BackboneTorchPath in the Train tab (needs a packaged taps.pt + .map.txt, +~357 MB torch DLLs shipped with the app; currently torch DLLs are copied by hand), update the native test case backbone-finetune in BeeNativeSegAI/test/SegAITest.cpp:4294 (it still expects the old rejection), rebuild and run native tests.
4. CT < 0.3 s on Iris Xe: extend harness realtime mode (quality, side, ROI, pre-decoded BGR8 frames), measure per stage, CPU vs GPU, with EnableGpu true; only the camera-frame path can meet 0.3 s (PNG decode alone 330-363 ms).
5. Data: ask the user for defect-free images and more MopL2/TRAYL2/NhanL2/NhanL1 images, and which images are the same physical bottle (split by bottle).
6. Commit with /ecommit (explicit paths only): EasyLabel UI/price/sub-agent files, SegAI options/pipeline, BeeNativeSegAI/SegV2Core.cpp + SegBackboneFineTune.cpp, tests AgentPriceTests.cs/AgentPromptRefinerTests.cs/TrainStepRulesTests.cs + csproj link lines, strings resx (Strings.resx, Strings.vi.resx), AgentsSettingsView.xaml, AgentChatWindow.xaml, LabelDialog.xaml(.cs), MainWindow.xaml(.cs). Nothing is committed.""",

'Decisions already made': """- Label ROI crop was NOT built: all boxes sit in 64-72% of the frame so a fixed ROI gives only ~1.25x resolution, and 960->1280 (1.33x) gave no gain; speed is not the blocker (camera path < 0.3 s). Reasoning only, not tested.
- Do not use IoU mean > 0.9 as the target: hand-drawn oriented boxes on faint defects cannot reach it (measured ~0.71-0.73 on train too). Proposed criteria: IoU 0.5 with recall >= 0.9 and <= 1 false alarm per 10 images, per group.
- Fine-tune was chosen next (user: 'tinh chinh backbone truoc'); instance head cannot use frozen-feature fine-tune without the second pass, hence the native patch.
- Split by physical bottle is unknown; near-duplicates used as proxy (dHash<=2). MopL2 treated as one distinct defect, all in Train (no dent val estimate possible).
- Never git stash, never build x64 for EasyAI.Wpf (Any CPU only, output bin\\Release), do not overwrite bin\\Release\\BeeNativeSegAI.dll of EasyVision.""",

'Do NOT redo': """- Do not re-run size 1280/960 comparisons, Quality-mode predict, ROI analysis, taxonomy or latency code reading; results above.
- Do not rebuild BeeNativeSegAI or the app unless the native/managed source changes again (builds are current at 10:19; native DLL 09:54).
- Do not re-extract hardware facts (i7-1360P, Iris Xe, RTX A500, 15.7 GB).""",

'In-flight agents and background work': """- Background training run train-ft3 may STILL BE RUNNING at pause time (EasyLabel.SegAI.Tests.exe realtrain, output artifacts/EAI-LOGO/train-ft3, log run.log, started 2026-10-17 morning, 97/29, side 960, sem 60, backbone 15, instance 300, ~60-70 min; do not let the PC sleep). No watcher is armed; in a new session check with tasklist, then run by hand: artifacts/EAI-LOGO/claude-build4/EasyLabel.SegAI.Tests.exe realpredict --model <newest models\\v*.segai> --out <artifacts/EAI-LOGO/pred-ft3> then python tmp/EAI-LOGO/score_boxes.py <predictions.json> 0.45 and sweep_val.py.
- The user may have EasyAI.exe open: do not train from the app while the harness runs; models/registry of the project are written by the harness.
- No agents running (both vision agents finished).""",

'Next commands': """Authorization: build and focused tests were allowed by the user this session (/ebuild used once); training/prediction runs on the real project were allowed ('tu quyet dinh', 'tinh chinh backbone truoc'). Commit is allowed on main by the standing rule once checks pass, nothing committed yet; push/deploy/hardware false.
- Check training: tasklist | grep SegAI.Tests ; cat artifacts/EAI-LOGO/train-ft3/train-result.json
- Failure details: newest folder in E:\\Training\\TrainLOGONEW\\LOGO_SegMent.easyai.data\\runs, file failure.json
- Baseline run (no fine-tune): VAL=$(cat tmp/EAI-LOGO/val-names.txt); artifacts/EAI-LOGO/claude-build4/EasyLabel.SegAI.Tests.exe realtrain --out "E:/Code/EasyVision_Unisen - NoService/artifacts/EAI-LOGO/train-base" --val "$VAL" --sem-epochs 60 --instance-epochs 300 --side 960   (--out must be under artifacts/EAI-LOGO)
- App build: dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release -v:minimal > artifacts/<run>/wpf.log 2>&1 (close EasyAI.exe first or use -p:OutDir=<project-local>/)
- Tests: dotnet test EasyLabel/tests/EasyLabel.SegAI.Tests/EasyLabel.SegAI.Tests.csproj -c Release "-p:OutDir=<project>/artifacts/<run>/test-out/\"""",

'Risks and unverified': """- UI changes (Enter approves popup, chat path buttons, wand toggle + sub-agent run, Refresh price button over the network, Files panel restore after the Train tab, delete without list jump, Check/Label tab speed) were built and unit tested but NOT run in the app. The Files panel was lost for the user until the View-menu entry is re-ticked once (old bug, persisted).
- Sub-agent prompt refine and price refresh were never run against a real Claude/Codex or the network from the app.
- Fine-tune: first run failed with SEHException (cause unproven); second run result unknown; torch CPU time unknown; the combined path has no native test; native test case backbone-finetune is stale.
- Detection quality: val is small (34 boxes) and not split by physical bottle; no defect-free images exist, so false alarms per clean image are unknown; labels are hand-drawn and noisy.
- Latency: no Iris Xe measurement at 1280/1920, Quality-refine cost unmeasured, EnableGpu default false; product path must use camera frames not PNG.
- Native/torch DLLs and Models\\DefectV2 in the EasyAI output were copied by hand (lost on a clean build); the packaged app has no fine-tune assets.
- Worktree has many other sessions' dirty files; stage explicit paths only.""",

'Acceptance still owed': """- Recall >= 0.9 at IoU 0.5 with <= 1 false alarm per 10 images on a leak-free val: NOT met (best seen val recall .44-.56 on mixed splits, .28 leak-free earlier split).
- Detect < 0.3 s per image on Intel Iris Xe: not measured end to end (inference part ~25 ms at 960 on an old model; PNG decode alone exceeds it).
- Real-app confirmation of all UI changes by the user.""",
}

for title, text in fills.items():
    pat = re.compile(r'(### ' + re.escape(title) + r'\n)TODO\(claude\):[^\n]*\n')
    assert pat.search(tail), title
    tail = pat.sub(lambda m: m.group(1) + text + '\n', tail, count=1)
assert 'TODO(claude)' not in tail
open(p, 'w', encoding='utf8', newline='').write(head + '## Pause 2026-10-16 10:20' + tail)
print('filled')
