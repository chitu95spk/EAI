# CLAUDE.md — Luật làm việc trong repo EasyVision_Unisen

> File này được nạp vào context **mỗi lượt hội thoại**, nên nó chỉ chứa thứ luôn đúng.
> Kế hoạch refactor nhiều phase (roadmap, Task Card P0–P6, code template `Tool.Common`/
> `Tool.Circle`, bảng ánh xạ file) đã chuyển sang
> [`docs/architecture/refactor-plan.md`](docs/architecture/refactor-plan.md) —
> **chỉ mở khi thực sự nhận task có mã `P<phase>.<task>`**.

## Thứ tự đọc khi bắt đầu

1. Mục 0 bên dưới (Hard Rules).
2. `tail -n 200 CODEX_HISTORY.md` — 2–3 mục gần nhất. **Không đọc cả file** (~190 KB).
2a. Có tool `memory_*` (agentmemory): `recall` mã task/chủ đề trước, rồi chỉ đọc `deferred.md` + contract theo ID; bỏ qua `tail`
   nếu recall đã đủ. Subagent không có tool memory: coordinator dán `Memory slice` (≤8 dòng) vào brief.
   Server tắt → quay về luồng file. Chi tiết: `docs/architecture/agentmemory-rules.md`.
   Hook báo "DOWN" chưa phải bằng chứng: chạy `curl -s -m 5 http://localhost:3111/agentmemory/livez` và `ToolSearch` nạp tool
   `memory_*` một lần trước khi kết luận tắt; kiểm tra lại khi user nhắc tới (L016).
2b. `head -n 25 docs/agent-lessons/LESSONS.md` — chỉ phần "Top rules" (15 dòng), không đọc cả file (L017).
2c. CodeGraph bị lock ("database is locked", "Connection closed") do tiến trình cũ treo: không bỏ qua, không đợi.
   Tìm tiến trình giữ nó (`.codegraph/daemon.pid`, `writer.pid`; `Get-CimInstance Win32_Process` lọc `codegraph`), `taskkill /PID <pid> /F`,
   `codegraph unlock`, rồi mở lại bằng `codegraph sync` hoặc chạy lại `codegraph_explore` để đọc/ghi được. Chỉ kill tiến trình
   codegraph (không kill `dotnet`/app của user). Nếu vẫn lỗi, ghi "CodeGraph: unavailable" và báo user.
2d. Đọc xong CodeGraph thì trả kết nối: mỗi agent/subagent chỉ đọc bằng `codegraph_explore` hoặc CLI `codegraph explore`
   (chạy foreground, đặt `CODEGRAPH_NO_DAEMON=1`, để tiến trình tự thoát), không giữ `serve --mcp` hay `daemon` chạy nền,
   không mở DB bằng công cụ khác. Trước khi báo xong, kiểm tra không còn tiến trình `codegraph` do mình tạo
   (`Get-CimInstance Win32_Process` lọc `codegraph`, loại dự án khác) và kill phần còn lại; không để tiến trình
   giữ `.codegraph/codegraph.db` (nguyên nhân "database is locked" khi index/sync).
3. Chỉ khi làm task refactor phase: `docs/architecture/refactor-plan.md`.

Codex dùng `AGENTS.md` làm điểm vào (workflow map-driven) và vẫn phải tuân mục 0 + mục 12 ở đây.

---

## Kỷ luật context & token (áp dụng cho mọi agent)

Mỗi request gửi lại **toàn bộ** hội thoại cộng file này. Việc lãng phí context ở lượt
đầu sẽ bị trả giá lại ở mọi lượt sau, nên:

1. **Không đọc cả file lớn.** Dùng `grep -n` tìm dòng, rồi `sed -n 'A,Bp'` đọc đúng
   khối cần. `CODEX_HISTORY.md`, `.codex-session.md`, `logs.json` đều thuộc loại này.
