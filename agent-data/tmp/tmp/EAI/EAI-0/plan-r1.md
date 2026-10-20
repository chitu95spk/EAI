# EAI-0 implementation plan — revision EAI-0-r1-plan-v1

Source: easyvision-architect (opus) single pass, 2026-10-05; saved by root. Master plan: tmp/EAI/plan.md r4.
Class: LOGIC/COMPLEX → sonnet implementer, 3 sequential slices (S1 → S2 → S3). One EAI-0 commit after all slices + verification.

## Decisions
- TFMs: Core, Ai, Core.Tests → net9.0; Vision, App → net9.0-windows; new SegAI, Camera, SegAI.Tests → net9.0-windows; EasyAI.Wpf → net9.0-windows + UseWPF. LangVersion stays 12.0.
- System.Drawing.Common stays 8.0.19 (bump to 9.0.x only on MSB3277/NU1605; record version).
- New central packages: WPF-UI 4.0.3, CommunityToolkit.Mvvm 8.4.0 (confirm on restore; on NU1102 pin nearest 4.0.x/8.4.x, never floating). ScottPlot.WPF deferred to EAI-4 (unused now).
- MVS: direct P/Invoke of MvCameraControl.dll (not the net40 MvCameraControl.Net.dll). EAI-0 = ICameraSource contract + SDK load + MV_CC_GetSDKVersion only; enumerate/open/grab/close + struct layouts → EAI-6 (hardware). Optional non-shipping probe tmp/EAI/EAI-0/spike-mvsnet/ may call the net40 wrapper's GetSDKVersion for evidence.

## Evidence (architect, path:line)
- EasyLabel.sln maps all solution platforms to project Any CPU; x64 from Directory.Build.props:6 PlatformTarget. New projects follow it.
- Repo-root Directory.Build.targets:2-11 imports into EasyLabel, acts only with EasyVisionIsolatedVerification=true → never pass that flag. A spike under tmp/ imports the root Directory.Build.props (.NET Framework) → spike folder needs its own empty Directory.Build.props/.targets.
- Core.Tests links src/EasyLabel.App/Shell/AutoSaveService.cs (Core.Tests.csproj:25) → must keep compiling on net9.0.
- ProjectService.Open (ProjectService.cs:23-46) writes .easylabel/project.json + classes.txt into the opened folder; EnumerateImages is private → shell uses Project.Images. Smoke uses a fixture copy only.
- AtomicFile.WriteAllText public (Core/Io/AtomicFile.cs:9) → reuse for settings/layout.
- SEGAI_GetVersion returns kSegAIVersion=1 (BeeNativeSegAI.cpp:17,32) → expected ABI 1. SEGAI_GetBuildInfo returns static literal (cpp:37-40) → IntPtr + PtrToStringUTF8, never free. Both extern "C" cdecl (SegAINativeExport.h:13,71-72).
- bin/Release has BeeNativeSegAI + opencv_world455 + openvino, no c10/torch; BeeNativeSegAI/x64/Release has c10/torch*/libiomp5md, no opencv/openvino. c10 static import when torch enabled; torch_cpu delay-loaded (vcxproj:190-194) → loader = primary folder + extra dependency folders.

