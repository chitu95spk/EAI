# Task I094 — "Auto Tune": demand-driven, CT-optimal CPU core assignment

> **This document is the single source of truth. Implement it exactly. Do not redesign, do not expand
> scope, do not invent new mechanisms. If a step seems ambiguous, re-read the cited `file:line`
> anchors — every fact you need is already in this repo.**

## 0. MISSION

Add ONE button "Auto Tune Cores" to `MonitorPanel` that computes and applies a **demand-driven,
minimal-footprint** CPU-core plan for the **current** workload, optimizing **total cycle time (CT)**.
Core philosophy (from measured logs — see §12):

- **Reserve the minimum.** Only tasks that are actually running get a core. Idle tasks
  (EtherNet/IP, AutoTrigger, ProgramSync, an unused 2nd camera) get `-1` (Auto) and take nothing.
- **Realtime = one LOGICAL core each, share the HT sibling.** ECAT and Ladder each get a single
  logical processor; their HT-sibling logical stays in the shared pool for vision. Do NOT reserve a
  whole physical core (2 logicals) per realtime task.
- **Cameras get NO reservation.** Camera reads are I/O-bound (they mostly wait on the SDK/DMA, not the
  CPU — proven in §12), so a camera only receives a *soft preferred* E-core; it never removes a core
  from the pool. Priority during capture is already handled by the existing dynamic scope.
- **Everything else goes to the shared pool** so the tool scheduler (Width/Learning) has the most
  cores — that is what actually shortens CT.

## 1. NON-NEGOTIABLE GUARDRAILS

### 1.1 MUST
1. Managed C# only. Exactly TWO files in scope:
   - `BeeGlobal/Diagnostics/CpuAffinityService.cs`
   - `BeeInterface/Diagnostics/MonitorPanel.cs`
2. REUSE existing apply plumbing: apply the plan by calling `CpuAffinityService.SetConfiguredCore`
   per task (`CpuAffinityService.cs:408-444`) — it writes config, reapplies threads, and (ECAT/Ladder)
   calls `ApplyRuntimeIsolation`.
3. Change `ApplyRuntimeIsolation` reservation granularity from PHYSICAL to LOGICAL exactly as §6
   specifies — this is the only permitted edit to that method. Nothing else in it changes.
4. Keep thread-pinning code untouched (`EnterCurrentThreadCore`, `EnterCameraReadScope`,
   `LeaveCameraReadIsolation`, `ApplyEcatEnvironment`, `FindRecommendedCore`). You CALL them; you do
   not alter them.
5. Build `Release|x64` for both projects, 0 errors, warnings ≤ baseline. Fix every error you cause.
6. Repo conventions: event `-=` before `+=`; no new UI singleton/static state (state lives in
   `Global.Config` + `CpuAffinityService`); do not touch files outside scope even if the tree is dirty.

### 1.2 MUST NOT
1. Do NOT touch native (`EtherCatCommunication/**`, `BeeEcat/**`) or any P/Invoke / ABI.
2. Do NOT reserve a whole physical core for a realtime task, and do NOT reserve any camera core in the
   process default. (The old physical-granularity + camera-reservation behavior is being replaced.)
3. Do NOT auto-run the tuner on program/camera changes — button click only.
4. Do NOT add `CameraRead2/3` keys (>2 cameras = option A, §3).
5. Do NOT rebuild `BeeLicense` or the full solution; build only the two projects (§9).
6. Do NOT rename symbols, reformat unrelated code, or "fix" nearby smells.

## 2. COMPLETION PROTOCOL (run to done — do not stop early)
1. Edit `ApplyRuntimeIsolation` (logical reserve) in `CpuAffinityService.cs` per §6.
2. Add `AutoTuneAssignments` + `CpuAutoTunePlan`/`CpuAutoTuneChange` per §5/§7.
3. Add the button + handler in `MonitorPanel.cs` per §8.
4. Build both projects (§9). If red: read error → fix in-scope → rebuild. Never leave it red.
5. Self-verify every DoD item in §10 with concrete evidence.
6. Append `CODEX_HISTORY.md` (template 11.2); commit `[I094] Add demand-driven Auto Tune CPU cores`.
7. Report: files touched, build result, DoD status, blockers. If blocked, say exactly what/why —
   never silently skip.

## 3. LOCKED DECISIONS (do not revisit)
- **ECAT shares its HT sibling** (1 logical for ECAT; sibling to the pool). Best CT; extra jitter is
  absorbed by the configurable WKC tolerance from I093.
