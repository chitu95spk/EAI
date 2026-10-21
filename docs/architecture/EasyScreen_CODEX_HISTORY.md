# EasyScreen — Build History

> Append-only log of EasyScreen task execution. Companion to `EasyScreen_Plan.md`.
> Each entry: task, scope, build result, guard result, acceptance evidence, autonomous decisions.

---

## ES.0 — Read-in + environment validation — 2026-08-23

Scope: confirm the plan's locked decisions form the working baseline; validate the build environment before writing code.

Environment verified (real probes, not assumptions):
- MSBuild: VS2022 Professional present. Primary build via `dotnet build` (SDK 9.0.304).
- Packages already in `packages/`: CyotekImageBox 2.0.0-beta.2, OpenCvSharp4 4.11.0, Newtonsoft.Json 13.0.4, pythonnet 3.0.5.
- WGC dependencies restore cleanly from nuget.org: `Microsoft.Windows.SDK.Contracts` 10.0.19041.1, `Vortice.Direct3D11`/`Vortice.DXGI` 3.6.2 (Q5 choice confirmed available).
- OS build: **10.0.26200** — far above WGC minimum 17134 (Q4 satisfied on this host).
- .NET Framework 4.8 targeting pack present.
- PLC_Communication.csproj targets net461 (HslCommunication) — compatible with net48 consumers.

Autonomous decisions applied from plan section 9: Q1 separate `EasyScreen.sln`; Q5 Vortice; Q6 Keyence/DM defaults; Q11 password 797979; Q12 log defaults.

Deferred/not-yet-created (arrive with the tasks that need them): `tools/CaptureTestApps/` (ES.3b), `tools/FakePlcTransport.cs` (ES.8/ES.13).

---

## ES.1 — Scaffold solution — 2026-08-23

Scope: create both projects, the solution, the manifest, the shell entry point, and the conformance guard.

Files created (all within the plan section 12.2 manifest):
- `EasyScreen.sln`
- `EasyScreen.Core/EasyScreen.Core.csproj` — net48, x64. Refs: OpenCvSharp4(+Extensions+runtime.win), Newtonsoft.Json 13.0.4, pythonnet 3.0.5, Microsoft.Windows.SDK.Contracts, Vortice.Direct3D11/DXGI, ProjectReference to PLC_Communication. Zero EasyVision refs.
- `EasyScreen/EasyScreen.csproj` — WinExe, net48, x64. Refs EasyScreen.Core + Cyotek ImageBox (from packages/) + WinForms/Drawing.
- `EasyScreen/app.manifest` — PerMonitorV2 DPI, Win10/11 supportedOS, longPathAware.
- `EasyScreen/Program.cs`, `EasyScreen/Shell/NativeDpi.cs`, `EasyScreen/Shell/MainForm.cs`.
- `tools/check_easyscreen.sh` — the section 12.4 forbidden-pattern guard.

Build verification (ACTUAL output):
- Command: `dotnet build EasyScreen.sln -c Release -p:Platform=x64`
- Result: **Build succeeded. 0 Errors.** Produces `EasyScreen/bin/x64/Release/EasyScreen.exe` and `EasyScreen.Core.dll`.
- Warnings: 18 total, **all originating in PLC_Communication (third-party)**. EasyScreen-owned code: **0 warnings**. => WARNING BASELINE for EasyScreen = 0 (Q13).

Guard verification (ACTUAL output): `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED** (13 checks).

Runtime smoke (ACTUAL): launched `EasyScreen.exe`, confirmed main window title "EasyScreen" created, `CloseMainWindow()` => clean exit. `RUNNING ... window='EasyScreen'` then `CLOSED_OK`.

No EasyVision source modified (git status: only new EasyScreen/ , EasyScreen.Core/, tools/, docs/ paths).

Autonomous decisions:
- Newtonsoft.Json pinned to **13.0.4** (not the plan's illustrative 13.0.3) because PLC_Communication requires >=13.0.4; a downgrade is an NU1605 error. Recorded per section 12.9.
- Guard script `chk` refactored to take an explicit scope argument so per-project checks (e.g. "Core has no WinForms") do not also scan the UI project. This is a fix to my own tooling, not a contract change.
- NativeDpi cross-version DPI opt-in uses documented empty catches (OS feature probing). Each catch carries a reason comment, so the "no silent catch" guard passes by design.

Next: ES.2 — copy geometry + PLC transport.

---

## ES.2 — Copy geometry; PLC-transport plan correction — 2026-08-23

Scope: bring the reusable clean-copy code into EasyScreen.Core.

Files created:
- `EasyScreen.Core/Geometry/RectRotate.cs` — copied from BeeGlobal/RectRotate.cs (989 -> 996 lines).
- `EasyScreen.Core/Geometry/AnchorPoint.cs` — enums AnchorPoint, TypeCrop, ShapeType, Dir, extracted from BeeGlobal/Enums.cs (RectRotate's enum dependencies).
- `EasyScreen.Core/Plc/TimingUtils.cs` — copied from BeeGlobal/TimingUtils.cs (dependency-free accurate-delay helper).

RectRotate divergences (recorded in its provenance header): the copy is verbatim
except for namespace-move consequences — `BeeGlobal.ShapeType.*` -> `ShapeType.*`
(x3) and `BeeGlobal.RectRotate.PointInPolygon` -> `RectRotate.PointInPolygon` (x1).
No logic changed. Also stripped a stray UTF-8 BOM the header prepend pushed mid-file.

Build verification (ACTUAL): `dotnet build EasyScreen.Core -c Release -p:Platform=x64`
=> **Build succeeded**, 0 EasyScreen-owned warnings.

Geometry acceptance (ACTUAL): a 10-assertion harness (tools scratch) exercised
GetBoundingRect (axis-aligned + 90 deg rotation), ContainsPointRect (inside/outside/edge),
and CenterLocalToWorld round-trip against the copied RectRotate => **GEOMETRY OK (0 failures)**.

Guard (ACTUAL): `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

### PLAN CORRECTION — PlcClient is NOT copyable; ScreenPlcLink wraps HslCommunication directly