2. **Build phải ghi log ra file rồi grep**, không để MSBuild in thẳng ra output:
   ```bash
   MSBuild EasyVision.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:normal > build.log 2>&1
   grep -cE ": error " build.log
   grep -E ": error " build.log | head -20
   ```
   Một lần build ở `/v:normal` là ~100 KB; in thẳng ra là mất trắng ~25k token mỗi lần.
3. **Log/JSON lớn thì viết script lọc**, đừng đọc thô. Ghi kết quả tổng hợp ra file
   scratch rồi đọc phần tóm tắt.
4. **Không đọc lại file vừa sửa để "kiểm tra"** — thao tác sửa đã báo lỗi nếu thất bại,
   và build là bằng chứng thật.
5. **Không tạo file doc/changelog/report mà task không yêu cầu.**
6. Khi trả lời user: nêu kết luận + bằng chứng, không dán lại nguyên khối code đã đọc.

---

## Giao việc cho agent (chi tiết: `.agents/skills/eroute/SKILL.md` bước 4a/4b/7a)

1. **Không có cổng ngân sách/usage** (user, 2026-10-10): không đọc `statusline.json`, không hỏi `/usage`,
   không chia việc theo hạn mức 5 giờ. Chỉ ghi `tmp/<task-id>/deferred.md` khi user chạy `epause`/yêu cầu dừng.
1a. **Codex lane** (user, 2026-10-17): đọc/trace → Codex luna; sửa đơn giản → Codex terra; sửa logic ngoài vùng native/hardware →
   Codex `gpt-6.1-sol`; plan/gate/ACCEPT/native/safety giữ ở Claude. Một writer tại một thời điểm, Codex không build/git,
   luôn đối chiếu `git diff --stat` với allow-list sau khi Codex ghi. Chi tiết: `eroute/references/routing-claude.md` "Codex lane".
1b. **Ít subagent Claude, Claude làm việc khó** (user, 2026-10-17): việc tay chân (viết/sửa code theo spec, viết và chạy test,
   lọc log, đọc/trace) ưu tiên giao Codex; leader (main) quyết định cơ cấu agent trong hạn mức của chế độ (Codex hay subagent Claude
   `easyvision-*`, không bắt buộc là Claude). Không spawn agent cho việc tự làm được trong vài tool call. Claude Code giữ: plan, debug khó,
   native/safety, thiết kế, review/ACCEPT, build/commit. Số agent chạy cùng lúc do chế độ trong `.claude/dispatch-mode.txt` quyết định (`/enormal` 0 subagent Claude,
   `/emedium` tối đa 5 agent gồm cả Codex lẫn Claude, thay giới hạn cũ "tối đa 1 subagent Claude", `/emax` theo số phạm vi độc lập, trần 10); thiếu file = normal. Nhiều writer chỉ khi mỗi writer có allow-list
   file riêng, không trùng, không sửa file theo dõi chung; code PDM, executor, solver, release luôn một writer
   (`eroute/references/routing-claude.md` "Dispatch modes").
2. **Dữ liệu tạm ở trong project** (`tmp/`, `artifacts/`), không dùng `%TEMP%`.
3. **Agent chạy quá lâu hoặc ngoài scope → `TaskStop` ngay**, review toàn bộ output theo scope
   trước khi tiếp tục.

## Lệnh build chuẩn

| Việc | Lệnh |
| --- | --- |
| Build full | `MSBuild EasyVision.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal` |
| Build 1 project managed | `MSBuild BeeInterface\BeeInterface.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal` |
| Build native EtherCAT | `MSBuild EtherCatCommunication\EtherCatCommunication.vcxproj /t:Build /p:Configuration=Release /p:Platform=x64` |
| Guard PropetyTools | `bash tools/check_propety_tools.sh` |

MSBuild ở `C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe`.
Baseline warning: xem `docs/architecture/baseline_build.md`. Số warning phải ≤ baseline.

