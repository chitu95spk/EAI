# EasyVision Solution — Structure Review & Target Architecture (Codex Execution Plan)

> Companion to `docs/architecture/beeglobal-decoupling-plan.md` (slices S017–S031).
> This document covers the **whole solution**: BeeCore, BeeInterface, and the project graph.
> Map integration: continue `.structure-map.md` (prefix `S`) from **S032**.
> Same global rules apply (CLAUDE.md §0/§12): one slice = one commit, `git mv` to keep history,
> build-to-file + grep, warnings ≤ baseline, no `git add .`, append `CODEX_HISTORY.md`.
> **Precondition:** worktree clean for the files a slice touches (tree currently has ~326 dirty files).
> User decisions baked in: (1) **extract Tools into dedicated projects**; (2) **move EasyScreen to
> its own solution**.

---

## 1. Current state (measured 2026-09-03)

### 1.1 Managed dependency graph

```
BeeIOContracts (leaf) ── BeeImageProcessing ── BeeHmi ── PLC_Communication (Hsl, vendor)
        ▲                        ▲                            ▲
   BeeLadder, BeeEcat, BeeEcatSync, BeeProgramSync           │
        ▲                                                     │
   BeeGlobal ───────────────────────────────────────────────┘  (drags PLC vendor + EtherCAT down low)
        ▲
   BeeInterfaceControls,  BeeCore
        ▲
   BeeLadderEditor,  BeeInterface ◄── references 10 projects (god UI)
        ▲
   BeeUi ── BeeMain (exe)
```

### 1.2 Size / bloat

| Project | LOC | Files | Role | Symptom |
| --- | ---: | ---: | --- | --- |
| **BeeInterface** | 102,750 | 245 | WinForms UI + runtime orchestration | **god-project**, refs 10 projects; `Tool/` 38k, `Group/` 21k, `PLC/` 9.8k |
| **BeeCore** | 85,941 | 178 | vision domain | `Unit/` 37k (tool engines), `Func/` 22k (camera/draw), `Algorithm/` 15k |
| **PLC_Communication** | 58,318 | 621 | **HslCommunication vendored** | third-party lib in tree; referenced *directly by BeeGlobal* |
| BeeGlobal | 29,125 | 52 | global state + mixed layers | god-object `Global` (~6,600 external calls); see companion plan |
| BeeLadderEditor | 20,957 | 23 | ladder editor UI | ok |
| BeeInterfaceControls | 9,485 | 15 | UI control kit | ok (but refs BeeGlobal) |
| BeeLadder | 7,558 | 19 | ladder core | ok |
| BeeUi | 4,603 | 21 | secondary camera UI | **refs BeeInterface (102k)** — inverted layering |
| BeeHmi / BeeEcat / BeeImageProcessing / BeeIOContracts / BeeEcatSync / BeeProgramSync | ≤3k each | | leaf/infra | ok |

### 1.3 Core problems

1. **BeeInterface is a monolith** (102k) referencing everything → any UI edit rebuilds the world;
   feature UIs (tools, runtime view, PLC config) are not separable.
2. **Tools are split across two god-projects** with no contract between them: engine in
   `BeeCore/Unit/*`, UI in `BeeInterface/Tool/*`, glued by the global `Common.PropetyTools`
   registry (`BeeCore/Common.cs`) and `Global.EditTool/ToolSettings` (`dynamic`). Tools cannot be
   developed or built in isolation.
3. **BeeGlobal → PLC_Communication**: a 58k vendor lib is dragged into the lowest shared layer.
4. **`Global` god-object** couples every feature (see companion plan).
5. **Off-axis projects**: `EasyScreen.*` (a different product) and `BeeUi` (inverted dependency)
   inflate the solution's build graph.

---

## 2. Target "standard" layered architecture

Strict layers; a project may only reference **downward**. No layer may reference `Global` state
except through interfaces defined in **L0 Contracts**.

```
L0  Contracts      BeeIOContracts        enums, POCO models, tool/hardware/service INTERFACES
L1  Vendor/Native  PLC_Communication (Hsl), EtherCatCommunication, BeeCV, PylonCli,
                   Pattern, BeeNativeOnnx/RCNN/SegAI           (isolated; only wrappers above)
L2  Shared infra   BeeGlobal (slimmed state facade), BeeImageProcessing
L3  Hardware       BeeEcat (+ EcatSync, Ladder, ProgramSync) — owns PLC_Communication & EtherCAT
L4  Domain         BeeCore (vision algorithms, camera, data) + BeeCore.Tools (tool ENGINES)
L5  UI kit         BeeInterfaceControls, BeeHmi
L6  Feature UI     BeeToolsUI (tool forms) · BeeRuntimeUI (Group/View runtime) ·
                   BeeInterface (shell/settings, slimmed)
L7  Composition    BeeMain (exe)          [BeeUi folded in or reduced]
────────────────────────────────────────────────────────────────────────
Separate solution  EasyScreen.sln : EasyScreen(.XP)(.Core), EasyScreen.LicenseNative
```

