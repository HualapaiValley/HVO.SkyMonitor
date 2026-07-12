# Virtual Planetarium and ASI174 Simulator Implementation

## 1. Purpose

This plan defines the first production-quality planetarium-backed virtual camera
slice. It connects the local catalog, astronomy transforms, optical projection,
sensor rendering, CameraAgent capture, and annotation paths so they use one
reproducible celestial scene.

The first milestone is a monochrome ASI174MM-compatible virtual camera with a
zenith-pointing fisheye lens at Hualapai Valley Observatory. The second milestone
adds ASI174MC color compatibility without claiming raw Bayer fidelity. Blazor UI
work is out of scope unless a minimal diagnostic surface is the cheapest way to
verify the running capture path.

This is an incremental equidistant-fisheye slice within phases 1, 2, and 5.
Completing it does not complete those phases: rectilinear, telescope, remaining
fisheye mappings, solar-system, and full compatibility-matrix work remain.

This document refines phases 1, 2, and 5 of
[`docs/project-plan.md`](../project-plan.md) and the requirements in
[`docs/virtual-camera.md`](../virtual-camera.md). Those documents remain the
architecture and product authorities; this document fixes the implementation
fixtures, work order, and validation evidence for this slice.

## 2. Fixed Reference Fixture

### 2.1 Observatory

Use the legacy persisted V5 coordinate fixture named **Hualapai Valley
Observatory**:

| Field | Value |
| --- | --- |
| Name | `Hualapai Valley Observatory` |
| Latitude | `35.347` degrees north |
| Longitude | `-113.878` degrees, east-positive convention |
| Time zone | `America/Phoenix` |
| Elevation | synthetic `0 m`, used only by the initial geometric fixture with refraction disabled |

Do not silently substitute the current host values `35.5599378`,
`-113.9119818`, and `520 m`; their provenance is not recorded. Elevation remains
configurable and may replace the fixture value only after a source is documented.

The location comes from the V5 persisted configuration at commit
`8fd662aca22e6b595f4a1882bab5c6fa319acb7b`; see
[`docs/reference-code.md`](../reference-code.md). Longitude is west and therefore
negative in the repository's east-positive convention.

### 2.2 ASI174MM sensor

The canonical monochrome profile is:

| Field | Value |
| --- | --- |
| Sensor profile | `VirtualAsi174Mm` |
| Sensor | Sony IMX174 / ASI174MM-compatible geometry |
| Width | `1936 px` |
| Height | `1216 px` |
| Pixel size | `5.86 um` square |
| Principal point | `(968, 608)` in continuous pixel-edge coordinates before calibration |
| Raw format | unsigned 16-bit little-endian samples, two bytes per pixel |
| Row layout | tightly packed, stride `1936 * 2`, unless an explicit stride is supplied |
| Boresight | altitude `90 deg`, azimuth `0 deg` |
| Roll | `0 deg` |
| Horizontal flip | configurable; disabled in the canonical analytic fixture |

Pixel coordinates are continuous from the top-left sensor edge. Pixel `(0,0)`
has center `(0.5,0.5)`. For the canonical unflipped zenith basis, north maps
toward negative image Y and east toward positive image X. At the full-resolution
horizon the expected cardinal coordinates are north `(968,12.16)`, east
`(1563.84,608)`, south `(968,1203.84)`, and west `(372.16,608)`.

The virtual sensor response is a simulation. Read noise, dark current, full
well, gain conversion, quantum efficiency, vignetting, and defects must be
labeled as simulation parameters unless a hardware source is cited.

### 2.3 Fisheye optics

Legacy references identify a Fujinon `FE185C086HA-1`, nominally `2.7 mm` and
approximately `f/1.8`, but they conflict on projection and field coverage:

- one preset describes an approximate equisolid lens and roughly `146 x 94 deg`;
- the active synthetic rig uses an equidistant `185 x 185 deg` normalized image
  circle;
