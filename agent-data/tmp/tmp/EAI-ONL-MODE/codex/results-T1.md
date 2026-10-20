# T1 — WPF online unit-test feasibility (blocked)

Reviewed: 2026-10-18. No code, project, package, build, or test changes were made.

## Decision

Blocked under the approved `new files only` scope. No ineffective test project was created.

## Evidence

- `EasyLabel/src/EasyAI.Wpf/ViewModels/OnlineStatusViewModel.cs:230-301` declares `TryParseThresholds`, `TryParseStatus`, and `OnlineStatusSnapshot` as `internal`.
- `EasyLabel/src/EasyAI.Wpf` has no `InternalsVisibleTo` entry for a WPF test assembly. A new test project therefore cannot compile direct parser assertions without an existing source edit.
- `EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj:15-38` references Core, Commands, Online, SegAI, Camera, Onnx, and builds/copies Agent MCP output. The handoff explicitly permits a new reference-based test project only when it does not drag the whole application.
- `EasyLabel/tests/EasyLabel.SegAI.Tests/EasyLabel.SegAI.Tests.csproj:28-96` tests selected UI-pure rules by linked source, but a source-linked substitute for the requested VM/adapter tests is outside the approved scope.
- `OnlineControlAdapter` is public, but its running behavior depends on the sealed `OnlineSession` (`EasyLabel/src/EasyLabel.Online/OnlineSession.cs:10-24`), so a fake adapter/session cannot exercise its attached state without constructing the real Online runner graph.

## Required scope decision

Authorize either (1) a production `InternalsVisibleTo` seam plus a dedicated WPF test project, or (2) an explicitly approved isolated source-link test scope. Keep the test project at C# 7.3 syntax and add no NuGet packages.

## Verification

No build or test attempted: the handoff's dependency condition was not met.
