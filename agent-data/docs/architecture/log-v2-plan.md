# Log V2 — định dạng lưu log cho Agent phân tích (kế hoạch)

Trạng thái: **kế hoạch, chưa duyệt, chưa code**. Chưa có contract ID (cấp khi duyệt).

## 1. Hiện trạng (đã xác minh)

- `BeeGlobal/LogsDashboard.cs`: `LogEntry {Time, Level, Source, Message, IngestionIdentity}`, lưu
  **một mảng JSON** `Logs/logs.json` (+ `.bak`), `SaveNow` **ghi lại toàn bộ file** mỗi chu kỳ,
  cap `_maxPersistedLogs = 50000` (~295 B/bản ghi, ~15 MB).
- `Time` là `"/Date(ms+0700)/"` (DataContractJson), một số bản ghi thiếu offset → mơ hồ múi giờ.
- `Message` là chuỗi tự do; log PERF dùng `key=value` (vd `v=2 navId=4 showReturnMs=871 ...`) nên
  số liệu bị chôn trong text, mỗi lần vẽ biểu đồ phải regex lại.
- `IngestionIdentity` = `runId=.. pid=.. ingestSeq=..` cũng là chuỗi.
- Hệ quả: Agent phải đọc cả file (tốn token), không lọc theo thời gian/source mà không parse hết,
  không append được, cap 50k nên mất lịch sử, ghi lại toàn file gây áp lực LOH (từng làm Ladder
  scan trễ 207 ms — xem comment trong LogsDashboard.cs).

## 2. Mục tiêu → quyết định thiết kế

| Mục tiêu | Quyết định |
| --- | --- |
| Ít tốn chi phí (token/CPU/đĩa) | Agent **không đọc log thô**; đọc `logmap` + rollup + truy vấn có lọc. Bản ghi gọn, gzip khi đóng segment |
| Lập biểu đồ dễ | Parse `key=value` thành **trường có kiểu** lúc ghi; rollup 1 phút (min/avg/p95/max) sẵn dạng bảng → CSV/HTML |
| Lưu trữ lớn | **NDJSON append-only chia segment** theo ngày/kích thước, nén `.gz` segment đã đóng, retention theo dung lượng/ngày — bỏ cap 50k |
| Trích xuất nhanh | Mỗi segment có `tMin/tMax/count/level counts` trong manifest → bỏ qua segment ngoài khoảng; chỉ mở đúng file cần |
| Bản đồ log cho Agent | `logmap.json` + `LOGMAP.md` (mục 4) |

## 3. Định dạng V2

Thư mục: `Logs/v2/` (cạnh `logs.json` cũ, **không đụng** file cũ).

```
Logs/v2/
  logmap.json                 # bản đồ cho Agent (mục 4) — file đầu tiên Agent đọc
  LOGMAP.md                   # hướng dẫn 1 trang + ví dụ lệnh truy vấn
  catalog.json                # đăng ký feat/proc/ev + schema trường + legacyMap (độc lập namespace)
  seg/2026-10-03/000123.ndjson(.gz)
  rollup/1m/2026-10-03.ndjson # tổng hợp theo phút, theo (feat,proc,ev)
```

Một dòng = một bản ghi, khoá ngắn:

```json
{"t":1790866840952,"lv":2,"feat":"runtime","proc":"gc","ev":"gc.sample","src":"PERF.GC","run":"32cd2b32","seq":29,"f":{"windowMs":5005,"g0":35,"g1":15,"g2":6,"heapBytes":69172656},"m":""}
```

- `t`: epoch ms **UTC** (số nguyên). Không còn `/Date()/`.
- `lv`: **chỉ INFO(2) WARN(3) ERROR(4) FATAL(5)**. V2 **không có TRACE/DEBUG**: logger bỏ chúng ngay
  tại cổng vào (không enqueue, không lưu). Mã số giữ nguyên để tương thích log cũ.
- **Phân loại theo tính năng/tiến trình, KHÔNG theo namespace/class** (xem mục 3a). `feat` = tính năng,
  `proc` = tiến trình/giai đoạn nghiệp vụ, `ev` = mã sự kiện ổn định. `src` chỉ còn là
  thông tin tham khảo (tên class/chuỗi gốc), không dùng để lọc hay nhóm.
- `run`/`pid`/`seq` tách khỏi `IngestionIdentity`; `(run,seq)` là khoá dedupe.
- `f`: trường số/bool/chuỗi ngắn đã parse. `m`: phần message còn lại (rỗng nếu parse hết).
- Quy tắc parse: `key=value` tách bằng khoảng trắng; số → number, `true/false` → bool; giá trị
  không chắc kiểu → giữ chuỗi. Bản ghi không khớp mẫu nào: `ev="raw"`, toàn bộ nội dung vào `m` (không mất dữ liệu).
