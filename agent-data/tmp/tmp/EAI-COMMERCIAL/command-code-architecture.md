# EasyAI command-code architecture (ARCHITECT, read-only study, 2026-10-17)

Scope: `EasyLabel/src` (EasyAI.Wpf, EasyLabel.Core, EasyLabel.Ai.Mcp, ...). Nothing was built or run; counts come from grep over the source and are approximate ("~"). Items marked (assumed) were derived from a method signature, not from reading the body.

Goal: every feature, tool and setting gets one stable CODE. Natural language -> intent -> code + args -> validated -> executed by the SAME registry that menus, buttons, hotkeys and slash commands use. The LLM sees a small catalog, answers with a strict JSON schema, and the app validates before acting.

---------------------------------------------------------------------------------------------------

## 1. Inventory: what exists and how it is addressed today

### 1.1 Seven parallel addressing systems (none references another)

| # | System | Where | Size | Naming style | Role |
|---|--------|-------|------|--------------|------|
| 1 | `[RelayCommand]` methods | 17 ViewModels in `EasyAI.Wpf/ViewModels` | ~158 commands | PascalCase method -> `XxxCommand` | the real actions (UI-bound) |
| 2 | `ShortcutId` enum + `ShortcutTable` | `Infrastructure/ShortcutTable.cs` | 51 ids | PascalCase (`ToolAiBox`, `AutoLabel`, `TagNg`) | key -> action; mapped to a ViewModel command by a hand-written `switch (ShortcutId)` in `MainWindow.xaml.cs` (~L614+) and in `Canvas/*` (53 `ShortcutId.` references) |
| 3 | `AutomationProperties.AutomationId` | all `.xaml` | ~558 ids | PascalCase with ad-hoc prefixes: `Tool*`, `Ctx*`, `Menu*`, `AgentChat*`, `Mcp*`, `ImportLabelMe*` | UIA smoke tests; the only thing that names "buttons" |
| 4 | Settings: `AppSettings` property | `Infrastructure/AppSettings.cs` | ~86 public members | PascalCase, string-typed enums (`AgentEffort`="medium", `AutoLabelSource`="segment", `ImageSource`="cameraMvs") | persisted values |
| 5 | Settings: `SettingsAccessor.Row*` + `SettingsModel.Page*` | `Infrastructure/SettingsAccessor.cs`, `SettingsModel.cs` | ~45 row ids, ~9 pages | camelCase strings (`agentMaxUsdPerDay`, `cameraConnect`) | Settings dialog rows; hand-mapped to #4 by switch |
| 6 | Slash commands | `Services/Agent/AgentSlashCommands.cs` + `TryHandleSlashCommand` in `LabelStepViewModel.AgentChat.cs` ~L319-445 | 8 words (`clear check suggest model mode cancel help forget`) | lower-case words, enum `AgentSlashKind` | typed chat commands; each case calls a RelayCommand or edits state |
| 7 | MCP tool names (labeller agent) | `EasyLabel.Ai.Mcp/EasyAiTools.cs` (+`AnalysisTools.cs`) | 11 tools: `get_image_info get_crop get_reference list_shapes sam2_segment propose_shapes memory_read memory_add grid_crop render_overlay measure` | snake_case | what the labelling LLM may call |

Plus: ~1111 `Strings.resx` keys (captions, notices; ad-hoc names such as `AgentSlashClearBusy`), `Core.Commands.ICommand` (undo stack: `Name`, `Execute`, `Undo`) which is string-named and only used for editing shapes, and map-IDs (`EAI-POS`, `EAI-CAM`, `EAI-UI`...) which are project-management tags, not runtime ids.

### 1.2 Concrete inconsistencies and hard-coded strings

1. The same action has 3-5 names. Example "train": `TrainStepViewModel.StartAsync` -> `StartCommand`; `ShortcutId.Train`; `MainViewModel.EasyTrainAsync`; `EasyTrain...` UIA ids; no slash command. "Fit": `CanvasViewModel.Fit`, `ShortcutId.Fit`, `FitWidth`, `FitSpace`, `CtxFit...`.
2. The MCP allow-list is written four times: `AgentCommandLine.AllowedTools` (one long comma string with `mcp__easyai__` prefix), `SuggestAllowedTools` (3 tools), Codex `enabled_tools` (L98), plus `EasyAiTools.Call` switch + `BuildDescriptions` + the skill `.md` files. Adding a tool means touching 5 places; nothing checks they agree.
3. Effort has three vocabularies: enum `AgentEffort {Economy, Normal, Best, XHigh, Max}`, settings text `low/medium/high/xhigh/max`, legacy text `economy/normal/best` (`AgentRunOptions.ParseEffort` maps). `Precision` easy/normal/hard, `BatchMode` current/all/remaining, `AutoLabelSource` segment/ai/agent, `ImageSource` files/cameraMvs: each is a hand-parsed string "enum" with its own tolerant parser.
4. Settings: clamp limits live as `const` in `AppSettings` (e.g. `AgentMaxUsdPerDayMax = 10000`, `AgentBatchWorkersMax = 32`) and again in the dialog rows; unit/min/max/default are not data, so an agent cannot read them. Good seed for a schema, but not machine readable today.
5. Preconditions are implicit: `CanRunAgent`, `CanClearHistory`, `CanvasPane.HandleKey`, `ShortcutInput.BlocksShortcuts` each re-implement "is this allowed now". Nothing can answer "which commands are valid right now?" - which is exactly the question needed to shrink the LLM catalog.
6. Commands that open dialogs cannot be driven headless: `OpenFolderAsync`, `OpenProjectFileDialogAsync`, `NewProjectAsync`, `ChooseNativeFolderAsync`, `ExportAsync`, `ImportLabelMeAsync`, `ShowConvertYolo`. An agent needs a path-argument variant of each.
7. Stub: `MainViewModel.NotYet()` (L2050) is a placeholder command bound in menus.
8. Slash `SlashText("AgentSlashClearBusy", "Cannot clear ...")` embeds English fallbacks in code at ~25 call sites: the text exists in resx AND in C#.
9. UI language of agent prompts is a flag (`PromptLanguage` ENG/VIE) separate from `UiCulture`; the keyword understanding of user intent in Vietnamese does not exist anywhere (the only NL understanding is "send the whole sentence to Claude Code").
10. No telemetry that says "which command was invoked by whom" (menu, hotkey, slash, agent). `log.csv` records only agent runs and prompt length.

### 1.3 God classes (where the actions are buried)

| File | Lines | Commands | Comment |
|------|-------|----------|---------|
| `ViewModels/LabelStepViewModel.cs` (+`.AgentChat` 1115, `.AgentAttach`, `.Classify`) | 2566 (+~1300) | 15 + 9 + 2 | label source switch, AI/agent/Segment-AI runs, accept/reject, batch, chat, slash, caps |
| `ViewModels/MainViewModel.cs` | 2474 | 34 | project, nav, export/import, tabs, layout, notices |
| `ViewModels/CanvasViewModel.cs` | 1844 | 29 | tools, edit, zoom, brightness, class select |
| `ViewModels/TrainStepViewModel.cs` (+`.Classify`) | 1749 | 10 | train setup/start/stop, fix, ultra, model use |
| `ViewModels/InspectViewModel.cs` | 1258 | 9 | |
| `Services/Agent/AgentRunFiles.cs`, `AgentResults.cs`, `AgentBatch.cs` | 768 / 776 / 501 | - | run folder, parsing, batch (8196 lines in 30 agent files) |
| `MainWindow.xaml.cs` | 848 | - | shortcut switch, key routing |

