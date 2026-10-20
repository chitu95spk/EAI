---
name: easyai-project-plan
description: "EasyAI = WPF-UI front-end on EasyLabel.sln for Segment AI (label, train, check, stats); locked decisions and EAI-0 status"
metadata:
  node_type: memory
  type: project
  originSessionId: fa1df89c-fc0d-4e0a-882e-08ad010f38d0
  modified: 2026-10-05T07:58:48.389Z
---

EasyAI (user request 2026-10-05): standalone app that imports a dataset / images / one MVS camera, labels (auto-label from a model, import model), trains/retrains Segment AI, checks on an image list or camera, shows statistics. Master plan: `tmp/EAI/plan.md` (r4), EAI-0 plan `tmp/EAI/EAI-0/plan-r1.md`, issues `tmp/EAI/EAI-0/issues.md`, contract `.claude/task-contract.EAI-0.toml`.

Locked decisions: lives inside `EasyLabel/EasyLabel.sln` (net9.0, LangVersion 12, not net48/C# 7.3); WPF-UI 4.0.3 + CommunityToolkit.Mvvm 8.4.0 (ScottPlot deferred to EAI-4); native engine shared (`BeeNativeSegAI.dll`, ABI 1), managed Segment AI code cloned; MVS only, via P/Invoke of `MvCameraControl.dll` (net40 wrapper not used); Space = Train/Val, pan = middle mouse; raster masks with holes supported from EAI-1; stay on net9 (EOL 2026-11-10 accepted); UI rules: only WPF-UI controls, no mock/placeholder UI, one Primary button per panel, ICommand on release, modes as enums, Grid+GridSplitter.

Status: EAI-0 committed b0504bc31. EAI-1 CLOSED 2026-10-05: S1-S6 + review fixes committed (4e7a4bf10, ff2b70afe, 6b4797738, d1201fa3d, 2034015a5 S5 training, 73c81f7a3/6e0ca74d9 S6 parity test, 340c70dff review fixes). Parity vs EasyVision SegmentAI.DoWork bit-exact 11/11 on bin/Release/Program/TESSSSSST/SegAI_00_000/model.segai (+dataset PNGs; harness ref48 in tmp/EAI/EAI-1/parity, env EASYAI_PARITY_MODEL/IMAGE_DIR/OUT; needs repo\BeeNativeSegAId\Release on PATH for c10/torch). 42 Core + 96 SegAI tests pass. Owed: real V1/V2 training smoke (needs user authorization, EASYAI_RUN_TRAIN=1), review items 6/7 skipped, fixes 3/8 untested. Handoff: tmp/EAI-1/deferred.md. EAI-2 CLOSED 2026-10-07: A 04e89a0b9 (canvas, geometry, hole-preserving SegAI save), B da6293798 (Step 1 Data panel); handoff tmp/EAI/EAI-2/deferred.md; owed mouse/UI smoke after review fixes + 150% DPI (UltraViewer steals foreground, harness has a foreground guard). Next: EAI-3. Owed from EAI-0: MVS enumerate/open/grab (EAI-6, hardware), 150% DPI, multi-monitor, light/dark switch, ABI-mismatch/126 paths untested.

**Why:** big multi-milestone build; decisions were locked by the user, do not re-ask.
**How to apply:** read plan.md section 5/8 before each EAI milestone. On this PC env `MVCAM_COMMON_RUNENV` points at the SDK Development folder, the DLL is in Common Files\MVS\Runtime\Win64_x64. Related: [[easylabel-annotation-tool]], [[feedback-ui-no-tips-mimic-existing-tools]], [[segai-v2-prior-shift-box-overextension]].
