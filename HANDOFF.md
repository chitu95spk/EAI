# EAI (EasyAI / EasyLabel) — bàn giao sang máy khác

Tạo 2026-10-20 từ `E:\Code\EasyVision_Unisen - NoService` (HEAD nguồn: xem `agent-data/source-HEAD.txt`, commit `c76d1ed3`).
Remote gốc: https://github.com/BeeEyesAutomation/EasyVision_Unisen (EAI và BeeNativeSegAI nằm TRONG repo này, không có remote riêng).

## 1. Thư mục này chứa gì

| Đường dẫn | Nội dung |
| --- | --- |
| `EasyLabel/` | Toàn bộ solution `EasyLabel.sln` (net9.0): app WPF **EasyAI.Wpf**, Core, SegAI, Commands, Online, Camera, tests. |
| `EasyLabel/Lib/` | Bộ native git-ignored (~500 MB): `native/segai` (BeeNativeSegAI.dll, libtorch, OpenCV, OpenVINO, Models/DefectV2) + `vcrt`. Build copy từ đây (`-p:SegAiNativeDir=...` để đổi). |
| `BeeNativeSegAI/` | Source C++ của engine Segment AI (35 file tracked). **Không** kèm `x64/`, `artifacts/`, `test/data` (build output, dựng lại được). |
| `agent-data/` | Dữ liệu agent: memory, contract, handoff, lessons, skills (mục 4). |
| `HANDOFF.md` | File này. |

Bản copy là **working tree** (gồm cả 72 file đang sửa chưa commit ở máy gốc, danh sách `agent-data/uncommitted-at-copy.txt`; một số là hunk của session khác). Đây là git repo mới, một commit khởi tạo `baseline-from-main-c76d1ed3+dirty`; lịch sử gốc không đi theo.
Nếu cần lịch sử/diff chuẩn: clone repo gốc rồi dùng `EasyLabel/` và `BeeNativeSegAI/` ở đó.

## 2. Dựng lại trên máy mới

Yêu cầu: Windows 10/11 x64, .NET 9 SDK, VS 2022 (MSBuild + workload C++ v143), MVS SDK (camera Hikrobot, `MVCAM_COMMON_RUNENV`), Git.
Không đi theo (tải/cài lại): libtorch (`ThirdParty/libtorch` của repo gốc, xem README ở đó), OpenCV 4.5.5, OpenVINO — đã có bản DLL trong `EasyLabel/Lib/native/segai` nên **chạy app không cần dựng native**; chỉ cần dựng native khi sửa `BeeNativeSegAI/`.

1. App: `dotnet build EasyLabel\src\EasyAI.Wpf\EasyAI.Wpf.csproj -c Release` — **chỉ Any CPU**, output `bin\Release` (không có bin\x64). Ghi log ra file rồi grep `: error ` (CLAUDE.md).
2. Test: `dotnet test EasyLabel\EasyLabel.sln -c Release` (Core, SegAI, Commands, Online, Ai.Mcp, EasyAI.Wpf.Tests…). Bench: `tests/EasyLabel.OnlineBench`, `tests/EasyLabel.Train512Bench` (README trong từng thư mục; cần `EasyLabel` và `BeeNativeSegAI` là anh em — đúng bố cục `E:\Code\EAI`).
3. Native (nếu sửa): `MSBuild BeeNativeSegAI\BeeNativeSegAI.vcxproj /p:Configuration=Release /p:Platform=x64`; vcxproj trỏ libtorch/OpenCV theo `ThirdParty` của repo gốc → cần chỉnh đường dẫn. ABI = 1; đổi struct công khai thì tăng ABI.
4. Dữ liệu ngoài repo, phải mang riêng: `E:\EAI_TEST` (ảnh OK, test agent), `E:\Training\TrainLOGONEW\images` (126 cặp LabelMe, 123 NG — dữ liệu NG thật), model `.segai` mẫu (`bin/Release/Program/TESSSSSST/SegAI_00_000/` ở repo gốc), model RapidOCR/ONNX nếu dùng OCR (không nằm trong repo). Đường dẫn tuyệt đối `E:\...` có thể phải sửa ở env/settings.

## 3. Trạng thái công việc (đọc theo thứ tự)

Sản phẩm: app WPF cục bộ để label → train → kiểm tra Segment AI, rẻ, đơn giản cho operator; có agent chat (Claude/Codex CLI), chế độ Online (realtime, ngân sách 100 ms / 20 MP). Mục tiêu và quyết định đã khóa: `agent-data/memory/easyai-project-plan.md`, `easyai-product-target.md`, `easylabel-annotation-tool.md`.