- Segment đóng khi: qua 00:00 hoặc đạt ~8 MB thô; khi đóng → ghi manifest rồi gzip.

## 3a. Phân loại theo tính năng / tiến trình (độc lập namespace)

Vấn đề: `Source` hiện là tên class/namespace (`PERF.Program.Navigation`, `PERF.Tool.0`...). Đổi tên
class, di chuyển file, hoặc refactor phase (P0–P6) sẽ đổi `Source` → biểu đồ và truy vấn của Agent vỡ.

Quy tắc:
1. **Khoá phân loại là `feat` + `proc` + `ev`**, lấy từ bảng đăng ký ổn định `catalog.json`, không suy từ tên type.
   Đổi namespace/class **không được** đổi các khoá này.
2. `feat` (tính năng) — tập đóng, vd: `camera`, `plc`, `ecat`, `ladder`, `vision_tool`, `segment_ai`,
   `ocr`, `project`, `ui_nav`, `startup`, `shutdown`, `license`, `runtime`(GC/CPU), `storage`.
3. `proc` (tiến trình/giai đoạn trong tính năng) — vd `ecat.connect`, `ecat.cyclic`, `camera.grab`,
   `vision.run_once`, `segai.train`, `startup.ui_build`.
4. Thêm tuỳ chọn `corr` (mã tương quan: cycle/frame/job id) để nối các sự kiện cùng một lần chạy xuyên tính năng.
5. **API ghi V2 nhận `feat/proc/ev` tường minh** (hằng số trong một file đăng ký), không tự lấy
   `GetType().FullName`. Điểm gọi cũ chỉ truyền `source` sẽ qua bảng ánh xạ.
6. `src` giữ lại để truy vết về code, nhưng **ngoài khoá lọc**.
7. Bảng ánh xạ chuyển đổi (L0): `legacy Source/mẫu message → feat/proc/ev` nằm trong `catalog.json`
   (`legacyMap`: regex có thứ tự). Bản ghi không khớp → `feat="unclassified"` và được liệt kê trong logmap
   để bổ sung ánh xạ, không bị bỏ.
8. Kiểm tra tự động (L0/L3): mọi `feat/proc/ev` dùng trong code phải có trong `catalog.json`; test fail nếu
   thiếu hoặc nếu có khoá mới mà chưa đăng ký. `catalog.json` có `schemaVersion` và `deprecated` để đổi tên khoá
   có kiểm soát (alias cũ → mới).

## 3b. Cách ly Save / Load / Refresh khỏi Cycle phần mềm (ràng buộc cứng)

Cycle = vòng chạy thời gian thực (Ladder scan, EtherCAT cyclic, vòng chụp/xử lý ảnh). Log chỉ là quan sát,
**không được** làm Cycle chậm, chờ, hoặc đổi hành vi. Đây là bài học đã có: `SaveNow` rewrite 36 MB trên
UI thread từng làm Ladder scan trễ 207 ms.

Quy tắc bắt buộc:
1. **Đường ghi (hot path) chỉ enqueue.** Điểm gọi log trong Cycle chỉ đẩy một struct nhỏ vào hàng đợi
   lock-free/bounded (ring buffer cấp phát sẵn) rồi return. Không I/O, không parse, không serialize, không
   `lock` dùng chung với phần ghi file, không alloc LOH trong hot path.
2. **Tiến trình ghi riêng:** 1 thread nền ưu tiên thấp (`BelowNormal`) làm parse `key=value` → trường,
   serialize, append, đóng/gzip segment, cập nhật manifest/rollup. Gán **affinity tách khỏi core của Cycle**
   (theo `CpuAffinityService`; máy 2 core thì không cho tranh core ECAT/Ladder).
3. **Hàng đợi đầy → bỏ bản ghi, không chặn.** Chính sách (TRACE/DEBUG đã bị loại ở cổng vào): drop INFO trước, giữ WARN+; đếm
   `dropped` và ghi 1 bản ghi `logger.dropped` khi thread nền rảnh. Cycle không bao giờ chờ log.
4. **Save độc lập:** append theo batch (theo thời gian/số dòng), không rewrite toàn bộ. Lỗi đĩa/đầy đĩa/
   segment hỏng → chỉ tắt logger và báo 1 lần, Cycle tiếp tục. Mọi exception bị nuốt trong thread log.
5. **Load độc lập:** đọc theo segment lazy (qua manifest), trên thread nền riêng, đọc bằng chia sẻ
   (`FileShare.ReadWrite`) để không khoá file đang ghi; phân trang, không dựng toàn bộ danh sách trong RAM.
   Không dùng `_ioLock`/`syncRoot` toàn cục (xem lưu ý `EtherCatDevice.syncRoot`).