**Lưu ý native**: `EtherCatCommunication.vcxproj` xuất ra `bin/Release/` và post-build mirror
sang `lib/native/x64/Release/`. Hai bản phải cùng timestamp/size sau khi build. Không sửa
struct trong `Ds5Api.h` mà không bump `Ds5NativeConstants.EtherCatApiVersion` — sai lệch ABI
sẽ làm app từ chối nạp DLL.

---

## Bản đồ nhanh (nơi thường phải sửa)

| Vùng | File |
| --- | --- |
| Vòng đời EtherCAT, PLC protocol, connect/reconnect | `BeeGlobal/Protocol/ParaProtocol.cs` |
| Ladder runtime loop, input/scan/output phase | `BeeGlobal/Hardware/HardwareLadderCoordinator.cs` |
| Cầu nối Ladder ⇄ DS5 axis, POSITION_*, servo request | `BeeGlobal/Hardware/KvMl16vUnitBus.cs` |
| Cấu hình motion DS5 (watchdog, WKC, auto-recover) | `BeeGlobal/Hardware/KvMl16vMotionProfiles.cs` |
| Gán CPU core, isolation, Auto Tune | `BeeGlobal/Diagnostics/CpuAffinityService.cs` |
| Wrapper managed cho native master | `BeeEcat/EtherCat/EtherCatDevice.cs`, `Ds5SharedMotionRuntime.cs` |
| Native master, cyclic loop, recovery | `EtherCatCommunication/EtherCatMaster.{h,cpp}` |
| Native DS5 state machine, quick-stop latch | `EtherCatCommunication/DS5/Ds5Controller.cpp` |
| UI trạng thái PLC/Camera, Form cảnh báo | `BeeInterface/Group/View.cs` |
| Form commissioning KV-ML16V | `BeeInterface/PLC/KvMl16vCommissioningForm*.cs` |

---

## 0. Hard Rules Cho Codex/Claude (PHẢI ĐỌC TRƯỚC)

### 0.1 DO — Luôn làm

1. **Đọc `CODEX_HISTORY.md` mục mới nhất** trước khi bắt đầu bất kỳ task nào — để nắm thay đổi gần đây và blocker native.
2. **Phân loại task trước khi gõ code**:
   - *Task refactor phase* (user nêu mã `P<x>.<y>`, hoặc việc di chuyển/tách project): mở `docs/architecture/refactor-plan.md`, lấy Task Card ở mục 5, điền template mục 11.
   - *Task thường* (sửa bug, tối ưu, thêm tính năng, phân tích log): **không** cần Task Card. Vẫn phải theo mục 0.1, 0.2, 0.3 và mục 12 bên dưới.
3. **Dùng helper resolver** cho mọi truy cập tool: `Common.TryGetTool / TryGetToolList / EnsureToolList / SetToolList`. Không bao giờ dùng `Common.PropetyTools[ip][it]` trực tiếp.
4. **Event subscribe luôn cặp đôi**: `X.Event -= Handler; X.Event += Handler;` — không có ngoại lệ.
5. **Automatically commit completed task work on `main` under §12.1 after its required checks pass.** Keep one task per commit; a large task may use several completed-part commits. Use `[P<phase>.<task>] <summary>` for a refactor phase, or the map ID / area prefix for ordinary work.
6. **Build/test only with explicit current-task user authorization.** Recommend the narrowest useful executable check when it proves a material risk. Record an unauthorized or unavailable build as unverified; never infer a pass. If a code commit needs build evidence that was not authorized, report that gap before committing.
7. **Move file giữ history**: dùng `git mv` (CLI) hoặc Solution Explorer (VS). Update `.csproj` ngay trong cùng commit.
8. **Append `CODEX_HISTORY.md` only when task tracking is authorized** (`authorization.update_tracking=true`), or when the user explicitly requests a history update. Record scope, files, verification, blockers, and notes in that existing entry.
9. **Giữ public API cũ** khi tách project: dùng `[assembly: TypeForwardedTo(typeof(...))]` trong shim assembly cũ, **không** đổi namespace của type đã serialize.
10. **If `CLAUDE.md` or the refactor plan is wrong or infeasible**, correct it only when it is in the authorized scope; otherwise report the discrepancy and request a scope revision. When changing the refactor plan, update its existing Changelog. Update `CODEX_HISTORY.md` only under rule 8.
11. **Place shared helpers by concern**: UI in `BeeShared.UI`, non-UI in `BeeGlobal`. Use the refactor plan's namespace map; do not add helpers at the `BeeCore` root.

