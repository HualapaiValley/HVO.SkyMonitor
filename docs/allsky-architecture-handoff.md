# All-Sky Architecture Handoff

This handoff gives another agent enough context to brainstorm product,
architecture, workflow, and implementation ideas for HVO.SkyMonitor without
needing to first learn the internal code paths. It is intentionally high level.
Use `docs/roadmap.md` for portfolio direction and stable initiative IDs. Use
`docs/project-plan.md` as the authoritative architecture, virtual-first
requirements, phase-order, and completion source when decisions become
implementation work.

## What The System Is

HVO.SkyMonitor is a distributed all-sky imaging system. A CameraAgent runs near
each camera, acquires sky frames, stores immutable local evidence, performs
local processing, and uploads selected reconstructable artifacts to a central
LogicHost. LogicHost receives data from many CameraAgents, stores it durably,
runs central processing, and exposes operational review surfaces.

The completed virtual-first milestone proved the full software path with
`VirtualSkyCameraModule` before requiring production physical camera modules.
VirtualSky behaves as a normal camera module behind the same camera contract, so
the rest of the system should not need special branches for simulation.

## The Big Picture

```text
Virtual or physical camera module
  -> CameraAgent acquisition loop
  -> durable local raw ingress in SQLite plus files
  -> independent durable lanes for local processing, upload, and secondary work
  -> local recipes produce previews, annotations, metadata, and combinations
  -> manifest-v2 upload to LogicHost
  -> LogicHost reconstructs immutable raw evidence
  -> central workers run shared recipes and window/event workflows
  -> operators review fleet state, captures, artifacts, weather/cloud, and events
```

The most important invariant is that raw evidence is accepted durably before
optional processing. Processing may fail, retry, skip, or be re-run, but it must
not silently erase the raw capture record.

## Main Runtime Pieces

### CameraAgent

CameraAgent is the edge process. One CameraAgent owns one configured camera. It
owns camera discovery, capture timing, setpoint control, raw normalization,
bounded local history, local processing, local authenticated operation,
telemetry, and upload retry state.

CameraAgent keeps raw and derivative payloads as files and uses SQLite WAL for
transactional ingress, lane state, retries, acknowledgement, quarantine, and
retention decisions. In-memory queues are wake-up accelerators only; durable
SQLite/file state is the source of truth.

### LogicHost

LogicHost is the central system. It accepts many CameraAgents across sites. It
owns registration/profile history, central ingest, SQL/Redis/MinIO-backed state,
background workers, central processing, durable history, retrieval APIs, and
operator UI.

LogicHost is not part of acquisition correctness. CameraAgent must continue
bounded local operation through network or LogicHost outages. After verified
central ingest, LogicHost becomes the durable archive and central processing
system of record.

### Shared Libraries

The solution is split so core behavior can be shared without coupling the two
hosts:

- `AgentCore`: transport-neutral camera, capture, artifact, identity, lineage,
  profile, and timing contracts.
- `Astronomy`: time, coordinates, catalogs, ephemerides, camera geometry,
  projection, and visible-scene behavior.
- `Imaging`: reusable pure image/pixel algorithms, calibration primitives,
  rendering, encoding, annotation, masks, and analysis helpers.
- `Processing`: host-neutral recipe definitions, execution contracts,
  selectors, transforms, analyzers, gates, windows, outcomes, and product
  identity.
- `Catalog.Sqlite`: optional shared read-only SQLite catalog adapter.
- `Common`: reusable ASP.NET security, identity, API, middleware, and
  observability infrastructure.
- `CameraAgent.Common`: edge orchestration, local durability, lanes, storage,
  outbox, retention, telemetry, and configuration.
- `CameraAgent`: the local ASP.NET/Blazor host and composition root.
- `LogicHost`: the central ASP.NET/Blazor host, persistence, workers, and
  composition root.

CameraAgent and LogicHost must not reference each other directly. Shared
contracts or algorithms belong in host-neutral projects.

## VirtualSky In Plain Terms

VirtualSky is the current production-quality camera module used to validate the
system before physical hardware is required. It combines three separate ideas:

1. Astronomy decides what is visible for a UTC instant and observatory location.
2. Optics project visible sky directions through a configured lens/orientation
   onto sensor coordinates.
3. Sensor simulation converts the projected scene into raw camera pixels using
   exposure, gain, response, noise, CFA sampling, and quantization.

Virtual raw frames must look like normal camera frames to CameraAgent. Raw data
does not contain labels, display tone mapping, constellation overlays, or truth
labels. Those belong in derivatives, tests, or review surfaces.

Virtual scenarios can add deterministic clouds and transients. Scenario truth is
test-oracle data; detectors and processing workflows should not receive hidden
labels or expected scores.

## Capture And Processing Flow

1. A camera module produces a `CameraFrame` for a capture request.
2. CameraAgent assigns stable capture and artifact identities.
3. Raw bytes, sidecar metadata, and SQLite ingress state are committed durably.
4. Durable lane records fan the capture out to independent consumers.
5. Local processing recipes may produce calibrated frames, previews,
   annotations, rolling combinations, quality metrics, environmental facts, or
   cloud/event products.
