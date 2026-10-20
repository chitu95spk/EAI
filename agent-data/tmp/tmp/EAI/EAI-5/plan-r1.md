# EAI-5 plan r1 (draft, awaiting user approval) - Inspect tab with its own image list

User request: remove the Data tab and put an **Inspect** tab in its place (tab order Inspect | Gán nhãn | Huấn luyện) with an Inspect-only image list, separate from the training file list. User answered "mặc định" to the five questions; recommended options taken. Master plan: tmp/EAI/plan.md section 6 row 4 (Kiểm tra). Depends on EAI-UI commit F (tab host) and EAI-4 (registry/active model, AutoLabelService/predictor).

## 1. Behaviour
- **Source (Q1):** an independent Inspect list, not tied to the training project's images. Add via 'Mở thư mục ảnh Inspect', 'Thêm ảnh', drag-drop; list kept in memory and persisted per project in `.easyai/inspect.json` (paths only, relative or absolute; missing files shown as such). Camera (MVS) is EAI-6.
- **Run (Q2):** uses the ACTIVE model (registry). Buttons: 'Chạy ảnh hiện tại', 'Chạy tất cả' (background, progress, cancel; images processed sequentially through the existing predictor lock; refused while training runs). Per image result: verdict OK/NG, region count, max score, elapsed ms, and the predicted regions drawn on the canvas as a read-only overlay (dashed class colours with scores, same renderer as auto-label suggestions) — no editing and no label writes on this tab.
- **List columns:** file name, verdict (OK green / NG red / not run grey, with icon so colour is not the only cue), regions, score, ms. Search box, filter OK/NG/not run, sort by column. Selecting a row shows the image + overlay in the canvas.
- **Summary strip (small):** run count, OK, NG, mean ms (computed from results; shown only when results exist).
- **Export results (Q3 default):** 'Xuất CSV' (file, verdict, regions, score, ms, model file, timestamp). The optional 'Thêm vào dữ liệu huấn luyện' button is NOT included in this milestone (listed as a later option to avoid a half feature).
- **Settings (Q4 default):** threshold and minimum area come from the model's existing inference settings (SegInferenceSettings); the tab shows them read-only with a link to Settings > AI model (no separate sliders).
- Verdict rule: image is NG when at least one NG region passes the filters (same rule as ToolSegmentAI IsOK of SegAiResult), OK otherwise; error results are shown as 'Lỗi' with the message in a tooltip.

## 2. Architecture
- `EasyLabel.SegAI/Inspection/InspectService` (UI-free, tested): Run(path) / RunAll(paths, IProgress, CancellationToken) over the loaded model with the same guards as AutoLabelService (training active, no model), results cache keyed by (path, model file, file stamp), summary maths, CSV writer.
- `InspectListStore` (UI-free): list persistence `.easyai/inspect.json` (works with the single-folder layout of EAI-DATA, or the old one if EAI-DATA is not done yet via ProjectLayout once available).
- WPF: `InspectTabView` + `InspectViewModel` as the first tab; Inspect list panel replaces the training Tệp panel while the Inspect tab is active (the training Tệp panel returns on the other tabs); canvas shows the Inspect image with the overlay layer; Run/RunAll/Cancel commands bound; strings en/vi; all controls real.

## 3. Verification
Build 0 errors; unit tests (verdict rule, cache key, cancel, CSV, list persistence); parity: for the ng-only fixture model the verdict/regions equal AutoLabelService results on the same images; UIA smoke en+vi: open Inspect folder, Run all, filter NG, select row overlay, Export CSV, tab switch keeps results, guarded while training.

## 4. Out of scope
Camera/Live (EAI-6), statistics page (EAI-7), editing labels from Inspect, adding inspected images to the dataset, SAM2.

## 5. Risks
Large images (2520x2600) take time per run: sequential with progress and cancel; results cached. Overlay on big images uses the existing suggestion renderer. The Inspect list separate from the training list must not leak selection state between tabs (tested).
Estimate: about 0.9M tokens.

## 6. Open questions (defaults taken)
Q1 independent Inspect list persisted per project (taken). Q2 Run current / Run all with OK/NG, ms, score, overlay, read-only (taken). Q3 CSV export only; add-to-training deferred (taken). Q4 inference settings read-only from the model (taken). Q5 order: F, EAI-DATA, G, then EAI-5 (taken).