## Invariants
1. No file outside EasyLabel/, tmp/EAI/**, artifacts/EAI/** changes. EasyVision read-only. C# 7.3 rule does not apply to EasyLabel.
2. Existing EasyLabel .cs behavior unchanged; edits only for net9 compile errors; behavior change needed → stop. New net9 warnings (e.g. WFO1000) recorded, not fixed.
3. Native ownership: never copy BeeNativeSegAI.dll/deps into output; never free build-info string; on failed load remove every AddDllDirectory cookie, no handle leak; after success module stays loaded for process lifetime.
4. Never SetDefaultDllDirectories / SetDllDirectory (process-wide).
5. UI thread never does native loads, image decode, file enumeration (Task.Run then marshal back; bitmaps Frozen).
6. UI rules A/B (plan §5): only WPF-UI controls, or standard WPF elements styled by WPF-UI ControlsDictionary where no ui: equivalent exists (Grid, GridSplitter, Image, StatusBar); every visible control wired; no placeholder/"coming soon"; theme brushes only; exactly one Primary button per panel; ICommand + CanExecute; no guidance text, titles + tooltips only.
7. Runtime user settings in %LOCALAPPDATA%\EasyAI\ (product behavior); every agent run redirects into artifacts/EAI/EAI-0/<run>/ via --settings-dir; corrupt/missing JSON → defaults, never crash.
8. No MV_CC_EnumDevices/open/grab in EAI-0.
9. No floating package versions; no packages beyond WPF-UI and CommunityToolkit.Mvvm.

## S1 Retarget (≈8 files)
- M EasyLabel/Directory.Packages.props: add the two PackageVersion entries.
- M src/EasyLabel.Core/EasyLabel.Core.csproj net9.0; src/EasyLabel.Ai net9.0; src/EasyLabel.Vision net9.0-windows; src/EasyLabel.App net9.0-windows; tests/EasyLabel.Core.Tests net9.0.
- M EasyLabel.sln: add new projects per slice (`dotnet sln add`, src/tests solution folders, all configs → Any CPU like existing). Directory.Build.props unchanged.
- Gate: build clean, Core test count = baseline.

## S2 Engines (≈17 files)
### src/EasyLabel.SegAI/ (namespace EasyLabel.SegAI.Native), net9.0-windows, refs Core
- NativeSegAiOptions.cs: `sealed record NativeSegAiOptions(string? PreferredFolder, IReadOnlyList<string> ExtraDependencyFolders)`.
- NativeSegAiFolderResolver.cs: candidates in order: options.PreferredFolder; env EASYAI_SEGAI_DIR; AppContext.BaseDirectory; dev fallback walk up ≤8 parents for bin\Release\BeeNativeSegAI.dll (+ <repo>\BeeNativeSegAI\x64\Release as extra dependency folder if present). Returns probed list for error text.
- NativeSegAiLibrary.cs: `static NativeSegAiLoadResult TryLoad(NativeSegAiOptions o)` idempotent, thread-safe (lock), retryable until first success; `static bool IsLoaded`; registers NativeLibrary.SetDllImportResolver once (static ctor, guarded) returning the preloaded handle for "BeeNativeSegAI.dll"; sequence: AddDllDirectory(native folder + extras) → LoadLibraryExW(fullPath, 0, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS) → version calls.
- NativeSegAiLoadResult.cs: Status {Loaded, NotFound, LoadFailed, EntryPointMissing, AbiMismatch}; Win32Error, LoadedPath, ProbedFolders, AbiVersion, BuildInfo, Message (folder, code, missing-dependency hint for 126); ExpectedAbiVersion = 1.
- Interop/SegAiNative.cs: DllImport("BeeNativeSegAI.dll", Cdecl) int SEGAI_GetVersion(); IntPtr SEGAI_GetBuildInfo().
- Interop/Kernel32.cs: internal LoadLibraryExW, AddDllDirectory, RemoveDllDirectory, FreeLibrary (SetLastError).
### src/EasyLabel.Camera/ (namespaces EasyLabel.Camera, EasyLabel.Camera.Mvs), net9.0-windows, no OpenCvSharp
- ICameraProvider.cs: `Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken)`, `ICameraSource Create(CameraInfo)`.
- ICameraSource.cs (IAsyncDisposable): CameraInfo Info; CameraState State; Task OpenAsync(ct); Task<CameraFrame> GrabOneAsync(TimeSpan timeout, ct); IAsyncEnumerable<CameraFrame> StreamAsync(ct); Task CloseAsync(); event EventHandler<CameraFaultEventArgs> Faulted. Contract: SDK work on internal worker thread, never UI; stream latest-frame-wins (dropped frames disposed by source); Faulted raised off UI thread.
- CameraFrame.cs: sealed IDisposable; owns ArrayPool buffer copied from SDK buffer before FreeImageBuffer; Width, Height, Stride, PixelFormat, FrameNumber, TimestampUtc; consumer must Dispose.
- CameraInfo.cs, CameraPixelFormat.cs (Mono8, Bgr8, Rgb8, BayerRG8…), CameraState.cs, CameraFaultEventArgs.cs.
- Mvs/MvsSdk.cs: `static MvsSdkLoadResult TryLoad()`; runtime dir = env MVCAM_COMMON_RUNENV\Win64_x64, else C:\Program Files (x86)\Common Files\MVS\Runtime\Win64_x64; same LoadLibraryEx flags + resolver; version 0xAABBCCDD → A.B.C.D (also expose raw hex).
- Mvs/MvsNative.cs: only DllImport("MvCameraControl.dll", StdCall) uint MV_CC_GetSDKVersion(). Rest in EAI-6.
- Mvs/MvsSdkLoadResult.cs; Interop/Kernel32.cs (internal duplicate; Core source untouched).
### tests/EasyLabel.SegAI.Tests/ (xunit, net9.0-windows, mirrors Core.Tests)
- NativeSegAiFactAttribute.cs: sets Skip at discovery when resolver finds no folder.
- NativeSegAiLibraryTests.cs: (a) empty folder under test output → NotFound, message contains path, no throw; (b) [NativeSegAiFact] → Loaded, AbiVersion==1, non-empty BuildInfo; (c) TryLoad twice → same handle, idempotent.
### Spike (not in sln): tmp/EAI/EAI-0/spike/
- Directory.Build.props + .targets (empty `<Project/>`), EaiSpike.csproj (net9.0-windows, x64, ManagePackageVersionsCentrally=false, ProjectReference SegAI + Camera), Program.cs printing one JSON line per engine (status, version, path, Win32 error) → artifacts/EAI/EAI-0/r1/spike.json.

## S3 EasyAI.Wpf shell (≈24 files): src/EasyAI.Wpf/
- EasyAI.Wpf.csproj: WinExe, net9.0-windows, UseWPF, ApplicationManifest; refs Core, SegAI, Camera; packages WPF-UI, CommunityToolkit.Mvvm.
- app.manifest: PerMonitorV2 DPI awareness.
- App.xaml(.cs): merge ui:ThemesDictionary + ui:ControlsDictionary; composition root without DI: StartupOptions → SettingsStore → culture → ApplicationThemeManager.ApplySystemTheme() → MainViewModel → MainWindow; DispatcherUnhandledException → ui:MessageBox + log.
- MainWindow.xaml(.cs) ui:FluentWindow: WindowBackdropType=Mica, ExtendsContentIntoTitleBar, SystemThemeWatcher.Watch(this, Mica, true); ui:TitleBar "EasyAI · <project folder>" with SymbolIcon; ui:InfoBar for engine/project errors; Grid columns Images | GridSplitter | Preview | GridSplitter | StepPanel, MinWidth 180/360/260, defaults 240/*/300; window MinWidth 880, MinHeight 560. Compact (<1100 DIP): left column + splitter collapse, right panel becomes TabControl (Images | Step) re-hosting the same pane instances. StatusBar: image i/N, selected size, SegAI + MVS status items (tooltips: path, build info). Code-behind only layout apply/save (DragCompleted debounced 500 ms, Closing, Reset).
- Infrastructure/StartupOptions.cs: --settings-dir, --culture en|vi, --project <folder>.
- Infrastructure/SettingsStore.cs: base = option, else env EASYAI_SETTINGS_DIR, else %LOCALAPPDATA%\EasyAI; settings.json + layout.json via AtomicFile; tolerant load.
- Infrastructure/AppSettings.cs (NativeSegAiFolder, UiCulture, SchemaVersion=1); LayoutSettings.cs (LeftWidth, RightWidth, window bounds/state, SchemaVersion=1; defaults here; load clamps to minimums + visible screen).
- Services/ImagePreviewLoader.cs: async BitmapImage OnLoad + IgnoreColorProfile + DecodePixelWidth ≤2048, Freeze; cancels previous; errors as message.
- Services/EngineStatusService.cs: SegAI + MVS TryLoad on Task.Run at startup and on native-folder change.
- ViewModels/MainViewModel.cs (ObservableObject): Project?, ObservableCollection<ImageItemViewModel>, SelectedImage, CurrentStep (WorkflowStep), AvailableSteps (EAI-0: Data only); commands OpenFolderCommand (async, Microsoft.Win32.OpenFolderDialog, ProjectService.Open on Task.Run), NextImage/PreviousImage (↑/↓), ResetLayoutCommand, ChooseNativeFolderCommand. Field-based [ObservableProperty] (LangVersion 12).
- ViewModels/ImageItemViewModel.cs, EngineStatusViewModel.cs (state, version, path, message, RequiresRestart), WorkflowStep.cs (enum 1..5; only registered steps shown).
- Views: ImageListPane (ui:ListView virtualized, header count); ImagePreviewPane (read-only Image Stretch=Uniform, empty without project, no hint text; replaced by canvas in EAI-2); StepPanel (stepper = RadioButtons bound to enum, AvailableSteps only); DataStepView (project path, image/class counts, "Open folder" = the only Primary).
- Resources/Strings.resx (en neutral) + Strings.vi.resx; strongly typed (StronglyTypedLanguage=CSharp, public) or checked-in Strings.Designer.cs fallback; XAML {x:Static}.
- Engine-missing InfoBar action "Choose folder": nothing bound yet → retry load now; else save setting + "Restart to apply" InfoBar.

