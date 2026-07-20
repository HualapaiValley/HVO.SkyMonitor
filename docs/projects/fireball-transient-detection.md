# Fireball and Transient Detection Plan

## 1. Scope

This document defines the subsystem architecture for optional meteor, fireball,
satellite, aircraft, and transient detection. The umbrella work item is issue
[#65](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/65).

The authoritative status and execution order live in
[`docs/project-plan.md`](../project-plan.md). This document owns transient
behavior and invariants only. Runtime work starts only when its linked issue
meets the readiness rules in `docs/planning/agent-execution.md`.

The initial implementation is not a generic machine-learning platform. It starts
with deterministic image algorithms, measured heuristics, retained source
evidence, and reviewable reason-coded classifications.

## 2. Compatibility Constraints

Migration from the existing serial CameraAgent pipeline preserves configured
standard-step behavior while durable top-level lanes replace the in-memory
coordinator. It does not permit nested queues inside arbitrary processing
operations.

The rolling combiner and its linear-mean history remain separate from transient
history. Preview, annotation, combined artifacts, and event observations retain
their distinct roles and identities. A centered temporal detector is never
implemented as a synchronous step that waits for future frames in the standard
processing list.

## 3. Target Architecture

Capture produces one owned raw artifact. The system persists or safely accepts it
at ingress, then fans out small durable references to independent top-level
processing lanes:

```text
Physical or virtual camera
    -> raw capture ingress
    -> capture distributor
         -> standard artifact lane
         -> optional edge transient lane
         -> upload/export lane

LogicHost artifact ingest
    -> durable processing job
    -> central transient worker
    -> event records and derived artifacts
```

Top-level lanes are intentionally different from hidden queues inside arbitrary
filters. Each lane has one observable bounded coordinator. Stages within a lane
remain ordered and synchronous unless a reviewed requirement proves otherwise.

The raw payload is not cloned for each lane. Local lanes use an ownership-safe
lease or reload the durable artifact. Central workers use artifact identifiers and
object-storage retrieval rather than CameraAgent-local paths.

## 4. Capture Cadence and Auto Control

Physical continuous capture means no configured idle time after a completed
exposure. The next exposure begins after only acquisition-critical work:

```text
exposure/readout
    -> safe frame ownership
    -> enabled ROI or sparse metering
    -> bounded exposure/gain decision
    -> camera setpoint application
    -> raw ingress handoff
    -> next exposure
```

Cadence configuration distinguishes `Continuous` from
`MinimumStartInterval`. VirtualSky keeps a fixed simulated cadence by default
because it renders immediately and would otherwise run at unrestricted CPU speed.

Auto-control ownership distinguishes disabled, camera-native, and host-metered
operation. Host metering uses linear sky samples and excludes ground,
obstructions, permanent lights, saturated outliers, and image-circle exterior.
RGGB16 metering uses selected photosites or Bayer cells without full demosaicing.
`CaptureMeteringPolicy` supplies a sparse ROI, calibrated image circle, and
rectangular excluded mask regions; the meter honors configured 16-bit byte order
and counts only bytes read after those filters.

The controller adjusts from the active setpoint, applies hysteresis, stays within
the configured exposure/gain envelope, and records every decision reason. Exact
exposure start/end, readout, metering, setpoint-application, ingress-handoff, and
observed inter-exposure gap are retained. A `1-2 ms` metering objective must be
measured on physical x64 and ARM64 targets rather than treated as a portable
correctness threshold.

`CaptureTimingDescriptor` retains the requested deadline and module acquisition
boundaries. Optional `CaptureCycleEvidence` retains the host call, cadence start
reason, ownership, solar regime, sparse-meter counts, active-to-decided control
transition, handoff start, and observed gap. `MinimumStartInterval` deadlines are
computed with monotonic elapsed time from the preceding actual host start;
`Continuous` creates no cadence timer. Existing manifests without cycle evidence
remain valid and are not backfilled.
When automatic controls change, `ICameraSetpointController` applies the complete
next setpoint before durable ingress begins. Modules that cannot provide that
boundary cannot enable camera-native or host-metered ownership.

Issue [#58](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/58) owns this
work.

## 5. Raw Ingress and Processing Isolation

A bounded in-memory queue cannot guarantee nonblocking acquisition, no loss, and
bounded memory during indefinite overload. Optional processing must not block
normal acquisition, so durable pending work is the source of truth and in-memory
channels are wake-up accelerators only.

Ingress assigns stable capture and artifact identity, publishes immutable raw
payload and sidecar files, and commits discoverable SQLite WAL work only after
the required files exist. Successful ingress handoff is durable; no
acknowledged writer-queue crash window is accepted. Workers recover pending work
after restart and process lane references idempotently. Raw deletion requires
both retention eligibility and acknowledgement from every configured required
consumer.

If durable ingress itself cannot accept another frame, CameraAgent enters an
explicit unhealthy state and pauses or stops according to disk-pressure policy.
It never silently drops raw evidence. In-memory channels may accelerate wake-up
but are not authoritative state.

The standard lane is required. Upload is required when central operation is
enabled. The transient/secondary lane is optional by default; an operator may
make it required only with explicit retention and pressure consequences.

Issue [#59](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/59) owns this
work.

## 6. Reprocessable Central Artifacts

Central processing requires enough persisted information to reconstruct raw
pixels without consulting the current rig registration. Capture manifests and
normalized central records include:

- agent and stable capture sequence identity;
- frame and artifact identifiers plus source lineage;
- exact exposure start/end and capture/readout timing;
- dimensions, stride, pixel format, byte order, and content length;
- exposure, gain, temperature, and source metadata;
- capture-time rig, calibration, mask, sensor-recipe, and processing-profile
  versions;
- verified checksum and object-storage reference.

LogicHost provides authorized artifact retrieval and a durable idempotent job
model with lease, expiry, retry/backoff, quarantine, algorithm version, and
source references. Neighboring frames are selected by capture sequence, not
ingestion order, so delayed offline uploads remain valid.

Issue [#60](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/60) owns this
generic infrastructure.

## 7. Detection Model

The detector uses its own compatibility-aware history rather than the normal
rolling mean. A candidate centered on frame `N` may use:

```text
N-2  earlier classification/background context
N-1  earlier classification/background context
N    primary candidate
N+1  later classification/background context
N+2  later classification/background context
```

Five frames provide context; they are not automatically combined into the event
image. Most meteors are expected in one exposure, and boundary-crossing events
may occupy two. Longer persistence lowers meteor confidence but never determines
classification by itself.

Provisional edge detection is causal and cannot wait inside the standard frame
worker. It uses prior compatible frames and records a pending candidate. Final
validation runs after later frames arrive or a timeout is reached. A centered
background excludes every frame determined to contain event signal.

The initial detector operates on a linear Mono16 or RGGB16-derived luminance
representation and applies versioned sky, image-circle, horizon, obstruction,
bad-pixel, and saturation masks. It normalizes compatible exposure/gain changes
and handles star motion through registration, star masks, or another measured
conservative strategy.

The V1 window, normalization, mask encoding, persistent catalog-projected star
mask, background arithmetic, outcomes, and lineage are defined in
[`transient-temporal-background-v1.md`](../contracts/transient-temporal-background-v1.md).
The V1 residual qualification, saturation topology, component geometry,
candidate receipt, observation promotion, and deterministic assessment rules are
defined in
[`transient-extraction-assessment-v1.md`](../contracts/transient-extraction-assessment-v1.md).

Structured output includes a polyline, bounding region, width and brightness
profiles, saturation, fragments, measured features, confidence, reason codes,
source references, and algorithm/calibration/mask versions. An overlay is a view
of that data, not the authoritative result.

`Fireball` is a brightness/severity assessment of a meteor. Classification
families initially include meteor, satellite, aircraft, sensor artifact,
environmental artifact, and unknown.

Issue [#62](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/62) coordinates
the shared detector. Processing owns candidate/event/assessment contracts and
recipe-facing outcomes; Imaging owns pure pixel, mask, background, component,
and geometry algorithms.

## 8. Event and Artifact Model

A transient event spans one or more captures and does not belong inside the
one-capture, one-artifact-per-role `FrameArtifactSet`. The event model separately
tracks:

- stable event identity and agent;
- pending, provisional, validated, rejected, and needs-review state;
- one or more per-frame observations;
- source raw and background artifact references;
- classification assessments and feature evidence;
- detector, calibration, mask, and reconstruction versions;
- reviewer overrides and notification state;
- derived crop, preview, mask, overlay, and reconstructed-image references.

Raw frames remain the primary evidence. A reconstructed image combines a clean
background with positive event residuals only from event-bearing frames. It is
clearly marked as derived and cannot claim exact intra-exposure timing or
unrecoverable saturated photometry.

## 9. Edge and Central Execution

Supported operating directions are:

| Mode | Behavior |
| --- | --- |
| `Off` | No detector window, allocation, or processing |
| `Edge` | CameraAgent performs configured provisional and final analysis |
| `Central` | LogicHost analyzes stored raw artifacts |
| `Hybrid` | CameraAgent triggers quickly; LogicHost performs full-context validation |

Hybrid is the recommended production direction. The virtual-first milestone
proves all four software modes without selecting physical sensitivity or target
hardware budgets.
The edge lane keeps a restart-safe candidate journal and never waits for future
frames in the standard pipeline. Central jobs tolerate out-of-order ingest,
support historical reprocessing, and preserve prior algorithm assessments.

Issue [#63](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/63) owns edge
execution. Issue
[#64](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/64) owns central
validation, event persistence, reconstruction, and review.

## 10. Deterministic Simulation

VirtualSky supplies versioned, seeded scenarios for meteors, saturated
fireballs, fragmentation, satellite motion/flares/shadow entry, aircraft blink
patterns, exposure-boundary crossings, and sensor artifacts.

Sky events are projected and integrated into each overlapping exposure before
sensor response, vignetting, noise, CFA sampling, and quantization. Cosmic rays
and hot pixels are injected at the sensor stage. Simulated events appear in the
ordinary raw and preview outputs; end-to-end tests never inject a perfect private
line directly into the detector buffer or expose expected labels to production
detection code.

Issue [#61](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/61) owns this
work.

## 11. Dependency Order

The authoritative cross-system order is in the project plan. The transient
subsystem order is:

1. Complete reconstructable contracts (#92), shared recipes (#93), durable
   ingress (#94), lanes (#95), outbox/ingest/retrieval/workers/windows
   (#97-#101), and cloud masks (#105) as required by the target runtime.
2. Add deterministic transient scenarios in #61.
3. Complete the shared detector children coordinated by #62.
4. Add optional edge execution in #63 after hardware-neutral cadence #58 and
   durable lanes #95.
5. Complete central validation/persistence children coordinated by #64 after
   retrieval, worker, and window support.
6. Add event review in #107, then prove the complete path in #108.

Physical cadence, camera modules, ARM64 budgets, and retained real-event
evidence are later acceptance and do not block this software sequence.

## 12. Decision Ledger

Resolved platform decisions:

| Decision | Resolution | Owner |
| --- | --- | --- |
| Local durable state | Immutable payload/sidecar files plus SQLite WAL work state | #94 |
| Ingress acknowledgement | Required files and discoverable journal work are durable before success | #94 |
| Lane defaults | Standard required; upload required when central enabled; transient/secondary optional by default | #95 |
| Queue topology | Observable top-level lanes; no nested queues inside arbitrary operations | #95 |
| Event contract owner | Processing; pure detector image algorithms remain in Imaging | #62 |
| Virtual completion | Deterministic software evidence; physical sensitivity, false-positive, and ARM64 acceptance deferred | #65/#89 |

The epic must not silently resolve the remaining product choices during an
unrelated implementation:

1. Bright-fireball-only initial scope versus faint-meteor sensitivity.
2. Complete offline edge classification versus provisional offline preservation.
3. Backlog byte/age budgets and disk-pressure thresholds.
4. Exposure-first versus gain-first host-control defaults and ROI configuration.
5. Registration versus star-mask strategy for initial temporal subtraction.
6. Edge and central assessment authority and notification-update behavior.
7. Human-review workflow and confidence thresholds.
8. Aircraft and satellite data sources, licensing, availability, and offline
   behavior.
9. Source raw/event retention and later cross-agent correlation.

Each decision is recorded in the owning issue before its dependent runtime slice
is accepted.

## 13. Issue Index

- [#58 Continuous cadence and metering](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/58)
- [#59 Durable raw ingress and processing lanes epic](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/59)
- [#60 Reconstructable artifacts and central processing epic](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/60)
- [#61 Deterministic transient scenarios](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/61)
- [#62 Shared detector and event contracts epic](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/62)
  ([#113 contracts/inputs](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/113),
  [#115 compatibility/backgrounds](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/115),
  [#121 extraction/assessment](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/121),
  [#119 baselines](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/119))
- [#63 Optional CameraAgent transient lane](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/63)
- [#64 LogicHost transient validation and review epic](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/64)
  ([#116 central validation](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/116),
  [#118 reconstruction/review](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/118))
- [#65 Fireball and transient detection epic](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/65)