- the old active renderer ignores physical focal length when scaling the image
  circle.

Therefore the first conformance profile is explicitly synthetic and calibrated
for validation, not presented as a measured Fujinon model:

| Field | Value |
| --- | --- |
| Profile | `VirtualFisheye180Equidistant` |
| Projection | equidistant, `r = f * theta` |
| Total angular field | `180 deg` across the usable circle |
| Image-circle radius | `0.98 * min(width, height) / 2` = `595.84 px` at full resolution |
| Focal length in pixels | `imageCircleRadius / (pi / 2)` |
| Horizon | image-circle edge |
| Principal point | sensor center |

This profile directly matches Stellarium's `ProjectionFisheye` geometry. Add a
separate named Fujinon candidate profile only after the synthetic path passes;
keep its mapping, circle, distortion, and calibration version explicit and mark
all unmeasured values as provisional.

### 2.4 Canonical times and astronomy model

Use these three minimum fixtures:

1. `2025-01-15T08:00:00Z` for the primary zenith fisheye fixture.
2. `2025-01-15T08:59:50.170Z`, approximately one sidereal-hour angle after the
   primary fixture.
3. `2025-07-15T08:00:00Z` for second-season coverage.

Record the final object IDs, expected visibility, and reference pixels in
machine-readable manifests. Do not derive expected values from the code under
test.

HYG coordinates are J2000/ICRS catalog coordinates. Before generating fixtures,
implement and document the first production model: precess J2000 coordinates to
the observation date using a cited IAU model; omit nutation, annual aberration,
proper motion, and parallax unless their required source fields and tests are
implemented. Geometric fixtures disable refraction. Record all enabled and
omitted effects in each manifest.

## 3. Architecture and Ownership

### `HVO.SkyMonitor.AgentCore`

Own only transport-neutral immutable configuration and provenance contracts:

- typed lens kind and projection model;
- sensor geometry and raw response mode;
- principal point, image-circle radius, calibrated focal lengths, FOV, crop,
  flip, distortion/calibration version, boresight, and roll;
- scene/catalog/projection/sensor recipe identifiers recorded in metadata.

Do not add astronomy calculations, SQLite, SkiaSharp, ASP.NET, or host services.

### `HVO.SkyMonitor.Astronomy`

Own deterministic celestial and optical geometry:

- UTC, sidereal time, RA/Dec to geometric Alt/Az;
- optional refraction policy;
- ENU vectors and camera basis from boresight and roll;
- forward/inverse fisheye and perspective projection;
- storage-neutral catalog records, queries, and selection behavior;
- visible-scene construction independent of pixel format;
- later solar-system ephemerides and constellation topology.

### `HVO.SkyMonitor.Imaging`

Own reusable image and sensor algorithms:

- linear monochrome and RGB scene buffers;
- magnitude-to-flux mapping and deterministic point-spread function;
- sky background, vignetting, exposure/gain response, noise, defects, clipping,
  and quantization;
- preview conversion and annotation drawing from already-projected objects.

Imaging consumes projected scene records. It must not calculate sidereal time,
query a catalog, or construct host configuration.

### `HVO.SkyMonitor.Catalog.Sqlite`

Add this shared infrastructure adapter after Astronomy catalog contracts are
frozen. It owns `Microsoft.Data.Sqlite`, snapshot schema checks, read-only
connections, checksum validation, and process-cached query execution. Both hosts
may reference it. It references Astronomy contracts, but Astronomy must not
reference it. Do not place SQLite or EF Core in Astronomy.

### `HVO.SkyMonitor.CameraAgent.Common`

Own integration into the edge capture pipeline:

- construct the scene request from loaded observatory, rig, capture time, and
  module options;
- call shared Astronomy and Imaging services from `VirtualSkyCameraModule`;
- pass the same scene identity/projected-object data to annotation processing;
- preserve ordinary capture, artifact, preview, storage, upload, and telemetry
  behavior without simulator-only host branches.

