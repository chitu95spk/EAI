# EasyScreen — Detailed Implementation Plan

> **Status: IMPLEMENTATION COMPLETE.** ES.1 through ES.15 are complete. Execution evidence is recorded in `EasyScreen_CODEX_HISTORY.md`.
> Mandatory prerequisites: `CLAUDE.md` section 0 (Hard Rules), latest 3 entries of `docs/architecture/CODEX_HISTORY.md`.
> Every EasyVision symbol referenced below (`file:line`) was verified to exist in this repo during the research pass.

---

## 1. Product Scope

EasyScreen is a **standalone Windows application** that reads text and numbers from **the screen of a third-party application** (not from a camera), using multiple user-defined check regions, running **OCR** on each region, and **sending the results down to a PLC**.

### 1.1 Locked decisions (confirmed by user)

| # | Decision | Consequence |
|---|---|---|
| 1 | **No parallel execution.** Regions are checked **sequentially only** | `RunCheckMode` enum is dropped. Runner is a simple ordered `foreach`. Removes the Python-GIL risk entirely |
| 2 | **Own Communication layer.** Do NOT use `BeeGlobal.Comunication` / `Global.*` | EasyScreen owns `ScreenPlcLink`. Zero shared runtime state with EasyVision. Both apps can run at the same time |
| 3 | **Own save/load.** Completely unrelated to EasyVision project files | New `.esproj` JSON format. No `ClassProject`, no `BinaryFormatter`, no serialization-compat constraints |
| 4 | **Trigger from PLC** (in addition to manual click) | Bit-input polling loop is in scope for v1 |
| 5 | **PLC config saved inside the project file** | `.esproj` contains connection + bit map + value table + regions + output config in one document |
| 6 | **Copy controls from BeeInterface** rather than referencing it | EasyScreen references no EasyVision assembly at all — see §3 for what is actually copyable |
| 7 | **Step 3 inspects against the registered master image** (as well as live) | Master is the default test source, so ROI alignment is guaranteed and failures isolate to OCR/config — §5.5 Step 3 |
| 8 | **5 % size-deviation guard in Run mode** | A target window more than 5 % off the registered master aborts the cycle before OCR and prompts the operator to maximize the window — §4.4, §5.2.4 |
| 9 | **Support non-WinForms target windows** (WPF, Qt, Electron/Chromium, Java, UWP, DirectX) | Capture is built as pluggable engines with auto-probing; **Windows Graphics Capture is the primary engine** because it is the only one covering every framework — §4.3 |
| 10 | **Save Logs option in Step 4**, modelled on EasyVision | `ScreenLogger` + `ImageArchive` port `LogsDashboard`'s format, atomic write, retention and toggles — §4.10, Step 4 UI |
| 11 | **Edit-mode canvas has zoom +/− and pan like EasyVision's View** | `RoiCanvas : Cyotek ImageBox` — the *same standalone NuGet package* EasyVision's canvas derives from, so behaviour matches with no EasyVision coupling — §5.4 |
| 12 | **Run-mode button colour tracks cycle phase; clicking it asks for the Edit password `797979`** | Phase machine Ready→Triggered→Checking→Sending→Complete; the button no longer runs checks — PLC trigger only — §5.2.1–5.2.3 |

### 1.2 Consequence of decisions 2+3+6: EasyScreen is fully self-contained

EasyScreen references **no EasyVision project**. Its only external dependencies are third-party:

- `HslCommunication` (from `PLC_Communication/`) — PLC transport
- `OpenCvSharp4` — image handling for crop + OCR buffer
- `Python.Runtime` (pythonnet) — OCR engine bridge
- `Newtonsoft.Json` — project persistence
- **`CyotekImageBox` 2.0.0-beta.2** — zoom/pan canvas base class. Already in `packages/`; this is the *same standalone package* EasyVision's own canvas derives from (`ImageCanvasControl : ImageBox`, `BeeInterface/ShapeEditing/ImageCanvasControl.cs:19`), so zoom/pan behaviour matches with zero EasyVision coupling (§5.4)
- **`Microsoft.Windows.SDK.Contracts`** + a D3D11 interop package (`Vortice.Windows` or `SharpDX.Direct3D11`) — Windows Graphics Capture, required to support non-WinForms windows (§4.3.3)

Everything else is **copied source** or **new code**.

---

## 2. Solution Layout

```
EasyScreen/                          # WinExe, .NET Framework 4.8, x64
├── Program.cs
├── Shell/
│   ├── MainForm.cs                  # host; owns AppSession; switches Run/Edit
│   ├── FloatingButton.cs            # Run-mode overlay button
│   └── AppSession.cs                # replaces EasyVision "Global": instance, not static
├── Wizard/
│   ├── WizardHost.cs                # Back/Next/Finish + step navigation
│   ├── Step1Capture.cs
│   ├── Step2Plc.cs
│   ├── Step3Regions.cs
│   └── Step4Output.cs
├── Controls/
│   ├── RoiCanvas.cs                 # NEW — replaces View.cs ROI editing (see §3.3)
│   ├── RegionListPanel.cs           # NEW — replaces EditRectRot side panel
│   ├── PlcSettingsPanel.cs          # NEW — slim replacement for ProtocolPLC
│   ├── BitInputRow.cs               # ported from ucBitInput
│   ├── BitOutputRow.cs              # ported from ucBitOutput
│   ├── ValueRow.cs                  # ported from ucValueInput/ucValueOutput
│   └── SnipOverlayForm.cs           # NEW — Snipping-Tool-style region picker
└── EasyScreen.csproj

EasyScreen.Core/                     # Class Library, .NET Framework 4.8, x64
├── Model/
│   ├── ScreenProject.cs
│   ├── ScreenRegion.cs
│   ├── StringToIntMap.cs
│   ├── ScreenCaptureTarget.cs
│   ├── PlcConfig.cs
│   └── Enums.cs
├── Geometry/
│   ├── RectRotate.cs                # COPIED verbatim from BeeGlobal
│   └── AnchorPoint.cs               # COPIED (enum extracted)
├── Imaging/
│   ├── ScreenCaptureService.cs      # NEW — P/Invoke capture
│   └── RegionCropper.cs             # PORTED subset of BeeCore Cropper
├── Ocr/
│   ├── ScreenOcrRuntime.cs          # PORTED core of OCR.DoWork
│   └── OcrReadResult.cs
├── Plc/
│   ├── PlcClient.cs                 # COPIED from BeeGlobal (namespace PlcLib)
│   ├── TimingUtils.cs               # COPIED (PlcClient's only BeeGlobal dep)
│   ├── ScreenPlcLink.cs             # NEW — slim replacement for ParaProtocol
│   ├── ScreenValue.cs               # PORTED from ParaValue
│   └── ScreenBit.cs                 # PORTED from ParaBit
├── Runtime/
│   ├── ScreenCheckRunner.cs         # NEW — sequential runner
│   └── TriggerService.cs            # NEW — PLC bit-input polling
└── Persistence/
    └── ProjectStore.cs              # NEW — .esproj JSON save/load
```

Both projects are added to a **new `EasyScreen.sln`**, not to `EasyVision.sln` — they share no build dependency. (Adding them to `EasyVision.sln` as unreferenced projects is also acceptable if the user prefers one solution; see §9 Q1.)

---

## 3. Reuse Map — What Is Actually Copyable

This section is the most important result of the research pass. **"Copy the BeeInterface controls" is only partially achievable**, and the reason matters.

### 3.1 Copy verbatim (clean, self-contained)

| Source | Lines | Coupling found | Action |
|---|---|---|---|
| `BeeGlobal/RectRotate.cs` | 989 | **None.** Only `System`, `System.Collections.Generic`, `System.Drawing`, `System.Linq`. Pure geometry: `GetBoundingRect`, `ContainsPointRect`, `CenterLocalToWorld`, `WorldToLocal`, rotation normalize | Copy as-is → `EasyScreen.Core/Geometry/RectRotate.cs`, change namespace |
| `BeeGlobal/Enums.cs:371` `AnchorPoint` | 9 | None | Extract enum only (`None, TopLeft, TopRight, BottomLeft, BottomRight, Rotation, Center, TopCenter, RightCenter, BottomCenter, LeftCenter`). Drop hexagon `V0..V5`, `RingInner`, `Vertex` — not needed for rectangles |
| `BeeGlobal/TimingUtils.cs` | ~60 | None (winmm P/Invoke) | Copy as-is (accurate-delay helper for TriggerService poll pacing) |

> **CORRECTED during ES.2 (see EasyScreen_CODEX_HISTORY.md).** The original plan proposed copying `BeeGlobal/Protocol/PlcClient.cs` (1,459 lines) as the PLC transport. Verification against the real source found **45 references to EasyVision `Global.*`** (`Global.Config`, `Global.LogsDashboard`, `Global.PLCStatus`, `Global.IsAllowReadPLC`, `Global.Comunication`) plus BeeGlobal `LogEntry`/`LeveLLog`/`PLCStatus` types — it is heavily coupled, not cleanly copyable.
>
> **PlcClient is therefore NOT copied.** It only wraps HslCommunication brand drivers (`MelsecMcNet`, `KeyenceMcNet`, `OmronFinsNet`, `SiemensS`, `ModbusTcpNet`, `Delta*`, `Panasonic*`, …), all of which implement HslCommunication's **`IReadWriteNet`** interface. `ScreenPlcLink` (§4.6) constructs the driver via a small brand switch and calls `IReadWriteNet` (`ReadInt16/Int32/Float/Double/Bool`, `ReadString(addr,len)`, and matching `Write*`) directly. Same multi-brand support, ~60 lines instead of 1,459, and zero EasyVision coupling.

### 3.2 Port with adaptation (small, needs retargeting)

| Source | Lines | Adaptation required |
|---|---|---|
| `BeeGlobal/Protocol/ParaValue.cs` | 295 | Keep `ParaValueConverter.TryConvert` verbatim (the `TypeVar` string→typed parser is exactly what Step 3 needs). Delete `ParaValueSourceMap` entirely — its `TypeValuePLC` sources (TotalOK/TotalNG/Qty/Cycle/NoProg…) are EasyVision inspection counters with no meaning in EasyScreen. Result: `ScreenValue { Name, Address, TypeVar, Direction, Value }` |
| `BeeGlobal/Protocol/ParaBit.cs` | ~60 | Strip `ValueInput`/`ValueOutput` legacy enums; keep `{ Address, BitIndex, Function, IsBlink }` |
| `BeeInterface/PLC/ucBitInput.cs` + `.Designer.cs` | 127 + designer | Retarget `ParaBit` → `ScreenBit`; drop `System.Data.OleDb` and `OpenCvSharp.Flann` (unused stray usings) |
| `BeeInterface/PLC/ucBitOutput.cs` | 121 | Same |
| `BeeInterface/PLC/ucValueInput.cs` / `ucValueOutput.cs` | 89 + 89 | Retarget `ParaValue` → `ScreenValue`; remove `TypeValuePLC` source dropdown (EasyVision-specific) |
| `BeeInterface/PLC/ucValuePicker.cs` | 55 | Reuse as the Step-3 "target PLC value" combo |
| `BeeCore/Func/Crop.cs` → `Cropper.CropRotatedRect` | subset of 1,538 | Extract only `CropRotatedRect` + `CropRotatedRectTimed` chain and its mask compositing. Drop `PatchCropContext`, the three `UltraFast*` variants, and all timing instrumentation → ~200 lines |
| `BeeCore/Unit/OCR.cs:182-345` (`DoWork` core) | ~140 | Remove **all** `Common.TryGetTool(IndexThread, Index)` calls (score accumulation, status, logging) and `ImageSourceRuntimeService`. Keep: `Py.GIL()` block, `find_ocr(ptr,h,w,ch,stride,name,limit)`, `boxes/scores/labels` parsing, `ConvertBoxToPoints`, `MinAreaRect` + width<height angle correction, `sAllow` character filter, left-to-right sort by `Box._PosCenter.X` |
| `BeeCore/Common.cs:353-424` (Python bootstrap) | ~70 | Keep `Runtime.PythonDLL` assignment, `PythonEngine.Initialize()`, `BeginAllowThreads()`, `Py.Import("Tool.OCR")` → `OCR` class instance. Drop Yolo/CraftOCR imports and `Global.*` flags → local `IsOcrReady` |

### 3.3 CANNOT be copied — must be written new

This is the critical correction to the first draft of this plan.

| EasyVision component | Lines | Why it cannot be copied | Replacement |
|---|---|---|---|
| **`BeeInterface/Group/View.cs`** | **10,159** | This is where ROI **drag / resize / rotate / hit-test / cursor** actually lives (`imgView_MouseDown:688`, `imgView_MouseMove:825`, `_dragAnchor:345`, `ShowCursor:350`). It is fused with EasyVision's inspection canvas, shape editors, overlays, zoom/scroll globals | **`RoiCanvas : ImageBox` (new, ~350 lines)** — zoom/pan inherited from the same Cyotek NuGet package EasyVision uses, so only ROI logic is written. See §5.4 |
| `BeeInterface/ShapeEditing/ImageCanvasControl.cs` | 2,258 | Adds EasyVision shape editors, `IImageCanvas`, navigation locking on top of `ImageBox` | Not copied — EasyScreen derives from `ImageBox` directly |
| **`BeeInterface/Group/EditRectRot.cs`** + Designer | 903 + 1,347 | It is **only the side panel**. All real editing is delegated to `View` via `Global.EditTool.View` (line 191). It reads/writes `Global.IndexRotChoose`, `Global.StatusDraw`, `Global.Config.SizeCCD`, `Global.TypeCrop` — five pieces of EasyVision global state. Copying it yields a panel wired to a canvas that does not exist | **`RegionListPanel.cs` (new, ~300 lines)** |
| **`BeeGlobal/Protocol/ParaProtocol.cs`** | **7,608** | Inspection-cycle state machine: program counters, flowcharts, lanes, EtherCAT, ladder bridge, Yolo hooks, 40+ system values. EasyScreen needs ~5% of it | **`ScreenPlcLink.cs` (new, ~350 lines)** — see §4.5 |
| **`BeeInterface/PLC/ProtocolPLC.cs`** | **5,880** | Full hardware manager UI: multi-device profiles, EtherCAT/PCI/ladder topologies, KV-ML16V commissioning, auto-scan, import. The Single/Word buttons (`btnRunModeWord:725`, `btnTopologySingle:726`) are 2 lines inside a 5,880-line form | **`PlcSettingsPanel.cs` (new, ~400 lines)** — see §5.3 |

**Net effect:** ~2,500 lines copied verbatim, ~800 lines ported, ~1,650 lines written new. Versus ~25,000 lines if the coupled components were copied wholesale.

---

# BACKEND SPECIFICATION

## 4. Backend Detail

### 4.1 Data model (`EasyScreen.Core/Model/`)

All model types are plain POCOs serialized by Newtonsoft.Json. No `[Serializable]`, no `BinaryFormatter` — decision #3 frees us from EasyVision's serialization constraints.

