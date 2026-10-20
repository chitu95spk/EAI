# Customer #2 (plant manager / integrator) - buy-or-not review of EasyAI, 2026-10-17
Read-only review. Evidence: tmp/EAI-STRATEGY/{competitors,roadmap,capabilities}.md, EASYLABEL_SPEC.md head, EasyLabel/ tree, src greps. "Not found" = grep of EasyLabel/src found nothing, not proof of absence.
Severity: B = blocks purchase, H = strongly lowers willingness to pay, M = nice to have.

## Verdict
Interesting for a 2-core/8 GB plant that wants local training and a Vietnamese UI, but today it is an engineering tool with no commercial wrapper (no licence, installer, accuracy report, backup, support). I would pilot it, not buy it.

## Items
CB-01 B Accuracy proof: no number I can show my boss. YOLO validated with val==train (mAP .995 on 5 images is meaningless); SAM2, anomaly, Segment AI on production PC all UNMEASURED. Fix: per-model validation report (held-out set, per-class precision/recall/FP/FN, confusion, sample overlays) exported as PDF/HTML; benchmark set published. Effort: M (2-3 wk).
CB-02 B Train-time on my PC unknown: only 16-core figure (76 s/84 imgs); "3 h" for 187 imgs is a memory note; 2-core i7-7600U UNMEASURED. Fix: measure, publish time table, show ETA before start. Effort: S.
CB-03 B No licensing/anti-copy: no licence key, activation, trial or hardware binding in EasyLabel (EasyVision has BeeLicense; not wired here). Anyone can copy the folder. Fix: reuse BeeLicense pattern, 30-day trial, per-PC key, offline activation. Effort: M.
CB-04 B No installer/updater: manual venv bootstrap.ps1, SAM2/YOLO-World ONNX placed by hand, trainer folder must exist, bin\Release is the output. Operator cannot install it. Fix: single MSI/MSIX with offline model pack, prerequisite check, signed binaries, versioned update channel with rollback. Effort: L.
CB-05 B AGPL/GPL exposure: Ultralytics AGPL-3.0 (or undisclosed Enterprise price), YOLO-World GPL, SAM3 custom licence. If I ship machines to my own customers I inherit this risk. Fix: legal review, default to Apache/MIT models (SAM2, Segment AI), licence matrix and NOTICE in installer, YOLO opt-in. Effort: M (legal).
CB-06 B Delivery to EasyVision is manual: copy .segai by hand; YOLO ONNX has no consumer in EasyVision (loads .segai only). Roadmap T4 "Send to EasyVision" not built. Fix: build T4 with versioned copy, sidecar (dataset hash, metrics), refuse on format mismatch, one-click rollback to previous model. Effort: S-M.
CB-07 H Privacy ambiguity: bulk labelling is local, but Agent mode sends JPEG crops (<=1536 px), project memory, class names to Claude/Codex under the user's own login. Customer NDAs may forbid. No consent dialog or "offline lock" found. Fix: one-time per-project consent (Q5), global "Offline only" switch that disables Agent and weight downloads, outbound-traffic list in docs, explicit no-telemetry statement (telemetry only grepped on agent paths). Effort: S.
CB-08 H Cost not predictable: no USD cap per batch/day, no estimate (T5 unbuilt); only ~0.026 USD/run on 2 images measured. Cost per 1000 images cannot be quoted. Fix: T5 caps + pre-run estimate + published cost-per-1000 table; local-first routing (T7). Effort: S-M. TCO also includes my own Claude subscription/API, a vendor I do not control.
CB-09 H Dependence on third-party CLI: Agent runs `claude -p` / Codex CLI subprocess; a vendor policy or CLI change breaks it. Fix: document as optional, make every core workflow work without it, pin tested CLI versions. Effort: S.
CB-10 H Hardware requirements unclear: YOLO path needs CUDA GPU (UI says "No CUDA GPU"), SegAI uses OpenVINO/CPU, SAM2 wants DirectML. No hardware probe or spec sheet. Fix: T3 probe + published minimum/recommended spec + safe mode for <=4 cores/8 GB. Effort: M.
CB-11 H Labour saved is unproven: no time-and-motion data (minutes per image manual vs assisted, images needed per defect class). Roboflow users report still confirming image by image. Fix: pilot on 2 real datasets (e.g. GEAR, DEBUG_LOGO), publish minutes saved and review rate. Effort: M.
CB-12 H Operator usability: 8-12 deliberate steps, expert controls (epochs, px, batch, family ids, mAP50-95) visible by default, no wizard, terse errors without fix buttons. Fix: T8 Easy mode, T2 actionable errors, first-run wizard. Effort: M.
CB-13 H Wrong/missing task coverage: no OK/NG image classification and no good-only anomaly detection, which most plants need and Keyence/Cognex/LandingLens sell. Fix: classification head first (frozen MobileNetV2 + small head), then PatchCore-lite; consumer tool in EasyVision. Effort: L.
CB-14 H Backup/restore: project is a plain folder (.easyai/, LabelMe JSON source of truth, good for portability) but no backup, snapshot, project-bundle export or corruption recovery found; agent cache in %LOCALAPPDATA% has no size cap. Fix: "Export project bundle" (images, labels, models, settings, hash) + auto snapshot before train/delete + cache LRU. Effort: M.
CB-15 H Audit trail: partial (log.csv for cost, agent-outcomes, run metrics). Missing: who labelled/approved, dataset hash per model, model lineage, approval - needed for ISO/customer audits. Fix: traceability pack (roadmap H3): model card with dataset hash, labeller, reviewer, date, metrics; append-only run log. Effort: M.
CB-16 H No multi-user/team sharing: single-user local folder; roadmap says "NOT to build" team features. Integrators with several operators/lines need at least a shared project on a network drive with locking. Fix: per-image lock + last-editor, or documented shared-folder workflow. Effort: M.
CB-17 H No support/logging package: no crash reporter, diagnostics button or log viewer found (grep Serilog/crash: no app-level handler). I cannot remote-diagnose a customer line. Fix: rolling log, diagnostics zip (versions, hardware probe, last errors, no images), support-bundle button. Effort: S-M.
CB-18 H No SLA/support terms/version policy: no support channel, response times, supported OS list, LTS, end-of-life. Fix: published support tiers, LTS every 6 months, compatibility matrix EasyAI <-> .segai format <-> EasyVision build. Effort: S (business), M (process).
CB-19 H Documentation: spec and plans are internal agent docs; no operator manual, quick start, troubleshooting or video; GuideWindow is passive. Fix: 20-page vi/en operator guide + 5-minute quick start + error FAQ. Effort: M.
CB-20 M Runtime fragility: SAM2/YOLO-World files placed manually, Ultralytics downloads weights on first use (breaks offline plants), venv with torch cu124 (multi-GB). Fix: hash-verified offline model pack, no first-use downloads. Effort: M.
CB-21 M Learning loop incomplete: only Agent suggestions recorded; corrections to SegAI/SAM2/YOLO not learned; no uncertainty queue (T6/T7). "Learns from you" is not yet true for the free local path. Effort: M.
CB-22 M Lock-in perception: 8 export formats incl. YOLO/COCO (good) but only .segai runs in EasyVision. Fix: advertise open export, state data ownership in contract, add ONNX consumer or classification tool. Effort: S.
CB-23 M Release hygiene: 5 test projects exist, but only CHANGELOG_DEV, stray logs in EasyLabel root, mixed net8/net9 output folders, 150% DPI UI smoke UNMEASURED. Fix: release checklist, clean artifacts, signed build, UI smoke at 100/150% DPI. Effort: S.
CB-24 M Pricing undefined: competitors range from free (CVAT/Label Studio/X-AnyLabeling) to ~$79-149/mo cloud (Roboflow, Label Studio Starter; UNVERIFIED) to quote-only (Cognex/Keyence/MERLIC/LandingLens). A plant wants a budgetable price: per-PC perpetual + annual update fee. Fix: define Operator-PC and Integrator tiers incl. 1 year updates. Effort: S.
CB-25 M Vietnamese UI is a differentiator but parity checked by entry count only (911/911); no native-speaker review. Fix: operator review of vi strings + parity test (T2). Effort: S.

