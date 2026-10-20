# CC17-C — OnlineStatusWindow follow-ups: ACCEPT

## Files changed

- `EasyLabel/src/EasyAI.Wpf/Views/OnlineStatusWindow.xaml`
- `tmp/EAI-ONL-MODE/smoke/Program.cs`
- `tmp/EAI-ONL-MODE/smoke/OnlineStatusSmoke.csproj`
- `tmp/EAI-ONL-MODE/smoke/app.manifest` (new)

Added exactly eight AutomationIds: RecipeId, Accepted, Completed, Dropped, Failed, P95BudgetText, ThresholdMessage and RunningIndicator. Per the user's follow-up, removed `Unloaded="OnClosed"` and retained `Closed="OnClosed"`. No OnlineStatusViewModel.cs edit. The 360-DIP Vietnamese capture shows no layout defect, so the two-column grids remain unchanged.

The harness converts capture pixels to Bgr32 and compares RGB tuples, ignoring the unused alpha/padding byte. Its deterministic checks reject four uniform-RGB fixtures, including differing alpha bytes, and accept one RGB-varying fixture. Capture waits are bounded; DwmFlush precedes full-content capture. Only validated PrintWindow flags=2 output can be saved. Flags=0 may trigger a repaint but its image is discarded. No desktop copying or bitmap scaling is used.

## Commands and build evidence

All commands ran from the approved repository, sequentially after B completed. Process-local TEMP/TMP, APPDATA/LOCALAPPDATA, SDK-root and NuGet-cache settings are documented in results-CC17-B.md. No machine settings were changed.

```powershell
dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release --no-restore -bl:artifacts/EAI-ONL-MODE/cc17-c/root-20261018T150126793/wpf-build.binlog
dotnet build tmp/EAI-ONL-MODE/smoke/OnlineStatusSmoke.csproj -c Release --no-restore -bl:artifacts/EAI-ONL-MODE/cc17-c/full-20261018T150549825/smoke-build.binlog
```

| Build | Log | Exit / numbers |
| --- | --- | --- |
| Production WPF, including current XAML | `tmp/EAI-ONL-MODE/cc17-c/root-20261018T150126793/wpf-build.log` | 0; 0 errors, 24 warnings; 12.86 s |
| Final harness | `tmp/EAI-ONL-MODE/cc17-c/full-20261018T150549825/smoke-build.log` | 0; 0 errors, 0 warnings; 0.89 s |

Both binlogs exist: production 780726 bytes; final harness 397561 bytes. The initial harness build also passed; subsequent builds were justified by capture-readiness fixes.

The production and copied harness `EasyAI.dll` match: 1589760 bytes, SHA256 `1EFFECE51C0AE1FED2E1855E54BE9D9A5993FE5D013D63BEE08FC154CB277FF6`. The harness therefore used the freshly compiled production window.

## Native capture evidence

The apphost EXE was launched so the PerMonitorV2 application manifest applies:

```powershell
& "tmp/EAI-ONL-MODE/smoke/bin/Release/net9.0-windows/OnlineStatusSmoke.exe" "artifacts/EAI-ONL-MODE/ui-smoke/CC17-20261018T080551047Z-dpi96" --dpi96
& "tmp/EAI-ONL-MODE/smoke/bin/Release/net9.0-windows/OnlineStatusSmoke.exe" "artifacts/EAI-ONL-MODE/ui-smoke/CC17-20261018T080551047Z-dpi144" --dpi144
```

Logs: `tmp/EAI-ONL-MODE/cc17-c/full-20261018T150549825/smoke-96.log` and `smoke-144.log`.

**Both exits 0. Each run logs validator=PASS, blankCases=4, visibleCases=1. All eight captures validate on attempt 1 with flags=2. GetDpiForWindow reports 96 for all eight windows; width is 360 DIP.** The requested-DPI option records intent; the manifest and host display determine native DPI. The requested-144 directory contains files named dpi96 and is explicitly not 144-DPI evidence. A true 144-DPI window was not achieved in this environment; native 144-DPI rendering remains unverified. No bitmap was rescaled to imitate it.

Final native-96 PNGs under `artifacts/EAI-ONL-MODE/ui-smoke/CC17-20261018T080551047Z-dpi96/`:

| PNG | Pixels | Bytes | SHA256 |
| --- | --- | --- | --- |
| en-US-idle-dpi96.png | 360x547 | 13309 | 1C68F7B5D06495C4ACBFFE47AC17DA3AD8A90607901E6D05C130780257653C32 |
| en-US-running-dpi96.png | 360x567 | 16500 | BAE01E512F1ADEF84891A0CB8991CAC1A60AE5A6951350A3184DBF173C08B14D |
| vi-VN-idle-dpi96.png | 360x547 | 15051 | 485972DC8C45F096F18DF5D4D622268E79CEC3EAC2BC9F6DC868F97A6962AF19 |
| vi-VN-running-dpi96.png | 360x567 | 18428 | 629EA7E88C924A2834305B73ADF5E61A157B59B4946BB302CBEA2FB40EF0F4DA |

Root and independent reviewer inspected all four final captures via thumbnails. English/Vietnamese, idle/running, full custom title bar and close affordance render consistently. The long Vietnamese recipe wraps fully; counters, p95, thresholds, inputs, Apply and Stop remain visible without clipping or overlap. No layout change was warranted. The fake inference host supplies fixture state only, with no inference-performance claim.

Earlier runs `CC17-20261018T080215962Z-96dpi` and `CC17-20261018T080358507Z-dpi{96,144}` are retained but superseded: the first correctly rejected a blank capture; the second accepted partial flags=0 images that root visual QA rejected. Their logs are under `root-20261018T150126793` and `fix-20261018T150356700`. They are not final visual PASS evidence.

## Audit and residual verification

Task ID: CC17-C; approved scope is the four listed files, under architect plan CC17-r1 and the current user brief/follow-up. Preserve production VM/adapter/session/window use, C# 7.3 authored syntax, existing layout unless an observed defect requires it, accepted Package A, and unrelated dirty work. No new packages, wiring, model work, training, hardware, legacy application build, Git writes or tracking update.

Scoped diff/whitespace/conflict-marker and declaration inspection passed. Harness compilation explicitly uses C# 7.3; production LangVersion was not changed. No compiler root errors were found. The initial runtime root was a genuinely uniform capture; the next was incomplete flags=0 window rendering, fixed by accepting full-content captures only and synchronizing composition. The same writer made the fixes; independent reviewer returned **ACCEPT** for final source, evidence and visual QA. Original dirty-file hash comparison found no drift.

Native 144 DPI, OS UIA traversal and a live theme-change lifecycle test remain unverified. AutomationId properties and the single Closed handler are source/compile verified. Overall product acceptance remains with Claude; no application wiring or production-readiness claim is made.

ID: CC17-C
Phase: ui
Changed files: the four files listed above
Verify result: pass — production build 0 errors/24 warnings; harness 0/0; four native-96 visual cases pass; native 144 DPI unavailable in this run
Next ID: n/a — requested B/C scope complete; no autonomous follow-on work