### 0.2 DON'T — Tuyệt đối KHÔNG

1. **KHÔNG đổi tên symbol public** (class/namespace/method) trong cùng commit với move file cấu trúc. Rename là Phase 6 riêng.
2. **KHÔNG tạo call site `Common.PropetyTools[...]` mới**. Sau commit, regex `\.PropetyTools\[` ở code mới phải = 0 (kiểm tra ngoài `Common.cs`).
3. **KHÔNG động vào file ngoài scope task** đang làm. Worktree thường dirty — không format/refactor file lạ kể cả khi "thấy ngứa mắt".
4. **KHÔNG revert** file native version/build output tự generated.
5. **KHÔNG commit** build artifacts: `bin/`, `obj/`, `x64/`, `Debug/`, `Release/`, `*.pdb`, `*.user`, `.vs/`.
6. **Never claim build verification without a real log.** Follow the current-task build/test authorization gate in `AGENTS.md`; an unauthorized build stays unverified.
7. **KHÔNG gộp 2 phase** vào 1 commit/PR.
8. **KHÔNG động vào `Pattern/Pattern2.*`, `Pattern/Pitch*`** trong Phase 1-2 (đang phát triển song song). Chỉ được thay build flag (như C++17, `HAS_ZXING`).
9. **KHÔNG đổi namespace** của type đã serialize vào `ClassProject.json` nếu chưa test load 1 project mẫu.
10. **KHÔNG tạo singleton/static state mới ở UI layer**. State global đi qua `BeeCore.Domain.Common.*`.
11. **KHÔNG tạo file mới ở root `BeeCore`**. File mới phải vào sub-folder đúng concern.
12. **KHÔNG xoá file Form (`.cs`/`.Designer.cs`/`.resx`)** khi chưa migrate xong sang module mới và chưa pass smoke test.

### 0.2a Frontend implementation rules (all agents)

- Implement frontend changes in the real production UI and interaction path. Do not ship mock, simulated, placeholder, parallel, or temporary screens or controls with a plan to replace them later.
- Reuse or extend the established EasyVision layout and custom controls. For any control added or materially changed in the task, do not use a stock WinForms `CheckBox`, `ComboBox`, `NumericUpDown`, or similar control when an existing custom control serves the same role. If the production component cannot meet the requirement within the approved scope, stop and request a scope revision instead of substituting an approximation.
- Make each changed layout adapt to the supported window sizes, screen resolutions, DPI/font scales, and localized text. Keep all required content and interactive controls visible, reachable, and free of clipping or overlap; reflow or provide scrolling where needed.
- Check the changed layout against the actual production control tree and, when runtime verification is authorized, the rendered UI at relevant sizes and scales. Do not report visual behavior as verified from source inspection alone.

### 0.2b Project local temporary data and test evidence

- Store all agent-controlled temporary/test data inside `E:\Code\EasyVision_Unisen - NoService`: scratch scripts, harnesses, fixtures, downloads, screenshots, logs, binlogs, reports, intermediate outputs, and checkpoints. Use `tmp/<task-or-scope>/` for scratch work and `artifacts/<task-or-scope>/<run>/` for evidence; preserve existing project-local verification paths.
- Never choose `%TEMP%`, `%TMP%`, a user-profile/Desktop folder, another drive, or the older sibling workspace for this data. Resolve output paths, including link/junction targets, and confirm they remain inside this project before writing or launching a tool/test.
- Redirect tool temp/cache/output paths into the project before execution, using task-process settings such as `TEMP`/`TMP` when supported; never change machine-wide environment settings. If a tool/harness cannot be safely redirected, stop and report the limitation instead of running it with external outputs. This rule grants no build/test permission.
- Save resume-critical scripts, inputs, results, exact commands, and status to project files before ending a long step or session; do not rely only on chat or in-memory tool state. Report their relative paths in the task handoff/final, or authorized session tracking. Preserve previous run evidence; no automatic deletion or overwriting to clean up a session.