Pure `*Rules.cs` helpers (about 20) already exist and are UI-free - they are the natural handler bodies.

### 1.4 Assets worth keeping

`ShortcutTable` (single table, formatted display text), `SettingsAccessor` (id -> property), `AgentSlashCommands.Parse` (pure parser), `ProposalStore` (validated proposals with `Action` add/replace/remove, `MaxNewLabels=16`, `MaxLabelLength=64`), `AgentOutcomeRecording`, `AgentSpendTracker`/`AgentPrices` (cost already measured in USD), `AgentPromptRefiner` (small-model rewrite precedent), `Core.Commands.CommandStack` (undo), CommunityToolkit.Mvvm (source generators already in the build), `LangVersion 12`, net9.

---------------------------------------------------------------------------------------------------

## 2. ID scheme

### 2.1 Form
`DOMAIN.VERB.OBJECT[.QUALIFIER]` - upper-case ASCII, dot separated, 2-5 segments, max 24 chars. The id is the primary key everywhere (menus, hotkeys, agent, telemetry, tests, docs). Examples: `LBL.RUN.AUTO`, `TRN.START`, `CAN.TOOL.RECT`, `SET.AGENT.CAP_DAY`.

Rules:
- Verb vocabulary is closed: `RUN START STOP CANCEL OPEN CLOSE SAVE SHOW HIDE TOGGLE SET GET ADD REMOVE DELETE NEXT PREV ZOOM FIT EXPORT IMPORT ACCEPT REJECT SELECT CLEAR UNDO REDO`. A verb-less id (`TRN.START`) is allowed when the domain makes the object obvious.
- IDs are append-only. A renamed feature keeps its id; a removed one is tombstoned in `commands.retired.json` (never reused). Rename of the human label is free; rename of the id is a breaking change (major bump).
- Two kinds share one namespace: **CMD** (action, has handler) and **SET** (a setting value; get/set/reset are derived, not separate ids). A third kind **TOL** (labeller MCP tools) lives in the same registry but is only callable by the labeller agent (see 2.5).

### 2.2 Domains

| Domain | Meaning | Source today |
|--------|---------|--------------|
| `PRJ` | project: new/open/save/recent/close | MainViewModel |
| `IMG` | image list: add/open folder/next/prev/delete/tag OK-NG/class | MainViewModel, DataStepViewModel, ImageClassCommand |
| `CAN` | canvas: tools, edit, zoom, brightness, shapes, class pick | CanvasViewModel |
| `LBL` | labelling: source, AI/agent/Segment-AI auto-label, accept/reject, batch | LabelStepViewModel |
| `AGT` | agent chat/session: prompt, model, effort, mode, suggest, check, forget, skills, MCP servers | AgentChat, AgentsSettingsViewModel |
| `TRN` | training | TrainStepViewModel |
| `INS` | inspect/evaluate model | InspectViewModel |
| `CAM` | live camera | LiveCameraViewModel, CaptureViewModel |
| `EXP` | export/import/convert/send to EasyVision | ExportViewModel, ImportLabelMe, ConvertYolo |
| `SET` | settings (values) | AppSettings |
| `UI` | mode, theme, layout, tabs, windows | MainViewModel, ThemeService |
| `SYS` | help, about, exit, cache | misc |
| `TOL` | labeller-agent MCP tools | EasyLabel.Ai.Mcp |

### 2.3 Versioning
- Registry file `commands.catalog.json` (generated, committed) has `catalogVersion` (semver). Each command has `since` (catalog version), optional `deprecated`/`replacedBy`, and `schemaRev` (int, bumps when args change incompatibly).
- Compatibility rule: adding optional args or new ids = minor; changing/removing an arg = `schemaRev++` and the old rev stays accepted for one minor via an adapter (LLM caches and saved macros keep working).
- A snapshot test (`CommandCatalogSnapshotTests`) fails the build when a committed id disappears or an arg changes without `schemaRev`.

