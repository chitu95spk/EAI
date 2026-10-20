---
name: segai-trainval-labeling-decisions
description: "Segment AI Train/Val model, label-tab behaviour decisions and open items (V148-V154, 2026-09-29/30)"
metadata:
  node_type: memory
  type: project
  originSessionId: a8090c57-2ecd-4188-b5e2-43c5fb31a8db
  modified: 2026-09-29T18:20:15.682Z
---

User-decided semantics (chat, Vietnamese): Val must be chosen by the user, never a random %. An image WITHOUT a label is ignored for BOTH training and validation (SegTrainingMode.Ignored; no badge, dimmed). Labelled images: Auto=Train, pin Val=Val; switching to Val keeps the label. Space toggles the current labelled image Val<->Train (Val->Auto, else->Val); right-click menu also works. ValidationPercent stays serialized but no longer selects images (UI row removed). Label hotkeys must work anywhere while labelling (IMessageFilter owned by ToolSegmentAI; Delete NOT forwarded inside the image list because there it deletes the image). Labels auto-save ~600 ms after each commit using the already computed image key; Save Program keeps its flush handler (last-edited image + pending list edits) but returns early when nothing is dirty. A labelled Val image whose labels lie entirely outside Check now aborts training like Train (allowEmptyLabels removed) — asked the user, no answer yet.

Fixed bug: new labelled image lost its labels on reopen (deferred list save overwrote inline-saved labels with an empty list).

Commits (main): cd077ae checkpoint, ef555d1 V148, cfb621e V149, 8220741 V150, d251f45 canvas checkpoint, 2a4c289 V152 (+aedda81 fix), 1c42042 V151 (thumbnail cache), 6cd5134 build fix. V154 (ProjectContainerStore in-memory hash cache) implemented + reviewed by opus, review-fix round done, FINAL verification and commit were still pending when this was written.

Open / owed: UI smoke of everything (hotkey filter, Space vs focused button, debounce save, progressive list load) — agents cannot run the UI; real-dataset timings of the list load and Save Program; V151 cache-hit speedup measured only x4.7-8.1 on synthetic PNGs (test asserts >=3x); V153 per-image Val evaluation table (missed/false-alarm per Val image) not started; renaming a thumbnail then editing its labels can still be overwritten by the list save (pre-existing); zip Save after ONE changed file still rewrites the whole package (incremental zip update not done; folder storage gets the full V154 benefit); the user's running EasyVision.exe (started 20:26 on 09-29) predates V150+ and needs a rebuild via BeeMain x64.
**Why:** so future sessions do not re-ask settled decisions or repeat the investigations.
**How to apply:** read this before touching ToolSegmentAI*.cs, SegmentAI.Dataset.cs, DefectLabelCanvas.cs, RegisterImgDashboard.cs. Related: [[segai-v2-trainer-facts]], [[feedback-git-line-level-staging-verify-head]], [[easyvision-two-launch-folders]].