### 0.3 Stop-and-ask — Khi nào BẮT BUỘC hỏi user trước khi tiếp tục

| Tình huống | Hỏi gì |
|---|---|
| Build baseline (Phase 0) không pass | Blocker nào được phép skip / disable project nào |
| An in-scope file has overlapping pre-existing edits that cannot be preserved safely | Ask how to resolve the overlap; leave unrelated dirty files untouched |
| Một bước CLAUDE.md nếu làm sẽ phá smoke test (ví dụ break load project cũ) | Muốn skip / sửa kế hoạch / chấp nhận break |
| Cần thêm NuGet package / native dependency mới | Có được phép thêm dependency không |
| Pilot tool effort > 2 ngày | Đổi sang tool khác / dừng pilot / mở rộng timebox |
| Phát hiện type public sẽ bị đổi tên/namespace ảnh hưởng `ClassProject.json` | Có project mẫu để test backward-compat không |
| Move/rename file tạo > 100 file diff | Có muốn chia commit nhỏ hơn không |

### 0.4 Task flow and pre-flight

Read the recent history tail, classify the request, and inspect the scoped diff before editing.
Open a Task Card only for a refactor-phase task. Preserve unrelated dirty files and resolve only
unsafe overlapping edits. Implement within scope, run permitted static checks, and run executable
checks only with current-task authorization. Update tracking and perform Git actions only when
authorized. Report changed files, evidence, and remaining blockers. `AGENTS.md` defines the map
workflow, cross-review, and build/test gate.

---

## 12. Git & Commit Workflow

### 12.1 Branch naming

- Continue on the current branch, including large tasks on `main`. Never switch to `main`, create a branch/worktree, or merge solely because a task is large; those actions require the user's request. For requested refactor branches, use `refactor/p<x>-<y>-<slug>`.
- Standing user authorization: the coordinator (or `/fix` root) automatically commits completed task work on `main` after required checks, audit, and applicable review pass. An explicit no-commit instruction or active `authorization.commit=false` overrides it. Other branches require current-task commit authorization.
- For large tasks, commit coherent completed parts with their own checks/evidence; never mix tasks/phases, commit broken intermediate states, widen approved scope/map IDs, or mark the whole task done early. Report commit hashes and scope.
- Commit permission grants no build/test, benchmark, hardware, push, PR, tag, or deployment permission. Documentation needs static checks; code needing compiler/runtime evidence cannot be auto-committed while that evidence is failed or unverified. Request only the missing authorization.

### 12.2 Commit message format

```
[P<x>.<y>] <summary <= 60 chars>

<body — optional, bullet các thay đổi>

Refs: docs/architecture/tasks/YYYY-MM-DD-P<x>.<y>-<slug>.md
```

### 12.3 Khi nào push, khi nào PR

- Push and open a PR only when explicitly authorized for the current task. Neither a completed phase nor a passing review grants that permission.
- For an authorized PR, link the available History entry and Task Card, and include a UI screenshot only when a smoke test was actually authorized and run.

### 12.4 Xử lý dirty worktree

- **Trước khi bắt đầu task**: `git status` → nếu có file dirty:
  - Review in-scope edits and preserve them. Ask only if overlapping edits cannot be integrated safely.
  - Leave out-of-scope files untouched; their presence alone is not a stop condition.
- Stage only task-owned files/hunks; never `git add .` or include pre-existing edits. If owned hunks cannot be isolated safely, leave the work intact and ask for a scope decision. Automatic commits do not authorize committing earlier tasks or artifacts.
- **Sau commit**: nếu vẫn còn dirty (ngoài scope), KHÔNG động.

