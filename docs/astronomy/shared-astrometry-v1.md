# Shared astrometry library — bounded integration slice

Issue #1088 follows the standalone #1084/#1085/#1087 experiments. The operator authorized reusable shared code; this document does not activate all of #520–#526, change the production catalog, or enable a host pipeline. Existing Operations work and #1055 remain separate. The attempted coordination note on #520 could not be posted through the current authorization route; the explicit scope and claim are recorded on #1088.

## Ownership and API

- `HVO.SkyMonitor.Astronomy`: input/catalog/calibration/assessment contracts, blind spherical pattern acquisition, warm local refinement, evidence identity and native pixel/sky/frame mapping
- `HVO.SkyMonitor.Imaging`: linear source detection and Bayer reconstruction/luminance
- No new project, native library, host reference, database, web dependency or runtime service is added
- CameraAgent and LogicHost already consume these shared owners; no cross-host reference is introduced

Typical flow:

1. Decode an immutable source into a declared linear image in the calibrated input pixel space
2. For raw Bayer data, reconstruct with the actual post-readout CFA phase before computing linear luminance. Never feed stretched display RGB or generically interpolated raw CFA samples to scientific detection
3. Call `StellarDetector.Detect` with an aperture/known-validity mask and explicit shape/noise settings. Convert returned positions/flux to `AstrometricDetection`; the detector's PSF moments are not centroid covariance
4. Build capture-authoritative `AstrometricFrameContext`, one reusable `AstrometricCalibration`, and an immutable supplied `AstrometricCatalogData`
5. Use `AstrometricSolver.Solve` for blind acquisition or `Refine` with a prior accepted assessment. `SolveAsync` consumes `IAstrometricCatalogSource`; the adapter must honor cancellation and its result must state requested-coverage completeness
6. Retain an accepted result only after its status/identity checks. A rejection, unavailable catalog, timeout, or cancellation cannot change the previous object. Cold recovery is an explicit caller decision, not an automatic hidden request
7. Use `AstrometricMapping` for accepted pixel↔sky and frame↔frame mapping

The numerical API never receives renderer truth, known catalog correspondences, a camera-product name, or a nearby orientation seed for blind acquisition. The public inputs contain source centroids and numeric calibration only. Calibration pose fields are ignored by blind ray acquisition; warm refinement deliberately uses the accepted prior.

## Compact per-frame evidence

Retain optical calibration once by immutable identity. `AstrometricFrameAssessment` contains only:

- Source capture/artifact IDs, descriptor/payload hashes, observer identity/location, exact UTC exposure endpoints and derived midpoint
- Detector/settings provenance, calibration/catalog/catalog-selection identities, solver/settings identity, and explicit coordinate/refraction conventions
- Mode, status and reason; measured fitted parameters only when accepted
- Boresight altitude/azimuth in degrees, roll in degrees, uniform focal-scale multiplier, boresight right ascension in hours and declination in degrees at the exposure midpoint
- Pixel RMS, inlier/verification/expected-isolated counts and spatial coverage; uncalibrated quality score and explicitly unavailable covariance
- Optional association-diagnostic identity and prior assessment reference

Associations are separate bounded diagnostics in `AstrometricSolveResult`, not embedded in every compact assessment. Execution timings are also separate and do not enter semantic identity. Canonical assessment serialization validates source/convention fields, accepted-state quality/reference invariants, numerical bounds and SHA256 content identity. Duplicate JSON properties and injected computed mapping flags are rejected; probability/uncertainty fields are parsed and must match the declared unavailable/uncalibrated semantics. It does not authenticate the capture source or sign the evidence.

There is no database schema or capture-descriptor mutation in this change. A future typed Metadata product should store the result once and keep artifact/assessment/calibration references in per-frame joins, rather than repeat full catalog scenes or calibration objects. This preserves #1055's direction without taking over its scene-deduplication implementation.

### Measured versus predicted

An accepted assessment supplies a measured mapping. Failed frames have no fitted parameters and cannot be constructed as `AstrometricMapping`. Their prior-assessment reference is provenance, not proof that the prior is valid for the current image. A future caller may display a last-good prediction only with an explicit predicted/unverified label and compatible time/location/readout/calibration checks; it must not copy previous RMS or quality as a fresh measurement.

