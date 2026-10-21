# I092 — DS5 Continuous Relative Position Timeout Ride-Through

## Status

- Source implementation: complete.
- Native/managed build: not authorized.
- Executable and hardware-motion verification: not authorized.
- Interop status: partial until focused build and physical DS5 acceptance are completed.

## Objective

Prevent a healthy active Profile Position `MoveRelative` action from being interrupted by
`POSITION_SAFETY_ABORT` solely because the managed heartbeat exceeds the configured
`CommandTimeoutMs`. This intentionally permits only the already accepted Relative target to
finish; it does not permit unbounded command generation without the managed Ladder heartbeat.

## Locked Behavior

| Condition | Required behavior |
| --- | --- |
| Heartbeat timeout during healthy active PP Relative | Do not Quick Stop; continue the accepted target. |
| Heartbeat returns before target completion | Clear deferred-timeout state and continue rolling Relative targets. |
| Heartbeat remains absent through target completion | Finish the current target, perform controlled Servo OFF, and accept no new target. |
| Heartbeat returns after target completion | If Operation Enable, Servo ON, and `M350 Run_Axis` remain ON, start one new full Relative segment through the normal Ladder sequence. |
| WKC loss, link loss, Fault/Alarm, or externally asserted Forced Stop | Preserve immediate Quick Stop. Hold automatic Reset while the Ladder reset relay remains eligible; resume only after `ServoReady` returns and `Run_Axis` remains ON. |
| Hard stop interrupts a segment | Discard the old remainder and start a new full Relative segment. |
| `Run_Axis` becomes OFF | Never auto-resume; retain existing Position Halt behavior. |
| Absolute, CSP, or HOME | Preserve the existing command-timeout and safety behavior. |

`PositionAutoRecoveryRetries=3` remains limited to the existing no-progress Relative recovery.
It does not limit fault-reset holding. Persistent faults remain stopped because `ServoReady`
cannot return.

## Implementation

### Native DS5 state machine

- Keep deferred-timeout and held-after-target flags inside `Ds5Controller::AxisRuntime`; no
  native export, structure layout, API version, or managed P/Invoke contract changes.
- Defer the command-age Quick Stop only for active Profile Position `MoveRelative` when WKC is
  fresh, the drive is Operation Enabled and Remote, and Fault, Alarm, and Quick Stop are clear.
- Require a newer monotonic heartbeat before a deferred or held action can accept a new
  non-safety action. Existing stale action/heartbeat rejection remains active.
- If the accepted target completes while timeout is still deferred, mark the action terminal
  and transition to controlled Servo OFF. Do not synthesize another Relative target.
- Any loss of the eligibility conditions falls back immediately to the existing stale-command
  Quick Stop path.
- Remove the temporary PDO-layout error only after compact remap and layout re-read succeed.
- Retain a WKC safety error until WKC is fresh and every matching Quick Stop latch has recovered;
  clear only the same error text so unrelated later diagnostics are preserved.

### Managed Ladder unit bus

- Detect the deferred and held-after-target states from existing snapshot fields; no ABI field is
  added.
- Emit at most one warning per observed timeout event:
  `POSITION_COMMAND_TIMEOUT_DEFERRED` or
  `POSITION_COMMAND_TIMEOUT_HELD_AFTER_TARGET`.
- Include `State`, `CurrentRequest`, `CommandAgeMs`, configured `CommandTimeoutMs`, and a classified
  `StopCause` in Position diagnostics.
- Preserve the Ladder recovery owner. Fault, Alarm, and Forced Stop first select Quick Stop;
  the existing error-clear relay then holds `ResetFault` through the runtime's pending-reset
  suppression. No direct motion resume is issued from the safety handler.
- Keep hard-stop remainder discard, normal `M350` Position Halt, Relative look-ahead, and the
  three-attempt no-progress recovery unchanged.

## Excluded Files and Contracts

- `bin/Release/Common/Common.ladder`
- `tools/Generate-Ds5PositionLadder.ps1`
- R31601
- timeout configuration defaults
- Position scaling
- public DS5 ABI and P/Invoke structures

R31601 remains unused by the generated Ladder program.

## Acceptance Scenarios

1. A heartbeat gap longer than `CommandTimeoutMs` during healthy rolling Relative motion produces
   no Quick Stop and no `POSITION_SAFETY_ABORT`; the accepted target continues.
2. Heartbeat recovery before target completion clears deferral and permits normal look-ahead
   rolling.
3. Permanent heartbeat loss finishes only the current target, then disables the servo without
   accepting another target.
4. Recovery after target completion enables and starts one new full Relative segment only while
   Operation Enable, Servo ON, `M350 Run_Axis`, `R30804`, and Servo Ready gates permit it.
5. WKC loss, link loss, Fault/Alarm, and Forced Stop still Quick Stop immediately. Held reset does
   not create rapid reset-action submissions, and an uncleared fault cannot restart.
6. A hard stop discards the interrupted remainder. The later automatic Relative start uses the
   full configured segment.
7. PP Absolute, CSP, HOME, Halt, and the three-attempt no-progress recovery retain their existing
   behavior.
8. Diagnostics distinguish deferred timeout, held-after-target timeout, hard safety stop, and
   historical PDO-remap errors.

## Verification Gate

No build, executable test, deployment, or hardware motion was authorized for this task. Before
production use, explicitly authorize a focused native/managed Release x64 build and perform the
acceptance scenarios on the physical DS5 EtherCAT chain. This change is on a high-risk motion and
safety path and depends on the I089 WKC/DC-sync recovery foundation.