### Hosts

CameraAgent and LogicHost register the concrete SQLite catalog adapter at their
composition roots. LogicHost may
consume the same Astronomy and Imaging APIs for conformance and future
reprocessing, but neither host may implement private projection math. UI changes
are not required in this phase.

## 4. Catalog Snapshot

Use a versioned, local, read-only HYG SQLite snapshot. The V6 reference records:

- catalog release: HYG `4.2`;
- source: `https://astronexus.com/projects/hyg`;
- license: [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/);
- reference snapshot name: `hyg_v42.sqlite`;
- reference SHA-256:
  `95A720585139452227F2201BDDB03E8DD7E98094E378D3FFB3D9EBC1AFAA2C76`.

`hyg_v42.sqlite` is a legacy-derived artifact, not an upstream HYG download.
Before using it, independently verify the recovered snapshot checksum, source
CSV, license, attribution, and CSV-to-SQLite preprocessing. Any distributed
snapshot or subset must retain attribution and ShareAlike-compatible treatment.
Do not make normal capture depend on a network download. Provide:

1. A small checked-in deterministic test fixture containing selected bright
   stars and the minimum schema needed by tests, with source, attribution,
   license, and derivation steps.
2. A reproducible acquisition/build script for the full snapshot that records
   and verifies separate SHA-256 values for the exact compressed source CSV,
   decompressed CSV, and generated SQLite snapshot before installation. The
   legacy SQLite checksum alone does not prove source identity or preprocessing.
3. A configured local snapshot path for CameraAgent and LogicHost deployment.
4. Process-cached, read-only access with deterministic magnitude-then-ID order.
5. Metadata containing name, version, source URL, license, checksum, and schema
   or preprocessing version.

The first query policy is magnitude `<= 6.5`. The SQLite adapter applies
magnitude and coarse sky-region filtering and returns deterministically ordered
candidates without applying the visible-result limit. The Astronomy visible-
scene builder performs exact projection/visibility rejection, then applies
`MaximumResults` to the brightest visible objects in stable magnitude-then-ID
order. The adapter must not reference projection services or Imaging. Use `2000`
as the maximum visible result count, not as a global pre-projection truncation.
Add a test proving brighter out-of-frame rows cannot displace an in-frame star.

## 5. Visible Scene Contract

Add an immutable scene request containing:

- UTC instant;
- observatory location and refraction parameters;
- sensor and calibrated optics;
- orientation, flips, crop, and horizon policy;
- catalog query and catalog metadata;
- requested solar-system bodies when implemented.

The visible-scene builder returns stable records containing at least:

- catalog/body ID and display name;
- object kind;
- equatorial and geometric/apparent horizontal coordinates;
- camera direction and projected pixel;
- apparent magnitude and optional color index;
- visibility/rejection reason where useful for diagnostics;
- catalog, projection, and algorithm versions.

One scene result is the geometry authority for raw rendering and annotation.
Rendering and annotation must not independently repeat catalog selection or
coordinate conversion.

## 6. ASI174MM Rendering Milestone

Implement in this order:

1. Fill a deterministic linear background inside the image circle; zero pixels
   outside the circle.
2. Convert magnitude to relative linear flux using a documented formula.
3. Spread flux through a deterministic, bounded PSF. A centered PSF conserves
   configured energy within `0.5%`; energy outside the sensor at an edge is lost
   and the in-frame kernel is not renormalized.
4. Apply radial vignetting as a configurable linear factor.
5. Apply exposure and gain without moving celestial geometry.
6. Add deterministic bias/read noise, optional shot noise, dark current, and
   fixed defects using an explicit seed and stable algorithm version.
7. Clamp and quantize to unsigned 16-bit little-endian Mono16 samples.

Raw frames contain no labels, constellation lines, display gamma, or tone
mapping. Preview generation may tone-map separately.

The first milestone is complete only when:

- the full `1936 x 1216` profile captures through `ICameraModuleFactory`;
- a reduced `484 x 304` fixture (exact quarter scale) runs quickly in CI with
  principal point `(242,152)` and image-circle radius `148.96 px`;
- identical inputs produce identical bytes and checksum;
- advancing time moves known stars consistently;
- changing roll/boresight changes projected geometry predictably;
- changing exposure/gain changes statistics but not projected positions;
- raw, preview, storage, latest-frame, telemetry, and upload paths remain
  ordinary CameraAgent paths;
- renderer and annotation positions agree within the stated tolerance.

## 7. Annotation Milestone

Replace the current hard-coded zenith marker and independent perspective
projector. The annotation step must consume the same calibrated scene context or
projected visible-object records as the virtual capture.

Initial annotations may be minimal testable marks plus names for selected bright
stars. Requirements:

- annotations are derivatives and never alter raw data;
- labels are bounded and deterministic;
- horizontal flip, crop, resize, and preview scaling are explicit transforms;
- a test proves a selected catalog star's raw centroid and annotation anchor
  agree after preview scaling;
- later constellation segments resolve through stable object IDs from the same
  scene.

VirtualSky constellation completeness is implemented. Topology endpoints are
resolved by HIP identifier independently of magnitude and result limits, edges
are projected as bounded great-circle chord sequences, and visible portions are
clipped to sensor, image-circle, horizon, and projection-domain boundaries.
Real-camera and virtual-camera behavior remain distinct:

- For a real-camera frame, annotation may augment its geometry with topology
  endpoints omitted by normal magnitude, label, or visible-result selection so
  it can draw and clip the figure. It must never synthesize missing star pixels
  or imply that a catalog endpoint was detected in the physical image.
- For VirtualSky acquisition only, the
  `IncludeConstellationEndpointStars` render option includes otherwise omitted
  topology stars in the generated virtual scene and raw sensor simulation when
  constellation figures are requested. This is a virtual render-recipe choice,
  not generic annotation behavior, and must be recorded in frame provenance.
  Profiles that enable constellation lines should set the render option
  explicitly rather than allowing the annotation step to mutate raw data.

When the complete figure lies within the calibrated view, all resolvable
segments should be drawn. When a figure crosses the sensor, image-circle, or
horizon boundary, draw and clip only the geometrically visible segment portions
instead of dropping the figure because one endpoint is off-screen.
Invalid/back-facing projection domains must not be bridged with a straight line.

The constellation overlay exposes explicit derivative style options for line
value/color, thickness, and opacity, with deterministic defaults for Mono8 and
RGB24 previews. Acceptance tests cover a fully visible figure with an
endpoint omitted from the base visible-object selection, one-endpoint and
multi-segment boundary clipping, mirrored orientation, and stable output for
each supported preview format.

No Blazor UI is required. A command-line fixture generator or test artifact is
preferred for visual inspection.

## 8. Stellarium Validation

### 8.1 Role

Stellarium is an independent geometry and visual reference, not a byte-for-byte
golden-image oracle. The repository's deterministic raw checksums are internal
regression fixtures; Stellarium validates location, time, orientation, field
coverage, projection scale, and selected bright-object positions.

Stellarium `ProjectionFisheye` is azimuthal equidistant and directly validates
the canonical synthetic lens. It cannot directly validate an equisolid lens.

### 8.2 Automation

Add an idempotent setup script or documented container that installs pinned
Ubuntu 24.04 packages:

```bash
sudo apt-get update
sudo apt-get install -y --no-install-recommends \
  stellarium=23.4-2build3 \
  stellarium-data=23.4-2build3 \
  xvfb xauth libgl1-mesa-dri imagemagick
```

Add a repository script such as `scripts/validate:stellarium` that:

1. Creates a fresh temporary Stellarium user directory.
2. Uses `TZ=UTC`, `LIBGL_ALWAYS_SOFTWARE=1`, `GALLIUM_DRIVER=llvmpipe`,
   `QT_SCALE_FACTOR=1`, and a fixed `1024 x 1024` Xvfb display.
