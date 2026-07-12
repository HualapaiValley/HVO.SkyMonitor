# HVO.SkyMonitor Project Plan

## 1. Purpose

HVO.SkyMonitor is a distributed all-sky imaging system. Operators run one or
more self-contained CameraAgent instances near their cameras, often on
Raspberry Pi or other Linux hosts. Each agent acquires images, controls the
camera, performs bounded local processing, retains a limited local history,
and eventually uploads selected artifacts to a central LogicHost.

LogicHost is the durable system of record. It receives artifacts from many
agents, stores image data and metadata, runs heavier or cross-frame processing,
and provides historical browsing, visualization, and future event analysis.

The immediate priority is a complete CameraAgent vertical slice using a
planetarium-backed virtual ASI174-family camera. Central ingestion follows
after the agent can reliably produce, process, retain, and expose realistic
images.

This document is the authoritative product description, architecture, and
implementation plan. Status claims in this document must reflect working code
and executable tests, not scaffolding or configuration flags.

## 2. Product Model

### 2.1 Deployment

- A CameraAgent process owns exactly one camera.
- A host may run multiple isolated CameraAgent processes or containers.
- Each agent has its own configuration, local Identity UI, device identity,
  pipeline, filesystem history, telemetry, and central registration.
- Camera hardware is selected through an in-process `ICameraModule`; a camera
  does not require a separate service unless its vendor SDK requires one.
- An agent must continue capturing and retaining data during a LogicHost or
  network outage.
- One LogicHost accepts data from $1..n$ agents across $1..n$ observatories.

### 2.2 CameraAgent responsibilities

The CameraAgent owns work that must remain close to the camera:

1. Camera discovery, initialization, acquisition, and health reporting.
2. Exposure and gain selection within configured hardware limits.
3. Raw-frame normalization into shared pixel and metadata contracts.
4. Configurable local processing such as calibration and rolling combination.
5. Creation of preview and optional annotated derivative images.
6. Bounded filesystem retention of raw and derived artifacts.
7. A local authenticated UI for setup, monitoring, history, and diagnostics.
8. A durable upload outbox so temporary central outages do not lose work.
9. Telemetry for cadence, queue depth, processing latency, disk use, and errors.
10. A local read-only astronomy catalog snapshot so acquisition and processing work while disconnected from LogicHost.

### 2.3 LogicHost responsibilities

LogicHost owns work requiring durable or centralized resources:

1. Agent registration, credential validation, and rig-profile versioning.
2. Idempotent streamed ingestion into MinIO and metadata into SQL Server.
3. Durable historical retention and lifecycle policy enforcement.
4. Cross-frame and cross-agent processing, including timelapses and detection.
5. Central previews, galleries, search, overlays, and operational dashboards.
6. Reprocessing old captures against updated catalogs or algorithms.

LogicHost must not be required for normal camera acquisition. CameraAgent must
not become the permanent historical archive.

## 3. Architectural Decisions

### 3.1 Keep the current repository

The existing repository already has the correct deployment boundaries,
configuration model, module factory, capture loop, processing-step discovery,
local UI, registration flow, and LogicHost foundation. Development continues
here. Legacy V5 and V6 repositories are algorithm and behavior references only.

Code may be ported only after its behavior is understood and covered by new
tests. Legacy host topology, service registration, persistence models, and
configuration are not copied wholesale.

### 3.2 Shared project boundaries

The intended project dependency direction is:

```text
HVO.SkyMonitor.AgentCore
  ^
  +--- HVO.SkyMonitor.Astronomy
         ^
         +--- HVO.SkyMonitor.Imaging

HVO.SkyMonitor.Catalog.Sqlite ---> HVO.SkyMonitor.Astronomy

HVO.SkyMonitor.CameraAgent.Common ---> AgentCore + Astronomy + Imaging
HVO.SkyMonitor.CameraAgent        ---> CameraAgent.Common + Catalog.Sqlite
HVO.SkyMonitor.LogicHost          ---> AgentCore + Astronomy + Imaging + Catalog.Sqlite
```

CameraAgent and LogicHost must not reference each other. They share stable
contracts, projection, catalog, and imaging behavior through the dedicated
libraries. LogicHost does not own camera acquisition or the edge processing
pipeline, and CameraAgent does not own central persistence or processing.

#### `HVO.SkyMonitor.AgentCore`

Contains stable, transport-neutral agent and artifact contracts only:

- Camera module lifecycle and capabilities.
- Rig, sensor, optics, orientation, and observatory configuration records.
- Raw frame layout and capture metadata.
- Frame identity and artifact provenance contracts.
- Pipeline and artifact-role vocabulary shared across hosts.