6. **Refresh độc lập:** UI chỉ nhận snapshot bất biến (đọc từ bộ đệm vòng đã đóng băng hoặc từ file) rồi vẽ
   trong `BeginInvoke` theo lô nhỏ; Refresh không chạm hàng đợi ghi, không giữ lock của writer. Refresh thất bại
   hoặc chậm không ảnh hưởng ghi và Cycle. Tần suất giới hạn (debounce), tạm dừng khi form ẩn.
7. **Công cụ Agent (ev_logq) là tiến trình ngoài**, chỉ đọc file; không bao giờ ghi vào app hay khoá file.
8. **Chuyển đổi (L1) chạy offline**, không bao giờ trong tiến trình app lúc đang chạy.
9. **Giới hạn tài nguyên:** ngân sách CPU/RAM/IO của thread log có trần (vd batch tối đa N MB/s, bộ đệm cố định);
   vượt trần thì drop theo quy tắc 3 chứ không tăng ưu tiên.

Tiêu chí đo (bắt buộc ở L3/L4, cần được cấp quyền chạy):
- Thời gian gọi log trong Cycle: p99 và max (µs), so baseline.
- Jitter Cycle (Ladder scan, ECAT cyclic) khi bật log đầy tải, khi Save/Load/Refresh đang chạy, khi đĩa chậm
  hoặc đầy: không tăng so với baseline tắt log.
- Gen2/LOH count và allocation/giây trước-sau.
- Thử lỗi: rút/khoá thư mục log, đầy đĩa, hàng đợi đầy → Cycle không đổi.
Chưa đo = `unverified`; vì đây là đường realtime, build pass không đủ để coi là sẵn sàng.

## 4. Bản đồ log cho Agent (`logmap.json`)

```json
{
  "schema": "logmap/1",
  "generatedUtc": "...", "tz": "UTC",
  "totals": {"records": 0, "bytes": 0, "tMin": 0, "tMax": 0},
  "runs":    [{"run":"32cd2b32","pid":26336,"startUtc":"...","endUtc":"...","records":0}],
  "segments":[{"file":"seg/2026-10-03/000123.ndjson.gz","tMin":0,"tMax":0,"count":0,"lv":{"WARN":1,"ERROR":0}}],
  "sources": [{"feat":"runtime","proc":"gc","ev":"gc.sample","src":"PERF.GC","count":0,"first":0,"last":0,
               "fields":{"g2":{"type":"int","unit":"count"},"heapBytes":{"type":"int","unit":"B"}},
               "chart":"line","sample":{...}}],
  "unclassified":{"count":0,"topSrc":["..."]},
  "hotspots":[{"kind":"errors|gaps|spikes","feat":"...","tMin":0,"tMax":0,"hint":"..."}],
  "howto":   ["Đọc LOGMAP.md", "Lọc: ev_logq --feat runtime --proc gc --since 10m --field g2 --agg p95"]
}
```

`hotspots` do bộ ghi tính sẵn (ERROR/WARN theo cụm, khoảng trống >N giây, spike p95 vượt ngưỡng) để
Agent đi thẳng tới vùng nghi vấn thay vì quét. `chart` là gợi ý loại biểu đồ theo kiểu trường.

## 5. Công cụ truy vấn (offline, cho Agent)

`tools/LogV2/ev_logq.py` (Python stdlib, không thêm dependency):
`--since/--until`, `--feat`, `--proc`, `--ev`, `--corr`, `--level`, `--run`, `--field F --agg count|min|avg|p95|max --step 1m`,
`--format csv|ndjson|summary`, `--limit`. Dùng `logmap.json` để bỏ qua segment; ưu tiên đọc `rollup/` khi
chỉ cần thống kê. Xuất CSV → biểu đồ (skill `dataviz`) hoặc HTML tĩnh.
Parquet/DuckDB chỉ là **tuỳ chọn** (cần thêm dependency → phải hỏi user, CLAUDE.md §0.3).

## 6. Kế hoạch chuyển đổi (theo phase, mỗi phase một commit)

