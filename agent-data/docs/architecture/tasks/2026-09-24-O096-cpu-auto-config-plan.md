# O096 - One-click Auto CPU / EtherCAT configuration (plan v1.1)

Status: approved by the user on 2026-09-24 ("tien hanh thuc hien plan"). D1 was decided by the user (see section 12); D2-D8 are approved at the recommended defaults. Implementation is split into three revisions (r1, r2, r3), one commit each. Contract: `.codex/task-contract.O096.toml`. Backup tag before work: `backup-before-o096` (HEAD 6a4f07d).

Source of this document: an architect plan (opus) plus its D1 delta, both from 2026-09-24. Where a value is "derived", it was worked out by hand from the code and MUST be confirmed by the offline harness. If the pure function, implemented exactly as written in section 4, produces a different value than a golden value below, STOP and report the discrepancy. Do not silently change either side.

## 1. Goal

Replace the panel full of manual CPU/EtherCAT realtime options (Monitor panel, `BeeInterface/Diagnostics/MonitorPanel.cs`) with ONE "Auto" button. Auto reads the machine topology (physical cores, logical cores, P/E class, SMT) and chooses the whole configuration: isolation flags, core assignments, every option flag. The operator chooses nothing.

Rule behind the design:
- Auto only moves off what Auto Tune Cores does today where there is a hardware measurement or an explicit user decision (D1) to justify it.
- Every option whose evidence is negative, contradictory or missing is forced to its pre-change default (off / 0 / Auto).
- Auto is not a way to run experiments.

Non-goals: no native change, no ABI change, no change to ECAT Settings values, no ladder/PLC change, no claim of fewer drops or less jitter.

## 2. Ground truth

Existing Auto Tune Cores (`CpuAffinityService.AutoTuneAssignments`, cs 784-903; `AssignAutoTuneCore` 1675; `TryPickRecommendedCore` 2019; `FindRecommendedCore(used, preferEfficiency)` 2032-2075; `ValidateAutoTunePlan` 1907; `CompleteAutoTunePlan` 1695-1748; click handler `MonitorPanel.OnAutoTuneClicked` 632-743):
- It only assigns cores (ECAT and Ladder P-core-first on different physical cores; CameraRead0/1 on distinct E-cores first; EcatIoPoll from the pool; other task keys reset to Auto) and writes `EnableRealtimeCoreIsolation` and `EnablePerCameraReadAffinity` = true.
- It does NOT touch Independent, Experimental, ESC counters, Fence boot core, RX timeout, Freeze WKC, Read watchdog, `CpuReservedTaskKeys*`, ECAT Settings.
- Gates: blocked if non-boot physical cores < cameras + ECAT + Ladder, or ECAT needed and physical cores < 4 (cs 811-812). `EnsureRecommendedRuntimeAssignments` (cs 913, run at load `LoadData.cs:442`) no-ops if physical < 6 or logical < 8.
- It depends on live `Global.Comunication` state (Vision.cs:33-38): before hardware config is loaded it plans no ECAT or Ladder core.
- Apply runs on the UI thread, holds `CpuAffinityService.RuntimePolicyGate` (also taken by lane admission `BeeInterface/Group/View.cs:11895` and tool scheduler admission `BeeCore/PropetyTool.cs:946`), and its idle-wait loop (MonitorPanel.cs 685-706) has no timeout.

Per-option runtime semantics and apply timing:
- Realtime isolation: live via `ApplyRuntimeIsolation`. Gates process-default CPU-set exclusion, vision hard mask, reserved-core set, ECAT/Ladder backfill, Ladder-only pin (`OwnsCoreExclusively`).
- Camera read affinity: live. Gates camera backfill, whether a camera core is fenced out of the vision pool, `isolateCameraReadCores` in `BeeCore/Func/Camera.cs:4180-4185`.
- Independent hardware runtime: wins over isolation (mutually exclusive); needs ECAT reconnect.
- Experimental realtime: env `BEE_ECAT_ADAPTIVE_SPIN`, `BEE_ECAT_STAGED_OUTPUT`, `BEE_ECAT_UNPIN_WHEN_UNFENCED`; latched once per connect; needs reconnect.
- ESC error counters: live; costs about 44 ms of held cyclic lock per sample.
- Fence boot core: env `BEE_ECAT_CYCLIC_FORBID_MASK`; needs reconnect.
- RX timeout: env `BEE_ECAT_RECEIVE_TIMEOUT_US`; 0 = Auto; needs reconnect.
- Freeze WKC on read: live; suspends the master's WKC fault verdict during camera reads (axis-safety trade-off).
- Read watchdog: live; forced 0 when Freeze is off.
- Clear Core Locks reconnects unconditionally. Native env vars are latched once per connect; only ESC counters, Freeze WKC and Read watchdog apply live.

Constraints: never hard-mask through `EnterCurrentThreadCore`; ECAT and Ladder on different physical cores (`ValidateAutoTunePlan` 1918-1926); no realtime task on the Windows boot core (1942-1944); `MinimumVisionPhysicalCores = 2` (Vision.cs:302); `EcatIoPoll` is deliberately not in the default reserved keys (cs 1050-1051, measured 13% p95); topology masks only support group 0 and at most 64 CPUs; C# 7.3.