It must not depend on SkiaSharp, EF Core, MinIO, ASP.NET Core, or a camera SDK.

#### `HVO.SkyMonitor.Astronomy` (new)

Contains reusable astronomy and catalog behavior:

- UTC, Julian date, and local sidereal time calculations.
- Equatorial, horizontal, ENU, camera-ray, and pixel transformations.
- Configurable atmospheric refraction.
- Projection contracts, implementations, and a projector factory.
- Star, planet, constellation, and deep-sky-object domain records.
- Catalog query contracts and storage-neutral catalog implementations.
- Planet ephemeris and constellation topology services.

The existing `IImageProjector`, `PixelPoint`, and `AltAzPoint` contracts move
from `AgentCore` into this project when it is introduced. Projection is
astronomy domain behavior and is not part of the camera-module transport
contract.

This project must be deterministic, thread-safe, and free of ambient state for
a supplied time, location, rig, and catalog. It must not depend on CameraAgent,
LogicHost, Imaging, EF Core, SkiaSharp, or UI code.

Catalog persistence is host infrastructure, not Astronomy domain behavior. Each
LogicHost and CameraAgent receives the same versioned, read-only SQLite catalog
snapshot locally. Catalog data must never be added to the shared SQL Server
schema or fetched during normal CameraAgent acquisition/processing.

The optional `HVO.SkyMonitor.Catalog.Sqlite` infrastructure adapter owns concrete
SQLite access, snapshot validation, and process-cached query execution. It may
reference Astronomy catalog contracts; Astronomy must not reference it. Both
hosts may compose the adapter without referencing each other. Architecture tests
must enforce this direction when the adapter project is introduced.

#### `HVO.SkyMonitor.Imaging` (new)

Contains reusable pixel and image algorithms:

- Validated image layout, stride, bit depth, and buffer ownership helpers.
- Mono8 and Mono16 operations required by the first vertical slice.
- Planetarium rendering using `HVO.SkyMonitor.Astronomy` projections.
- Rolling frame combination and future calibration primitives.
- Preview rendering, celestial annotation, and image/FITS encoding.

Rendering and annotation may use SkiaSharp internally, but public domain
contracts must not expose disposable Skia objects. Both CameraAgent and
LogicHost use this project when they need identical preview or annotation
behavior.

#### `HVO.SkyMonitor.CameraAgent.Common`

Owns edge orchestration:

- Capture scheduling and module execution.
- Ordered processing pipelines and backpressure.
- Exposure control policy.
- Artifact-set creation and processing context.
- Local storage, retention, upload outbox, and telemetry.
- Module implementations that do not require a separate distributable.

#### `HVO.SkyMonitor.CameraAgent`

Owns the runnable edge host, local Identity, API, Blazor UI, configuration,
health checks, and deployment packaging.

### 3.3 Shared projection contract

Projection is a single shared implementation used in three places:

1. The CameraAgent virtual camera projects catalog and ephemeris objects into
  raw simulated sensor coordinates.
2. CameraAgent processing projects labels and annotations into local
  derivatives.
3. LogicHost projects labels and annotations when creating or reprocessing
  central derivatives.

No host project may implement its own sidereal-time, coordinate-conversion,
camera-basis, refraction, or optical-projection math. Host code constructs an
immutable projection context and calls `HVO.SkyMonitor.Astronomy` interfaces.
The context contains UTC, observatory location, sensor geometry, optics,
boresight, roll, horizon/refraction policy, and relevant rig-profile version.

Projection implementations are stateless after construction and safe for
concurrent use. APIs use explicit units in names or dedicated value types,
return a failure/visibility result for out-of-domain objects, and never signal
normal visibility conditions through exceptions.

Every generated derivative records:

- Projection model identifier and algorithm version.
- Rig-profile version and hash.
- Catalog snapshot identifier and checksum.
- Ephemeris and refraction model identifiers where applicable.
- Rendering or annotation recipe version.

CameraAgent and LogicHost must run the same projection conformance fixture.
Given identical context and celestial coordinates, both consumers must produce
the same visibility result and pixel coordinate within a documented numeric
tolerance. Package-version drift is observable in artifact metadata and must
not silently overwrite an existing derivative recipe.

### 3.4 One acquisition lifecycle, many pipelines

Every camera follows the same lifecycle:

```mermaid
flowchart LR
    A[Schedule capture] --> B[ICameraModule acquisition]
    B --> C[Preserve raw artifact]
    C --> D[Normalize and calibrate]
    D --> E[Combine or stack]
    E --> F[Create preview and annotations]
    F --> G[Store local artifacts]
    G --> H[Queue selected artifacts for upload]
    H --> I[Publish telemetry and latest state]
```

