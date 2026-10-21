# CLAUDE.md — Luật làm việc trong repo EAI (EasyAI / EasyLabel + BeeNativeSegAI)

> File này được nạp vào context **mỗi lượt hội thoại**, nên nó chỉ chứa thứ luôn đúng.
> Repo tách ra từ EasyVision_Unisen (commit `c76d1ed3`, xem `HANDOFF.md`). Luật gốc đầy đủ ở `agent-data/CLAUDE.md`,
> `agent-data/AGENTS.md`; các mục riêng EasyVision (`Common.PropetyTools`, EtherCAT/DS5, BeeCore/BeeGlobal, Pattern,
> `ClassProject.json`, refactor phase `P<x>.<y>`, `CODEX_HISTORY.md`) **không áp dụng** ở đây.

## Thứ tự đọc khi bắt đầu

1. Mục 0 bên dưới (Hard Rules).
2. `head -n 25 docs/agent-lessons/LESSONS.md` — chỉ phần "Top rules", không đọc cả file (L017).
3. Theo task ID: `/eresume <ID>` → phần cuối `tmp/<ID>/deferred.md` + `.claude/task-contract.<ID>.toml`. Mở contract theo ID, không glob.
4. `git status --short`, `git log --oneline -5` (repo không có `CODEX_HISTORY.md`; lịch sử = git log + `deferred.md`).
5. Có tool `memory_*` (agentmemory): `recall` mã task/chủ đề trước, rồi chỉ đọc `deferred.md` + contract theo ID.
   Subagent không có tool memory: coordinator dán `Memory slice` (≤8 dòng) vào brief. Server tắt → luồng file.
   Hook báo "DOWN" chưa phải bằng chứng: chạy `curl -s -m 5 http://localhost:3111/agentmemory/livez` và `ToolSearch` nạp
   `memory_*` một lần trước khi kết luận tắt (L016). Chi tiết: `docs/architecture/agentmemory-rules.md`.
6. Nếu dùng CodeGraph: chỉ đọc bằng `codegraph_explore` / CLI `codegraph explore` (foreground, `CODEGRAPH_NO_DAEMON=1`), không
   giữ `serve --mcp`/daemon chạy nền. Bị lock ("database is locked", "Connection closed"): tìm tiến trình `codegraph`
   (`.codegraph/*.pid`, `Get-CimInstance Win32_Process`), `taskkill /PID <pid> /F`, `codegraph unlock`, `codegraph sync`.
   Chỉ kill tiến trình codegraph do mình tạo (không kill `dotnet`/app của user). Vẫn lỗi → ghi "CodeGraph: unavailable", báo user.

Codex dùng `AGENTS.md` làm điểm vào (lane Codex, audit, build policy) và vẫn phải tuân mục 0 + mục Git ở đây.

---

## Kỷ luật context & token (áp dụng cho mọi agent)

Mỗi request gửi lại **toàn bộ** hội thoại cộng file này; lãng phí context ở lượt đầu bị trả giá ở mọi lượt sau:

1. **Không đọc cả file lớn.** `grep -n` tìm dòng, rồi `sed -n 'A,Bp'`. `deferred.md` append-only: chỉ đọc phần cuối.
2. **Build/test phải ghi log ra file rồi grep**, không in thẳng ra output (một build `/v:normal` ≈ 100 KB ≈ 25k token).
3. **Log/JSON lớn thì viết script lọc**, ghi tổng hợp ra file scratch rồi đọc phần tóm tắt.
4. **Không đọc lại file vừa sửa để "kiểm tra"** — thao tác sửa đã báo lỗi nếu thất bại; build là bằng chứng thật.
5. **Không tạo file doc/changelog/report mà task không yêu cầu.**
6. Trả lời user: kết luận + bằng chứng, bằng tiếng Việt, không dán lại nguyên khối code đã đọc.
   Doc/contract/handoff/brief cho agent viết tiếng Anh.

## Giao việc cho agent (chi tiết: `.claude/skills/eroute/SKILL.md`, `eroute/references/routing-claude.md`)

1. **Không có cổng ngân sách/usage** (user, 2026-10-10): không đọc `statusline.json`, không hỏi `/usage`, không chia việc theo
   hạn mức 5 giờ. Chỉ ghi `tmp/<task-id>/deferred.md` khi user chạy `epause`/yêu cầu dừng.
1a. **Codex lane** (user, 2026-10-17): đọc/trace → Codex luna; sửa đơn giản → Codex terra; sửa logic ngoài vùng native/hardware →
   Codex `gpt-6.1-sol`; plan/gate/ACCEPT/native/safety giữ ở Claude. Một writer tại một thời điểm, Codex không build/git,
   luôn đối chiếu `git diff --stat` với allow-list sau khi Codex ghi.