3. Runs a checked-in `.ssc` startup script.
4. Sets observer longitude `-113.878`, latitude `35.347`, altitude `0`, fixed
   UTC, azimuthal mount, zenith view, `ProjectionFisheye`, disk viewport, and
   `180 deg` FOV.
5. Disables atmosphere/refraction for the geometric fixture, landscape, fog,
   Milky Way, nebulae, labels, planets, twinkle, and luminance adaptation.
6. Sets the same magnitude limit as the fixture.
7. Calls `core.setTimeRate(0)`, `core.moveToAltAzi("90d","0d",0)`, and
   `StelMovementMgr.zoomTo(180,0)`, then waits a bounded two seconds for render
   settling.
8. Emits selected landmark coordinates with `core.getScreenXYFromAltAzi`,
   verifies width, height, projection, FOV, location, UTC, flips, and disk
   viewport, saves output with `core.saveOutputAs`, captures a PNG, and calls
   `core.quitStellarium()` under a process timeout.
9. Transforms Stellarium coordinates into canonical sensor coordinates with
   `x = 968 + (x_stellarium - 512) * (595.84 / 512)` and, because
   `core.getScreenXYFromAltAzi` reports Y upward from the bottom edge,
   `y_top = 1024 - y_stellarium`, then
   `y = 608 + (y_top - 512) * (595.84 / 512)`. Equivalently, normalize
   both systems by usable image-circle radius. Record native Stellarium,
   normalized unit-circle, and sensor coordinates. Synthetic cardinal landmarks
   must agree within `1.5 px`; catalog stars use a separately documented
   tolerance covering astronomy-model difference and screen rounding.
10. Writes screenshots, logs, manifests, and diffs under `TestResults/` or another
   ignored artifact directory.

The command must use a writable screenshot directory inside the temporary user
directory or explicitly enable Stellarium's screenshot-directory permission.

Do not commit Stellarium screenshots without reviewing bundled-data licensing
and attribution. A transient CI artifact is preferred. The automated .NET test
gate must not require Stellarium packages; run this validation through an
explicit script and an optional manual/scheduled GitHub workflow.

### 8.3 Validation hierarchy

1. Exact analytic tests for synthetic ENU rays and each projection equation.
2. Independent catalog-star RA/Dec fixtures and published astronomy cases.
3. Optional Astropy/ERFA fixed fixtures for celestial-coordinate cross-checks,
   with IERS network downloads disabled and documented model tolerances.
4. Stellarium screen-coordinate checks for end-to-end convention and scale.
5. Stellarium screenshots for visual diagnostics and operator review.

Image-wide pixel equality against Stellarium is forbidden because Stellarium,
Qt, Mesa, catalog, antialiasing, and display rendering versions change pixels.

### 8.4 Headless rendered-scene and constellation validation

Add a pinned headless Stellarium container after the local package workflow is
stable. The image should pin its Ubuntu base by digest, exact Stellarium and data
package versions, Mesa llvmpipe, Xvfb, fonts, locale, and startup script. A
repository command should build/run the image without host GUI dependencies and
write diagnostics under `TestResults/stellarium/`. Run it as an explicit local
command and manual or scheduled workflow, not as a dependency of every .NET unit
test invocation. A small opt-in integration category may invoke the container
when Docker is available.

For the same fixed UTC, observer, projection, orientation, magnitude limit, and
sensor normalization, validation must compare:

- selected virtual raw star centroids and annotated star anchors to normalized
  Stellarium object coordinates;
- image-circle/horizon position, projection scale, cardinal orientation, roll,
  and horizontal flip;
- constellation endpoint coordinates and visible/clipped segment boundaries for
  at least one complete figure and figures crossing the image circle and sensor
  edges;
- VirtualSky output with `IncludeConstellationEndpointStars` both disabled and
  enabled, proving added simulated endpoints use the same externally validated
  coordinates;