## Verification (authorized: build, run_tests, ui_smoke)
- Env (task process only): TEMP/TMP=tmp/EAI/EAI-0/temp; DOTNET_CLI_TELEMETRY_OPTOUT=1; -nodeReuse:false. NuGet cache: see decision D4.
- Step 0 baseline before S1: `dotnet test tests/EasyLabel.Core.Tests -c Release` (net8 count); if net8 runtime absent, record and use --list-tests after S1 + static [Fact]/[InlineData] count on pre-change tree.
- Build: `dotnet build EasyLabel/EasyLabel.sln -c Release -p:Platform=x64 -nodeReuse:false -bl:artifacts/EAI/EAI-0/r1/build.binlog > artifacts/EAI/EAI-0/r1/build.log 2>&1`; grep ": error " = 0; grep "warning NU1".
- Tests: `dotnet test EasyLabel/EasyLabel.sln -c Release --no-build --logger "trx;LogFileName=eai0.trx" --results-directory artifacts/EAI/EAI-0/r1/test > artifacts/EAI/EAI-0/r1/test.log 2>&1`; Core count = baseline; SegAI test (b) passes (not skipped).
- Spike: `dotnet run --project tmp/EAI/EAI-0/spike -c Release > artifacts/EAI/EAI-0/r1/spike.json`: SegAI Loaded ABI 1 (126 → extra folder logic suspect); MVS Loaded + version; no enumerate.
- UI smoke: fixture 3-5 repo images copied into tmp/EAI/EAI-0/fixture-project/; launch `EasyAI.Wpf.exe --settings-dir artifacts/EAI/EAI-0/r1/smoke/settings --project tmp/EAI/EAI-0/fixture-project`; PowerShell UIAutomation script tmp/EAI/EAI-0/smoke/: screenshot default (PrintWindow), select image + screenshot, resize 900x600 (compact) + screenshot, seed non-default layout.json → relaunch → applied → ResetLayout via AutomationId → close via WindowPattern → layout.json = defaults; repeat with --culture vi. Real splitter drag owed unless SendInput works. Regression: start EasyLabel.App.exe, 5 s, main window exists, close; note writes outside project (PythonPaths uses %APPDATA%).
- Owed: 150% DPI screenshots (machine setting), light/dark OS switch, MVS enumerate/open/grab (hardware).

