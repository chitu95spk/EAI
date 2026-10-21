# BeeGlobal Decoupling & De-layering — Codex Execution Plan

> **Status:** ready for execution. Author: review pass 2026-09-03.
> **Map integration:** register these slices under `.structure-map.md` (prefix `S`, next id `S017`).
> Each slice below = one map row = one commit. Do them in order; do **not** batch phases.
> **Precondition for ALL slices:** the worktree must be clean for the files a slice touches.
> As of this plan the tree has ~326 uncommitted changes including `Enums.cs`, `Global.cs`,
> `Config.cs`, `Hardware/*`, `Protocol/ParaProtocol.cs`, `LogsDashboard.cs`. **Do not start a
> slice while its target files are dirty** — commit/stash that work first, then proceed.

---

## 1. Why (problem statement)

`BeeGlobal` is a **low-level library** (referenced *by* BeeCore, BeeInterface, BeeLadderEditor)
but it has accreted four unrelated concerns:

1. WinForms controls & UI-only helpers (`LogsDashboard`, `AdjustControl`, `AdjustMoveMouse`,
   `PCI_Card`, `DASK`).
2. Motion / PLC layer (`Protocol/*`, `Hardware/*`) that is actually a **consumer of BeeEcat**.
3. Per-project infra used by a single project (`Diagnostics/*`, `Localization/*`, dep scanners).
4. `Global.cs` — a static god-object read **~6,600 times** outside BeeGlobal
   (BeeInterface 4345, BeeCore 1857, BeeUi 446) + 515 inside BeeGlobal.

Consequences: any small edit in BeeGlobal forces a near-full-solution rebuild; the motion/PLC
layer cannot be separated from UI; global mutable state makes logic hard to test and race-prone.

## 2. Goals

1. Move single-consumer classes down to the project that actually uses them.
2. Consolidate `Hardware/*` + `Protocol/*` into **BeeEcat** (chosen target).
3. Reduce direct `Global.*` coupling (strangler, multi-slice).

## 3. Hard architectural constraint (must respect)

Current reference graph:

```
BeeIOContracts (leaf)  <-  BeeLadder / BeeEcat  <-  BeeGlobal  <-  BeeCore  <-  BeeInterface
```

`BeeGlobal` already references `BeeEcat`. If `Protocol/*` / `Hardware/*` move **into** BeeEcat
while still calling `Global` / `Config` / `Enums` (which live in BeeGlobal), that creates a
**reference cycle**. Therefore: **push shared contracts DOWN to `BeeIOContracts` (the leaf)
BEFORE moving any code UP into BeeEcat.** `BeeEcat` already declares `namespace BeeGlobal.EtherCat`
and `BeeIOContracts` already hosts `namespace BeeGlobal` types, so keeping the `BeeGlobal`
namespace while relocating files is safe and does not break `using` directives.

## 4. Global rules for every slice (from CLAUDE.md §0 / §12)

- Use `git mv` to preserve history; update the owning `.csproj` in the **same commit**.
- Keep the `BeeGlobal` namespace on relocated types (file/assembly move only — **no rename**).
  Renaming public symbols is out of scope here.
- For any public type that is serialized into a saved program, add
  `[assembly: TypeForwardedTo(typeof(...))]` in the origin assembly and **load one genuinely OLD
  program to prove backward-compat** before committing. See the compatibility section below.

