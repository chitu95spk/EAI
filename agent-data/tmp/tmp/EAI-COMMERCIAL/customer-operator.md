# EasyAI from the customer's seat (operator, no ML knowledge, VN, 2-core i7-7600U / 8 GB)
Read-only review, 2026-10-17. Sources: tmp/EAI-AGENT/ui-map.md, tmp/EAI-STRATEGY/roadmap.md, EasyLabel/src/EasyAI.Wpf Resources/Strings.vi.resx (dump: tmp/EAI-COMMERCIAL/vi_dump.txt, line numbers below = vi_dump.txt lines = key order), Views/*.xaml, ViewModels/LabelStepViewModel*.cs. Nothing was run; judgements come from strings and XAML only (UI behaviour unverified).

## Journey scores (ease / trust, 1-5)
| Step | Ease | Trust | Why |
|---|---|---|---|
| 0 Install / first run | 3 | 3 | Simple mode is default on new installs (App.xaml.cs:30), but agent sign-in needs a terminal and model files may be missing |
| 1 New project | 2 | 3 | "Loại dự án: Phát hiện / Vị trí", "Sao chép ảnh và nhãn", .easyai - no plain-language goal question |
| 2 Type "tìm vết xước" | 2 | 2 | Needs Claude/Codex account, English-oriented prompt chain, costs real dollars |
| 3 Agent labels | 3 | 2 | Works per single image by default; wait times, "$" progress; results unexplained |
| 4 I correct | 3 | 3 | Accept/Reject understood; Train/Val/Bỏ qua flags and "Stale/Thiếu JSON" are not |
| 5 Train | 3 | 2 | Simple strip helps; YOLO/epoch/Val/batch jargon still on screen in Advanced; no time promise on 2-core |
| 6 Test on camera/images | 2 | 2 | Camera = MVS only, parameters in microseconds/dB/ROI; OK/NG rule undefined |
| 7 Understand OK/NG, trust | 2 | 2 | Metrics mAP/IoU/Precision/Recall; no "how many bad parts will I miss" statement |
| 8 Send to EasyVision | 4 | 3 | One button, but folder must be set in Settings first |

## Findings

**CU-01 (Critical) The agent needs a Claude/Codex account the operator does not have.** Typing "tìm vết xước" only works after logging into a third-party service. Message: "Claude Code chưa đăng nhập: hãy mở terminal, chạy claude và đăng nhập bằng /login" (AgentNotSignedIn, vi line 134; LabelStepViewModel.cs:739). An operator does not know what a terminal is; they also need a paid subscription/API they never bought with EasyAI. Wished: first-run wizard that says plainly "Tính năng này cần tài khoản Claude (trả phí riêng)", with a Đăng nhập button (exists in AgentsSettingsView.xaml:182 but is not offered from the error), and a fully offline path that needs no account.

**CU-02 (Critical) Hidden money and data leaving the factory.** Progress shows "Agent 3/20 - $0.42" (AgentBatchProgressFormat, 775), "tối đa $..." (920), estimate "khoảng $X (ước tính)" (1037). The customer is never told before the first run that images are uploaded to an outside service (NDA risk, roadmap Q5 consent only "recommended", not implemented in strings: no Consent key exists). Wished: one-time consent per project in plain Vietnamese ("Ảnh của bạn sẽ được gửi ra ngoài nhà máy. Có / Không"), visible default cap (roadmap Q4 1 USD/batch) shown in VND-friendly wording, a "Chạy offline" switch.

**CU-03 (Critical) Nothing tells me whether the result is good enough to stop a defective part.** Train result is "Đã huấn luyện {0} trong {1}" (194) or in Advanced "mAP50 {1}, mAP50-95 {2}" (226); compare table shows "IoU điểm ảnh / Precision / Recall / Tỉ lệ diện tích khung" (252-256). Simple mode shows only "4 OK {0} · NG {1}" (1072). No sentence like "Trong 20 ảnh thử, bắt được 18 ảnh lỗi, báo nhầm 2". Wished: translate "Ảnh đúng / Bỏ sót / Báo nhầm" (257-260, these exist but are buried in Advanced compare) into the Simple-mode headline with a traffic light and a "Chưa đủ tin cậy, thêm ảnh lỗi" recommendation.

**CU-04 (High) Project kind is jargon.** "Loại dự án: Phát hiện / Vị trí" (696-697) - operator wants "Tìm lỗi (xước, nứt, thiếu)" vs "Đếm / định vị chi tiết". There is no "OK/NG per part" option even though the whole business is OK/NG; OK/NG-only models are flagged "EasyVision chưa đọc được mô hình OK/NG" (SendOkNgUnsupported, 1094). Evidence ProjectDialog.xaml (Strings.ProjKind). Wished: pick by goal, with pictures; hide .easyai/".data" naming.

**CU-05 (High) New project asks 2 path decisions + a copy toggle before any value.** Fields: name, location, kind, image folder (optional), "Sao chép ảnh và nhãn" (ProjectDialog.xaml; 700-703). Errors like "Tên được Windows dành riêng", "thư mục ảnh và thư mục dữ liệu chồng lên nhau" (723, 731) are file-system talk. Click count to first image on screen: Open folder(1) + name/location/browse(4) + OK(1) = ~6+. Wished: drag a folder of photos onto the window, project auto-named and auto-placed.

**CU-06 (High) Agent prompt works best in English; sub-agent rewrites my Vietnamese silently.** "Sub-agent sửa prompt ... dịch sang tiếng Anh nếu cần" (877, 883) means what I typed is replaced and I may not recognise what was sent; placeholder example is vague ("đánh dấu mọi vết xước", 119). Extra latency + cost per run. Wished: show my sentence + the interpreted one with a checkbox, and ready-made Vietnamese chips ("vết xước", "nứt", "thiếu chi tiết") so I rarely type.

**CU-07 (High) Too many knobs on the label bar.** Options row: Ngôn ngữ ENG/VIE, "Độ chính xác box" Easy/Normal/Hard, "Mức nỗ lực của agent" Economy/Normal/Best, Model, Shape, Label strip, "Chạy song song", "Tốc độ" (LabelStepView.xaml:242-363; vi 891-916). "Mức nỗ lực", "box", "shape", "skill", "sub-agent", "cache" are meaningless to me; wrong choice quietly changes cost. Wished: hide all of it in Simple mode; one "Gán nhãn" button.

**CU-08 (High) Default is "current image only"; labelling 100 photos is many clicks.** RunAgentCoreAsync works on the current image (ui-map item 5); "Mọi ảnh chưa nhãn" is a different source row (AutoLabelAll, vi ~85). Operator expects one press = all photos. Estimated clicks: type prompt 1, Run 1, then per image Accept/Enter; for 50 images ~100+ actions. Wished: "Gán nhãn tất cả ảnh còn lại" as the primary button with a review grid (AgentBatchReview strings exist, 784-791) and "Chấp nhận tất cả".

**CU-09 (High) Wait with no honest ETA on my PC.** TrainEtaFormat "Còn khoảng {0} phút" (1050) appears only from epoch 1; 2-core speed is "unmeasured" (roadmap risks, Q6). Recorded Segment AI V2 training elsewhere ~3 h. A shop-floor user fears the PC is frozen and cannot use it meanwhile; "Chế độ an toàn ... huấn luyện chậm hơn" (1046-1049) does not say how much slower. Wished: before Start: "Dự kiến ~X giờ trên máy này; bạn có thể để qua đêm", and a "Tắt máy khi xong" / sleep-block option.

**CU-10 (High) Training block messages assume I know Train/Val.** "Chưa có ảnh nào đặt là Val. Hãy đặt một ảnh làm Val hoặc dùng Tự chia ảnh Val" (186); "Hãy gán nhãn ít nhất 2 ảnh" (1008). Fix buttons exist (TrainAutoAssignVal 1009, TrainFixGoToLabel 1015) which is good, but the Train/Val/"Bỏ qua" toggles are on every image (66-68). Wished: auto-split silently, never mention Val; say "cần ≥ 10 ảnh có lỗi để kết quả đáng tin" with a count.

**CU-11 (High) YOLO / GPU / Python error text leaks into a CPU-only shop.** "Không có GPU CUDA ...", "Thiếu môi trường Python (.venv) ... tools\easyai-train" (217-221). Advanced tab 'YOLO / RT-DETR' is shown next to Segment AI (TrainKindUltra = identical English, 209). On this PC it is always a dead end. Wished: hide unavailable engines entirely on the 2-core/no-CUDA profile.

**CU-12 (High) Missing backbone/native files = stuck.** "Không tìm thấy file backbone (mobilenetv2-12.onnx). Hãy mở thư mục model, chép file vào đó" (188) and "Hãy chọn thư mục [Segment AI native] trong Cài đặt" (187). A bought product must not ask the operator to copy ONNX files; model pack installer exists (ModelPack* 1095-1108) but is opt-in and "Thu muc:" lacks diacritics (1105). Wished: installer bundles everything; first run self-check with a single "Sửa" button.

**CU-13 (Medium) Live camera supports MVS only and shows engineering parameters.** "Phơi sáng (us)", "Độ khuếch đại (dB)", "Gamma", "Chế độ kích hoạt", "Lệch ROI theo X (px)" (954-964); banner "Camera chưa kết nối" with only "Cài đặt camera" (930-931). Operator who just wants to point at a part gets a lab console. No statement that other camera brands are unsupported. Wished: "Tự động" preset default, plain-language "Ảnh tối quá / sáng quá" helper, supported-camera list on the connect screen.

**CU-14 (Medium) How "OK"/"NG" is decided is not explained.** Inspect shows Kết quả / Điểm / "Độ tin cậy" and settings "Ngưỡng {0} · diện tích tối thiểu {1} px (theo cài đặt model)" (InspectSettingsFormat 613). I do not know that NG = a segmented region above a threshold, nor that I can change it and what happens. Wished: slider "Nhạy / Ít nhạy" with live count of how many test images flip, and a one-line rule: "NG nếu tìm thấy vết xước lớn hơn X mm".

**CU-15 (Medium) "Cần xem lại" queue is good but unexplained.** InspectFilterDoubtful (1077) and the uncertainty sort exist; label is vague. Wished: "Ảnh máy chưa chắc - bạn hãy xem" with a counter and a hint that correcting these teaches the model.

**CU-16 (Medium) Corrections do not visibly improve anything.** Vocabulary rule: "Một từ chỉ được học sau khi bạn chấp nhận lớp của nó từ ba lần" (1039) - invisible learning, three-times rule is unexplained, memory UI is in Agent settings ("Bộ nhớ agent", 867-890). I cannot see "mô hình đã học thêm từ sửa của bạn". Wished: "Huấn luyện lại với phần bạn vừa sửa" button after N corrections, with before/after OK/NG counts.

**CU-17 (Medium) Mixed language / untranslated strings in the Vietnamese UI.** Identical to English: TrainKindUltra (209), CvFmtDetect/Obb, SetPageAgents, ModeTrain/Val "Train"/"Val" (66-67), "Segment AI", "epoch", "batch", "loss", "IoU", "Skill", "sub-agent", "cache", "Precision/Recall", slash-commands "/clear /check /model" (983), "Stale", "ONNX". Missing diacritics at ModelPackTargets (1105). Wished: Vietnamese glossary (Epoch -> "số lượt học", Val -> "ảnh kiểm tra") and parity test that rejects unchanged strings.

**CU-18 (Medium) Chat flow exposes developer slash-commands.** AgentSlashHelp "/clear /check /suggest /model <tên> /mode <current|all|remaining> /cancel /forget /help" (983-990); "Mô hình: {0}", "Chế độ không hợp lệ: ... Dùng current, all hoặc remaining" (990). Live steps like "Đang đo", "Đang khoanh vùng đối tượng" (996-997) are interesting but the "Ảnh {0}/{1}" and tool names ("Đang dùng {0}") are noise. Wished: chat shows 3 big buttons (Gán nhãn ảnh này / Tất cả ảnh còn lại / Dừng); slash-commands hidden.

**CU-19 (Medium) Error messages blame me or name internals.** "Lần chạy {0} agent thất bại: {1}", "mã thoát {0}, không có đầu ra" (135, 138), "Agent không hoàn tất trong 5 phút và đã bị dừng" (136), "Không dùng được GPU, chạy bằng CPU" (AiProviderFallbackFormat) - no next action, no support code. Roadmap T2 promises "every error has a fix button" - only the train blocks have it so far. Wished: every error = what happened in 1 sentence + one button (Thử lại / Đăng nhập / Liên hệ hỗ trợ + copy log).

**CU-20 (Medium) Window is dense for a 1366x768 shop PC.** Left tool strip 84 px with ~15 drawing tools (ToolStripView.xaml), docking panels, stock ComboBox at LabelStepView.xaml:122/307, two settings pages deep for colours/gradients (468-474 "Màu gradient bắt đầu"). Unverified at the real size. Wished: Simple mode shows only image, suggestions and the 5-step strip (EasyStepStrip exists, 1063-1074); everything else behind "Nâng cao".

**CU-21 (Medium) Version/format fear: "bản cũ không còn mở được dự án này".** MigrationOneWay (line 28) says older EasyAI versions can no longer open the project after migration. A factory with two PCs will lose work. Wished: ask before migrating, keep the backup path visible, and refuse if another PC would be impacted.

**CU-22 (Medium) Send-to-EasyVision depends on a Settings folder chosen elsewhere.** ExpSendFolderNotSet "Hãy chọn trong Cài đặt > Tệp" (1026) forces a detour on the last step, right when I am excited; "EasyVision không đọc được định dạng file model này. Hãy cập nhật EasyVision hoặc huấn luyện lại" (1030) gives two opposite fixes. Wished: auto-detect the EasyVision install, ask once at the Send click with a picker, and say precisely which EasyVision version is needed.

**CU-23 (Low) Window titles and deletion prompts are fine but "Thùng rác" semantics unclear for labels.** "Xóa các tệp nhãn của {0}? Ảnh không bị xóa." (432) good; "Xóa kỹ" vs recycle not stated for models/runs. Wished: undo for model deletion, "Đã xóa - Hoàn tác".

**CU-24 (Low) No visible help.** Guide window exists (GuideWindow.xaml) but the UI map says no tips by owner decision (memory: no help text); a first-time operator has no 60-second video/tour and no "Mẫu dự án demo (vỏ nhựa)" to try without risk. Wished: a built-in sample project with 20 pre-labelled images so I can see success before using my own parts.

**CU-25 (Low) Cost of mistakes is not reversible by me.** "Lỗi thời / Thiếu JSON" chips (Stale, MissingJson, 69-70), "Tệp dự án không hợp lệ" (730) have no repair button. Wished: "Sửa dự án" auto-repair and backup restore button.

## Time / money summary (estimated, unmeasured)
- Setup: 6+ clicks to first image; sign-in outside the app if using agent.
- Labelling 50 images with agent: unknown wait, $ per image (roadmap measured ~USD 0.026 per run on 2 images, tiny sample), 100+ acceptance actions in current default.
- Training on 2-core/8 GB: hours (not promised in UI). PC unusable meanwhile risk.
- Wasted money risk: paying for agent runs before knowing the model will work; wasted weeks if OK/NG is not what EasyVision can consume (CU-04).

## Top 5 deal-breakers
1. CU-01/CU-02: agent needs a Claude/Codex account + terminal login, costs dollars, and uploads factory images without a clear consent.
2. CU-03/CU-14: no plain statement of "how many bad parts will it catch / falsely reject" and no explanation of the OK/NG rule.
3. CU-09: no honest training time on the 2-core PC (likely hours) and no overnight-safe mode.
4. CU-12/CU-11: operator told to copy ONNX files / fix Python/CUDA; the product is not self-contained.
5. CU-04: project kinds are "Phát hiện/Vị trí", no OK/NG-per-part path that EasyVision can load (SendOkNgUnsupported).