6. Upload creates a manifest-v2 contract that contains enough descriptor,
   layout, timing, profile, lineage, recipe, and checksum facts to reconstruct
   the capture centrally without consulting current CameraAgent state.
7. LogicHost stores immutable payload bytes in object storage, authoritative job
   and metadata state in SQL, and optional acceleration state in Redis.
8. LogicHost workers reconstruct inputs, verify checksums and compatibility,
   run shared recipes, persist outputs and lineage, and expose results for
   review.

The same processing recipes can run locally and centrally. CameraAgent decides
edge scheduling and retention. LogicHost decides central scheduling, history,
reprocessing, and cross-agent or window workflows.

## Processing Vocabulary

Recipes are versioned and deterministic. Recipe identity includes the recipe
name, semantic version, implementation version, canonical options, input
selection, and parameter hash. Changing behavior or options without changing
identity is not allowed.

Processing operations fall into four broad kinds:

- Transforms produce image or encoded artifacts.
- Analyzers produce structured measurements, masks, geometry, or assessments.
- Gates produce explicit run/skip decisions with reason codes.
- Window processors consume ordered compatible captures.

Outcomes are explicit: `Produced`, `Skipped`, `RetryableFailure`, or
`TerminalFailure`. Missing inputs, incompatible layouts, stale environmental
state, and daylight/cloud/weather conditions should be represented explicitly,
not hidden as ordinary success.

Examples of built-in recipe concepts include linear normalization, encoded
preview, annotation, rolling mean, image quality, cloud assessment, weather/cloud
overlay, and no-op analyzer contracts for pipeline validation.

## Data And Evidence Principles

- Raw frames are immutable primary evidence.
- Derivatives must carry lineage back to their source artifacts.
- Payload checksums matter; central reconstructability matters more than current
  device state.
- Capture-time rig, sensor, calibration, mask, and processing profile identities
  are part of the evidence.
- Retention must wait for required lane acknowledgements and policy eligibility.
- Finite local storage can force capture pause/unhealthy state; it should not
  pretend to accept frames it cannot durably retain.
- Integration tests should use real boundaries where those boundaries matter:
  SQLite, SQL Server, MinIO, filesystem, and HTTP behavior are not mocked merely
  to call a test integration coverage.

## Operational Shape

Local development uses two app hosts plus shared services:

- CameraAgent local UI/API: normally port `5130` in container mode.
- LogicHost central UI/API: normally port `5174` in container mode.
- Shared services: SQL Server, Redis, MinIO, and Mailpit, configured from
  ignored environment files.
- Smoke-test environment contracts capture host names, public URLs, ports,
  catalog identity, owner credentials, and data policy before automation starts
  or mutates application resources.

The smoke-test direction is to make environment assumptions explicit before a
run. Generated bootstrap state, device secrets, and credentials stay out of
checked-in files and evidence bundles.

## Useful Brainstorming Areas

Good brainstorming topics for another agent include:

- Operator workflows for capture review, fleet health, degraded state, and
  event triage.
- How to explain raw evidence, derivatives, lineage, and reconstruction to a
  non-developer operator.
- Ways to compare local and central processing outcomes without implying one is
  always authoritative.
- Cloud/weather/event review surfaces that show confidence, staleness, skipped
  reasons, and provenance clearly.
- Smoke-test orchestration UX, evidence packaging, and failure summaries.
- Configuration editing and validation flows for cameras, rigs, cadence,
  processing profiles, and upload policies.
- Central reprocessing and historical comparison workflows.
- Multi-CameraAgent fleet views for many sites or observatories.

Avoid brainstorming that assumes CameraAgent and LogicHost can call each other
directly, that raw frames can be replaced by previews, or that virtual scenario
truth can be used by production detectors.

## Terms To Preserve

- CameraAgent: edge process near one configured camera.
- LogicHost: central host for ingest, durable archive, workers, and UI.
- VirtualSky: ordinary camera module that simulates all-sky captures.
- Raw ingress: durable local acceptance of immutable raw bytes plus metadata.
- Lane: durable local work stream for upload, processing, or secondary tasks.
- Manifest v2: reconstructable upload contract for capture evidence.
- Recipe: versioned host-neutral processing definition.
- Artifact: raw or derived payload plus identity, role, variant, checksum, and
  lineage.
- Lineage: ordered source-artifact relationship for reconstruction and review.
- Environmental observation: versioned time/source/unit/quality/staleness fact,
  not a hidden default.

## Pointers For Deeper Context

- `docs/project-plan.md`: authoritative architecture, phase plan, and ownership.
- `docs/virtual-camera.md`: VirtualSky, astronomy, optics, sensor simulation,
  deterministic clouds, and transient scenarios.
- `docs/contracts/capture-manifest-v2.md`: reconstructable capture evidence.
- `docs/contracts/processing-recipes-v1.md`: processing identity, inputs,
  outcomes, and recipe vocabulary.
- `docs/planning/requirements-crosswalk.md`: requirement ownership map.
- `docs/runbooks/smoke-test.md`: smoke-test environment contract and data policy.
