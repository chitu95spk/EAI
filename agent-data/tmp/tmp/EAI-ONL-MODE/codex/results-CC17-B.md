# CC17-B — WPF unit tests: PASS

## Files created

- `EasyLabel/src/EasyAI.Wpf/Properties/InternalsVisibleToTests.cs` — only the exact fully qualified assembly attribute required by the brief.
- `EasyLabel/tests/EasyAI.Wpf.Tests/EasyAI.Wpf.Tests.csproj`
- `EasyLabel/tests/EasyAI.Wpf.Tests/OnlineStatusViewModelTests.cs`

The new net9.0-windows/UseWPF project references the actual EasyAI.Wpf project, uses C# 7.3 with implicit usings/nullable disabled, and references only existing centrally listed test packages. No linked test source or production behavior changes. VM construction/disposal uses an STA thread and Dispatcher; adapter tests use a real locally constructed Recipe/OnlineRunner/OnlineSession.

## Commands and numbers from logs

Successful command, from the repository root:

```powershell
dotnet test EasyLabel/tests/EasyAI.Wpf.Tests -c Release -p:RestoreConfigFile="E:\Code\EasyVision_Unisen - NoService\tmp\EAI-ONL-MODE\codex\cc17-nuget.config" -p:NuGetAudit=false --logger "console;verbosity=normal" -bl:artifacts/EAI-ONL-MODE/cc17-b/root-20261018T145704223/wpf-tests.binlog
```

Log: `tmp/EAI-ONL-MODE/cc17-b/root-20261018T145704223/wpf-tests.log`.

**Exit 0; 12 total, 12 passed, 0 failed; 0 compiler errors; 93 warning diagnostic lines; test time 4.7284 seconds.** Warning lines include existing production nullable diagnostics and NU1701 for the transitive SkiaSharp.Views.WPF package; this is a full-log diagnostic-line count, not a unique-warning count. Agent MCP built and its production post-build copy completed successfully. There is no MCP dependency/copy blocker.

The run covers idle/full/missing-p95/negative-counter/malformed status JSON, all eight requested threshold inputs, Unknown/Ok/Over budget states including p95 equal to budget, command CanExecute running-to-stopped transitions, and adapter Attach/Detach/NOT_RUNNING.

Earlier environment-only failures are preserved under `tmp/EAI-ONL-MODE/cc17-b/root-{20261018T145150847,20261018T145222670,20261018T145248961,20261018T145308104,20261018T145430914,20261018T145611998}/`, with matching binlogs under `artifacts/EAI-ONL-MODE/cc17-b/`. They encountered SDK-directory access, user NuGet.Config access, or an initially empty sandbox package cache; none was a compiler or MCP-copy defect.

Successful process settings: TEMP/TMP=`<repo>/tmp/EAI-ONL-MODE`; APPDATA/LOCALAPPDATA=`cc17-appdata`/`cc17-localappdata` beneath that folder; MSBUILDSDKREFERENCEDIRECTORY=`cc17-sdk-roots`; NUGET_PACKAGES=`nuget`; NUGET_HTTP_CACHE_PATH=`cc17-http-cache`; NUGET_PLUGINS_CACHE_PATH=`cc17-plugin-cache`. PowerShell native execution used ErrorActionPreference=Continue. The local NuGet configuration has no feeds and reads existing user/VS caches as fallback folders; no packages or dependencies were added. MSBuild's SDK-root environment override avoids its per-user SDK lookup, as defined in [ToolLocationHelper source](https://source.dot.net/Microsoft.Build.Utilities.Core/ToolLocationHelper.cs.html).

## Audit and review

Task ID: CC17-B; exact approved new-only scope and files are above. The current user request and cc17-brief/HANDOFF authorize focused EasyLabel build/test execution. Architect plan CC17-r1 preserves production behavior, existing dependencies, real WPF/session paths, and C# 7.3 authored syntax. A remains frozen; OnlineStatusViewModel.cs was not edited. No fabricated Work approval, commit, tracking, wiring, legacy build, hardware, training, or model work.

Scoped whitespace/conflict-marker/declaration inspection passed; test compilation uses LangVersion 7.3. First distinct roots were environment access errors; no compiler errors remain. The independent reviewer requested exact one-line IVT content; the same writer corrected it and the same reviewer returned **ACCEPT**. The tests verify requested unit behavior; they do not prove application wiring or visual/DPI behavior, which belongs to C. Existing production dependency warnings remain.

ID: CC17-B
Phase: process
Changed files: the three new files listed above
Verify result: pass — 12/12 tests; 93 warning diagnostic lines; 0 compiler errors
Next ID: CC17-C — AutomationIds, lifecycle binding, capture validation and DPI smoke