Các mảng và việc còn mở (commit mới nhất ở trên):
- **EAI-CLS** (bảng lớp tên, nhãn tự thêm; commit 6188321d, 170d035b, c76d1ed3): còn nợ UI smoke, P4 phần cứng (latency N>2, memory K=33), P5 cần OK riêng, `FormSegmentTraining.cs` là code chết ở repo EasyVision (không thuộc EAI). Handoff: `agent-data/tmp/EAI-CLS/deferred.md`.
- **EAI-CC** (command registry CC-0..CC-5): CC-1..CC-3 đã commit; còn CC-4 (hotkeys, cần UI smoke) và CC-5 (settings). Contract: `agent-data/claude/task-contract.EAI-CC.toml`, brief `tmp/EAI-CC/`.
- **EAI-ONL / EAI-ONL-MODE** (Online): CC-12..CC-17b đã commit (recipe, runner, endpoint LAN, status window); chưa đo full-chain ở 20 MP, recall model ≈ 0 là blocker; UI smoke của status window nợ. Plan: `tmp/EAI-ONL-MODE/plan.md`; memory `eai-onl-status.md`, `eai-online-budget-100ms-20mp.md`.
- **EAI-COMMERCIAL / EAI-COST / EAI-AGENT**: review thương mại hóa (`tmp/EAI-COMMERCIAL/SYNTHESIS.md` là điểm vào), giới hạn chi phí agent, popup chat. `deferred.md` trong mỗi thư mục.
- **EAI-POS**: vị trí/góc (recall ≈ 1.0, center 4.5 px / angle 3° chưa đạt, nghi nhiễu nhãn): `eai-pos-status.md`.
- **LIVE_CAM**: camera 0.15 s grab, YOLO-World 0 box trên ốc vít: `live-cam-online-test-status.md`.
- **BeeNativeSegAI**: 2 file đang sửa chưa commit (`SegBackboneFineTune.cpp`, `SegV2Core.cpp`) — kiểm `git diff` trước khi dùng; V2 trainer, prior-shift box 1.6x: `segai-v2-*.md`.
- Việc chung nợ: **mọi UI smoke** (CLAUDE.md 0.2a: không báo "đã verify" nếu chưa chạy UI thật).

## 4. Dữ liệu agent (`agent-data/`) và cách nạp trên máy mới

- `CLAUDE.md`, `AGENTS.md` — luật của repo gốc (tiếng Việt). Chỉ phần liên quan EAI: Hard Rules 0.1/0.2/0.2a/0.2b, lệnh build, kỷ luật token. Các mục nhắc `Common.PropetyTools`, EtherCAT, `CODEX_HISTORY.md` là của EasyVision, **không áp dụng** cho `E:\Code\EAI`. Nên rút gọn thành `E:\Code\EAI\CLAUDE.md` riêng khi bắt đầu.
- `memory/` — các memory EAI/SegAI/feedback + `MEMORY.md`. Nạp: copy `*.md` vào `C:\Users\<user>\.claude\projects\<slug-của-E--Code-EAI>\memory\` (slug tạo ra sau lần đầu mở Claude Code trong `E:\Code\EAI`), hoặc dùng `memory_save` của agentmemory. `MEMORY.md` gốc còn liệt kê cả mục EasyVision không liên quan — lọc lại.
- `claude/` — `task-contract.EAI-*.toml` (+template), `dispatch-mode.txt`, `invariants.md`, `settings.json`, `agents/` (easyvision-* subagent), `skills/` (eroute, eresume, ecommit...). Copy vào `E:\Code\EAI\.claude\`; các skill/agent còn tên đường dẫn EasyVision (BeeCore, CODEX_HISTORY) → sửa trước khi tin. `settings.local.json` cố ý không mang theo (quyền cục bộ).
- `docs/easyai/` (hướng dẫn, handoff LOGO), `docs/agent-lessons/` (LESSONS.md, ledger chi phí), `docs/architecture/`.
- `tmp/EAI*/` — plan, contract, `deferred.md`, script smoke, báo cáo (chỉ file text/script ≤ 300 KB; ảnh, build output, log lớn không mang theo). Đặt lại thành `E:\Code\EAI\tmp\` để các đường dẫn trong handoff vẫn đúng.
- Quy ước làm việc đã chốt: luôn `eresume <ID>` đọc `deferred.md`; không `git stash`; không `git add .`; stage đúng hunk của task; UI smoke dùng PrintWindow, truyền đường dẫn có dấu nháy; chỉ build/test khi user cho phép; ít subagent Claude, Codex làm việc tay chân (xem memory `feedback-*`).
- Token/khóa API: **không** có trong gói này (Codex token cũ đã bị thu hồi; agent cần đăng nhập lại CLI trên máy mới). agentmemory (WSL2) không mang theo — cài lại `npx -y @agentmemory/agentmemory@latest`.

## 5. Việc đầu tiên trên máy mới

1. `git log --oneline -3` (một commit baseline) → build app Any CPU → chạy `dotnet test`.
2. `tail` `agent-data/tmp/EAI-CLS/deferred.md`, `tmp/EAI-COMMERCIAL/deferred.md`, rồi chọn contract (EAI-CC CC-4 hoặc EAI-ONL-MODE) và hỏi user OK trước khi sửa/build.
3. Chép `E:\EAI_TEST`, `E:\Training\TrainLOGONEW` nếu cần đo Online/POS.
