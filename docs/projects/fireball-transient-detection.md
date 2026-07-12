# Fireball and Transient Detection Plan

## 1. Scope

This document prepares the architecture for optional meteor, fireball, satellite,
aircraft, and transient detection without moving that work ahead of the current
CameraAgent hardening and durable-ingest queue. The umbrella work item is issue
[#65](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/65).

The authoritative status and execution order live in
[`docs/project-plan.md`](../project-plan.md). This document defines dependencies
within the deferred fireball work; it does not promote those issues ahead of the
near-term queue. Fireball-specific runtime work remains deferred until its
dependencies and open product decisions are resolved.

The initial implementation is not a generic machine-learning platform. It starts
with deterministic image algorithms, measured heuristics, retained source
evidence, and reviewable reason-coded classifications.

## 2. Baseline Pipeline Constraints

The baseline entering this design uses one bounded in-memory channel and one
worker that runs every configured processing step serially for a capture context.
Issue #59 may replace that top-level coordinator only as described below; it does
not permit nested queues inside arbitrary processing steps.

The rolling combiner is the only component with frame history. Its private queue
creates a linear mean from raw Mono16 or RGGB16 frames. Other processing steps do
not receive that queue. Preview reads the current raw artifact, annotation draws
on that preview, and the combined frame remains a separate artifact.

Raw preservation is currently an ordered processing step rather than a mandatory
acquisition boundary. Processing order is configurable, but changing order does
not change the artifact explicitly selected by a step. There is no generic
overlay collection, external processing-job queue, multi-frame event model, or
central raw-artifact loader today.

These facts make a centered temporal detector unsuitable as another synchronous
step in the standard processing list.

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

The controller adjusts from the active setpoint, applies hysteresis, stays within
the configured exposure/gain envelope, and records every decision reason. Exact
exposure start/end, readout, metering, setpoint-application, ingress-handoff, and
observed inter-exposure gap are retained. A `1-2 ms` metering objective must be
measured on physical x64 and ARM64 targets rather than treated as a portable
correctness threshold.

Issue [#58](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/58) owns this
work.

## 5. Raw Ingress and Processing Isolation

A bounded in-memory queue cannot guarantee nonblocking acquisition, no loss, and
bounded memory during indefinite overload. Optional processing must not block
normal acquisition, so durable pending work is the source of truth and in-memory
channels are wake-up accelerators only.

Ingress assigns stable capture and artifact identity, writes the raw payload and
complete capture manifest atomically, and commits the discoverable work record
last. Workers recover pending work after restart and process lane references
idempotently. Raw deletion requires both retention eligibility and acknowledgement
from every configured required consumer.

If durable ingress itself cannot accept another frame, CameraAgent enters an
explicit unhealthy state and pauses or stops according to disk-pressure policy.
It never silently drops raw evidence. The decision to commit raw durably before
the next exposure or permit a small ownership-safe writer queue with a documented
crash window remains open.

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

Structured output includes a polyline, bounding region, width and brightness
profiles, saturation, fragments, measured features, confidence, reason codes,
source references, and algorithm/calibration/mask versions. An overlay is a view
of that data, not the authoritative result.

`Fireball` is a brightness/severity assessment of a meteor. Classification
families initially include meteor, satellite, aircraft, sensor artifact,
environmental artifact, and unknown.

Issue [#62](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/62) owns the
shared contracts and pure Imaging algorithms.

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

Hybrid is the recommended direction, subject to offline and hardware decisions.
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

The project plan decides when this sequence starts. Once promoted, the internal
dependency order is:

1. Implement continuous physical cadence and low-latency metering in #58.
2. Implement durable raw ingress and top-level lanes in #59.
3. Complete reconstructable upload/ingest and durable central jobs in #60.
4. Add deterministic transient scenarios in #61.
5. Prove shared event contracts and detection algorithms in #62.
6. Add optional edge execution in #63.
7. Add central validation and persistence in #64.

Isolated design experiments do not change the authoritative queue or justify
runtime configuration before the required infrastructure exists.

## 12. Open Decisions

The epic must not silently resolve these questions during implementation:

1. Bright-fireball-only initial scope versus faint-meteor sensitivity.
2. Complete offline edge classification versus provisional offline preservation.
3. Durable raw commit before the next exposure versus a small writer queue and
   documented crash window.
4. Filesystem manifest versus SQLite/WAL local work journal.
5. Required/optional lane defaults, backlog limits, and disk-pressure policy.
6. Exposure-first versus gain-first host-control defaults and ROI configuration.
7. Registration versus star-mask strategy for initial temporal subtraction.
8. Edge and central assessment authority and notification-update behavior.
9. Human-review workflow and confidence thresholds.
10. Aircraft and satellite data sources, licensing, availability, and offline
    behavior.
11. Source raw/event retention and later cross-agent correlation.

Each decision is recorded in the owning issue before its dependent runtime slice
is accepted.

## 13. Issue Index

- [#58 Continuous cadence and metering](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/58)
- [#59 Durable raw ingress and processing lanes](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/59)
- [#60 Reprocessable artifacts and central jobs](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/60)
- [#61 Deterministic transient scenarios](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/61)
- [#62 Shared detector and event contracts](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/62)
- [#63 Optional CameraAgent transient lane](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/63)
- [#64 LogicHost validation and event persistence](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/64)
- [#65 Fireball and transient detection epic](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/65)