## Risks
- Package versions may not resolve → pin nearest listed. Mvvm 8.4 partial properties need C# 13 → field-based [ObservableProperty].
- net9 warnings (WFO1000, NU190x) recorded; error needing behavior change → stop.
- BinaryFormatter removed in net9: App .resx ImageList streams covered by App start smoke; failure → stop, escalate.
- Partial native build in bin/Release → extra dependency folders + clear 126 message. Torch delay-load for training → EAI-4.
- SetDllImportResolver once per assembly → static ctor guard.
- ProjectService.Open writes .easylabel/ → fixture only; EAI-1 reconciles with .easyai/.
- Compact-mode tab adds S3 complexity: over budget → stop and report, never drop the rule.

## Decisions taken by root / user (filled at approval)
- D1 camera scope (escalation 1): EAI-0 = interface + SDK version; enumerate/open/grab owed to EAI-6 (consistent with user's hardware_test=false).
- D2 net40 wrapper (escalation 2): P/Invoke primary; optional evidence probe.
- D3 path (escalation 3): everything under tmp/EAI/EAI-0/; contract deferred_file corrected.
- D4 NuGet cache (escalation 4): user waiver 2026-10-05 — keep global ~/.nuget/packages; only TEMP/TMP redirected into tmp/EAI/EAI-0/temp.
- D5 Reset layout in EAI-0 (escalation 5): contract wins; plan.md §8 note.
