# Retained capture astrometry

Issue #1643 adopts [the measured recipe](measured-astrometry.md) in CameraAgent's
existing post-admission durable graph. No shipped profile enables it. This uses
the current SQLite/file publisher, replay pins, source-retention graph and
structured-product upload contract; it introduces no database layout or recovery
protocol conversion.

Configure the stable `MeasuredAstrometry` alias in an **explicit v2** graph.
Its two direct dependencies must be durable `ProjectedScene` and
`MeasuredStellarAssociations` nodes. Set its `required` to `false` and its
publication to `durable-local`. Keep an enabled Storage retention owner. Keep
raw storage independent of this optional branch; an optional metadata Storage
branch may depend on astrometry for upload. Cadence skips must not become a
required dependency of raw publication.

`MeasuredAstrometryCaptureOptions` accepts:

| Option | Contract |
| --- | --- |
| `outputVariant` | Default `measured-astrometry-v1`; context uses the `-context` suffix. |
| `captureStride` | Default/minimum 60, maximum 1,000,000. Only `(captureSequence - 1) % captureStride == 0` executes on the live durable worker. At five-second acquisition this samples one capture per five minutes. |
| `catalog` | Required `AstrometryCatalogSnapshotV1`: immutable metadata, complete bounded star selection, package provenance, completeness flag/limit and coordinate model. Maximum 2,500 rows and 2 MiB context. Materialize from a verified local adapter before configuring the revision; no network/current catalog lookup occurs during execution. Incomplete selections retain `Unavailable`. |
| `clock` | Explicit `AstrometricClockFacts`; defaults to `NotSupplied`, with no invented timing precision. |
| `previousAccepted` | Optional exact native accepted assessment. The frozen context retains it and references its source raw artifact. Stale/incompatible priors retain rejection and never launch cold fallback. |
| `recipe` | Existing normalized `MeasuredAstrometryOptions`: solver, diagnostics, uncertainty and cooperative execution budget (20 seconds default, 60 seconds maximum). |

One host-wide gate serializes numerical execution. This branch runs after durable
raw admission, on existing bounded worker/replay scheduling, never inside camera
acquisition or a viewer request. Sampled-out captures have the durable
`astrometry.capture-cadence` skip and no solution; they never borrow another
capture's fit. Retry uses the frozen graph options and exact dependencies. Prepared context and
assessment files use the existing metadata publisher before the node commit.
A retry validates and adopts a published assessment, including budget failure,
without fitting again; a damaged prepared file fails closed.

## Immutable artifacts and reconstruction

The node publishes two typed Metadata artifacts in the existing node commit:

- `measured-astrometry-context-v1`, media
  `application/vnd.hvo.measured-astrometry-context+json`, recipe
  `retained-astrometry-context/1.0.0/retained-astrometry-v1`. This is the exact
  canonical recipe context, retained once separately from the compact result.
- The unchanged `measured-astrometry-solution-v1` payload from
  `measured-astrometry/1.0.0/measured-astrometry-v1`. Its semantic solution
  identity remains the shared canonical payload hash.

The adopted solution's ordered immediate parents are raw, measured associations,
scene, context. The standalone recipe's existing two-parent form is unchanged.
The context also holds the prior's raw artifact when supplied. Existing retention
walks these parents; restart restores completed outputs without refitting.
In LogicHost only this authenticated context parent at ordinal 3 may resolve to
another capture, and it must belong to the same device and have the Raw role.
Current raw/measured/scene parents and all solution parents remain capture-bound.
Ingest retains and checks every supplied role, variant and producer hash; native
scene/context parents carry roles without invented variant or producer facts.
Reconciliation admits the products only when their required parents resolve.
Raw/descriptor, catalog rows/package, scene/calibration, observer, reported time,
readout, settings and prior are independently bound by the shared recipe.
Hashes preserve identity and integrity; they do not establish physical accuracy.

Replay submission pins the original context **and assessment** under the existing
execution lease. Replay verifies and republishes those immutable bytes rather
than rerunning a clock-dependent budget outcome or consulting current settings.
Replay fails closed if originals are absent, ambiguous, renamed or altered.
No live work dispatches to a replay runner.

Export/import uses `StructuredProcessingProductManifestV1` for all four Metadata
products and the raw manifest/payload. Preserve ordered source IDs and producer
descriptors. `RetainedAstrometryProducts.Request` rebuilds the same request;
`Verify` checks its retained assessment without fitting. LogicHost's existing
input reader now reconstructs ingested typed products from their authenticated
structured descriptor even when they have no central-producer evidence row.
`ProcessingRunnerArtifactMetadata.RecipeDescriptor` preserves that producer
through claim projection and rejects descriptor/identity mismatches. The runner
advertises the shared recipe; this does not activate central jobs or change
lease, graph, ingestion or publication authority.

## Viewer handoff to #1644

Use `ICameraAgentRetainedAstrometryService.GetAsync(captureId, token)` or
`GET /api/v1/operations/gallery/{captureId}/astrometry`. The route inherits
`OperationsReadV1` owner authorization. Use the displayed Current Sky capture ID
or the selected archive capture ID; no latest-fit cache or prediction occurs.

`CameraAgentRetainedAstrometry` contains `CaptureId`, `Availability`, `Reason`,
optional `ArtifactId`, `SolutionIdentitySha256`, `GeometryBasis`, and `Solution`.
Availability is `Retained`, `NotRetained` or `Unavailable` (including busy read
capacity or invalid evidence). A retained solution's `Assessment.Status` is
`Accepted`, `Rejected`, `Unavailable` or `BudgetExceeded`; retain its reason.
Only `HasMeasuredMapping` yields `GeometryBasis=MeasuredFit`. All other outcomes
have `Unavailable` geometry. Configured scene geometry is never labeled a fit.