Pipeline profiles decide which optional steps run and which artifacts are
retained or uploaded. Camera modules acquire frames; they do not stack, encode,
persist, upload, or annotate them.

### 3.5 Raw and derived artifacts are distinct

The current single mutable `CameraFrame` processing model must become an
artifact set. A processing step may add a derivative but must not erase the
original capture.

Initial artifact roles are:

| Role | Meaning | Typical encoding |
| --- | --- | --- |
| `Raw` | Immutable camera output | Native bytes or FITS |
| `Calibrated` | Corrected linear image | Mono16/FITS |
| `Combined` | Rolling combination of recent frames | Mono16/FITS |
| `Preview` | Display-ready derivative | JPEG or PNG |
| `AnnotatedPreview` | Preview with celestial labels | JPEG or PNG |
| `Metadata` | Capture, rig, processing, and provenance data | JSON |

Each artifact requires a stable artifact ID, frame/sequence ID, role, media
type, dimensions, pixel format where applicable, byte length, checksum,
creation time, source artifact IDs, processing recipe/version, and rig-profile
version.

### 3.6 Backpressure and ownership

- Use one bounded channel between capture and the ordered local pipeline.
- Do not recreate V5's nested processing queues.
- The configured full-mode policy must be explicit and observable.
- Raw data must be preserved before a fallible derivative step.
- Buffer ownership and disposal must be explicit; pooled memory cannot outlive
  its owner.
- A failed optional step records failure telemetry and preserves available
  artifacts. It must not silently substitute a different artifact role.

## 4. First Vertical Slice: Virtual ASI174 Family

The virtual camera is not a UI animation or a special simulator workflow. It is
an `ICameraModule` that emits the same raw contract expected from later ZWO,
SBIG, DSLR, UVC, or RTSP adapters.

The detailed sensor, lens, compatibility, fixture, and external comparison
requirements are defined in
[`virtual-camera.md`](virtual-camera.md).

### 4.1 Sensor profile

The initial virtual sensors model the Sony IMX174 geometry used by ASI174MM and
ASI174MC:

- 1936 × 1216 active pixels.
- 5.86 µm square pixels.
- Configurable monochrome or color response.
- Mono16 as the canonical monochrome raw format.
- RGB24 as the initial color-rendering compatibility format.
- Explicit Bayer16 CFA output after frame-layout contracts can describe its
  pattern, packing, stride, endianness, and levels.
- Configurable exposure, gain, readout delay, and deterministic random seed.

Physical response values such as read noise, full-well capacity, dark current,
quantum efficiency, and gain conversion must be documented from a source or
labeled as simulation parameters. Legacy heuristic values are not presented as
measured IMX174 characteristics.

### 4.2 Scene inputs

For a supplied UTC instant, observatory, and rig, the renderer produces the sky
that falls on the configured sensor:

- HYG stars filtered by magnitude and projected field of view.
- Solar-system objects from the selected ephemeris implementation.
- Optional deep-sky objects for validation and annotation.
- Background level, vignetting, configurable horizon mask, and sensor noise.
- Optional deterministic clouds or transient injections for later tests.

Constellation lines and labels are derivatives. They are not burned into the
raw virtual-camera output.

The same celestial scene must render through configurable fisheye and
rectilinear/telescope optics. Fisheye support includes equidistant,
equisolid-angle, orthographic, and stereographic mappings. Rectilinear support
uses perspective/gnomonic projection with intrinsics derived from physical
focal length and pixel pitch or supplied by calibration.

### 4.3 Determinism

The module accepts a `TimeProvider` and seed. Identical time, location, rig,
catalog, exposure, gain, and seed must produce identical pixel data. This makes
astronomy, exposure, combination, storage, and API tests reproducible.

### 4.4 Virtual-camera acceptance criteria

- The module is selected only by configuration through `ICameraModuleFactory`.
- It emits valid 1936 × 1216 Mono16 and RGB24 frames with complete metadata.
- Mono and color profiles support fisheye, rectilinear, and telescope optics
  without camera-module-specific projection code.
- Stars move consistently when time advances and rotate consistently when rig
  orientation changes.
- A catalog star projected by the renderer lands at the same pixel used by the
  annotation service within a documented tolerance.
- Increased exposure or gain changes image statistics predictably without
  changing celestial geometry.
- Frames pass through the ordinary processing, storage, latest-frame, and
  telemetry paths without simulator-specific branches.
- A fixed fixture produces a golden test image and stable pixel checksum.
- A pinned headless planetarium validation compares selected rendered centroids,
  annotation anchors, projection boundaries, orientation, and constellation
  endpoint/clipping geometry. It runs explicitly or on a manual/scheduled
  workflow rather than making every unit test depend on a GUI stack.