1b. **Ít subagent Claude, Claude làm việc khó** (user, 2026-10-17): việc tay chân (code theo spec, viết/chạy test, lọc log, trace)
   ưu tiên Codex; leader quyết định cơ cấu agent trong hạn mức chế độ. Không spawn agent cho việc tự làm được trong vài tool call.
   Claude giữ: plan, debug khó, native, thiết kế, review/ACCEPT, build/commit. Số agent theo `.claude/dispatch-mode.txt`
   (`/enormal` 0 subagent Claude, `/emedium` tối đa 5 agent gồm cả Codex, `/emax` theo số phạm vi độc lập, trần 10); thiếu file = normal.
   Nhiều writer chỉ khi mỗi writer có allow-list file riêng, không trùng, không sửa file theo dõi chung.
2. **Dữ liệu tạm ở trong project** (`tmp/`, `artifacts/`), không dùng `%TEMP%`.
3. **Agent chạy quá lâu hoặc ngoài scope → `TaskStop` ngay**, review toàn bộ output theo scope trước khi tiếp tục.

## Bố cục và build

Bố cục repo và lệnh đầy đủ: `docs/architecture/eai-build.md`. Tối thiểu phải nhớ:

- App: `dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release -v:minimal > tmp/build.log 2>&1` → `grep -cE ": error " tmp/build.log`.
  **Chỉ Any CPU**; exe duy nhất `EasyLabel\src\EasyAI.Wpf\bin\Release\net9.0-windows\EasyAI.exe` (`bin\x64` là cũ, không trỏ user vào).
- Test: `dotnet test EasyLabel/EasyLabel.sln -c Release > tmp/test.log 2>&1`; SegAI native cần `EASYAI_SEGAI_DIR` (đã đặt cấp user).
- Native `BeeNativeSegAI/` (C++ v143, x64): chỉ build khi sửa; ABI = 1, đổi struct công khai thì tăng ABI.

---

## 0. Hard Rules Cho Codex/Claude (PHẢI ĐỌC TRƯỚC)

### 0.1 DO — Luôn làm

1. **Phân loại task, xem diff trong scope trước khi gõ code**; theo contract `tmp/`/`.claude/task-contract.<ID>.toml` nếu có.
2. **Event subscribe luôn cặp đôi**: `X.Event -= Handler; X.Event += Handler;` — không có ngoại lệ.
3. **Tự commit việc đã xong trên `main`** theo mục Git sau khi check bắt buộc pass. Một task một commit; task lớn có thể nhiều commit
   theo phần đã xong. Prefix bằng task ID (`[EAI-CC] ...`).
4. **Build/test chỉ khi user cho phép trong task hiện tại.** Đề xuất check hẹp nhất có ích khi nó chứng minh rủi ro thật. Build
   không được phép/không chạy được → ghi `unverified`, không suy ra pass. Commit code cần bằng chứng build mà chưa được phép
   → báo khoảng trống đó trước khi commit.
5. **Move file giữ history**: `git mv`, cập nhật `.csproj` trong cùng commit.
6. **Giữ public API và định dạng đã lưu**: không đổi namespace/tên type, field đã serialize (`.segai`, project/dataset JSON,
   LabelMe, recipe Online, settings) khi chưa thử load một file mẫu cũ.
7. **CLAUDE.md hoặc plan sai/không khả thi**: chỉ sửa khi nằm trong scope được duyệt; nếu không, báo sai lệch và xin đổi scope.
8. **Cập nhật tracking** (`deferred.md`, contract, memory) khi contract cho phép (`authorization.update_tracking=true`) hoặc user yêu cầu.

### 0.2 DON'T — Tuyệt đối KHÔNG

1. **KHÔNG đổi tên symbol public** (class/namespace/method) trong cùng commit với move file.
2. **KHÔNG động vào file ngoài scope task.** Worktree thường dirty — không format/refactor file lạ kể cả khi "thấy ngứa mắt".
3. **KHÔNG revert** file native version/build output tự sinh.
4. **KHÔNG commit** build artifacts: `bin/`, `obj/`, `x64/`, `Debug/`, `Release/`, `*.pdb`, `*.user`, `.vs/`, `EasyLabel/Lib/`.
5. **Không báo đã build/verify nếu không có log thật.** Build chưa được phép thì vẫn là unverified.
6. **KHÔNG gộp 2 task ID** vào 1 commit.
7. **KHÔNG tạo singleton/static state mới ở UI layer**; state đi qua service/ViewModel sẵn có.
8. **KHÔNG xoá view/window (`.xaml`/`.xaml.cs`)** khi chưa migrate xong và chưa pass smoke.
9. **KHÔNG `git stash`** (suýt mất file của session khác); **không `git add .`** — stage đúng file/hunk của task, rồi kiểm HEAD.
10. **KHÔNG đổi struct ABI native** mà không tăng ABI.