- **Ladder = 1 logical on a physical core DIFFERENT from ECAT**; its sibling goes to the pool.
- **Cameras: no reservation, soft E-core preference only.** Dynamic priority via the existing scope.
- **Idle tasks get `-1`.** Never reserve for a task not running in the current workload.
- **>2 cameras = option A:** only `CameraRead0/1` get a preferred core; cam 3–4 use generic
  `CameraRead`. Do not add new keys.
- **Manual-run only.**

## 4. RUNTIME INPUTS (use these exact sources — do not invent new plumbing)

| Need | Source (verified) |
| --- | --- |
| Processing mode | `Global.Config.ProcessingMode` (`BeeGlobal/Enums.cs:594`) |
| Lane count | `Global.GetConfiguredLaneCount()` (`BeeGlobal/LaneRuntime.cs:95`, 1..4) |
| Camera count | `Global.Config.NumCamera` (`BeeGlobal/Config.cs:38`) |
| Word mode (no ECAT cyclic thread) | `Global.IsSingleProgramWordMode` (`BeeGlobal/LaneRuntime.cs:76`) |
| Topology | `CpuAffinityService.GetTopology()` → `CpuTopologyEntry{IsHighPerformance,IsEfficiencyCore,PhysicalCoreIndex,DisplayIndex,CpuSetId,Group}` (`CpuAffinityService.cs:17-27`) |
| Physical-core count / key | `CountPhysicalCores(items)` (`:973`), `PhysicalCoreKey(entry)` (`:984`) |
| Task keys | `CpuAffinityTaskKeys.*` (`CpuAffinityService.cs:35-49`) |
| Current core of a task | `CpuAffinityService.GetConfiguredCore(taskKey)` (`:388`) |
| Known tasks (display names) | `CpuAffinityService.GetKnownTasks()` (`:243`) |

### Demand flags (compute once)
- `bool wordMode = Global.IsSingleProgramWordMode;`
- `bool ecatNeeded = !wordMode;`   // no ECAT cyclic thread in Word mode
- `bool ladderNeeded = !wordMode;`
- `int cameraCount = Math.Max(1, Math.Min(2, Global.Config?.NumCamera ?? 1));`
- Tasks considered IDLE for this task and always set to `-1`: `EtherNetIpTcp/Udp/Scanner`,
  `AutoTriggerAcquire/Analyze`, `ProgramSync`. (They are absent from the measured workload, §12. If a
  future workload needs them, extend later — not now.)

## 5. ALLOCATION ALGORITHM (deterministic — implement exactly)

Build physical-core representatives like `FindRecommendedCore` (`:878`): one entry per physical core
(lowest-DisplayIndex logical). Track `HashSet<long> usedPhysical`. To pick, add an `internal` helper
`TryPickRecommendedCore(usedPhysical, preferEfficiency)` that returns the next unused representative
using the SAME sort as `FindRecommendedCore` (do not duplicate the sort; refactor `FindRecommendedCore`
to call the helper, keeping its existing public/private signatures working).

Compute `plan[taskKey] = coreDisplayIndex` (or `-1`) in THIS order:

1. **ECAT** — if `ecatNeeded`: `e = TryPickRecommendedCore(usedPhysical, preferEfficiency:false)`
   (P-core). `plan[EcatDs5] = e.DisplayIndex`; `usedPhysical.Add(PhysicalCoreKey(e))`. Else `-1`.
2. **Ladder** — if `ladderNeeded`: `l = TryPickRecommendedCore(usedPhysical, false)` (next P-core,
   distinct physical from ECAT by construction). `plan[LadderRuntime] = l.DisplayIndex`;
   `usedPhysical.Add(PhysicalCoreKey(l))`. Else `-1`.
   > NOTE: `usedPhysical` blocks a THIRD task from taking ECAT's or Ladder's physical core, but the HT
   > sibling of ECAT/Ladder is intentionally NOT assigned to anyone here — it simply stays in the
   > shared pool (it is not excluded by §6, because §6 reserves logicals, not physicals).
3. **Cameras** — for `i in 0..cameraCount-1`: `c = TryPickRecommendedCore(usedPhysical, preferEfficiency:true)`
   (E-core). `plan[CameraRead{i}] = c.DisplayIndex`. **Do NOT** add camera cores to any permanent
   reservation. Because §6 no longer reserves camera cores, this is only a soft run-time preference;
   the core rejoins the pool after doneCCD via the existing scope (`:301`/`:787`). The unused camera
   key (e.g. `CameraRead1` when `cameraCount==1`) → `-1`. Do NOT add camera physical to `usedPhysical`
   (cameras may share physical cores with the pool — they barely use CPU).