## 5. Projection and Catalog Plan

### 5.1 Coordinate pipeline

Implement and test each transformation independently:

1. Normalize UTC and calculate Julian date.
2. Calculate Greenwich and local sidereal time.
3. Convert catalog RA/Dec to topocentric altitude/azimuth.
4. Apply optional atmospheric refraction above a configured altitude floor.
5. Convert horizontal coordinates into an ENU unit vector.
6. Transform the vector into the camera basis from boresight and roll.
7. Apply the configured optical projection to sensor coordinates.
8. Reject points behind the camera, below the horizon mask, or off sensor.

Implement equidistant, equisolid-angle, orthographic, and stereographic fisheye
mappings plus perspective/gnomonic rectilinear mapping. Projection
implementations must support forward and inverse mapping and define behavior at
the optical axis, horizon, image edge, and invalid domain.

The shared API accepts an immutable projection context rather than resolving
configuration, clocks, catalogs, or services internally. Catalog querying and
pixel drawing are separate concerns: projection maps coordinates, catalog
services select objects, and imaging services render the projected result.

### 5.2 Catalog contracts

Use immutable records and query interfaces rather than exposing CSV rows or EF
entities. The initial catalog API must support:

- Magnitude limit.
- Sky region or projected-frame filtering.
- Maximum result count with deterministic brightest-first selection.
- Stable object identifiers and display names.
- Optional color index or spectral data for rendering.

Load and index the local HYG SQLite snapshot once per process. Do not query a
central service, parse source data, or create a service scope for every frame.
Catalog licensing, source URL, version, checksum, and preprocessing steps must
be documented beside the packaged data. Snapshot updates are explicitly
distributed and applied atomically; CameraAgent continues using its current
snapshot while offline.

The initial region contract is an optional inclusive J2000 spherical cap on the
candidate query. Astronomy derives a conservative cap from the calibrated
projection and horizon policy, while adapters may use it only to reduce
candidates. Exact visibility and `MaximumResults` remain Astronomy concerns.
Refraction falls back to the geometric-horizon cap or an all-sky query where an
optical cap cannot be proven conservative. The process-cached SQLite adapter
filters its validated immutable rows without changing the snapshot schema.

### 5.3 Astronomy validation

- Unit tests use published reference cases for sidereal time and coordinate
  conversion.
- Projection round trips are tested across center, cardinal directions,
  horizon, edge, and out-of-domain inputs.
- Catalog selection is deterministic and bounded.
- Planet positions are compared with a trusted ephemeris at fixed instants.
- End-to-end fixtures verify known stars at known pixels for Hualapai Valley
  Observatory and at least one second latitude.
- A shared conformance suite runs against the projection API as consumed from
  CameraAgent and LogicHost test projects, preventing host-specific math or
  dependency drift.
- Public APIs are covered for invalid units, non-finite values, below-horizon
  objects, off-sensor results, and projection singularities.
- Fixture manifests record all time, location, sensor, lens, orientation,
  catalog, refraction, and version inputs needed to reproduce an image.
- Representative output is compared with a configured external planetarium and
  later with the operator-provided comparison system as described in the
  virtual-camera specification.

## 6. CameraAgent Pipeline Plan

### 6.1 Capture and exposure control

Exposure selection is a host policy, not virtual-camera logic. The first
controller uses configured day/night defaults and clamps every result to the
rig's exposure envelope. The next controller adds feedback from a measured
linear-image statistic such as median or percentile ADU.

Required behavior:

- Explicit transition policy for day, twilight, and night.
- Configurable target ADU and tolerance.
- Bounded changes per capture to avoid oscillation.
- Independent exposure and gain limits and preference order.
- Recovery after saturation, darkness, capture failure, or stale feedback.
- Recorded reason for every setpoint change.

The controller consumes prior-frame measurements and produces the next
`CaptureSetpoint`. Camera adapters apply and report the effective settings.

### 6.2 Rolling combination

Port the proven V5 behavior as an initial algorithm, not its host design:

- Keep immutable snapshots of compatible linear frames.
- Emit a combined artifact after every capture, including warm-up.
- Use the newest configured number of compatible frames.
- Compute a linear arithmetic mean with sufficient accumulator precision.
- Record contributing frame IDs, count, and total integration time.
- Reset compatibility state when dimensions, format, rig version, orientation,
  horizon, or projection-affecting configuration changes.
- Retain enough inputs to satisfy configured minimum frame and integration
  windows without interpreting those values as batch flush triggers.

