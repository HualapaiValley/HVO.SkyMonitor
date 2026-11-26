# Motorized focus and virtual qualification (#1712)

[Issue #1712](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1712) adds
operator-directed focus sessions on top of the SDK-free motor contract and
deterministic simulator delivered in #1841. CameraAgent accepts an optional
singleton `IMotorizedFocuser` through dependency injection. The standard host
registers no device. The Focus page and sidebar report manual-only capability
on those installations. Physical motor and unattended qualification are separate.

## Acquisition and admission

The same authorized operator starts, observes and stops manual and motorized
sessions through `ManualFocusSessionCoordinator`. An absolute move or relative
step ends at its requested coordinate. Moves, steps, and autofocus hold normal
exposures from the first drain through final verification or recovery. This
can cost capture windows but avoids exposures at trial focus positions. The
Focus page discloses the hold. Scheduled work follows its ordinary admission
and cadence rules. Other preview and calibration exposures receive a conflict.

Each move and its associated preview share a bounded acquisition operation on
the pinned camera generation. The command gate is released between operations,
so operator pause, schedule and lifecycle changes remain effective. Releasing
the focus hold respects the current admission state. Stop, host shutdown,
lost observation, safety/operation deadlines and module withdrawal interrupt
the operation. An installer lifecycle claim or armed restart fences new work.
On failure or cancellation, the session retains its focus hold and attempts
to restore the initial motor coordinate on the pinned generation, even during
a lifecycle hold. Cleanup can run while capture is paused and is refused if
admission is unavailable. The restore uses `StopWaitTimeout`. If a move fails
or times out, the session awaits an abort inside the boundary with a fresh
stop timeout. The virtual device verifies the original optical position,
including backlash. If restoration cannot be
verified, normal admission stays closed until restart recovery. Before
restarting, the operator must resolve the disconnect or stall and verify
the intended focus. The simulator starts from its configured initial state.

## Measurement and evidence

The operator selects a positive step spacing. Five equally spaced positions
are centered near the starting coordinate. The first sample is at or above
the lower limit plus the full backlash allowance, and the fifth is at or
below the upper limit. Near the lower limit, the whole window shifts up to
preserve spacing and the approach from below. A sweep that cannot fit with
full take-up is rejected before sampling. The sweep tracks the
same star, and measures HFD from preview pixels using the manual-session
measurement. All five measurements must be valid and comparable, with
decreasing then increasing HFD around a distinct interior minimum.
Autofocus approaches the selected position from below again and requires a
sixth measurement below both neighboring widths before accepting it.

Preview exposure and gain are scoped to each call. With VirtualSky the
simulator's optical coordinate maps linearly over the declared simulated
preview range. A sweep writes no camera profile or normal capture. Session
history includes the request, initial limits and state, measured positions,
image and settings provenance, verified applied position or recovery outcome.
The operator can save even an interrupted sweep with zero samples, then export
the verified immutable record through the existing authenticated history route.

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
- Abort stops the current move immediately and returns its stationary state.
  The move reports `Aborted`. Abort while idle is idempotent. Caller cancellation
  stops its own move and cancels its task. Move and abort both return a cancelled
  task when the token is already cancelled at admission and leave any active
  move running.
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

## Physical qualification prerequisites and unattended verification

Qualify each supported motor, controller, firmware, driver and installed rig
combination on an attended test rig first. Record those identities, the tested
temperature and load range, the homing and position-readback method, the safe
travel envelope, and measured backlash in motor steps. Set software travel
limits inside the independently verified physical stops. Measure motion,
settling, abort and recovery times and verify that the configured deadlines
leave margin. Identify who can stop the test and how they can isolate motor
power without relying on CameraAgent.

Before approving unattended use, retain time-stamped traces and focus session
exports for these checks on the actual hardware.

- Verify homing and direction, both software limits, and full backlash
  take-up. Sweep near both limits, including an initial position less than
  one backlash allowance above the lower limit. Compare reported coordinates
  with physical position and confirm every approach stays inside safe travel.
- Repeat autofocus over the qualified temperature and load range. Set
  acceptance limits for HFD repeatability, position repeatability, settling and
  capture hold time before the run. Confirm the return exposure verifies the
  selected minimum and the active camera profile is preserved.
- Interrupt motion and preview with operator stop, deadline, camera withdrawal,
  lost observation, controller disconnect and stall. Verify abort stops motion
  within the recorded bound. Verify starting focus before capture resumes. If
  restoration is uncertain, confirm admission stays closed and an operator can
  identify and recover the fault.
- Exercise host restart and controller power loss during motion and between
  samples. Verify reconnect or reboot cannot resume old work, and require a
  known home or independently verified position before allowing new motion.
- Verify that ordinary exposures stay held through motion and recovery, an
  operator pause survives teardown, and a replacement camera generation never
  receives a cleanup move. Confirm the audit record identifies the outcome,
  limits, sampled positions, and whether restoration was verified.

Record the hardware and software revisions, procedure, measured results,
operator recovery steps and sign-off with the qualification evidence. Unattended
operation requires separate acceptance for the tested installation.