- a rendered HVO preview and Stellarium screenshot as retained human-review
  diagnostics, with coordinate/error reports as the automated pass/fail oracle.

Stellarium constellation lines are not automatically a topology oracle. The IAU
standardizes constellation boundaries and names, not stick-figure connectivity.
Exact HVO segment connectivity remains validated against the pinned D3-Celestial
source and generated checksum. Compare HVO figures directly with Stellarium only
after pinning a Stellarium sky culture and proving its endpoint topology matches
the selected D3-Celestial figure; otherwise use Stellarium to validate each
endpoint's celestial and screen position independently.

## 9. ASI174MC Follow-On

After the ASI174MM milestone and shared annotations pass, reuse the identical
visible scene and optics for `VirtualAsi174McRgb`:

- geometry remains `1936 x 1216`, `5.86 um`, same principal point and lens;
- derive linear star color from HYG B-V/color index with a documented fallback;
- render a linear RGB scene and apply configurable channel response, white
  balance, vignetting, exposure/gain, and deterministic channel noise;
- quantize explicitly to packed `Rgb24` in documented channel order;
- prove Mono and RGB profiles have matching object geometry;
- prove color response changes channels but not object positions;
- add reduced and full-resolution configuration samples and tests.

This is rendered RGB compatibility, not raw ASI174MC emulation. Do not claim
hardware-faithful color until a sourced CFA pattern and response are available.

`VirtualAsi174McBayer` is a later milestone requiring frame contracts for CFA
pattern, sample depth, packing, stride, endianness, black/white levels, and
channel gains. Bayer sampling remains raw; demosaicing is an Imaging derivative.

## 10. Test Matrix and Quality Gates

### Astronomy tests

- finite/range validation for every public value type;
- sidereal and RA/Dec-to-Alt/Az reference cases;
- ENU cardinal axes, zenith, horizon, and below-horizon cases;
- camera basis at zenith and arbitrary boresight/roll;
- forward/inverse projection center, cardinal, edge, singularity, off-sensor,
  and invalid-domain cases;
- Hualapai fixtures at two UTC instants and a second latitude fixture;
- deterministic catalog ordering, magnitude/result bounds, read-only snapshot,
  metadata, checksum failure, and cancellation/lifetime behavior.

### Imaging tests

- magnitude-to-flux monotonicity and reference values;
- PSF symmetry, bounded support, edge behavior, and energy tolerance;
- image-circle mask and vignetting;
- Mono16 byte order, clipping, zero/maximum exposure, gain scaling, deterministic
  noise, and differing-seed behavior;
- malformed layouts and overflow protection;
- RGB channel order, color-index fallback, channel response, and geometry parity.

### CameraAgent tests

- configuration-driven ASI174MM/MC creation;
- reduced and full frame dimensions/lengths;
- fixed checksum plus object-centroid and image-statistic assertions;
- time, boresight, roll, flip, exposure, and gain behavior;
- raw/preview/annotation agreement;
- ordinary pipeline, persistence, latest-frame, telemetry, and upload behavior;
- invalid catalog, optics, and sensor configuration fails before capture;
- `cameraagent.sample.json` uses the required `484 x 304` reduced profile and
  verifies its scaled principal point and image circle.

### External validation

- checked-in Stellarium script and fixture manifest;
- reproducible one-command validation;
- selected bright objects within a tolerance split into astronomy-model,
  viewport-normalization, and screen-rounding components;
- diagnostic PNG/diff/log output on failure;
- version and settings recorded with every run.

### Required gates

- Red-green-refactor for each deterministic behavior.
- Add a checked-in coverage enforcement command before claiming numeric gates.
  It must fail when new projection, camera-basis, visible-scene, or frame-layout
  files fall below 95% line or 90% branch coverage; new renderer/catalog-adapter
  files fall below 90% line or 85% branch coverage; or the reviewed aggregate
  solution baseline regresses.