`ProjectedSceneKind.ImageRegistered` alone does not prove a fit, and current `ProjectedSceneV1` contains no measured-solution reference. This change does not flip that enum or change that schema. Existing physical staging remains Predicted.

## Coordinates and clock trust

This version explicitly supports fixed-position J2000 catalog coordinates precessed with the existing IAU1976 model to mean equatorial coordinates of date. Proper motion, aberration, parallax, atmospheric refraction and a HYG4.4 epoch/photometric upgrade are not silently inferred. Other declared coordinate models are unavailable. Altitude is geometric; pixels use continuous top-left pixel-edge coordinates.

RA/Dec is the optical boresight, which need not coincide with the rectangular image center when the principal point is offset. A fisheye has position-dependent angular scale: `LocalPixelScale` returns separate local x/y arcseconds per pixel, or unavailable components at the aperture edge. It is not one global TAN/WCS scale.

The exposure midpoint is computed from capture UTC endpoints; mapping across frames precomputes local ENU↔J2000 rotations, including the date change. Geometric ground/outside-aperture samples are invalid for celestial registration. Mapping aligns exposure centroids; it does not remove motion already integrated within one exposure.

**Fit quality does not validate UTC or observing location.** A deliberate10-minute timestamp error can still yield a high-quality fit with about2.5degrees of changed inferred orientation. Preserve capture-authoritative timestamp/site provenance. Malformed ranges can be rejected; physically inaccurate but well-formed metadata cannot be identified from this fit alone.

The requested Operations clock-health/time-sync controls are a separate UI/host capability dependency. Future presentation may report actual synchronization status, last sync and offset, and expose a confirmed resync only where a supported authorized host command exists. This library adds no time-sync command, security setting change, clock-health producer or Operations edit.

## Limits and catalog adapter gap

The production algorithms contain no synthetic catalog. They consume supplied immutable entries and metadata. The current in-memory triangle implementation retains deliberately bounded search limits inherited from the reviewed experiment:

- At most 2,500 requested and solved catalog entries (no deeper selection is qualified; see [Deep selection profile](#deep-selection-profile)) and an internal 8,192-entry catalog data capacity; 1,500 index stars, 2 million indexed triangles, 10,000 supplied detections
- Up to120 brightest eligible stars for wide-field indexing; full eligible supplied set for perspective indexing
- Default28 bright image detections,192 sampled triangles per scale,200000 hypotheses,32 retained candidates
- Focal multiplier0.90–1.10 in0.01 steps; valid radial-domain floors enforced;20 local iterations
- Cooperative numerical-work budgets:15s cold,500ms warm; cancellation checked during index/search/refinement
- Warm correction limited to1degree and±2% relative focal scale, intersected with the original absolute MinimumFocalScale/MaximumFocalScale interval and physical projection domain; default prior maximum age600s; observer/calibration/catalog/actual-selection/settings mismatch rejects
- Image processing bounds:16M pixels and4096 detector candidates; candidate exhaustion is explicit

Catalog materialization is outside the numerical-work timer and is governed by the injected adapter/caller cancellation. Timers are cooperative, not hard real-time termination. Limits reject or return unavailable/budget outcomes; there is no silent full-catalog completeness claim.

No default production HYG adapter is wired here. The existing `ICelestialCatalog`/`Catalog.Sqlite` remain read-only catalog owners. An adapter must request a declared magnitude ceiling, use authoritative snapshot metadata, detect limit exhaustion and report incomplete coverage. AstrometricCatalogData requires a finite CompletenessMagnitudeLimit; all synchronous, asynchronous-provider and warm calls refuse a selection whose declared limit is below MaximumCatalogMagnitude, or whose completeness flag is false. The declaration and flag are included with the rows in SelectionIdentitySha256, so a coverage change invalidates warm reuse. The constructor has no implicit/default completeness ceiling. A fixed `MaximumResults` truncation must not be passed as complete. A dense global/narrow-field index that exceeds this implementation's bounds requires a separately qualified index/candidate strategy; a synthetic narrow-field success does not fill absent catalog stars. No `CatalogSnapshotResult` shape changes or new catalog installation occur.

## Quality

Verification IDs are selected deterministically by FNV-1a over UTF-16 ID code units modulo5. They are excluded from triangle indexing, candidate ranking and local fitting. Final fit and verification assignments cannot share a detection. Nearby angular candidates are only deduplicated when their catalog/detection assignments substantially overlap, preserving close aliases in narrow fields.

Current engineering gates require12 fitting stars,4 verification stars, fitting RMS≤0.4px, verification RMS≤0.5px,≥35% width and height spread, and≥50% of expected isolated eligible fitting stars. Projected isolation (>12px) is an approximation to the detector's close-source exclusion; eligibility has an explicit magnitude ceiling. It is not a complete physical sensitivity model. The detector permits other isolation/shape settings, but the solver currently uses its declared fixed12px isolation and6px edge profile; an arbitrary detector setting is not automatically an equivalent completeness model.

Score0–100 combines30% fit residual,30% verification residual,20% count and20% spread. Residual components use exp(−(RMS/0.3px)²); count saturates at40 stars and spread at70% dimensions. It is not a calibrated probability. Covariance remains unavailable. Multiple independently verified distinct candidates reject; finite sampled search does not prove mathematical global uniqueness.

## Future application and stack insertion points

1. CameraAgent: optional post-ingress Processing analyzer from calibrated linear pixels with exact raw lineage; do not block durable raw admission or mutate capture-time descriptors
2. Processing: explicit versioned astrometry recipe and typed Metadata schema/semantic identity registration; bind catalog/calibration/settings/source identities into execution identity
3. CameraAgent persistence: existing layoutless typed Metadata product envelope is the likely seam; source lists belong in one diagnostic artifact if retained
4. LogicHost: use the same library/recipe through its adapter and immutable artifact reconstruction. `CentralProcessingGraphNodeRegistry` currently rejects canonical-JSON graph bindings; auxiliary-artifact dependencies are the safer existing seam pending explicit registry/catalog work
5. Future registered stack: a separate opt-in versioned recipe, never a silent change to `rolling-mean-v1` (COMB-010)

Current stacks remain unregistered arithmetic means. Edge insertion points are `RollingCombinationCaptureProcessingStep`, its compatible history selection and lineage; central points are `CentralDerivativeRecipeCatalog`, window resolver/input reader/job executor/output writer. A future registered recipe must bind ordered source frame and accepted solution identities, reference UTC/solution, mapping version, interpolation, coverage masks, rejection policy and actual integration into output identity. Preserve raw originals and transient evidence. Ordinary geometric resampling must follow linear CFA reconstruction.

Meteor/satellite consumers may reuse accepted pixel/sky mappings and explicit timing/quality later. Existing transient geometry remains detector-pixel evidence; this change adds no event classifier, orbital propagation, source rejection policy or trajectory solver.

## Installed HYG selection

The installed `SqliteCelestialCatalog` also implements
`IAstrometricCatalogSource`; `AddInstalledCelestialCatalog` resolves all catalog
interfaces to the same validated immutable cache. Use the existing installed
snapshot resolver, not a new catalog registry. `ReadAsync` accepts a finite
magnitude (including negative values) and any entry bound of at least 1, which
is the caller's already validated request. It finds the exact matching count by
binary search and copies the smaller of that count and the bound, so no
allocation scales with the bound itself. `SolveAsync` requests 2,500 entries for
every public settings value. A copied selection larger than the catalog data's
internal capacity (8,192 entries) is rejected by `AstrometricCatalogData` with
an `ArgumentException` for `stars`; before #1167 the reader rejected any bound
above 2,500 with `ArgumentOutOfRangeException`.
Cancellation is checked before selection, while copying and after bounded
materialization.

Only a resolver-validated production package with every requested matching row
inside the bound reports `IsCompleteForRequestedMagnitude=true`. An overflowing
selection still carries the requested magnitude ceiling but is explicitly
incomplete; the solver returns `catalog-incomplete` before numerical fitting.
Fixture packages and directly opened database files cannot assert production
sky completeness. A non-finite magnitude or a bound below 1 throws
`ArgumentOutOfRangeException`; canceled reads throw
`OperationCanceledException`.

Installed selections retain catalog ID, package version/kind and preprocessing
version in `AstrometricCatalogProvenance`, alongside the existing data checksum,
schema, source/license and coordinate model. These fields bind the catalog
identity used for warm-prior compatibility. Standalone evidence without package
provenance retains its previous identity. A new package identity cannot silently
reuse a prior frame fit even when its selected stars are unchanged. This adds no
catalog schema, HYG upgrade, proper-motion or lifecycle behavior; those remain
owned by #521.

The initial #1098 catalog profile is the approved full HYG 4.2 snapshot with
magnitude <=5: 1,637 entries, below both the 2,500 solver bound and the current
2,000 visible-render bound. Set `AstrometricSolverOptions.MaximumCatalogMagnitude`
and VirtualSky `MaximumMagnitude` to 5 explicitly for that profile. The existing
solver default of 7 selects 15,598 catalog rows and therefore remains unavailable
through this bounded adapter; defaults are not silently reinterpreted. A
magnitude <=5.5 request already has 2,865 rows and is incomplete at the legacy
2,500 bound, and no deeper selection is qualified (see below). These
counts establish catalog selection, not detection sensitivity or actual-camera
qualification (#1102/#1106). #522 still owns honest visibility/resource behavior
for deeper production rendering profiles.

### Deep selection profile

Issue #1167 measured a complete deeper HYG selection under the profile
`hyg-deep-selection-v1` (an 8,192-entry request bound, measured to magnitude
6.0) and it is not qualified; the result and evidence are in
[Deep HYG catalog selection](deep-catalog-selection-v1.md). Nothing is
advertised:

- No profile is declared, and no public member selects one.
  `AstrometricCatalogSelectionProfile` and
  `AstrometricSolverOptions.CatalogSelectionProfile` are internal, reachable
  only through Astronomy's `InternalsVisibleTo` test assemblies so that the
  evaluation harness stays runnable. The settings property has no setter, so
  neither configuration nor deserialized settings can select a profile, and
  the public `AstrometricSolverOptions` constructor is unchanged from before
  #1167.
- The name `hyg-deep-selection-v1` is burned. It records what was measured, it
  is never declared, and any later attempt uses a new name.
- For internal evaluation settings, `Validate` rejects an unknown name, and a
  `MaximumCatalogMagnitude` above the profile's maximum. The profile name
  serializes last in `IdentitySha256` when set and is omitted when null, so
  every public settings identity is unchanged. The derived entry bound is
  `[JsonIgnore]`. `SolverVersion` remains `spherical-triangle-astrometry-v2`,
  and `SelectionIdentitySha256` is unchanged.
- The catalog data container has an internal capacity of 8,192 entries
  (`MaterializationCeiling`), independent of any profile. It is the hard
  ceiling of the data constructor and the warm core, is not public, and
  promises no solve.
- A selection larger than the bound of its settings returns `Unavailable` with
  `catalog-selection-unsupported` before any numerical work. That bound is
  2,500 for every public settings value. The check follows
  `catalog-incomplete`, and `OpticalCalibrationSession` applies the same check.
- An accepted fit whose expected or retained (fitting plus withheld) star count
  exceeds the 2,500-star assessment evidence bound returns `BudgetExceeded`
  with `resource-limit` and proposes no mapping, so last-good evidence is not
  replaced. Only a deep selection, and so only internal evaluation, can reach
  this.

Settings keep the legacy 2,500-entry bound. That bound admits any requested
magnitude whose complete selection fits: on the HYG 4.2 production snapshot,
up to magnitude 5.37 (2,483 rows; 5.38 selects 2,517 and is incomplete). Only
magnitude 5 is qualified (#1098/#1126). The band above 5.0 up to 5.37 is
admitted unqualified, exactly as before #1167.
