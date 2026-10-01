# Actual virtual-camera astrometry baseline

Issue [#1102](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1102)
qualifies the existing ordinary VirtualSky capture path for
[#1098](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1098).
This is a stationary-renderer baseline. Production exposure integration and
final reruns remain [#522](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/522)
and [#1106](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1106).
It does not certify a physical camera, sensitivity, optical calibration or
uncertainty. Runtime algorithms and host recipes are unchanged by this harness.

## Workload and input boundary

Use an approved offline HYG 4.2 installation through `CatalogSnapshotResolver`.
The expected database SHA-256 is
`B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2`,
with 119,625 fixed-star rows. The complete magnitude <=5 selection contains
1,637 entries, below renderer admission 2,000 and solver materialization 2,500.
An unavailable snapshot fails the qualification; sparse fixtures do not replace it.

The harness loads shipped ASI174/ASI178 sensor dimensions, pitch and electron
response, then declares controlled virtual optics/illumination. Each capture
uses the normal `VirtualSkyCameraModule`, one-second applied exposure, gain 150,
magnitude-zero rate 60,000 electrons/s, background 2 electrons/s, native PSF
sigma 1/radius 4 pixels, vignetting 0.25, and normal read/shot noise. These rates
are synthetic assumptions, not measured installed-lens throughput.

| Camera view | Native sensor | Output operation |
| --- | --- | --- |
| Mono native | 1936 x 1216, 5.86 micrometres | Full sensor, identity |
| Mono ROI | Same | Native ROI (240,96,1440,1024) |
| Mono bin | Same | Full sensor, 2 x 2 digital average |
| Mono ROI/bin | Same | Declared ROI, 2 x 2 digital average |
| Mono mirror / independent roll | Same | Full sensor with each changed orientation |
| RGGB native | 3096 x 2080, 2.4 micrometres | Full native unbinned RAW16 |

Three additional views crop one photosite from the already captured RGGB
payload at origins (1,0), (0,1), (1,1). They declare the corresponding GRBG,
GBRG and BGGR phases and parent-payload identity. Native RGGB covers phase (0,0).
These derived views exercise shared scientific reconstruction; they do not
claim camera Bayer crop/bin support. Ordinary Bayer crop/bin, charge binning
and unequal radial-fisheye bins are rejected by focused tests.

`VirtualAstrometryPixels` retains only an owned final payload, authoritative
layout and capture facts. Measurement cannot see scene provenance, projected
objects, IDs, centroids or pose. Its validity mask uses stored saturation and
the declared nominal aperture. Outside-aperture black pixels must not dominate
global median/MAD; the mask contains no projected-star information. RGGB uses
full-resolution `LinearBayerReconstruction.ToLuminance`, not the transient
half-resolution integer converter. Detection options are recorded by identity:
peak 8 stored ADU mono / 32 CFA, noise multiplier 6, minor sigma >=0.2,
major sigma <=1.8 mono / 2.3 CFA, variance ratio <=2.5, isolation radius 12/bin.
The existing 4,096-candidate and 16-million-pixel limits remain enforced.

The solver receives measured centroids, a separately declared nominal model,
capture facts and the complete catalog. Nominal pose is (52,290,-41) degrees;
nominal focal is the specified reference focal divided by 1.037. Projection
family, parity, principal point and aperture are fixed initial assumptions.
Recovering these optical parameters belongs to #1104. The captured configured
pose is used only after solving, by the independent scorer.

## Partitions, timing and independent checks

Tuning is 2026-02-10 08:00 UTC with seed 110220. Held-out sequences start at
08:00 UTC on 2026-01-15, 2026-05-15 and 2026-09-15, with seeds 110201, 110205
and 110209. Each sequence contains three frames 60 seconds apart. Noise uses
the production seed/capture-sequence mixer at indices 0,1,2. The first frame
acquires blind; the next two use only the last accepted compatible assessment.
All 90 supported camera/derived-view cases must execute and pass. Tuning never
counts as held-out evidence.

The current module reports start/start/start acquisition callbacks and renders
stationary stars at `SceneUtc`. This harness explicitly declares the logical
interval `[requested start, requested start + applied exposure]`, retaining
reported callbacks separately. It does not overwrite timestamps with rendering
truth. Independent Cartesian precession, sidereal and ENU matrices transport
the scoring reference from scene time to the logical midpoint. Final
exposure-correct evidence must replace this provisional timing interpretation
through #522/#1106.

`VirtualAstrometryReference` implements Cartesian rotations and equidistant
projection without production coordinate, precession, camera-basis or projector
calls. Rounded existing Astropy/ERFA anchors validate that implementation.
Checks include every association's independent nearest catalog ID; withheld
catalog sources; full pose-basis error; predetermined non-stellar grids across
frame times/readouts; inverse rays; local finite-difference pixel scale; and
support/null results. Withheld accuracy uses sources at least 12 output pixels
inside both rectangle and aperture; boundaries are checked separately.

Existing solver acceptance remains >=12 fitting and >=4 verification stars,
>=35% coverage on both axes, fitting RMS <=0.4 and verification RMS <=0.5 pixels.
Additional limits are association precision 1.0, withheld mapping RMS <=0.5 and
p95 <=0.75 pixels, full-basis error <=0.06 degrees, focal relative error <=0.1%,
grid error <=0.75 pixels and local scale error <=0.2%. Warm hypotheses must be
zero. Payload and compact-assessment identities must reproduce exactly.
Calibration/catalog/settings/site incompatibility, cancellation and bounded
work must reject without a successful replacement mapping.

## Running and retaining evidence

Commit the candidate and build its exact head before collecting final results:

```bash
dotnet build tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release -warnaserror
DOTNET_TieredCompilation=0 \
HVO_ASTROMETRY_CATALOG_ROOT=/absolute/private/test/catalog-root \
HVO_EVIDENCE_REVISION="$(git rev-parse HEAD)" \
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~ActualPixelsBlindWarmReadouts" \
  --logger trx --results-directory TestResults/virtual-astrometry
DOTNET_TieredCompilation=0 \
HVO_ASTROMETRY_CATALOG_ROOT=/absolute/private/test/catalog-root \
HVO_EVIDENCE_REVISION="$(git rev-parse HEAD)" \
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~ActualConfiguredResolutionResources" \
  --logger trx --results-directory TestResults/virtual-astrometry-resources
```

Both manual gates require the requested revision in the built assembly identity.
TRX/JSON attachments retain final RAW16 bytes, source/layout/configuration/
nominal-calibration/catalog/algorithm identities, measured sources, associations,
withheld/grid scores and explicit failures. Preserve original failed/tuning
records separately. These Manual gates supplement mandatory Unit, Docker
Integration, classifier and other Tier C candidate evidence; protected
development CI alone does not execute them.

Resource evidence uses native mono and native CFA, concurrency 1, Release with
tiered compilation disabled, five warmups and 30 measurements per workload.
Record every capture/measurement wall and process CPU sample, allocations,
process working set/cumulative peak, buffer sizes, throughput, payload hashes
and Linux I/O counters. Run the resource workload in a fresh test process;
the cumulative peak includes any previously executed tests in that process.
The first combined invocation inherited a 3,090,255,872-byte peak from the
90-view qualification and failed the unchanged 2 GiB resource budget.
The same committed resource workload alone passed with a 1,178,484,736-byte
peak. Both original records remain evidence; this does not claim that the
whole qualification runner fits in 2 GiB or measure solver memory separately.
Resource JSON retains raw measurements before reporting budget failures.
Stateless operations have no queue/backlog. Cumulative
process peak is not an operation-local memory measurement. Budgets on the
qualification host are capture p95 <=10s mono/20s CFA, decode/detection p95 <=2s
mono/5s CFA, cold <=15s, warm <=500ms and cumulative process peak <=2 GiB.
Any subsequent production change needs equivalent before/after evidence;
investigate median regression >max(20%,50ms), allocation >10% or peak +128 MiB.
This initial harness measures unchanged production code and claims no speedup.

The #1102 issue/PR ledger owns exact-head results, independent deep review,
green required checks and #1087 crop/bin disposition. Successful stationary
qualification does not complete the epic's exposure, calibration, diagnostics
or uncertainty criteria.