### 0.2a Frontend implementation rules (all agents)

- Sửa trên UI thật và đúng luồng tương tác production. Không ship màn hình/control mock, giả lập, placeholder, song song hay tạm.
- Dùng lại hoặc mở rộng layout, style, control sẵn có của EasyAI.Wpf (WPF-UI). Với control thêm mới hoặc sửa đáng kể, không dùng
  control WPF stock khi đã có control/style riêng cùng vai trò. Thành phần production không đáp ứng được trong scope → dừng, xin đổi scope,
  không thay bằng bản xấp xỉ. Không thêm text "tip"/hướng dẫn; bắt chước tool có sẵn.
- Layout đã sửa phải chịu được các kích thước cửa sổ, độ phân giải, DPI/font scale, chữ dài; mọi nội dung và control bắt buộc
  phải nhìn thấy, với tới được, không cắt/chồng; reflow hoặc cho cuộn khi cần.
- Kiểm tra theo cây control thật và, khi được phép chạy, UI render ở các kích thước liên quan. **Không báo UI "đã verify" chỉ từ đọc
  source.** UI smoke: chụp bằng PrintWindow (không CopyFromScreen), đường dẫn truyền vào luôn đặt trong dấu nháy.

### 0.2b Project local temporary data and test evidence

- Mọi dữ liệu tạm/test do agent tạo để trong `D:\CODE\EAI`: script, harness, fixture, download, ảnh chụp, log, binlog, báo cáo,
  kết quả trung gian, checkpoint. `tmp/<task>/` cho scratch, `artifacts/<task>/<run>/` cho evidence; giữ đường dẫn verify có sẵn.
- Không chọn `%TEMP%`, `%TMP%`, thư mục user-profile/Desktop, ổ khác hay workspace cũ. Resolve đường dẫn (kể cả link/junction) và
  xác nhận vẫn nằm trong project trước khi ghi hoặc chạy tool/test.
- Chuyển temp/cache/output của tool vào project trước khi chạy (đặt `TEMP`/`TMP` cho tiến trình khi tool hỗ trợ); không đổi biến
  môi trường toàn máy. Tool không chuyển được an toàn → dừng và báo, không chạy với output bên ngoài. Luật này không cấp quyền build/test.
- Lưu script, input, kết quả, lệnh chính xác, trạng thái quan trọng để resume vào file trong project trước khi kết thúc bước dài/session;
  báo đường dẫn tương đối trong handoff/final. Giữ evidence cũ; không tự xoá/ghi đè để dọn dẹp.

### 0.3 Stop-and-ask — Khi nào BẮT BUỘC hỏi user trước khi tiếp tục

| Tình huống | Hỏi gì |
|---|---|
| Build/test baseline không pass | Blocker nào được skip / tắt project nào |
| File trong scope có thay đổi dirty chồng lấn, không giữ an toàn được | Cách giải quyết chồng lấn; file dirty không liên quan để nguyên |
| Một bước trong CLAUDE.md/plan sẽ phá smoke test hoặc phá load file đã lưu (`.segai`, dataset, project) | Skip / sửa plan / chấp nhận break |
| Cần thêm NuGet package / dependency native mới | Có được phép thêm không |
| Pilot tool tốn > 2 ngày | Đổi tool / dừng / mở rộng timebox |
| Type/field đã serialize sẽ đổi tên/namespace | Có file mẫu để test tương thích ngược không |
| Move/rename tạo > 100 file diff | Có chia commit nhỏ hơn không |

### 0.4 Task flow and pre-flight

Đọc phần cuối `deferred.md`/git log, phân loại yêu cầu, xem diff trong scope trước khi sửa. Giữ file dirty không liên quan, chỉ xử lý
chồng lấn không an toàn. Làm trong scope, chạy static check được phép, chỉ chạy executable check khi task hiện tại cho phép. Cập nhật
tracking và thao tác Git chỉ khi được phép. Báo file đã đổi, bằng chứng, blocker còn lại.