> **BACKWARD COMPATIBILITY — saved programs.** Programs persist as `List<List<PropetyTool>>` via
> **`BinaryFormatter` → base64** (`BeeCore/Data/Access.cs`), which embeds **assembly + namespace +
> type name**. The read path has **no `SerializationBinder`** today. So `Enums.cs` (**S022** — enum
> field types are recorded), `ParaBit`/`ParaValue` (**S023**), and any `[Serializable]` type that
> appears in the tool graph **cannot just be `git mv`'d to another assembly** — old `.json` programs
> would fail to load. Before S022/S023: (1) add a `LegacyProgramBinder : SerializationBinder` in
> `CreateFormatter(forRead)` mapping the unchanged type name to its new assembly; (2) add
> `[assembly: TypeForwardedTo]` in the origin assembly; (3) never rename type/namespace; (4) smoke
> with a program file saved by the **current shipped build**, not a re-saved one. `RotatedBoxInfo`
> is not `[Serializable]` and is exempt.
- `git add` only in-scope files. **Never `git add .`.** The tree is dirty with unrelated work.
- Build to a file, then grep (never dump MSBuild to stdout):
  ```bash
  "/c/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/MSBuild.exe" \
      EasyVision.sln /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal > build.log 2>&1
  grep -cE ": error " build.log     # must be 0
  grep -cE ": warning " build.log   # must be <= baseline in docs/architecture/baseline_build.md
  ```
- Guard: `bash tools/check_propety_tools.sh` must return 0. Every event subscribe stays `-=` then `+=`.
- After each slice: append a block to `CODEX_HISTORY.md` and update `.codex-session.md` with the
  resume point (scope / files / verify command / blocker).
- Commit message: `[S0xx] <summary>` with a body and `Refs: docs/architecture/beeglobal-decoupling-plan.md`.
- Stop-and-ask if a slice produces >100 changed files, or if a serialized type must change assembly
  and no sample project is available to test load.

---

## 5. Per-file verdict table (reference)