## Competitor comparison (as a buyer; prices per competitors.md, mostly UNVERIFIED)
- Roboflow: best labelling UX and hosted training; cloud, free tier data is public, credit/seat pricing. EAI wins on offline/privacy and EasyVision integration; loses on proven accuracy and polish.
- CVAT / Label Studio: free self-host but Docker + staff time, no operator UX, no training. EAI lighter on a 2-core PC; they win on team features and maturity.
- X-AnyLabeling: closest free desktop; GPL, no train loop. EAI wins only if the train+deliver loop works end to end.
- Landing AI: few-image OK/NG, cloud-first; EAI lacks classification/anomaly (CB-13).
- Cognex ViDi/In-Sight, Keyence IV4/VS, MERLIC: tested, supported, SLA-backed, but closed hardware or quote-priced. EAI can win on cost and openness if CB-01/03/04/06/18 are fixed.

## What would make me pay
1. A held-out accuracy report on my own sample images, produced by the tool (CB-01).
2. One offline installer with licence and updates (CB-03/04).
3. "Send to EasyVision" with one-click rollback (CB-06).
4. A written statement and switch: "no image leaves this PC" (CB-07).
5. Measured train time on a 2-core/8 GB PC and a fixed price (CB-02/24).

## Minimum sellable product checklist
[ ] Installer (offline, signed, prerequisites, model pack, uninstall)           CB-04, CB-20
[ ] Licence key + trial + hardware binding                                        CB-03
[ ] Validation report per model (held-out metrics, PDF/HTML)                      CB-01
[ ] Measured benchmarks: 2-core/8 GB train + inference time                       CB-02, CB-10
[ ] Send to EasyVision + versioning + rollback                                    CB-06
[ ] Offline-only switch, consent, data-flow statement                             CB-07
[ ] USD cap/estimate if Agent ships, else Agent labelled optional/beta            CB-08, CB-09
[ ] Easy mode + actionable errors + auto Train/Val split                          CB-12
[ ] Project bundle export + automatic snapshot                                    CB-14
[ ] Model card / audit record (dataset hash, labeller, metrics, date)             CB-15
[ ] Diagnostics zip + rolling logs                                                CB-17
[ ] Operator manual vi/en + quick start                                           CB-19
[ ] Licence matrix; no AGPL/GPL component bundled by default                      CB-05
[ ] Support terms, compatibility matrix, price list                               CB-18, CB-24
[ ] Segment AI or OK/NG classification proven on 2 real datasets                  CB-11, CB-13