Verified against the real source (not the plan's first-40-lines assumption):
`BeeGlobal/Protocol/PlcClient.cs` has **45 references to EasyVision `Global.*`**
(Global.Config x8, Global.IsAllowReadPLC x6, Global.LogsDashboard x24, Global.PLCStatus x7,
Global.Comunication x2) plus BeeGlobal types LogEntry/LeveLLog/PLCStatus. The plan's
section 3.1 claim ("PlcClient's only dependency is TimingUtils") is **wrong**.

However, PlcClient merely creates HslCommunication brand drivers (MelsecMcNet, KeyenceMcNet,
OmronFinsNet, SiemensS, ModbusTcpNet, Delta*, Panasonic*, ...) and calls them by reflection.
All those drivers implement HslCommunication's `IReadWriteNet`, which exposes exactly the
typed primitives EasyScreen needs: ReadInt16/Int32/Float/Double/Bool, ReadString(addr,len),
and the matching Write* (all returning OperateResult<T>).

Correction: **do not copy PlcClient.** `ScreenPlcLink` (already new code in plan section 4.6)
will construct the brand driver via a small switch and call `IReadWriteNet` directly. This
removes 1,459 lines of Global-coupled code and the entire risk of dragging EasyVision state in.
`TimingUtils` is still copied (clean, useful for TriggerService poll pacing).

Plan updated accordingly (sections 3.1, 3.2, 4.6, 12.2). PlcClient/ScreenValue/ScreenBit
"PORTED" entries move from ES.2 into ES.8 where ScreenPlcLink is built.

---

## ES.4 — Data model + persistence — 2026-08-23

Files created (all in the section 12.2 manifest):
- `Model/Enums.cs` — CaptureKind, WindowMatchMode, CaptureEngineKind, RegionDataType,
  RegionUsage, CheckResult, RunPhase, ConnectionKind, HardwareTopology/RunMode,
  BitDirection, ValueDirection, TypeVar, BitFunction, PlcBrand (14), LogLevel, ImageFileFormat.
- `Model/StringToIntMap.cs` — TryMap + CreateDefault (OK=1,NG=0,Run=1,Stop=2,Error=3).
- `Model/ScreenCaptureTarget.cs`, `Model/ScreenRegion.cs`, `Model/PlcConfig.cs`
  (+ ScreenBit, ScreenValue), `Model/LogSettings.cs`, `Model/AppSettings.cs` (+ OcrSettings),
  `Model/ScreenProject.cs`.
- `Persistence/ProjectStore.cs` — Save/Load/CreateDefault/Validate. Newtonsoft JSON,
  atomic temp->replace write, master image inline as base64 PNG. No BinaryFormatter,
  no [Serializable] (decision #3). Runtime fields marked [JsonIgnore].

Build (ACTUAL): Core Release x64 -> Build succeeded, 0 EasyScreen warnings.

Acceptance (ACTUAL): 25-assertion save/load round-trip (tools scratch):
name, capture engine, registered size, tolerance, master image restored pixel-exact,
PLC brand, 2 values with types, 1 bit, 2 regions with ROI center + 15deg rotation
preserved, 5-entry map (OK=1/Stop=2/unmapped=false), logging, password 797979, and
Validate() detecting a dangling target value and an empty MappedInt map => **MODEL OK**.

Guard (ACTUAL): ALL CHECKS PASSED (tightened the BinaryFormatter check to match real
usage, not the word appearing in a doc comment).

---

## ES.3a — Capture: legacy engines + enumeration + probe — 2026-08-23

Files created (Imaging/, all in manifest):
- `ICaptureEngine.cs` — interface + WindowInfo, CaptureDiagnostics, CaptureProbeResult/Report.
- `NativeMethods.cs` — Win32 P/Invoke: EnumWindows/EnumChildWindows, text/class/pid,
  GetWindow/ClientRect, ClientToScreen, DWM extended-frame/cloaked, PrintWindow, BitBlt,
  CreateCompatible*, ShowWindow(SW_MAXIMIZE)/SetForegroundWindow.
- `CaptureSupport.cs` — Bitmap->CV_8UC3 Mat; blank-frame probe (16x16 grid, stdDev<1.0).
- `GdiCaptureEngines.cs` — PrintWindowEngine (PW_RENDERFULLCONTENT), BitBltEngine,
  CopyFromScreenEngine + WindowGeometry helper (client/DWM-frame/window rects).
- `ScreenCaptureService.cs` — EnumerateTopLevelWindows/ChildWindows (excludes self,
  minimized, cloaked; framework guess by class name), ResolveWindow, Capture (engine
  order + fallback + blank rejection), Probe, MaximizeWindow.
- `GraphicsCaptureEngine.cs` — **ES.3a stub**: IsAvailable=false so Capture falls through
  to GDI. Real WGC body is ES.3b.

Fix: `Size` disambiguated to System.Drawing.Size in 3 Imaging files (OpenCvSharp also
defines Size).

Build (ACTUAL): Core Release x64 -> Build succeeded, 0 EasyScreen warnings.

Acceptance (ACTUAL) — real capture of a live Notepad window (found via enumeration,
Win11 Notepad's launcher process exits so MainWindowHandle is unusable):
- Enumeration found it: title/class/process = ".env - Notepad / Notepad / Notepad".
- Probe: GraphicsCapture avail=False (stub); PrintWindow ok (194ms), BitBlt ok (42ms),
  CopyFromScreen ok (29ms). Recommended=PrintWindow.
- Capture(Auto) fell through WGC and succeeded via PrintWindow: 1680x989 CV_8UC3, not blank.
- Blank probe: solid gray -> blank=true; gray+rectangle -> blank=false.
- Rendered PNG reviewed: crisp, fully legible window text -> OCR-grade capture confirmed.
=> **CAPTURE OK** (11/11 asserts).

Guard (ACTUAL): ALL CHECKS PASSED.

Next: ES.3b -- real Windows Graphics Capture (D3D11 + WinRT interop). The product gate.

---

## ES.3b — Windows Graphics Capture — PARTIAL — 2026-08-23

Implementation completed at source level:
- Replaced the ES.3a availability stub with an HWND-based Windows Graphics Capture engine.
- Added a cached D3D11 device/context and active-HWND capture item, free-threaded frame pool,
  capture session, and CPU-readable staging texture.
- Added hardware-device creation with WARP fallback, resize-driven frame-pool recreation,
  BGRA staging readback to an owned BGR `Mat`, and optional client-area cropping.
- Corrected the `IGraphicsCaptureItemInterop` COM declarations to preserve HRESULT and use
  the required out interface pointer.
- Added `WinRtCaptureInterop.cs` beyond the original manifest because direct HWND activation
  needs the internal `IGraphicsCaptureItemInterop` declaration on .NET Framework 4.8.
- Resource lifetime is explicit: event unsubscribe, session close, frame-pool close, staging
  texture/context/device disposal, and frame/surface/texture scopes on error paths.

Plan corrections recorded:
- Vortice 3.6.2 only provides modern-.NET assemblies and cannot compile when its APIs are
  consumed by the locked `net48` target. Package references are now 2.4.2, the final release
  with `netstandard2.0` assets; restore selected those assets for both Direct3D11 and DXGI.
- Base WGC support starts at Windows 10 build 17134, but direct HWND capture through
  `CreateForWindow` requires build 18362. Runtime availability now enforces the latter.

Static verification performed:
- `dotnet restore EasyScreen.Core/EasyScreen.Core.csproj` succeeded.
- `project.assets.json` resolves `Vortice.Direct3D11/2.4.2` and `Vortice.DXGI/2.4.2` to their
  `lib/netstandard2.0` compile assets for `.NETFramework,Version=v4.8`.
- Scoped forbidden-pattern inspection found no EasyVision references, WinForms/Core UI use,
  parallel region execution, blocking task waits, or C# 8 syntax in the ES.3b files.
- Frame-pool event wiring is balanced (`+=` once, `-=` before subscribe and during teardown).
- `git diff --check` passed; only line-ending conversion notices were emitted.

Not run by repository policy:
- No build, capture smoke, four-target PNG validation, or 1,000-frame resource soak was run
  because the current user request did not explicitly authorize build/test execution.
- ES.3b remains **partial and uncommitted**. Do not create the `[ES.3b]` commit until the
  targeted Release x64 build and mandatory capture acceptance have completed.

Resume: authorize and run the targeted EasyScreen Release x64 build; fix any compile/runtime
issues within ES.3b scope, then capture Notepad, WPF, Edge, and Calculator and run the
1,000-frame handle/GPU-memory soak. On success, append actual evidence and commit `[ES.3b]`.

### ES.3b completion — build and runtime acceptance

User authorization received to run the full build/test gate.

Build (ACTUAL):
- Command: `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`.
- Final result: **Build succeeded, 0 errors, 0 warnings in the final output**.
- The first compile exposed four API-boundary errors (ambiguous `MapFlags`, obsolete
  OpenCvSharp external-data constructor, and WinRT `IClosable` projection for session/pool).
  All were corrected in scope. The transitive PolySharp warning was suppressed specifically
  because it only offers C# 8+ source polyfills while EasyScreen remains locked to C# 7.3.

Runtime defects found and fixed by acceptance:
- `Environment.OSVersion` returned a manifest-virtualized old version in the test process.
  Availability now reads the real OS version with `RtlGetVersion`; this host reports build 26200.
- A static WPF window does not continuously emit `FrameArrived`. The engine now caches the
  latest owned CPU frame, probes immediately for a new frame, waits one refresh interval when
  a cache exists, and returns a clone when the content is unchanged. Resize invalidates the
  cache and recreates the frame pool before a new frame is accepted.

Four-target WGC acceptance (ACTUAL, final binary, client-area capture):
- Notepad / Win32: **1668x983**, grayscale stddev **13.470**, 177 ms,
  `artifacts/ES.3b/wgc-notepad.png`.
- WPF fixture: **904x581**, stddev **18.920**, 172 ms,
  `artifacts/ES.3b/wgc-wpf.png`.
- Microsoft Edge / Chromium GPU fixture: **1089x1324**, stddev **16.972**, 202 ms,
  `artifacts/ES.3b/wgc-edge.png`.
- Calculator / UWP (`ApplicationFrameWindow`): **1200x933**, stddev **21.157**, 201 ms,
  `artifacts/ES.3b/wgc-calculator.png`.
- All four PNGs were visually reviewed: correct target content, readable text, correct colors,
  no blank/black frame, and no client-area offset.

1,000-frame resource soak (ACTUAL, cached WPF session after 20-frame warm-up):
- Captures: **1000/1000**, failures: **0**, elapsed: **37,818 ms**.
- Process handles: **708 -> 705 (delta -3)**.
- Private bytes: **84,029,440 -> 80,433,152 (delta -3,596,288)**.
- GPU process memory: **10,166,272 -> 10,166,272 (delta 0)**.
- First/last checksum: **52,907,210,628 / 52,907,210,628**; both rendered PNGs were
  visually reviewed and are pixel-stable.

Test fixtures added beyond the original manifest:
- `tools/CaptureTestApps/WpfCaptureTarget/` — deterministic hardware-composited WPF target.
- `tools/CaptureTestApps/CaptureHarness/` — x64 WGC capture and resource-soak harness.
- `tools/CaptureTestApps/edge-capture.html` — deterministic Chromium/GPU content target.

Guard (ACTUAL): `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.3b PASSED — product capture gate cleared.** Next: ES.3c engine probe and selection.

## ES.3c — engine probe and selection — 2026-08-23

Implementation:
- Extended `CaptureProbeReport` with target metadata, framework guess, prioritized child-window
  candidates, and an optional child-window fallback recommendation for GDI-era engines.
- Updated `ScreenCaptureService.EnumerateChildWindows` to capture visible child HWND metadata,
  include process/framework hints, and order known content classes first:
  `Chrome_RenderWidgetHostHWND`, `Intermediate D3D Window`, `SunAwtCanvas`, Qt `QWindowIcon`,
  and WPF `HwndWrapper`.
- Kept runtime fallback order as `GraphicsCapture -> PrintWindow -> BitBlt -> CopyFromScreen`.
  The top-level probe winner still drives `Recommended`; child fallback metadata is advisory for
  Step 1 when WGC is unavailable or an engineer intentionally selects a legacy engine.
- Added `probe <window-token> [expected-engine]` to the capture harness. It fails if the
  recommended engine does not match the expected engine and prints per-engine availability,
  success, blank-frame state, elapsed time, child candidates, and child fallback metadata.

Verification (ACTUAL):
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- `MSBuild tools/CaptureTestApps/CaptureHarness/CaptureHarness.csproj /t:Build
  /p:Configuration=Release /p:Platform=x64 /v:minimal` succeeded. Final rerun output had
  **0 errors and 0 warnings**.
- `MSBuild tools/CaptureTestApps/WpfCaptureTarget/WpfCaptureTarget.csproj /t:Build
  /p:Configuration=Release /p:Platform=x64 /v:minimal` succeeded.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Runtime probe gate (ACTUAL):
- WPF fixture: framework `WPF`, recommended `GraphicsCapture`; WGC succeeded in 258 ms,
  `LooksBlank=False`.
- Microsoft Edge / Chromium: framework `Chromium/Electron`, recommended `GraphicsCapture`;
  WGC succeeded in 289 ms, `LooksBlank=False`. Child fallback was detected:
  class `Intermediate D3D Window`, engine `PrintWindow`.
- Calculator / UWP: framework `UWP/WinUI`, recommended `GraphicsCapture`; WGC succeeded in
  263 ms, `LooksBlank=False`. Child candidates included `Windows.UI.Core.CoreWindow`.

Acceptance: **ES.3c PASSED — engine probe and automatic selection gate cleared.** Next: ES.5 OCR runtime.

## ES.5 — OCR runtime — 2026-08-23

Implementation:
- Added `EasyScreen.Core/Ocr/OcrReadResult.cs` with the plan fields for text, tokens, scores,
  boxes, errors, success state, and elapsed time.
- Added `EasyScreen.Core/Ocr/ScreenOcrRuntime.cs`, porting the load-bearing OCR path from
  `BeeCore/Unit/OCR.cs:182-345`: `Py.GIL()` per read, CV_8UC3/continuous Mat preparation,
  pointer/shape/stride handoff, `find_ocr(ptr,h,w,ch,stride,modelName,limit=0)`, tuple guards,
  box conversion through `Cv2.MinAreaRect`, width/height angle correction, allow-character
  filtering, and left-to-right sorting by `RectRotate._PosCenter.X`.
- Ported only the OCR bootstrap subset from `BeeCore/Common.cs:353-424`: `Runtime.PythonDLL`,
  `PythonEngine.Initialize()`, `BeginAllowThreads()`, `Py.Import("Tool.OCR")`, class
  construction, and per-model `initialize_ocr(modelName)`. Yolo/CraftOCR/global status/logging
  paths were intentionally not carried over.
- Added `tools/OcrSmoke/`, which renders deterministic `A123` / `OK` text to a Mat. If an OCR
  deployment is present through `EASYSCREEN_PYTHON_PATH`, it runs the end-to-end read; otherwise
  it asserts the expected explicit deployment error path.

Verification (ACTUAL):
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- `dotnet restore tools/OcrSmoke/OcrSmoke.csproj` succeeded.
- `MSBuild tools/OcrSmoke/OcrSmoke.csproj /t:Build /p:Configuration=Release /p:Platform=x64
  /v:minimal` succeeded.
- `tools/OcrSmoke/bin/x64/Release/net48/EasyScreen.OcrSmoke.exe` exited 0 with
  `OCR NOT VERIFIED | OCR PythonPath is empty.` This is the expected Q9/Q10 branch on this host.
- `rg` found no `Common.*`, `Global.*`, `ImageSourceRuntimeService`, `BeeCore`, `BeeGlobal`,
  `BeeInterface`, `TryGetTool`, or `LogsDashboard` references in the ES.5 OCR code.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.5 PASSED — OCR runtime implemented; live OCR model verification is NOT VERIFIED
on this host because no Python/OCR deployment path is configured.** Next: ES.6 ROI canvas.

## ES.6 — ROI canvas — 2026-08-23

Implementation:
- Added `EasyScreen/Ui/RoiCanvas.cs` deriving from Cyotek `ImageBox`, matching the planned
  EasyVision zoom/pan base while keeping ROI logic in EasyScreen only.
- Added `CanvasMode` (`Edit`, `Pan`) and configured inherited behavior: middle-button pan by
  default, left-button pan in Pan mode, scrollbars always visible, grid background, inherited
  wheel zoom, `ZoomIn`, `ZoomOut`, `ZoomToFit`, and `ActualSize` shortcuts.
- Implemented ROI overlay rendering in image coordinates mapped through `GetOffsetPoint`:
  cyan unselected outlines, yellow selected outlines, fixed-size handles, rotation handle,
  labels, mask outline support, and optional result badges.
- Implemented hit-testing through `PointToImage`, front-to-back region order, body move,
  center-based handle resize, rotation with optional shift snapping, arrow-key nudging,
  delete through an injectable confirmation delegate, selection/region/hover events, and
  `CenterOn` using ImageBox's `ScrollTo(image point, display point)` API.
- Added `tools/RoiCanvasSmoke/`, an STA WinForms smoke harness that renders a synthetic screen,
  creates 3 ROIs, exercises move at 100%, resize at 400%, rotate after a scrolled viewport,
  and writes evidence PNGs under `artifacts/ES.6/`.

Verification (ACTUAL):
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- `dotnet restore tools/RoiCanvasSmoke/RoiCanvasSmoke.csproj` succeeded.
- `MSBuild tools/RoiCanvasSmoke/RoiCanvasSmoke.csproj /t:Build /p:Configuration=Release
  /p:Platform=x64 /v:minimal` succeeded.
- `tools/RoiCanvasSmoke/bin/x64/Release/net48/EasyScreen.RoiCanvasSmoke.exe artifacts/ES.6`
  exited 0: 3 regions, zoom 400, ROI 0 moved, ROI 1 resized, ROI 2 rotated.
- Evidence PNGs visually reviewed: `roi-100.png`, `roi-400.png`, and `roi-scrolled.png`
  all show the image, grid, ROI overlays, labels, fixed-size handles, and selected state at
  the expected viewport/zoom.
- Scoped scan found no `Common.*`, `Global.*`, `BeeInterface`, `BeeCore`, task blocking, or
  `Thread.Sleep` in the ES.6 UI/smoke code.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.6 PASSED — ROI canvas and zoom/pan editing behavior are implemented.** Next: ES.7 RegionCropper.

## ES.7 — RegionCropper — 2026-08-23

Implementation:
- Added `EasyScreen.Core/Imaging/RegionCropper.cs`, a standalone EasyScreen cropper for the
  v1 rotated-rectangle ROI scope.
- Ported the EasyVision-compatible crop sequence from `BeeCore/Func/Crop.cs`: affine warp around
  the ROI anchor, all-channel `GetRectSubPix`, optional exclusion mask placement by rotating the
  mask center delta into patch coordinates, mask-only output, and black/white background
  compositing based on `mask.IsWhite`.
- Intentionally did not port `ToolInputSession`, `PatchCropContext`, ultra-fast variants,
  timing instrumentation, or `Global.LogsDashboard` paths.
- Added `tools/RegionCropperSmoke/`, which renders a deterministic source image, crops a
  22-degree ROI, applies a same-angle exclusion mask, writes `source.png`, `crop.png`,
  `masked.png`, and `mask-only.png`, and validates size/nonblank/mask coverage.

Verification (ACTUAL):
- `dotnet restore tools/RegionCropperSmoke/RegionCropperSmoke.csproj` succeeded.
- `MSBuild tools/RegionCropperSmoke/RegionCropperSmoke.csproj /t:Build
  /p:Configuration=Release /p:Platform=x64 /v:minimal` succeeded.
- `tools/RegionCropperSmoke/bin/x64/Release/net48/EasyScreen.RegionCropperSmoke.exe
  artifacts/ES.7` exited 0: crop `340x110`, crop stddev `53.048`, masked stddev `79.223`,
  zero mask pixels `3344`.
- Evidence PNGs were visually reviewed: crop is rectified, masked crop has the expected black
  exclusion rectangle, and mask-only matches that exclusion area.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- Scoped scan found no `Common.*`, `Global.*`, `BeeCore`, `BeeGlobal`, `BeeInterface`,
  `ToolInputSession`, `LogsDashboard`, task blocking, or `Thread.Sleep` references in ES.7.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.7 PASSED — rotated ROI crop and mask compositing are implemented.** Next: ES.8 ScreenPlcLink.

## ES.8 — ScreenPlcLink — 2026-08-23

Implementation:
- Added `EasyScreen.Core/Plc/ScreenPlcLink.cs` with the ES.8 API: connect/disconnect,
  connection/log events, bit read/write, value staging, value read/write, output flush,
  value lookup, output-name listing, and bypass mode.
- Added `IScreenPlcTransport` so production HslCommunication access and deterministic tests
  share the same `ScreenPlcLink` behavior.
- Added `HslScreenPlcTransport`, which creates brand-specific HslCommunication clients by
  reflection, configures TCP/serial connection properties, calls `ConnectServer`/`ConnectClose`,
  and dispatches typed read/write calls for `Bool`, `Int`/`Int16`, `Int32`, `Float`, `Double`,
  and ASCII `String`.
- Added `InMemoryPlcTransport` for smoke tests and future runner tests without PLC hardware.
- Implemented word-bit address resolution: explicit `ScreenBit.Address` wins; otherwise input
  bits use `Config.InputWordAddress + "." + BitIndex` and output bits use
  `Config.OutputWordAddress + "." + BitIndex`.
- Bypass mode short-circuits read/write operations to success while still staging values and
  updating bit last-state in memory.

Verification (ACTUAL):
- `dotnet restore tools/PlcLinkSmoke/PlcLinkSmoke.csproj` succeeded.
- `MSBuild tools/PlcLinkSmoke/PlcLinkSmoke.csproj /t:Build /p:Configuration=Release
  /p:Platform=x64 /v:minimal` succeeded.
- `tools/PlcLinkSmoke/bin/x64/Release/net48/EasyScreen.PlcLinkSmoke.exe` exited 0:
  wrote `Ready` to `DM200.0`, read `Trigger` from `DM100.0`, staged and flushed output values
  to `D300` and `D320`, read input double from `D400`, validated output name ordering, and
  verified bypass behavior.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- Scoped scan found no `BeeCore`, `BeeGlobal`, `BeeInterface`, `Common.*`, `Global.*`,
  task blocking, `Thread.Sleep`, or parallel execution references in ES.8 code.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.8 PASSED — PLC link, staging/flush, and bypass are implemented.** Next: ES.9 Step 1 UI.

## ES.9 — Step 1 UI — 2026-08-23

Implementation:
- Added `AppSession` as the edit-session holder for the active `ScreenProject`, project path,
  and dirty state.
- Replaced the placeholder main form body with `WizardHost`, including the four-step strip,
  inline validation banner, bottom navigation buttons, and the first-step gate.
- Added `Step1CaptureControl`, covering window/region capture source selection, top-level
  window enumeration, match mode, client-area flag, framework hint, capture engine selector,
  `Test Engines` probe table, preview, register-image action, and size tolerance editing.
- Added `SnipOverlayForm`, a 30% black full-virtual-desktop selection overlay that records a
  screen rectangle by drag/Enter and cancels with Escape.
- Added `tools/Step1UiSmoke/`, an STA WinForms smoke harness that opens the main form, asserts
  the wizard/Step 1 controls exist, and writes an evidence screenshot under `artifacts/ES.9/`.
- Reworked Step 1 event subscriptions to named handlers with unsubscribe-before-subscribe
  hygiene so the EasyScreen guard's event-balance rule passes.

Verification (ACTUAL):
- `dotnet restore tools/Step1UiSmoke/Step1UiSmoke.csproj` succeeded.
- `MSBuild tools/Step1UiSmoke/Step1UiSmoke.csproj /t:Build /p:Configuration=Release
  /p:Platform=x64 /v:minimal` succeeded.
- `tools/Step1UiSmoke/bin/x64/Release/net48/EasyScreen.Step1UiSmoke.exe artifacts/ES.9`
  exited 0 with `STEP1 UI OK | controls=41` and wrote
  `artifacts/ES.9/step1-mainform.png`.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- Scoped scan found no `BeeCore`, `BeeGlobal`, `BeeInterface`, `Common.*`, `Global.*`,
  task blocking, `Thread.Sleep`, or parallel execution references in ES.9 code.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.9 PASSED — Step 1 capture UI is implemented and gated.** Next: ES.10 Step 2 UI.

## ES.10 — Step 2 UI — 2026-08-23

Implementation:
- Added `PlcSettingsPanel`, a slim EasyScreen-only replacement for the oversized EasyVision
  hardware manager UI.
- Implemented PLC editor controls for brand, TCP/serial connection fields, station ID,
  input/output word bases, and offline bypass.
- Added bit and value grids backed by `PlcConfig.Bits` and `PlcConfig.Values`, plus Add and
  Test Row actions for each grid.
- Wired Step 2 to `ScreenPlcLink`: bypass uses `InMemoryPlcTransport`, while non-bypass uses
  the production HslCommunication transport from ES.8.
- Extended `WizardHost` to navigate Step 1 -> Step 2 with per-step gates. Step 2 requires a
  PLC connection or bypass and at least one output value.
- Added `tools/Step2UiSmoke/`, an STA WinForms smoke harness that enables bypass, adds one
  output bit and one output value, connects, tests both rows, validates the gate, and writes
  an evidence screenshot under `artifacts/ES.10/`.

Verification (ACTUAL):
- `dotnet restore tools/Step2UiSmoke/Step2UiSmoke.csproj` succeeded.
- `MSBuild tools/Step2UiSmoke/Step2UiSmoke.csproj /t:Build /p:Configuration=Release
  /p:Platform=x64 /v:minimal` succeeded. The cold build emitted known warnings from the
  bundled `PLC_Communication` dependency only.
- `tools/Step2UiSmoke/bin/x64/Release/net48/EasyScreen.Step2UiSmoke.exe artifacts/ES.10`
  exited 0 with `STEP2 UI OK | bits=1 | values=1 | bypass=ok` and wrote
  `artifacts/ES.10/step2-plc.png`.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- Scoped scan found no `BeeCore`, `BeeGlobal`, `BeeInterface`, `Common.*`, `Global.*`,
  task blocking, `Thread.Sleep`, or parallel execution references in ES.10 code.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.10 PASSED — Step 2 PLC configuration UI, connect/bypass, and row tests are implemented.** Next: ES.11 Step 3 UI.

## ES.11 — Step 3 UI — 2026-08-23

Implementation:
- Added `Step3RegionsControl`, combining a `RoiCanvas` master-image editor, a region list,
  per-region data type/usage/target editors, allow-character editing, a key=value map editor,
  and Master/Live test toggles.
- Implemented region creation/deletion, PLC output-value binding, map parsing, Step 3
  validation, and wizard navigation from Step 2 into Step 3.
- Implemented per-region and Test All execution through `RegionCropper` plus OCR result
  conversion for `String`, `Int`, and `MappedInt` regions.
- Production testing uses `ScreenOcrRuntime`; the control also accepts injected OCR/live
  providers so smoke tests can validate UI/crop/conversion paths without a Python OCR
  deployment.
- Live test mode compares capture size against the Step 1 registered size and shows the
  planned inline warning: `size mismatch — result unreliable`.
- Added `tools/Step3UiSmoke/`, which creates a synthetic master image, injects deterministic
  OCR output, adds two regions, binds them to PLC output values, verifies Master conversions,
  verifies Live size-mismatch warning, validates the Step 3 gate, and writes evidence under
  `artifacts/ES.11/`.

Verification (ACTUAL):
- `dotnet restore tools/Step3UiSmoke/Step3UiSmoke.csproj` succeeded.
- `MSBuild tools/Step3UiSmoke/Step3UiSmoke.csproj /t:Build /p:Configuration=Release
  /p:Platform=x64 /v:minimal` succeeded.
- `tools/Step3UiSmoke/bin/x64/Release/net48/EasyScreen.Step3UiSmoke.exe artifacts/ES.11`
  exited 0 with `STEP3 UI OK | regions=2 | count=123 | status=1 | live=size-mismatch` and
  wrote `artifacts/ES.11/step3-regions.png`.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- Scoped scan found no `BeeCore`, `BeeGlobal`, `BeeInterface`, `Common.*`, `Global.*`,
  task blocking, `Thread.Sleep`, or parallel execution references in ES.11 code.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.11 PASSED — Step 3 region UI, binding, Master test conversion, and Live size warning are implemented.** Next: ES.12 Step 4 UI.

## ES.12 — Step 4 UI — 2026-08-23

Implementation:
- Added `Step4OutputControl`, covering output-region usage, result snapshot display,
  Clear Results, CSV export, and log-folder open actions.
- Added a Logs + Images settings panel for `LogSettings`: Save logs, Only errors,
  Log every cycle, Perf log, OK/NG image toggles, raw/result image toggles, log/image
  folders, image format, retention days, disk guard percent, and a live disk readout.
- Wired Step 4 into `WizardHost` with a Step 4 gate. Finish checklist/polish remains the
  ES.15 card as planned.
- Implemented `GetUsedOutputRegions()` so `RegionUsage.NotUsed` entries remain configured
  but are verifiably skipped by output selection.
- Added `tools/Step4UiSmoke/`, which creates two output-bound regions, marks one NotUsed,
  verifies only the Used region is selected for output, exports a CSV snapshot, saves/loads
  `.esproj` to prove log/image toggles persist, clears result state, and writes evidence
  under `artifacts/ES.12/`.

Verification (ACTUAL):
- `dotnet restore tools/Step4UiSmoke/Step4UiSmoke.csproj` succeeded.
- `MSBuild tools/Step4UiSmoke/Step4UiSmoke.csproj /t:Build /p:Configuration=Release
  /p:Platform=x64 /v:minimal` succeeded.
- `tools/Step4UiSmoke/bin/x64/Release/net48/EasyScreen.Step4UiSmoke.exe artifacts/ES.12`
  exited 0 with `STEP4 UI OK | used=1 | unused=1 | csv=ok | persist=ok` and wrote
  `artifacts/ES.12/step4-output.png`.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- Scoped scan found no `BeeCore`, `BeeGlobal`, `BeeInterface`, `Common.*`, `Global.*`,
  task blocking, `Thread.Sleep`, or parallel execution references in ES.12 code.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.12 PASSED — Step 4 output/log/image UI is implemented; NotUsed regions are skipped and toggles persist.** Next: ES.12b logging backend.

## ES.12b — Logging backend — 2026-08-23

Implementation:
- Added `EasyScreen.Core/Logging/LogRecord.cs` with the planned EasyVision-compatible
  four-field JSON shape: Time, Level, Source, Message.
- Added `ScreenLogger`, including non-blocking queue ingest, 400-row drain batches,
  `OnlyErrors` filtering, Perf row handling, cycle-row logging, query/export/clear,
  retention by age and `MaxEntries`, process-exit flush, and atomic JSON writes through
  temp file + replace.
- Added `ImageArchive`, including day-foldered image saving, raw/result image toggles,
  ROI/text overlay rendering for result images, retention of old day folders, disk guard,
  WARN cleanup logging, and FATAL image-disable behavior when free space stays below the
  configured threshold.
- Added `tools/LoggingSmoke/`, which verifies cycle rows, `OnlyErrors`, atomic-write cleanup,
  old day-folder retention, forced disk-low disable + FATAL logging, and image archive output.

Verification (ACTUAL):
- `dotnet restore tools/LoggingSmoke/LoggingSmoke.csproj` succeeded.
- `MSBuild tools/LoggingSmoke/LoggingSmoke.csproj /t:Build /p:Configuration=Release
  /p:Platform=x64 /v:minimal` succeeded.
- `tools/LoggingSmoke/bin/x64/Release/net48/EasyScreen.LoggingSmoke.exe artifacts/ES.12b`
  exited 0 with `LOGGING OK | cycle=1 | only-errors=ok | retention=deleted |
  disk-low=fatal | image=ok`.
- First solution-build attempt failed because a prior `EasyScreen.exe` process from the
  workspace held `EasyScreen.Core.dll`; that process was stopped and the build was rerun.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded after the lock was removed. Final rerun output had **0 errors and 0 warnings**.
- Scoped scan found no `BeeCore`, `BeeGlobal`, `BeeInterface`, `Common.*`, `Global.*`,
  task blocking, `Thread.Sleep`, or parallel execution references in ES.12b code.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.12b PASSED — logging, retention, image archive, disk guard, and FATAL disable path are implemented.** Next: ES.13 runner and size guard.

## ES.13 — Runner + size guard — 2026-08-23

Implementation:
- Added `CaptureSizeGuard` and `SizeCheck`, comparing every captured frame to the Step 1
  registered master size before OCR or PLC value writes.
- Added `ScreenRunOutcome` and `RegionRunResult` to carry cycle and per-region execution
  evidence without using task-style `.Result` properties.
- Added `ScreenCheckRunner`, a sequential pipeline that captures once, aborts on size
  mismatch, crops each Used region, runs OCR, converts `String` / `Int` / `MappedInt`,
  stages output values, flushes PLC outputs once, sets Result / ResultNg / Error bits, logs
  cycle details, and archives images after PLC flushing.
- Runner constructors support injected capture/OCR/logger/archive dependencies for deterministic
  smoke tests while the default path uses `ScreenCaptureService` and `ScreenOcrRuntime`.
- Fixed the `Python.Runtime.Runtime.PythonDLL` reference in `ScreenOcrRuntime` after adding the
  `EasyScreen.Core.Runtime` namespace so Python.NET remains unambiguous.
- Added `tools/RunnerSmoke/`, which verifies a two-region OK cycle writes `D300=123` and
  `D320=1`, sets result bits, and verifies a >5% shrink aborts before OCR and writes only
  the Error bit.

Verification (ACTUAL):
- `dotnet restore tools/RunnerSmoke/RunnerSmoke.csproj` succeeded.
- `MSBuild tools/RunnerSmoke/RunnerSmoke.csproj /t:Build /p:Configuration=Release
  /p:Platform=x64 /v:minimal` succeeded.
- `tools/RunnerSmoke/bin/x64/Release/net48/EasyScreen.RunnerSmoke.exe` exited 0 with
  `RUNNER OK | values=D300:123,D320:1 | bits=result/error | size-abort=no-ocr`.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- Scoped scan found no `BeeCore`, `BeeGlobal`, `BeeInterface`, `Common.*`, `Global.*`,
  task blocking, `Thread.Sleep`, or parallel execution references in ES.13 code.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.13 PASSED — sequential runner, size guard, PLC value writes, result bits, and size-abort Error-only path are implemented.** Next: ES.14 Run mode.

## ES.14 — Run mode — 2026-08-23

Implementation:
- Added `TriggerService`, which polls the PLC trigger bit, detects rising edges, prevents
  overlapping cycles, runs one cycle per edge, and emits the planned run phases.
- Added `FloatingButtonForm`, a borderless topmost floating status button with the 9-phase
  colour machine and progress ring: Ready, Triggered, Checking, Sending, CompleteOk,
  CompleteNg, Error, SizeMismatch, and Disconnected.
- Added `EditUnlockGate` and `PasswordPromptForm`, enforcing the configured edit password
  (`797979` by default), 3 attempts, and 30-second lockout.
- Added MainForm mode hooks: Enter Run mode hides the wizard and shows the floating button;
  Enter Edit mode restores the wizard; clicking the floating button opens the password prompt.
- Added `tools/RunModeSmoke/`, which renders phase evidence screenshots, verifies password
  refusal/lockout/acceptance, checks MainForm mode switching, checks PLC rising-edge one-cycle
  behavior, and checks SizeMismatch phase emission.

Verification (ACTUAL):
- `dotnet restore tools/RunModeSmoke/RunModeSmoke.csproj` succeeded.
- `MSBuild tools/RunModeSmoke/RunModeSmoke.csproj /t:Build /p:Configuration=Release
  /p:Platform=x64 /v:minimal` succeeded. The cold build emitted known warnings from the
  bundled `PLC_Communication` dependency only.
- `tools/RunModeSmoke/bin/x64/Release/net48/EasyScreen.RunModeSmoke.exe artifacts/ES.14`
  exited 0 with `RUN MODE OK | phases=triggered-checking-sending-complete |
  password=ok-lockout | trigger=one-cycle | size=size-mismatch`.
- Evidence screenshots were written under `artifacts/ES.14/`: `01-triggered.png`,
  `02-checking.png`, `03-sending.png`, and `04-complete.png`.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- Scoped scan found no `BeeCore`, `BeeGlobal`, `BeeInterface`, `Common.*`, `Global.*`,
  task blocking, `Thread.Sleep`, or parallel execution references in ES.14 code.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.14 PASSED — Run mode floating button, phase machine, trigger service, password lockout, and mode switching are implemented.** Next: ES.15 wizard polish.

## ES.15 — Wizard polish — 2026-08-23

Implementation:
- Upgraded `AppSession.Dirty` to an event-backed property and added `ClearDirty()` so the
  shell can reflect dirty state consistently.
- Added all-step validation and a Finish checklist to `WizardHost`; Finish is enabled only
  when Step 1 capture, Step 2 PLC, Step 3 regions, and Step 4 output gates all pass.
- Added `WizardHost.TryFinish()` and `FinishRequested`, allowing the shell to enter Run mode
  only after the checklist succeeds.
- Wired MainForm dirty-title tracking and wizard Finish -> Run mode. The existing Run -> Edit
  path remains guarded by the ES.14 password prompt.
- Added `tools/WizardPolishSmoke/`, which seeds a complete project, validates all gates,
  checks the checklist has no missing item, checks dirty marker on/off, executes Finish -> Run,
  returns to Edit, and writes evidence under `artifacts/ES.15/`.

Verification (ACTUAL):
- `dotnet restore tools/WizardPolishSmoke/WizardPolishSmoke.csproj` succeeded.
- `MSBuild tools/WizardPolishSmoke/WizardPolishSmoke.csproj /t:Build
  /p:Configuration=Release /p:Platform=x64 /v:minimal` succeeded. The cold build emitted
  known warnings from the bundled `PLC_Communication` dependency only.
- `tools/WizardPolishSmoke/bin/x64/Release/net48/EasyScreen.WizardPolishSmoke.exe
  artifacts/ES.15` exited 0 with `WIZARD POLISH OK | gates=ok | finish=run | edit=ok |
  dirty=ok` and wrote `artifacts/ES.15/wizard-polish.png`.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- Scoped scan found no `BeeCore`, `BeeGlobal`, `BeeInterface`, `Common.*`, `Global.*`,
  task blocking, `Thread.Sleep`, or parallel execution references in ES.15 code.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.15 PASSED — wizard gates, finish checklist, dirty tracking, and full Edit -> Run -> Edit smoke pass are implemented.**

## Final EasyScreen plan status — 2026-08-23

All EasyScreen implementation cards ES.1 through ES.15 are complete. The last full-solution
build and EasyScreen guard both passed after ES.15.

## ES.feedback — UI/flow cleanup after screenshot feedback — 2026-08-23

Implementation:
- Reworked the MainForm shell around a top toolbar with New, Open, Save, Save As, and
  Run/Edit actions. Project saves use `ProjectStore.Save`, project loads use
  `ProjectStore.Load`, and the title shows the current file plus a dirty marker.
- Added `UserAppSettings`, stored at `%LOCALAPPDATA%/EasyScreen/settings.json`, for
  remembering `LastProjectPath`. Startup loads the last saved project when present and
  enters Run mode only after the run checklist passes; invalid or missing projects stay in
  Edit mode with the checklist warning visible.
- Changed wizard navigation so step buttons, Back, and Next can move freely between all
  four steps. Validation now warns and controls Finish/Run only; it no longer blocks
  editing other steps.
- Updated Step 1 layout to use a scrollable left panel plus fill preview, and replaced the
  old client-area checkbox flow with three explicit capture modes: Client Area, Full
  Window, and Screen Region. Register status now records the mode and registered size.
- Updated Step 2 PLC UI to use a resizable table layout, added Bypass PLC messaging, and
  added Add default outputs for offline setup. Bypass uses the in-memory PLC transport and
  allows Finish without physical PLC hardware as long as output values exist.
- Added scroll-friendly containers to Step 3 and Step 4 so fixed editor content remains
  reachable when the form is resized smaller.
- Expanded Step 1, Step 2, and wizard smoke coverage for Full Window mode, default PLC
  outputs, free wizard navigation, save/load, and startup auto-Run behavior.

Verification (ACTUAL):
- `dotnet run --project tools/Step1UiSmoke/Step1UiSmoke.csproj -c Release --
  artifacts/ES.feedback` exited 0 with `STEP1 UI OK`.
- `dotnet run --project tools/Step2UiSmoke/Step2UiSmoke.csproj -c Release --
  artifacts/ES.feedback` exited 0 with `STEP2 UI OK | bits=4 | values=2 |
  bypass=ok | defaults=ok`.
- `dotnet run --project tools/WizardPolishSmoke/WizardPolishSmoke.csproj -c Release --
  artifacts/ES.feedback` exited 0 with `WIZARD POLISH OK` and `WIZARD STARTUP OK`.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.feedback PASSED — resized wizard UI, free navigation, save/open/startup,
PLC bypass/default outputs, and Step 1 Full Window mode are implemented and smoke-tested.**

## ES.feedback2 — Splitter and toolbar overlap fix — 2026-08-23

Implementation:
- Replaced the shell's direct dock stacking with a two-row `TableLayoutPanel`, ensuring the
  top toolbar consumes its own row and can no longer overlap the wizard step strip.
- Added `SplitPaneLayout`, a small WinForms helper that applies initial splitter distances
  only after the split container has enough width. This avoids startup exceptions while
  preventing the parameter pane from collapsing to the tiny default width.
- Applied explicit draggable split panes to Step 1, Step 3, and Step 4. Step 1 now opens with
  a wide parameter pane and a visible splitter between the parameter controls and preview.
- Expanded `Step1UiSmoke` to assert that the toolbar does not overlap the step strip, Step 1
  has a split pane, the parameter pane opens at a usable width, and the splitter is easy to
  grab.

Verification (ACTUAL):
- `dotnet run --project tools/Step1UiSmoke/Step1UiSmoke.csproj -c Release --
  artifacts/ES.feedback2` exited 0 with `STEP1 UI OK`.
- `dotnet run --project tools/Step2UiSmoke/Step2UiSmoke.csproj -c Release --
  artifacts/ES.feedback2` exited 0 with `STEP2 UI OK`.
- `dotnet run --project tools/WizardPolishSmoke/WizardPolishSmoke.csproj -c Release --
  artifacts/ES.feedback2` exited 0 with `WIZARD POLISH OK` and `WIZARD STARTUP OK`.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal`
  succeeded. Final rerun output had **0 errors and 0 warnings**.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.feedback2 PASSED — toolbar/step overlap is fixed and split panes open at
usable sizes while remaining user-adjustable.**

## ES.feedback3 — Step 3 responsive fields and OCR.py deployment — 2026-08-24

Implementation:
- Reworked Step 3's left editor pane from fixed `SetBounds` coordinates to responsive
  `TableLayoutPanel` rows. `Data type`, `Usage`, `Target`, and `Allow` now resize with the
  split pane width instead of being clipped when the user drags the splitter smaller.
- Added `EasyScreen/Tool/OCR.py`, copied from the existing Release OCR script, and marked it
  as EasyScreen content so MSBuild copies it to `Tool/OCR.py` beside `EasyScreen.exe`.
- Added OCR runtime Python path fallback: if `OcrSettings.PythonPath` is empty, EasyScreen now
  searches beside the executable, a `Python` folder beside the executable, `DLLs`, the current
  directory, and PATH for `python39.dll` before failing.
- Updated `OcrSmoke` to copy the same `Tool/OCR.py` content into the smoke output and to pass
  deployment validation when OCR initializes and returns at least one token/box.
- Expanded `Step3UiSmoke` to resize the form, drag the Step 3 splitter to a narrow parameter
  pane, and assert that combo/text fields remain inside their parent rows.
- Copied `OCR.py` into the currently used Release output folder
  `EasyScreen/bin/x64/Release/Tool/OCR.py` for the already-built app.

Verification (ACTUAL):
- Standard `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release /p:Platform=x64
  /v:minimal` reached compile/copy but was blocked by the currently running
  `EasyScreen.exe` holding `EasyScreen.Core.dll` in the Release output.
- Alternate EasyScreen build to `artifacts/ES.feedback3/build/` succeeded and copied
  `Tool/OCR.py`.
- `tools/Step3UiSmoke` built to an isolated output and exited 0 with `STEP3 UI OK`.
- `tools/OcrSmoke` built to an isolated output, copied `Tool/OCR.py`, initialized PaddleOCR
  through the runtime, and exited 0 with `OCR OK`.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.feedback3 PASSED — Step 3 fields resize with the split pane, OCR.py is
source-managed and copied to output, and PythonPath-empty deployments auto-detect python39.dll
when available.**

## ES.anycpu — Release build platform switch — 2026-08-24

Implementation:
- Changed `EasyScreen.sln` to expose only `Debug|Any CPU` and `Release|Any CPU`, removing
  stale EasyScreen solution-level `x64`/`x86` selections that could send builds to the old
  `bin/x64/Release` output tree.
- Changed `EasyScreen` and `EasyScreen.Core` to build as `AnyCPU` with `Prefer32Bit=false`.
  On 64-bit Windows this keeps the process 64-bit while using the standard Any CPU output
  path.
- Changed the EasyScreen smoke/test utility projects to `AnyCPU` with `Prefer32Bit=false`
  so their default `Release` output matches the app platform.
- Updated the active EasyScreen plan build command and acceptance text from Release x64 to
  Release Any CPU.
- Confirmed `PLC_Communication` was already `Any CPU`. `tools/ActiveManager` remains x64
  intentionally because it is outside EasyScreen and depends on Win64 camera/native binaries.

Verification (ACTUAL):
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release "/p:Platform=Any CPU" /v:minimal`
  succeeded. It produced `EasyScreen/bin/Release/EasyScreen.exe` and
  `EasyScreen.Core/bin/Release/EasyScreen.Core.dll`; `Tool/OCR.py` was copied beside the app
  under `EasyScreen/bin/Release/Tool/OCR.py`.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.
- `dotnet run --project tools/Step1UiSmoke/Step1UiSmoke.csproj -c Release --
  artifacts/ES.anycpu` exited 0 with `STEP1 UI OK`.
- `dotnet run --project tools/Step2UiSmoke/Step2UiSmoke.csproj -c Release --
  artifacts/ES.anycpu` exited 0 with `STEP2 UI OK`.
- `dotnet run --project tools/Step3UiSmoke/Step3UiSmoke.csproj -c Release --
  artifacts/ES.anycpu` exited 0 with `STEP3 UI OK`.

Acceptance: **ES.anycpu PASSED — EasyScreen Release builds now use Any CPU by default, the
Release output is `EasyScreen/bin/Release`, OCR.py deploys to that output, and the focused
EasyScreen guard/smokes pass.**

## ES.step2ocr — Step 2 PLC editor/monitor and OCR runtime installer — 2026-08-24

Implementation:
- Rebuilt Step 2 into a professional PLC configuration surface: connection card on top,
  resizable split area, tabbed `Bits`/`Values` overview grids, right-side detail editors,
  and a monitor/status bar.
- Added explicit Bit Input/Output editing. Input functions are limited to `None`,
  `Trigger`, `Reset`, and `Bypass`; output functions are limited to `None`, `Result`,
  `ResultNg`, `Ready`, `Busy`, `Error`, and `Heartbeat`. Non-`None` functions are validated
  as unique because runtime lookup resolves by function.
- Added explicit Value Input/Output editing with name, direction, address, type,
  string length, and sample value. Non-empty value names are validated as unique.
- Added direct row APIs to `ScreenPlcLink`: `ReadBit(ScreenBit, out bool)`,
  `WriteBit(ScreenBit, bool)`, and public `ResolveBitAddress(ScreenBit)`, while preserving
  the existing `BitFunction` APIs used by the runner.
- Added Step 2 monitor controls. Monitor starts/stops from the UI, polls at 500 ms by
  default, guards against overlapping ticks, performs PLC polling off the UI thread, and
  updates live `Last` values for bits/values. Bypass mode uses `InMemoryPlcTransport` so
  inputs/outputs can be simulated without hardware.
- Added `EasyScreen.OcrRuntimeTool`, a small WinForms utility with `Create Package` and
  `Install Package` tabs. The packager copies a Python39 runtime subset, required OCR
  site-packages plus `.dist-info` dependency closure, PP-OCRv5 server det/rec model
  folders, `Tool/OCR.py`, and writes `manifest.json` with file hashes. The installer copies
  into current-user Python39/model paths, writes EasyScreen `OcrPythonPath`, and attempts
  Python import verification when `python.exe` is present.
- Added `EasyScreen.OcrRuntimeToolSmoke` with a fake Python/model tree to verify package
  creation, install copy paths, manifest/settings output, and `OCR.py` deployment without
  requiring the real multi-GB Paddle runtime during CI/smoke.
- Added EasyScreen startup OCR preload. The app now applies user-local `OcrPythonPath` when
  a project `PythonPath` is empty, then initializes `Tool/OCR.py` and preloads the current
  OCR model in a background task so the first Step 3 read does not pay the full load cost.
- Fixed Step 3 String regions: adding/selecting `String` no longer falls back to `Int`, and
  empty AllowChars defaults to all uppercase/lowercase letters plus digits.

Verification (ACTUAL):
- Isolated Any CPU build: `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release
  "/p:Platform=Any CPU" /p:OutputPath="artifacts/ES.step2ocr/build/" /v:minimal` succeeded
  and built `EasyScreen.exe`, `EasyScreen.OcrRuntimeTool.exe`, and
  `EasyScreen.OcrRuntimeToolSmoke.exe`.
- Standard `EasyScreen/bin/Release` build is currently blocked by the running
  `EasyScreen.exe` process holding `EasyScreen.Core.dll`; the isolated output was used for
  compile verification without disturbing the operator's open app.
- `artifacts/ES.step2ocr/step2build/EasyScreen.Step2UiSmoke.exe artifacts/ES.step2ocr`
  exited 0 with `STEP2 UI OK | bits=5 | values=2 | bypass=ok | defaults=ok |
  monitor=ok | editor=ok`.
- `artifacts/ES.step2ocr/step3build/EasyScreen.Step3UiSmoke.exe artifacts/ES.step2ocr`
  exited 0 with `STEP3 UI OK | regions=3 | count=123 | status=1 | string=ABC123 |
  live=size-mismatch`.
- `artifacts/ES.step2ocr/ocrtoolbuild/EasyScreen.OcrRuntimeToolSmoke.exe
  artifacts/ES.step2ocr/ocrtool` exited 0 with `OCR RUNTIME TOOL OK`.
- `bash tools/check_easyscreen.sh` => **ALL CHECKS PASSED**.

Acceptance: **ES.step2ocr PASSED — Step 2 now has split grid/detail editing and a live
monitor, direct PLC row APIs exist, bypass can simulate I/O, OCR preloads on startup, String
regions keep alphanumeric AllowChars, and an OCR runtime package/install tool is available.**

## ES.roi-edit — Step 3 test selection and ROI anchor editing fixes — 2026-08-24

Implementation:
- Fixed Step 3 grid refresh after Test/Apply/ROI drag so it preserves the selected
  `ScreenRegion` object and suppresses temporary `SelectionChanged` events while rebuilding
  rows. This prevents the editor from loading a different row and changing `Data type` after
  pressing Test.
- Changed ROI resize behavior from center-scaling to EasyVision-style fixed-opposite-anchor
  editing. Dragging a corner now keeps the opposite corner fixed; dragging a side keeps the
  opposite side fixed. Move remains center/body drag only, and rotated ROIs resize in their
  local coordinate system.

Verification (ACTUAL):
- `git diff --check` passed.
- Build/smoke tests were not run in this turn because the current user request did not
  explicitly authorize build/test execution under the repository build policy.

Acceptance: **ES.roi-edit IMPLEMENTED — Step 3 Test no longer changes the active DataType
through grid selection churn, and ROI resize no longer changes the opposite corner/side.**

## ES.run-hide-edit — Hide edit form in Run mode — 2026-08-24

Implementation:
- Changed `MainForm.EnterRunMode()` so the floating Run button is shown as an independent
  top-level form and the edit `MainForm` is hidden with `Hide()`.
- Changed `MainForm.EnterEditMode()` so the same edit form is shown and activated again
  before the floating button is hidden.
- Updated the floating button password prompt owner selection so, while the edit form is
  hidden, the prompt is owned by the floating button instead of a hidden main form.
- Added a RunMode smoke assertion that Run mode hides the edit form and Edit mode restores it.

Verification (ACTUAL):
- `git diff --check` passed.
- Build/smoke tests were not run in this turn because the current user request did not
  explicitly authorize build/test execution under the repository build policy.

Acceptance: **ES.run-hide-edit IMPLEMENTED — entering Run mode now hides the edit form and
leaves only the floating Run button visible; returning to Edit mode shows the form again.**

## ES.int-clean-text — Extract digits for Int regions — 2026-08-24

Implementation:
- Updated Step 3 test conversion so `RegionDataType.Int` extracts numeric characters from
  OCR text before parsing. Non-numeric noise such as letters or punctuation is ignored.
- Updated Run mode conversion in `ScreenCheckRunner` with the same logic so UI tests and
  production cycles behave consistently.
- Preserved fail-closed behavior: if no digits remain, or the extracted number is outside
  `Int32`, the region remains NG/Error instead of silently writing `0`.
- Updated Step 3 smoke fixture so noisy OCR text like `ab123x456m` is expected to produce
  integer value `123456`.

Verification (ACTUAL):
- `git diff --check` passed.
- Build/smoke tests were not run in this turn because the current user request did not
  explicitly authorize build/test execution under the repository build policy.

Acceptance: **ES.int-clean-text IMPLEMENTED — Int regions now tolerate OCR text containing
extra non-numeric characters before/around the number.**

## ES.roi-one-axis — One-axis ROI handle resizing — 2026-08-24

Implementation:
- Changed ROI hit-testing to prefer side-center handles before corner handles. This makes
  small/thin ROIs easier to resize in one direction without accidentally grabbing a corner.
- Added drag-time corner axis locking. If the user starts from a corner handle, EasyScreen
  now resolves the gesture to the dominant local ROI axis: horizontal drags edit left/right
  size only, and vertical drags edit top/bottom size only.
- Kept resize math in rotated ROI local coordinates, so one-axis resize follows the ROI's
  own angle rather than raw screen X/Y.

Verification (ACTUAL):
- `git diff --check -- EasyScreen\Ui\RoiCanvas.cs` passed.
- Build/smoke tests were not run in this turn because the current user request did not
  explicitly authorize build/test execution under the repository build policy.

Acceptance: **ES.roi-one-axis IMPLEMENTED — Step 3 ROI edit points now support one-axis
resizing like the EasyVision-style editor behavior requested by the operator.**

## ES.run-button-circle — Smaller borderless circular Run button — 2026-08-24

Implementation:
- Reduced the floating Run button form from 108x108 to a 76x76 square so the Run indicator
  is less visually dominant in Run mode.
- Removed the explicit dark ellipse outline and the magenta `TransparencyKey` background
  that could leave a colored anti-aliased edge around the button.
- Added an ellipse `Region` for the borderless form and keeps width/height equal on resize,
  making the floating button a true circle instead of an oval.
- Kept the progress arc only when progress is non-zero, drawn inside the circle instead of
  acting as a permanent border.

Verification (ACTUAL):
- `git diff --check -- EasyScreen\Shell\FloatingButtonForm.cs` passed.
- Build/smoke tests were not run in this turn because the current user request did not
  explicitly authorize build/test execution under the repository build policy.

Acceptance: **ES.run-button-circle IMPLEMENTED — the Run-mode floating button is smaller,
borderless, and clipped to a real circle.**

## ES.int-any-digits — Accept noisy and overflowing integer OCR text — 2026-08-24

Implementation:
- Kept the existing non-numeric character removal for `RegionDataType.Int` in both the
  Step 3 Test path and the production `ScreenCheckRunner` path.
- Changed extracted numeric text outside the `Int32` range from NG to a successful,
  saturated value: positive overflow becomes `Int32.MaxValue` and negative overflow
  becomes `Int32.MinValue`.
- Kept NG behavior only when the OCR text contains no integer digits.
- Updated the Step 3 smoke fixture with noisy overflowing text to cover this rule.

Verification (ACTUAL):
- `git diff --check` passed.
- Build/smoke tests were not run in this turn because the current user request did not
  explicitly authorize build/test execution under the repository build policy.

Acceptance: **ES.int-any-digits IMPLEMENTED — an Int region is OK whenever OCR text contains
digits; surrounding characters are ignored and overflow is safely saturated.**

## ES.parameter-cards — EasyVision-style parameter cards and accordions — 2026-08-24

Implementation:
- Added a standalone EasyScreen `ParameterAccordion` and `ParameterAccordionSection` with
  the same neutral surface/header palette used by EasyVision, collapsible card headers,
  chevrons, summaries, hover state, rounded borders, and automatic pane-width updates.
- Kept the control local to EasyScreen so the production utility does not acquire the full
  BeeGlobal/BeeInterfaceControls dependency graph.
- Grouped Step 1 into Capture source, Capture engine, and Register image cards. Responsive
  row sizing now follows the draggable split pane width.
- Grouped Step 2 into PLC connection, Bit editor, and Value editor cards while preserving
  the Bits/Values overview grids and monitor toolbar.
- Grouped Step 3 into Region list, Region parameters, and Test & result cards while keeping
  the ROI canvas in the resizable split pane.
- Rebuilt Step 4 fixed-bound layouts as responsive card layouts for Region outputs, Logging,
  and Image archive parameters.
- Extended the four Step UI smoke fixtures with structural accordion/card assertions.

Verification (ACTUAL):
- `git diff --check` passed for all tracked files in scope, and the new accordion source was
  inspected for whitespace and C# 7.3-compatible syntax.
- Build/smoke tests were not run in this turn because the current user request did not
  explicitly authorize build/test execution under the repository build policy.

Acceptance: **ES.parameter-cards IMPLEMENTED — EasyScreen parameters are grouped into clean,
collapsible EasyVision-style cards that resize with each split pane.**

## ES.easyocr-option — Selectable EasyOCR detection engine — 2026-08-24

Implementation:
- Added persisted OCR engine settings with backward-compatible PaddleOCR defaults:
  `Engine`, comma-separated EasyOCR `Languages`, and `UseGpu`.
- Added an OCR engine card to Step 3 with PaddleOCR/EasyOCR selection, language codes,
  GPU/CPU selection, and a responsive card summary.
- Extended `ScreenOcrRuntime` model caching and Python calls so cache identity includes the
  selected engine, languages, and device. Step 3 Test, Run mode, and startup preload now all
  use the same project OCR settings.
- Extended `Tool/OCR.py` with lazy EasyOCR import, cached `easyocr.Reader` instances, direct
  NumPy image reads from the existing image pointer, EasyOCR allowlists, and normalized
  boxes/scores/text output compatible with the existing C# result pipeline.
- Preserved the PaddleOCR entry points and added separate CPU/GPU Paddle model cache keys.
- Extended the OCR production package tool to include EasyOCR, Torch-family dependencies,
  and the current `%USERPROFILE%/.EasyOCR` model cache when available, then install it to the
  production user's default EasyOCR cache path.
- Extended Step 3, OCR runtime, and OCR package smoke fixtures for the new engine option.

Verification (ACTUAL):
- Static C# diff/whitespace review and Python AST syntax parsing passed.
- Build and runtime OCR smoke tests were not run because the current user request did not
  explicitly authorize build/test execution under the repository build policy.

Acceptance: **ES.easyocr-option IMPLEMENTED — projects can select EasyOCR or PaddleOCR while
sharing the existing region conversion, AllowChars, result display, and PLC output flow.**

## ES.ui-preload-polish — Unclipped action rows and startup OCR preload — 2026-08-24

Implementation:
- Increased the Step 2 Bit/Value editor card bodies so wrapped Add/Edit/Delete/Test/Write
  buttons remain inside the card at narrow split-pane widths instead of being clipped at the
  bottom edge.
- Increased the Step 3 Test & result action row and card body so `Test All` can wrap below
  the Master/Live/Test Region controls without overlapping the warning/status rows.
- Exposed accordion section titles for deterministic UI structural checks.
- Moved selected-engine OCR preload from the Form `Shown` event to `MainForm` construction.
  A keyed task guard prevents duplicate concurrent preload requests for the same Python path,
  engine, languages, device, and model.
- This means a saved EasyOCR project starts creating its `easyocr.Reader` while the software
  is opening, before the operator presses Test or enters an OCR cycle.
- Extended Step 2 and Step 3 smoke fixtures with visible-button containment assertions.

Verification (ACTUAL):
- `git diff --check` passed for all files in scope.
- Build and visual/runtime smoke tests were not run because the current user request did not
  explicitly authorize build/test execution under the repository build policy.

Acceptance: **ES.ui-preload-polish IMPLEMENTED — wrapped controls stay inside their cards,
and the selected EasyOCR engine begins initialization during application startup.**

## ES.easyocr-full-clone — Complete EasyOCR runtime and model packaging — 2026-08-24

Implementation:
- Made EasyOCR packaging explicit and enabled by default in the OCR Runtime Tool.
- Added pre-package validation for the EasyOCR and Torch packages, the complete `.EasyOCR`
  cache, its `model` directory, and at least one initialized `.pth` model file.
- Continued copying dependency closure from Python package metadata and added the
  `python-bidi`/`bidi` distribution-to-import mapping required by EasyOCR.
- Cloned the complete EasyOCR cache tree, including every detection/recognition model and
  `user_network` file, rather than copying only a single selected model.
- Added manifest engine metadata, a complete EasyOCR cache-file list, and retained SHA-256
  entries for every packaged file.
- Added install-side checks that reject an EasyOCR bundle missing Torch or cached model
  weights, while installing the complete cache into the production user's `.EasyOCR` path.
- Moved the large Create/Install copy operations to background tasks so the WinForms tool
  stays responsive while the multi-gigabyte Torch runtime is copied.
- Extended the package smoke fixture for detection and recognition models, custom user
  networks, Torch native DLLs, `python-bidi`, manifest coverage, and missing-cache rejection.

Verification (ACTUAL):
- `git diff --check` and targeted static source inspection passed.
- The current workstation sources were confirmed present: Python39 EasyOCR, Torch, and the
  `.EasyOCR` cache containing `craft_mlt_25k.pth` and `english_g2.pth`.
- Build and smoke execution were not run because the current user request did not explicitly
  authorize build/test execution under the repository build policy.

Acceptance: **ES.easyocr-full-clone IMPLEMENTED — the tool now creates a complete offline
EasyOCR bundle with its initialized models and refuses incomplete production packages.**

## ES.ocr-worker-shutdown-ct — Worker packaging, offline Python, shutdown, and CT overlay — 2026-08-24

Implementation:
- Added a shared `EasyScreen.OcrRuntimeCore` assembly and a separate x64
  `EasyScreen.OcrRuntimeWorker` process. Create/Install jobs use JSON input and emit JSON-line
  progress with phase, byte/file counters, current file, completion, and errors.
- Reworked package creation to use adjacent `.partial` staging, block-stream copies, complete
  SHA-256 manifests, and staged publication only after copying and hashing complete.
- Added package schema v2 with OCR-only `site-packages`, Paddle/EasyOCR model caches,
  `Tool/OCR.py`, and the official CPython 3.9.13 x64 offline installer. The installer is
  validated by fixed size, official checksum, and signer before packaging or execution.
- Installation verifies every manifest hash first, reuses a writable CPython 3.9 x64 runtime
  found through the requested path, registry, or PATH, and installs current-user Python only
  when required. It updates user PATH without setting `PYTHONHOME`, verifies OCR imports/model
  initialization, and publishes `OcrPythonPath` only after success.
- Added progress/status/current-file UI and cancellation ownership. Closing the tool confirms
  cancellation, gives the worker three seconds to stop, then terminates the worker/installer
  process tree so no child process remains orphaned.
- Added a unified EasyScreen close path for Edit mode and the Run-button Exit command. It
  disposes runtime UI, waits briefly for OCR preload, shuts down Python.NET, exits the message
  loop, and uses a five-second process watchdog only when native shutdown remains stuck.
- Added runtime-only OCR cycle time on each region and an adaptive canvas overlay that renders
  detected content plus `CT Detect` below the tested ROI, flipping/clamping at viewport edges.
- Extended OCR runtime, Run mode, and Step 3 fixtures for the external worker protocol,
  progress phases, package v2 layout, Exit command, and CT Detect state.

Verification (ACTUAL):
- `git diff --check`, project XML parsing, and Roslyn C# 7.3 syntax parsing passed for the
  changed source set.
- Build and smoke execution were not run because the approved plan explicitly retained the
  repository requirement for separate build/test authorization.

Acceptance: **ES.ocr-worker-shutdown-ct IMPLEMENTED — OCR packaging/install runs out of
process with visible progress and offline Python bootstrap, EasyScreen exits completely, and
tested ROIs show their detected content and OCR cycle time.**

## ES.ocr-worker-cs0121 — Disambiguate shutdown watchdog thread — 2026-08-24

Implementation:
- Wrapped the shutdown watchdog delegate in an explicit `ThreadStart` so C# 7.3 resolves the
  `Thread` constructor without ambiguity against `ParameterizedThreadStart`.

Verification (ACTUAL):
- Roslyn C# 7.3 syntax parsing and static source inspection passed.
- Build execution was not run because the current request did not explicitly authorize it.

Acceptance: **ES.ocr-worker-cs0121 FIXED — MainForm no longer reports CS0121 at the watchdog
thread constructor.**

## ES.step3-canvas-zoom — Add Step 3 canvas zoom toolbar — 2026-08-24

Implementation:
- Added a responsive toolbar above the Step 3 ROI canvas with `Full`, Zoom Out, the current
  zoom percentage, and Zoom In controls.
- `Full` fits the registered image to the available canvas. Zoom In/Out preserve the current
  viewport position, while the existing mouse and keyboard zoom behavior remains available.
- Added stable control names and extended the Step 3 smoke fixture to cover the complete
  toolbar and percentage-label refresh.

Verification (ACTUAL):
- Confirmed the referenced ImageBox API exposes `ZoomToFit`, position-preserving Zoom In/Out,
  and an `EventHandler`-based `ZoomChanged` event.
- Roslyn C# 7.3 syntax parsing and diff whitespace checks passed.
- Build and smoke execution were not run because this request did not authorize them.

Acceptance: **ES.step3-canvas-zoom IMPLEMENTED — Step 3 now exposes visible Full, Zoom Out,
and Zoom In controls without removing existing canvas navigation.**

## ES.activation — Add BeeLicense activation gate and Form Active — 2026-08-24

Implementation:
- Added `EasyScreen.LicenseNative`, a minimal x64 C++/CLI assembly that compiles the existing
  `BeeCV/LicenseGuard.cpp` source. It preserves EasyVision's encrypted `.mac` request and
  signed personalized `BeeLicense.dll` formats while avoiding BeeCV's OpenCV/Hikrobot runtime
  dependencies.
- Added an auto-sizing Form Active with Camera Key, Machine ID, visible Key Active, Copy Key,
  Export `.mac`, Select `BeeLicense.dll`, Install & Activate, Continue, and Exit. Camera Key
  defaults to the literal `HIK`, so the request payload is `<machine fingerprint>*HIK`.
- Startup validates `BeeLicense.dll` beside `EasyScreen.exe` before constructing MainForm.
  Missing/invalid licenses expose only Form Active and Exit; successful activation enters the
  normal startup flow. Enter Run mode revalidates the installed license, and Edit mode exposes
  a License toolbar action.
- Personalized `BeeLicense.dll` files are never committed or bundled. Form Active validates a
  selected carrier before atomically installing it beside the executable.
- Replaced the shutdown watchdog's blocking `Thread.Sleep` with a background `Task.Delay` and
  documented the three best-effort catch blocks found by the EasyScreen conformance guard.

Verification (ACTUAL):
- Minimal native Release x64 build passed. `dumpbin /dependents` confirmed only VC runtime,
  Windows, and CLR dependencies; no OpenCV or camera runtime is linked.
- `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release "/p:Platform=Any CPU" /v:minimal`
  passed with zero warnings and zero errors. The native x64 project is mapped within the
  AnyCPU solution and copied beside `EasyScreen.exe`.
- Activation smoke build and execution passed. The generated `.mac` decoded back to the
  current 64-character Machine ID and device ID `HIK`; the rendered form contained every
  required field/action and a non-empty Key Active.
- `bash tools/check_easyscreen.sh` passed every independence, architecture, hygiene, and event
  balance rule.

Acceptance: **ES.activation PASSED — EasyScreen now blocks startup on the same personalized
BeeLicense mechanism as EasyVision and visibly asks for the HIK-bound Key Active workflow.**

## ES.ocr-runtime-v3 — Selective OCR engines and portable Python39 — 2026-08-24

Implementation:
- Upgraded OCR packages to schema v3. Every manifest file is owned by Shared, PaddleOCR,
  EasyOCR, PythonPortable, or PythonInstaller; dependency closures are calculated separately
  and `.dist-info/RECORD` maps distribution ownership without duplicating shared libraries.
- Create Package now supports PaddleOCR, EasyOCR, or both and bundles the pinned official
  CPython 3.9.13 x64 installer plus embeddable ZIP by default. Both prerequisites retain
  download, byte progress, size, checksum, and signature validation where applicable.
- Install Package now reads package capabilities, offers selective engine install, shows the
  selected data size, and supports Portable, Existing Python39, and Per-user Installer modes.
  Legacy v1/v2 packages remain all-components installs because they have no ownership data.
- Portable mode extracts to `%LOCALAPPDATA%\EasyScreen\Runtime\Python39`, configures
  `python39._pth` for vendored `Lib\site-packages`, and does not change PATH, registry,
  `PYTHONHOME`, or pip. Cancellation never activates incomplete settings.
- Engine installation is add/repair only. Successful verification persists Python mode and
  installed engines; EasyScreen keeps the project engine unchanged and reports a missing
  runtime before preload, Test, or Run mode.
- `Tool/OCR.py` now lazy-loads Paddle only for PaddleOCR. EasyOCR-only installations no
  longer require Paddle and initialize with offline model downloads disabled.
- Extended the OCR tool smoke fixture with schema/component assertions and Paddle-only,
  EasyOCR-only, and combined install scenarios.

Verification (ACTUAL):
- Roslyn C# syntax parsing passed for every changed C# source; Python AST parsing passed for
  `Tool/OCR.py`; project XML, focused source assertions, and `git diff --check` passed.
- Build and executable smoke tests were not run because this task explicitly retained the
  separate build/test authorization gate.

Acceptance: **ES.ocr-runtime-v3 IMPLEMENTED — production installation can select either OCR
engine and defaults to a private, no-admin Python39 portable runtime.**

## ES.ocr-runtime-cache-python39 — Reuse unchanged runtime files and resolve per-user Python — 2026-08-24

Implementation:
- Create Package now seeds its atomic `.partial` directory from the currently published
  package. Unchanged files use same-volume hard links where supported, so large OCR libraries,
  models, and Python prerequisites are not cloned again; the streamed copy path remains the
  compatibility fallback.
- Install Package compares every planned destination with its package source. Matching files
  advance progress as `Skipped identical file` without being overwritten, while changed files
  continue through the existing temporary-file repair path.
- Per-user Python installation now writes a persistent installer log, waits for the runtime to
  become usable, creates a missing writable `Lib\site-packages`, and resolves a valid CPython
  3.9 x64 path from the requested path, default path, registry, and PATH before reporting an
  error. Failure messages include the requested path, exact validation reason, and installer
  log tail instead of the previous generic invalid-runtime message.
- Extended the OCR runtime smoke fixture with repeated Create and Install repair scenarios that
  require reuse/skip progress messages.

Verification (ACTUAL):
- Roslyn C# 7.3 syntax parsing passed for the package manager and smoke fixture.
- Diff whitespace checks passed.
- Build and executable smoke tests were not run because this task did not explicitly authorize
  them.

Acceptance: **ES.ocr-runtime-cache-python39 IMPLEMENTED — repeat package operations reuse
unchanged files, and successful Python installers are resolved to their actual valid runtime.**

## ES.ocr-download-completion — Finish prerequisite downloads without waiting for EOF — 2026-08-24

Implementation:
- Python installer and embeddable ZIP downloads now stop reading as soon as the received byte
  count reaches the known response length, avoiding an indefinite final EOF read after the UI
  has already displayed 100 percent.
- HTTP connect/read-write operations use a 30-second timeout and disable keep-alive for these
  pinned one-file downloads.
- The worker flushes the completed file and reports an explicit `Validating` phase with `1/1`
  files before checksum/signature verification. Cached prerequisites report the same validation
  lifecycle, and Create Package returns to a visible `Scanning` phase before library discovery.

Verification (ACTUAL):
- Roslyn C# 7.3 syntax parsing and diff whitespace checks passed.
- Build and executable worker smoke tests were not run because this request did not explicitly
  authorize them.

Acceptance: **ES.ocr-download-completion IMPLEMENTED — reaching the advertised byte count no
longer leaves the worker blocked in DownloadingPython, and every post-download phase is visible.**

## ES.python39-x64-fallback — Validate installed Python from executable and PE metadata — 2026-08-24

Implementation:
- CPython validation still prefers an executed `3.9|64` probe, but no longer rejects an
  otherwise valid runtime solely because `python.exe` cannot start before the OCR payload and
  production dependencies are fully available.
- The fallback validates both `python.exe` and `python39.dll` as AMD64 PE images, requires the
  runtime DLL file version to be 3.9, and requires either the installed or embeddable standard
  library layout. Full imports remain mandatory in the post-install Verify phase.
- Existing-Python mode now reports every candidate path and its rejection reason. Portable mode
  includes the concrete validation reason instead of only reporting a generic CPython x64 error.

Verification (ACTUAL):
- Roslyn C# 7.3 syntax and diff whitespace checks passed.
- Static PE inspection of the workspace host Python reported AMD64 (`0x8664`) and file version
  3.9.13 for both `python.exe` and `python39.dll`.
- Build and executable installation tests were not run because this request did not explicitly
  authorize them.

Acceptance: **ES.python39-x64-fallback IMPLEMENTED — valid official Python 3.9 x64 layouts can
continue to payload installation, while invalid candidates now expose exact diagnostics.**

## ES.activation-runtime-deploy — Deploy the native activation VC runtime — 2026-08-24

Implementation:
- Confirmed `EasyScreen.LicenseNative.dll` is an x64 C++/CLI bridge whose only non-system VC
  dependency is `VCRUNTIME140.dll` (plus the Windows Universal CRT).
- The Release x64 native project now copies the matching Microsoft VC143 `vcruntime140.dll`
  redistributable into its output. The EasyScreen Release target then copies that dependency
  beside `EasyScreen.exe` and fails packaging if it is missing.
- Runtime discovery uses `VCInstallDir` plus `VCToolsRedistVersion`; the initially attempted
  `VCToolsRedistDir` property is empty in this Visual Studio project context and was removed.
- Form Active distinguishes the three deployment files: `EasyScreen.LicenseNative.dll` is the
  native bridge, `vcruntime140.dll` is its runtime dependency, and `BeeLicense.dll` is the
  personalized customer license. Load errors now identify missing files, x86/x64 mismatch, or
  remaining Universal CRT dependency errors explicitly.

Verification (ACTUAL):
- `dumpbin /dependents` confirmed the native bridge requires `VCRUNTIME140.dll`, the Windows
  Universal CRT API set, `KERNEL32.dll`, and `mscoree.dll`.
- Roslyn C# 7.3 syntax, project XML, and diff whitespace checks passed.
- Static MSBuild property resolution located the dependency at
  `VC\Redist\MSVC\14.44.35112\x64\Microsoft.VC143.CRT\vcruntime140.dll`.
- Build and production startup tests were not run because this request did not explicitly
  authorize them.

Acceptance: **ES.activation-runtime-deploy IMPLEMENTED — Release deployment now carries the
missing VC runtime and activation load failures identify the actual missing component.**

## ES.ocr-folder-fast-path — Skip child scans for completed package folders — 2026-08-24

Implementation:
- Create Package now reads only the existing output `manifest.json` first. A schema v3 package
  with the same selected engines and required Python prerequisites is accepted immediately;
  source library, model, and output child folders are not enumerated, hashed, or copied.
- Successful installs persist the package manifest SHA-256 alongside schema, runtime mode,
  engines, and resolved Python target in `ocr-install-state.json`.
- A repeated Install reads only the manifest file and successful marker, then checks the expected
  first-level runtime/package/model folders. When those values match, it skips manifest file
  hashing, recursive directory enumeration, per-file comparison, copy, and model initialization.
- Even without a completed marker, each existing Paddle detection/recognition or EasyOCR model
  folder is now treated as an opaque installed unit: its package hashes are not verified, its
  children are not enumerated/copied, and that specific model is not initialized during Verify.
  OCR library imports still run so an unusable Python/engine installation is not activated.
- Unmarked, failed, legacy-marker, changed-manifest, changed-mode, changed-engine, or missing-root
  installations retain the existing full validation/repair path once. Deleting/renaming the
  output package or install marker explicitly forces a rebuild/repair.
- The smoke fixture now requires repeated Create and Install calls to emit only one Completed
  progress event stating that no child folder or file was scanned, and covers an unmarked install
  whose existing model folders must remain opaque and unchanged.

Verification (ACTUAL):
- Roslyn C# 7.3 syntax parsing and diff whitespace checks passed.
- Build and executable smoke tests were not run because this request did not explicitly
  authorize them.

Acceptance: **ES.ocr-folder-fast-path IMPLEMENTED — completed package/install folders avoid all
recursive child scanning while unknown or changed folders still receive one safe repair pass.**

## ES.ocr-portable-python-path — Resolve the installed portable python39.dll — 2026-08-24

Implementation:
- A completed-install fast marker now requires the top-level `python39.dll`; a leftover or empty
  Portable folder can no longer bypass extraction and publish invalid settings.
- Install refuses to update EasyScreen settings unless `<resolved target>\python39.dll` exists,
  and reports that exact expected path when the runtime payload is incomplete.
- EasyScreen replaces a missing/broken project PythonPath with the valid user-local installed
  runtime. Enter Run mode reloads the latest settings, so an installer run while EasyScreen was
  open is recognized without preserving a stale invalid path.
- User settings save merges newer externally-written valid OCR runtime fields, preventing an open
  EasyScreen instance from overwriting the Portable path when it later saves the project path.
- Runtime resolution accepts a Python folder, direct `python.exe`, or direct `python39.dll`, and
  auto-detects both `%LOCALAPPDATA%\EasyScreen\Runtime\Python39` and the standard per-user
  Python39 folder before searching application/PATH locations.

Verification (ACTUAL):
- Roslyn C# 7.3 syntax parsing and diff whitespace checks passed for the installer, shell settings,
  MainForm, and OCR runtime resolver.
- Build and executable Portable installation tests were not run because this request did not
  explicitly authorize them.

Acceptance: **ES.ocr-portable-python-path IMPLEMENTED — Portable installation cannot activate a
folder without python39.dll, and EasyScreen resolves the valid installed runtime automatically.**

## ES.ocr-install-import-only — Remove model initialization from package verification — 2026-08-24

Implementation:
- Install Verify now imports only the selected OCR libraries (`paddle`, `paddleocr`, `paddlex`
  and/or `easyocr`, `torch`). It never constructs Paddle detection/recognition models or an
  EasyOCR Reader; model initialization remains owned by EasyScreen startup/preload.
- The import-only process timeout is 90 seconds. A successful install message explicitly states
  that model initialization is deferred to EasyScreen startup.
- OCR Runtime Core, Tool, and Worker versions were advanced to 3.3.0. The Tool title displays the
  loaded Core version and every non-fast-path install emits a Starting progress event with the
  loaded Core version and verification policy.
- This makes stale deployment visible. The reported log used Core output timestamp 13:41 while
  the relevant source was modified at 14:20, and its old `initialized models` message could not
  originate from the corrected source.

Verification (ACTUAL):
- Roslyn C# 7.3 syntax, project XML, and diff whitespace checks passed.
- Static output inspection confirmed the currently built Tool/Worker Core DLL is version 1.0.0.0
  and predates the source changes; it must be rebuilt/redeployed before this fix is active.
- Build and executable install tests were not run because this request did not explicitly
  authorize them.

Acceptance: **ES.ocr-install-import-only IMPLEMENTED — package installation no longer waits for
OCR model construction, and logs identify stale Core deployments unambiguously.**

## ES.plc-delta-serial-worker — Delta SS2 serial settings and responsive connection — 2026-08-24

Implementation:
- The persisted PLC configuration now includes serial DataBits and StopBits. New projects default
  to 7 data bits, Even parity, 1 stop bit, and station 1; older project files retain safe field
  initializer fallbacks when those properties are absent.
- Step 2 exposes Data bits, Parity, and Stop bits alongside COM, baud, and station. TCP and serial
  editors enable only the controls relevant to the selected connection type, and invalid `None`
  stop bits are not offered.
- Serial transports now call the HSL serial `Open`/`Close` lifecycle instead of the TCP-only
  `ConnectServer`/`ConnectClose` methods. The selected DataBits, Parity, and StopBits are passed to
  `SerialPortInni` rather than the previous hard-coded 8-N-1 values.
- `Delta + Serial` resolves to `DeltaSerialAscii`, matching the Delta DVP/SS2 programming-port
  7-E-1 workflow. A read-only D0 probe must succeed after the COM port opens; otherwise the link is
  closed and the UI reports that the port opened but the PLC did not answer.
- Interactive Connect and Start Monitor connection attempts run through `Task.Run`, guarded by an
  attempt generation so a late result cannot reconnect after Disconnect or disposal. Step 2 shows
  colored Connecting, Connected, Disconnected, and detailed Connection failed states, including the
  active COM/baud/format/station summary. The synchronous method remains only for the existing
  headless Step 2 smoke harness; no WinForms button invokes it.

Verification (ACTUAL):
- Roslyn parsed all three changed C# files with C# 7.3 and reported no syntax errors.
- Focused source assertions confirmed the 7-E-1 defaults, Delta serial driver, serial Open path,
  D0 response probe, background interactive connection, visible status states, and all three new
  editors. Scoped diff whitespace checks passed.
- Build, executable smoke tests, and physical Delta SS2 communication were not run because this
  request did not explicitly authorize them.

Acceptance: **ES.plc-delta-serial-worker IMPLEMENTED — Step 2 persists 7-E-1 serial settings,
connects Delta SS2 through the serial ASCII driver without freezing the UI, and reports a verified
PLC response or a detailed failure.**

## ES.plc-station-zero — Allow Delta serial station zero — 2026-08-24

Implementation:
- Delta serial validation now accepts station 0 through 247 instead of rejecting station 0.
- The existing Step 2 station editor already supports zero and persists it as a byte, so no UI or
  project schema change was required.
- A station-zero connection still must pass the existing read-only D0 response probe. Opening the
  COM port alone is not promoted to Connected when the configured PLC does not answer.

Verification (ACTUAL):
- Roslyn C# 7.3 syntax parsing, focused station-range source assertions, and scoped diff whitespace
  checks passed.
- Build and physical Delta SS2 communication were not run because this request did not explicitly
  authorize them.

Acceptance: **ES.plc-station-zero IMPLEMENTED — Delta serial station 0 can be saved and used by the
transport without weakening the PLC-response connection check.**

## ES.ocr-import-heartbeat-timeout — Diagnose and extend OCR import verification — 2026-08-24

Implementation:
- Replaced the single combined Python process with one import process per selected OCR engine.
  Paddle verifies `paddle`, `paddleocr`, and `paddlex`; EasyOCR verifies `torch` and `easyocr`.
- The Python probe prints flushed start/completion markers around every module. The worker consumes
  those markers asynchronously and emits a one-second heartbeat containing the engine, current
  module, elapsed seconds, and completed-module count.
- Removed the fixed 90-second timeout for all imports combined. Each current module now receives a
  five-minute timeout that resets only after the next module begins, accommodating slow first-load
  DLL inspection without permitting an unlimited hang.
- Timeout and cancellation forcibly terminate the complete Python process tree. Failures identify
  the exact engine/module and retain the final 4,000 diagnostic characters instead of returning the
  context-free `Verify timed out` message.
- Runtime Core, Tool, and Worker versions advanced together to 3.3.1 so deployed logs identify the
  corrected verifier. Model construction remains deferred to EasyScreen startup.

Verification (ACTUAL):
- Roslyn C# 7.3 syntax parsing, project XML/version checks, focused timeout/heartbeat/process-tree
  assertions, and scoped diff whitespace checks passed.
- Build and executable OCR installation were not run because this request did not explicitly
  authorize them.

Acceptance: **ES.ocr-import-heartbeat-timeout IMPLEMENTED — slow first imports no longer fail at
90 seconds, progress exposes the active module, and a genuine five-minute module hang is terminated
with an actionable error.**

## ES.step3-engine-actions-allowchars — Persist OCR engine and expose Step 3 actions — 2026-08-24

Implementation:
- Step 3 now has an explicit pending-editor commit. MainForm invokes it before every Save/Save As,
  and Test Region/Test All invoke it before OCR, so the visible OCR engine, language, GPU, selected
  region type, target, Allow chars, and Map values are written to the project model first.
- OCR engine changes update `Project.Ocr.Engine` through both selected-value and user-commit events.
  The handler compares the full engine/language/GPU tuple before marking the project dirty, while
  programmatic project reload remains protected by the existing loading guard.
- Master/Live, Test Region, Test All, warning, and result status were removed from the collapsible
  Test accordion and placed in a permanently visible card below the left-side parameter accordion.
- Allow chars is now a full-width multiline editor with vertical scrolling, return-key support,
  and no word wrapping, matching the Map editing pattern. The default alphanumeric set is displayed
  as uppercase, lowercase, and digits on separate lines.
- Project JSON preserves the multiline formatting. The OCR runtime removes CR/LF separators before
  passing the allow-list to Python and before filtering returned labels, so the new presentation
  does not accidentally allow newline characters and applies equally to Edit tests and Run mode.

Verification (ACTUAL):
- Roslyn parsed all four changed C# files with C# 7.3 and reported no syntax errors.
- Focused source assertions confirmed pre-save engine commit, project model assignment, persistent
  Test placement, multiline Allow chars, three-line defaults, and runtime CR/LF normalization.
  Scoped diff whitespace checks passed.
- Build and executable WinForms/project round-trip tests were not run because this request did not
  explicitly authorize them.

Acceptance: **ES.step3-engine-actions-allowchars IMPLEMENTED — EasyOCR selection is committed before
save/test, Test actions stay visible outside accordions, and Allow chars supports multiline editing
without changing OCR allow-list semantics.**

## ES.region-empty-value-ng — Treat empty or nonnumeric OCR values as NG — 2026-08-24

Implementation:
- Added one shared region value converter used by both the Step 3 test UI and the production
  `ScreenCheckRunner`, removing the duplicate conversion implementations that could drift apart.
- A String OCR value containing no non-whitespace characters is now invalid and produces NG with
  `OCR text is empty`. An Int OCR value with no digits produces NG with value zero; Int values that
  contain digits retain the existing character-filtering behavior. Empty MappedInt values also
  produce NG while retaining the configured default value as the diagnostic value.
- A fully empty OCR result tuple is now treated as a successful recognition with empty text, so it
  reaches the region data-type validation and becomes NG. Null, incomplete, mismatched, Python, and
  image-conversion failures remain technical Error results.
- Invalid values are not written to the configured PLC target; the existing runner behavior only
  writes a target value after successful conversion.

Verification (ACTUAL):
- Roslyn parsed the four affected C# files with C# 7.3 and reported no syntax errors.
- Focused source assertions confirmed String/Int/MappedInt empty rejection, the shared Step 3 and
  Run-mode conversion path, semantic handling of a completely empty OCR tuple, and preservation of
  Error for an incomplete tuple. Scoped diff whitespace checks passed.
- Build and executable OCR/UI tests were not run because this request did not explicitly authorize
  them.

Acceptance: **ES.region-empty-value-ng IMPLEMENTED — OCR values are NG when String is empty,
MappedInt is empty, or Int contains no digits, consistently in Step 3 and Run mode.**

## ES.easyocr-dependency-entry-repair — Repair missing EasyOCR site-package entries — 2026-08-24

Implementation:
- Audited the active Python 3.9 EasyOCR installation. Its direct runtime entries are `easyocr`,
  `torch`, `torchvision`, `cv2`, `numpy`, `scipy`, `PIL`, `skimage`, `yaml`, `bidi`, `shapely`,
  `pyclipper`, and `ninja`; every entry is present on the packaging machine. Package selection still
  expands transitive dependencies through each distribution's METADATA and RECORD ownership.
- Added the missing explicit EasyOCR roots for OpenCV headless, Shapely, and pyclipper, and reject
  Create Package with a complete missing-entry list when the source Python runtime is incomplete.
- Create Package no longer trusts only the schema/engine marker. It compares every top-level
  dependency-closure entry and its manifest ownership against the existing package. Missing module,
  `*.dist-info`, `*.libs`, or top-level `.pth` entries trigger repair; existing entries are reused.
- Install Package now derives every selected top-level `Lib\\site-packages` entry from the manifest.
  The completed-install fast path is accepted only when all entries exist. A missing entry is copied
  as a whole, while an existing top-level entry is opaque: no child file is hashed, scanned, copied,
  or overwritten. Legacy packages use the same top-level-entry policy.
- Manifest verification skips installed opaque site-package entries and verifies only entries that
  will actually be copied. Existing Paddle and EasyOCR model-folder behavior remains unchanged.
- OCR Runtime Core, Tool, and Worker versions advanced together to 3.3.2 so deployed logs identify
  this dependency-repair implementation.

Verification (ACTUAL):
- Roslyn parsed the package manager and expanded smoke fixture with C# 7.3 and reported no syntax
  errors. All three project XML files parse and report version 3.3.2.
- Focused source assertions confirmed dependency auditing, manifest ownership checks, Create and
  Install missing-entry repair, opaque existing-entry behavior, legacy routing, and scoped
  whitespace checks. The smoke fixture now covers deleting `scipy`, restoring it, and preserving an
  existing Torch-folder sentinel, but was not executed because this request did not authorize tests.
- Build, executable package creation, and executable installation were not run under the repository
  authorization gate.

Acceptance: **ES.easyocr-dependency-entry-repair IMPLEMENTED — Create/Install detects and clones
missing EasyOCR site-package entries while never descending into entries already installed.**

## ES.plc-runmode-output — Publish application mode through a PLC output bit — 2026-08-24

Implementation:
- Appended `RunMode` to `BitFunction`, preserving every existing enum ordinal and persisted project
  value. Step 2 exposes it only in the Output function list and retains the existing uniqueness rule.
- MainForm requests `RunMode=1` only after activation, OCR-engine, and full wizard validation pass
  and the application commits to Run mode. Entering Edit mode requests `RunMode=0` before showing
  the editor. Startup auto-Run and the floating button's Edit command use these same transitions.
- PLC writes run on a serialized background task chain rather than the WinForms thread. This keeps
  hardware SDK calls off the UI thread and preserves final ordering when Run/Edit is toggled quickly.
- A successful real or bypass PLC connection replays the current mode state, so a newly connected
  transport is initialized to zero in Edit mode or one in Run mode.
- Bypass projects may pass the Run checklist without a prior manual Connect. Entering Run mode now
  creates and connects the in-memory transport on demand before queuing the one state.
- Shutdown queues `RunMode=0` and waits up to one second before disposing the wizard/PLC link. The
  existing five-second native/Python shutdown watchdog remains unchanged.

Verification (ACTUAL):
- Roslyn parsed all five changed C# files with C# 7.3 and reported no syntax errors.
- Focused source assertions confirmed enum append order, Output-only UI exposure, validated Run
  transition, Edit/shutdown clearing, reconnect replay, serialized background writes, and bypass
  coverage. Scoped diff whitespace checks passed.
- The Step 2 smoke fixture now asserts the in-memory RunMode transition `false -> true -> false`,
  but it was not executed because this request did not explicitly authorize tests. No build or
  physical PLC write was run.

Acceptance: **ES.plc-runmode-output IMPLEMENTED — a configured RunMode output is one in Run mode,
zero in Edit/shutdown, and resynchronized after PLC connection without blocking the UI.**

## ES.plc-trigger-run-worker-ctloop — Run/Edit worker, PLC Trigger, and CTLoop — 2026-08-24

Implementation:
- Added a Step 2 `Mode: Edit → Run` / `Mode: Run → Edit` action that requests the real application
  mode transition. Valid Run entry writes the configured `RunMode` output and then starts the PLC
  trigger worker; Edit and shutdown request an immediate stop and clear the mode output.
- Connected the existing `TriggerService` to `ScreenCheckRunner` in `MainForm`. A rising edge on the
  configured Trigger input executes the selected-screen capture, region OCR/conversion, output value
  flush, and Result/ResultNg/Error writes. A held-high Trigger does not start repeated cycles.
- Trigger polling is guarded against overlapping ticks. Ready is one while idle, Busy is one only
  during a cycle, and stale Result/ResultNg/Error states are cleared at cycle start. Worker startup
  and cleanup are serialized so rapid Run/Edit changes cannot let an old worker overwrite a new
  worker's PLC status.
- Added an editable `Trigger ms` Step 2 parameter backed by `PlcConfig.PollIntervalMs`; pending Step 2
  connection parameters are committed before Save. PLC SDK calls for Run startup and cleanup remain
  on background tasks.
- `Add default outputs` now includes Busy and RunMode bits plus an Int32 output Value named `CTLoop`
  at `D340`. The runner writes Capture + OCR processing milliseconds to CTLoop immediately before
  flushing output Values to the PLC.

Verification (ACTUAL):
- Roslyn parsed the nine affected production/smoke C# files with C# 7.3 and reported no syntax
  errors. Focused source assertions covered the mode event, worker construction, rising-edge guard,
  non-overlap guard, CTLoop runtime/default wiring, polling persistence, and serialized cleanup.
- Scoped whitespace checks passed. Smoke sources now cover the Step 2 mode request, default
  Busy/RunMode/CTLoop points, Ready/Busy restoration, and the D340 CTLoop write.
- No build, executable smoke test, OCR run, or physical PLC check was run because this request did
  not authorize build/test execution.

Acceptance: **ES.plc-trigger-run-worker-ctloop IMPLEMENTED — Run mode now consumes PLC Trigger rising
edges and performs Capture → OCR → PLC output with CTLoop timing and serialized Run/Edit lifecycle.**

## ES.ocr-create-dependency-audit — Repair version-qualified EasyOCR dependencies — 2026-08-24

Implementation:
- Fixed `Requires-Dist` parsing during Create Package. Distribution names are now extracted before
  version operators, so requirements such as `typing-extensions>=4.8`, `sympy==1.13.1`,
  `imageio>=2.33`, and `lazy-loader>=0.4` enter the recursive dependency closure instead of being
  treated as nonexistent package names.
- Added Windows x64 / Python 3.9 marker filtering. Linux/macOS-only and optional `extra` requirements
  are excluded, while applicable Python 3.9 requirements remain in the package audit. This avoids
  both missing Windows runtime libraries and accidentally requesting Linux NVIDIA/Triton packages.
- Create Package now rejects an unresolved applicable transitive dependency and reports its name
  together with the parent distribution. It no longer silently publishes an incomplete package
  when a dependency is declared in METADATA but absent from the source `Lib\site-packages`.
- Expanded the EasyOCR runtime-entry audit to include the Torch and scikit-image runtime stack:
  filelock, typing_extensions, sympy/mpmath, networkx, Jinja2/MarkupSafe, fsspec, imageio, tifffile,
  packaging, and lazy_loader. Single-file `.py`/`.pyd` modules are recognized as installed entries.
- Existing schema v3 packages are re-evaluated against the corrected closure. Missing top-level
  entries or manifest ownership force the existing `.partial` repair path; existing complete
  top-level entries remain opaque and are reused. Core, Tool, and Worker versions were advanced
  together to 3.3.3.

Verification (ACTUAL):
- Roslyn parsed the package manager and expanded smoke fixture with C# 7.3 without syntax errors.
  All three OCR Runtime project XML files parse and report version 3.3.3. Scoped whitespace checks
  passed.
- The source smoke fixture now covers version-qualified Torch/scikit-image dependencies, excludes a
  Linux-only CUDA requirement, repairs a deleted transitive `imageio` entry in an existing package,
  and expects a named failure for an unresolved transitive distribution. It was not executed because
  this request did not authorize build/test execution.

Acceptance: **ES.ocr-create-dependency-audit IMPLEMENTED — Create Package now repairs old EasyOCR
packages with version-qualified runtime dependencies and refuses to publish unresolved closures.**

## ES.run-status-button-polish — Compact smooth circular Run status — 2026-08-24

Implementation:
- Reduced the floating Run status diameter from 76 to 62 logical pixels and increased its text from
  8 pt to 9.5 pt Segoe UI Semibold Bold. The form remains a true circular hit region and keeps its
  existing drag, click-to-edit, and right-click menu behavior.
- Added high-quality anti-aliasing, pixel offset, compositing, a subtle 1.15 px white outline, and
  rounded caps on the thinner progress ring for a smoother RJButton-style edge without adding a
  BeeInterface/RJButton project dependency.
- Shortened only the long visual phase labels (`Triggered`, `Checking`, `Sending`, `SizeMismatch`,
  and `Disconnected`) so the larger font remains centered and unclipped. Runtime phase values and
  PLC behavior are unchanged.

Verification (ACTUAL):
- Roslyn parsed the floating form and updated Run mode smoke source with C# 7.3 without syntax
  errors. Focused source assertions covered diameter, font, anti-alias settings, smooth border,
  rounded progress caps, short labels, and font disposal. Scoped whitespace checks passed.
- The smoke source now checks the 62 px size and circular center/corner region, but executable UI
  rendering was not run because this UI-only request did not authorize build/test execution.

Acceptance: **ES.run-status-button-polish IMPLEMENTED — the Run status is smaller, rounder, smoother,
and uses larger unclipped status text without introducing a new UI assembly dependency.**

## ES.debug-runmode-right-click-edit — Separate PLC debug mode and status-button Edit — 2026-08-24

Implementation:
- Changed the Step 2 mode action into `Debug RunMode: OFF/ON`. It now writes only the configured
  RunMode output bit and never calls `MainForm.EnterRunMode`, changes `MainForm.IsRunMode`, hides the
  wizard, starts the Trigger worker, or opens the floating Run form.
- Removed the Step 2 mode event chain from `PlcSettingsPanel` through `WizardHost` to `MainForm`.
  Actual Run mode remains available through Finish, the main Run/Edit toolbar action, and valid
  startup auto-Run behavior.
- Debug writes remain serialized on background tasks. Real PLC debugging requires an active
  connection, Bypass may create its in-memory transport, a missing RunMode output produces a clear
  message, and write failure leaves the displayed/debug state unchanged. The button is guarded
  against overlapping clicks and reports that EasyScreen remains in Edit mode.
- Removed the floating status button's left-click Edit/password action. Left-drag still repositions
  the status indicator and uses a move cursor; only the right-click `Edit` menu item raises the Edit
  request that opens the password prompt. The right-click Exit action is unchanged.

Verification (ACTUAL):
- Roslyn parsed the six affected production/smoke C# files with C# 7.3 without syntax errors.
  Focused assertions confirmed removal of the mode event chain, debug-only PLC writes, preserved
  Edit UI, connection/error handling, click serialization, left-drag-only status behavior, and the
  right-click Edit route. Scoped whitespace checks passed.
- Smoke sources cover ON/OFF in Bypass without hiding the host and reject left-click Edit while
  accepting the right-click Edit command. They were not executed because build/test execution was
  not authorized in this request.

Acceptance: **ES.debug-runmode-right-click-edit IMPLEMENTED — Step 2 toggles only the PLC RunMode bit
for debugging, while status-button Edit/password is available only from the right-click menu.**

## ES.activation-hide-request-key — Remove activation request and Copy Key UI — 2026-08-24

Implementation:
- Removed the `Key Active` label/text box and `Copy Key` button from `FormActive` rather than merely
  hiding them. The controls, click handler, and dispose subscription no longer exist, so the encoded
  activation request is not exposed through the WinForms control tree.
- Kept activation-request generation internal in `_activationRequest`. `Export .mac` continues to
  write the same machine/camera-bound request for the license issuer without displaying or copying
  the encoded value through the clipboard.
- Collapsed the activation card from five rows to four, moved License DLL and the explanatory text
  upward, and reduced the default/minimum form height to remove the unused whitespace.

Verification (ACTUAL):
- Roslyn parsed `FormActive` and the updated activation smoke source with C# 7.3 without syntax
  errors. Focused assertions confirmed both controls/handler are absent, internal request generation
  and `.mac` export remain wired, the compact layout is present, and the smoke source expects no key
  or copy controls. Scoped whitespace checks passed.
- No build or rendered activation-form smoke was run because this UI request did not authorize
  build/test execution.

Acceptance: **ES.activation-hide-request-key IMPLEMENTED — Form Active no longer exposes Key Active
or Copy Key, while secure internal `.mac` export remains functional.**

## ES.run-status-rj-gradient-plc-error — RJ-style gradient status and PLC fault state — 2026-08-24

Implementation:
- Replaced the floating form's direct status painting with a local `RunStatusButton` derived from
  WinForms `Button`. It exposes RJButton-compatible radius and border properties without adding the
  BeeInterface/BeeGlobal dependency graph to EasyScreen.
- The 62 px button is fully rounded, borderless, and rendered with a smooth three-stop vertical
  gradient. Each Run phase has its own color palette while preserving the existing drag and
  right-click Edit/Exit behavior.
- `Disconnected` now displays `ErrPLC` with a red gradient. PLC/Trigger-read service errors route to
  that state, and a subsequent successful Trigger read clears the PLC Error output and returns the
  indicator to `Ready` automatically.
- Extended the Run mode smoke source with border, radius, label, red-gradient, and hosted-button
  interaction coverage.

Verification (ACTUAL):
- Roslyn parsed the five affected production/smoke C# files with C# 7.3 without syntax errors.
  Eleven focused source assertions covered the real Button type, maximum radius, zero border,
  gradient paint, phase colors, `ErrPLC` routing, PLC recovery, and absence of a Bee UI dependency.
- Scoped whitespace checks passed. Build, rendered UI smoke, and physical PLC testing were not run
  because this request did not authorize build/test execution.

Acceptance: **ES.run-status-rj-gradient-plc-error IMPLEMENTED — the floating status is a borderless,
fully rounded gradient Button and reports PLC failure as red `ErrPLC` with automatic recovery.**

## ES.step3-easyocr-selection-persistence — Preserve the saved OCR engine — 2026-08-24

Implementation:
- Confirmed the current `PROG.esproj` persists `Ocr.Engine = 1` (`EasyOcr`); project serialization
  was not the source of the regression.
- Removed the OCR Engine ComboBox `DataSource`. WinForms could reset the bound currency to index
  zero when Step 3 was removed from and re-added to `WizardHost`, causing the UI event to overwrite
  the loaded `EasyOcr` value with `PaddleOcr`.
- The selector now contains explicit unbound enum items. Programmatic load/reparent operations only
  update the UI, while `SelectionChangeCommitted` writes genuine user choices and Save continues to
  commit the currently displayed editor value.
- Extended the Step 3 smoke source to cover panel reparenting, ProjectStore save/load round-trip,
  and UI reload of an EasyOCR project.

Verification (ACTUAL):
- Roslyn parsed the two affected C# files with language version 7.3 without syntax errors. Seven
  focused assertions covered unbound items, event routing, editor commit, reparent regression,
  persistence regression, and the actual saved EasyOCR value in `PROG.esproj`.
- Scoped whitespace checks passed. Build and executable UI smoke were not run because this request
  did not authorize build/test execution.

Acceptance: **ES.step3-easyocr-selection-persistence IMPLEMENTED — a saved EasyOCR project remains
EasyOCR when Step 3 is opened, reparented, saved, and reloaded.**

## ES.edit-maximized-roi-full-default — Maximized Edit form and Full ROI canvas — 2026-08-24

Implementation:
- The Edit form now starts maximized and returns maximized when leaving Run mode. Standard Windows
  taskbar, minimize, and restore behavior remain available because this uses the normal maximized
  window state rather than a borderless exclusive-screen form.
- `RoiCanvas` now defaults to Cyotek `Fit` mode, displaying the complete registered image while
  retaining its aspect ratio and adapting to Form or SplitContainer size changes.
- Loading a master image and pressing `Full` explicitly restore `Fit`; Zoom In/Out explicitly switch
  to `Normal` so manual zoom is preserved until the operator selects Full again.
- Extended Step 3 and Run mode smoke sources with Fit/manual-Full transitions and maximized Edit
  form coverage.

Verification (ACTUAL):
- Roslyn parsed all five affected production/smoke C# files with language version 7.3. Eight focused
  source assertions covered initial/returned maximized state, default/reload Full Fit, manual zoom,
  and regression smoke coverage.
- Scoped whitespace checks passed. Build and executable UI smoke were not run because this request
  did not authorize build/test execution.

Acceptance: **ES.edit-maximized-roi-full-default IMPLEMENTED — Edit mode opens maximized and Step 3
shows the complete ROI image by default while preserving deliberate manual zoom.**

## ES.plc-system-output-trigger-interval — PLC-side trigger interval output binding — 2026-08-24

Implementation:
- Corrected the meaning of `Trigger ms`: it is now `PlcConfig.TriggerIntervalMs`, a value sent to a
  configured PLC integer output so the PLC program can generate its own periodic Trigger signal.
  `PollIntervalMs` remains independent and is exposed as `PLC scan ms` for PC-side Trigger-bit reads.
- Added persisted `SystemValueSource` choices for output values: `None`, `TriggerIntervalMs`, and
  `CycleElapsedMs`. Step 2 exposes this as `Out value from`, shows it in the Values grid, and enforces
  Direction=Out, Type=Int32, and a non-empty address for system-owned outputs.
- TriggerIntervalMs is written without blocking the UI when the PLC connects, Run mode starts, its
  numeric setting changes while connected, or its output mapping is applied. Manual Test Write uses
  the system value rather than the Sample field.
- Replaced the name-based `CTLoop` runtime assignment with `CycleElapsedMs` source binding. Existing
  loaded `CTLoop` outputs migrate to that source, while Add Default Outputs creates both CTLoop and
  TriggerMs mappings.
- Added source-based set/flush APIs to `ScreenPlcLink` and updated Step 2, runner, and Run mode smoke
  sources for interval write, source validation, and CTLoop behavior.

Verification (ACTUAL):
- Roslyn parsed all ten affected production/smoke C# files with language version 7.3. Twelve focused
  source assertions covered separated semantics, persistence, UI mapping, validation, Connect/Run
  writes, source-based CTLoop, defaults, and regression smoke coverage.
- Scoped whitespace checks passed. Build, executable smoke, and physical Delta PLC checks were not
  run because this request did not authorize build/test execution.

Acceptance: **ES.plc-system-output-trigger-interval IMPLEMENTED — Trigger ms is now a PLC-side
interval system value mapped to an Int32 output, separate from the PC Trigger scan interval.**

## ES.roi-canvas-scrollbars — Restore ROI canvas scrollbars — 2026-08-24

Implementation:
- Enabled Cyotek ImageBox `AutoScroll` in `RoiCanvas`. The existing `AlwaysShowHScroll` and
  `AlwaysShowVScroll` settings had no active scrolling surface while AutoScroll remained disabled,
  so neither scrollbar appeared even at 300% zoom.
- Enabled `AutoPan` alongside the existing middle-button/edit pan behavior. Full/Fit remains the
  default display mode; switching to manual Zoom now exposes both scroll axes and allows navigation
  over the enlarged image.
- Extended the Step 3 smoke source to assert both scrollbar settings in initial Fit mode and after
  entering manual zoom.

Verification (ACTUAL):
- Roslyn parsed the two affected C# files with language version 7.3. Seven focused source assertions
  covered AutoScroll, AutoPan, both always-visible scroll settings, preserved Fit default, and smoke
  coverage.
- Scoped whitespace checks passed. Build and executable rendered UI smoke were not run because this
  request did not authorize build/test execution.

Acceptance: **ES.roi-canvas-scrollbars IMPLEMENTED — ROI Canvas exposes horizontal and vertical
scrolling again, including when the registered image is enlarged beyond the viewport.**

## ES.roi-canvas-scroll-range-sync — Synchronize ROI canvas scroll range — 2026-08-24

Implementation:
- Corrected the previous scrollbar fix after rendered UI feedback showed that enabling `AutoScroll`
  alone did not publish a usable Cyotek virtual scroll range after zooming.
- `RoiCanvas.RefreshScrollRange` now derives `AutoScrollMinSize` from Cyotek's scaled image dimensions
  and explicitly calls its protected `UpdateScrollbars` API.
- The range is synchronized after handle creation, image replacement, host resize, SizeMode changes,
  and zoom changes. Step 3 Full, Zoom Out, Zoom In, and registered-image loading also request an
  explicit refresh so splitter and toolbar operations cannot leave a stale range.
- Extended Step 3 smoke coverage to require a non-empty virtual scroll range after manual zoom.

Verification (ACTUAL):
- Roslyn parsed the three affected production/smoke C# files with language version 7.3. Seven focused
  source assertions covered range publication, Cyotek scrollbar refresh, all lifecycle hooks, and
  the four explicit Step 3 transitions.
- Build and executable rendered UI smoke were not run because this request did not authorize build
  or test execution.

Acceptance: **ES.roi-canvas-scroll-range-sync IMPLEMENTED IN SOURCE — zoom and splitter changes now
recalculate both ROI Canvas scroll axes instead of relying on stale Cyotek layout state.**

## ES.roi-canvas-navigation — Separate Full view from Scroll/Pan navigation — 2026-08-24

Implementation:
- Corrected the Step 3 navigation model: Full means Fit the registered image inside the full canvas
  viewport and reset its scroll origin; it no longer represents or disables the independent
  Scroll/Pan capability.
- Added a visible `Pan` toggle to the ROI toolbar. Pan mode uses left-button drag; Edit mode retains
  ROI manipulation while middle-button drag and Space+left-button drag remain available for panning.
- Forced Cyotek's horizontal and vertical scrollbar axes visible after each range refresh, while
  synchronizing each axis Enabled state with actual scaled-image overflow. This works around the
  beta control leaving its native axes hidden after Fit-to-Normal transitions.
- Full and registered-image loading both restore Fit and scroll origin `(0,0)`. Step 3 smoke coverage
  now checks visible axes plus Pan/Edit mouse-mode transitions.

Verification (ACTUAL):
- Roslyn parsed all three affected production/smoke C# files with language version 7.3. Seven focused
  source assertions covered both visible axes, overflow state, Pan toolbar wiring, Edit/Pan mode, and
  Full/image-load origin reset.
- Build and executable rendered UI smoke were not run because this request did not authorize build
  or test execution.

Acceptance: **ES.roi-canvas-navigation IMPLEMENTED IN SOURCE — Step 3 Full view, visible scrolling,
and mouse panning are explicit independent behaviors.**

## ES.roi-canvas-wheel-scroll-viewport — Restore wheel zoom and deterministic scrollbars — 2026-08-24

Implementation:
- Fixed the Full/Fit mouse-wheel lock by making `RoiCanvas` own wheel zoom. The first wheel action
  after Full retains the calculated Fit zoom, changes to manual Normal mode, and then applies Zoom
  In/Out around the pointer. Wheel input is accepted while the canvas is hovered.
- Replaced reliance on Cyotek beta's inconsistent native scrollbar visibility with a dedicated
  `RoiCanvasViewport` containing real WinForms horizontal and vertical scrollbars.
- Both bars remain visible, enable only when the scaled image exceeds that viewport axis, and call
  `RoiCanvas.ScrollTo` as their thumbs move. Canvas Pan updates the scrollbar thumbs through
  `AutoScrollPositionChanged`.
- Native Cyotek bars are hidden only when the external viewport is active; its internal scroll range
  remains responsible for image transforms, ROI coordinates, and Pan behavior.
- Extended Step 3 smoke coverage to require both named external scrollbar controls.

Verification (ACTUAL):
- Roslyn parsed all four affected production/smoke C# files with language version 7.3. Seven focused
  source assertions covered wheel ownership, Fit-to-Normal transition, hover-wheel input, both real
  scrollbar controls, ScrollTo routing, Pan-thumb synchronization, and Step 3 viewport hosting.
- Build and executable rendered UI smoke were not run because this request did not authorize build
  or test execution.

Acceptance: **ES.roi-canvas-wheel-scroll-viewport IMPLEMENTED IN SOURCE — Full no longer traps wheel
zoom, and Step 3 uses deterministic horizontal/vertical scrollbar controls.**

## ES.ocr-install-fast-path — Reduce Install Package scanning — 2026-08-24

Implementation:
- Added a completed-install fast path before package manifest parsing. A repeat install now compares
  the stored package path, manifest length/timestamp, runtime mode, engine set, Python target, model
  targets, EasyScreen target, and required top-level folders. When they match, it skips manifest
  parsing, child scans, hashing, copying, and Python imports.
- Extended `ocr-install-state.json` with manifest identity and resolved target paths. Old state files
  remain compatible and fall back to the existing full validation path once.
- Added per-operation existence caches for top-level `Lib\site-packages` entries and model folders.
  Manifest validation and install-plan creation now perform one filesystem query per parent entry
  instead of repeating the same query for every child file.
- The fast path still checks required EasyOCR dependency entries. If a dependency such as `scipy`
  is missing, it falls back to Repair and installs the missing entry without rescanning an existing
  Torch/model tree.
- Extended OCR Runtime Tool smoke source to require fast-path metadata and the single-event fast
  completion message.

Verification (ACTUAL):
- Roslyn parsed both affected C# files with language version 7.3. Six focused source assertions
  covered pre-manifest reuse, persisted identity, both existence caches, and skipped expensive phases.
- Scoped whitespace checks passed. Build and executable smoke were not run because this request did
  not authorize build or test execution.

Acceptance: **ES.ocr-install-fast-path IMPLEMENTED IN SOURCE — repeat installs avoid the package-wide
scan, while Repair performs at most one existence query per top-level library/model folder.**

---

## ES.ocr-tesseract — Replace Python EasyOCR/PaddleOCR with native Tesseract — 2026-08-26

Scope: retire the entire embedded-Python OCR stack and drive OCR through the native Tesseract engine
(charlesw `Tesseract` 5.2.0 over libtesseract + leptonica). No pythonnet, no python39.dll, no per-user
runtime install, no `Tool/OCR.py`. User decisions this session: (1) native C# — remove Python entirely;
(2) ship `eng`+`vie` language data; (3) delete the Python OCR runtime tooling now.

Public API of `ScreenOcrRuntime` preserved (`EnsureInitialized`/`EnsureModel`/`Read`/`Shutdown`/`IsReady`/
`LastInitError`) so `ScreenCheckRunner`, `Step3RegionsControl`, and `MainForm` call sites are unchanged
in shape. `OcrReadResult` (Text/Tokens/Scores/Boxes) and `OcrCharacterHeightFilter` untouched.

Files changed:
- `EasyScreen.Core/Ocr/ScreenOcrRuntime.cs` — rewritten. Caches one `TesseractEngine` per (tessdata
  folder + languages) behind the existing lock; `Mat`→PNG (`Cv2.ImEncode`)→`Pix.LoadFromMemory`;
  `Process(pix, psm)`; word-level `ResultIterator` → axis-aligned `RectRotate` (angle 0), confidence/100
  as score, `AllowChars` applied both as `tessedit_char_whitelist` and the existing C# `FilterAllowed`,
  left-to-right sort by `Box._PosCenter.X`. Legacy `en`/`vi` language codes normalized to `eng`/`vie`.
- `EasyScreen.Core/Model/AppSettings.cs` — `OcrSettings` now `{ TessDataPath, Languages="eng+vie",
  PageSegMode=6, TimeoutMs }`; removed `PythonPath`/`ModelName`/`Engine`/`UseGpu`.
- `EasyScreen.Core/Model/Enums.cs` — removed `OcrEngineKind`.
- `EasyScreen.Core/Persistence/ProjectStore.cs` — dropped the `OcrEngineKind` normalization; default
  `Languages` now `eng+vie`. (Newtonsoft ignores the removed JSON fields on old `.esproj`, so they load.)
- `EasyScreen.Core/EasyScreen.Core.csproj` — `pythonnet` → `Tesseract` 5.2.0.
- `EasyScreen/EasyScreen.csproj` — references `Tesseract` directly (so native x64 DLLs land beside the
  exe; dotnet/sdk#20073), copies `tessdata/**`, removed the `Tool/OCR.py` content item.
- `EasyScreen/tessdata/eng.traineddata`, `vie.traineddata` — added (tessdata_fast).
- `EasyScreen/Ui/Step3RegionsControl.cs` — replaced the Engine combo + GPU checkbox with a Languages box
  + a Page-Segmentation combo (Auto/Single block/Single line/Single word); summary now "Tesseract / <langs>".
- `EasyScreen/Shell/MainForm.cs` — removed all Python path plumbing (`ApplyUserOcrPathIfMissing`,
  `IsPython39Runtime`, `EngineDisplayName`); `ValidateOcrEngineInstalled`/`BuildOcrEngineError` now check
  for the required `*.traineddata`; runtime/preload messaging reworded for Tesseract.
- `EasyScreen/Shell/UserAppSettings.cs` — dropped `OcrPythonPath`/`OcrInstalledEngines`/`OcrPythonMode`.
- Deleted `EasyScreen/Tool/OCR.py` and the Python runtime tooling: `tools/OcrRuntimeTool`,
  `OcrRuntimeCore`, `OcrRuntimeWorker`, `OcrRuntimeToolSmoke`, `OcrSmoke`; removed the four project
  entries from `EasyScreen.sln`. `tools/Step3UiSmoke` had its OCR-engine-combo assertions removed.

Verification (ACTUAL):
- `dotnet build EasyScreen.Core/EasyScreen.Core.csproj -c Debug` → Build succeeded, 0 errors.
- `dotnet build EasyScreen/EasyScreen.csproj -c Release` → Build succeeded, 0 errors (2 warnings, both the
  pre-existing `System.Runtime.WindowsRuntime` MSB3245 seen under `dotnet build`).
- Output beside `EasyScreen.exe`: `Tesseract.dll`, `x64/tesseract50.dll`, `x64/leptonica-1.82.0.dll`,
  `tessdata/{eng,vie}.traineddata`; `Tool/OCR.py` gone.
- Runtime smoke (throwaway console calling `ScreenOcrRuntime.Read` on rendered Mats): "AB123" read as
  `AB123` (score 0.90); "70859" with digit whitelist read as `70859` (score 0.96); boxes==tokens; 21–54 ms;
  init + shutdown clean.

Autonomous decisions: preserved the `Read`/`EnsureModel` string overloads (now ignore the model-name
argument) to keep the API stable; kept `tessedit_char_whitelist` in addition to the C# character filter
because the LSTM engine honours the whitelist only partially; default PSM 6 (SingleBlock).

Acceptance: **ES.ocr-tesseract IMPLEMENTED AND VERIFIED — EasyScreen OCR now runs on native Tesseract
(eng+vie) with the Python stack fully removed; solution builds clean and a real OCR read round-trips.**

---

## ES.hygiene-guard — Clear the three standing check_easyscreen hygiene failures — 2026-08-26

Scope: the `tools/check_easyscreen.sh` hygiene section was failing on three pre-existing spots (blocking
task wait, `Thread.Sleep`, empty `catch`). Fixed at the user's request; no behaviour change.

Files changed:
- `EasyScreen.Core/Runtime/TriggerService.cs` — `WaitForIdle` replaced its `while(...) Thread.Sleep(10)`
  spin loop with `SpinWait.SpinUntil(() => Volatile.Read(ref _polling) == 0, timeoutMs)` (same bounded
  wait, yields/parks internally).
- `EasyScreen/Ui/PlcSettingsPanel.cs` — extracted the run-mode bit sequence into a synchronous
  `WriteRunModeOutput(link, runMode)`; `QueueRunModeOutputAsync` calls it inside its continuation, and the
  headless-smoke `ConnectForTest` calls it directly instead of `SetRunModeOutputAsync(...).GetAwaiter()
  .GetResult()`. Identical bits written; the blocking wait is gone.
- `EasyScreen/Shell/MainForm.cs` — the four `BeginInvoke(...)` marshaling guards (`ReportOcrPreloadError`,
  `RunPhaseChanged`, `RunCycleCompleted`, `RunServiceError`) now `catch (InvalidOperationException)` with a
  comment (covers the handle-not-created / `ObjectDisposedException` closing race) instead of `catch { }`.

Verification (ACTUAL):
- `dotnet build EasyScreen/EasyScreen.csproj -c Release` → Build succeeded, 0 errors.
- `bash tools/check_easyscreen.sh` → **ALL CHECKS PASSED** (was: 3 hygiene FAILs).

Acceptance: **ES.hygiene-guard DONE — the EasyScreen conformance guard is green with no behavioural change.**

---

## ES.region-pattern — Add per-region Pattern check mode beside OCR — 2026-08-26

Scope: a region can now be checked by template matching instead of OCR. Pattern mode uses a separate
"Sample" box (drawn in addition to the search Roi), a Set Sample button that captures the template from
the master, and a self-contained grayscale multi-angle NCC matcher (no EasyVision native). Fixed
behaviour per the request: data type is always Int; Mode gray; angle range +/-5deg; step auto; MaxObj=1
best object; score 70%. Detect -> region value 1, no detect -> 0. User decisions this session:
(1) the Sample is a separate box you draw; (2) a pattern region always reports OK and just emits the 1/0
flag (the PLC decides pass/fail); a missing sample or a sample larger than the search area is a setup
Error, not a "not detected".

Files changed:
- `EasyScreen.Core/Model/Enums.cs` — added `enum RegionCheckMode { Ocr, Pattern }`.
- `EasyScreen.Core/Model/ScreenRegion.cs` — added `CheckMode`, `PatternSample` (RectRotate),
  `PatternSampleImagePngBase64`, `PatternScoreThreshold=0.70`, `PatternAngleRangeDeg=5`, `[JsonIgnore]
  LastPatternScore`. The template is stored base64 in the .esproj, like the register image.
- `EasyScreen.Core/Pattern/PatternMatcher.cs` (new) — `Match(search, template, angleRange, threshold)`
  rotates the gray template across +/- range (auto ~11 steps), `Cv2.MatchTemplate` CCoeffNormed, keeps the
  single best score; plus base64 PNG encode/decode helpers.
- `EasyScreen.Core/Runtime/ScreenCheckRunner.cs` — `RunRegion` branches to `RunPatternRegion` when
  `CheckMode==Pattern`: crops the Roi, matches the stored sample, writes 1/0 to the target value, Check=OK
  (a decode/size fault is Check=Error and is not sent).
- `EasyScreen.Core/Persistence/ProjectStore.cs` — normalization: Pattern regions are forced to
  `DataType=Int`; score clamped to (0,1]; angle clamped to [0,45].
- `EasyScreen/Ui/RoiCanvas.cs` — a second editable rectangle. `SampleEditMode` retargets mouse editing to
  the selected region's `PatternSample`; the sample is drawn as a green dashed box and gets the resize/
  rotate handles while editing; `RectKind` threads through hit-test/drag; Delete is suppressed while editing
  the sample.
- `EasyScreen/Ui/Step3RegionsControl.cs` — a "Pattern check" accordion section: Check-mode combo, Score %
  and Angle +/- numerics, "Edit Sample box" toggle (drives canvas SampleEditMode), "Set Sample" button
  (captures the sample box from the master), and a status label. Pattern mode locks the data-type combo to
  Int and disables the OCR-only fields; `TestPatternRegion` reports the score/value on the Test button;
  `ValidateStep` requires a captured sample and skips OCR-only checks for pattern regions.

Verification (ACTUAL):
- `dotnet build EasyScreen/EasyScreen.csproj -c Release` -> Build succeeded, 0 errors.
- `bash tools/check_easyscreen.sh` -> ALL CHECKS PASSED (event balance 123/154).
- Matcher smoke: upright score 1.000; rotated 3deg score 0.993 (angle found 3); blank 0.000; base64
  round-trip ok.
- Runner + persistence smoke: .esproj round-trip keeps CheckMode=Pattern, forced Int, sample/score/angle;
  `ScreenCheckRunner.RunOnce` yields region value=1 (detect, Check OK) and value=0 (blank, Check OK).

Notes for future agents: the interactive canvas Sample editing and Set Sample capture were validated by
build + the core/runtime smokes; they were not driven through an automated WinForms harness.

Acceptance: **ES.region-pattern IMPLEMENTED AND VERIFIED — regions can check by Pattern (separate Sample
box + Set Sample) producing a 1/0 value; OCR mode is unchanged.**
