# Virtual motorized focuser foundation (#1712)

This is the coordinator-authorized hardware-free slice of [#1712](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1712),
under the [bounded claim](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1712#issuecomment-6862021903).
It delivers the device contract and deterministic virtual motor only. The parent
issue remains open for registered-device UI, autofocus, session history/export
integration and separately tracked physical qualification. No device is registered
with the host, and no motion endpoint or unattended policy is enabled.

## Ownership

`IMotorizedFocuser` is an SDK-free AgentCore device contract. The virtual motor
lives in CameraAgent.Common beside the existing focus-session owner. It owns no
camera, image, profile, admission gate, session or durable state.

Future production use must go through the existing `ManualFocusSessionCoordinator`
ownership rules and the configured `CameraModuleOwnership` generation/lease,
inside `CaptureAdmissionCoordinator.ExecuteCaptureBoundaryAsync`. Movement and
its associated preview must complete inside the same exclusive boundary so a
normal exposure cannot observe an intermediate position. A caller must link
session cancellation, deadline, host shutdown and module revocation, await motor
termination before releasing the boundary, and never construct a second camera.
Do not hold the admission command gate for a whole autofocus session: #1017's
[revised ownership contract](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1017#issuecomment-5953317989)
uses bounded per-sample operations so pause/schedule commands remain usable.
The motor contract is not an authorization or session-ownership substitute.

The existing VirtualSky preview setpoint is a separate simulated optical model.
This slice does not connect a motor position to that model, mutate a profile,
change manual-session records or alter the retained Focus UI owned under #1110.

## Device and simulation semantics

- Positions and inclusive limits are nonnegative integer motor steps. Targets
  outside the limits are rejected before motion; they are never clamped.
- There is at most one move. A conflicting move is rejected rather than queued
  or allowed to abort the first move. State snapshots and terminal results are
  immutable, and each result carries the requested target and actual final state.
- Position is the motor's reported coordinate. `BacklashSteps` declares mechanical
  play, not automatic compensation. The simulator additionally exposes an optical
  position: on reversal, travel first consumes the remaining play. Partial
  reversals preserve the play already taken up. Initial play is engaged toward
  increasing positions. Optical and motor positions always stay within limits.
- A finite optional Celsius reading is telemetry only. Null means unavailable;
  temperature never moves the motor or changes an active profile.
- Simulation time advances only through `AdvanceBy`. Each complete configured
  interval moves up to the configured step count; a partial interval is carried
  forward within that move. New moves start with zero elapsed time. No wall-clock
  sleeps, timer, worker, random input or hardware is used. Large advances use
  constant-space arithmetic rather than iterating once per step.
- A no-op target completes immediately. A disconnected or stalled device returns
  that explicit outcome, including for a no-op. Fault injection during motion
  terminates immediately at the last completed interval's position. Clearing a
  stall or reconnecting permits a new explicit move; it never resumes old work.
- Abort stops the current move immediately and returns its stationary state;
  the move reports `Aborted`. Abort while idle is idempotent. Caller cancellation
  stops its own move and cancels its task. A token cancelled before admission
  changes nothing. Abort with a pre-cancelled token likewise changes nothing.
- Completion, abort, cancellation, stall and disconnect are serialized. The first
  terminal event wins. Old callbacks cannot stop a later move. No queued work or
  automatic retry remains after a terminal event, and late time advances cannot
  mutate a terminal result.
- State, play and temperature are in memory only. Constructing a fresh simulator
  resets them to declared initial values; there is no restart recovery claim.

Regression evidence must cover exact/partial delay, both limits, invalid input,
backlash and partial reversals, conflicts, pre/in-flight cancellation, idle and
moving abort, mid-move stall/disconnect, explicit recovery, stale callbacks,
immutable final snapshots, maximum travel/elapsed arithmetic and fresh instances.
Physical focus quality, driver timing and unattended safety remain unevidenced.