4. **UiMain** — `-1` (Auto). The UI thread runs draw work; leave it in the shared pool. (Do not pin it.)
5. **All idle tasks (§4)** — `-1`.

### Degradation (mandatory)
- `CountPhysicalCores(items) < 4`: too small to isolate. Set every task `-1`,
  `plan.IsolationEnabled=false`, Summary explains "CPU has <4 physical cores: isolation off, using the
  OS scheduler." (Note: threshold is now 4, because we no longer need 6+ for physical reservation —
  logical reserve of 2 cores is viable from 4 physical / e.g. 4C8T. Still, on <4 physical, don't fight
  the scheduler.)
- Otherwise `plan.IsolationEnabled = true`.
- Non-hybrid CPU (no E-cores): `preferEfficiency:true` collapses to DisplayIndex order; cameras still
  get valid cores. No special case.

### Invariant assertion BEFORE writing (fail-closed)
Assert: ECAT and Ladder (when assigned) are on DIFFERENT physical cores. If violated, do NOT apply —
return `Applied=false` with a Summary naming the conflict; leave config untouched.

## 6. `ApplyRuntimeIsolation` CHANGE — physical → logical reservation (the ONLY edit to this method)

Current behavior (`CpuAffinityService.cs:565-612`): reserves the ECAT and Ladder **physical** cores
(both HT siblings) and, while cameras read, the camera **physical** cores; the allowed set skips any
entry whose `PhysicalCoreKey` is reserved.

Replace the reservation with LOGICAL granularity:
1. Keep the guard that ECAT and Ladder must be on different physical cores
   (`ecatPhysical == ladderPhysical` → `ClearProcessDefaultCpuSets("...share one physical core")`,
   `:567-571`). Keep it.
2. Replace `var reserved = new HashSet<long>{ ecatPhysical, ladderPhysical };` with a logical set:
   `var reservedCpuSetIds = new HashSet<uint> { ecat.CpuSetId, ladder.CpuSetId };`
3. DELETE the camera-reservation block (`:574-596`, the `camera0/camera1/cameraIsolationActive`
   logic). Cameras are never reserved now. Remove the `cameraIsolationActive` references from the
   status string (`:628-630`) — report only ECAT and Ladder cores.
4. Change the allowed-set loop (`:597-607`) to exclude by LOGICAL id:
   `if (reservedCpuSetIds.Contains(entry.CpuSetId)) continue;` (was: `reserved.Contains(PhysicalCoreKey(entry))`).
5. The guard `allowedPhysicalCores.Count < 4` (`:608-612`): keep it but note it now counts distinct
   physical cores among the allowed logicals; because ECAT/Ladder siblings remain allowed, their
   physical cores still count. This is fine — keep the guard as a floor.

Effect: only 2 logical processors (the ECAT logical + the Ladder logical) leave the app pool; their
HT siblings and all camera cores remain available to vision/tools. ECAT/Ladder threads keep their
existing soft-CPU-set pin (per Windows docs, `SetThreadSelectedCpuSets` overrides the process default,
so a realtime thread still owns its excluded logical). Do not change the pinning.

> Because `EnterCameraReadScope`/`LeaveCameraReadIsolation` still call `ApplyRuntimeIsolation`, and
> that method no longer reserves camera cores, the dynamic scope simply becomes a soft camera pin plus
> a harmless re-apply. Leave those callers as-is.

## 7. NEW API in CpuAffinityService.cs

```csharp
public sealed class CpuAutoTunePlan
{
    public bool Applied;
    public bool IsolationEnabled;
    public bool EcatCoreChanged;
    public string Summary = string.Empty;
    public List<CpuAutoTuneChange> Changes = new List<CpuAutoTuneChange>();
}
public sealed class CpuAutoTuneChange
{
    public string TaskKey;
    public string DisplayName;   // from GetKnownTasks()
    public int OldCore;          // -1 = Auto
    public int NewCore;          // -1 = Auto
}

// apply=false -> compute Changes/Summary only, write nothing (preview).
// apply=true  -> set the two flags, SetConfiguredCore per changed task, then ApplyRuntimeIsolation()
//                + ApplyEcatEnvironment() once; Applied=true. Never calls SaveData.*.
public static CpuAutoTunePlan AutoTuneAssignments(Config config, bool apply);
```
Apply order (when `apply` and not degraded-off):
1. `config.EnableRealtimeCoreIsolation = plan.IsolationEnabled; config.EnablePerCameraReadAffinity = plan.IsolationEnabled;`
2. Compute `EcatCoreChanged` = old EcatDs5 core != new.
3. For each `Change` with `OldCore != NewCore`: `SetConfiguredCore(change.TaskKey, change.NewCore);`
4. `ApplyRuntimeIsolation(); ApplyEcatEnvironment();`
5. `plan.Applied = true;`
Include in `Summary`: chosen cores per active task, which tasks were set to Auto (idle), and a line
like "Shared pool: N logical cores for vision/tools".