### 12.5 Tag mốc

- Create a phase tag only when the user explicitly authorizes it.
- Never run a destructive phase reset without explicit user authorization and a reviewed recovery plan.

---


---

## Definition of Done and evidence status

1. **Scope and audit**: The scoped diff contains only approved work, preserves existing user edits, and has the audit record required by `AGENTS.md`. Unrelated dirty files may remain in the worktree.
2. **Static checks**: Run the permitted diff, whitespace, conflict-marker, C# 7.3, and declaration-scope checks that apply. Report their actual result.
3. **Executable checks**: Run a focused build, test, or smoke check only with explicit current-task authorization. If skipped, say `unverified`; never call the result a compile or runtime pass. Production readiness that depends on such evidence remains unverified.
4. **History**: Update `CODEX_HISTORY.md` and map/session tracking only when authorized or explicitly requested.
5. **Git actions**: Completed work on `main` triggers the automatic commit gate in §12.1; incomplete or failed work does not. All other Git actions retain their separate authorization.

For motion/safety work (DS5, EtherCAT, Ladder output), a passing build alone is insufficient for
production readiness. Record missing hardware evidence and required cases in the authorized
history entry, or in the final report when tracking is disabled.

---

## Cập nhật file này

Edit this file within the approved scope and add a short Changelog line. Record the reason in
`CODEX_HISTORY.md` only when tracking is authorized or explicitly requested.

Giữ file này **dưới ~15 KB**. Bất cứ thứ gì chỉ dùng cho một loại task cụ thể thì đưa
sang `docs/architecture/` và để lại một dòng trỏ tới.

### Changelog
- 2026-04-22: Tạo file (kế hoạch refactor đầy đủ, 74 KB).
- 2026-08-28: Tách kế hoạch refactor (mục 1–7, 9–11, 13) sang `docs/architecture/refactor-plan.md`;
  giữ lại Hard Rules, Git workflow, DoD. Thêm mục "Kỷ luật context & token", lệnh build chuẩn,
  bản đồ file nhanh. Phân biệt rõ *task thường* và *task refactor phase* — trước đây mục 0 mặc
  định mọi task đều là Task Card nên agent bị buộc làm thủ tục thừa cho việc sửa bug lẻ.
  74 KB → 13 KB, tiết kiệm ~15k token mỗi lượt hội thoại.
- 2026-10-03: Aligned build, tracking, Git, and dirty-worktree gates with task authorization;
  clarified static versus executable evidence and preserved production frontend rules.
- 2026-10-03: Authorized automatic completed-task commits on main and completed-part commits for large tasks; preserved verification, scope, and separate execution/push permissions.
- 2026-10-03: Added "Giao việc cho agent" (token-budget gate vs 5h limit, deferred-work file, runaway-agent stop).
- 2026-10-03: Required project-local temporary/test data and durable session evidence; prohibited automatic external scratch storage and evidence cleanup.
- 2026-10-04: Reading order: probe agentmemory before claiming it is down (L016) and read LESSONS Top rules at session start (item 2b, L017); approved by user via /eskill.
- 2026-10-17: Added reading-order item 2d: agents release CodeGraph after reading (no background serve/daemon, kill own leftovers) so index/sync is never locked (user request).
- 2026-10-17: Added reading-order item 2c: kill the stale CodeGraph process and unlock when CodeGraph is locked or disconnected, then reopen (user request).
- 2026-10-17: Added "Giao việc" item 1b: Codex takes manual coding/testing, fewer Claude subagents (max 1 at a time), Claude Code keeps hard work (user request).
- 2026-10-17: Added dispatch modes (`/enormal`, `/emedium`, `/emax`; `.claude/dispatch-mode.txt`) replacing the fixed "max 1 Claude subagent" cap in item 1b (user request).
- 2026-10-10: Removed the token-budget/usage gate from agent rules (user decision); eroute 4a and eresume step 4 updated.
