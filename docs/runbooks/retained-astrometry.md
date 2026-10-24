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
or altered sources return no solution. Caller cancellation propagates.

Fixtures: `RetainedAstrometryDurabilityTests` uses #1642's seeded 512-square
`MeasuredAstrometryFixture` raw pixels, the actual measured producer, durable
publication, restart/read, frozen replay and structured export/import.
`RetainedAstrometryRunnerTests` exercises the real runner claim serialization
and reconstruction seam. See [measured evidence](../validation/issue-1643-retained-astrometry.md).
Optical review/activation UI, calibration write-back, stacking, physical
qualification and production/full-resolution performance qualification remain
outside this slice.