Guiding rules:
- **PLC vendor only reachable from L3.** BeeGlobal must NOT reference PLC_Communication.
- **Tools talk through L0 interfaces**, never by cross-referencing each other or reaching into
  `Global`. The registry (`Common.PropetyTools`) becomes a service behind an interface.
- **UI depends on domain, never the reverse.** No L4/L3/L2 project may reference an L6 project.

---

## 3. Workstreams (each = a group of `.structure-map.md` slices, S032+)

Order: **W1 (contracts/slim) → W2 (tools) → W3 (UI split) → W4 (housekeeping)**. W4 is independent
and may run anytime. W1 reuses the companion plan (S017–S031); do not duplicate those slices.

### W1 — Slim BeeGlobal & isolate the PLC vendor  *(builds on companion plan)*
- **S032 · high · break `BeeGlobal → PLC_Communication`** — find every PLC_Communication use in
  BeeGlobal (`Protocol/*`, `Comunication.cs`), move it behind the L3 hardware layer (BeeEcat) or a
  thin `IPlcTransport` interface in BeeIOContracts. Remove the `PLC_Communication` `ProjectReference`
  from `BeeGlobal.csproj`. This is the single biggest de-layering win and is a precondition for
  Protocol moving to BeeEcat (companion S031).
- Companion S017–S031 (enum down, hardware/protocol up, Global strangler) are part of W1.

### W2 — Tool decoupling (dedicated Tool projects)  *(user-chosen)*
Target: `ITool`/`IToolRegistry` in L0, engines in `BeeCore.Tools` (L4), forms in `BeeToolsUI` (L6).

- **S033 · medium · define contracts** — add `IToolEngine`, `IToolParameters`, `IToolResult`,
  `IToolRegistry` to **BeeIOContracts**, modeled on the existing `PropetyTool` surface
  (`BeeCore/PropetyTool.cs` — note it is `ISerializable`; **do not change its namespace/assembly**
  without a sample-project load test, per §5). Keep `PropetyTool` where it is for now; the interface
  wraps it.
- **S034 · medium · registry behind interface** — make `Common.PropetyTools`/`TryGetTool`
  (`BeeCore/Common.cs`) implement `IToolRegistry`; replace `Global.EditTool`/`ToolSettings`
  (`dynamic`) with the L0 interfaces (this is companion S029 for the tool slice). Callers keep using
  `Common.TryGetTool` (guard `check_propety_tools.sh` still applies).
- **S035 · high · extract `BeeCore.Tools`** — `git mv BeeCore/Unit/*` (tool engines: Yolo, Patterns,
  MultiOnnx, MultiPattern, CheckMissing, …) into a new `BeeCore.Tools` project referencing
  BeeCore + BeeIOContracts. Do it **one tool family per commit** to keep diffs reviewable. Engines
  reference each other only via L0 interfaces afterwards.
- **S036 · high · extract `BeeToolsUI`** — `git mv BeeInterface/Tool/*` into a new `BeeToolsUI`
  project (L6) referencing BeeInterfaceControls + BeeCore.Tools + BeeIOContracts. One tool form
  family per commit. `BeeInterface` then references `BeeToolsUI` instead of hosting the forms.
- After W2: editing/adding a tool touches only its L0 interface + its engine (BeeCore.Tools) + its
  form (BeeToolsUI); the rest of the solution does not rebuild.

### W3 — Slim BeeInterface (split remaining feature UIs)
- **S037 · high · extract `BeeRuntimeUI`** — move `BeeInterface/Group/` (21k, runtime `View.cs`
  live inspection) into its own L6 project. This is the second-largest chunk after Tools.
- **S038 · medium · extract PLC config UI** — move `BeeInterface/PLC/` (9.8k) into `BeeToolsUI`'s
  sibling or a `BeeHardwareUI` project (L6), so PLC UI sits above the L3 hardware layer cleanly.
- **S039 · low · residual BeeInterface** — what remains is the app shell + settings + steps +
  calibration (~25k). Keep as `BeeInterface`. Verify it no longer transitively forces tool/runtime
  rebuilds.

### W4 — Housekeeping (independent)  *(user-chosen: split EasyScreen)*
- **S040 · medium · EasyScreen → own solution** — create `EasyScreen.sln` containing
  `EasyScreen`, `EasyScreen.XP`, `EasyScreen(.XP).Core`, `EasyScreen.LicenseNative`. Remove those
  projects from `EasyVision.sln`. They already only reference `PLC_Communication`/their own `Core`,
  so the cut is clean. (Keep `PLC_Communication` shared via a relative path or a shared build.)
- **S041 · medium · fix BeeUi inverted dependency** — `BeeUi` (4.6k) currently references
  `BeeInterface`. Decide per audit: if `BeeUi` is the live secondary camera UI, cut the
  `BeeInterface` ref (move the few shared bits down); if it is legacy/dead, fold its live forms into
  `BeeInterface` and drop the project. Confirm callers in `BeeMain` first.
- **S042 · low · consolidate tiny hardware libs (optional)** — evaluate merging `BeeEcatSync`,
  `BeeProgramSync`, `BeeLadder` under the L3 `BeeEcat` umbrella if their split no longer earns its
  keep. Only if it reduces references without hurting the ECatIO/ECAT_HMI standalone apps that
  consume them.

