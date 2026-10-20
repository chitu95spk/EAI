# CC-3 brief for Codex (EAI-CC, plan EAI-ONL-MODE-r1-plan-v1 phase B)

Reply in English. Bound by tmp/EAI-ONL-MODE/codex/HANDOFF.md sections 0 and 6. You may build/test EasyLabel projects (project-local TEMP/TMP);
you do NOT commit, stage or stash. Single writer for everything below. Save results to `tmp/EAI-CC/results-CC3.md`.

## Goal (behaviour-neutral refactor)
Chat slash commands must go through the command registry instead of the `AgentSlashKind` switch in
`EasyLabel/src/EasyAI.Wpf/ViewModels/LabelStepViewModel.AgentChat.cs` (`TryHandleSlashCommand`, about lines 315-390).
Design source: `tmp/EAI-COMMERCIAL/command-code-architecture.md` section 5 row CC-3 and sections 2-4.
- Add registry descriptors AGT.CLEAR, AGT.CHECK, AGT.SUGGEST, AGT.CANCEL, AGT.MODEL.SET, AGT.MODE.SET, AGT.FORGET, AGT.HELP
  (Aliases = the slash word without slash: clear, check, suggest, cancel, model, mode, forget, help). CC-2 deliberately skipped AGT.MODEL.SET, AGT.MODE.SET, AGT.FORGET.
  Follow the existing patterns (`ViewModelCommandHandlers`, `ViewModelCommandEndpoint`, `OnlineCommandDescriptors`, `WpfCommandHost`); look at how CC-2/CC-12 added commands and at the catalog snapshot tests
  (`EasyLabel/tests/EasyLabel.Commands.Tests/Catalog/commands.catalog.json` is a snapshot: update it only through the established regeneration path and explain every changed line).
- `AgentSlashCommands.Parse` and `AgentSlashKind` stay (the parser tests must keep passing unchanged). `TryHandleSlashCommand` keeps its signature and observable behaviour
  (same notices, same clearing of the prompt box, same busy rule `CanClearHistory`, unknown `/word` still shows the unknown-command notice, empty/prompt text still returns false).
  Replace the switch body by a registry invocation keyed by the command name/alias; the VM-side effects (clear history, forget vocabulary, set model, set mode, run check/suggest/cancel) move into
  the handler targets or stay as VM methods called by them.
- `/help` text is generated from the registered AGT.* descriptors (aliases plus the existing argument hints `<name>` and `<current|all|remaining>`), result identical to today's `AgentSlashHelp` string
  when all eight commands are present. Do not add or change resource keys (Resources/Strings* are dirty from other sessions: DO NOT EDIT THEM).
- Remove the inline English fallbacks of `SlashText(key, english)` for these commands only if every key already exists in Strings.resx and Strings.vi.resx (they do: AgentSlashHelp, AgentSlashUnknownFormat,
  AgentSlashClearBusy, AgentSlashUnavailable, AgentSlashModelFormat, AgentSlashModelUnknownFormat, AgentSlashModeFormat, AgentSlashModeUnknownFormat, AgentSlashForgetDone). Verify before removing.
- Online mode gating: the AGT.* descriptors must not be listed for `online` / `online-running` (they are chat commands for simple/advanced). Use the existing Modes mechanism and add a gating test.

## File discipline (IMPORTANT, other sessions have uncommitted edits)
`LabelStepViewModel.AgentChat.cs` already has ~137 uncommitted lines from ANOTHER session in hunks starting near lines 5, 502, 750, 771, 1015 of the working file.
Do not touch those hunks. Your edits there must stay inside the slash-command region (about 315-420) plus, if unavoidable, new `using` lines placed away from line 5's hunk.
Prefer NEW files (a new partial class file `LabelStepViewModel.SlashRegistry.cs`, new handler/descriptor files, new test files) over edits to existing ones.
Snapshots of the working files BEFORE your work are in `tmp/EAI-CC/cc3-before/` (AgentChat.cs, MainViewModel.cs, WpfCommandHost.cs, ViewModelCommandHandlers.cs, AgentSlashCommands.cs); Claude will diff against them to extract exactly your hunks.
Other dirty files you must NOT edit at all: MainWindow.xaml(.cs), Strings.*, AppSettings.cs, ShortcutTable.cs, Services/Agent/AgentRunner.cs, anything under EasyLabel.SegAI.
Allowed to edit (besides new files): LabelStepViewModel.AgentChat.cs (slash region only), MainViewModel.cs and WpfCommandHost.cs and ViewModelCommandHandlers.cs ONLY if the diff in each is a few contiguous lines; list every such file in the result.

## Verification you run (log to files, then grep)
- `dotnet test EasyLabel/tests/EasyLabel.Commands.Tests -c Release` (new tests for the 8 descriptors, aliases, mode gating, help generation, snapshot).
- Existing slash tests: `EasyLabel/tests/EasyLabel.SegAI.Tests` filtered to `AgentSlash|AgentLearning` (the csproj is dirty by another session: do not edit it; if the filter run cannot build for reasons outside your files, report exactly why).
- `dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release` 0 errors.
Report: files created, files edited with line ranges, test counts, what is unverified (no UI smoke of the real chat box).