The first version intentionally excludes image registration, sigma clipping,
dark subtraction, and motion compensation. Those become separate algorithms
after the baseline is measured.

### 6.3 Preview and annotation

- Generate display-ready previews from raw, calibrated, or combined sources as
  selected by pipeline configuration.
- Apply a deterministic stretch without changing the source artifact.
- Use the same projector and catalog query as the virtual camera.
- Support optional star, planet, DSO, constellation, cardinal-direction, and
  horizon overlays.
- Real-camera constellation overlays may resolve topology endpoint geometry
  omitted by the base visible-object selection, but they never synthesize star
  pixels or claim a physical detection.
- VirtualSky may expose an explicit `IncludeConstellationEndpointStars` render
  option that adds omitted topology stars to the simulated scene before raw
  generation. The option is virtual-only, versioned in the render recipe, and
  recorded in provenance; the annotation step still never mutates raw data.
- Draw complete figures when their topology is inside the calibrated view and
  clip partial figures against sensor, image-circle, projection-domain, and
  horizon boundaries rather than requiring both endpoints to be on-screen.
- Make constellation line value/color, thickness, opacity, and endpoint-star
  inclusion explicit deterministic recipe options.
- Keep label placement bounded to the image and record the catalog/recipe
  version used to create the derivative.

### 6.4 Local storage and retention

Replace no-op storage with a real artifact store. The initial filesystem layout
is organized by agent, UTC date, frame ID, and artifact role. Writes use a
temporary path followed by atomic rename. Metadata and checksums are committed
with the artifact.

Retention is policy-based per role:

- Raw frames may have the shortest edge retention.
- Combined frames and previews may be retained longer.
- Outbox-referenced files cannot be removed until upload succeeds or an
  operator explicitly abandons them.
- Cleanup operates by stored metadata and policy, not filename assumptions.
- Disk-pressure thresholds can shorten eligible history while preserving the
  current frame and pending uploads.

### 6.5 Local API and UI

The existing local authenticated CameraAgent experience remains. The first
complete workflow exposes:

- Current camera/module state and active rig version.
- Latest raw statistics, combined preview, and optional annotated preview.
- Exposure/gain decision and stack contribution count.
- Pipeline step duration and failure state.
- Capture queue, outbox, filesystem usage, and retention state.
- A bounded local gallery by UTC date and artifact role.
- Capture start/stop and safe configuration validation where already allowed.

The local UI is operational tooling, not a replacement for the central archive.

## 7. Implementation Phases

Status values are `Not started`, `In progress`, `Blocked`, or `Complete`.
Complete requires the listed acceptance checks to pass.

### Phase 0: Baseline and contracts — In progress

Deliverables:

- Consolidate project documentation and remove contradictory status documents.
- Fix active Dockerfiles and remove references to deleted projects.
- Resolve pending EF model/migration drift so integration hosts start.
- Upgrade, replace, or remove dependencies responsible for package audit
  warnings, then enable warnings-as-errors centrally for local and CI builds.
- Record the initial coverage baseline and add non-regression enforcement to CI.
- Define frame identity, image layout, artifact role, provenance, and artifact
  set contracts without breaking camera-module isolation.
- Add architecture tests for project dependency direction.

Exit criteria:

- Full solution builds with zero errors and zero warnings in Debug and Release.
- Known vulnerable package advisories are resolved rather than hidden with
  `NoWarn`; the current `NU1902` and `NU1903` baseline is reduced to zero.
- Existing warning suppressions are audited. Any retained suppression is as
  narrow as possible and documents why changing the code would be incorrect or
  incompatible with framework/generated-code requirements.
- Unit and integration test hosts start from a clean checkout.
- A raw artifact survives a deliberately failing downstream processing step.
- No current documentation claims an unimplemented simulator or upload path is
  complete.

### Phase 1: Astronomy and projection foundation — In progress

Deliverables:

- Create `HVO.SkyMonitor.Astronomy` and its MSTest project.
- Move projector contracts and coordinate value types out of `AgentCore` while
  preserving a deliberate compatibility migration for current consumers.
- Implement time, coordinates, refraction, camera basis, and equidistant
  projection with forward/inverse tests.
- Implement the required fisheye and rectilinear projection compatibility
  matrix from the virtual-camera specification.
- Add catalog contracts and a process-cached HYG implementation.
- Add initial planet and constellation data services.
- Add project-reference architecture tests that prevent either host from
  defining or substituting private projection math.

Exit criteria:

- Published astronomy fixtures and projection round trips pass.
- Catalog licensing and version are documented.
- Known object-to-pixel fixtures pass at two observatory locations.
- CameraAgent and LogicHost projection conformance fixtures return matching
  visibility and pixel results from the shared assembly.