Topology API (`BeeCpu/CpuTopology.cs`, `CpuTopologyEntry.cs`): `DetectTopology` (184-270) via `GetSystemCpuSetInformation`: `EfficiencyClass`, `PhysicalCoreIndex`, group/logical index, `hybrid = min != max class`, `IsHighPerformance`, `IsEfficiencyCore` (a three-tier CPU's middle tier counts as efficiency). No SMT flag (siblings grouped via `PhysicalCoreKey`). Cached forever. SILENT fallback (234-247) when the API fails: one entry per `Environment.ProcessorCount`, each its own physical core, non-hybrid, nothing logged. `Global.GetPhysicalCoreCount` (`BeeGlobal/Global.cs:330-407`) is an independent detector with a different fallback. No CPU model detection exists in the repo.

Persistence: BinaryFormatter Base64 in `Common\Default.config` (`BeeCore/Data/SaveData.cs:306-315`, `LoadData.cs:357-358`, also project container `ProjectContainerStore.cs:95,145`). BinaryFormatter does not run field initializers. CPU flags migrate in `LoadData.cs:409-440` keyed on `CpuAffinityConfigVersion` (currently 7): below 5 forces isolation and camera affinity true; below 6 seeds reserved keys; below 7 forces Independent false. `SaveData.MarkSettingsChanged` is a silent no-op before program data is ready (`SaveData.cs:50-57`). A config can travel to a machine with a different topology.

Why `PERF.Cpu.*` lines are missing from captures: sources starting with "PERF" or "CPU" are recorded only when `IsPerfLog` / `IsCpuLog` are on, INFO only when `IsInfoLog` is on (`LogsDashboard.cs:1163-1179`); `IsInfoLog` and `IsCpuLog` default off (`LoadData.cs:443-452`); `LogDiagnosticSnapshot` (`CpuAffinityService.cs:635-701`) writes the topology line once per process and the plan line only when the plan changes.

An ECAT core is pinned even when nothing fences it (`ApplyEcatEnvironment`, cs 1540-1566), unless Experimental (UNPIN_WHEN_UNFENCED) or Independent is on. That pinned-but-unfenced state is exactly the one measured on 18/09 (isolation off, ecat=C2, stalls in the send path). So whenever Auto turns isolation off it MUST also clear the ECAT and Ladder core assignments.

## 3. Evidence per output

| Output | Auto value | Evidence | Source |
|---|---|---|---|
| Camera reads: one distinct physical core each, never on a realtime core | on when the gate passes | measured (337 reads) | CODEX_HISTORY.md:15487-15508 |
| Camera reads on E-cores | on | reasoned; I093 not verified on hardware | cs:2024-2031 |
| Realtime isolation | on when the gate passes | reasoned + field captures, no on/off A/B | CODEX_HISTORY.md:13564-13570 |
| ECAT on an E-core, 2P+8E | C11 | measured, small and observational (C11: 0 drops / 16.4 min, 3 captures; C2: 28 drops / 78.5 min, 5 captures; paired 3.2 min/side: drops 2 to 0, WKC error 49.3% to 4.9%) | session doc 2026-09-21:721-777 |
| ECAT on an E-core, every other two-class hybrid (user decision D1) | slot A | REASONED, UNMEASURED | this plan |
| Ladder on an E-core away from ECAT, 2P+8E | C7 | measured, confounded | CODEX_HISTORY.md:15850-15851 |
| Ladder on other hybrids ("top of the lower half" of the E list) | slot B | reasoned; only reproduces the measured C7 | this plan |
| Independent | off | never tested | Config.cs:123-130 |
| Experimental | off | contradictory | Config.cs:135-143 |
| ESC counters | off | measured negative | CODEX_HISTORY.md:15808-15822 |
| Fence boot core | off | measured negative, confounded | CODEX_HISTORY.md:15824-15857 |
| RX timeout | Auto (0) | reasoned | CODEX_HISTORY.md:15896-15899 |
| Freeze WKC / Read watchdog | off / 0 | fix not verified; safety trade-off | Config.cs:167-187 |
| Cyclic hard pin `BEE_ECAT_CYCLIC_HARD_PIN` | never set | measured negative (R253) | cs:1519-1522 |
| Reserved keys | default list, `Explicit=false`, no EcatIoPoll | camera fence measured; EcatIoPoll measured 13% p95 | LoadData.cs:424-428; cs:1047-1061 |

Label rule (added after the r1 review): the "measured" evidence label may be shown only for a two-class 2P+8E result where ECAT is needed and rule E applies, and its text must say "layout measured with Experimental on; this option set (Experimental off) is not yet verified on hardware (H1)". Every other 2P+8E result (Ladder only, no realtime task) is labelled reasoned. The label appears in the r3 confirmation dialog.

Rejected before, do not repeat: cyclic hard pin; `FORBID_MASK` as default; periodic ESC log as default; removing C0/C1 (boot core) from the process default CPU set; Ladder hard pin through the shared `EnterCurrentThreadCore` (R239 regression).

## 4. Decision policy

Pure function `BeeCpu.CpuAutoPolicy.Decide(CpuAutoPolicyInput)` (no I/O, no statics, deterministic, independent of input order).

Inputs: topology entries (+ `IsFallbackTopology`, NUMA and last-level-cache indexes); `HardwareKnown` (false when `Global.Comunication` is null); `NeedsEcat`, `NeedsLadder` (existing predicates); `CameraCount` clamped to 1..2 as Auto Tune does (cameras 3-4 are left to today's automatic selection with a warning); processor name (display only, never an input to the policy).

Derived: physical cores N; non-boot physical cores B; P count; E count; SMT per core; number of distinct efficiency classes; realtime task count R (ECAT and/or Ladder); camera count K.

Checks, in order:
1. Unsupported -> no change at all, with a reason code. Any of: topology API fell back, more than one processor group, more than 64 logical CPUs, more than one NUMA node, three or more efficiency classes, or `HardwareKnown = false` ("hardware configuration not loaded").
2. Below gate: B < K + R, or ECAT needed and N < 4 -> safe-degraded state: isolation off, camera affinity off, ECAT/Ladder/camera assignments -1, every option at its default. UI text: "This PC cannot fence realtime cores. EtherCAT/Ladder run on the Windows scheduler. E-804 risk under camera load." (reasoned)
3. Gate passes -> isolation on and camera affinity on. Reserved keys = default list with `Explicit = false`. Tool locks that conflict with the plan reset to Auto (existing `GetAutoTuneToolConflicts`). EcatIoPoll and the vision pool computed exactly as `CompleteAutoTunePlan` (1707-1718) does today.
4. Core rule:
   - Two-class hybrid CPU (exactly two efficiency classes): RULE E.
   - Non-hybrid CPU or single-class hybrid (some AMD parts): RULE P = today's `FindRecommendedCore` order, unchanged.

RULE E (D1):
- E list = the first logical CPU of each non-boot E physical core, in core-number order, ordinals 0..e-1. If the Windows boot core is an E-core, its whole physical core is removed before numbering (on Intel hybrids the boot core is a P-core, so nothing changes).
- Slot A = E ordinal e-1 (the highest-numbered non-boot E-core). Slot B = E ordinal max(0, floor(e/2) - 1) ("top of the lower half of the E list"; chosen only because it reproduces the measured C7 with nothing but core ordering; no vendor cluster assumption).
- ECAT and Ladder both needed: ECAT -> slot A; Ladder -> slot B if e >= 2; if e = 1 Ladder spills to rule P (highest-numbered free non-boot P-core).
- Only one of them needed: it takes slot A.
- Cameras: lowest free E ordinals; when E-cores run out they spill through today's `preferEfficiency` order (ascending free P-cores).
- Non-boot P-cores stay in the vision pool unless a spill takes one. No minimum number of E-cores is kept for vision. The existing optional-fence floor (vision pool >= 2 physical cores, `CpuAffinityService.Vision.cs:281-298`) still decides whether camera cores are fenced out of the pool (optional fences are dropped in reserved-key order, camera 0 before camera 1); Auto warns whenever it drops a fence.
- Never place a realtime task on the boot core or its SMT sibling; ECAT and Ladder on different physical cores; each camera on its own physical core not shared with ECAT or Ladder.

Recomputed layouts (derived by hand; the harness must confirm them):

| Class | Rule | ECAT | Ladder | Cam0 / Cam1 | Vision pool (mask) | EcatIoPoll | Rule-P layout (comparison) | Evidence |
|---|---|---|---|---|---|---|---|---|
| 2P+8E, 12 logical / 10 physical (P = C0-C3, E = C4-C11) | E | C11 | C7 | C4 / C5 | C2-3, C6, C8-10 (`0x74C`) | C2 | C2 / C11 / C4,C5, `0x7C0` | measured layout (3.2 min, Experimental was on) |
| 4P+6E, 14 / 10 (P = C0-C7, E = C8-C13) | E | C13 | C10 | C8 / C9 | C2-7, C11-12 (`0x18FC`) | C2 | C6 / C4 / C8,C9, `0x3C0C` | reasoned, unmeasured; no machine known |
| 4P+8E, 16 / 12 (P = C0-C7, E = C8-C15; the user's screenshot machine) | E | C15 | C11 | C8 / C9 | C2-7, C10, C12-14 (`0x74FC`) | C2 | C6 / C4 / C8,C9, `0xFC0C` | reasoned, unmeasured |
| 2P+4E, 8 / 6 (P = C0-C3, E = C4-C7) | E | C7 | C5 | C4 / C6 (cam1 fence dropped, warning) | C2-3, C6 (`0x4C`) | C2 | C2 / C7 / C4,C5, `0x60` | reasoned, unmeasured |
| 2P+4E, 1 camera | E | C7 | C5 | C4 | C2-3, C6 (`0x4C`) | C2 | - | reasoned |
| 2P+8E, Single Program + Word, 1 camera (R = 0) | E, no realtime task | - | - | C4 | C2-3, C5-11 (`0xFEC`) | C2 | identical | same as today (I9b) |
| Two-class hybrid, Word + ECAT (no Ladder) | E | slot A | - | lowest E-cores | - | - | - | reasoned |
| non-hybrid 8 cores / 16 threads | P | C14 | C12 | C2 / C4 | C6-11 | - | same | untested |
| non-hybrid 6 cores / 12 threads | P | C10 | C8 | C2 / C4 (camera 1 fence dropped: FenceDropped warning; the pool keeps exactly 2 physical cores - erratum, it is not "under 2") | - | - | same | untested |
| non-hybrid 4 cores / 4 threads, ECAT + Ladder, 1 camera | P | C3 | C2 | C1 | 1 physical core -> warning | - | same | untested |
| non-hybrid 4 cores / 4 threads, 2 cameras | below gate | -1 | -1 | -1 | whole CPU | - | - | reasoned |
| 2 cores / 4 threads, ECAT needed | below gate | -1 | -1 | -1 | whole CPU | - | - | reasoned |
| 2 cores / 4 threads, Single Program + Word, 1 camera | P (passes today's gate) | - | - | C2 | 1 of 2 physical cores | - | same | status quo (D6) |
| API fallback, multi-group, multi-NUMA, three-tier | unsupported | no change | | | | | | - |

Additional golden cases (derived): synthetic 6P+2E (14 logical / 8 physical): ECAT C13, Ladder C12, cameras spill to C2 / C4, pool `0xFC0`. Synthetic 8P+1E (17 / 9): ECAT C16, Ladder spills to C14, cameras C2 / C4, pool `0x3FC0`. Boot core is an E-core (E C0-C3, then 4P with SMT): C0's physical core excluded; ECAT C3, Ladder C1, cameras C2 / C4.

What runs on the P-cores under rule E (reasoned): every non-boot P-core joins the vision pool, so heavy ONNX / Learning / single-thread barcode work mostly lands on P-cores. Likely benefit: tool throughput (reasoned). Risk: ECAT and Ladder now run on the slowest core class; timing headroom (cyclic scan p50 58 us on a 1000 us budget) is measured only on 2P+8E; E-core clock and power-state behaviour under Windows power management is unmeasured; heavier P-core load raises heat.

## 5. Option values Auto writes

Isolation and camera affinity: on when the gate passes, off below the gate. Independent, Experimental, ESC counters, Fence boot core, Freeze WKC: always off. RX timeout: always 0 (Auto). Read watchdog: always 0. Auto never sets the cyclic hard pin. Auto never adds EcatIoPoll to the reserved keys.

## 6. Invariants

- I1: no ECAT, Ladder or camera assignment on the Windows boot physical core or its SMT sibling (cs:1942-1944).
- I2: ECAT and Ladder on different physical cores; each camera has its own physical core, not shared with ECAT or Ladder (cs:1918-1926).
- I3: no hard mask through `EnterCurrentThreadCore`; the Ladder hard pin is only set through `OwnsCoreExclusively`; that code is not touched.
- I4: Auto never writes `BEE_ECAT_CYCLIC_HARD_PIN`.
- I5: Auto never turns on Independent, Experimental, ESC, Fence, Freeze or Read watchdog; RX timeout is always 0.
- I6: Auto never produces an ECAT that is pinned but unfenced. Either the gate passes and isolation is on with ECAT in the reserved list, or ECAT and Ladder assignments are -1.
- I7: the process default CPU set is never the vision pool and keeps the boot core; `ApplyRuntimeIsolationCore` is not changed.
- I8: EcatIoPoll is never added to the reserved keys.
- I9 (narrowed by D1): Auto's core output equals today's Auto Tune output only for (a) non-hybrid and single-class CPUs; (b) any configuration with R = 0 (for example Single Program + Word); (c) camera placement on hybrids with E >= 2K + 2 (for K = 2, E >= 6). The vision-pool, reservation and EcatIoPoll logic stays unchanged code; only its inputs change.
- I10: ECAT reconnects only if a latched `BEE_ECAT_*` value changed or the runtime affinity is mismatched, and only after operator confirmation while not in Run mode (v-r4: never in Run mode; see section 13).
- I11: the UI thread never waits on `RuntimePolicyGate`, and every wait is bounded.
- I12: pressing Auto twice gives no change, no reconnect and no save.
- I13: upgrading never turns Auto mode on and never changes a CPU field silently.
- I14: no native ABI change, no `Ds5Api.h` edit, no `EtherCatApiVersion` bump. New Config fields are `[OptionalField]` primitives or strings only. No namespace changes.
- I15: only group 0 and at most 64 logical CPUs are supported; anything else gets no plan.
- I16: C# 7.3. Events always `-=` before `+=`. No new `PropetyTools[` call sites. No new static state in the UI layer.
- I19: on a two-class hybrid, when ECAT is needed it always gets slot A.
- I20: rule E never places a realtime task on a P-core while a free non-boot E-core is available, apart from the spill cases in section 4.
- I21: rule E never changes the gate decision; the below-gate result is identical to rule P (the gate only counts cores).
- I22: the ECAT rule used ("E", "P" or "E-spill") and the evidence label for the CPU class are written to the plan journal and log line, and shown in the confirmation dialog.

## 7. UX (revision r3)

- One primary "Auto" button next to a status label: `Auto (policy v1) - matches this PC` / `Auto - stale: planned on another CPU or hardware mode` / `Manual` / `Auto unavailable: <reason>`. The label also shows the CPU model name (display only).
- Pause, Module threads only and Interval stay visible. Every other control moves into a collapsible "Advanced" area, collapsed by default. While Auto mode is on: all Advanced controls, Auto Tune Cores, Clear Core Locks, Core reservation and the system-task items in the core context menu are disabled and marked "Auto-managed"; a "Switch to manual" link turns Auto mode off without changing anything at runtime; ECAT Settings (retry and delay) stays enabled. Every manual handler also returns early when Auto mode is on. The legacy Auto Tune Cores button stays in Advanced as the rule-P fallback (D7).
- One confirmation dialog in plain language, for example: `ECAT->C11 (E-core), Ladder->C7 (E-core), Cam0->C4, Cam1->C5. Turned off: Experimental, ESC counters, Fence boot core, Freeze WKC (watchdog 0). RX timeout Auto. Tool locks reset: N. ECAT reconnect: YES - the axis stops, and the line must be in Edit mode.` Each "turned off" item carries a one-line reason; warnings are listed (spill, fence dropped, below gate). The dialog and summary state the ECAT rule and the evidence label, for example "ECAT on E-core: measured on 2P+8E only; not yet verified on this CPU (4P+8E)".
- Blocked states refuse with an explicit message (v-r4: Run mode is NOT a blocked state any more, see section 13): hardware not loaded, unsupported topology, busy timeout, program data not ready.
- "Revert" is enabled after an apply in this session; it restores the snapshot through the same apply pipeline and sets Auto mode off.
- Persistence: Auto mode, the topology signature and the needs signature are saved. At startup only the topology signature is compared (needs are unknown during `LoadData`); a mismatch sets "stale", writes a WARN line and a journal entry, and never re-plans silently (D4). A needs-signature mismatch is detected when the panel is shown, and when `EnsureRecommendedRuntimeAssignments` changes a key while Auto mode is on.

## 8. Apply flow and threading (r2 for the service part, r3 for the UI part)

1. UI thread: check busy flag, `Global.Config` present, `Comunication` loaded, and not in Run mode (`!Global.IsRun`; confirm this predicate; v-r4: the Run-mode condition was dropped, see section 13). Otherwise refuse with no waiting loop.
2. Snapshot the inputs on the UI thread and run `Decide` (microseconds, at most 64 entries). Map the result to a `CpuAutoApplyPlan`: target fields, assignments, reserved list, conflicts, summary.
3. No-change check: compare field by field, plus the environment snapshot, plus `IsEcatRuntimeAffinityMismatch`. If nothing differs, only set the Auto-mode flag and signatures if not already set, save, and say "already on Auto plan". No reconnect.
4. Confirm, then capture the revert snapshot in memory in BeeGlobal: all option fields, `CpuReservedTaskKeys` and `CpuReservedTaskKeysExplicit`, a deep copy of `CpuAffinityAssignments`, the old value of each reset tool, the Auto fields, and the config/project owner references.
5. `await Task.Run(() => CpuAffinityService.TryApplyAutoPlan(config, plan, toolResetsUnderGate, budgetMs: 5000))`: loop on `Monitor.TryEnter(RuntimePolicyGate, 100)`; inside the gate check that the owners have not changed and `IsVisionRuntimeIdle`; take a `BEE_ECAT_*` environment snapshot, write the fields, call `SetConfiguredCore`, reset tools, call `ApplyRuntimeIsolation` and `ApplyEcatEnvironment`, take a second environment snapshot; leave the gate. All-or-nothing: on budget exhaustion nothing has changed and the UI reports busy. The 5 s budget only bounds how long the UI waits; it does not tune any control loop. (Assumption A2: `RefreshToolSchedulerForCurrentLoad` and `SetConfiguredCore` -> `ReapplyRegisteredThreads` can run on a thread-pool thread while holding the gate; verify, otherwise move that step to the UI continuation outside the gate.)
6. Back on the UI thread: `MarkSettingsChanged`, plus `MarkProgramChanged` if tools were reset. Detect the "program data not ready" no-op in `SaveData.cs:50-57` and warn.
7. Reconnect: `IsEcatReconnectRequired(envBefore, envAfter, runtimeMismatch)` is a pure function. Reconnect through the existing `ReconnectActiveEcatForAffinityAsync("CPU Auto")` only if ECAT is connected; otherwise show "applies at next ECAT connect" (`ParaProtocol.cs:4864` applies the environment on every connect attempt).
8. Logging: an append-only, size-bounded journal `Common\CpuAutoPlan.log` (absolute path from the config base, last ~50 entries), written at apply, at startup evaluation and at revert; a dashboard line at WARN level whose source does not start with "CPU" or "PERF" (so it survives default filters); a forced `LogDiagnosticSnapshot` at apply and on every `ApplyEcatEnvironment` call so the topology and plan lines appear at the start of every capture when `IsPerfLog` is on.

## 9. Files and symbols

Revision r1 (BeeCpu + harness only; no BeeGlobal edit):
- `BeeCpu/CpuTopologyEntry.cs`: add `NumaNodeIndex` and `LastLevelCacheIndex` (offsets 17 and 16 in the SYSTEM_CPU_SET_INFORMATION struct).
- `BeeCpu/CpuTopology.cs`: add `IsFallbackTopology` and `DetectionError`, set in `DetectTopology` at 229-247; add a cached `GetProcessorName()` (registry `ProcessorNameString`, display only). Existing detection behaviour must not change.
- New `BeeCpu/CpuAutoPolicy.cs` (add to `BeeCpu.csproj`): `CpuAutoPolicy.Version = 1`, `Decide(CpuAutoPolicyInput)`, `BuildTopologySignature`, `SelectRealtimeSlots(eList, needsEcat, needsLadder)`, `UsesRuleE(CpuAutoTopologyClass)` (single per-class selector so taking a class back to rule P is a one-line change plus golden updates), enums `CpuAutoOutcome` / `CpuAutoTopologyClass`, class `CpuAutoPolicyResult` (including `EcatRule`, `EvidenceLabel`, warnings, reason code, chosen options, assignments, pool mask).
- In the same file: `CpuCoreRanking.FindRecommendedCore(items, used, preferEfficiency)` and `GetWindowsReservedPhysicalCores(items)`, extracted VERBATIM from `CpuAffinityService`. The private methods in `CpuAffinityService` delegate to them in r2 (not r1).
- Harness under `artifacts/O096/harness` (see section 10).

Revision r2 (BeeGlobal glue):
- `BeeGlobal/Config.cs`: `[OptionalField] bool CpuAutoModeEnabled`, `string CpuAutoTopologySignature`, `string CpuAutoNeedsSignature`, `int CpuAutoPolicyVersion`; bump `CpuAffinityConfigVersion` to 8.
- New partial `BeeGlobal/Diagnostics/CpuAffinityService.Auto.cs` (add to `BeeGlobal.csproj`): `PlanAuto`, `IsAutoPlanCurrent`, `TryApplyAutoPlan`, `CaptureEcatEnvironmentSnapshot` (every `BEE_ECAT_*` variable), `IsEcatReconnectRequired`, `CpuAutoRevertSnapshot`, `EvaluateAutoModeAtStartup`, `MigrateCpuAffinityConfig(Config)`.
- `MigrateCpuAffinityConfig` holds the migration steps from `LoadData.cs:409-440` moved verbatim, plus a new step for `< 8` that sets the Auto fields to off / null / 0.
- `LogDiagnosticSnapshot(reason, force)` overload; stale-flag hook in `EnsureRecommendedRuntimeAssignments` (its fill logic unchanged); `CpuAffinityService` private ranking methods delegate to `CpuCoreRanking`.
- `BeeCore/Data/LoadData.cs` calls `MigrateCpuAffinityConfig`, then `EvaluateAutoModeAtStartup`, after line 442.

Revision r3 (UI): `BeeInterface/Diagnostics/MonitorPanel.cs`: `BuildLayout` adds the Auto button, status label, Revert button and Advanced container; new `OnAutoClicked`, `OnAutoRevertClicked`, `OnSwitchToManualClicked`, `ApplyAutoModeUiState` (called from `SyncAffinityOptionsFromConfig`); early returns in the existing handlers; guard system-task items in `ShowCoreContextMenu`.

Deliberately unchanged: `AutoTuneAssignments` and the Auto Tune button's behaviour (legacy, disabled while Auto mode is on), `ApplyRuntimeIsolationCore`, `ApplyEcatEnvironment` logic, all native code.

Out of scope: `ParaProtocol.cs`, `HardwareLadderCoordinator.cs`, `KvMl16vUnitBus.cs`, `BeeEcat/*`, `EtherCatCommunication/*`, `Camera.cs`, `View.cs`, `PropetyTool.cs`, `SaveData.cs` (called, not edited), `ResourceMonitorService.cs` (separate optional task optional-follow-up-A), `Program.cs` (separate optional task optional-follow-up-B), `Pattern*`, ECAT Settings / KV-ML16V watchdog tolerances. `BeeGlobal/Diagnostics/GcDiagnosticsSampler.cs` is dirty from unrelated work: never touch or stage it.

## 10. Verification

- V0 static: no C# 8+ syntax (no `??=`, switch expressions, `using var`, `is not`, ranges); `bash tools/check_propety_tools.sh` (pre-existing violations in other files are not this task's concern; this task must add none); every event subscription `-=` before `+=`; `git diff --name-only` within scope.
- V1 build: forced `/t:Rebuild` (NOT incremental; an incremental build that skips CoreCompile proves nothing) of the projects touched by the revision, logs under `artifacts/O096/build/*.log` (verbatim, grep for errors, never print raw). Invoke MSBuild through Git Bash with `MSYS_NO_PATHCONV=1` (otherwise `/t:` is mangled). Do NOT rebuild `BeeLicense.dll` into Release. Full solution build only when the revision touches BeeGlobal or above.
- V2 isolated harness (`artifacts/O096/harness`, following the `artifacts/O092/r2-r3` precedent): built into `artifacts/O096/isolated`; NEVER run `tests/BeeCore.Persistence.Tests` (it hijacks `bin/Release` and orphans LocalDB); write nothing to `bin/Release`; no LocalDB.
  - Fixtures. Hybrid: F1 2P+8E (exact 18/09 `PERF.Cpu.Topology` layout), F2 4P+6E, F3 4P+8E, F11 2P+4E. Non-hybrid: F4 8c/16t, F5 8c/8t, F6 16c/32t, F7 6c/12t, F8 4c/4t, F9 4c/8t, F10 2c/4t. Unsupported: F12 three-tier (6P+8E+2 low-power E), F13 API fallback, F14 multi-group (72 logical), F15 two NUMA nodes, F16 one logical CPU. Synthetic: 6P+2E, 8P+1E, boot-core-is-E.
  - Needs matrix per fixture: ECAT+Ladder with 2 cameras; ECAT+Ladder with 1 camera; ECAT only; none with 1 camera; hardware unknown.
  - Assertions: A1 cores distinct as required by I2; A2 boot core and its sibling never used; A3 only primary logical CPUs assigned; A4 gate outcomes; A5 the golden values in section 4 exactly (including pool masks, spill warnings, EcatIoPoll core); A6 oracle test: a verbatim copy of today's `FindRecommendedCore` / Auto Tune ordering equals the extracted ranking over every fixture and seeded random used-sets; policy output equals the oracle Auto Tune output only for non-hybrid and single-class fixtures and for hybrid fixtures with R = 0; camera equivalence asserted only where E >= 2K+2; A7 option values constant; A8 unsupported inputs produce no change and a reason code; A9 idempotent; A10 deterministic and independent of input order; A11 signatures stable and distinct; A12 (r2) migration from configs at versions 4, 5, 6 and 7 through a real BinaryFormatter round-trip: old semantics preserved, version becomes 8, Auto fields off; A13 (r2) new fields survive a round-trip (optionally a config saved by the new build loads in a harness linked against the pre-change `BeeGlobal.dll`); A14 (r2) truth table for `IsEcatReconnectRequired`; A15 (r2) apply then revert restores every field; A16 synthetic 6P+2E and 8P+1E golden cases; A17 boot core is an E-core; A18 for every e >= 2 slot A != slot B, for e = 1 Ladder spills to P and never shares ECAT's physical core; A19 gate decision under rule E equals rule P on every fixture; A20 I19 and I20 hold on shuffled input; A21 `EcatRule` and `EvidenceLabel` present in every result.
- V3 UI smoke, no hardware (r3): open the app, load a sample project, run one still-image inspection. Auto in Edit mode without ECAT: plan shown, "applies at next connect", Advanced disabled, Revert works, UI stays responsive; Run mode (v-r4): Auto applies unless it needs an ECAT reconnect while ECAT is connected, then it refuses and changes nothing. If this cannot be run, say so explicitly.
- V4 hardware acceptance (owed; build-pass is NOT acceptance):
  - H1 2P+8E rig: start from the screenshot-style configuration (four gold options on), press Auto; expect `CyclicResidency pinnedCpu=11 adaptiveSpin=0 stagedOutput=0 forbidMask=0x0`, Ladder `reservedCore=C7`, `visionMask=0x74C`; 20-30 min production capture recording drops per minute, AL 0x001B, WKC error %, TX/RX p50/p99, jitter p50/p90, E-804 count; compare with C11 (0 drops / 16.4 min) and C2 (28 / 78.5).
  - H2 (optional): paired A/B rule P vs rule E on the rig, one variable, 20-30 min per side.
  - H3 4P+8E, MANDATORY and paired: 20-30 min on rule P (Auto Tune layout C6 / C4), then 20-30 min on rule E (C15 / C11), changing only that variable.
  - H3b 4P+6E: mandatory before Auto is used on any such machine; if none exists the class keeps the "unverified" label. H3c 2P+4E: only if such a machine exists, same paired protocol.
  - H4 exactly one reconnect on an environment change; second press gives no reconnect; ECAT disconnected gives a deferred latch. H5 Freeze WKC turned off where it had been on: watch for E-804 during camera reads. H6 below-gate machine reports `pinnedCpu=unpinned`; check cycle time. H7 Revert brings back the earlier `CyclicResidency`. H8 a config copied from another CPU shows "stale" and a journal line.
  - Capture protocol: turn on `IsPerfLog` and `IsCpuLog` first; confirm `PERF.Cpu.Topology` and `PERF.Cpu.Plan` appear after each connect; attach `Common\CpuAutoPlan.log`; record the CPU model and the Windows power plan; change one variable per capture.
- `CODEX_HISTORY.md` for each revision must say "not verified on hardware" and list H1-H8.

## 11. Risks, stop conditions, must-not-claim

- R1: measurements exist for one rig only. Rule E is "reasoned, unmeasured" on every hybrid class except 2P+8E, and on those classes Auto deliberately changes ECAT placement. By the user's decision D1 Auto acts as a trial there until H3 / H3b pass; the UI evidence label and the gates are the mitigation. The unexplained 19/09 to 21/09 regression (0.14% to 49% WKC error) means C11 may only be avoiding a rig fault; the ECAT thread was off its pinned core 80.7% of the time in the best capture.
- R2: turning off Freeze WKC or Experimental on machines where they were on may make E-804 or cycle-time behaviour visibly change. Freeze off is the safety-correct default, but production will notice.
- R3: a reconnect stops the axis. `EtherCatDevice.syncRoot` is one global lock, so a reconnect can stall other callers; unchanged from today.
- R5: moving the migration block could change load behaviour; A12 covers it plus a line-by-line diff review.
- R6: in manual mode `EnsureRecommendedRuntimeAssignments` and Auto Tune still recommend P-core-first, so on any hybrid they can pull a key away from the rule-E layout; the stale flag and log line catch this but do not prevent it.
- R7: the "before" environment snapshot may differ from what native actually latched if settings were changed earlier without a reconnect; only the ECAT core is caught then, via `IsEcatRuntimeAffinityMismatch`.
- R8: ECAT timing on an E-core under power-saving states or Thread Director scheduling is unmeasured; record the Windows power plan (operations note, not code).
- L2 cache sharing between ECAT, Ladder and the cameras is unknown and not controlled.
- Advisory from the r1 review (not implemented): a hybrid with a low-power E-core island that Windows reports as the SAME efficiency class (possibly Meteor Lake) would put slot A, and so ECAT, on that island. `LastLevelCacheIndex` is read but not used in any decision. A later policy revision should treat "E-cores span more than one last-level cache" as unsupported, or at least warn. Also, the harness smoke on a single-die Intel part reports NumaNodeIndex 0 and LastLevelCacheIndex 0 for every CPU, which is plausible but does not prove offsets 16/17; confirm against `GetLogicalProcessorInformationEx` or on a multi-CCX part.
- Task numbering: this task was first numbered O094 by mistake; O094 belongs to another task (process provenance). It is O096 now.
- Stop conditions: A1, A2 or A6 fails; the change needs a file outside scope, a native edit or a serialized-type change; any in-scope file is dirty; `RefreshToolSchedulerForCurrentLoad` or the Run-state predicate cannot be confirmed; a golden value cannot be reproduced by the rule as written; per class: a gate capture on rule E shows more drops than the paired rule-P baseline, or any E-804 the rule-P side did not show (revert that machine at once with Revert or Auto Tune Cores, and switch `UsesRuleE` off for that class in the next policy revision with a version bump and golden updates; a class with no paired baseline cannot pass its gate). Rollback options: Revert in the UI, `git revert` r3 -> r1, or set Auto mode off in config.
- This work must NOT claim: fewer drops, lower WKC errors or less jitter; that rule E is safe or better on any class other than 2P+8E; that class 4P+6E or 4P+8E is safe; that the "top of the lower half" Ladder rule is anything more than a way to reproduce C7; that P-core vision throughput improves; that the Monitor panel caused the reported UI freeze or the crash.

Assumptions: A1 `Global.IsRun == true` means Run mode and Edit mode is acceptable for applying Auto. A2 see section 8 step 5. A3 BinaryFormatter version tolerance ignores extra primitive members when an older build loads a newer config. A4 every hybrid that Windows reports as exactly two efficiency classes gets rule E; AMD Zen5/Zen5c may report a single class and fall to rule P; three-tier parts stay unsupported. A5 `ApplyEcatEnvironment` has no periodic callers (the 7 call sites found are per-connect or per-UI-action). A7 the order of E-cores by core number is stable across boots on the same machine.

## 12. Decision status

| ID | Topic | Status |
|---|---|---|
| D1 | ECAT core rule | Decided by the user (2026-09-24): E-core on every two-class hybrid, rule P elsewhere, with per-class gates and the per-class stop condition. The architect recommended E only on the measured 2P+8E shape; the user chose broader. |
| D2 | Auto never turns on Freeze WKC, Experimental, ESC, Fence or Independent | Approved at recommended default: never |
| D3 | Advanced area | Approved: collapsed, disabled while Auto mode is on |
| D4 | Re-plan when the CPU changes | Approved: warn plus one click in v1; no silent re-plan |
| D5 | Below-gate behaviour | Approved: isolation off and ECAT/Ladder cleared |
| D6 | 2-core Single Program + Word machine | Approved: keep today's behaviour until an A/B exists |
| D7 | Legacy Auto Tune Cores button | Approved: keep in Advanced as the rule-P fallback until all gates pass |
| D8 | Confirmation dialog | Approved: one confirmation per apply |

## 13. Corrections found while mapping r2 (2026-09-24, after r1 was committed)

These override the earlier text of this plan wherever they differ.

- **HardwareKnown (sections 4, 8):** `Global.Comunication` is a static initialiser (`Global.cs:719`) and is never null. Before a program loads it holds a default `Comunication` (BitWord run mode, PLC protocol), so `NeedsEcat` and `NeedsLadder` read false, not "unknown". "HardwareKnown = false when Comunication is null" can never trigger. There is no real "hardware loaded" flag. r2 defines `CpuAffinityService.IsHardwareConfigurationKnown()` conservatively: `Global.StartupPreparationReady && !Global.IsChangeProg && Global.Comunication != null && Global.Comunication.Hardware != null && Global.Comunication.Protocol != null`. Known limitation, to be recorded in the history: a project with no ECAT/Ladder looks the same as no project loaded, and Auto then plans with R = 0; the needs signature marks the plan stale when the hardware mode changes. r3 should only enable the Auto button when this predicate is true.
- **Blocked states (sections 7, 8):** the predicate for refusing Auto is `Global.Config == null`, `!Global.StartupPreparationReady`, `Global.IsRun` (Run mode; `Global.IsRun` is a plain static bool, defaults true, written only by `BeeUi/Unit/EditTool.cs` lines 598 and 652), `Global.IsChangeProg`, or `Global.PLCStatus == PLCStatus.Reconnect` (precedent: `FormProgramSync.CanTransferNow`, `FormProgramSync.cs:564-584`). Do NOT use `BeeCore.Common.IsRun`. The existing Monitor panel handlers check no Run/Edit state today; the guard is new.
- **v-r4 (2026-09-25, user request; supersedes every "Run mode refuses Auto" statement above, including sections 6 I10, 7, 8 step 1 and V3):** Auto is pressable in Run mode. `GetAutoBlockReason()` no longer contains `Global.IsRun`; the Run-mode guard moved to `GetEcatReconnectBlockReason()`, used only for the ECAT reconnect. ECAT is NEVER reconnected in Run mode. Because the running ECAT thread keeps its old core until the next connect while `ApplyRuntimeIsolation` protects the new configured core (the E-804 combination), the apply (and Revert) REFUSES, inside the gate and before any write, when `Global.IsRun`, ECAT is connected and the change would need an ECAT reconnect (ECAT core or a latched option differs); the reason is "needs an ECAT reconnect, which is never done in Run mode; switch to Edit mode." Run mode still applies when ECAT is not connected or when the plan needs no reconnect (isolation, pool and camera changes with the ECAT core and latched options unchanged). The 5 s bounded idle wait is unchanged, so in Run mode a busy vision runtime commonly ends as Busy.
- **Thread safety (assumption A2, resolved):** `SetConfiguredCore`, `ReapplyRegisteredThreads`, `ApplyRuntimeIsolation`, `ApplyEcatEnvironment` and `Global.RefreshToolSchedulerForCurrentLoad` contain no WinForms or `Invoke` and may run on a thread-pool thread while holding `RuntimePolicyGate`. Lock rule: never take `lane.SyncRoot` while holding the gate; `IsVisionRuntimeIdle` reads `lane.IsBusy` without locking. `SaveData.MarkSettingsChanged` / `MarkProgramChanged` are BeeCore, are `Interlocked` writes and are silent no-ops before program data is ready. BeeGlobal cannot call them: r2 returns data, r3 (UI) calls them.
- **Tool resets:** `PropetyTool.CpuCoreAssignment` is BeeCore. `TryApplyAutoPlan` therefore takes the tool resets as a caller-supplied `Action` executed under the gate; the UI supplies it.
- **Journal path (section 8 step 8):** NOT `Common\CpuAutoPlan.log`: `ProjectContainerStore.EnumerateWorkspaceFiles` packs every file under `Common` into `General.com`, so a journal there would travel with exported packages. Use `<AppDomain.CurrentDomain.BaseDirectory>\Logs\CpuAutoPlan.log` (precedent `LogsDashboard.cs:394`, `LadderScanEventLogger.cs:270-273`; the `Logs` folder is not packed). Bounded (last ~50 lines or 64 KB), never throws.
- **Log filters (section 2 text, section 8 step 8):** the dashboard filter (`LogsDashboard.IsLogEntryEnabled`, ~1304-1320) gates a source only when it is exactly `CPU` / `PERF` or starts with `CPU.` / `PERF.`; all other sources are gated by level (WARN survives with the default `IsWarningLog = true`; INFO is dropped unless `IsInfoLog`). `PERF.Cpu.*` lines need only `IsPerfLog`, not `IsCpuLog`. The Auto WARN line uses source `CPU Auto` (space, not category-gated) at WARN level.
- **Startup evaluation (section 7):** `LoadData.Config()` runs on a pool thread with `Global.Config == null` and `Global.LogsDashboard == null`, so any dashboard line is dropped there. `EvaluateAutoModeAtStartup(Config config)` therefore takes the config as a parameter, changes no field (I13), and writes only the file journal; the stale state is recomputed on demand by `IsAutoPlanCurrent` when the panel is shown.
- **Revert of assignments:** `SetConfiguredCore` never removes a list entry, and in-place `CoreIndex` writes do not invalidate the plan cache key (`PlanKey`, `Vision.cs:56-99`). The revert snapshot must deep-copy the list contents (including entries with -1) and restore through a `CpuAffinityService` method that replaces the list under `lock(Sync)` and bumps `runtimeAffinityPlanVersion`.
- **Backfill interplay:** `ApplyRuntimeIsolationCore` calls `EnsureRecommendedRuntimeAssignments` (cs:993), which can refill blank keys (for example CameraRead2/3 when `NumCamera > 2`) with today's P-first / prefer-efficiency logic. That is consistent with "cameras 3-4 are left to today's automatic selection" but must be stated in the confirmation warnings.
- **ECAT connected predicate:** there is no public `IsEcatConnected`. r2 exposes it as data: any device in `Global.Comunication.EnsureHardware().ActiveDevices()` whose `Protocol.TypeControler == TypeControler.ECatIO && Protocol.IsConnected`. The reconnect itself stays in the UI (`MonitorPanel.ReconnectActiveEcatForAffinityAsync`, which reconnects every ECAT device, connected or not; "only if ECAT is connected" is a new predicate). r2 returns `EcatReconnectRequired` and `EcatConnected` as data.
- **Environment snapshot:** exactly seven managed setters: `BEE_ECAT_ADAPTIVE_SPIN`, `BEE_ECAT_STAGED_OUTPUT`, `BEE_ECAT_UNPIN_WHEN_UNFENCED`, `BEE_ECAT_CYCLIC_FORBID_MASK`, `BEE_ECAT_RECEIVE_TIMEOUT_US`, `BEE_ECAT_CYCLIC_CPU_SET_ID`, `BEE_ECAT_CYCLIC_CPU`. `BEE_ECAT_CYCLIC_HARD_PIN` is never written.
- **Migration:** only the version block at `BeeCore/Data/LoadData.cs:409-440` (versions < 3, 4, 5, 6, 7) moves to `MigrateCpuAffinityConfig(Config)` in `BeeGlobal.Diagnostics`; it uses only BeeGlobal types. The two calls after it (`NormalizeAssignments`, `EnsureRecommendedRuntimeAssignments`) STAY in `LoadData.cs`. The load order stays: migrate, normalize, ensure recommended, then the new startup evaluation.
- **Verification builds:** the repo has an isolated mode (`/p:EasyVisionIsolatedVerification=true`, `BuildSupport/EasyVisionIsolatedVerification.props`, outputs under `artifacts/verification/`) that keeps a verification build from overwriting the live `bin\` launch folders; a non-isolated native build overwrote live DLLs once. Legacy `csc.exe` is C# 5 only, so the BeeCpu/harness builds rely on the Roslyn compiler that MSBuild 17 uses (`/langversion:7.3`). Prefer the isolated mode for r3 (BeeInterface) and for any native project. `new Config()` elsewhere gets the field initialiser (8) and Auto fields off.
- **Test seams for A12-A15:** split the r2 logic so most of it is testable without Win32 or `Global`: pure plan-to-config mutation, revert snapshot capture/restore, `IsEcatReconnectRequired`, environment snapshot diff, `MigrateCpuAffinityConfig`. `TryApplyAutoPlan` wraps them with the gate and the Win32 calls and is covered by build, review and hardware case H4. The r2 harness references the built `BeeGlobal.dll` (never writes to `bin/Release`).

Optional separate tasks, NOT approved and NOT part of O096: optional-follow-up-A (move `ResourceMonitorService.Sample` GPU/WMI work off the UI thread), optional-follow-up-B (crash diagnostics: restore unhandled-exception hooks in `Program.cs` lines 66/69 with an absolute log path; log an exit reason before `Process.Kill()` in `BeeUi/Form/Main.cs` `TerminateCurrentProcess`).