## 8. UI — button in MonitorPanel.cs
Anchor: toolbar `FlowLayoutPanel bar` at `MonitorPanel.cs:426-436`.
1. Field: `private readonly Button _btnAutoTune = new Button();`
2. `_btnAutoTune.Text = "Auto Tune Cores"; _btnAutoTune.AutoSize = true; bar.Controls.Add(_btnAutoTune);`
   `_btnAutoTune.Click -= OnAutoTuneClicked; _btnAutoTune.Click += OnAutoTuneClicked;`
3. Handler (async, guarded by existing `_affinityBusy`, `:227`):
   - `var preview = CpuAffinityService.AutoTuneAssignments(Global.Config, false);`
   - If `preview.Changes.Count == 0`: MessageBox info with `preview.Summary`; return.
   - Build a detail string (Summary + each change `DisplayName: C{old|Auto} -> C{new|Auto}`), confirm
     via `MessageBox.Show(..., MessageBoxButtons.OKCancel, ...)` warning "ECAT may reconnect."
   - On OK: `_affinityBusy = true;` → `var applied = CpuAffinityService.AutoTuneAssignments(Global.Config, true);`
     → `try { SaveData.Config(Global.Config); } catch (Exception ex) { Global.LogError("CPU Affinity", ...); }`
     → `if (applied.EcatCoreChanged) await ReconnectActiveEcatForAffinityAsync();` (existing, `:292`).
   - `try/catch` around all; `finally { _affinityBusy = false; _corePanel.RefreshAssignments(); }`.
   Reuse `ReconnectActiveEcatForAffinityAsync`; create no new reconnect path.

## 9. VERIFY (exact commands)
```
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" "BeeGlobal\BeeGlobal.csproj" /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" "BeeInterface\BeeInterface.csproj" /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo
```
Expected: both end `-> ...dll`, `0 Error(s)`, only pre-existing CS0414/CS0067/CS1998 warnings.

Smoke (describe if app not runnable): MonitorPanel → Auto Tune → dialog lists only active tasks (ECAT,
Ladder, cams); idle tasks show `-> Auto`; after Apply, ECAT/Ladder on distinct P-core logicals with
their SIBLINGS still selectable in the "Runtime tasks" menu (i.e. not reserved), cameras on E-cores.

## 10. DEFINITION OF DONE (self-verify each with evidence)
- [ ] `ApplyRuntimeIsolation` reserves LOGICAL (ECAT+Ladder CpuSetIds); camera reservation removed; ECAT≠Ladder physical guard kept.
- [ ] `AutoTuneAssignments`/`CpuAutoTunePlan`/`CpuAutoTuneChange` implemented; preview and apply both work.
- [ ] Demand-driven: idle tasks → `-1`; cameras get preferred core but are NOT added to `usedPhysical` and NOT reserved; only active-count cameras assigned.
- [ ] Realtime = 1 logical each, siblings remain in the pool (verified: ECAT sibling logical still in allowed set).
- [ ] Degradation `<4 physical` → all Auto, isolation off; invariant assert prevents ECAT/Ladder sharing a physical core.
- [ ] Button added to `bar`; preview→confirm→apply→reconnect only if EcatCoreChanged; `-=`/`+=` used.
- [ ] `BeeGlobal` + `BeeInterface` build `Release|x64`, 0 errors, warnings ≤ baseline.
- [ ] `git diff --name-only` = only the 2 in-scope files + this doc + `CODEX_HISTORY.md`.
- [ ] `CODEX_HISTORY.md` appended; commit `[I094] Add demand-driven Auto Tune CPU cores`.

## 11. ROLLBACK
- Build/smoke fail unfixable in-scope: `git checkout HEAD -- BeeGlobal/Diagnostics/CpuAffinityService.cs BeeInterface/Diagnostics/MonitorPanel.cs`; report the blocker precisely; never leave a red build.
- Runtime regression: operator resets any task to Auto via the existing right-click menu, or restores `Config` from backup. The button writes only `Config`; it creates no new persistent state.

## 12. MEASURED EVIDENCE (why this design — from F:\logs.json, 2026-08-27)