```csharp
// ScreenProject.cs — root document, one .esproj file
public sealed class ScreenProject
{
    public int SchemaVersion  = 1;
    public string Name        = "";
    public DateTime SavedUtc;

    public ScreenCaptureTarget Capture = new ScreenCaptureTarget();
    public PlcConfig           Plc     = new PlcConfig();
    public List<ScreenRegion>  Regions = new List<ScreenRegion>();
    public OcrSettings         Ocr     = new OcrSettings();

    // Reference image registered in Step 1. Stored as base64 PNG inside the
    // .esproj so a project is a single portable file.
    public string RegisterImagePngBase64 = "";

    [JsonIgnore] public Bitmap RegisterImage;   // hydrated on load
}

// ScreenCaptureTarget.cs
public sealed class ScreenCaptureTarget
{
    public CaptureKind Kind = CaptureKind.Window;

    // Re-resolution keys for Run mode (HWND is not stable across restarts)
    public string WindowTitle       = "";
    public string WindowClass       = "";
    public string ProcessName       = "";
    public WindowMatchMode MatchMode = WindowMatchMode.TitleContains;

    public bool ClientAreaOnly = true;

    // Used when Kind == FullScreenRegion
    public Rectangle ScreenRegion;

    // Captured size at registration time ("master size"). Guards against the
    // target window being resized between Edit and Run, which would invalidate
    // every ROI.
    public Size RegisteredSize;

    // A live capture whose width or height deviates from RegisteredSize by more
    // than this percentage blocks the run and tells the operator to maximize the
    // target window (see §4.9).
    public double SizeTolerancePercent = 5.0;
}

public enum CaptureKind     { Window, FullScreenRegion }
public enum WindowMatchMode { TitleExact, TitleContains, ProcessName }

// ScreenRegion.cs — one check region
public sealed class ScreenRegion
{
    public string Id   = Guid.NewGuid().ToString("N");
    public string Name = "Region 1";
    public int    Order;                       // sequential execution order

    public RectRotate Roi  = new RectRotate();
    public RectRotate Mask;                    // optional exclusion area, nullable

    public RegionDataType DataType = RegionDataType.String;
    public StringToIntMap Map      = new StringToIntMap();

    public string TargetValueName = "";        // -> ScreenValue.Name in PlcConfig.Values
    public RegionUsage Usage = RegionUsage.Used;

    // OCR tuning, mirrors OCR.cs fields
    public string AllowChars = "0123456789";
    public int Clahe = 2, Sigma = 2, Blur = 1;
    public bool StripWhitespace = true;

    [JsonIgnore] public string   LastText;
    [JsonIgnore] public object   LastValue;
    [JsonIgnore] public CheckResult LastResult = CheckResult.None;
    [JsonIgnore] public string   LastError;
    [JsonIgnore] public long     LastElapsedMs;
}

public enum RegionDataType { Int, String, MappedInt }
public enum RegionUsage    { Used, NotUsed }        // no Invert, per requirement
public enum CheckResult    { None, OK, NG, Error }

// StringToIntMap.cs
public sealed class StringToIntMap
{
    public List<MapEntry> Entries = new List<MapEntry>();
    public int  DefaultValue      = 0;
    public bool FailWhenUnmapped  = true;      // unmapped -> NG instead of DefaultValue
    public bool IgnoreCase        = true;
    public bool TrimWhitespace    = true;

    public bool TryMap(string text, out int value);
    public static StringToIntMap CreateDefault();   // OK=1, NG=0, Run=1, Stop=2, Error=3
}
public sealed class MapEntry { public string Key = ""; public int Value; }

// OcrSettings.cs
public sealed class OcrSettings
{
    public string PythonPath = "";      // folder containing python39.dll
    public string ModelName  = "EasyScreen";
    public int    TimeoutMs  = 5000;
}
```

### 4.2 PLC configuration model

```csharp
// PlcConfig.cs — everything Step 2 produces, saved in the .esproj
public sealed class PlcConfig
{
    public PlcBrand      Brand      = PlcBrand.Keyence;   // from copied PlcClient
    public ConnectionKind Connection = ConnectionKind.Tcp;

    public string Ip   = "192.168.0.10";
    public int    Port = 8501;
    public string ComPort  = "COM3";
    public int    BaudRate = 115200;
    public Parity Parity   = Parity.Even;
    public byte   StationId = 0;

    // "Single and Word like the current Hardware page"
    public HardwareTopology Topology = HardwareTopology.Single;  // Single only in v1
    public HardwareRunMode  RunMode  = HardwareRunMode.BitWord;  // Bit+Word addressing

    // Word-mode base addresses: bits are addressed as "<word>.<bit>"
    public string InputWordAddress  = "DM100";
    public string OutputWordAddress = "DM200";

    public List<ScreenBit>   Bits   = new List<ScreenBit>();
    public List<ScreenValue> Values = new List<ScreenValue>();

    public bool IsBypass = false;      // run without a PLC, for offline testing
    public int  PollIntervalMs = 100;
}

public enum ConnectionKind   { Tcp, Serial }
public enum HardwareTopology { Single }                  // reserved for future
public enum HardwareRunMode  { BitWord }                 // reserved for future

// ScreenBit.cs — ported from ParaBit
public sealed class ScreenBit
{
    public BitDirection Direction;      // In | Out
    public BitFunction  Function;       // see below
    public string Address = "";         // explicit, or "" to use word base + BitIndex
    public int    BitIndex;
    public bool   IsBlink;
    [JsonIgnore] public bool LastState;
}
public enum BitDirection { In, Out }

// Trimmed from EasyVision's I_O_Input / I_O_Output (BeeIOContracts/IO/IoEnums.cs)
// to only what EasyScreen actually needs.
public enum BitFunction
{
    None,
    // Inputs
    Trigger, Reset, Bypass,
    // Outputs
    Result,        // overall OK
    ResultNg,      // overall NG
    Ready,         // idle and able to accept a trigger
    Busy,          // check in progress
    Error,         // capture/OCR/PLC failure
    Heartbeat      // blink while alive
}

// ScreenValue.cs — ported from ParaValue (ParaValueSourceMap removed)
public sealed class ScreenValue
{
    public string Name    = "";        // referenced by ScreenRegion.TargetValueName
    public string Address = "";
    public TypeVar TypeVar = TypeVar.Int;
    public ValueDirection Direction = ValueDirection.Out;
    public int StringWordLength = 16;  // for TypeVar.String
    [JsonIgnore] public object Value;
}
public enum ValueDirection { In, Out }
public enum TypeVar { Int, Float, String, Bool, Int16, Int32, Double }   // copied
```

### 4.3 `ScreenCaptureService` (new) — must support **any** window technology

**Requirement:** the target application is **not necessarily WinForms**. EasyScreen must read WPF, Qt, Electron/Chromium, Java/Swing, UWP/WinUI, Delphi/VCL, MFC, and GPU/DirectX-rendered windows. No single Win32 API covers all of them, so capture is built as **pluggable engines with automatic probing**.

#### 4.3.1 Compatibility matrix by UI framework

| Target framework | `BitBlt` | `PrintWindow` + `PW_RENDERFULLCONTENT` | **WGC** | `CopyFromScreen` |
|---|:---:|:---:|:---:|:---:|
| WinForms / MFC / Delphi VCL (GDI) | ✅ | ✅ | ✅ | ✅ |
| WPF (D3D9/DWM composited) | ❌ black | ✅ | ✅ | ✅ |
| Qt Widgets | ⚠ partial | ✅ | ✅ | ✅ |
| Qt Quick / QML (OpenGL) | ❌ | ⚠ often black | ✅ | ✅ |
| Java Swing / AWT / JavaFX | ⚠ | ✅ (Swing) / ❌ (JavaFX) | ✅ | ✅ |
| Electron / Chromium / CEF | ❌ | ⚠ needs child HWND | ✅ | ✅ |
| Browsers with GPU accel. | ❌ | ⚠ | ✅ | ✅ |
| UWP / WinUI 3 | ❌ | ❌ | ✅ | ✅ |
| DirectX / OpenGL / games | ❌ | ❌ | ✅ | ✅ |
| **Works when occluded / background** | ✅ | ✅ | ✅ | ❌ |

**Conclusion: WGC (Windows Graphics Capture) is the only engine that covers every framework** — it captures the DWM-composited output, so how the app renders is irrelevant. It is what Snipping Tool and Teams use. `CopyFromScreen` also works everywhere but only if the window is visible and unobstructed, which is unacceptable in production. Therefore **WGC is the primary engine and the others are fallbacks** for older OS builds.

#### 4.3.2 Engine architecture

```csharp
public interface ICaptureEngine : IDisposable
{
    CaptureEngineKind Kind { get; }
    bool IsAvailable(out string reason);                 // OS/runtime support check
    bool TryCapture(IntPtr hWnd, bool clientAreaOnly, out Mat frame, out string error);
    bool TryCaptureRegion(Rectangle screenRegion, out Mat frame, out string error);
}

public enum CaptureEngineKind
{
    Auto = 0,           // probe and pick — default
    GraphicsCapture,    // WGC        — all frameworks, Win10 1803+
    PrintWindow,        // PW_RENDERFULLCONTENT — most GDI/WPF
    BitBlt,             // GDI only
    CopyFromScreen      // universal, requires unobstructed
}

public static class ScreenCaptureService
{
    public static IReadOnlyList<WindowInfo> EnumerateTopLevelWindows();
    public static IReadOnlyList<WindowInfo> EnumerateChildWindows(IntPtr parent);
    public static IntPtr ResolveWindow(ScreenCaptureTarget target, out string error);

    public static Mat Capture(ScreenCaptureTarget target, out CaptureDiagnostics diag);

    // Runs every available engine against the target and reports which ones work.
    // Step 1 calls this once; the winner is stored in ScreenCaptureTarget.Engine.
    public static CaptureProbeReport Probe(IntPtr hWnd, bool clientAreaOnly);
}

public sealed class WindowInfo
{
    public IntPtr Handle; public string Title; public string ClassName;
    public string ProcessName; public Rectangle Bounds;
    public bool IsMinimized; public bool IsCloaked;      // UWP hidden windows
    public UiFrameworkGuess Framework;                   // from ClassName heuristics
}

public sealed class CaptureDiagnostics
{
    public bool Success; public string Error;
    public CaptureEngineKind EngineUsed;
    public bool FellBack;                 // preferred engine failed
    public long ElapsedMs; public Size Size;
}

public sealed class CaptureProbeReport
{
    public List<CaptureProbeResult> Results;   // one per engine
    public CaptureEngineKind Recommended;
}
public sealed class CaptureProbeResult
{
    public CaptureEngineKind Kind; public bool Available, Succeeded, LooksBlank;
    public long ElapsedMs; public string Error;
}
```

`ScreenCaptureTarget` gains two fields:

```csharp
public CaptureEngineKind Engine = CaptureEngineKind.Auto;  // chosen in Step 1, saved
public bool CaptureChildWindow = false;                    // Chromium/Electron case
public string ChildWindowClass = "";                       // e.g. Chrome_RenderWidgetHostHWND
```

#### 4.3.3 `GraphicsCaptureEngine` (WGC) — the primary engine

This is the single most valuable component for non-WinForms support, and the most involved to build (~450 lines). Specification:

**Availability:** Windows Graphics Capture itself begins at Windows 10 build 17134 (1803), but direct HWND capture through `IGraphicsCaptureItemInterop.CreateForWindow` requires build 18362 (1903). EasyScreen therefore requires build 18362 for this engine and also checks `GraphicsCaptureSession.IsSupported()` at runtime. If unavailable, `IsAvailable` returns false and the engine is skipped.

**NuGet / interop for .NET Framework 4.8:**
- `Microsoft.Windows.SDK.Contracts` (10.0.19041.x) — WinRT projections for .NET Framework
- `System.Runtime.WindowsRuntime`
- A D3D11 interop layer — **`Vortice.Direct3D11` / `Vortice.DXGI` 2.4.2**. This is the final Vortice release with `netstandard2.0` assets compatible with .NET Framework 4.8; newer Vortice releases require modern .NET. Do not hand-roll D3D11 P/Invoke.

**Capture sequence:**

1. Create a D3D11 device: `D3D11CreateDevice(BgraSupport)` → get its `IDXGIDevice` → `CreateDirect3D11DeviceFromDXGIDevice` → `IDirect3DDevice`.
2. Create the capture item from the HWND via the COM interop interface `IGraphicsCaptureItemInterop` (GUID `3628E81B-3CAC-4C60-B7F4-23CE0E0C3356`), method `CreateForWindow(hWnd, iid, out item)`. There is no managed API for this — the interop cast is mandatory.
3. Create `Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, numberOfBuffers: 2, item.Size)`. **Use the free-threaded variant** — the worker thread has no `DispatcherQueue`, and the non-free-threaded factory throws without one.
4. `session = framePool.CreateCaptureSession(item)`. Set `session.IsCursorCaptureEnabled = false` (a mouse cursor over a digit ruins OCR). On Windows 11 (build 22000+) also set `session.IsBorderRequired = false` to suppress the yellow capture border; guard the property with a reflection/version check since it does not exist on Win10.
5. `session.StartCapture()`, then wait for a frame on `framePool.FrameArrived` with a timeout (default 1000 ms). **Take the second frame, not the first** — the first is frequently blank while the pool warms up.
6. Copy the frame's `IDirect3DSurface` into a CPU-readable **staging texture** (`ResourceUsage.Staging`, `CpuAccessFlags.Read`), `Map` it, and copy row by row honouring `RowPitch` into a `Mat` of type `CV_8UC4`.
7. `Cv2.CvtColor(BGRA2BGR)` to produce the `CV_8UC3` the OCR path expects.
8. Dispose session, frame pool, staging texture, and the frame — **in that order**, every time, including on the error path. A leaked frame pool pins GPU memory and the app dies after a few hours of production use.

**Lifetime:** creating a D3D device and frame pool per capture costs ~15–30 ms. Because the runner captures once per cycle, **cache the device, item, frame pool and session per target HWND** and reuse them; recreate only when the HWND changes or `item.Closed` fires. Add `GraphicsCaptureEngine.Reset()` for when the target window is closed and reopened.

**Known constraints to encode as guard rails:**
- WGC cannot capture a **minimized** window — `IsIconic` must be checked first and reported as a specific error, not a generic failure.
- `item.Closed` fires when the target closes; the engine must surface this as "target window closed" so Run mode shows a clear operator message rather than a black frame.
- Windows may show a yellow border on Win10; this is cosmetic, does not appear in the captured pixels, and must not be treated as a bug.
- WGC returns the **window including its DWM frame**; when `ClientAreaOnly` is true, crop using `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` versus `GetClientRect`/`ClientToScreen`.

#### 4.3.4 Child-window handling (Electron / Chromium / CEF)

Chromium renders into a **child** HWND, so `PrintWindow` on the top-level handle can return the frame furniture with a blank content area. When the probe detects this:

- `EnumerateChildWindows` lists descendants with their class names.
- Known content classes to offer first: `Chrome_RenderWidgetHostHWND` (Chromium/Electron/CEF), `Intermediate D3D Window` (Chromium GPU), `SunAwtCanvas` (Java AWT), `QWindowIcon`/`Qt5152QWindowIcon` (Qt), `HwndWrapper[*]` (WPF).
- The chosen child class is stored in `ChildWindowClass` and re-resolved at Run time — child handles are not stable across restarts, the class name is.

WGC on the top-level window generally makes this unnecessary, which is another reason it is the primary engine. Keep the child path for the fallback engines.