- No deleted, weakened, skipped, or broadly recategorized tests.
- No broad analyzer suppressions and no new warnings.
- Debug and Release builds pass with warnings as errors.
- Full solution tests and formatting pass.
- Package vulnerability audit is clean.
- Full-resolution render reports elapsed time and peak/allocation observations;
  do not impose an unmeasured Raspberry Pi budget until target hardware data is
  available.

## 11. Work Streams and Integration Order

One primary agent owns integration, contracts, and final validation. Parallel
agents may research or implement non-overlapping work only after contracts are
fixed.

1. **Primary integration agent**: baseline, contract decisions, task graph,
   merges, conflict resolution, end-to-end module wiring, and all final gates.
2. **Astronomy agent**: camera basis, projection context, storage-neutral catalog
   contracts, and visible-scene builder with focused Astronomy tests.
3. **Catalog agent**: shared SQLite adapter and focused adapter tests after
   Astronomy contracts freeze.
4. **Imaging agent**: Mono16 then RGB sensor renderer using the frozen projected
   scene contracts, with focused Imaging tests.
5. **Validation agent**: Stellarium setup/script/manifests and independent
   coordinate/image diagnostics; it must not define production projection math.
6. **Review agent**: post-slice architecture, numerical, performance, and test
   review; findings are repaired by the owning agent and revalidated centrally.

Do not let multiple agents edit the same contracts or project files. Research
agents return evidence; the primary agent verifies it. Complete ASI174MM and
annotation gates before beginning ASI174MC rendering.

| Owner | Exclusive edit scope |
| --- | --- |
| Primary | AgentCore contracts, CameraAgent.Common, host DI, solution/package files, sample configuration, integration tests, and cross-project API approval |
| Astronomy | all Astronomy contracts and implementation under `src/HVO.SkyMonitor.Astronomy` plus `tests/HVO.SkyMonitor.Astronomy.Tests` |
| Catalog | new SQLite adapter project and its focused tests after Astronomy contracts freeze |
| Imaging | `src/HVO.SkyMonitor.Imaging`, `tests/HVO.SkyMonitor.Imaging.Tests` after projected-scene contracts freeze |
| Validation | new Stellarium scripts, `.ssc` files, manifests, and validation documentation only |

The primary agent owns annotation pipeline integration; Imaging owns only
annotation drawing primitives. The primary agent approves cross-project API
designs without editing files assigned to another agent. Contract revisions are
returned to the owning agent.

## 12. Deliverables

- Extended immutable rig/optics/provenance contracts and sample configurations.
- Read-only versioned HYG catalog fixture and reproducible full-snapshot setup.
- Shared camera-basis/projector and visible-scene APIs.
- Deterministic linear Mono16 ASI174MM renderer and VirtualSky integration.
- Shared annotation geometry with raw/preview agreement.
- Automated Stellarium validation scripts and manifests.
- ASI174MC RGB compatibility profile and renderer after Mono acceptance.
- Focused unit, conformance, integration, checksum, geometry, and statistics
  tests meeting coverage thresholds.
- Updated project-plan status, virtual-camera documentation, catalog attribution,
  sample configuration, and operational instructions.

The phase is not complete when an image merely looks plausible. Completion
requires reproducible catalog provenance, independent positional validation,
shared renderer/annotation coordinates, deterministic raw output, ordinary
CameraAgent pipeline operation, and all quality gates above.

This slice may move phases 1, 2, and 5 forward but must not mark them complete.
Reduced-resolution Mono16 and RGB24 coverage now includes equisolid,
orthographic, stereographic, perspective, and telescope profiles, with selected
full-resolution fixtures and operator samples. Initial constellation topology,
an offline Astronomy Engine ephemeris for the Sun through Neptune, fixed JPL
Horizons reference cases, complete D3-Celestial constellation topology, and
cross-host scene conformance are now implemented. Outbox-aware retention now
protects pending payloads, metadata, and index entries across outages and
restarts. Remaining long-run hardening includes graceful channel drain, capture
failure backoff, disk-pressure policy, and accelerated soak validation.