### What this task DOES and does NOT do (READ FIRST — scope reality check)
This task is **hygiene + stability only: stop wasting cores**. It is **NOT** the fix for CT and **NOT**
the fix for the DS5 Axis `WorkCounterLoss` (E-804). Field evidence proves core allocation alone is
insufficient:

- The tool batch runs at only **22–41 % process CPU** (`PERF.ToolBatch.Done procCpu%`) ⇒ the machine is
  **not CPU-bound** during inspection; extra pool cores give diminishing returns.
- **One program < 400 ms, but two programs in parallel spike to ~1130 ms despite 12 logical CPUs** ⇒ no
  CPU scaling; the limit is **memory bandwidth**, not cores.
- **The Axis still faults when 2 programs + CCD read run together even with ECAT already on its own
  core** ⇒ core isolation cannot help, because memory bandwidth is shared by every core: a saturated
  RAM bus stalls the ECAT cyclic thread's PDO access on its isolated core.

The actual root cause is the **full-frame 20 MP warp for rotated ROIs** in `Crop.cs`, fixed by
**task I095** (`2026-08-27-I095-windowed-rotated-crop-memory-bandwidth-fix.md`) — that is the primary
work. **Do I095 first if both are queued.** Keep I094 for what it genuinely gives: no reservation for
idle tasks, and 1 logical (not a whole physical core) per realtime task.

### CT breakdown (per lane, ~1130 ms avg, 862–1380)
- **Camera read** 376 ms avg (cam=0 up to 788 ms; cam=0 ≈ 2× cam=1). I/O-bound: activeReads 1 vs 2 ≈
  equal (343 vs 361 ms); `sdk` transfer wait dominates, `copy` (CPU) only 18–38 ms ⇒ cameras do NOT
  need a dedicated core.
- **Tool check** 314 ms avg, but batch only 22–41 % CPU. Serial poles inside it: Width **crop
  263–336 ms** (slow masked path, `fast=False`) and Learning ONNX **detect 68–360 ms** on the full
  5472×3648 frame (GPU-bound).
- **DoneCCDToResult** 343 ms avg (up to 547 ms) — PLC result handshake.
- Active tasks only: ECAT, Ladder (DS5 motion), 2 cameras, tools. NO EtherNet/IP, AutoTrigger, or
  ProgramSync entries ⇒ reserving cores for them is pure waste.
- Realtime cost is tiny per cycle (ECAT scan ~1–2 ms of a 4 ms period) ⇒ a full physical core per
  realtime task wastes the HT sibling vision could use.

### Errors / warnings and root causes (from the same log)
| Signal | Count | Root cause | Fix |
| --- | --- | --- | --- |
| `POSITION_SAFETY_ABORT StopCause=WorkCounterLoss` (E-804) | 4 ERROR | Bursty camera+crop CPU contention starves the ECAT logical → WKC stale → drive fault | I093 (WKC tolerance, done) + this task (isolation) |
| `ECAT RecoveryAttempt slave 2 AL 0x001A Synchronization error` | 5 WARN | DC-sync broken by the SAME cyclic-timing jitter; auto-recovers | Same as above |
| `PERF.PLC.WriteIOProcessing slow ChangeMode 451–548 ms` (thr 40) | 2 WARN | Mode-change PLC write stalls ~0.5 s (not comms: `wait=0 connected=True ecatFault=False`) | Investigate in §14 (not core-related) |
| `PERF.Width slow crop 263–454 ms` / `PERF.Lane slow >900 ms` | many WARN (13+20+20+1) | **Rotated ROI → full-frame 20 MP warp** (`Crop.cs:807`); memory-bandwidth bound | **Task I095** (primary fix) |
| No CPU scaling: 1 prog <400 ms vs 2 prog ~1130 ms on 12 logicals | field | Same 20 MP warp ×2 saturates the RAM bus | **Task I095** |
| Axis fault persists with ECAT on a dedicated core | field | Bandwidth is shared across all cores; isolation cannot fix it | **Task I095** |

## 13. SAFETY
Motion-safety path. Auto Tune can change the ECAT core → EtherCAT reconnect (reuse
`ReconnectActiveEcatForAffinityAsync`). Before production on the real 4P+6E PC, verify on hardware:
(a) two cameras simultaneously no longer produce `WorkCounterLoss`; (b) with ECAT sharing its HT
sibling, jitter stays within the configured WKC tolerance (I093); (c) physically pulling the EtherCAT
cable still Quick Stops immediately. Depends on I089 WKC/DC-sync recovery and I092 ride-through.
Related: [[e804-workcounterloss-camera-starvation]], task I093.
