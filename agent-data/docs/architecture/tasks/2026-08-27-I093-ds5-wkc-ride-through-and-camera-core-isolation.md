# I093 — DS5 WKC Ride-Through Tuning + Hybrid-CPU Camera Core Isolation

## Status

- Source implementation: complete (managed only; no native code change).
- Managed build: verified — BeeGlobal, BeeEcat, BeeInterface Release x64 compile with no new errors.
- Executable and on-hardware verification: NOT done. Requires the physical DS5 EtherCAT chain plus
  the 4P+6E production PC. Affinity and watchdog changes are on a motion-safety path.

## Trigger

`F:\logs.json` field diagnosis of repeated `POSITION_SAFETY_ABORT ... StopCause=WorkCounterLoss`
(operator sees this as **E-804 "mất kết nối"**) while two cameras capture and inspect at the same
time. Root cause: CPU starvation of the EtherCAT cyclic / Ladder threads.

Key log evidence:
- `PERF.Ladder.Cycle slow=1638ms input=1637232us` — EtherCAT input read stalled ~1.6 s.
- `PERF.ECAT.Runtime maxJitter=82205us errors=113..138` (cycle ~4 ms).
- `PERF.Camera.Read cam=1 affinity=C5 actual=C0/C2` — camera 2's assigned core is never honored;
  it spills onto the cores the realtime threads need. `cam=0 affinity=C2 actual=C2` is honored.

## Levers implemented

### Lever 3 — Configurable EtherCAT WorkCounter watchdogs (managed only)

The native `Ds5NativeControllerConfig` already carries `ProcessDataWatchdogMs` and
`MaxConsecutiveWorkCounterMisses`; only the managed side hard-coded the defaults (100 ms / 25).
Now surfaced end to end:

- `BeeGlobal/Hardware/KvMl16vMotionProfiles.cs`: added `ProcessDataWatchdogMs` (default 100) and
  `MaxConsecutiveWorkCounterMisses` (default 25) as `[OptionalField]`; Normalize restores defaults
  for older configs (0 → default); Validate bounds (8..2000 ms, 1..100 cycles); Clone + CopyFrom.
- `BeeGlobal/Protocol/ParaProtocol.cs`: pass both into `Ds5SharedMotionRuntime.ConfigureBeforeOpen`.
- `BeeInterface/PLC/KvMl16vCommissioningForm.cs`: two new header fields ("PDO WD" ms, "WKC miss")
  with tooltips; wired to working profile and Save. `.Import.cs` round-trips both.

Behavior unchanged until an operator raises the values (defaults identical to prior constants).
Raising them reduces abort frequency for short EtherCAT stalls but lets the drive hold its last
command longer on a genuine link loss — a real safety trade-off. Native miss counter is hard-capped
at 100 cycles (~400 ms at 4 ms cycle), so this lever cannot cover a 1.6 s stall; it only trims
residual short-jitter aborts. The stall itself is eliminated by Lever 1.

### Lever 1 — Camera read prefers E-cores on hybrid CPUs (managed only)

`BeeGlobal/Diagnostics/CpuAffinityService.cs`:
- `FindRecommendedCore` gained a `preferEfficiency` overload. ECAT/Ladder keep P-core-first
  (unchanged); `CameraRead0`/`CameraRead1` now recommend E-cores first, falling back to P-cores
  only when no E-core is free. On a non-hybrid CPU both orders collapse to DisplayIndex, preserving
  prior behavior. Representatives are the primary (lowest-index) logical of each physical core, so a
  recommendation never lands on an HT sibling.
- HT-sibling parking of the ECAT/Ladder cores is already structural: `ApplyRuntimeIsolation`
  reserves by physical-core key, so both HT siblings of a realtime core are excluded from the
  process-default CPU set — no extra code needed.

This fixes the auto-recommendation. The logged machine still carries a STALE MANUAL assignment
(cam1 = C5 = an HT sibling / excluded core), which `EnsureRecommendedAssignment` deliberately keeps.
That must be corrected in config (see Operator action).

### Lever 4 — Render load (investigated, no code change)

`camera.DrawResult` (~37–43 ms) runs on the **UI thread** (`View.ShowLaneResultCore`, marshalled via
Invoke). The UI thread is registered for affinity (`EditTool.cs` → `UiMain`), and the
`CollageRenderer` worker thread inherits the process-default CPU set. Once Lever 1 isolation is
active and correctly configured, both are confined to non-realtime cores, so render is NOT the
E-804 root cause — only a UI-responsiveness cost. A preview downscale from 5472×3648 is a possible
future optimization but was not needed for the fault.

## Operator action (no build required)

On the 4P+6E production PC, in MonitorPanel / Diagnostics:
1. Enable `EnableRealtimeCoreIsolation` and `EnablePerCameraReadAffinity`.
2. Assign ECAT and Ladder to distinct P-cores (even/primary logical indices); leave their HT
   siblings unassigned. Assign `Camera 1/2 ReadCCD` to two distinct **E-cores** (never an odd
   HT-sibling index, never a realtime core).
3. Save and reconnect EtherCAT. Confirm `PERF.Camera.Read cam=1 affinity=Cx actual=Cx` matches.

## Verification gate

No executable or hardware motion test was authorized. Before production use, run on the physical
DS5 chain: (a) two-camera simultaneous capture no longer produces `WorkCounterLoss` aborts with
cameras on E-cores; (b) raised watchdog/miss values behave as expected; (c) a real EtherCAT link
pull still Quick Stops promptly. Depends on the I089 WKC/DC-sync recovery foundation and the I092
timeout ride-through.