## 13. Baseline Status and Continuation

Status on 2026-07-12: **implementation baseline ready for review; original
prompt acceptance remains in progress**. This baseline intentionally does not
mark project phases 1, 2, or 5 complete.

The baseline includes:

- shared astronomy, camera-basis, fisheye/perspective projection, visible-scene,
  catalog, planet, and constellation services;
- deterministic ASI174MM Mono16 and ASI174MC RGB24 compatibility rendering;
- configuration-driven VirtualSky capture through preview, annotation, rolling
  combination, storage, latest-frame, telemetry, and outbox components;
- persisted scene provenance, offline catalog packaging, and HYG attribution;
- shared object, constellation, image-circle, and cardinal annotations;
- independent constellation endpoint lookup, clipped great-circle geometry,
  VirtualSky endpoint-star inclusion, and configurable line styling;
- ordinary-path production-host integration coverage from VirtualSky capture
  through rolling combination, preview, annotation, filesystem persistence,
  latest-frame publication, telemetry, and durable outbox selection;
- fixed reduced/full ASI174MM projected pixels, raw Sirius centroids, image
  statistics, and checksums under the electron-domain sensor recipe;
- exact CameraAgent-level movement evidence for the documented sidereal hour,
  boresight tilt, 90-degree roll, and horizontal flip;
- fixed full-resolution canonical equidistant ASI174MC RGB24 checksum and image
  statistics plus configured RGB preview/annotation pipeline coverage;
- versioned `hualapai-asi174-conformance-v1.json` evidence consumed by Astronomy
  and CameraAgent tests for primary, sidereal-hour, second-season,
  second-latitude, orientation, reduced/full Mono16, and full RGB24 cases;
- pinned Stellarium automation and analytic/cross-host conformance tests;
- an additional ASI178MC RGGB16 development profile with demosaicing, Bayer
  stacking, and a provisional Fujinon FE185C057HA-1 candidate calibration.

The canonical default sample remains the prompt-required exact-quarter ASI174MM
fixture (`484 x 304`, principal point `(242,152)`, radius `148.96`). ASI178MC
hardware comparison uses `cameraagent.asi178mc-comparison.json` and does not
replace that conformance fixture.

PR #55 merge scope closes the VirtualSky baseline as follows:

1. The storage-neutral candidate contract includes an optional conservative
   J2000 spherical cap. Astronomy derives projection/horizon caps and retains
   exact visibility and final result limiting; in-memory, CSV, and SQLite-backed
   catalogs preserve deterministic ordering while applying the hint.
2. The coverage gate fails when required reports omit a high-risk or renderer/
   catalog source file, and focused geometry tests cover projection domains,
   clipping boundaries, subdivision, and degenerate chords.
3. Shared public APIs added by this baseline include XML documentation, and the
   final PR evidence retains Debug/Release build, test, vulnerability, coverage,
   and format results.

Real-camera geometry-only constellation overlays are explicitly deferred to
issue #56. Pinned containerized Stellarium validation is explicitly deferred to
issue #57 because the host executable/package mismatch prevents reproducible
evidence; it remains an external manual/scheduled oracle rather than a normal
.NET test dependency.

Continue physical ASI178 calibration separately after that baseline work:

1. Confirm the installed lens barrel marking. Current image-circle geometry most
   strongly indicates the 1.8 mm FE185C057HA-1.
2. Acquire a clear synchronized frame and match at least 10-20 stars using the
   observed orientation: north near image-up, east image-left, west image-right.
3. Fit principal point, boresight tilt, roll, projection family, and radial
   distortion; replace the provisional lens profile only with residual evidence.
4. Characterize hardware mono-bin output and controlled bias, dark, and flat
   response without changing immutable Bayer raw data.
