# EAI-ARCHIVE: finished EasyAI work (as of 2026-10-09)

Excludes EAI-CLS (another session). The nine per-ID contracts were moved (git mv) to `.claude/archive/EAI-ARCHIVE/task-contract.EAI-<ID>.toml`; plans stay under `tmp/EAI/<ID>/`.
"Unverified" items are tracked in `tmp/EAI/EAI-OPEN/deferred.md`.

| ID | What was delivered | Commits | Status |
|---|---|---|---|
| EAI-0 | EasyLabel net9 retarget, SegAI/Camera/WPF-UI shell skeleton, native + MVS spikes | b0504bc31 | done |
| EAI-1 | EasyLabel.SegAI backend: native wrappers, dataset IO, LabelMe converter with holes, `.easyai` store, SegAiSession, training pipeline, registry, parity test | 4e7a4bf10 ff2b70afe 6b4797738 d1201fa3d 2034015a5 73c81f7a3 6e0ca74d9 340c70dff | done; real-model parity only as opt-in dump test |
| EAI-2 | WPF annotation canvas, geometry/edit session, Step 1 Data panel, arrow-key stepping | 04e89a0b9 da6293798 245ef9bd9 b47f995f1 | done; mouse smoke after fixes unverified |
| EAI-3 | Auto-label backend (mask to polygon with holes), Label panel, suggestion layer | f03e0cd4c 417ff55d8 | done |
| EAI-4 | Training service, live charts, candidates, comparison, Use this model | ab206ffea 1a1109299 | done |
| EAI-DATA | Single `.easyai` folder, LabelMe JSON with train/val flags, non-destructive migration, multi-format Export | 8ada1a590 477d94d80 | done; edge cases unverified |
| EAI-5 | Inspect tab: own image list, OK/NG, ms, read-only overlay, CSV | 9cf131411 | done; vi smoke and some UI paths unverified |
| EAI-UI | Labelme-style shell, menus, tools, AvalonDock panels, Label/Train tabs, shortcut table, logo theme and icon, LabelMe import/convert | 224e06604 7c6adce88 5841e357f 5b2301473 0a13edd19 29b11a453 a9fc05c4b e2beab849 9116ab4a1 ae5f79878 2267b9f6a 8f9697117 | done; many UI smokes unverified |
| EAI-AI | Auto-label source selector, SAM2/YOLO-World ONNX (DirectML/CPU), AI-Points/AI-Box, MCP server, Claude Code agent runner, S4 hardening | 11751d81f 04126c4ab ea8d710d6 b7c4427c4 8f4bc8cae 04d4b17fb 9d9442f17 b35a25eaf 6100dc850 97e3b1fba | S1-S4 done; 150% DPI and GPU latency owed |
| plan/track | Contracts, plans, issue logs | 62c644158 00323e1e8 | done |

## Last recorded test evidence
- EAI-AI S3b (artifacts/EAI/EAI-AI/s3b-r2/): build 0 errors; Mcp 32, Onnx 26, Core 150, SegAI 458 passed / 3 skipped (Model/RealAgent categories not run).
- EAI-AI S4 smoke: en and vi at 100% DPI (artifacts/EAI/EAI-AI/s4/smoke).
- Numbers for the earlier IDs are in their own contracts/handoffs; not re-measured for this archive.

## Notes
- Not re-verified at archive time: hashes were read from `git log --grep '^\[EAI'`; no build or test was run.
- Uncommitted leftovers: `.claude/task-contract.EAI-UI.toml` plan-r4 reference edit; ucReport.cs/.Designer.cs RJButton edits mixed with another session's lines (user said ignore).