#### 4.3.5 Framework detection (advisory)

`UiFrameworkGuess` is inferred from the window class name and used only to pre-select an engine and to show a helpful hint in Step 1:

| Class name pattern | Guess | Suggested engine |
|---|---|---|
| `HwndWrapper[*` | WPF | PrintWindow (WGC if blank) |
| `Chrome_WidgetWin_*` | Chromium / Electron | **WGC** |
| `Qt*QWindow*` | Qt | **WGC** |
| `SunAwt*` | Java | PrintWindow |
| `WindowsForms10.*` | WinForms | PrintWindow |
| `TfrmMain`, `TApplication` | Delphi VCL | PrintWindow |
| `ApplicationFrameWindow`, `Windows.UI.Core.CoreWindow` | UWP / WinUI | **WGC** |
| `Afx:*` | MFC | PrintWindow / BitBlt |

This is a **hint only** — the probe result always wins over the guess.

#### 4.3.6 Engine selection at runtime

```
Capture(target):
  if target.Engine != Auto:
      try that engine
      on failure -> fall through the ordered list below, set diag.FellBack = true
  ordered fallback: GraphicsCapture -> PrintWindow -> BitBlt -> CopyFromScreen
  each attempt runs the blank-frame probe; a blank result counts as a failure
  if all fail -> diag.Success = false with a per-engine reason list
```

The engine that actually succeeded is recorded in `CaptureDiagnostics.EngineUsed` and logged (§4.10) whenever it differs from the configured one — a silent fallback in production is exactly the kind of drift that must be visible.

**P/Invoke surface required:**

| API | DLL | Purpose |
|---|---|---|
| `EnumWindows`, `EnumChildWindows`, `IsWindowVisible`, `GetWindowTextW`, `GetClassNameW`, `GetWindowThreadProcessId` | user32 | Build the window/child list |
| `GetWindowRect`, `GetClientRect`, `ClientToScreen` | user32 | Geometry; client-area-only capture |
| `PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT=0x2)` | user32 | Fallback engine — most GDI and WPF windows, works occluded |
| `GetWindowDC`, `ReleaseDC`, `BitBlt` (`SRCCOPY \| CAPTUREBLT`) | user32/gdi32 | Fallback engine — GDI only |
| `Graphics.CopyFromScreen` | GDI+ | Last-resort engine and full-screen region capture |
| `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` | user32 | Correct coordinates on scaled displays |
| `IsIconic`, `ShowWindow(SW_MAXIMIZE)`, `SetForegroundWindow`, `IsZoomed` | user32 | Minimized check; the operator Maximize button (§5.2.4) |
| `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS, DWMWA_CLOAKED)` | dwmapi | Exclude the drop-shadow border; detect cloaked UWP windows |
| `IGraphicsCaptureItemInterop::CreateForWindow` (COM) | WinRT interop | **WGC engine** — see §4.3.3 |
| `D3D11CreateDevice`, `CreateDirect3D11DeviceFromDXGIDevice` | d3d11 | **WGC engine** D3D device |

**Blank-frame probe (used by every engine).** GPU-composited windows silently return an all-black bitmap from `BitBlt`/`PrintWindow` instead of failing. After every capture:

- Sample a 16 × 16 grid of pixels plus the four quadrant centres.
- Compute mean luminance and standard deviation.
- Treat as blank when `stdDev < 1.0` (a uniformly flat image), whatever the mean — this catches all-black *and* all-white results.
- A blank result is a **failure**: fall through to the next engine, and record `LooksBlank = true` in the probe report.

This probe is what makes automatic engine selection work; without it a Chromium window would appear to capture successfully and produce blank OCR forever.

**DPI:** call `SetProcessDpiAwarenessContext` in `Program.Main` before any window is created, and add `<dpiAwareness>PerMonitorV2</dpiAwareness>` to `app.manifest`. All ROI coordinates are stored **relative to the captured bitmap**, never to screen coordinates — this makes ROIs immune to the window being moved.

**Size guard:** `Capture` compares the captured size to `ScreenCaptureTarget.RegisteredSize` and returns the verdict alongside the frame. See §4.4 — this is a first-class blocking check in Run mode, not a soft warning.

### 4.4 Master-size guard (`CaptureSizeGuard`)

The ROIs in a project are drawn on the **master image** registered in Step 1. If the operator later runs the target application in a restored/resized window, every ROI reads the wrong pixels. EasyScreen must catch this and tell the operator to maximize the window before continuing.

```csharp
public static class CaptureSizeGuard
{
    public static SizeCheck Evaluate(Size captured, ScreenCaptureTarget target);
}

public sealed class SizeCheck
{
    public bool   IsWithinTolerance;
    public Size   Captured, Registered;
    public double WidthDeviationPercent, HeightDeviationPercent, MaxDeviationPercent;
    public string OperatorMessage;   // localized, shown verbatim in Run mode
}
```

Deviation is computed per axis against the registered master:

```
widthDev  = |captured.Width  - registered.Width | / registered.Width  * 100
heightDev = |captured.Height - registered.Height| / registered.Height * 100
maxDev    = max(widthDev, heightDev)
IsWithinTolerance = maxDev <= target.SizeTolerancePercent      // default 5.0
```

The check runs **before any OCR work**, so a mis-sized window costs one capture, not a full region sweep.

**Behaviour when the check fails:**

| Mode | Behaviour |
|---|---|
| **Run** | The cycle is **aborted before OCR**. `BitFunction.Error` is set, `Result`/`ResultNg` are left untouched (no false OK/NG is reported to the PLC), and the operator notification in §5.2.4 is raised. The next trigger re-evaluates — as soon as the window is maximized the run proceeds normally with no restart needed |
| **Edit / Step 3 live test** | Inline warning strip above the canvas; the test still runs so an engineer can diagnose, but the result is badged *"size mismatch — result unreliable"* |

`OperatorMessage` is deliberately plain-language and actionable, since a line operator reads it, not an engineer:

> **Window size does not match the registered master.**
> Registered: 1920 × 1080  ·  Now: 1280 × 720  (33 % smaller)
> Please **maximize the application window to full screen**, then trigger again.

### 4.5 `ScreenOcrRuntime` (ported)

```csharp
public static class ScreenOcrRuntime
{
    public static bool  IsReady { get; }
    public static string LastInitError { get; }

    public static bool EnsureInitialized(OcrSettings settings);
    public static void EnsureModel(string modelName);
    public static OcrReadResult Read(Mat matCrop, string allowChars, string modelName);
    public static void Shutdown();
}

public sealed class OcrReadResult
{
    public bool   Success;
    public string Text;          // tokens joined, filtered by allowChars
    public List<string> Tokens;  // per detected box, sorted left-to-right
    public List<float>  Scores;
    public List<RectRotate> Boxes;
    public string Error;
    public long   ElapsedMs;
}
```

**Port procedure — read `BeeCore/Unit/OCR.cs:182-345` and carry over exactly this:**

1. `using (Py.GIL())` wrapper — mandatory, non-negotiable.
2. Ensure the Mat is `CV_8UC3` and continuous (`OCR.cs:205-215`): convert GRAY→RGB, clone if `!IsContinuous()`.
3. Extract `h = Rows`, `w = Cols`, `ch = Channels()`, `stride = (int)Step()`, `p = Data`.
4. Call `G.objOCR.find_ocr((long)p, h, w, ch, stride, modelName, limit)` — pass `limit = 0` (EasyVision hardcodes `limit = 0` at `OCR.cs:224` regardless of `LimitArea`).
5. Result is a 3-tuple: `boxes`, `scores`, `labels`. Guard: null check, `Length() == 0` check, and `boxes.Length() != labels.Length()` check — all three exist in the original and all three are load-bearing.
6. Per box: `ConvertBoxToPoints` → `Cv2.MinAreaRect` → if `width < height` swap and `Angle += 90`; if `Angle > 145` then `Angle = -(180 - Angle)`.
7. Filter each label through `AllowChars`: `new string(label.Where(c => allowed.Contains(c)).ToArray())`.
8. Sort all results by `Box._PosCenter.X` ascending (left-to-right reading order).

**Delete during the port:** every `Common.TryGetTool(IndexThread, Index)` reference (score accumulation, `.Results`, `.ScoreResult`, `.StatusTool`), every `Global.LogsDashboard.AddLog` call (replace with `OcrReadResult.Error`), and the `ImageSourceRuntimeService.CloneFrameForProgram` frame acquisition — EasyScreen passes the Mat in directly.

**Initialization** (ported from `BeeCore/Common.cs:353-424`): set `Python.Runtime.Runtime.PythonDLL = Path.Combine(settings.PythonPath, "python39.dll")`, verify the file exists, `PythonEngine.Initialize()`, `PythonEngine.BeginAllowThreads()`, then inside `Py.GIL()` do `Py.Import("Tool.OCR")` → `GetAttr("OCR")` → `Invoke()`. Requires `Tool/OCR.py` next to the executable. `EnsureModel` calls `initialize_ocr(modelName)` once per model name (`OCR.cs:543`).

> **Threading:** Python must be initialized on one thread and `BeginAllowThreads` must be called exactly once per process. The worker thread that runs checks acquires the GIL per call. Never call `PythonEngine.Shutdown()` on the UI thread while a check is running.

### 4.6 `ScreenPlcLink` (new — replaces 7,608-line ParaProtocol)

```csharp
public sealed class ScreenPlcLink : IDisposable
{
    public PlcConfig Config { get; }
    public bool IsConnected { get; }
    public event Action<bool>   ConnectionChanged;
    public event Action<string> LogMessage;

    public ScreenPlcLink(PlcConfig config);

    public bool Connect(out string error);
    public void Disconnect();

    // --- Bits -------------------------------------------------------
    public bool ReadBit (BitFunction fn, out bool state);
    public bool WriteBit(BitFunction fn, bool state);
    private string ResolveBitAddress(ScreenBit bit);   // "DM100.3" in Word mode

    // --- Values -----------------------------------------------------
    public bool SetValue(string name, object value);   // stage in memory
    public bool WriteValue(ScreenValue v);             // push one value now
    public int  FlushOutputs();                        // push all Direction==Out; returns count written
    public bool ReadValue(ScreenValue v);              // pull one Direction==In value

    public ScreenValue FindValue(string name);
    public IReadOnlyList<string> OutputValueNames();   // feeds the Step-3 combo
}
```

**Implementation notes:**

- Wraps the copied `PlcLib.PlcClient`. `Connect` maps `PlcConfig` → the brand-specific client constructor.
- `ResolveBitAddress`: in `HardwareRunMode.BitWord`, an input bit is `$"{Config.InputWordAddress}.{bit.BitIndex}"` and an output bit is `$"{Config.OutputWordAddress}.{bit.BitIndex}"`, unless `bit.Address` is explicitly set. This mirrors EasyVision's pattern at `ParaProtocol.cs:2392` (`PlcClient.WriteBit(AddRead + "." + AddressInput[...], Value)`).
- Value read/write switches on `TypeVar` exactly as `ParaProtocol.ReadOneValueInput:2100` / `WriteOneValueOutput:2180` do — reuse that switch verbatim, including `ReadStringAsciiKey(addr, 16)` and `WriteString(addr, s, 16)`.
- `SetValue` only stages into `ScreenValue.Value`; `FlushOutputs` performs the actual PLC writes at the end of a cycle. This is the same two-phase pattern as `SetValueByName` (`ParaProtocol.cs:2228`) + `WriteValueOutputs` (`ParaProtocol.cs:2218`), and it keeps one PLC round-trip per value per cycle rather than per region.
- `IsBypass == true` short-circuits every read/write to a no-op returning success, so the whole app is testable without hardware.

### 4.7 `TriggerService` (new)

```csharp
public sealed class TriggerService : IDisposable
{
    public event Action TriggerRisingEdge;
    public event Action ResetRequested;

    public void Start(ScreenPlcLink link, int pollMs);
    public void Stop();
}
```

- A single background `Task` loop with `CancellationToken`, polling `BitFunction.Trigger` every `Config.PollIntervalMs` (default 100 ms).
- **Rising-edge detection only:** fires when the previous read was `false` and the current is `true`. A held-high trigger fires exactly once.
- Skips polling entirely while a check is already running (`Interlocked` busy flag) so a slow OCR pass cannot queue triggers.
- Also drives `BitFunction.Heartbeat` (toggle each poll) and `BitFunction.Ready` (true when idle and connected).
- All events marshal to the UI thread via `SynchronizationContext` captured at `Start`.

### 4.8 `ScreenCheckRunner` (new — sequential only)

```csharp
public sealed class ScreenCheckRunner
{
    public ScreenCheckRunner(ScreenPlcLink plc);

    public event Action<ScreenRegion> RegionCompleted;   // per-region progress
    public event Action<SizeCheck>    SizeGuardTripped;  // window not maximized (§4.4)
    public event Action<RunPhase>     PhaseChanged;      // drives the button colour (§5.2.1)
    public ScreenRunOutcome Run(ScreenProject project, Mat frame);
}

public sealed class ScreenRunOutcome
{
    public CheckResult Overall;
    public int  RegionsChecked, RegionsSkipped, ValuesWritten;
    public long ElapsedMs;
    public string Error;
    public List<ScreenRegion> Regions;

    // Set when the run was aborted by the master-size guard (§4.4).
    public bool      SizeGuardBlocked;
    public SizeCheck SizeCheck;
}
```