**Audit record** cho task sửa code: task ID, scope, file đã đổi, verify (hoặc lý do skip), giả định compiler (C# 12/net9),
lỗi gốc đầu tiên, rủi ro còn lại. Verify chưa chạy ghi `unverified`, không bao giờ `PASS`.

---

## 12. Git & Commit Workflow

### 12.1 Branch

- Làm tiếp trên nhánh hiện tại, kể cả task lớn trên `main`. Không chuyển nhánh, tạo branch/worktree hay merge chỉ vì task lớn; phải
  có yêu cầu của user. Repo không có remote (lịch sử gốc: https://github.com/BeeEyesAutomation/EasyVision_Unisen).
- Quyền đứng sẵn: coordinator tự commit việc đã xong trên `main` sau khi check, audit, review cần thiết pass. Lệnh "không commit"
  hoặc `authorization.commit=false` trong contract sẽ ghi đè. Nhánh khác cần quyền commit của task hiện tại.
- Task lớn: commit từng phần hoàn chỉnh kèm check/evidence riêng; không trộn task, không commit trạng thái hỏng, không mở rộng scope,
  không đánh dấu cả task xong sớm. Báo hash commit và scope.
- Quyền commit không bao gồm build/test, benchmark, phần cứng, push, PR, tag, deploy. Doc chỉ cần static check; code cần bằng chứng
  compile/runtime thì không tự commit khi bằng chứng đó fail hoặc unverified — chỉ xin quyền còn thiếu.

### 12.2 Commit message

```
[EAI-<ID>] <summary <= 60 chars>

<body — optional, bullet các thay đổi>

Refs: tmp/EAI-<ID>/<plan-or-deferred>.md
```

### 12.3 Push / PR / tag

- Chỉ push, mở PR, tạo tag khi task hiện tại cho phép rõ ràng. Review pass không phải là quyền đó.
- PR được phép: link plan/contract; chỉ đính ảnh UI khi smoke test thật sự được phép và đã chạy.
- Không reset phá hủy khi chưa có quyền rõ ràng và kế hoạch khôi phục đã review.

### 12.4 Dirty worktree

- **Trước task**: `git status`; review và giữ thay đổi trong scope, chỉ hỏi khi chồng lấn không tích hợp an toàn được. File ngoài scope
  để nguyên; chỉ việc chúng tồn tại không phải lý do dừng.
- Chỉ stage file/hunk của task; không `git add .`, không kèm thay đổi có sẵn. Không tách được hunk của mình an toàn → để nguyên, hỏi.
  Tự commit không cho phép commit việc của task trước hay artifact. Sau khi stage theo dòng, kiểm HEAD (build blob của HEAD nếu cần).
- **Sau commit**: còn dirty ngoài scope thì KHÔNG động.

---

## Definition of Done and evidence status

1. **Scope và audit**: diff chỉ chứa việc đã duyệt, giữ thay đổi của user, có audit record (mục 0.4). File dirty không liên quan được phép còn.
2. **Static check**: chạy các check diff, whitespace, conflict-marker, C# 12/nullable, phạm vi khai báo áp dụng được; báo kết quả thật.
3. **Executable check**: build/test/smoke chỉ khi task hiện tại cho phép. Skip → `unverified`, không gọi là pass. Sẵn sàng production phụ
   thuộc bằng chứng đó thì vẫn unverified.
4. **Tracking**: cập nhật `deferred.md`/contract/memory chỉ khi được phép hoặc user yêu cầu.
5. **Git**: việc xong trên `main` kích hoạt cổng tự commit (12.1); việc dở/fail thì không. Thao tác Git khác cần quyền riêng.

Camera/phần cứng (MVS, trigger, Online realtime 100 ms / 20 MP): build pass không đủ cho production. Ghi rõ bằng chứng phần cứng còn nợ
và các case cần đo trong báo cáo cuối hoặc `deferred.md`.

## Dữ liệu ngoài repo

`E:\EAI_TEST` (ảnh OK), `E:\Training\TrainLOGONEW\images` (NG thật), model `.segai` mẫu, model RapidOCR/ONNX — chưa có trên máy này.
Đường dẫn `E:\...` trong memory/handoff/settings có thể phải đổi.

---

## Cập nhật file này

Sửa file này trong scope được duyệt và thêm một dòng Changelog. Giữ file **dưới ~15 KB**; thứ chỉ dùng cho một loại task thì đưa sang
`docs/architecture/` và để lại một dòng trỏ tới.

### Changelog
- 2026-10-21: Tạo bản EAI từ `agent-data/CLAUDE.md` (EasyVision): giữ thứ tự đọc (LESSONS, agentmemory, CodeGraph), kỷ luật token,
  giao việc (Codex lane, dispatch modes, TaskStop), Hard Rules 0.1–0.4, Git 12.x, DoD; bỏ PropetyTools/EtherCAT/BeeCore/refactor phase/
  `CODEX_HISTORY.md`; thêm bố cục EAI, lệnh dotnet/MSBuild, Any CPU, `EASYAI_SEGAI_DIR`, no-stash, UI smoke PrintWindow (user request).
- 2026-10-21: Chuyển bảng bố cục và lệnh build/test chi tiết sang `docs/architecture/eai-build.md` (giữ < 15 KB).
