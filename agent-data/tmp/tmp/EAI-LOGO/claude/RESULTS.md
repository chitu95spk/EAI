# EAI-LOGO Claude session 2026-10-15
Harness: tmp/EAI-LOGO/claude/harness2 (modes trainall, predictall); build artifacts/EAI-LOGO/claude-build.
Round1: train 15/val 3 (artifacts/EAI-LOGO/claude-train3) -> predictions claude-pred1 over 143 images (claude-allproj, hardlinks).
Review: tmp/EAI-LOGO/claude/sheet*.jpg, cands.json. Accepted 27 images / drafts in artifacts/EAI-LOGO/claude-proj2 (description "claude-draft score"); rejected MopL2 x4, 4 low-score boxes.
Round2: train 38/val 7 (claude-train4, 1157 s) -> claude-pred2. Held-out drafts 26/13/33/8 detected .42-.70; human val NhanL2(1)/CHONG(19)/NhanL1(7) still .19-.31 (missed).
Round2 new >=.3: real = NHAN_PHONG (44),(50), NhanL2 (6); FP = NHAN_PHONG (36), CHONG (12),(7), Phong_L1 (9), TRAY (9).
Not done: timing, box tightening, full-res review of images with all scores <.25, user review of drafts.
