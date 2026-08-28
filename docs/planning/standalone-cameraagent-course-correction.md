# Standalone CameraAgent Course Correction

> Historical decision record. Issues #205 through #211 delivered this course
> correction and its standalone acceptance gate. Imperative and future-tense
> language below records the executed contract; it does not describe open work.

## Decision

Virtual-first completion required a production-like standalone CameraAgent
checkpoint before split-host deployment or LogicHost UI work. The checkpoint
proved that acquisition, local control, processing, environmental history,
storage, recovery, and authenticated operator workflows are complete while
LogicHost, SQL Server, Redis, and MinIO are absent.

The ASI676MC and 2.5 mm fisheye scenario is the primary full-resolution
acceptance profile. It is not a special renderer architecture. VirtualSky must
also support configuration-selected sensor geometry, mono/color/CFA response,
sample and container depth, ROI, binning, readout layout, and optics. Changing
to an ASI174 Mono8 ROI/binning profile must use the same production module and
pipeline contracts.

## Product Outcome

A completed standalone CameraAgent can:

1. Evaluate a local timezone-aware recurring and exception schedule, including
   fixed and solar-relative day, twilight, and night windows.
2. Acquire immutable VirtualSky raw evidence through an ordinary configured
   camera/rig/readout profile and the complete production catalog.
3. Select compatible local calibration references and produce calibrated
   derivatives without modifying native raw bytes.
4. Execute an explicit versioned graph for combination, preview, annotation,
   quality/cloud assessment, weather overlay, local storage, and telemetry.
5. Run durable edge transient/fireball processing from ordinary linear frames.
6. Gather deterministic virtual environmental observations independently of
   image cadence and retain/query them locally while offline.
7. Let an authenticated operator inspect schedule decisions, sensor freshness,
   calibration selection, graph state, artifacts, lineage, quarantine, storage,
   and recovery from the CameraAgent UI.
8. Continue correctly through restart, pressure, optional-step failure, and
   central outage, with zero central network attempts in explicit standalone
   mode.

## Work Packages

### Configurable VirtualSky sensor and readout

- Separate astronomical scene, installed optics, native sensor, readout mode,
  response, and quantization.
- Support 8-, 10-, 12-, 14-, and 16-bit meaningful samples independently from
  8- or 16-bit containers and any explicitly supported packed representation.
- Record a versioned native-to-stored code transform/alignment and whether
  black/white levels use native ADC or stored-container code units. Right
  aligned, left shifted, full-range scaled, and packed mappings are not
  interchangeable.
- Extend manifest v2 and central normalized layout additively with nullable
  stored-code transform and level-code-space fields. Existing 16-bit identity
  layouts may use the canonical identity/stored-code default only when the old
  descriptor proves it unambiguously. Retired lower-depth camera history with
  insufficient canonical facts is not admitted into fresh canonical state; do
  not infer native precision/alignment from free-form metadata. New readout
  profiles require both fields before acquisition.
- Support Mono, rendered RGB, and configured CFA raw modes without preset-name
  branches.
- Describe native geometry, ROI in native photosite coordinates, bin factors,
  bin algorithm, output dimensions/stride, CFA origin/parity, and byte order.
- Derive output principal point, focal scale, image circle, masks, and projected
  object coordinates from the native calibrated rig plus ROI/bin transform.
- Require equal X/Y binning for radial fisheye projection until an explicit
  anisotropic/elliptical projection and mask contract exists. Rectilinear modes
  may use unequal bins with independent transformed focal scales.
- Preserve truthful raw descriptors and reject impossible or misaligned modes.
- Add a representative conformance matrix rather than an exhaustive Cartesian
  benchmark: ASI174 Mono8 ROI/binning, generic Mono10-in-16, ASI174 unpacked
  12-in-16, ASI178MC Bayer14-in-16, ASI676MC Bayer12-in-16, and generic Mono16.

An ASI174 native 640 x 480 ROI binned 4 x 4 produces 160 x 120 output. Full
1936 x 1216 ASI174 geometry binned 4 x 4 produces 484 x 304. Profiles must state
native ROI and output geometry separately so a 640 x 480 binned output cannot be
mistaken for an impossible ASI174 native region.