### Phase 2: Imaging and virtual ASI174MM — In progress

Deliverables:

- Create `HVO.SkyMonitor.Imaging` and its MSTest project.
- Port the useful V5/V6 starfield rendering behavior behind new contracts.
- Implement deterministic mono and color background, stars, planets,
  vignetting, and sensor-noise stages.
- Implement `VirtualSkyCameraModule` with ASI174MM and ASI174MC profiles in the
  agent module layer.
- Add realistic fisheye, rectilinear, and telescope sample configurations.

Exit criteria:

- Virtual-camera acceptance criteria in section 4.4 pass.
- A development run continuously produces realistic raw frames through the
  existing capture worker.
- CPU, allocation, frame size, and generation latency baselines are recorded
  on x64 and the intended Raspberry Pi architecture when available.

### Phase 3: Artifact pipeline and local persistence — In progress

Deliverables:

- Replace mutable single-frame processing with artifact-set processing.
- Preserve raw before optional transformation.
- Implement real filesystem artifact storage and metadata indexing.
- Implement preview encoding for Mono16.
- Update latest-frame access and local gallery to select artifact roles.
- Add retention and disk-pressure policies.

Exit criteria:

- Raw and preview artifacts survive process restart and can be browsed locally.
- Retention removes only eligible artifacts.
- Failed encoding or annotation cannot remove raw data.
- Sustained capture demonstrates bounded memory and channel behavior.

### Phase 4: Exposure control and rolling combination — In progress

Deliverables:

- Implement day/night setpoint policy and ADU feedback controller.
- Implement rolling linear combination with compatibility resets.
- Emit combination provenance and integration metadata.
- Surface exposure decisions and combination state in telemetry and local UI.

Exit criteria:

- Deterministic brightness sequences converge without oscillation.
- Warm-up, full-window, reset, and incompatible-frame tests pass.
- A long-running virtual night produces stable combined images while memory and
  local storage remain bounded.

### Phase 5: Planetarium derivatives and agent hardening — In progress

Deliverables:

- Implement annotation and constellation derivative steps.
- Add health checks for camera, catalog, pipeline, storage, and disk pressure.
- Complete local operational history and diagnostics.
- Validate container startup, restart recovery, graceful shutdown, and ARM64
  deployment documentation.

Exit criteria:

- Rendered stars and labels align through the shared projector.
- A 24-hour accelerated simulation completes without unbounded growth or lost
  raw artifacts.
- The CameraAgent can be installed and operated without LogicHost.

### Phase 6: Upload contract and durable outbox — In progress

This is the transition to LogicHost work, after the local agent is proven.

Deliverables:

- Finalize a versioned upload manifest using the artifact contracts.
- Add a durable CameraAgent outbox with retry, backoff, idempotency key, and
  bandwidth limits.
- Replace base64 JSON with streamed binary or multipart transport.
- Define central acknowledgement and safe local cleanup behavior.

Exit criteria:

- Network interruption and agent restart do not duplicate or lose artifacts.
- Upload throughput does not block acquisition or exhaust local memory.

### Phase 7: LogicHost durable ingest — In progress

Deliverables:

- Stream bytes into MinIO with deterministic object keys and checksums.
- Store normalized frame/artifact/provenance records in SQL Server.
- Make ingestion idempotent by agent, frame ID, artifact role, and recipe.
- Expose latest and historical artifact queries.
- Add server-side derivative scheduling without coupling it to request handling.
- Reference the shared Astronomy and Imaging assemblies for central labels and
  annotations; do not introduce LogicHost-specific projection calculations.

### Phase 8: Central processing and experience — Deferred