---

## 4. Sequencing summary

```
W1 (S017–S032)  slim BeeGlobal, break PLC-vendor coupling      ─┐ enables Protocol→BeeEcat
W2 (S033–S036)  tool contracts → BeeCore.Tools → BeeToolsUI     ─┤ enables tool isolation
W3 (S037–S039)  BeeRuntimeUI, PLC UI, residual BeeInterface     ─┘ shrinks the monolith
W4 (S040–S042)  EasyScreen split, BeeUi fix, hw-lib merge        (independent, anytime)
```

Do **not** start W2's extractions (S035/S036) before W1's contract moves (S022 `Enums→BeeIOContracts`,
S033 tool interfaces) land — otherwise the new Tool projects would need to reference BeeGlobal and
recreate the cycle.

## 4b. BACKWARD COMPATIBILITY — saved programs (READ THIS BEFORE ANY SERIALIZED-TYPE MOVE)

Programs are persisted as `List<List<PropetyTool>>` via **`BinaryFormatter` → base64 → text file**
(`BeeCore/Data/Access.cs`, `SaveProg`/`LoadProg`/`SaveBase64`). BinaryFormatter embeds the
**assembly name + full namespace + type name** of every object in the graph. The read path
(`CreateFormatter(forRead)`) currently has **no `SerializationBinder`** — only a `Patterns` load
surrogate. Therefore **moving a serialized type to a different assembly breaks loading old
programs** unless compatibility is added deliberately.

Types confirmed in the saved graph (all `[Serializable]`): `PropetyTool` (`BeeCore/PropetyTool.cs`,
the graph root) and the tool engines in `BeeCore/Unit/*` — `Yolo`, `Patterns`, `Edge`, `Width`,
`SegmentAI`, `PositionAdj`, `MatchingShape`, `AutoTrig`, … — plus `ProgNo`, `AutoTriggerProfile`,
`CameraCalibrationConfig`, `ImageSourceProfile`, and **the enum types in `Enums.cs`** (enum-typed
fields record their assembly-qualified type). `RotatedBoxInfo` is **not** `[Serializable]` → free to
move.

**This makes S022 (Enums→BeeIOContracts) and S035 (Unit engines→BeeCore.Tools) the highest-risk
slices** — they relocate exactly the types stored in existing `.json` program files.

**This is already specced as a foundation slice — do it FIRST:**
`docs/architecture/tasks/2026-09-03-Sxx-legacy-program-binder.md` (concrete `LegacyProgramBinder`
code + the exact hook in `Access.cs` + golden-file smoke). It must land before S022/S023/S035.

**Mandatory compatibility strategy (do this once, before W2's engine moves):**
1. **Add a `LegacyProgramBinder : SerializationBinder`** in `CreateFormatter(forRead)`. Override
   `BindToType(assemblyName, typeName)` to resolve the (unchanged) `typeName` regardless of which
   assembly it now lives in — a single central remap table. This survives every future move.
2. **Add `[assembly: TypeForwardedTo(typeof(NewLocation))]`** in the origin assembly (e.g. BeeCore)
   for each moved serialized type, as a belt-and-braces fallback the binder can also rely on.
3. **Never rename the type or its namespace** — only the assembly changes.
4. **Smoke with a genuinely OLD program file** (one saved by the current shipped build, not one
   re-created after the move) after every serialized-type slice; load it and run one static-image
   inspection before committing.

Non-serialized moves (Phase A: `PCI_Card`, `AdjustControl`, `AdjustMoveMouse`, dep scanners,
`RotatedBoxInfo`, `LocalizationRegistry`) are **not** affected by this section.

## 5. Verification & risk (per slice)

1. Build to `build.log`; `grep -cE ": error "` = 0; warnings ≤ baseline
   (`docs/architecture/baseline_build.md`).
2. `bash tools/check_propety_tools.sh` = 0 (+ the Global guard from companion S027).
3. **Serialization smoke is mandatory for S033/S034/S035** — `PropetyTool` is `ISerializable` and
   persisted in `ClassProject.json`. Load one real sample project, run one static-image inspection,
   confirm no deserialization exception before committing. Use `[assembly: TypeForwardedTo]` if a
   serialized type changes assembly; **never rename its type or namespace here**.
4. `git diff --name-only` in-scope only; append `CODEX_HISTORY.md`; update `.codex-session.md`.
5. **Stop-and-ask** (CLAUDE.md §0.3) if: a slice exceeds 100 changed files; a serialized type must
   move assembly with no sample project to test; or a tool-engine extraction reveals a
   safety/motion path (route to the hardware test checklist instead of a build-only DoD).

## 6. Expected outcome

- BeeGlobal no longer references the PLC vendor; `Global` shrinks to an interface-backed facade.
- A tool = {L0 interface, BeeCore.Tools engine, BeeToolsUI form}; adding/editing one is local.
- BeeInterface drops from 102k/245-files toward a ~25k shell; feature UIs build independently.
- `EasyVision.sln` no longer builds the EasyScreen product; the two evolve separately.
- Reference count on the hottest projects falls, cutting incremental build time and blast radius.