Legend: **KEEP** (correctly placed) · **LOCAL→X** (single consumer, relocate to X) ·
**MOVE→X** (belongs to X's concern) · **DOWN→IOC** (contract, push to BeeIOContracts) ·
**DECOUPLE** (strip `Global` first, cannot move yet).

| File / types | Real consumers | Verdict |
| --- | --- | --- |
| `PCI_Card.cs` | BeeInterface(1) | LOCAL→BeeInterface |
| `Common/AdjustControl.cs`, `Common/AdjustMoveMouse.cs` | BeeInterface(1) | LOCAL→BeeInterface |
| `General/DependencyScanner.cs`, `General/PythonDepScanner.cs` | BeeUi(1) | LOCAL→BeeUi |
| `CameraCalibrationConfig.cs` | BeeCore(6) | **KEEP** — `Global.cs:847` holds a static `CameraCalibration` instance, so moving the type to BeeCore creates a BeeGlobal→BeeCore cycle. Move only after the static field is relocated (a Phase D-style change). |
| `RotatedBoxInfo.cs` | BeeCore(2) | LOCAL→BeeCore (not referenced anywhere in BeeGlobal — safe) |
| `Localization/LocalizationRegistry.cs` | BeeInterfaceControls(3) | LOCAL→BeeInterfaceControls |
| `Localization/LanguageStore.cs` | Interface/Controls/Ui | MOVE→BeeInterfaceControls |
| `DASK.cs` | BeeInterface(1)+BeeUi(1) | MOVE→BeeInterfaceControls |
| `LogsDashboard.cs` (`LogCanvas`, `LogEntry`, `LogsDashboard`, `LogCategoryToggleButton`) | Core(29)/Interface(18)/Ui(3) | SPLIT: keep log facade in BeeGlobal, MOVE WinForms controls→BeeInterfaceControls |
| `Diagnostics/CpuAffinityService.cs`, `ResourceMonitorService.cs` | mostly BeeCore(6-8) | MOVE→BeeCore (verify coupling first) |
| `Hardware/HardwareModel.cs` (Global.=0) | Core(1)/Interface(5) | MOVE→BeeEcat (cleanest, first) |
| `Hardware/KvMl16vMotionProfiles.cs`, `KvMl16vPositionScalingService.cs` | motion | MOVE→BeeEcat |
| `Hardware/KvMl16vUnitBus.cs` (Global.=1) | internal | MOVE→BeeEcat (strip 1 Global ref) |
| `Hardware/KvMl16vCommissioningDiscovery.cs` | BeeInterface(1) | MOVE→BeeEcat |
| `Hardware/VisionLadderSignalState.cs`, `LadderSystemValueBridge.cs` | ladder bridge | MOVE→BeeEcat |
| `Hardware/HardwareLadderCoordinator.cs` (Global.=16) | Interface(3)/LadderEditor(1) | DECOUPLE→BeeEcat |
| `Protocol/ParaBit.cs`, `Protocol/ParaValue.cs` | POCO | DOWN→IOC |
| `Protocol/PlcClient.cs` (Global.=45) | Core(1)/Interface(1) | DECOUPLE→BeeEcat |
| `Protocol/ParaProtocol.cs` (7843 lines, Global.=309, Config=58, enum=105) | Core(4)/Interface(10) | DECOUPLE→BeeEcat (god-object knot, last) |
| `Enums.cs` (654 lines, only `using System`) | whole repo | DOWN→IOC (**cycle-breaker, do early**) |
| `Config.cs` (deps `BeeGlobal.Diagnostics`) | wide | KEEP |
| `Global.cs` | ~6600 calls | KEEP + DECOUPLE (Phase D) |
| `RectRotate.cs` (Core 78/Interface 39) | wide geometry | KEEP |
| `ParaShow/ParaCommon/ParaIO/ParaCamera`, `ItemNew/ItemRegsImg/ProgNo/Labels/Model/HistoryCheck` | config/model, wide | KEEP |
| `QpcTimer/TimingUtils/CycleTimerSplit/PerformanceLog` | timing, shared | KEEP |
| `AutoTriggerProfile/ImageSourceModel/BatchRename/IntArrayWithEvent/ValuePara` | ≥2 projects | KEEP |
| `Test.cs` | stub | verify dead, then delete |

---

## 6. Execution slices (map rows S017…)

> Order matters: **contracts down → code up → Global last.** Verify (§4) after every slice.

### Phase A — low-risk local moves (independent; safe to start once each file is clean)

- **S017 · low · LOCAL→BeeInterface** — `git mv` `PCI_Card.cs`, `Common/AdjustControl.cs`,
  `Common/AdjustMoveMouse.cs` into `BeeInterface/` (e.g. `BeeInterface/Class/`). Remove the 3
  `<Compile>` items from `BeeGlobal.csproj`, add to `BeeInterface.csproj`. Keep namespace `BeeGlobal`.
- **S018 · low · LOCAL→BeeUi** — move `General/DependencyScanner.cs`, `General/PythonDepScanner.cs`
  into `BeeUi/`. (Note: both files currently declare **no namespace** — they live in the global
  namespace. Moving the file works as-is; do not add a namespace in the same commit as the move.)
- **S019 · low · LOCAL→BeeCore** — move **`RotatedBoxInfo.cs` only** into `BeeCore/` (nothing in
  BeeGlobal references it — safe). **Do NOT move `CameraCalibrationConfig.cs`:** `Global.cs:847`
  holds `public static CameraCalibrationConfig CameraCalibration = new CameraCalibrationConfig()`,
  so relocating the type to BeeCore would make BeeGlobal reference a BeeCore type and form a cycle.
  It stays in BeeGlobal until the static field itself is moved out of `Global` (a Phase D task).
- **S020 · low · LOCAL→BeeInterfaceControls** — move `Localization/LocalizationRegistry.cs`,
  then `Localization/LanguageStore.cs`, then `DASK.cs`.
- **S021 · low · cleanup** — confirm `Test.cs` has no live caller (`grep -rn "\bTest\b"` scoped to
  the `BeeGlobal.Test` type, not the substring); delete if dead.

### Phase B — push contracts to BeeIOContracts (unlocks the cycle)

> **PREREQUISITE — do the LegacyProgramBinder foundation slice first:**
> `docs/architecture/tasks/2026-09-03-Sxx-legacy-program-binder.md`. It installs the read-side
> `SerializationBinder` (+ golden-file smoke) that keeps old saved programs loadable once S022/S023
> relocate serialized types. Without it, moving `Enums.cs`/`ParaBit`/`ParaValue` makes old programs
> **silently load empty** (LoadBase64 swallows the exception).

- **S022 · medium · DOWN→IOC · CYCLE-BREAKER** — `git mv Enums.cs` into `BeeIOContracts/`. Keep
  `namespace BeeGlobal` and **every enum name unchanged** (they are serialized widely). Update both
  `.csproj`. This lets BeeEcat/BeeLadder share the enums without referencing BeeGlobal.
  **Smoke is mandatory** (enum values are persisted): load one sample project after build.
- **S023 · low · DOWN→IOC** — move `Protocol/ParaBit.cs`, `Protocol/ParaValue.cs` (pure POCO)
  into `BeeIOContracts/`.

### Phase C — move clean Hardware group into BeeEcat (after S022)

Order by increasing `Global` coupling:

- **S024 · medium · MOVE→BeeEcat** — `Hardware/HardwareModel.cs` (Global.=0). Prove it builds with
  BeeEcat only referencing BeeIOContracts.
- **S025 · medium · MOVE→BeeEcat** — `KvMl16vMotionProfiles.cs`, `KvMl16vPositionScalingService.cs`,
  `KvMl16vCommissioningDiscovery.cs`, `VisionLadderSignalState.cs`, `LadderSystemValueBridge.cs`.
- **S026 · medium · MOVE→BeeEcat** — `KvMl16vUnitBus.cs`; first remove its single `Global.` read
  (inject the value or read it via a BeeIOContracts interface), then move.

### Phase D — reduce `Global.*` coupling (strangler; multi sub-slice, no big-bang)

- **S027 · low · guard** — add `tools/check_global_usage.sh` (mirror of `check_propety_tools.sh`)
  that fails when new `Global.` call sites appear in new code. Wire into the DoD checklist.
- **S028 · low · organize** — split `Global.cs` into `partial class Global` files by concern
  (UI-state / runtime-state / hardware-state / program-state). **File split only, no API change.**
- **S029 · medium · de-`dynamic`** — replace `dynamic` fields `Main`, `ScanCCD`, `EditTool`,
  `ToolSettings`, `StepEdit` with interfaces declared in `BeeIOContracts`; assign through the
  interface. Removes runtime-binding risk and unblocks moving dependent code.
- **S030 · high · inject state** — for `ParaProtocol` (309 `Global.`) and `PlcClient` (45), replace
  direct `Global.` reads with an injected context/snapshot object or events. This is the
  precondition for Phase E.

### Phase E — move Protocol / coordinator into BeeEcat (only after Phase D)

- **S031 · high · DECOUPLE→BeeEcat** — `PlcClient.cs`, then `HardwareLadderCoordinator.cs`, then
  finally `ParaProtocol.cs`. Only when each file's `Global.` ref count is 0 (or routed through a
  BeeIOContracts interface).
- These sit on the **motion/safety path**. A build-pass is **not** done: record in `CODEX_HISTORY.md`
  that hardware was not verified and list the required test cases (DS5 connect, ladder scan cycle,
  `POSITION_START` accept/complete, quick-stop latch).

---

## 7. Verification checklist (run per slice)

1. `MSBuild EasyVision.sln … > build.log 2>&1` → `grep -cE ": error " build.log` = 0;
   warnings ≤ baseline (`docs/architecture/baseline_build.md`).
2. `bash tools/check_propety_tools.sh` = 0 (plus the new Global guard from S027 onward).
3. Smoke: launch app → load one sample project → run one static-image inspection cycle → no
   exception. **Mandatory after S022/S023** (serialized enum/POCO changed assembly).
4. `git diff --name-only` contains only in-scope files.
5. Append `CODEX_HISTORY.md`; update `.codex-session.md` resume point.
6. Phase E slices: explicitly state "hardware not verified" + the DS5/ladder test cases.

## 8. Rollback

Per slice: `git reset --hard` to the previous commit (each slice is atomic). Because contracts
move before code, a failed Phase C/E slice never leaves a dangling reference — the down-moved
enums/POCO in Phase B remain valid independently.