### 2.4 i18n keys
Derived, not typed: caption `cmd.<ID>.name`, description `cmd.<ID>.desc`, confirm text `cmd.<ID>.confirm`, error `cmd.<ID>.err.<code>`, setting unit `set.<ID>.unit`. Resources: one `Commands.resx` / `Commands.vi.resx` pair (not the 1111-key Strings.resx). Synonyms for routing live in a third file `Synonyms.vi.json` / `Synonyms.en.json` (section 3.4) so translators and the router share them. A test asserts every id has name+desc in both languages. Inline English fallbacks in code (inventory #8) are deleted as commands migrate.

### 2.5 Descriptor (JSON schema per command)

```jsonc
{
  "id": "LBL.RUN.AGENT",
  "kind": "cmd",                    // cmd | set | tol
  "since": "1.0.0", "schemaRev": 1,
  "name": "cmd.LBL.RUN.AGENT.name", "desc": "cmd.LBL.RUN.AGENT.desc",
  "args": {                         // JSON Schema subset (type, enum, minimum, maximum, default, pattern, required)
    "type": "object", "additionalProperties": false,
    "properties": {
      "prompt": {"type": "string", "maxLength": 2000},
      "scope":  {"enum": ["current","all","remaining"], "default": "current"}
    }
  },
  "when": ["PROJECT_OPEN", "IMAGE_SELECTED", "NOT_BUSY"],   // context flags, also drives CanExecute + catalog slicing
  "cost": {"class": "C3", "unit": "usd", "estimate": "agent.cost.estimate"},  // C0..C3
  "risk": "R2",                      // R0..R4
  "undo": {"kind": "snapshot", "scope": "image"},           // none | stack | snapshot | irreversible
  "confirm": "never|ifBatch|always",
  "headless": true,                  // false = needs a dialog today (must get a path-arg variant before the agent may call it)
  "ui": {"menu": "Label/Auto label", "button": "AgentRunButton", "shortcut": "AutoLabel", "slash": "run"},
  "tags": ["label","agent","claude"],
  "aliases": ["gan nhan", "auto label", "chay agent"]
}
```

Classes:
- Cost: **C0** instant/free local (UI state, settings), **C1** local compute seconds (SAM2, Segment-AI single image), **C2** local compute minutes+ (batch auto-label, training, export), **C3** paid LLM tokens (agent run/batch/suggest/refine).
- Risk: **R0** read-only, **R1** reversible UI/view state, **R2** edits project data but undoable/backed up, **R3** costs money or long/occupies hardware (C3/C2 commands; camera live), **R4** destructive or leaves the machine (delete files, clear cache, overwrite output folder, send to EasyVision folder).
- Policy matrix (one table, in code): origin `user-direct` -> any risk, R4 shows its normal dialog; origin `agent` -> R0/R1 auto, R2 auto only with Undo available, R3 requires a cap check (`SET.AGENT.CAP_DAY`, `SET.AGENT.CAP_RUN`) then one confirm, R4 always a modal confirm showing the exact args. Over-cap or unknown arg = reject with a machine-readable error the router can retry on.
- Result envelope (every command returns it): `{ok, code, message_key, data, undoToken, cost:{usd,turns}, ms}`; this is also what the LLM sees back (short).

**Settings as commands**: one descriptor per setting, `kind:"set"`, with `type`, `enum`, `min`, `max`, `default`, `unit`, `restart` and `scope` (`app` | `project`). The three generic verbs `SET.GET`, `SET.SET`, `SET.RESET` take `{id, value}`; the agent catalog therefore lists settings as a compact table (id, type, range) instead of 86 separate tools.

**Variables that are not settings** (UI state such as current tool, zoom, selected class, batch mode, chat model) get ids in the same space with `scope:"session"` (e.g. `CAN.TOOL`, `AGT.MODE`), so "variable" and "setting" share one mechanism.

**MCP tools**: keep the tool names on the wire (Claude/Codex see `get_crop`), but declare them as `TOL.IMG.CROP` etc. with `wireName`. The registry then generates `AllowedTools`, Codex `enabled_tools`, `Describe()` and the skill tool table. Fixes inventory #2.

---------------------------------------------------------------------------------------------------

## 3. NL -> intent -> command pipeline (cost-optimised)

Two different LLM roles must not be mixed:
- **Pilot** (new): turns the user's sentence into command codes for the app. Cheap, short, strict JSON.
- **Labeller** (existing Claude Code/Codex + MCP): a heavy run that actually looks at the image. The pilot's output for "label the defects" is simply `LBL.RUN.AGENT {prompt}`; the labeller is unchanged.

### 3.1 Tiers (stop at the first tier that is confident)

| Tier | Mechanism | Cost | Handles |
|------|-----------|------|---------|
| T0 | Exact: hotkey, menu, button, `/slash`, or pasted code `LBL.RUN.AGENT {...}` | 0 | everything direct |
| T1 | Local router: normalise (lower, strip Vietnamese diacritics, unify synonyms), regex/keyword + number/unit extraction against `Synonyms.*.json`; score each command; accept if top score >= 0.8 and margin >= 0.25 | 0 tokens, <5 ms | ~60-70% of operator phrases ("tiep theo", "luu", "phong to", "train", "dung lai", "cap ngay 3 do") |
| T2 | Small model (Haiku-class via the existing Claude/Codex backend, or a local ONNX embedding for retrieval + a tiny generator) with a **sliced** catalog (top-k 6-10) and JSON-schema-constrained output `{"calls":[{"id","args"}],"clarify":null}` | ~400-700 input tokens, ~40 output | paraphrase, args in prose ("gan nhan cac vet xuoc tren tat ca anh con lai") |
| T3 | Large model (Sonnet+) with the full catalog (still compact form) only for multi-step plans, ambiguity after T2 asked for `clarify`, or the user typed `/plan` | ~2k tokens | "train xong thi export cho EasyVision roi tat" |

Escalation rules: T1 below threshold -> T2; T2 returns `clarify` or schema-invalid twice -> T3; T3 invalid -> show the 3 closest commands as buttons (never guess for R3/R4).

### 3.2 Catalog slicing (token control)
- **Compact line format** in prompts (about 25-35 tokens/command): `LBL.RUN.AGENT|prompt:str,scope:cur|all|rem|Run labelling agent|C3 R3`. Full JSON schema is NOT sent; it is used only to validate the answer. Full 40-command catalog ~1.2k tokens; all ~250 commands would be ~8k - so never send all.
- **Slice 1 - context filter**: drop commands whose `when` flags are false now (no project open -> no `CAN.*`, `TRN.*`). Typically removes 40-60%.
- **Slice 2 - domain by screen**: current workflow step (Data/Label/Train/Inspect) boosts its domain; `UI.*`/`SYS.*` always present.
- **Slice 3 - retrieval**: BM25 over name+aliases+tags (T1 already computed scores) or embedding top-k; send k=8 plus the 5 most-used in this session.
- **Settings** are one table row per id only when the utterance mentions a settings-like noun or number+unit; otherwise omitted.
- Stable-prefix prompt: system text + schema rules first (cacheable), slice last, so Anthropic prompt caching covers the fixed ~600 tokens.

### 3.3 Caching
- Utterance cache: key = `sha1(normalised text + catalogVersion + context-flags)` -> validated plan (`%LOCALAPPDATA%\EasyAI\cmd-cache.json`, LRU 500). A repeated phrase costs 0 tokens. Only plans that were **executed and not undone** are stored (feedback loop through `AgentOutcomeRecording`-style recording).
- Learn-new-synonym loop: when T2 resolves a phrase that T1 missed and the user did not undo it, append the normalised phrase to a per-user `synonyms.learned.json` (T1 handles it next time). Mirrors the existing "learned project vocabulary" and the `/forget` command (`AGT.FORGET`).
- Result cache for pure reads (R0) within a context version.

### 3.4 Vietnamese synonym table (`Synonyms.vi.json`, abridged; matching is diacritic-insensitive)

| Id | Phrases (vi, with diacritics shown once; router strips them) | en |
|----|----------------------------------------------------------------|----|
| `IMG.NEXT` | ảnh tiếp, tiếp theo, ảnh sau, qua ảnh, next | next image |
| `IMG.PREV` | ảnh trước, quay lại ảnh, lùi | previous |
| `PRJ.SAVE` | lưu, lưu dự án, save | save |
| `PRJ.OPEN.FOLDER` | mở thư mục, nạp thư mục ảnh, thêm thư mục | open folder |
| `LBL.RUN.AGENT` | gán nhãn bằng agent, nhờ AI gán nhãn, chạy agent, tự động gán nhãn theo mô tả | run agent |
| `LBL.RUN.SEG` | gán nhãn tự động, segment AI, auto label | auto label |
| `LBL.ACCEPT.ALL` | chấp nhận hết, đồng ý tất cả, lấy hết | accept all |
| `LBL.REJECT.ALL` | bỏ hết, từ chối tất cả, xoá đề xuất | reject all |
| `LBL.CANCEL` | dừng, huỷ, thôi, ngừng chạy | cancel |
| `TRN.START` | huấn luyện, train, bắt đầu train, đào tạo | start training |
| `TRN.STOP` | dừng train, ngưng huấn luyện | stop training |
| `INS.RUN.ALL` | kiểm tra tất cả ảnh, chạy model trên tất cả, đánh giá | run model on all |
| `CAN.ZOOM.IN/OUT/FIT` | phóng to, thu nhỏ, vừa khung, vừa màn hình | zoom |
| `CAN.TOOL.RECT/POLY/...` | vẽ hình chữ nhật, vẽ đa giác, công cụ box | tool |
| `CAN.UNDO` | hoàn tác, undo, quay lại bước trước | undo |
| `CAM.SNAP` | chụp ảnh, chụp một tấm, snapshot | snapshot |
| `EXP.SEND.EV` | gửi sang EasyVision, xuất cho máy | send to EasyVision |
| `SET.AGENT.CAP_DAY` | giới hạn mỗi ngày, trần chi phí ngày, ngân sách ngày | daily cap |
| `SET.AGENT.EFFORT` | mức nỗ lực, chạy kỹ hơn / nhanh hơn / tiết kiệm | effort |
| `UI.MODE` | chế độ đơn giản, chế độ nâng cao, easy mode | UI mode |

Numbers/units are parsed by T1 (`3 đô`, `3$`, `2 usd`, `50 ảnh`, `gấp đôi`), typed directly into the arg schema; the schema `minimum/maximum` then clamps or rejects. Colloquial scope words map to enums (`tất cả`=all, `còn lại`=remaining, `ảnh này/hiện tại`=current).

### 3.5 Validation and safety (always, all tiers)
1. Parse answer with the schema of that id (`additionalProperties:false`); unknown id -> reject (never fuzzy-execute).
2. Evaluate `when` against live context; fail -> tell the user why (i18n error key), do not execute.
3. Apply the policy matrix (risk x origin x caps).
4. Execute through the registry, capture `undoToken`; show a one-line receipt in chat ("LBL.RUN.AGENT scope=all - est. $0.40 - Undo").
5. Telemetry row (`cmd-log.csv`): time, id, origin (`menu|hotkey|slash|nl-t1|nl-t2|nl-t3|agent`), ok, ms, usd. This yields the data for tuning the synonym table and the T1/T2 hit rate.

---------------------------------------------------------------------------------------------------

## 4. Target structure and registry design (C#, net9)

### 4.1 Projects

```
EasyLabel/src/
  EasyLabel.Commands/                 net9.0, no WPF, no Newtonsoft dependency in the public surface
    CommandId.cs                      readonly record struct CommandId(string Value) + constants class CommandIds (generated)
    CommandDescriptor.cs              id, kind, args schema, when, cost, risk, undo, ui hints, aliases
    CommandContext.cs                 flags enum CtxFlags (PROJECT_OPEN, IMAGE_SELECTED, SHAPE_SELECTED, BUSY, CAMERA_LIVE, ...), ICommandContext
    CommandRegistry.cs                id -> descriptor + handler; IReadOnlyList<CommandDescriptor> Query(ctx)
    CommandInvoker.cs                 validate -> precondition -> policy -> run -> result envelope -> telemetry -> undo
    Settings/SettingDescriptor.cs     typed settings table (replaces SettingsAccessor row switch)
    Routing/IntentRouter.cs           T1 (normalise, synonyms, scoring); ITierModel for T2/T3 (implemented by EasyAI.Wpf)
    Routing/Synonyms*.json            embedded resources
    Catalog/CatalogWriter.cs          compact prompt lines + JSON catalog
  EasyLabel.Commands.Generators/      netstandard2.0 incremental source generator + analyzers (no runtime dependency)
  EasyLabel.Commands.Tests/           snapshot, schema, i18n completeness, router golden set (vi+en)
EasyAI.Wpf/
  Commands/                           thin adapters only
    WpfCommandHost.cs                 ICommandContext implementation (reads MainViewModel state), UI-thread dispatcher
    CmdExtension.cs                   XAML markup extension {c:Cmd LBL.RUN.AGENT} -> ICommand + Header + InputGestureText
    ShortcutBinder.cs                 ShortcutTable entry -> CommandId (replaces MainWindow switch)
    PilotService.cs                   T2/T3 calls through the existing AgentRunner/claude backend
    Handlers/                         [Command] static handlers grouped per domain (Prj, Img, Can, Lbl, Agt, Trn, Ins, Cam, Exp, Ui)
EasyLabel.Ai.Mcp/                     tools declared with [Tool("TOL.IMG.CROP", Wire="get_crop")]; registry generated Describe()/allow-list
```

Dependency direction: `EasyLabel.Commands` references nothing but BCL; `EasyAI.Wpf` references it; `EasyLabel.Ai.Mcp` references it (so the labeller tools share ids and schema). Core/SegAI services are called from handlers, not from the registry.

### 4.2 Declaring a command (attribute-based, generator-assisted)

```csharp
public static class LabelHandlers
{
    [Command("LBL.RUN.AGENT", Risk = R.R3, Cost = C.C3, When = Ctx.ProjectOpen | Ctx.ImageSelected | Ctx.NotBusy,
             Undo = UndoKind.Snapshot, Confirm = Confirm.IfBatch, Ui = "AgentRunButton", Shortcut = nameof(ShortcutId.AutoLabel))]
    public static Task<CommandResult> RunAgent(ILabelStepService svc, RunAgentArgs args, CancellationToken ct)
        => svc.RunAgentAsync(args.Prompt, args.Scope, ct);
}

public sealed record RunAgentArgs(
    [property: Describe("cmd.LBL.RUN.AGENT.prompt"), MaxLength(2000)] string? Prompt = null,
    BatchScope Scope = BatchScope.Current);          // enum -> JSON enum via generator, no hand parser
```

Generator output (compile time, so no reflection at startup and trimming-safe):
1. `CommandIds` constants (`CommandIds.LblRunAgent == "LBL.RUN.AGENT"`) - used by C#, never typed as strings.
2. `GeneratedCommandTable` : descriptors with JSON schema built from the args record (types, enums, `Range`, `MaxLength`, defaults).
3. `commands.catalog.json` (additional file, committed; snapshot tested) and the compact prompt lines.
4. Diagnostics: duplicate id (CMD001), id not matching the grammar (CMD002), missing i18n key (CMD003 warning, error in CI), handler arg type not serialisable (CMD004), `Headless=false` command exposed to agent (CMD005).
Fallback if a generator is not wanted in phase 1: the same attributes read by reflection at startup into the same descriptor types; the snapshot test is identical. The generator can replace it later without changing any call site.

Settings use the same pattern on `AppSettings` (properties get `[Setting("SET.AGENT.CAP_DAY", Min=0.01, Max=10000, Unit="usd", Scope=App)]`); the existing consts (`AgentMaxUsdPerDayMax`) become the attribute values; `SettingsAccessor`'s switch is generated.

Labeller tools: `[Tool("TOL.IMG.CROP", Wire = "get_crop")]` on the `EasyAiTools` methods; `Describe()`, `Call` switch, `AllowedTools`, Codex `enabled_tools` all come from one generated list.

### 4.3 One registry, every entry point

```
                      +--------------------+
 Menu/Button  ------> |                    |
 Hotkey (ShortcutTable)--> CommandInvoker.ExecuteAsync(id, args, origin)
 /slash (AgentSlashCommands)-> |  validate -> when -> policy -> handler -> result |--> telemetry, undo, chat receipt
 NL chat -> IntentRouter ----> |                    |
 Labeller agent (TOL.*) -----> +--------------------+  (only kind=tol ids, own allow-list)
```

- **ViewModels**: keep `[RelayCommand]` (no churn). A generated binder registers each ViewModel command into the registry by id (`[BindCommand("TRN.START")]` on the existing method, or the handler class calls `StartCommand`). During migration the registry wraps the old command; later the logic moves into a handler and the VM property forwards: `StartCommand = registry.AsICommand(CommandIds.TrnStart)`.
- **Menus/buttons**: XAML `Command="{c:Cmd TRN.START}"`; the extension supplies `CanExecute` (from `when` + busy flags, re-evaluated on `CtxChanged`), the localized header and `InputGestureText` from `ShortcutTable`. `AutomationId` = id with dots replaced (`TRN_START`) so UIA smoke tests become derivable from the catalog.
- **Hotkeys**: `ShortcutEntry` gains `CommandId`; `TryApplicationShortcut` becomes `registry.Execute(entry.CommandId, origin: Hotkey)`; the 53-reference switch disappears; user-rebinding becomes a data change. `BlocksShortcuts` (text-box focus) stays as the pre-filter.
- **Slash**: `/run`, `/clear` ... resolved through `Aliases` with `ui.slash`; `AgentSlashKind` and the switch in `TryHandleSlashCommand` are replaced by a lookup. `/help` is generated from the catalog.
- **Agent/NL**: `PilotService` renders the sliced catalog, calls the model, hands `{calls}` to the invoker with `origin: nl-tN`. The invoker is the only code path, so policy and caps cannot be bypassed by the LLM.
- **Threading**: handlers declare `Thread = Ui | Background`; invoker marshals via `IUiDispatcher` (CommunityToolkit async commands already handle re-entrancy: `NOT_BUSY` flag).

### 4.4 Undo
Reuse `EasyLabel.Core.Commands.ICommand/CommandStack` for shape edits (`undo.kind=stack`). For batch/agent runs use a project snapshot token (copy of the affected annotation JSON files, already recorded per run in `agent-runs\`), kind `snapshot`. `irreversible` commands (R4) must carry `confirm=always`.

---------------------------------------------------------------------------------------------------

## 5. Migration plan (small committable phases)

Each phase is independently shippable, behaviour-neutral unless stated, and ends with: build `EasyAI.Wpf` + `EasyLabel.Commands.Tests`, no new warnings. Per repo rule, builds/tests need explicit user authorization.

| Phase | Content | Files (approx.) | Risk | Exit check |
|-------|---------|-----------------|------|------------|
| CC-0 | This design reviewed; freeze id grammar + domain list + verb list; write `commands.retired.json` convention | docs only | none | user approves |
| CC-1 | New project `EasyLabel.Commands` (descriptors, ids, registry, invoker, result envelope, policy matrix) + tests; **no app wiring** | +1 proj, ~12 files | low | unit tests: grammar, duplicate id, policy matrix, schema validation |
| CC-2 | Reflection-based `[Command]` scan + catalog JSON writer + snapshot test; register the ~40 sample commands (section 6) by **wrapping** existing VM `ICommand`s via `WpfCommandHost` (no logic moved) | +`EasyAI.Wpf/Commands/*`, small VM attribute adds | low-med: command names must match; CanExecute parity | snapshot test; manual: menu still works |
| CC-3 | Slash commands go through registry (`AgentSlashKind` switch -> lookup); `/help` generated; add `AGT.*` ids; remove inline English fallbacks for these 8 | AgentSlashCommands, AgentChat partial | low | existing `AgentSlash*` tests keep passing |
| CC-4 | Hotkeys: `ShortcutEntry.CommandId`; replace the `ShortcutId` switch with registry call; keep `ShortcutTable` display text | MainWindow.xaml.cs, Canvas key handler, ShortcutTable | **medium**: key routing, text-box blocking, focus | UIA smoke of the 51 shortcuts (generated list) |
| CC-5 | Settings schema: `[Setting]` attributes on AppSettings, generated accessor; `SET.GET/SET/RESET`; Settings dialog rows built from descriptors; string "enums" -> real enums with one parser (`effort`, `precision`, `source`, `mode`) incl. legacy values | AppSettings, SettingsAccessor, SettingsModel, AgentRunOptions | **medium**: persisted file compatibility (SchemaVersion bump + legacy read test) | load old settings.json fixtures |
| CC-6 | MCP tools: `[Tool]` attributes; generate `Describe()`, allow-lists (Claude/Codex/suggest) from registry; test that wire names are unchanged | Ai.Mcp, AgentCommandLine | low-med | `ToolTests`, `AgentRunContractTests`, AgentCommandLine tests |
| CC-7 | XAML `{c:Cmd}` for menus/buttons domain by domain (UI, PRJ, IMG first; then CAN, LBL, TRN, INS, CAM, EXP); AutomationIds derived; remove `NotYet` | xaml per domain | medium: bindings, DataContext, visual regression | UIA smoke per domain; screenshots (rule 0.2a) |
| CC-8 | Router T0/T1 + synonyms vi/en + golden-set tests (>=200 phrases); chat box tries T1 before sending text to the labeller; receipts + undo | Routing/*, AgentChat | medium: false positives on R3 | golden set; policy: R3/R4 never from T1 without confirm |
| CC-9 | Pilot T2/T3 (`PilotService`), catalog slicing, utterance cache, telemetry csv; caps wired to `SET.AGENT.CAP_*` | PilotService, cache | medium: cost leak, prompt injection through image names/notes | cost test with fake model; injection tests (args are schema-validated, ids closed) |
| CC-10 | Headless variants for dialog commands (`PRJ.OPEN.FOLDER {path}`, `EXP.RUN {format,dir}` ...); mark `Headless=true`; expose to agent | MainViewModel, ExportViewModel | medium: path safety (limit to project/output folders) | path-sandbox tests |
| CC-11 | Generator replaces reflection; analyzers CMD001-005 as errors; god-class split begins as handlers absorb logic (LabelStepViewModel first) | generators proj | medium: tooling | clean build; snapshot unchanged |

Cross-cutting risks and mitigations:
- **Scope creep into god-class refactor**: CC-2 deliberately wraps instead of moves; moving logic is optional per phase.
- **Id churn**: ids are append-only + snapshot test; review each new id like a public API.
- **Prompt injection**: untrusted text (image file names, notes, project memory) is never put in the catalog; args are schema-validated; the pilot cannot name an id outside the slice or the registry; R3/R4 gated by policy not by the model.
- **Cost leak**: T2/T3 count against `SET.AGENT.CAP_DAY`; telemetry shows $ per tier; T2 uses the cheapest model by default (`haiku`), configurable.
- **Vietnamese quality**: synonyms are data, tested by a golden set; learned synonyms are per user and removable (`AGT.FORGET`).
- **Behaviour parity** (CanExecute, focus, busy flags) is the main regression surface in CC-4/CC-7; do them one domain at a time.
- Repo rules: no new static/singleton state at the UI layer (registry is DI-held, built in `App` start-up); new files go in sub-folders; commits one phase each; UI changes must use the production custom controls and be checked at supported sizes/scales.

---------------------------------------------------------------------------------------------------

## 6. Sample catalog (~44 real commands, derived from the code)

Legend: Src = current implementation. Cost/Risk per 2.5. `H`=headless today (Y/N). When flags: P=PROJECT_OPEN, I=IMAGE_SELECTED, S=SHAPE_SELECTED, N=NOT_BUSY, B=BUSY, C=CAMERA_LIVE. "(assumed)" = signature seen, body not read.

| ID | Args | Src | When | Cost | Risk | Undo | H |
|----|------|-----|------|------|------|------|---|
| `PRJ.NEW` | `{name,location}` | MainViewModel.NewProjectAsync | - | C0 | R1 | none | N (dialog) |
| `PRJ.OPEN.FILE` | `{path}` | OpenProjectFileDialogAsync | - | C0 | R1 | none | N |
| `PRJ.OPEN.FOLDER` | `{path}` | OpenFolderAsync | - | C1 | R1 | none | N |
| `PRJ.OPEN.RECENT` | `{index}` | OpenRecentAsync | - | C0 | R1 | none | Y |
| `PRJ.SAVE` | - | SaveProject / ShortcutId.Save | P | C0 | R2 | none | Y |
| `PRJ.SAVE.AS` | `{path}` | SaveProjectAsAsync | P | C0 | R2 | none | N |
| `IMG.NEXT` | - | NextImage / ShortcutId.NextImage | P | C0 | R0 | none | Y |
| `IMG.PREV` | - | PreviousImage | P | C0 | R0 | none | Y |
| `IMG.ADD` | `{paths[]}` | DataStepViewModel.AddImagesAsync | P | C1 | R2 | snapshot | N |
| `IMG.DELETE` | `{scope:image\|annotation}` | DeleteImageAsync / DeleteAnnotationFilesAsync | P,I | C0 | **R4** | irreversible | Y |
| `IMG.TAG` | `{tag:OK\|NG}` | ShortcutId.TagOk/TagNg (assumed) | P,I | C0 | R2 | stack | Y |
| `CAN.TOOL` | `{tool:select\|polygon\|rect\|rot\|circle\|point\|line\|linestrip\|aipoints\|aibox}` | CanvasViewModel.SelectTool(CanvasTool) | P,I | C0 | R1 | none | Y |
| `CAN.UNDO` / `CAN.REDO` | - | Undo / Redo | P | C0 | R1 | - | Y |
| `CAN.SHAPE.DELETE` | - | Delete | P,I,S | C0 | R2 | stack | Y |
| `CAN.SHAPE.MERGE` | - | MergeShapes | P,I,S | C0 | R2 | stack | Y |
| `CAN.SHAPE.CONVERT` | `{to:polygon\|rectangle\|rotated\|circle\|point}` | ConvertShape | P,I,S | C0 | R2 | stack | Y |
| `CAN.CLASS.SET` | `{name}` or `{index}` | SetClass / SetClassIndex | P,I | C0 | R1 | none | Y |
| `CAN.VIEW.ZOOM` | `{mode:in\|out\|fit\|actual\|width}` | ZoomIn/ZoomOut/Fit/ActualSize/FitWidth | P,I | C0 | R1 | none | Y |
| `CAN.SHAPES.TOGGLE` | `{visible?}` | ToggleShapes / HideShapes / ShowShapes | P,I | C0 | R1 | none | Y |
| `LBL.SOURCE.SET` | `{source:segment\|ai\|agent}` | LabelStepViewModel.SetSource | P | C0 | R1 | none | Y |
| `LBL.RUN.AGENT` | `{prompt?,scope:current\|all\|remaining}` | RunAgentAsync (current) / RunBatchAsync(mode) | P,I,N | C3 | R3 | snapshot | Y |
| `LBL.RUN.SEG` | `{scope:current\|all}` | AutoLabelCurrentAsync / AutoLabelAllAsync | P,I,N | C2 | R3 | snapshot | Y |
| `LBL.RUN.AI` | `{model:sam2\|yolo-world}` | RunAiSourceAsync + SetAiModel | P,I,N | C1 | R2 | stack | Y |
| `LBL.CANCEL` | - | CancelRun / AgentSlash /cancel | B | C0 | R1 | - | Y |
| `LBL.ACCEPT` | `{which:all\|selected}` | AcceptAll / AcceptSelected | P,I | C0 | R2 | stack | Y |
| `LBL.REJECT.ALL` | - | RejectAll | P,I | C0 | R2 | stack | Y |
| `AGT.PROMPT.SUGGEST` | `{count?}` | SuggestPromptAsync (`/suggest`) | P,I,N | C3 | R3 | none | Y |
| `AGT.CHECK` | - | RunCheckAsync (`/check`) | P,I,N | C3 | R3 | none | Y |
| `AGT.MODEL.SET` | `{model:haiku\|sonnet\|opus\|name}` | `/model`, AppSettings.AgentModelClaude | - | C0 | R1 | - | Y |
| `AGT.MODE.SET` | `{mode:current\|all\|remaining}` | `/mode`, AgentBatchMode | - | C0 | R1 | - | Y |
| `AGT.FORGET` | - | `/forget` (learned vocabulary) | P | C0 | R2 | irreversible | Y |
| `TRN.RECOMMEND` | - | UseRecommended | P | C0 | R1 | - | Y |
| `TRN.KIND.SET` | `{kind}` | SetKind(string) | P | C0 | R1 | - | Y |
| `TRN.QUALITY.SET` | `{quality}` | SetInstanceQuality(string) | P | C0 | R1 | - | Y |
| `TRN.START` | `{}` (uses current setup) | TrainStepViewModel.StartAsync / MainViewModel.EasyTrainAsync | P,N | C2 | R3 | none | Y |
| `TRN.STOP` | - | Stop | B | C0 | R1 | - | Y |
| `TRN.USE.MODEL` | - | UseModelAsync | P | C0 | R2 | snapshot | Y |
| `INS.RUN` | `{scope:current\|all}` | RunCurrentAsync / RunAllAsync | P,N | C2 | R2 | none | Y |
| `INS.EXPORT.CSV` | `{path}` | ExportCsv | P | C0 | R2 | none | N |
| `CAM.LIVE` | `{on:bool}` | StartLive / StopLive | - | C0 | R3 | - | Y |
| `CAM.SNAP` | - | SnapshotAsync | C | C0 | R2 | stack | Y |
| `EXP.RUN` | `{format:yolo\|voc\|coco\|easyvision\|labelme,dir}` | ExportAsync / ExportLabelMeAsync | P | C2 | R4 (writes files) | none | N |
| `EXP.SEND.EV` | - | SendToEasyVisionAsync | P | C1 | **R4** (writes to EasyVision model folder) | snapshot | Y |
| `UI.MODE.SET` | `{mode:simple\|advanced}` | SetUiMode | - | C0 | R1 | - | Y |
| `UI.TAB.SHOW` | `{tab:inspect\|label\|train}` | ShowInspectTab / ShowLabelTab / ShowTrainTab | P | C0 | R1 | - | Y |
| `UI.LAYOUT.RESET` | - | ResetLayout | - | C0 | R1 | - | Y |
| `SYS.SETTINGS.OPEN` | `{page?}` | ShowSettings / ShortcutId.Settings | - | C0 | R0 | - | Y |
| `SYS.CACHE.CLEAR` | - | SettingsViewModel.ClearCacheAsync | - | C0 | **R4** | irreversible | Y |
| `SET.GET` / `SET.SET` / `SET.RESET` | `{id,value}` | generic over AppSettings | - | C0 | R1 (R3 for cap ids raised) | stack | Y |

Representative **settings ids** (all via `SET.*`, derived from `AppSettings`):

| ID | Type / range | AppSettings member |
|----|--------------|--------------------|
| `SET.AGENT.CAP_DAY` | number 0.01..10000 usd, default 5 | AgentMaxUsdPerDay |
| `SET.AGENT.CAP_RUN` | number usd, default 1 | AgentMaxCostUsd |
| `SET.AGENT.MAX_IMAGES` | int 1..1000, default 50 | AgentMaxImages |
| `SET.AGENT.WORKERS` | int 1..32 (clamped to machine), default 1 | AgentBatchWorkers (AgentSpeedRules) |
| `SET.AGENT.EFFORT` | enum low\|medium\|high\|xhigh\|max, default medium | AgentEffort |
| `SET.AGENT.PRECISION` | enum easy\|normal\|hard | AgentPrecision |
| `SET.AGENT.BACKEND` | enum claude\|codex | AgentBackend |
| `SET.AGENT.LOCAL_FIRST` | bool, default true | AgentRouteLocalFirst |
| `SET.AGENT.SAM2_REFINE` | bool, default true | AgentBatchSam2Refine |
| `SET.CAN.OUT_OF_BOUNDS` | bool, default true | AllowOutOfBounds |
| `SET.CAN.LABEL_DIALOG` | bool, default true | ShowLabelDialog |
| `SET.CAN.COLOR_MODE` | enum auto\|uniform\|label | ShapeColorMode |
| `SET.PRJ.AUTOSAVE` | bool, default true | AutoSave |
| `SET.EXP.EMBED_IMAGE` | bool, default false | EmbedImageData |
| `SET.UI.THEME` | enum system\|light\|dark | Theme |
| `SET.UI.LANG` | enum vi\|en (restart) | UiCulture |
| `SET.TRN.ALLOW_CPU` | bool, default false | UltraAllowCpu |
| `SET.CAM.SOURCE` | enum files\|cameraMvs | ImageSource |

Representative **labeller tools** (kind=tol, wire names unchanged): `TOL.IMG.INFO`=get_image_info, `TOL.IMG.CROP`=get_crop, `TOL.IMG.GRID`=grid_crop, `TOL.IMG.REF`=get_reference, `TOL.SHP.LIST`=list_shapes, `TOL.SHP.PROPOSE`=propose_shapes, `TOL.SHP.OVERLAY`=render_overlay, `TOL.SHP.MEASURE`=measure, `TOL.AI.SAM2`=sam2_segment, `TOL.MEM.READ`=memory_read, `TOL.MEM.ADD`=memory_add. `TOL.*` ids never appear in the pilot's catalog, and `CMD`/`SET` ids never appear in the labeller's allow-list.

### Example round trip
User (vi): "gán nhãn tất cả ảnh còn lại bằng agent, tối đa 2 đô"
- T1: `gán nhãn`+`agent` -> `LBL.RUN.AGENT` (score .86), `tất cả ... còn lại` -> scope `remaining`, `2 đô` -> arg for cap; two intents -> T1 emits `SET.AGENT.CAP_RUN {value:2}` + `LBL.RUN.AGENT {scope:"remaining"}`.
- Policy: `LBL.RUN.AGENT` is R3/C3 and batch -> one confirm showing "remaining: 37 images, est. $1.20 (cap $2)". On OK the invoker runs both, logs origin `nl-t1`, tokens spent by the pilot: 0.

---------------------------------------------------------------------------------------------------

## 7. Addendum: "Online" run mode (realtime detect, < 50 ms per 5 MP image, 1 Gbps LAN)

All timings below are engineering ESTIMATES from typical figures (GigE payload ~117 MB/s, libjpeg-turbo ~400-500 MP/s per core). Nothing was measured. They must be replaced by measurements on the target PC (memory note: production hardware is a 2-core i7-7600U; 20 ms inference on it is unproven).

### 7.1 Modes and per-mode gating of commands

Three run modes, one registry value `UI.RUNMODE`: `simple` (guided), `advanced` (full UI), `online` (runtime). Today `UiMode` is "simple"|"advanced" (`AppSettings.UiMode`, `EasyModeRules.ResolveMode`); Online adds a third value and, more importantly, a new context flag set. Gating is data, not code: each descriptor has `modes` (default simple+advanced) and the invoker rejects a command whose `modes` excludes the current mode (error `MODE_BLOCKED`, i18n `cmd.err.mode`). The pilot catalog slice (3.2) filters by mode first, so in Online the LLM never even sees editing commands.

| Mode | Allowed | Blocked |
|------|---------|---------|
| simple / advanced (Offline) | everything existing; `ONL.STATUS`, `ONL.SET.*`, `ONL.RECIPE.*` for setup | `ONL.START` |
| online | `ONL.*`, `CAM.*` (live only), `INS.RUN` (read), `SET.GET`, `UI.RUNMODE.SET`, `SYS.*`, `AGT.*` setup/read-only | `CAN.*` editing, `LBL.RUN.*`, `TRN.START`, `IMG.DELETE`, `PRJ.NEW/OPEN`, `EXP.RUN`, `SYS.CACHE.CLEAR`, every R3/R4 except `ONL.STOP` |

While Online is RUNNING the context flag `ONLINE_RUNNING` is set and every command with `risk >= R2` is blocked unless its `modes` lists `online-running` explicitly (only `ONL.STOP`, `ONL.RECIPE.SWAP`, `ONL.THRESHOLD.SET`). `UI.RUNMODE.SET` (R1) is refused while `BUSY` or `ONLINE_RUNNING`.

New ids:

| ID | Args | Cost | Risk | Notes |
|----|------|------|------|-------|
| `UI.RUNMODE.SET` | `{mode:simple\|advanced\|online}` | C0 | R1 | extends `UI.MODE.SET` |
| `ONL.START` | `{recipe?}` | C0 (C1 warm-up) | R3 | loads frozen recipe + model, opens endpoint, warm-up N frames; refuses if recipe missing/invalid/hash mismatch; user confirm every time |
| `ONL.STOP` | - | C0 | R1 | drain queue, close endpoint; allowed always |
| `ONL.STATUS` | - | C0 | R0 | state, fps, p50/p95/p99 ms per stage, dropped frames, queue depth, recipe id+hash, last error |
| `ONL.SET.ENDPOINT` | `{kind:local\|tcp\|http\|grpc, host, port, auth?}` | C0 | R3 | opens a socket; LAN allow-list, no 0.0.0.0 without confirm |
| `ONL.SET.SOURCE` | `{kind:camera\|folder\|socket, id}` | C0 | R2 | reuses `CAM.*` source ids |
| `ONL.SET.BUDGET` | `{ms:int 10..500}` | C0 | R1 | target latency; overflow triggers overload policy |
| `ONL.SET.OVERLOAD` | `{policy:drop-oldest\|drop-newest\|skip-nth}` | C0 | R1 | |
| `ONL.SET.WIRE` | `{codec:jpeg\|png\|raw, quality?, roi?, scale?}` | C0 | R1 | see 7.2 |
| `ONL.THRESHOLD.SET` | `{class?, score, nms}` | C0 | R2 | hot-settable; creates a derived recipe version |
| `ONL.RECIPE.FREEZE` | `{from:run\|current, name}` | C0 | R2 | see 7.4 |
| `ONL.RECIPE.LOAD / SWAP / LIST` | `{id}` | C0 | R2 | SWAP is atomic between frames |
| `ONL.LOG.SET` | `{ngImages:bool, dir, maxGB}` | C0 | R2 | NG frame saving is an async queue, never in the hot path |

Settings (`SET.ONL.*`): `BUDGET_MS` (int, default 50), `QUEUE_DEPTH` (1..8, default 2), `WIRE_CODEC`, `SCORE`, `NMS`. The labeller agent (`TOL.*`) has no Online tool. The pilot may call only `ONL.STOP`, `ONL.STATUS`, `ONL.THRESHOLD.SET` while running.

### 7.2 Latency budget, 5 MP (2592 x 1944 = 5.04 MP) over 1 Gbps

Wire: 117 MB/s (~940 Mbit/s payload), ~1 ms framing. Return = result JSON of a few KB, ~0.5 ms.

Payload sizes (typical industrial surface images; content dependent):

| Format | Size | Transfer | Encode (client, 1 core) | Decode (server) |
|--------|------|----------|-------------------------|-----------------|
| Raw Mono8 | 5.04 MB | 43 ms | 0 | 0 |
| Raw RGB24 | 15.1 MB | 129 ms | 0 | 0 |
| JPEG q90 mono | ~1.2-1.8 MB | 10-15 ms | 8-12 ms | 8-12 ms |
| JPEG q90 RGB | ~2-3 MB | 17-26 ms | 12-18 ms | 12-18 ms |
| PNG mono (level 1) | ~2.5-3.5 MB | 21-30 ms | 40-80 ms | 25-40 ms |
| PNG RGB | ~6-9 MB | 51-77 ms | 100+ ms | 50-80 ms |

Serial latency of one frame (nothing overlapped):

| Stage (ms) | Raw Mono8 | JPEG mono | JPEG RGB | PNG mono |
|------------|-----------|-----------|----------|----------|
| Encode | 0 | 10 | 15 | 60 |
| Transfer | 43 | 13 | 22 | 25 |
| Decode | 0 | 10 | 15 | 30 |
| Preprocess (resize/letterbox, normalise) | 3 | 3 | 4 | 3 |
| Inference (small det/seg net, 15-25 ms assumed) | 20 | 20 | 20 | 20 |
| Postprocess (NMS, mask->polygon, position) | 3 | 3 | 3 | 3 |
| Return | 0.5 | 0.5 | 0.5 | 0.5 |
| **Total** | **70** | **60** | **80** | **142** |
| Fits 50 ms serial | no | no (10 over) | no | no |

What fits 50 ms:
1. **Pipelining** (encode/transfer of frame N+1 overlaps decode/inference of N): 50 ms is then per-frame THROUGHPUT, bottleneck = slowest stage. JPEG mono: max(10, 13, 10, 26) = 26 ms/frame (~38 fps), latency still ~60 ms. Raw mono: wire-bound 43 ms/frame (23 fps), latency 70 ms. Raw RGB never fits (129 ms).
2. **If < 50 ms means end-to-end latency**: only with a reduced frame (ROI crop or 2x2 binning to 1.26 MP: raw 1.26 MB -> 11 ms; total about 11 + 1 + 15 + 3 = 30 ms) or inference <= 15 ms plus encode hidden in sensor readout. Binning/ROI costs resolution: a requirement decision.
3. **Same machine** (`ONL.SET.ENDPOINT kind=local`): no wire/codec; grab -> preprocess 3 -> inference 20 -> post 3 = about 26 ms. Only path with real headroom; LAN server is the fallback.
4. A GigE camera feeding the inference PC directly already pays the same ~43 ms Mono8 transfer outside the app.
5. Open question for the user: latency or throughput, Mono8 or colour, and the resolution defects actually need (can the net run on a downscaled image?).

### 7.3 What may be LLM-driven and what may not

An LLM round trip is 1-30 s; the hot path has 0.05 s.

| Layer | LLM allowed? | Executed by |
|-------|--------------|-------------|
| NL -> command code (pilot T2/T3) | yes, setup time only | `PilotService` |
| Labelling (Claude Code/Codex + MCP `TOL.*`) | yes, offline authoring on stored images | agent run |
| Prompt -> detection config (classes, shape types, size/position/score rules, ROI, thresholds) | yes, once at freeze time (7.4) | agent run + `ONL.RECIPE.FREEZE` |
| Select/start/stop Online, change thresholds | yes via pilot, as validated commands | registry |
| Per frame: grab, decode, preprocess, inference, post, rules, NG verdict, result send, reject signal | **never** | compiled code: local native/ONNX/SegAI model or LAN inference server; `RecipeEvaluator` for the verdict |
| Review of saved NG frames, tuning suggestions | yes, asynchronous, off the hot path | agent; output is a proposed NEW recipe version |

Enforced mechanically: the hot-path assembly `EasyLabel.Online` has no reference to `AgentRunner`, `PilotService`, `EasyLabel.Ai.Mcp` or any LLM client (project references + analyzer), and the invoker rejects `AGT.*` run commands while `ONLINE_RUNNING`.

### 7.4 Freezing the Agent's output into a deterministic runtime recipe

The agent's work is converted ONCE into data; at run time only data + a model execute.

`ONL.RECIPE.FREEZE`:
1. Inputs: accepted shapes (agent/SAM2 proposals after the user's Accept, via `ProposalStore` results), the agent prompt with effort/precision (provenance), the class table, and the model trained or chosen offline from that data (Segment AI `.segai` or exported ONNX). The LLM is never the model.
2. A one-time agent run compiles the prompt into a rule set, validated by schema, e.g. `{"classes":[{"name":"scratch","minScore":0.45,"minAreaPx":120,"maxCount":0,"roi":[x,y,w,h]}],"position":{"mode":"bbox|centroid|polygon","units":"px|mm","calib":"id"},"nms":0.5,"verdict":{"ng_if":"any(scratch) or count(dent)>2"}}`. The verdict is a closed mini-language (and/or/not/any/count/area/compare) parsed by our code: no scripting, no free text.
3. Output folder `recipes/<id>/`: `recipe.json` (rules, preprocess params, wire params, thresholds, class map, `schemaRev`, catalog version), `model.segai|onnx` (+ sha256), `calib.json` (px->mm, optional), `provenance.json` (prompt, model/effort, agent run id, dataset hash, author, date). Recipe id = content-hash prefix of all of it.
4. Determinism: fixed interpolation and normalisation constants, fixed seeds, fixed thresholds; same image + recipe = same result (golden-image tests). Nothing in the recipe is regenerated at run time.
5. Load gate (`ONL.START`, `ONL.RECIPE.LOAD`): verify hashes and schema, model input size vs class map, run warm-up and a self-test on stored golden images; otherwise refuse with `RECIPE_INVALID`.
6. Change control: any edit (threshold, re-run prompt, new model) produces a NEW recipe id; `ONL.RECIPE.SWAP` is atomic between frames; rollback = swap back. Hot threshold changes log a derived version, so every result row names its exact recipe id.
7. Result row: frame id, recipe id, verdict, detections (class, score, position), per-stage ms. No LLM output in it.

### 7.5 Verification owed before promising 50 ms
- Measure on the target PC: real JPEG/PNG sizes and encode/decode ms on real 5 MP images; inference ms at the production input size; end-to-end on loopback and a real 1 Gbps link; p50/p95/p99 under camera and ladder load (memory notes record camera CPU starvation on the 2-core PC).
- Define the metric: grab-to-result latency vs throughput; judge on p99, not mean.
- If Online drives a reject output it is safety-adjacent: a passing build is not production evidence.

### 7.6 Structure and migration additions
- New project `EasyLabel.Online` (net9, no WPF, no LLM references): `FrameSource`, `WireCodec`, `RecipeLoader`, `RecipeEvaluator`, `InferenceHost` (local or LAN), `OnlineRunner` (bounded queue, overload policy, stage timers), `OnlineMetrics`. `EasyAI.Wpf` hosts only a status view and calls `ONL.*` through the registry.
- Registry additions: descriptor field `modes` (+ `online-running`), context flags `ONLINE_RUNNING`, `ENDPOINT_OPEN`, `RECIPE_LOADED`.
- Phases after CC-5: CC-12 `modes` field + `UI.RUNMODE.SET` + gating tests (no Online code); CC-13 `EasyLabel.Online` skeleton + recipe schema + freeze/load with golden tests (offline, no network); CC-14 local runner + metrics + measurement harness (produces the real numbers for 7.2); CC-15 LAN endpoint (`ONL.SET.ENDPOINT/WIRE`), overload policy, status UI; CC-16 pilot support for ONL setup commands.
- Risks: false confidence in the estimates (do CC-14 before any promise); an accidental LLM dependency in the hot path (project-reference guard); open endpoint security (LAN allow-list, auth token, no remote file paths in args).