| Phase | Nội dung | Rủi ro / ràng buộc |
| --- | --- | --- |
| L0 | Chốt schema + `catalog.json` (danh sách `feat/proc/ev` + `legacyMap` Source→feat) khởi tạo từ log thật; bộ mẫu test | chỉ đọc, không đổi runtime |
| L1 | **Converter offline** `tools/LogV2/ev_log_migrate.py`: stream `logs.json` (+`.bak`, các bản `scratchpad`/`outputs`) → `Logs/v2/`; idempotent, dedupe `(run,seq)`, giữ nguyên file cũ | file cũ tới 36 MB: stream, không load cả mảng; ghi vào `tmp/<task>/` khi thử |
| L2 | Sinh `logmap.json`, `LOGMAP.md`, rollup từ V2 + `ev_logq.py` | Agent dùng được ngay trên log cũ đã chuyển |
| L3 | **Writer V2 trong app** (theo mục 3b) (`BeeGlobal`, file mới trong sub-folder đúng concern): hàng đợi → thread nền, append có đệm, **không alloc LOH, không chạm UI thread/Ladder** | Realtime: đây là rủi ro chính; cần đo allocation + Gen2 trước/sau |
| L4 | **Dual-write**: ghi V2 song song `logs.json`; Dashboard vẫn đọc `logs.json` | Không đổi hành vi UI; so khớp số bản ghi V1 vs V2 |
| L5 | Dashboard đọc V2 (Load/Refresh theo mục 3b) (lazy theo segment), bỏ cap 50k, thay `SaveNow` rewrite bằng append; retention | Phải smoke UI; `ClassProject.json` không liên quan |
| L6 | Tắt ghi `logs.json`, giữ converter cho log cũ; cập nhật CLAUDE.md/elog để Agent đọc `logmap` trước | Bước cuối, chỉ sau khi L4/L5 đạt |

L0–L2 chỉ là tool offline (không đụng app) → nên làm trước, cho giá trị ngay.

## 7. Ước lượng (chưa đo, cần xác nhận ở L1)

~295 B/bản ghi hiện tại → NDJSON gọn ~110–140 B thô, gzip thường ~8–12x trên log lặp mẫu như PERF
→ ~12–18 B/bản ghi lưu trữ. 1 triệu bản ghi ≈ 15–20 MB nén. Số này là ước lượng; L1 sẽ báo số đo thật.

## 8. Tiêu chí hoàn thành

- L1: số bản ghi vào = số bản ghi ra + số trùng loại bỏ; 0 bản ghi mất (kể cả `ev="raw"`); chạy lại không đổi kết quả.
- L2: Agent trả lời "tuần này g2 GC p95 theo phút" chỉ bằng `logmap` + 1 truy vấn, không đọc thô.
- L3/L4: không tăng Gen2/LOH so với baseline; số bản ghi V2 = V1 trong cùng khoảng.
- Build/test chỉ khi user cấp quyền cho task hiện tại; chưa có thì ghi `unverified`.

## 9. Quyết định của user (2026-10-03) — đã chốt

1. **Retention theo kiểu History**: cấu hình trong `HistoryPanel` bằng `AdjustBarEx` số ngày (1–1000, như
   `_adjRetentionDays`/`Global.Config.LimitDateSave`), thêm field config riêng cho log (mặc định = `LimitDateSave`),
   và dùng lại `_adjMinimumFreeDisk` làm ngưỡng dung lượng trống tối thiểu; dưới ngưỡng thì xoá segment cũ nhất.
   Việc xoá chạy trên thread log nền (không chạm Cycle), chỉ xoá segment đã đóng, không xoá file đang ghi. Không dùng
   WinForms stock control (CLAUDE.md 0.2a).
2. **Parquet/DuckDB được phép** nhưng chỉ ở **phía công cụ Agent (Python, offline)**: `ev_log_parquet.py` đổi
   segment sang Parquet khi cần phân tích lớn/truy vấn SQL. App không thêm NuGet/native dependency; V2 gốc vẫn
   NDJSON. (Cài `pyarrow`/`duckdb` bằng pip, không nằm trong repo.)
3. **ScreenLogger/EasyScreen: không đưa vào V2.**
4. **Danh sách `feat` đã rà soát** từ 177 chuỗi Source trong code + 50.000 bản ghi thật (xem `catalog.json` v2):
   `runtime, ladder, ecat, plc, io, camera, vision_tool, pattern_match, ai, project, storage, ui, comms, startup`
   (+ `unclassified` làm lưới an toàn). Công cụ kiểm: `python tools/LogV2/ev_log_sources.py` (quét code đã track,
   báo Source chưa có luật). Kết quả: log thật 0 bản ghi lạc, code còn 18 chuỗi là text thông báo bị regex bắt nhầm, không phải Source.
   Ở L3, điểm gọi log chuyển sang truyền `feat/proc/ev` tường minh; bảng `legacyMap` chỉ là cầu nối.

### Changelog
- 2026-10-03: Tạo kế hoạch.
- 2026-10-03: Thêm mục 3a — phân loại theo feature/process, không phụ thuộc namespace (ý kiến user).
- 2026-10-03: Thêm mục 3b — Save/Load/Refresh cách ly khỏi Cycle (ý kiến user).
- 2026-10-03: Bỏ TRACE/DEBUG khỏi V2; plan được duyệt; contract V178 (L3–L6).
- 2026-10-03: Chốt retention kiểu History, cho phép Parquet phía công cụ, bỏ ScreenLogger, rà soát feat (14 feat, ev_log_sources.py).