Read `Assessment.Quality`, `Residuals` and `Uncertainty` directly. Quality is an
engineering score; conditional uncertainty can exist while total/shared
uncertainty remains withheld. Fitting and held-out RMS belong exclusively to
this capture. Never use these RMS values as new-prediction uncertainty.
`MeasuredAstrometryJson.ReconstructMapping` and `ApplyPixels` remain separate
bounded operations and require the exact retained calibration/measurements.

The projection admits one read at a time without queuing raw buffers. Each read
opens at most five authenticated artifacts, allows 32 MiB raw and 44 MiB total,
and returns a compact solution of at most 128 KiB. Every artifact passes the
existing sidecar/checksum boundary; semantic lineage is then revalidated. Missing
or altered sources, invalid envelopes and individual or aggregate byte-limit
violations return `Availability=Unavailable` with no solution. Caller cancellation
propagates. The aggregate-accounting regression lowers an internal test budget;
the production limit remains 44 MiB.

Fixtures: `RetainedAstrometryDurabilityTests` uses #1642's seeded 512-square
`MeasuredAstrometryFixture` raw pixels, the actual measured producer, durable
publication, restart/read, frozen replay and structured export/import.
`RetainedAstrometryRunnerTests` exercises the real runner claim serialization
and reconstruction seam. See [measured evidence](../validation/issue-1643-retained-astrometry.md).
Optical review/activation UI, calibration write-back, stacking, physical
qualification and production/full-resolution performance qualification remain
outside this slice.

## Current Sky and archived image inspection (#1644)

Current Sky and `/gallery/{captureId}` read the selected capture through
`OperationsReadV1`. The card shows retained failures as well as accepted fits;
an absent or invalid source cannot borrow another capture's mapping. The active
rig profile is never used to reconstruct a historical mapping. Live refreshes retry
an assessment that has not yet been retained; completed outcomes stay immutable.
Archived Retry image layers or Refresh measured evidence rereads the selected capture.
The authenticated read projects only the compact solution, exact mapping and bounded shared
residual diagnostics; catalog rows and raw buffers stay inside the read boundary.

Measured stellar associations and astrometric residuals are separate typed layers,
both initially off. Green marks fitting stars and amber marks held-out stars.
Residual vectors run from the fitted prediction to the measured centroid at **1×**
scale; rings locate fitted positions so subpixel vectors remain discoverable. They are neither the configured expected sky layer nor satellite
predictions. A bounded immutable producer chain must preserve the source raster
and exposure before these layers can be composed onto a base. A mismatch leaves
the original manifest/base usable and withholds the measured layers. The existing
metadata publisher retains a distinct `capture-astrometric-composition-v1`
manifest revision and its typed layer payloads; the original manifest selector
remains unique. Frozen export revalidates the current revision and uses those
same payloads. It never reruns measurement or fitting on annotated pixels.

Calculators use zero-based **source output** pixels, rather than scaled preview
pixels. They require either the exact raw image or a verified compatible base.
The retained projection already includes crop, binning, aperture and flip;
these transforms must not be applied again. Equatorial input/output is mean of
date at the retained exposure midpoint; horizontal coordinates are geometric,
with no refraction. Non-finite, out-of-raster, out-of-aperture and below-horizon
points are withheld. The card reports separately supported conditional,
systematic, shared-calibration and total pose uncertainty and the clock bound.
Point uncertainty is withheld; fitting/verification RMS is never promoted to a
prediction error or an uncertainty band. No calculator writes calibration.

### Reproducible virtual viewer fixture

The integration test below runs actual measured production, durable publication,
source verification, typed composition and frozen export for five 512-square
virtual scenarios. It checks raw/base bytes remain unchanged and accepted
calculator round trips. To preserve real products for browser inspection, first
create registered scratch with `hvo scratch HVO.SkyMonitor 1644 viewer`, then use
that returned directory as `HVO_ASTROMETRY_VIEWER_FIXTURE_ROOT`. It must be fresh.
The environment variable is read **only by the test assembly**.

```bash
env -u HVO_PRODUCTION_CATALOG_BUNDLE \
  HVO_ASTROMETRY_VIEWER_FIXTURE_ROOT="$FIXTURE_ROOT" \
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  flock -o /tmp/hvo-heavy-gate.lock timeout 10m \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
    --configuration Release -nodeReuse:false -p:UseSharedCompilation=false \
    --filter 'FullyQualifiedName~RetainedAstrometry_ViewerPublishesTypedDiagnostics' \
    --logger 'console;verbosity=normal'
```

Each `accepted`, `rejected`, `unavailable`, `budget` and `no-data` subdirectory is
an ordinary CameraAgent raw-ingress root with real SQLite/file products.
`viewer-fixture.json` names its capture, source/base checksums and manifest
identity. Use that root with the existing authenticated browser host fixture or
a capture-disabled local host, then open `/gallery/{CaptureId}`. For accepted
evidence select Processed, enable each measured toggle separately, enter
`X=280, Y=240`, calculate Pixel to sky, then Sky to pixel; the source coordinates
return within `1e-7` pixels. Capture rejected/no-data cards and a narrow viewport
as well. The parent issue owner completes screenshot and resource evidence on
the reviewed candidate. These virtual fixtures establish software behavior,
not physical accuracy or full-resolution qualification.