Deliverables include timelapses, central annotations and reprocessing, meteor or
transient detection, historical browsing, dashboards, and cross-agent analysis.
These features use stored raw artifacts and versioned rig/catalog metadata.
Fireball/transient preparation and runtime dependencies are defined in
[`projects/fireball-transient-detection.md`](projects/fireball-transient-detection.md)
and tracked by epic [#65](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/65).

## 8. Test and Quality Strategy

Tests are implementation deliverables, not a final hardening activity. Every
phase starts by translating its acceptance criteria into executable tests and
ends only after those tests pass at the narrowest useful level and in the full
solution. A configuration switch, registered service, or compiling stub is not
evidence that a feature works.

### 8.1 Development workflow

Use red-green-refactor for deterministic domain logic, regressions, and public
contracts whenever a focused failing test can express the intended behavior:

1. Add the smallest test that fails for the expected reason.
2. Run that test and record the failure; a test that passes before the
   implementation does not prove the new behavior.
3. Implement the smallest production change that satisfies the behavior.
4. Run the focused test, then the owning test project.
5. Refactor only while the tests remain green.
6. Run affected integration tests, the full non-hardware suite, and warning-
   clean Debug and Release builds before declaring the slice complete.

Tests must be deterministic. Inject `TimeProvider`, seeded random sources,
filesystem roots, and external-service boundaries. Do not use wall-clock
sleeps, network planetarium services, mutable global state, or test ordering in
unit tests. Numeric tests state units, tolerances, reference source, and the
reason for each tolerance.

Bug fixes begin with a regression test unless the failure is solely in
generated code or deployment configuration. In those cases, add the cheapest
executable configuration, startup, or integration check that would have caught
the defect.

### 8.2 Unit and conformance tests

- Coordinate, projection, catalog, and ephemeris reference cases.
- Forward/inverse projection properties, out-of-domain visibility, poles,
  horizon boundaries, azimuth wrapping, and every supported lens model.
- Shared CameraAgent/LogicHost conformance fixtures using identical manifests.
- Buffer layout and ownership.
- Mono16, RGB24, and future CFA channel/layout semantics, including malformed
  buffer rejection.
- Deterministic virtual-camera output checksums plus geometry and image-
  statistic assertions.
- Exposure-controller transitions and limits.
- Rolling-combination arithmetic and compatibility.
- Artifact provenance and retention decisions.
- Configuration validation for every module and processing step.
- Architecture tests for project references and forbidden host-specific
  astronomy/projection implementations.

### 8.3 Integration and contract tests

- Virtual camera through capture, pipeline, filesystem, API, and local UI data
  services.
- Every required sensor/lens combination in the virtual-camera compatibility
  matrix at reduced CI resolution, with selected full-resolution fixtures.
- Restart recovery with stored artifacts and pending outbox entries.
- Pipeline failure isolation.
- Configuration-specific pipeline composition.
- Streamed CameraAgent-to-LogicHost upload with SQL Server and MinIO.
- Versioned manifest compatibility, idempotency, checksum failure, retry, and
  partial-write behavior at process and storage boundaries.

Integration tests use disposable infrastructure and explicit MSTest categories.
Tests requiring Docker, hardware, external comparison systems, or long runtimes
must remain separately selectable, but the default CI suite cannot silently
exclude tests merely because their project name contains `IntegrationTests`.

### 8.4 Long-running, property, and performance tests

- Accelerated day/twilight/night simulation.
- Sustained bounded-channel pressure.
- Disk-pressure and retention behavior.
- Mono16 allocation and combination throughput.
- Raspberry Pi CPU, memory, temperature, storage, and capture-cadence budgets.

Golden images are useful for deterministic fixtures but must be paired with
numeric geometry and image-statistic assertions to avoid brittle tests.

Property-style tests should cover projection round trips, monotonic radial
mappings, bounded output coordinates, combination invariants, and retention
invariants over deterministic generated inputs. Performance tests record the
machine/runtime profile and assert only budgets stable enough for that runner;
benchmark observations must not be disguised as portable correctness tests.

### 8.5 Coverage and review gates

Coverage is a backstop, not a substitute for meaningful assertions:

- New or materially changed domain logic targets at least 90% line and 85%
  branch coverage in the touched production files.
- Astronomy projection, frame-layout, exposure-control, rolling-combination,
  artifact-provenance, retention, and upload-idempotency code targets at least
  95% line and 90% branch coverage.
- Generated migrations, Razor-generated code, and unavoidable platform shims
  may be excluded only through a reviewed, path-specific configuration.
- Overall solution line and branch coverage must not decrease. CI records a
  reviewed baseline, fails regressions, and raises the baseline as coverage
  improves.
- Surviving critical mutants, uncovered error branches, and tests that only
  assert non-null/success status require review even when percentage targets
  pass. Mutation testing may run on deterministic shared-domain projects when
  practical.

Each implementation PR or handoff report maps acceptance criteria to test
names, lists commands and results, and identifies any intentionally deferred
hardware or long-running checks. No test may be deleted, skipped, weakened, or
recategorized merely to make a gate pass.

### 8.6 Documentation and comments

- All public APIs in shared projects have useful XML documentation, including
  units, valid ranges, ownership/lifetime, failure behavior, and thread-safety
  where relevant.
- Astronomical constants and algorithms cite the applicable IAU, IERS,
  catalog, paper, or pinned legacy source.
- Non-obvious formulas, coordinate conventions, buffer layouts, concurrency
  invariants, and durability decisions receive concise rationale comments.
- Do not narrate obvious code. General knowledge belongs in XML documentation
  or focused design documentation rather than repetitive inline comments.
- Behavior or configuration changes update the project plan, runbook, sample
  configuration, and operator documentation in the same slice.

### 8.7 Zero-warning completion gate

Completion means zero warnings as well as zero errors. The required final gate
includes restore, Debug and Release builds, tests with coverage, formatting,
and package vulnerability/deprecation checks. CI treats warnings as errors once
the Phase 0 warning baseline is removed.

Do not satisfy this gate with broad `NoWarn`, disabled analyzers, reduced
analysis levels, or blanket `SuppressMessage` attributes. A new suppression is
allowed only when the diagnostic is a documented false positive or required
framework/generated-code pattern, is scoped to the smallest member/file, and
has a specific justification. Package vulnerability warnings are fixed by
upgrading, replacing, or removing the dependency; any temporary upstream block
is documented with an owner and expiration and prevents final completion.

The reusable implementation workflow is available as
[`../.github/prompts/implement-project-phase.prompt.md`](../.github/prompts/implement-project-phase.prompt.md).

## 9. Configuration Principles

- Separate module implementation options from the physical rig profile.
- Version rig and pipeline configuration used for every artifact.
- Validate all configuration at startup before capture begins.
- Pipeline steps use stable aliases in operator configuration; assembly-
  qualified type names remain an advanced extension mechanism.
- Unknown steps, unsupported formats, and invalid ordering fail with actionable
  errors.
- Secrets never live in camera rig or pipeline files.

At least two sample profiles will be maintained:

1. A fast, reduced-resolution virtual profile for development and CI.
2. A full 1936 × 1216 virtual ASI174MM profile for realistic validation.

## 10. Legacy Reference Policy

The external repositories, immutable commits, owning source files, tests, and
checkout commands are recorded in
[`reference-code.md`](reference-code.md). Use those sources selectively:

- V5 `RollingFrameStacker` for rolling-window semantics and test cases.
- V5/V6 `StarFieldEngine` for coordinate flow and rendering behavior.
- V5 celestial and constellation filters for overlay requirements.
- V5 FITS metadata fields and MinIO object organization as design input.

Do not copy:

- The monolithic V5 ASP.NET camera host.
- Nested processing queues or per-frame service scopes.
- Disposable Skia objects in domain contracts.
- Silent fallback from a failed combined artifact to a raw artifact.
- Base64 payload transport or duplicated archive/delivery objects.
- Configuration flags without an executable implementation and tests.

When legacy behavior is adopted, record the repository, pinned commit, and
source path in the implementing PR and add tests describing the intended
behavior in current terminology. Do not rely on a temporary checkout or an
unpinned branch as the only provenance record.

## 11. Near-Term Work Queue

The next implementation work should occur in this order. Ordinary-path
integration, machine-readable ASI174 evidence, catalog coarse filtering,
real-image geometry-only constellation overlays, and pinned headless Stellarium
validation are complete.

1. Validate standalone container startup with its packaged offline catalog and
   retain exact Debug/Release, vulnerability, coverage, format, and Stellarium
   evidence for the implementation baseline.
2. Complete local persistence restart browsing, disk-pressure policy, graceful
   channel drain, failure backoff, and accelerated full-night/24-hour soak work.
3. Validate ARM64 deployment and characterize performance on the intended
   Raspberry Pi hardware without inventing thresholds.
4. Finish the upload manifest/outbox contract and begin LogicHost durable ingest.
5. Continue physical ASI178 lens, orientation, Bayer response, and mono-bin
   calibration as a separate hardware-backed work stream.
6. Prepare deferred fireball/transient processing through continuous capture
   cadence [#58](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/58),
   durable raw fan-out [#59](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/59),
   and reconstructable central jobs
   [#60](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/60). Detector
   runtime work remains under epic
   [#65](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/65) after the
   preceding queue and recorded decision gates.

## 12. Success Definition for the CameraAgent Milestone

The CameraAgent milestone is complete when an operator can start a container on
a standalone host, select the virtual ASI174MM profile, and observe it running
for a full simulated night while it:

- Produces astronomically consistent Mono16 and RGB24 frames through fisheye
  and rectilinear/telescope profiles.
- Adjusts exposure and gain within policy.
- Produces traceable rolling combinations and previews.
- Retains a bounded, browsable filesystem history.
- Survives optional processing failures and process restarts.
- Reports useful health and pipeline telemetry through its local UI.
- Accumulates upload-ready artifacts without requiring LogicHost.

That milestone validates the architecture for a later physical camera adapter:
the adapter replaces only acquisition, while projection validation, processing,
storage, telemetry, UI, and eventual upload remain unchanged.