### Local scheduling and profile control

- Add local-time weekly schedules, date exceptions, blackouts, and temporary
  overrides.
- Support fixed-clock and sunrise/sunset/civil/nautical/astronomical twilight
  boundaries with offsets.
- Define independent day, twilight, and night exposure, gain, and cadence
  policies. Exposure duration and start cadence are distinct values.
- Make precedence explicit: unavailable/unsafe ingress and storage, manual
  pause, schedule, then cadence/control policy.
- Resolve schedule decisions in this order: unavailable/unsafe admission,
  manual pause, blackout or force-closed override, privileged temporary
  force-open override, date exception, weekly window, then default closed.
  Reject overlapping equal-priority windows with conflicting setpoint/cadence
  profiles instead of choosing by declaration order.
- Handle DST, ambiguous/skipped local times, UTC clock correction, restart, and
  an active exposure crossing a boundary deterministically. An ambiguous start
  uses the earlier UTC occurrence and an ambiguous end the later occurrence; a
  nonexistent local boundary advances to the first valid instant. An admitted
  exposure completes across closure and retains its admission/profile revision,
  but no later capture starts.
- A forward clock correction recomputes the current interval and never creates
  catch-up captures. A backward correction never replays a consumed one-shot
  override or capture sequence; recurring admission follows the persisted
  effective UTC interval while monotonic cadence remains authoritative. Restart
  evaluates only the current/future interval and never backfills missed starts.
- A missing sunrise/sunset/twilight event makes that solar-relative window
  unavailable with a reason code. Capture remains closed unless the same
  revision declares an explicit fixed-time fallback.
- Version the solar-event algorithm and record the timezone/rule source used to
  expand each effective schedule interval.
- Persist versioned local configuration, validate and preview before apply, and
  activate at an explicit capture boundary with audit history.
- Show active window, active profile revision, next transition, and reason-coded
  admission state in the CameraAgent UI.

### Calibration library and virtual acquisition

- Store immutable bias, dark, flat, and defect references plus manifests,
  checksums, source frames, master-build recipe, validity, and activation state.
- Extend `docs/contracts/reference-calibration-v1.md` additively. Preserve and
  reconcile existing synthetic bundles, profile commit markers, paths,
  retention holds, and restart behavior; do not introduce a competing durable
  library format.
- Match references on camera/sensor identity, native/readout geometry, CFA,
  sample/container depth, gain, offset, exposure applicability, temperature,
  and effective interval.
- Reject missing, stale, corrupt, incomplete, ambiguous, or incompatible
  bundles with stable reasons.
- Add a deterministic VirtualSky calibration-acquisition mode compatible with
  configured ASI response models. Inject known bias, dark current, flat field,
  vignetting, and defects and build/select matching pseudo references.
- Prove correction improves declared numerical residuals while immutable raw
  and all ordered reference lineage remain verifiable.
- Add local calibration status/library/acquisition UI. Physical cover, panel,
  illumination, and hardware acceptance remain deferred.

### Virtual environmental acquisition and local history

- Reuse `environmental-observation-v1`, completed #103 persistence/association
  semantics, and completed #157 durable edge delivery as compatibility inputs.
  Local standalone history extends those facts without redefining provenance,
  units, quality, or optional delivery.
- Add configuration-driven virtual sources for ambient temperature, relative
  humidity, pressure, wind speed/direction/gust, precipitation/rain state,
  cloud cover, sky brightness/quality, and camera temperature. Add a versioned
  sky-temperature value only after contract review.
- Support periodic, before/after capture, every-Nth-capture, regime-change, and
  on-demand triggers without coupling periodic providers to image cadence.
- Generate deterministic location/time/seed scenarios with noise, stale,
  missing, contradictory, timeout, and failed-source states.
- Persist targetless local observations durably in standalone mode, associate
  them temporally with captures, and expose bounded local history and freshness.
- Keep optional central delivery as a separate consumer of local durable facts;
  central mode must not determine whether local acquisition or history exists.
- Feed matched fresh/stale/missing observations explicitly to cloud assessment,
  overlays, corner annotation, telemetry, and health.

### Pipeline and operator completion

- Configure every intended node and dependency explicitly. Raw durability is
  mandatory; optional nodes have versioned enable/disable policy.