**Execution sequence (strictly ordered — decision #1):**

```
0. sizeCheck = CaptureSizeGuard.Evaluate(frame.Size(), project.Capture)
   if !sizeCheck.IsWithinTolerance:
        outcome.SizeGuardBlocked = true; outcome.Overall = Error
        plc.WriteBit(Error, true)          // Result / ResultNg deliberately untouched
        plc.WriteBit(Busy, false); plc.WriteBit(Ready, false)
        raise SizeGuardTripped(sizeCheck)  // drives the operator prompt, §5.2.4
        return outcome                     // ABORT before any OCR

1. Phase(Checking); plc.WriteBit(Busy, true); plc.WriteBit(Ready, false)
2. regions = project.Regions.Where(r => r.Usage == Used).OrderBy(r => r.Order)
3. foreach region in regions:                       // SEQUENTIAL — no Parallel.ForEach
     a. using crop = RegionCropper.CropRotatedRect(frame, region.Roi, region.Mask)
     b. ocr = ScreenOcrRuntime.Read(crop, region.AllowChars, region.Name)
     c. if !ocr.Success -> region.LastResult = Error; continue
     d. text = ocr.Text; if StripWhitespace -> strip
     e. convert per DataType:
          String    -> value = text                         ; OK
          Int       -> ParaValueConverter.TryConvert(TypeVar.Int, text, out value)
                       ; OK on success, NG on parse failure
          MappedInt -> region.Map.TryMap(text, out int mv)
                       ; OK -> value = mv
                       ; miss -> FailWhenUnmapped ? NG : (OK, value = DefaultValue)
     f. region.LastText/LastValue/LastResult assigned
     g. if TargetValueName != "" -> plc.SetValue(TargetValueName, value)   // staged
     h. raise RegionCompleted(region)
4. outcome.Overall = any(region.LastResult != OK) ? NG : OK
   (a single Error region makes Overall = Error)
5. Phase(Sending); plc.FlushOutputs()                // one batched PLC write
6. plc.WriteBit(Result,   Overall == OK)
   plc.WriteBit(ResultNg, Overall == NG)
   plc.WriteBit(Error,    Overall == Error)
7. plc.WriteBit(Busy, false); plc.WriteBit(Ready, true)
8. logger.LogCycle(outcome); imageArchive.SaveCycle(frame, outcome, project)   // §4.10
9. Phase(Overall == OK ? CompleteOk : Overall == NG ? CompleteNg : Error)
```

`Phase(Triggered)` is raised by `TriggerService` on the rising edge, before capture; the runner takes over from `Checking`. Archiving happens at step 8, **after** the PLC write, so disk I/O never delays the machine cycle.

Every `Mat` is inside a `using`. The incoming `frame` is owned by the caller and disposed by the caller.

### 4.9 `ProjectStore` (new)

```csharp
public static class ProjectStore
{
    public const string Extension = ".esproj";
    public static void Save(ScreenProject project, string path);
    public static ScreenProject Load(string path, out string warning);
    public static ScreenProject CreateDefault();
}
```

- Newtonsoft.Json, `Formatting.Indented`, `NullValueHandling.Ignore`.
- `Save` encodes `RegisterImage` to PNG → base64 → `RegisterImagePngBase64`, so a project is one portable file (decision #5: PLC config travels with it).
- `Load` decodes back to `Bitmap`, rebuilds `RectRotate` objects, and migrates on `SchemaVersion`.
- **Validation on load**, surfaced as `warning` (never a silent failure):
  - a region's `TargetValueName` no longer exists in `Plc.Values`
  - `RegisteredSize` is empty or the register image is missing
  - duplicate `ScreenValue.Name` entries
  - a `MappedInt` region with an empty map
- No `BinaryFormatter`, no `[Serializable]`, no `TypeForwardedTo` — decision #3 means there is no legacy format to stay compatible with.

### 4.10 Logging and archive (`ScreenLogger` + `ImageArchive`)

Step 4 gains a **Save Logs** option modelled on EasyVision's `LogsDashboard`. The design below is a **deliberately simplified port** — same concepts, same file format, ~15 % of the code.

#### 4.10.1 What EasyVision does (verified source)

| Concept | EasyVision implementation | Reference |
|---|---|---|
| Entry shape | `LogEntry { DateTime Time, string Level, string Source, string Message }`, `[DataContract]` with `[DataMember(Order=n)]` | `BeeGlobal/LogsDashboard.cs:21-37` |
| Levels | `LeveLLog { All, TRACE, DEBUG, INFO, WARN, ERROR, FATAL }` | `LogsDashboard.cs:19` |
| Storage | `Logs/logs.json`, `DataContractJsonSerializer<List<LogEntry>>` | `LogsDashboard.cs:271`, `SaveNow:1176` |
| Thread-safe ingest | `ConcurrentQueue<LogEntry>` drained by a UI timer, 400 entries / 16 ms | `LogsDashboard.cs:323-325`, `IngestTimer_Tick:1059` |
| Autosave | Debounce 1000 ms + heartbeat 5000 ms + `ProcessExit` | `LogsDashboard.cs:441-448` |
| Atomic write | write `.tmp` → `File.Replace(tmp, path, bak)` | `LogsDashboard.cs:1219-1231` |
| Toggles | `Save Logs`, `Perf Log`, `OnlyError`, `AutoReload` | `LogsDashboard.cs:384`, `Config.cs:305,309,311` |
| Filters | From/To date, Level, Find text, Today/7d/30d | `LogsDashboard.cs:687-699` |
| Export | Export CSV (`Time,Level,Source,Message`), Import JSON, Clear | `ExportCsv:1257`, `ImportJson:1286` |
| Image saving | `IsSaveOK`, `IsSaveNG`, `IsSaveRaw`, `IsSaveRS` | `Config.cs:442-443` |
| Retention | `LimitDateSave = 15` days, `MinFreeDiskPercentForReportCleanup = 15` | `Config.cs:425,429` |

#### 4.10.2 EasyScreen port

```csharp
// LogSettings.cs — saved inside the .esproj (decision #5)
public sealed class LogSettings
{
    public bool SaveLogs   = false;      // master switch, mirrors Config.IsSaveLog
    public bool OnlyErrors  = true;      // mirrors LogsDashboard.OnlySaveErr
    public bool LogEveryCycle = false;   // one INFO row per cycle with all region values
    public bool PerfLog     = false;     // per-region elapsed ms

    public string LogFolder = "Logs";                 // relative to the exe
    public string FileName  = "easyscreen-logs.json";
    public int    MaxEntries    = 100000;             // mirrors MaxLogCount
    public int    RetentionDays = 15;                 // mirrors LimitDateSave

    // Image archive
    public bool SaveImageOnOk = false;
    public bool SaveImageOnNg = true;
    public bool SaveRawImage    = true;   // the captured frame
    public bool SaveResultImage = false;  // frame + ROI overlays + OCR text
    public string ImageFolder = "Images";
    public ImageFormat ImageFormat = ImageFormat.Jpeg;
    public int  JpegQuality = 85;
    public int  MinFreeDiskPercent = 15;  // mirrors MinFreeDiskPercentForReportCleanup
}
public enum ImageFormat { Jpeg, Png, Bmp }

// ScreenLogger.cs
public sealed class ScreenLogger : IDisposable
{
    public ScreenLogger(LogSettings settings);
    public LogSettings Settings { get; }

    public void Log(LogLevel level, string source, string message);
    public void LogCycle(ScreenRunOutcome outcome);   // structured per-cycle row
    public void Flush();                              // force write now
    public IReadOnlyList<LogRecord> Query(DateTime from, DateTime to,
                                          LogLevel? level, string contains);
    public void ExportCsv(string path, IEnumerable<LogRecord> rows);
    public void Clear();
}

// LogRecord.cs — same four fields and the same JSON shape as EasyVision's LogEntry,
// so an EasyScreen log file opens in any tool that reads EasyVision logs.
[DataContract]
public sealed class LogRecord
{
    [DataMember(Order = 0)] public DateTime Time    { get; set; }
    [DataMember(Order = 1)] public string   Level   { get; set; }
    [DataMember(Order = 2)] public string   Source  { get; set; }
    [DataMember(Order = 3)] public string   Message { get; set; }
}
public enum LogLevel { TRACE, DEBUG, INFO, WARN, ERROR, FATAL }
```

**Ingest and write pipeline** — port the pattern, not the UI:

1. `Log(...)` enqueues to a `ConcurrentQueue<LogRecord>` and returns immediately. **It never touches disk on the calling thread** — the runner calls it from the worker thread and must not block on I/O.
2. A `System.Threading.Timer` drains the queue every 500 ms in batches of 400.
3. Writes are debounced 1000 ms, with a 5000 ms heartbeat for a dirty buffer, plus a flush on `ProcessExit` and on `EnterEditMode`.
4. Writing is atomic exactly as EasyVision does it: serialize to `MemoryStream` → write `<file>.tmp` with `fs.Flush(true)` → `File.Replace(tmp, path, bak)`. Never write the live file directly.
5. Filtering is applied at **enqueue** time, not write time: if `OnlyErrors` is on, only `ERROR`/`FATAL` are enqueued. `PerfLog` rows bypass `OnlyErrors` (matching `AddPerformanceLog`, `LogsDashboard.cs:620`).

**What gets logged** (the source column keeps EasyVision's dotted convention):

| Event | Level | Source | Message |
|---|---|---|---|
| Cycle completed | INFO | `Cycle` | `result=OK regions=3 elapsed=412ms Lot="A123" Status=2 Count=17` |
| Region NG | WARN | `Region.Status` | `text="XX" -> unmapped, expected one of OK/NG/Run/Stop/Error` |
| Region error | ERROR | `Region.Lot` | `OCR failed: <error>` |
| Size guard tripped | ERROR | `SizeGuard` | `captured=1280x720 master=1920x1080 dev=33.3% tol=5%` |
| Capture failed | ERROR | `Capture` | `PrintWindow/BitBlt/CopyFromScreen all returned black` |
| PLC write failed | ERROR | `Plc.Write` | `name=Lot addr=DM300 type=String err=<msg>` |
| PLC connect/disconnect | INFO | `Plc` | `connected brand=Keyence 192.168.0.10:8501` |
| Per-region timing | INFO | `Perf.Region` | `name=Lot crop=3ms ocr=88ms total=91ms` (only when `PerfLog`) |
| Mode change | INFO | `App` | `mode=Run project=Line3.esproj` |

**Retention** runs once at startup and once per day at first cycle after midnight:

- Delete `LogRecord`s older than `RetentionDays`, then rewrite the file atomically.
- Trim to `MaxEntries` newest if still over.
- Delete image folders older than `RetentionDays`.
- **Disk guard:** if free space on the log drive falls below `MinFreeDiskPercent`, delete oldest image day-folders first (never the log JSON), oldest first, until above the threshold; log one WARN per cleanup pass. If still below after deleting everything eligible, set `SaveImageOnOk = SaveImageOnNg = false` for the session and log a FATAL — the app must keep inspecting even when the disk is full.

```csharp
// ImageArchive.cs
public sealed class ImageArchive
{
    public ImageArchive(LogSettings settings);
    public string SaveCycle(Mat rawFrame, ScreenRunOutcome outcome, ScreenProject project);
    public void   RunRetention();
    public bool   IsDiskLow(out double freePercent);
}
```

**Layout on disk** — day-foldered so retention is a directory delete, not a file scan:

```
Logs/
  easyscreen-logs.json
  easyscreen-logs.json.bak
Images/
  2026-08-23/
    143012_337_OK_raw.jpg
    143012_337_OK_result.jpg
    143255_902_NG_raw.jpg
```

Filename: `HHmmss_fff_<OK|NG|ERR>_<raw|result>.<ext>`. The result image is the raw frame with each ROI outlined in its result colour and the OCR text drawn beside it — reusing `RoiCanvas`'s render routine against a `Graphics` from a `Bitmap` so the archived image looks like what the engineer saw in Step 3.

`SaveCycle` is called by the runner **after** `FlushOutputs`, so archiving never delays the PLC write. It runs on the worker thread, and is skipped entirely when both `SaveImageOnOk` and `SaveImageOnNg` are false — a `Mat` is never encoded when nobody asked for it.

---

# FRONTEND SPECIFICATION

## 5. Frontend Detail

### 5.1 Shell and mode switching

`MainForm` owns an `AppSession` — the instance-based replacement for EasyVision's static `Global`:

```csharp
public sealed class AppSession
{
    public ScreenProject Project { get; set; }
    public ScreenPlcLink Plc     { get; }
    public TriggerService Trigger{ get; }
    public ScreenCheckRunner Runner { get; }
    public AppMode Mode { get; private set; }      // Edit | Run
    public string ProjectPath { get; set; }
    public bool IsDirty { get; set; }

    public event Action<AppMode> ModeChanged;
    public void EnterRunMode(); public void EnterEditMode();
}
public enum AppMode { Edit, Run }
```

Per Hard Rule 0.2 #10 there is **no new static/singleton UI state**. `AppSession` is constructed once in `Program.Main` and passed by constructor injection into every step control.

**Mode transitions:**

- `EnterRunMode()`: validate the project (§5.6 gate) → connect PLC if not bypassed → hide `MainForm` → show `FloatingButton` → `Trigger.Start()`.
- `EnterEditMode()`: `Trigger.Stop()` → hide `FloatingButton` → restore `MainForm` → reload the wizard from the current project.

### 5.2 Run mode — `FloatingButton`

The core UI requirement: **must not cover the target application's window.**

| Property | Value |
|---|---|
| `FormBorderStyle` | `None` |
| `TopMost` | `true` |
| `ShowInTaskbar` | `false` |
| Size | 56 × 56 px (single round button) |
| Start position | Right edge of the working area, vertically centred: `X = Screen.PrimaryScreen.WorkingArea.Right - 56 - 8`, `Y = centre` |
| Shape | Rounded via `Region = GraphicsPath` (ellipse), so it reads as a floating dot, not a window |
| Opacity | `0.85` idle, `1.0` on hover |
| Drag | Click-and-drag moves it; position persisted to user settings (**not** to the .esproj) |
| Snap | Snaps to the nearest screen edge on release |

#### 5.2.1 Cycle phase colours

The button is the operator's only status indicator, so it must show **which phase the cycle is in**, not just the final result. The runner drives a phase state machine and the button repaints on every transition.

```
Ready ──PLC trigger──► Triggered ──► Checking ──► Sending ──► Complete ──2 s──► Ready
                                          │            │            │
                                          └────────────┴────────────┴──► Error / SizeMismatch
```

| Phase | Fill colour | Glyph | Raised when |
|---|---|---|---|
| **Ready** | Slate grey `#5A6472` | ● | Idle, PLC connected, waiting for a trigger |
| **Triggered** | Blue `#2D7FF9` | ⚡ | PLC trigger rising edge detected; frame capture starting |
| **Checking** | Amber `#F5A623`, pulsing | ⏳ | OCR running. Tooltip shows `region 2/3: Status` |
| **Sending** | Violet `#7B5CD6` | ⇅ | `FlushOutputs` — values being written to the PLC |
| **Complete · OK** | Green `#2FA84F` | ✓ | Cycle finished, every used region OK |
| **Complete · NG** | Red `#D0342C` | ✗ | Cycle finished, at least one used region NG |
| **Error** | Dark red `#8B1A14` | ⚠ | Capture / OCR / PLC failure |
| **Size mismatch** | Orange `#E8730C`, slow pulse | ⛶ | Window not maximized (§4.4 / §5.2.4) |
| **Disconnected** | Grey, hollow outline | ⛔ | PLC not connected and not bypassed |

Implementation notes:

- `ScreenCheckRunner` raises `PhaseChanged(RunPhase phase)` at each transition; `FloatingButton` subscribes with `-=` then `+=` and repaints via the captured `SynchronizationContext`.
- `Complete · OK/NG` holds for **2 seconds** (a `System.Windows.Forms.Timer`), then returns to `Ready`. `Error` and `Size mismatch` are **sticky** — they persist until the next successful cycle, because an operator must not be able to miss them.
- Pulsing is a 500 ms timer alternating fill alpha between 100 % and 60 %. **Stop the timer whenever the phase is not pulsing** — a permanently running repaint timer on a 2-core production PC is wasted CPU.
- A thin arc around the button's edge doubles as a progress ring during `Checking`, filling as regions complete (`RegionCompleted` gives `n/total`).

```csharp
public enum RunPhase
{
    Ready, Triggered, Checking, Sending,
    CompleteOk, CompleteNg, Error, SizeMismatch, Disconnected
}
```

#### 5.2.2 Interactions — clicking the button requests Edit mode

**The button does not run a check.** In Run mode, inspection is driven by the **PLC trigger only**; the button is a status light and the way back into Edit mode.

- **Left click** → opens the **Switch to Edit Mode** password prompt (§5.2.3).
- **Hover** → tooltip: current phase, last result, per-region text, elapsed ms, last run time, capture engine in use.
- **Right click** → context menu: `Switch to Edit Mode…` (password), `Show Last Result…`, `Reconnect PLC`, `Exit` (password). Note there is deliberately **no `Run Once`** — a manual trigger would write values to the PLC out of sequence with the machine cycle.
- `Show Last Result…` opens a small non-modal panel listing every region: name, OCR text, converted value, target PLC value, OK/NG. It never auto-opens, so it cannot obscure the target window.

#### 5.2.3 Edit-mode password prompt

```
┌────────────────────────────────────────────┐
│  🔒  Switch to Edit Mode                   │
│                                            │
│  Inspection will stop while editing.       │
│                                            │
│  Password:  [ • • • • • •            ]     │
│                                            │
│  ⚠ Incorrect password. 2 attempts left.    │
│                                            │
│         [ Cancel ]      [ Unlock ]         │
└────────────────────────────────────────────┘
```

| Property | Value |
|---|---|
| Default password | **`797979`** |
| Storage | `AppSettings.EditPassword` in the `.esproj`, so a line can change it per project |
| Input | `TextBox` with `UseSystemPasswordChar = true`; `Enter` submits, `Esc` cancels |
| Focus | Prompt is `TopMost` and takes focus so the operator can type immediately |
| Wrong password | Inline message, field cleared and refocused. **3 attempts**, then a **30-second lockout** with a countdown on the button |
| Success | `AppSession.EnterEditMode()` — `TriggerService.Stop()`, PLC outputs left in their last state, `Ready` bit cleared, floating button hidden, wizard restored to the current project |
| Logging | Every attempt is logged (§4.10): `INFO App "edit mode unlocked"` or `WARN App "edit unlock failed, attempt 2/3"` |

```csharp
public sealed class AppSettings
{
    public string EditPassword = "797979";
    public int    MaxUnlockAttempts = 3;
    public int    LockoutSeconds    = 30;
    public bool   RequirePasswordToExit = true;
}
```

> **Scope of this password.** This is an **operator lockout**, not security. It stops accidental edits on the shop floor; it is stored in plain text in the `.esproj` and anyone with file access can read it. Do not describe it to the user as protection against a determined person. If real protection is ever needed, store a salted hash instead — but do not build that in v1 unless asked.

**Comparing the password:** use an ordinal comparison on the trimmed input. Do **not** trim to a fixed length, lowercase it, or parse it as a number — `797979` is a string, and treating it as an `int` would make `0797979` and ` 797979 ` silently valid.

Between runs the button is the **only** visible EasyScreen surface — except when the size guard trips (§5.2.4).

#### 5.2.4 Operator prompt — window not maximized

When `ScreenCheckRunner.SizeGuardTripped` fires in Run mode, EasyScreen shows a prompt aimed at a line operator, not an engineer. This is the one case where covering part of the screen is correct: the run is already blocked, and the operator must act.

```
┌────────────────────────────────────────────────────────┐
│  ⛶   WINDOW SIZE DOES NOT MATCH                        │
│                                                        │
│  The application window is not the size that was       │
│  registered. Readings would be inaccurate.             │
│                                                        │
│      Registered (master):   1920 × 1080                │
│      Current:               1280 × 720   (33 % smaller)│
│      Allowed deviation:     5 %                        │
│                                                        │
│  ➜  Please MAXIMIZE the application window             │
│     to full screen, then trigger again.                │
│                                                        │
│   [ Maximize Target Window ]   [ Re-check ]   [ Close ]│
└────────────────────────────────────────────────────────┘
```

| Property | Value |
|---|---|
| Form | Borderless, `TopMost`, rounded, centred on the primary screen, ~420 × 260 px |
| Modality | **Non-modal** — it must never block the operator from interacting with the target application to fix the problem |
| Audio | One system alert on first appearance; not repeated on re-trigger |
| Auto-dismiss | Closes by itself as soon as a subsequent capture passes the guard |

**Buttons:**

- **Maximize Target Window** — resolves the target `HWND` and calls `ShowWindow(hWnd, SW_MAXIMIZE)` + `SetForegroundWindow`, then re-captures and re-evaluates. This fixes the problem in one click for the common case and is the reason the prompt exists rather than a bare error.
- **Re-check** — re-captures and re-evaluates without touching the window (for operators who maximized it manually).
- **Close** — dismisses the prompt. The floating button stays orange, and the next trigger raises it again. Closing never silently resumes checking with a wrong-sized window.

While the prompt is open, PLC triggers are still evaluated: each one re-runs the guard, so as soon as the window is corrected the line resumes with no operator interaction with EasyScreen at all.

Additional P/Invoke required for the Maximize button: `ShowWindow(SW_MAXIMIZE=3)`, `SetForegroundWindow`, `IsZoomed`.

### 5.3 Edit mode — `WizardHost`

A 4-step wizard with a top step-indicator strip and a bottom `Back / Next / Finish` bar. Steps are `UserControl`s swapped in a `Panel`.

Navigation is **gated** — `Next` is disabled until the current step validates:

| Step | Gate to leave |
|---|---|
| 1 Capture | A register image exists and `RegisteredSize` is non-empty |
| 2 PLC | Connected **or** `IsBypass == true`; at least one output `ScreenValue` defined |
| 3 Regions | ≥ 1 region; every region has a valid `TargetValueName`; every `MappedInt` region has ≥ 1 map entry |
| 4 Output | ≥ 1 region set to `Used` |

Failed gates show an inline message strip naming the exact problem — never a modal dialog.

### 5.4 `RoiCanvas` — zoom / pan canvas with ROI editing

**Requirement:** the Edit-mode view must have zoom +/− and pan behaving like EasyVision's View.

**Research result that shapes this design:** EasyVision's canvas is `ImageCanvasControl : ImageBox` (`BeeInterface/ShapeEditing/ImageCanvasControl.cs:19`) — it **derives from Cyotek's `ImageBox`**, a standalone NuGet package (`CyotekImageBox 2.0.0-beta.2`, already in `packages/`, referenced by `BeeCore`, `BeeInterface`, `BeeUi`). All the zoom/pan behaviour operators are used to comes from that package, **not** from EasyVision code.

**Therefore EasyScreen references the same NuGet package and derives from it:**

```csharp
using Cyotek.Windows.Forms;

public sealed class RoiCanvas : ImageBox     // <-- same base class as EasyVision's canvas
{
    public IList<ScreenRegion> Regions { get; set; }
    public int  SelectedIndex { get; set; }
    public bool RotationLocked { get; set; }
    public bool ShowResultBadges { get; set; }
    public CanvasMode Mode { get; set; }      // Edit | Pan

    public event Action<int> SelectionChanged;
    public event Action<int> RegionChanged;   // fires on drag END, not per mouse-move
    public event Action<PointF> HoverPositionChanged;

    public void CenterOn(int regionIndex);
}
public enum CanvasMode { Edit, Pan }
```

This is the single biggest simplification in the plan: **zoom, pan, scrollbars, and coordinate conversion are inherited, not written.** The estimate drops from ~600 lines to **~350 lines** of pure ROI logic.

#### 5.4.1 Zoom / pan inherited from `ImageBox` — configured to match EasyVision

| Capability | `ImageBox` member | EasyScreen configuration |
|---|---|---|
| Zoom level | `Zoom` (int, **percent**) | Same units as EasyVision, which computes `ScaleZoom = imgView.Zoom / 100.0` (`View.cs:2088`) |
| Zoom in / out | `ZoomIn()`, `ZoomOut()` | Bound to the `+` / `−` toolbar buttons and `Ctrl +` / `Ctrl −` |
| Zoom steps | `ZoomLevels` | Default collection (…25, 50, 75, 100, 150, 200, 300, 400, 500, 600, 700, 800…) |
| Fit to window | `ZoomToFit()` | `Fit` button and `Ctrl+0`; called once after Register Image |
| Actual size | `ActualSize()` | `100%` button and `Ctrl+1` |
| Wheel zoom | built-in | Zooms about the cursor — no code required |
| Pan | `PanMode`, `AutoPan` | See conflict resolution below |
| Scroll position | `AutoScrollPosition` | Equivalent to EasyVision's `Global.pScroll` (`View.cs:2089`) |
| Scrollbars | `AlwaysShowHScroll` / `AlwaysShowVScroll` | Set `true`, matching `View.Designer.cs:551-552` |
| Client ↔ image coords | **`PointToImage(Point)`** / `GetOffsetPoint(PointF)` | The whole basis of hit-testing (`ImageCanvasControl.cs:102` uses exactly this) |
| Viewport rect | `GetImageViewPort()` | Clipping the ROI overlay draw |
| Minimum zoom | `MinimumZoom` | `1`, matching `ImageCanvasControl.cs:44` |
| Interpolation | `InterpolationMode` | `NearestNeighbor` when `Zoom > 200` so pixels stay crisp for OCR inspection |

**Left-button conflict — pan vs. ROI drag.** EasyVision sets `PanMode = ImageBoxPanMode.Left` and suppresses it while editing via `ShouldLockNavigation()` (`ImageCanvasControl.cs:152`). EasyScreen resolves it explicitly:

- Default `PanMode = ImageBoxPanMode.Middle` → **middle-drag always pans**, left is free for ROI editing.
- **Space + left-drag** pans temporarily (cursor becomes a hand) — the standard convention, no mode switch needed.
- A toolbar toggle `Edit ⇄ Pan` sets `Mode`; in `Pan` mode `PanMode` becomes `Left` and all ROI hit-testing is bypassed. This mirrors EasyVision's Edit button (`EditRectRot.cs:745,756`) so the muscle memory carries over.
- While a ROI drag is in progress, `PanMode` is forced to `None` and restored on mouse-up — a drag can never turn into a pan mid-gesture.

**Toolbar under the canvas:** `[−] [100%] [+] [Fit]  ·  Zoom: 150%  ·  x: 842, y: 317  ·  [Edit ⇄ Pan]`
The live cursor position is reported in **image pixels** via `PointToImage`, which is what an engineer needs when lining an ROI up with a digit.

#### 5.4.2 Coordinate model

EasyVision centres ROI coordinates on `Global.Config.SizeCCD` (`EditRectRot.cs:880`, `ToolOCR.cs:452`). EasyScreen instead uses **image-pixel coordinates with origin at top-left**: `RectRotate._PosCenter` is the ROI centre in image pixels. This removes the `SizeCCD` global dependency entirely and makes ROI coordinates directly comparable to capture output.

Conversion is never hand-rolled — always `PointToImage` / `GetOffsetPoint` from the base class, so zoom and scroll are accounted for automatically.

**Rendering pipeline** — override `OnPaint`, call `base.OnPaint(e)` first so `ImageBox` draws the image, grid background and scrollbars, then draw ROI overlays on top in client space (`SmoothingMode.AntiAlias`):

1. `base.OnPaint(e)` — `ImageBox` renders the image at the current zoom/scroll.
2. Clip to `GetImageViewPort()` so overlays never paint over the scrollbars.
3. For each region: take the four corner points from the copied `RectRotate` (image space) and map each through `GetOffsetPoint` to client space.
   - Unselected: 1 px cyan outline + name label at the top-left corner.
   - Selected: 2 px yellow outline + 8 square anchor handles + a rotation handle on a stalk above the top edge.
   - Mask sub-ROI: dashed magenta outline.
4. Draw a status badge per region when `ShowResultBadges` (OK green / NG red / grey for unused).

Handle boxes are drawn at a **constant 10 px in client space** regardless of zoom, so they stay grabbable at 800 %.

**Hit-testing and interaction** — this is the logic being replaced from `View.cs:688-980`, reimplemented for rectangles only:

| Gesture | Behaviour |
|---|---|
| Hover over handle | Cursor changes: `SizeNWSE` / `SizeNESW` / `SizeNS` / `SizeWE` on corners and edges, a rotate cursor on the stalk, `SizeAll` inside the body |
| Drag corner handle | Resize; hold `Shift` to preserve aspect ratio |
| Drag edge handle | Resize on one axis only |
| Drag rotation stalk | Rotate about `_PosCenter`; hold `Shift` to snap to 15° increments |
| Drag body | Move; hold `Ctrl` to constrain to one axis |
| Click empty space | Deselect |
| **Mouse wheel** | **Zoom about the cursor — inherited from `ImageBox`, no code** |
| **Middle-drag**, **Space + left-drag** | **Pan — inherited via `PanMode`** |
| `Ctrl +` / `Ctrl −` / `Ctrl 0` / `Ctrl 1` | Zoom in / out / fit / actual size |
| Arrow keys | Nudge selected ROI by 1 px (10 px with `Shift`) |
| `Delete` | Delete the selected ROI (confirm) |

Hit-testing order is **handles → body → background**, iterating regions front-to-back so overlapping ROIs behave predictably. All hit-testing converts the mouse point once with `PointToImage`, then works purely in image space — so it is automatically correct at every zoom level and scroll offset.

All resize/rotate math reuses the copied `RectRotate` methods (`ContainsPointRect`, `CenterLocalToWorld`, `WorldToLocal`, `NormalizeRotation`) — no new geometry code is written.

`RoiChanged` fires on **mouse-up**, not on every mouse-move, so the model and the dirty flag are not thrashed during a drag.

### 5.5 Step-by-step UI specification

#### Step 1 — Capture

```
┌──────────────────────────────────────────────────────────────┐
│ Capture Source                                               │
│  ( ) Application Window     ( ) Screen Region                │
│                                                              │
│  Window:  [ Notepad — untitled          ▼ ] [ Refresh ]      │
│  Match by:[ Title contains ▼ ]  [x] Client area only         │
│  Detected framework: Chromium/Electron                       │
│                                                              │
│  Region:  x:0 y:0 w:0 h:0        [ Select Region… ]          │
├──────────────────────────────────────────────────────────────┤
│ Capture engine: [ Auto (recommended) ▼ ]   [ Test Engines ]  │
│   ✅ GraphicsCapture (WGC)   18 ms   ← recommended            │
│   ⚠ PrintWindow             12 ms   blank frame              │
│   ❌ BitBlt                   —      blank frame              │
│   ✅ CopyFromScreen           9 ms   needs unobstructed win.  │
├──────────────────────────────────────────────────────────────┤
│  [ Preview ]              [ ★ Register Image ]               │
├──────────────────────────────────────────────────────────────┤
│  ┌────────────────────────────────────────────┐              │
│  │                                            │              │
│  │      captured image preview (master)       │              │
│  │                                            │              │
│  └────────────────────────────────────────────┘              │
│  Registered: 1920 × 1080  ·  captured 14:23:07  ·  PrintWindow│
│  Size tolerance in Run mode: [ 5 ] %                         │
└──────────────────────────────────────────────────────────────┘
```

- The window dropdown lists `Title — ProcessName`, sorted by title, excluding invisible/zero-size/minimized windows and EasyScreen itself. `Refresh` re-enumerates.
- `Select Region…` minimises `MainForm`, then shows `SnipOverlayForm`: a borderless `TopMost` form covering the **entire virtual desktop** (`SystemInformation.VirtualScreen`), 30 % black `Opacity`, crosshair cursor. Drag draws a clear rectangle with a live `w × h` readout; `Enter`/mouse-up confirms, `Esc` cancels.
- **Register Image** captures once and stores it. The status line reports which capture path succeeded (`PrintWindow` / `BitBlt` / `CopyFromScreen`) — essential for diagnosing the black-window problem in §7.
- **Test Engines** runs `ScreenCaptureService.Probe` — every engine is tried against the selected window and the result table above is filled in with availability, elapsed ms, and whether the frame came back blank. The recommended engine is preselected and saved to `ScreenCaptureTarget.Engine`. This is how a **non-WinForms** target (WPF, Qt, Electron, UWP, DirectX) gets a working configuration without the engineer knowing anything about capture APIs.
- **Detected framework** is the advisory guess from the window class name (§4.3.5), shown so an engineer immediately understands why WGC was picked.
- For Chromium/Electron targets the probe may recommend a **child window**; when it does, a `Content child: Chrome_RenderWidgetHostHWND` row appears with a checkbox to use it.
- If every engine returns blank, an inline error appears: *"No capture engine could read this window. If this PC is Windows 10 before build 18362, window Graphics Capture is unavailable — update Windows, or use 'Screen Region' capture and keep the window unobstructed."*
- Re-registering when regions already exist and the new size differs from `RegisteredSize` prompts: *Keep ROIs (may be misaligned) / Scale ROIs proportionally / Cancel.*
- **Size tolerance** sets `ScreenCaptureTarget.SizeTolerancePercent` (default **5 %**), the threshold that triggers the operator prompt in Run mode (§4.4, §5.2.4). The registered image is the **master** that Step 3 inspects against and that the guard compares every live capture to. Best practice, shown as a hint under the field: *maximize the target window before registering, so the master is the full-screen size operators will use.*

#### Step 2 — PLC

```
┌──────────────────────────────────────────────────────────────┐
│ Connection                                                   │
│  Brand: [ Keyence ▼ ]   Mode: ( ) TCP  ( ) Serial            │
│  IP:   [192.168.0.10]  Port:[8501]                           │
│  COM:  [COM3 ▼] Baud:[115200▼] Parity:[Even▼] Station:[0]    │
│                                                              │
│  Topology: [ Single ]      Run mode: [ Word ]                │
│  Input word:  [DM100]      Output word: [DM200]              │
│                                                              │
│  [ Connect ]  ● Disconnected        [ ] Bypass (offline)     │
├──────────────────────────────────────────────────────────────┤
│ Bit I/O                                        [+ Add Bit]   │
│  Dir  Function    Address     Bit   Blink   Test             │
│  In   Trigger     DM100        0     [ ]    ○  [Read]        │
│  Out  Result      DM200        0     [ ]    ○  [Set][Clear]  │
│  Out  Ready       DM200        2     [ ]    ●  [Set][Clear]  │
├──────────────────────────────────────────────────────────────┤
│ Values (Add Value)                           [+ Add Value]   │
│  Name         Address   Type     Dir    Live      Test       │
│  Lot          DM300     String   Out    "A123"   [Write]     │
│  Status       DM310     Int      Out    2        [Write]     │
└──────────────────────────────────────────────────────────────┘
```

- `Brand` is populated from the copied `PlcLib.PlcBrand` enum — all 14 brands are available with no extra work.
- **Topology `Single`** and **Run mode `Word`** are rendered as selected toggle buttons matching the current Hardware page's look (`ProtocolPLC.cs:725-741`), but in v1 they are fixed — the enums exist so multi-device/ladder modes can be added later without a schema change.
- TCP fields and Serial fields show/hide with the `Connection` radio.
- The connection indicator is a coloured dot bound to `ScreenPlcLink.ConnectionChanged`.
- **Bit I/O grid** uses the ported `BitInputRow`/`BitOutputRow` controls. `Function` is a combo over `BitFunction`. In Word mode the `Address` cell is read-only and shows the resolved `Word.Bit`. Per-row `Read`/`Set`/`Clear` buttons allow live verification without leaving the wizard.
- **Values grid** uses the ported `ValueRow`. `Name` must be unique and non-empty — it is the key Step 3 binds to. `Type` is the copied `TypeVar` enum. Per-row `Write`/`Read` tests the address immediately.
- **Bypass** disables all live PLC calls so the whole wizard and Run mode work with no hardware attached.

#### Step 3 — Regions

```
┌───────────────────────────┬──────────────────────────────────┐
│ Regions        [+][−][▲▼] │  Region settings                 │
│ ┌───────────────────────┐ │  Name:      [ Lot Number      ]  │
│ │ 1. Lot Number    OK   │ │  Data type: (•)String ( )Int     │
│ │ 2. Status        NG   │ │             ( )Mapped Int        │
│ │ 3. Count         —    │ │                                  │
│ └───────────────────────┘ │  Map (Mapped Int only):          │
│                           │   Key      Value    [+][−]       │
│  Run order = list order   │   OK        1                    │
│  (sequential)             │   NG        0                    │
│                           │   Run       1                    │
├───────────────────────────┤   Stop      2                    │
│ ┌───────────────────────┐ │   Error     3                    │
│ │                       │ │   Default:[0] [x] NG if unmapped │
│ │      RoiCanvas        │ │                                  │
│ │   (register image     │ │  PLC target: [ Lot (DM300) ▼ ]   │
│ │    + ROI overlays)    │ │  Allowed chars: [0123456789    ] │
│ │                       │ │  CLAHE[2] Sigma[2] Blur[1]       │
│ └───────────────────────┘ │                                  │
│  Zoom: [−][100%][+][Fit]  │  Test on: (•)Master ( )Live      │
│                           │  [ Test This Region ][Test All]  │
│                           │  Result: "A123"  →  "A123"  OK   │
│                           │  12 ms · master 1920×1080        │
└───────────────────────────┴──────────────────────────────────┘
```

- `+` adds a region centred in the current view with a default 200 × 60 ROI, auto-named `Region N`.
- `−` deletes with confirmation. `▲▼` reorders — **and the list order is the sequential execution order**, which is now the only execution model (decision #1). The list header states this explicitly.
- Selecting a list row selects the ROI on the canvas and vice versa (two-way binding through `SelectionChanged`).
- **Data type** radio drives the visibility of the Map grid: shown only for `Mapped Int`.
- The Map grid seeds from `StringToIntMap.CreateDefault()` — `OK=1, NG=0, Run=1, Stop=2, Error=3`, exactly the example given in the requirement.
- **PLC target** combo is fed by `ScreenPlcLink.OutputValueNames()` from Step 2, displaying `Name (Address)`. It warns inline if the region's data type is incompatible with the value's `TypeVar` — e.g. a `String` region bound to an `Int` value.
**Inspection source — Master vs Live.** Step 3 can inspect against **the registered master image** or against a **fresh live capture**:

| Source | Frame used | Purpose |
|---|---|---|
| **Master** (default) | `project.RegisterImage` — the exact image the ROIs were drawn on | Deterministic. The ROI is guaranteed aligned, so any failure is an **OCR/config** problem, not an alignment problem. This is the right default while drawing and tuning regions |
| **Live** | `ScreenCaptureService.Capture(...)` right now | Verifies the real pipeline end to end: current window content, current size, real capture path |

Making Master the default matters: it separates the two failure modes an engineer is debugging. If a region fails on Master, the ROI or OCR settings are wrong. If it passes on Master but fails on Live, the problem is capture, window size, or changed screen content.

- **Test This Region** crops just this ROI from the selected source, runs OCR, and shows raw text → converted value → OK/NG, plus elapsed ms and which source was used. It does **not** write to the PLC (a checkbox `Also write to PLC` enables that for commissioning).
- **Test All** runs the full sequential runner in preview against the selected source and fills the OK/NG column in the region list — the same code path as Run mode, so what passes here is what will pass in production.
- When source is **Live**, the master-size guard (§4.4) runs first. On deviation > 5 % an inline strip appears above the canvas:
  *"Live capture is 1280 × 720 but the master is 1920 × 1080 (33 % smaller). Maximize the target window — results below are unreliable."*
  The test still executes so the engineer can see what is happening, but every result is badged **size mismatch**. In Edit mode this is a warning; in Run mode the same condition aborts the cycle (§4.4).
- When source is **Master**, the guard is skipped entirely — the master image is by definition the reference size.

#### Step 4 — Output

```
┌──────────────────────────────────────────────────────────────┐
│ Output configuration                                         │
│                                                              │
│  #  Region        Data type   PLC target      Used  Unused   │
│  1  Lot Number    String      Lot (DM300)     (•)    ( )     │
│  2  Status        MappedInt   Status (DM310)  (•)    ( )     │
│  3  Count         Int         Count (DM320)   ( )    (•)     │
│                                                              │
│  [Select all Used]  [Select all Unused]                      │
├──────────────────────────────────────────────────────────────┤
│ Overall result                                               │
│  Result bit:     DM200.0   (OK when every Used region is OK) │
│  Result NG bit:  DM200.1                                     │
│  Error bit:      DM200.4                                     │
│                                                              │
│  Summary: 2 of 3 regions used · writes 2 values per cycle    │
├──────────────────────────────────────────────────────────────┤
│ Logs                                                         │
│  [x] Save Logs      [x] Only errors   [ ] Log every cycle    │
│  [ ] Perf Log (per-region timing)                            │
│  Folder: [ Logs               ] [Browse]  Keep: [15] days    │
│  Max entries: [100000]              Current file: 2.4 MB     │
│                                                              │
│ Images                                                       │
│  Save image on:  [ ] OK        [x] NG                        │
│  Content:        [x] Raw frame [ ] Result overlay            │
│  Folder: [ Images             ] [Browse]                     │
│  Format: [ JPEG ▼ ]  Quality: [85]   Min free disk: [15] %   │
│                                                              │
│  Disk: 142 GB free (68 %) · archive 1.1 GB · 9 days retained │
│  [ Open Log Folder ]  [ Export CSV… ]  [ Clear Logs ]        │
└──────────────────────────────────────────────────────────────┘
```

- Exactly two options per region — **Used / Unused. No Invert**, per the requirement. The ported control is `ItemRS` with its third radio removed (`ItemRS.cs:32` `ckInverse_CheckedChanged` is deleted, not merely hidden, so `RegionUsage` can never hold an invert state).
- Unused regions are skipped entirely by the runner: no capture crop, no OCR, no PLC write.
- The overall-result rows are read-only summaries of the Step 2 bit map, with a link back to Step 2 to change them.
- The summary line makes the per-cycle PLC write count explicit, since that drives cycle time.

**Logs panel** — mirrors EasyVision's `LogsDashboard` options so the semantics are already familiar (backend in §4.10):

| Control | Binds to | Behaviour matching EasyVision |
|---|---|---|
| `Save Logs` | `LogSettings.SaveLogs` | Master switch, same as `Config.IsSaveLog` (`Config.cs:309`). When off, nothing is enqueued and no file is written |
| `Only errors` | `OnlyErrors` | Same as `LogsDashboard.OnlySaveErr` — only `ERROR`/`FATAL` are stored. On by default so a production line does not fill the disk |
| `Log every cycle` | `LogEveryCycle` | Adds one INFO row per cycle with every region's value. Off by default; mutually informative with `Only errors` — enabling it forces `Only errors` off, exactly as EasyVision's Perf Log forces `Save Logs` on (`LogsDashboard.cs:495`) |
| `Perf Log` | `PerfLog` | Per-region crop/OCR timing. Enabling it auto-enables `Save Logs` and disables `Only errors`, replicating `ApplyPerfLogGate` (`LogsDashboard.cs:488`) |
| `Keep N days` | `RetentionDays` | Same meaning as `Config.LimitDateSave = 15` (`Config.cs:425`) |
| `Max entries` | `MaxEntries` | Same as `LogsDashboard.MaxLogCount = 100000` |
| `Min free disk %` | `MinFreeDiskPercent` | Same as `Config.MinFreeDiskPercentForReportCleanup = 15` (`Config.cs:429`) |
| `Save image on OK / NG` | `SaveImageOnOk` / `SaveImageOnNg` | Same pair as `Config.IsSaveOK` / `IsSaveNG` (`Config.cs:442`) |
| `Raw frame / Result overlay` | `SaveRawImage` / `SaveResultImage` | Same pair as `Config.IsSaveRaw` / `IsSaveRS` (`Config.cs:443`) |

- The **disk line** is live: free space, current archive size, and how many retention days are actually present. It turns amber below `MinFreeDiskPercent`.
- `Export CSV…` writes `Time,Level,Source,Message` with the same escaping rule as `LogsDashboard.ExportCsv` (`LogsDashboard.cs:1257`), so exports open identically in Excel.
- `Clear Logs` confirms, then truncates the JSON store and leaves archived images untouched (deleting images is a separate, explicit action).
- Every setting here is stored in the `.esproj` (decision #5), so a project carries its own logging policy.
- There is **no log viewer grid in v1.** `Open Log Folder` plus `Export CSV` covers commissioning; building a second `LogCanvas` is explicitly out of scope (§12.3).

### 5.6 Finish gate

`Finish` runs a full validation pass and shows a checklist of anything unresolved:

- Register image present and matches the target window's current size
- PLC connected or bypassed
- Every `Used` region has a valid, type-compatible `TargetValueName`
- Every `MappedInt` region has a non-empty map
- A `Trigger` input bit exists if PLC triggering is intended (warning, not an error — manual click always works)
- OCR runtime initialised (`ScreenOcrRuntime.IsReady`)

Passing prompts to save the `.esproj`, then offers **Enter Run Mode**.

---

## 6. Threading Model

| Thread | Owns |
|---|---|
| UI thread | All WinForms, `RoiCanvas` painting, wizard, `FloatingButton` |
| Trigger thread (1 background `Task`) | PLC bit polling, rising-edge detection, heartbeat |
| Worker thread (1 background `Task`) | Capture → OCR → convert → PLC write. **One at a time** |

Rules:

- Exactly **one** check runs at a time, guarded by `Interlocked.CompareExchange`. A trigger arriving mid-check is dropped and logged (not queued) — matching how a vision system should behave.
- Python's GIL is acquired only on the worker thread. Because execution is sequential (decision #1), there is never GIL contention between regions.
- Every event crossing back to the UI (`RegionCompleted`, `ConnectionChanged`, result state) marshals via the `SynchronizationContext` captured at startup. Per Hard Rule 0.1 #4, every subscription is written `X.Event -= H; X.Event += H;`.
- `FloatingButton` state updates are posted, never invoked synchronously from the worker.

---

## 7. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| **Non-WinForms target (WPF/Qt/Electron/UWP/DirectX) renders black under GDI capture** | Cannot capture at all | WGC primary engine + 3 fallbacks + blank-frame probe + Step 1 engine table (§4.3). **Verify against the real target app in ES.3b before building anything else** |
| **WGC HWND capture unavailable** (Windows 10 older than build 18362) | Modern-framework targets unsupported on that PC | `IsAvailable` check with a specific message; falls back to `CopyFromScreen` with an unobstructed-window warning. Confirm the production OS build in ES.0 |
| **WGC resource leak** (frame pool / D3D surfaces per cycle) | GPU memory grows, app dies after hours | Cache device + item + pool + session per HWND; dispose in fixed order on every path including errors; ES.3b acceptance includes a 1,000-cycle soak with a stable handle/memory count |
| **`RoiCanvas` replaces 10K lines of View.cs** | New-build risk | Reduced substantially by deriving from the same Cyotek `ImageBox` EasyVision uses — zoom/pan/scroll/coordinate conversion are inherited, only ROI logic is written. Scope is rectangles + rotation only. Timeboxed in ES.6 |
| Log/image archive fills the disk on a long-running line | Inspection stops, or Windows becomes unstable | Retention days + max entries + `MinFreeDiskPercent` guard; images deleted before logs; image saving auto-disables with a FATAL rather than letting the disk fill (§4.10) |
| Logging blocks the inspection cycle | Cycle time grows, PLC timeouts | `Log()` only enqueues; all disk I/O is on a timer thread; image archiving runs after `FlushOutputs` and is skipped entirely when both image toggles are off |
| Edit password gives a false sense of security | Misplaced trust | Documented explicitly as an operator lockout, not security (§5.2.3); plaintext storage is stated plainly rather than implied |
| Target window resized between Edit and Run | Every ROI silently misaligned → **wrong data written to PLC** | `CaptureSizeGuard` (§4.4) aborts the cycle before OCR at > 5 % deviation, sets only the Error bit (never a false OK/NG), and raises the operator prompt (§5.2.4) with a one-click Maximize. Auto-resumes once corrected |
| DPI scaling shifts coordinates | ROIs off by a scale factor | PerMonitorV2 manifest + all ROI coordinates stored relative to the captured bitmap, never screen space |
| Python/OCR environment missing (`Tool/OCR.py`, `python39.dll`, model) | OCR unavailable | `EnsureInitialized` returns a specific error; Step 3 disables Test with an explanatory banner; Bypass still allows full UI configuration |
| PLC string encoding differs by brand | Garbled text written | Reuse `ReadStringAsciiKey`/`WriteString` with the 16-word convention exactly as EasyVision does; per-row `Write` test in Step 2 verifies before commissioning |
| Copied `PlcClient` diverges from BeeGlobal's over time | Bug fixed in one, not the other | Record the source commit SHA in a header comment on the copied file; note it in `CODEX_HISTORY.md` |
| Floating button overlaps the target's controls | Defeats the core requirement | Draggable + edge-snapping + position persisted; 56 px and semi-transparent by default |

---

## 8. Task Cards

Commit prefix `[ES.<n>]`. One task = one commit. Append `docs/architecture/CODEX_HISTORY.md` after each (template §11.2 of CLAUDE.md).
Build verification: `MSBuild EasyScreen.sln /t:Build /p:Configuration=Release "/p:Platform=Any CPU" /v:minimal`.

| Task | Title | In-scope | Definition of Done |
|---|---|---|---|
| **ES.0** | Read-in, no questions | Read §9 locked decisions, §12 contract, and the four EasyVision sources named in §11. Create `tools/CaptureTestApps/` (Q3) and `tools/FakePlcTransport.cs` (Q7) | Decisions restated in CODEX_HISTORY as the working baseline. **No user input required** |
| **ES.1** | Scaffold solution | `EasyScreen.sln`, both projects, 4.8/Any CPU (`Prefer32Bit=false`), NuGet: OpenCvSharp4, Newtonsoft.Json, pythonnet, **CyotekImageBox**, **Microsoft.Windows.SDK.Contracts** + D3D11 interop; ref HslCommunication | Builds; empty MainForm runs |
| **ES.2** | Copy geometry + PLC transport | `RectRotate.cs`, `AnchorPoint`, `PlcClient.cs`, `TimingUtils.cs` — namespaces changed, source SHA in header | Compiles standalone; zero EasyVision references |
| **ES.3a** | **Capture spike — legacy engines** ⚠ | `ICaptureEngine`, `PrintWindow` / `BitBlt` / `CopyFromScreen`, blank-frame probe, window enumeration | Captures a WinForms and a WPF window correctly |
| **ES.3b** | **Capture spike — WGC** ⚠⚠ | `GraphicsCaptureEngine`: D3D11 device, `IGraphicsCaptureItemInterop`, free-threaded frame pool, staging-texture readback, resource caching | **Captures the real non-WinForms target** (Electron/Qt/WPF/UWP as applicable). Blocking gate for the whole product |
| **ES.3c** | Engine probe + selection ✅ | `Probe`, `CaptureProbeReport`, fallback ordering, child-window enumeration, framework guess | Probe correctly recommends an engine for 3 different framework targets |
| **ES.4** | Data model + persistence | All `Model/` types, `ProjectStore` | Save→load round-trip preserves regions, PLC config, image |
| **ES.5** | OCR runtime ✅ | `ScreenOcrRuntime`, `OcrReadResult`, ported bootstrap | Reads a known digit image correctly, or verifies the explicit missing-deployment error path when OCR is not installed; no `Common.*` references |
| **ES.6** | **`RoiCanvas : ImageBox`** ✅ | Derive from Cyotek `ImageBox`; ROI render, hit-test via `PointToImage`, drag/resize/rotate; zoom toolbar `[−][100%][+][Fit]`, pan mode toggle, wheel zoom, cursor readout | Create/move/resize/rotate 3 ROIs smoothly **at 100 %, 400 % and after panning**; zoom/pan behave like EasyVision's View. Timeboxed 2 days |
| **ES.7** | `RegionCropper` ✅ | Ported `CropRotatedRect` subset | Crop of a rotated ROI matches EasyVision's output on the same input |
| **ES.8** | `ScreenPlcLink` ✅ | Connect, bit read/write, value read/write, staging + flush, bypass | Connects to a real PLC (or bypass); reads/writes one value and one bit |
| **ES.9** | Step 1 UI ✅ | `Step1Capture`, `SnipOverlayForm`, engine selector + `Test Engines` table, framework hint, size tolerance | Pick window / snip region, probe engines, register master image, size + tolerance recorded |
| **ES.10** | Step 2 UI ✅ | `PlcSettingsPanel`, ported bit/value rows | Configure, connect, add 1 bit + 1 value, per-row test works |
| **ES.11** | Step 3 UI ✅ | `Step3Regions`, `RegionListPanel`, map editor, target combo, **Master/Live test toggle**, Test Region / Test All | Add 2 regions, bind targets; Test on **Master** returns text and converted value; Test on **Live** warns on size mismatch |
| **ES.12** | Step 4 UI ✅ | `Step4Output`, ported `ItemRS` minus Invert, **Logs + Images panel**, disk readout, Export CSV / Open folder / Clear | Used/Unused toggles; Unused verifiably skipped; log toggles persist to `.esproj` |
| **ES.12b** | Logging backend ✅ | `ScreenLogger`, `LogRecord`, `ImageArchive`, retention, disk guard, atomic write | Cycle rows written; `Only errors` filters; retention deletes an old day-folder; disk-low disables image saving and logs FATAL |
| **ES.13** | Runner + size guard ✅ | `ScreenCheckRunner` sequential pipeline, `CaptureSizeGuard`, step-0 abort | 2 regions → 2 correct values at the correct PLC addresses; result bits set. Shrinking the window > 5 % aborts before OCR and sets only the Error bit |
| **ES.14** | Run mode ✅ | `FloatingButton` with **9-phase colour machine + progress ring**, `TriggerService`, **Edit password prompt (`797979`, 3 attempts, 30 s lockout)**, size-mismatch operator prompt, mode switching | Button floats without covering target; **PLC rising-edge runs one cycle and the button visibly steps Triggered→Checking→Sending→Complete**; clicking the button opens the password prompt and only `797979` enters Edit mode; resizing the target raises the maximize prompt and the run auto-resumes |
| **ES.15** | Wizard polish ✅ | Gates, validation, Finish checklist, dirty tracking | All gates enforced; no unhandled exception in a full Edit→Run pass |

Order: ES.0 → ES.1 → ES.2 → **ES.3a → ES.3b → ES.3c (gate, complete)** → ES.4 → ES.5 → ES.6 → ES.7 → ES.8 → ES.9 → ES.10 → ES.11 → ES.12 → ES.12b → ES.13 → ES.14 → ES.15.

**ES.3b is the capability gate.** Validate it against the four reference targets in §9 Q3 (Notepad, a WPF window, Edge, Calculator) — not against a target the user has to supply. If WGC is unsupported on the build host, implement it fully, mark its acceptance `NOT VERIFIED`, verify the other three engines, and continue per §12.9. Do not stop the run.

---

## 9. Pre-resolved Decisions — no questions to ask

**The implementing agent must run ES.1 → ES.15 to completion without asking anything.** Every question that would otherwise block is answered here. If a situation arises that is not covered, apply §12.9 and keep going.

| # | Question | **Locked decision** | Rationale |
|---|---|---|---|
| Q1 | Solution placement | **Separate `EasyScreen.sln`** at repo root. Do not touch `EasyVision.sln` | Independent build; cannot break EasyVision (decision #6) |
| Q2 | Target application / UI framework | **Unknown at build time — support all.** Build all four engines; default `Engine = Auto`; the Step 1 probe picks per target at commissioning | Removes the dependency on knowing the target (§4.3) |
| Q3 | How to validate capture without the real target | **Self-validate against four reference targets** the agent can launch on any Windows PC: `notepad.exe` (Win32/GDI), a 20-line WPF window the agent writes into `tools/CaptureTestApps/` (WPF/DWM), `msedge.exe` (Chromium/GPU), `calc.exe` (UWP). ES.3b passes when **WGC captures all four non-blank** | Makes the gate objective and autonomous |
| Q4 | Production OS build | **Require Windows 10 ≥ 18362 for direct HWND capture.** Check `GraphicsCaptureSession.IsSupported()` **at runtime**, never at build time; fall back automatically and log which engine served | `CreateForWindow` was introduced after the base WGC API; the stronger minimum prevents a false-positive availability result |
| Q5 | D3D11 interop package | **`Vortice.Direct3D11` / `Vortice.DXGI` 2.4.2.** Do not use SharpDX | One clear choice; 2.4.2 is the final Vortice release compatible with the locked .NET Framework 4.8 target |
| Q6 | PLC brand and addressing | **Default `Keyence`, TCP `192.168.0.10:8501`, input word `DM100`, output word `DM200`.** All user-editable in Step 2 | Matches the addresses used throughout this plan |
| Q7 | How to validate PLC without hardware | **`IsBypass = true` + an in-memory `FakePlcTransport`** behind `ScreenPlcLink` (test-only, in `tools/`, not in the manifest). ES.8/ES.13 acceptance asserts against the fake's recorded writes | Full pipeline verifiable with no PLC attached |
| Q8 | Multiple projects at runtime | **One project loaded at a time.** `File ▸ Open` / `Save As` in Edit mode only. No recipe manager | §12.3 out of scope |
| Q9 | OCR model and Python | **Reuse EasyVision's convention**: `Tool/OCR.py` beside the exe, `python39.dll` under `OcrSettings.PythonPath`. If absent, `ScreenOcrRuntime.EnsureInitialized` returns false with a specific message, the UI shows a banner, and **every other feature still works** | OCR environment is a deployment concern, not a build blocker |
| Q10 | How to validate OCR without the model | **Synthetic fixture**: the agent renders `"A123"` and `"OK"` to a bitmap with `Graphics.DrawString` in `tools/`, and ES.5 asserts the pipeline end-to-end when OCR is available, or asserts the specific "not initialised" error path when it is not | Deterministic either way |
| Q11 | Edit password | **`797979`**, stored in `AppSettings.EditPassword` in the `.esproj`, 3 attempts, 30 s lockout | Decision #12 |
| Q12 | Log defaults | `SaveLogs = false`, `OnlyErrors = true`, `RetentionDays = 15`, `MaxEntries = 100000`, `MinFreeDiskPercent = 15`, `SaveImageOnNg = true`, `SaveImageOnOk = false` | Mirrors EasyVision `Config.cs` defaults |
| Q13 | Warning baseline | Record the warning count from the **first green ES.1 build** in `CODEX_HISTORY.md`; later tasks must not exceed it | Establishes the baseline autonomously |
| Q14 | Anything else not covered | Choose the option that is **smallest in scope, most reversible, and closest to existing EasyVision behaviour**; record it in `CODEX_HISTORY.md` under "Autonomous decisions" | §12.9 |

---

## 10. Definition of Done (whole feature)

- [ ] Edit mode: 4 steps, gated navigation, inline validation, no modal error dialogs
- [ ] Step 1: captures a chosen **window** and a **full-screen region**; Register Image stores the **master** reference, its size, and the size tolerance
- [ ] Step 2: connects PLC in **Single + Word** mode; Bit I/O configurable and testable; Add Value produces the named value table
- [ ] Step 3: add/delete **multiple regions**; each selects `Int` / `String` / `MappedInt` with an editable map; each binds to a registered PLC value; **inspection runs against the master image or a live capture**, per-region and Test All, returning text and converted value
- [ ] Step 4: **Used / Unused** only (no Invert); Unused regions provably skipped
- [ ] Step 1: **capture works on a non-WinForms window** (WPF / Qt / Electron / UWP / DirectX as applicable); the engine probe reports per-engine results and picks a working one
- [ ] Edit canvas: **zoom in/out, fit, actual size, wheel zoom, and pan** behave like EasyVision's View; ROI editing stays pixel-accurate at every zoom level
- [ ] Run mode: floating button on the right edge **does not cover** the target window; runs on **PLC trigger rising edge**; regions execute **sequentially**; correct values land at the correct PLC addresses; result bits set
- [ ] Run mode: button colour visibly steps **Triggered → Checking → Sending → Complete (OK/NG)**, and shows Error / Size-mismatch / Disconnected distinctly
- [ ] Run mode: clicking the button opens the Edit password prompt; **`797979`** enters Edit mode, wrong entries are refused with attempt counting and lockout
- [ ] Step 4: **Save Logs** and image-archive options persist in the `.esproj`; logs are written atomically, filtered by `Only errors`, and pruned by retention and the disk guard
- [ ] Run mode: a target window deviating **> 5 %** from the registered master **aborts the cycle before OCR**, writes no OK/NG, and prompts the operator to maximize the window; the run resumes automatically once corrected
- [ ] Save/Load `.esproj` restores regions + PLC config + register image in one file, independent of EasyVision
- [ ] `EasyScreen.sln` builds Release Any CPU with zero errors
- [ ] Zero references to any EasyVision assembly; zero `Common.PropetyTools[...]` usage; zero `Global.*` usage
- [ ] Every event subscription uses `-=` before `+=`
- [ ] `CODEX_HISTORY.md` appended per task

---

## 11. Notes for the Implementing Agent

- EasyScreen is outside the P0–P6 refactor roadmap. Use the `[ES.<n>]` commit prefix, but Hard Rules still apply: `-=` before `+=`, no new UI singletons, `git mv` to preserve history, real build verification, `CODEX_HISTORY.md` appended.
- **Before ES.2**, read `BeeGlobal/RectRotate.cs` in full and `BeeGlobal/Protocol/PlcClient.cs:1-40` to confirm the dependency surface has not changed since this plan was written.
- **Before ES.5**, read `BeeCore/Unit/OCR.cs:182-345` and `BeeCore/Common.cs:353-424` and port exactly the eight steps listed in §4.5.
- **Before ES.6**, read `BeeInterface/Group/View.cs:677-980` to understand EasyVision's anchor/drag semantics — then implement a rectangle-only subset. Do not copy that file.
- **Before ES.8**, read `BeeGlobal/Protocol/ParaProtocol.cs:2092-2234` — the value read/write switch and the stage/flush pattern port over directly; everything else in that file does not.
- Do not modify any EasyVision file. If a copied source turns out to need a fix, fix the EasyScreen copy and note the divergence in `CODEX_HISTORY.md`.

---

## 12. Implementation Contract — binding rules for the implementing agent

> **This section is normative.** Everything above describes *what* to build; this section constrains *how*, so the implementation cannot drift while running unattended. A task is not complete until every check here passes. If a rule here conflicts with your judgement, follow the rule and record the disagreement in `CODEX_HISTORY.md` — do not silently override it, and do not stop to ask (§12.9).

### 12.1 Golden rules

1. **Build only what §8 task cards list.** Do not add features, settings, screens, or abstractions that are not in this document. "It might be useful later" is not a reason.
2. **Never modify any EasyVision file.** Not `BeeCore`, `BeeGlobal`, `BeeInterface`, `BeeUi`, `BeeMain`, `EasyVision.sln`, or `CLAUDE.md`. EasyScreen is additive only.
3. **One task card = one commit**, message `[ES.<n>] <summary>`. Never combine two task cards.
4. **Copied code is copied verbatim.** When §3.1 says "copy as-is", do not reformat, rename, modernise, or "fix" it. Change only the namespace, and add the provenance header (§12.5). Improving copied code makes divergence from the EasyVision original untraceable.
5. **Run to completion without asking.** Execute ES.0 → ES.15 in one pass. Every decision that would otherwise need the user is locked in §9; anything unforeseen is resolved with §12.9 and logged. Only the three hard stops in §12.9 justify pausing.
6. **When this plan is wrong or impossible**, fix the plan, record the correction in `CODEX_HISTORY.md`, and continue. Do not silently deviate, and do not stall waiting for confirmation.
7. **Report results truthfully.** If a build fails, paste the error. If a test was not run, mark it `NOT VERIFIED` with the reason. Never describe a task as verified when it was not — running unattended makes honest status the only signal the user has.
8. **Never stub to make a check pass.** Do not comment out a failing assertion, weaken `tools/check_easyscreen.sh`, or mark acceptance met by narrowing the test. A red result reported honestly is worth more than a green one that lies.

### 12.2 File manifest — the only files to create

Create exactly these. If a task genuinely needs another file, add it per the §12.9 rule (nearest folder, `internal`, logged) — do not stop to ask.

```
EasyScreen.Core/
  Model/            ScreenProject.cs  ScreenRegion.cs  StringToIntMap.cs
                    ScreenCaptureTarget.cs  PlcConfig.cs  LogSettings.cs
                    AppSettings.cs  OcrSettings.cs  Enums.cs
  Geometry/         RectRotate.cs [COPIED]   AnchorPoint.cs [COPIED]
  Imaging/          ScreenCaptureService.cs  ICaptureEngine.cs
                    GraphicsCaptureEngine.cs  PrintWindowEngine.cs
                    BitBltEngine.cs  CopyFromScreenEngine.cs
                    CaptureSizeGuard.cs  RegionCropper.cs [PORTED]
                    NativeMethods.cs
  Ocr/              ScreenOcrRuntime.cs [PORTED]  OcrReadResult.cs
  Plc/              TimingUtils.cs [COPIED]
                    ScreenPlcLink.cs  ScreenValue.cs [PORTED]  ScreenBit.cs [PORTED]
                    PlcBrand.cs  IReadWriteNet driver switch  (ES.8; PlcClient NOT copied)
  Runtime/          ScreenCheckRunner.cs  TriggerService.cs
  Logging/          ScreenLogger.cs  LogRecord.cs  ImageArchive.cs
  Persistence/      ProjectStore.cs

EasyScreen/
  Program.cs   app.manifest
  Shell/       MainForm.cs  AppSession.cs  FloatingButton.cs
               EditPasswordForm.cs  SizeMismatchForm.cs
  Wizard/      WizardHost.cs  Step1Capture.cs  Step2Plc.cs
               Step3Regions.cs  Step4Output.cs
  Controls/    RoiCanvas.cs  RegionListPanel.cs  PlcSettingsPanel.cs
               BitInputRow.cs [PORTED]  BitOutputRow.cs [PORTED]
               ValueRow.cs [PORTED]  RegionUsageRow.cs [PORTED from ItemRS]
               SnipOverlayForm.cs  MapEditorGrid.cs

tools/check_easyscreen.sh
```

### 12.3 Explicitly out of scope for v1

Do **not** build these. They are named here because they are the most likely places to drift:

- A log viewer grid / `LogCanvas` equivalent — `Open Log Folder` + `Export CSV` only
- Multi-device, EtherCAT, PCI, or ladder PLC topologies — `Single` + `Word` only
- Polygon, circle, ring, or hexagon ROIs — rotated rectangles only
- Parallel or multi-threaded region checking — **decision #1**
- Multiple simultaneous projects, project switching at runtime, recipe management
- A settings screen outside the 4 wizard steps
- Auto-update, telemetry, database export, network/REST APIs
- Password hashing, user accounts, role permissions
- Any EasyVision refactor, cleanup, or "while I was there" fix

### 12.4 Forbidden patterns — `tools/check_easyscreen.sh` must exit 0

```bash
#!/bin/bash
# Run from repo root before every EasyScreen commit.
fail=0
SRC="EasyScreen EasyScreen.Core"
CS='--include=*.cs'
EX='--exclude-dir=bin --exclude-dir=obj'

chk() { # description, grep-args...
  local desc="$1"; shift
  local hits
  hits=$(grep -rn $CS $EX "$@" $SRC 2>/dev/null)
  if [ -n "$hits" ]; then
    echo "FAIL: $desc"; echo "$hits"; fail=1
  else
    echo "ok:   $desc"
  fi
}

# --- Independence from EasyVision (decisions 2, 3, 6) ---
chk "no EasyVision namespace imports" -E "^\s*using\s+(BeeCore|BeeGlobal|BeeInterface|BeeUi|BeeMain|PlcLib\.Bee)"
chk "no EasyVision global state"      -E "\bGlobal\.(Config|ParaCommon|Comunication|IsRun|LogsDashboard|ScaleZoom|pScroll)"
chk "no PropetyTools access"          -E "PropetyTools\s*\["
chk "no EasyVision project refs"      -E "ProjectReference.*(BeeCore|BeeGlobal|BeeInterface|BeeUi)" --include=*.csproj

# --- Sequential-only execution (decision 1) ---
chk "no parallel region checking" -E "Parallel\.(For|ForEach|Invoke)|Task\.WhenAll" EasyScreen.Core/Runtime

# --- Core must stay UI-free ---
chk "Core has no WinForms"  -E "^\s*using\s+System\.Windows\.Forms" EasyScreen.Core
chk "Core shows no dialogs" -E "MessageBox\.Show"                   EasyScreen.Core

# --- Persistence (decision 3) ---
chk "no BinaryFormatter"  -E "BinaryFormatter"
chk "no [Serializable] on models" -E "\[Serializable" EasyScreen.Core/Model

# --- Async / threading hygiene ---
chk "no blocking on tasks"   -E "\.(Result|GetAwaiter\(\)\.GetResult\(\))\s*;|\.Wait\(\)"
chk "no async void"          -E "async\s+void\s+(?!.*_(Click|Load|Changed|Tick|Closing))" -P
chk "no Thread.Sleep"        -E "Thread\.Sleep"
chk "no silent catch"        -E "catch\s*(\([^)]*\))?\s*\{\s*\}"

# --- Event-handler leak rule (CLAUDE.md Hard Rule 0.1 #4) ---
plus=$(grep -rn $CS $EX -E "^\s*[A-Za-z_][A-Za-z0-9_.]*\s*\+=\s*[A-Za-z_]" $SRC | wc -l)
minus=$(grep -rn $CS $EX -E "^\s*[A-Za-z_][A-Za-z0-9_.]*\s*-=\s*[A-Za-z_]" $SRC | wc -l)
if [ "$plus" -gt "$minus" ]; then
  echo "FAIL: $plus '+=' vs $minus '-=' — every subscribe needs '-=' first"; fail=1
else
  echo "ok:   event subscribe/unsubscribe balanced ($plus/$minus)"
fi

[ $fail -eq 0 ] && echo "ALL CHECKS PASSED" || echo "CHECKS FAILED"
exit $fail
```

Create this script in **ES.1** and run it before every commit. A task card is not done while it reports FAIL.

### 12.5 Mandatory patterns

| Rule | Why | Example |
|---|---|---|
| Every event subscription writes `-=` immediately before `+=` | CLAUDE.md Hard Rule 0.1 #4; prevents duplicate handlers across mode switches | `runner.PhaseChanged -= OnPhase; runner.PhaseChanged += OnPhase;` |
| Every `Mat` lives in `using` or is disposed in `finally` | OpenCV memory is unmanaged; a per-cycle leak kills a production line in hours | `using (Mat crop = RegionCropper.Crop(...))` |
| Every GDI/DC/D3D handle released in `finally` | `GetWindowDC` / staging textures / frame pools leak silently | `try { ... } finally { ReleaseDC(hWnd, hdc); }` |
| Core `async` methods use `.ConfigureAwait(false)` | Core must never capture a UI context | `await client.ReadAsync().ConfigureAwait(false)` |
| UI updates from worker threads go through the captured `SynchronizationContext` | Cross-thread WinForms access throws | `_ui.Post(_ => button.Refresh(), null)` |
| Every `catch` either logs via `ScreenLogger` or sets an `out string error` | Silent failures are the hardest production bug class | `catch (Exception ex) { error = ex.Message; return false; }` |
| Copied files carry a provenance header | Makes divergence traceable | see below |

```csharp
// ---------------------------------------------------------------------------
// COPIED VERBATIM from EasyVision: BeeGlobal/Protocol/PlcClient.cs
// Source commit: <git rev-parse --short HEAD at copy time>
// Copied on:     <YYYY-MM-DD>   Task: ES.2
// Changes:       namespace only (PlcLib -> EasyScreen.Core.Plc)
// DO NOT refactor. Fixes must be noted in docs/architecture/CODEX_HISTORY.md.
// ---------------------------------------------------------------------------
```

### 12.6 Frozen public API

The signatures in §4 and §5 are a **contract**. Do not rename, add, remove, or reorder public members without asking. Specifically frozen:

`ICaptureEngine`, `ScreenCaptureService`, `CaptureSizeGuard`, `SizeCheck`, `ScreenOcrRuntime`, `OcrReadResult`, `ScreenPlcLink`, `ScreenCheckRunner`, `ScreenRunOutcome`, `TriggerService`, `ScreenLogger`, `LogRecord`, `ImageArchive`, `ProjectStore`, `RoiCanvas`, `AppSession`, and every `Model/` type and enum.

Internal/private members are yours to design.

### 12.7 Per-task verification

Every task card must produce, in `CODEX_HISTORY.md`, the **actual output** of:

```bash
MSBuild EasyScreen.sln /t:Build /p:Configuration=Release "/p:Platform=Any CPU" /v:minimal
```

```bash
bash tools/check_easyscreen.sh
```

Plus the task's own acceptance evidence from the §8 table. "Should pass" is not evidence — paste what ran.

**Manual acceptance for the risky tasks:**

| Task | Must physically demonstrate |
|---|---|
| ES.3b | A captured PNG of **each of the four §9 Q3 reference targets** (Notepad, WPF, Edge, Calculator), each passing the blank-frame probe, plus a 1,000-iteration soak showing stable handle count and GPU memory |
| ES.6 | Move/resize/rotate a ROI at 100 %, 400 %, and after panning — the ROI must stay locked to the same pixels |
| ES.12b | Log file before/after retention; a forced disk-low condition disabling image saving and writing FATAL |
| ES.13 | `FakePlcTransport` write log showing correct values at correct addresses and in the right order; a > 5 % resize aborting before OCR |
| ES.14 | Frame-by-frame screenshots of the button stepping Triggered → Checking → Sending → Complete; password `797979` accepted, `000000` refused with the attempt counter and lockout |

### 12.8 Review checklist before declaring a task done

```
[ ] Only files from the §12.2 manifest were created or modified
[ ] No EasyVision file changed  (git diff --name-only | grep -E '^(Bee|EasyVision)' -> empty)
[ ] MSBuild Release Any CPU: 0 errors, and the warning count is recorded
[ ] bash tools/check_easyscreen.sh -> ALL CHECKS PASSED
[ ] Task-specific acceptance evidence pasted into CODEX_HISTORY.md
[ ] Every new event subscription has '-=' before '+='
[ ] Every new Mat / DC / D3D resource has a disposal path on the error branch too
[ ] Public API matches §12.6 exactly
[ ] Nothing from the §12.3 out-of-scope list was built
[ ] Commit message is exactly [ES.<n>] <summary>
```

### 12.9 Autonomous resolution — do not stop to ask

**Run ES.1 → ES.15 to completion in one pass.** Do not pause for approval between task cards, and do not ask questions — §9 has already answered them. When something unexpected happens, resolve it with the table below, log the decision, and continue.

| Situation | Resolve like this |
|---|---|
| A task seems to need a file outside the §12.2 manifest | Create it **inside the nearest existing folder**, keep it `internal`, and list it in `CODEX_HISTORY.md` under "Files added beyond manifest" with one line of justification |
| A frozen API signature (§12.6) does not fit | Keep the public signature; adapt internally with a private helper. If genuinely impossible, change it, and record the old and new signature plus the reason |
| A copied file needs edits beyond the namespace | Make the minimum edit, keep the provenance header, and add a `// DIVERGENCE:` comment on each changed line; summarise in `CODEX_HISTORY.md` |
| WGC is unavailable on the build machine | Still implement `GraphicsCaptureEngine` fully. Mark its ES.3b acceptance `NOT VERIFIED — WGC unsupported on build host`, verify the other three engines, and continue |
| A reference target from Q3 will not launch | Substitute any window of the same class (any Chromium browser, any WPF app) and record the substitution |
| No PLC available | Use the Q7 `FakePlcTransport`; mark PLC acceptance `VERIFIED VIA FAKE` |
| Python/OCR not installed | Verify the "not initialised" error path per Q10; mark OCR acceptance `NOT VERIFIED — no Python runtime` and continue |
| A task exceeds ~2 days of effort | Ship the **narrowest version that fully satisfies its acceptance row**, and open a follow-up note in `CODEX_HISTORY.md`. Do not silently reduce scope below the acceptance row |
| The plan contradicts itself or the code | Prefer: (1) the §12.6 frozen API, (2) the locked decisions in §1.1 and §9, (3) the narrative text. Fix this document and record the correction |
| You want to write "for now" / "TODO" / "simplified" | Do not. Either implement it to the acceptance row, or record it explicitly under "Deferred" in `CODEX_HISTORY.md` with the reason |

**The only hard stops** — everything else is your call:

1. A change would modify an EasyVision file (forbidden by §12.1 #2 — find another way).
2. A change would delete or overwrite user data, an existing project file, or anything outside `EasyScreen/`, `EasyScreen.Core/`, `tools/`, `docs/`.
3. `MSBuild` cannot produce a green Release Any CPU build after a genuine attempt to fix it.

At the end, report: tasks completed, tasks with `NOT VERIFIED` acceptance and why, autonomous decisions taken, files added beyond the manifest, and anything deferred.

---