- Validate the effective graph before activation. Disabling an upstream node
  must reject or explicitly disable its dependent subtree, never silently
  substitute data.
- Complete adapters for calibrated and rolling previews, object/constellation/
  cardinal/image-circle annotation, image-quality assessment, cloud assessment,
  weather overlay, durable local storage, and telemetry.
- Add configurable top-left, top-right, bottom-left, and bottom-right metadata
  overlays for identity/time, setpoint/schedule, environment/freshness, and
  catalog/calibration/stack/profile provenance. Missing values remain explicit.
- Preserve byte-identical reconstructable cardinal/image-circle/object/corner
  annotation after restart.
- Enable the real durable edge transient lane for the acceptance profile and
  surface causal/final assessment state in local operations and gallery views.
  A logging-only diagnostic must not be labeled fireball detection.
- Show per-node input/output, duration, status/reason, recipe/profile identity,
  artifact checksum, and lineage, with side-by-side role/variant comparison.

### Standalone operational acceptance

- Use ASI676MC 3552 x 3552 Bayer RGGB, truthful 12-bit samples in a 16-bit
  container, a versioned provisional 2.5 mm fisheye rig, five-second calibrated
  light exposures, and a separately declared cadence.
- Use the complete installed production catalog and exact catalog provenance.
  This means the verified 119,625-row production snapshot is installed and
  identified; each scene query remains bounded by declared magnitude and result
  limits rather than attempting to render every catalog row.
- Run pseudo calibration, rolling combination, previews, object/cardinal/corner
  annotation, virtual weather, cloud assessment/overlay, image quality, edge
  transient/fireball processing, storage, retention, and telemetry together.
- Verify known catalog objects and cardinal anchors at expected sensor/output
  coordinates, raw/calibrated residuals, checksums, dimensions, levels, recipe
  identities, ordered lineage, durable state, and rendered image usability.
- Before #211 is marked ready, pin the location, scene UTCs, rig/profile hashes,
  expected catalog row IDs, magnitude/result limits, coordinate tolerances,
  cadence, run duration/count, retention limits, storage headroom, fault/outage
  durations, backlog, and drain budget in its executable workload manifest.
- Exercise authenticated schedule/configuration, pause/resume, calibration,
  operations, gallery, artifact, environmental, and transient workflows in the
  real CameraAgent UI using VirtualSky rather than RandomImage.
- Restart at durable raw, calibration, annotation, and transient boundaries;
  exercise optional-step disable/failure, disk pressure, retention, and drain.
- Run with LogicHost, SQL Server, Redis, and MinIO absent and a deny sink that
  records zero central attempts. A later two-host gate proves optional export
  and recovery without becoming part of standalone correctness.
- Record the raw production budget. A 3552 x 3552 unpacked Bayer16 frame is
  25,233,408 bytes; a five-second start interval would produce 17,280 frames and
  about 436 GB decimal raw per day before derivatives. Retention and cadence
  must be explicit rather than inferred from exposure duration.

## Dependency Order

```text
configurable sensor/readout
  +--> local schedule and versioned profile control
  +--> calibration library and virtual acquisition
  +--> virtual environmental acquisition and local history

all four
  --> pipeline and operator completion
  --> standalone operational acceptance
  --> split-host deployment
  --> LogicHost UI
  --> final two-host E2E and fault matrix
```

The schedule, calibration, and environmental packages may proceed concurrently
only after the sensor/readout contracts they consume are merged. The final
standalone gate is the first complete ASI676MC/phase-12A full-resolution
composition and benchmark checkpoint; it builds on the earlier #171 standalone
ASI174 evidence.

## Validation Economy

The execution-time validation policy used for this work has been superseded by
the maintained risk-tier ladder in
[`agent-execution.md`](agent-execution.md). Retained acceptance evidence remains
valid only while its measured code, configuration, fixture, workload,
environment, and measurement logic remain unchanged.

## Deferred Boundaries

This correction does not claim physical ASI676MC color response, CFA parity,
lens calibration, calibration illumination, USB/readout timing, or detector
sensitivity. It preserves provisional source labels and creates the software
contracts and virtual evidence needed before those later hardware gates.
