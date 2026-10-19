# Shared orbital contracts v1 (#1402)

Status: design checkpoint under [#527](../planning/orbital-data-decision-527.md),
2026-10-19; executable types and conformance are delivered by #1402. A frozen
document is not evidence that those implementations already exist. This is a
satellite GP contract, not a generalized data-feed or catalog-install framework.

## Ownership and version boundary

Astronomy owns object/record/snapshot contracts, pure record selection and
timed satellite prediction/pass products. It may reuse AgentCore identities and
existing Astronomy geometry; it does not reference host, storage, network,
SkiaSharp or persistence adapters. #529 supplies numerical prediction.

CameraAgent.Common owns its operational refresh read model and #528's concrete
reader, job, automation, persistence and health adapters. LogicHost consumes the
same scientific manifest but owns any separately approved central operation;
it never references CameraAgent.Common for a UI/status DTO. Processing composes
prediction/measurement products and presentation; Imaging retains algorithms
and existing line/label payloads. HYG/OpenNGC and Catalog.Sqlite are unchanged.

Batch A publishes canonical shared types/interfaces and fixtures once. Batch B
implements their adapters; any schema amendment is proposed to A, reviewed,
versioned and adopted by both consumers together. There is no private parallel
DTO. Consumers wait for the reviewed contract commit or an explicitly recorded
stack. Persisted schemas/model/normalizer/policy hashes are immutable provenance.

## Identity, units and source normalization

Object identity is authority plus canonical decimal catalog text (`norad`,
`1` through `999999999`, without leading zeros). A display name and optional
international designator are aliases. The namespace permits a later qualified
analyst/source identity; initial support must not claim permanent identity for
reused analyst numbers. Missing physical type/status is unknown.

A normalized record contains:

| Field family | Required semantics |
| --- | --- |
| Identity | Record SHA256; object identity; stable source/dataset key; optional source record identifier; aliases |
| Model | Explicit Earth, TEME, UTC, SGP4 and provider normalization/derivative convention |
| Epoch / interval | UTC epoch, optional source-valid-from/to; absence is unknown validity |
| Elements | Mean motion rev/day; eccentricity; inclination, ascending node, argument of pericenter and mean anomaly in degrees; BStar in inverse Earth radii; original GP DOT/DDOT fields and convention; ephemeris/classification fields; optional element-set/revolution numbers |
| Provenance | Parser/normalizer/profile version and source-asserted type/status only when supplied |

Initial CelesTrak GP JSON supplies its documented omitted model constants only
under the approved profile. Reject conflicting center/frame/time/theory,
nonfinite values, invalid eccentricity/inclination/mean motion, malformed
identities, invalid intervals and unsupported ephemeris/model combinations.
Normalize angles and negative zero consistently in typed data before hashing;
UTC is canonical. Preserve source bytes unchanged. Never serialize modern
records through TLE text or silently clamp corrupt elements.

DOT/DDOT retain the provider GP/TLE-field convention; adapter unit conversion
matches the pinned reference initializer, with no extra factors of two/six.
They are not independently measured physical motion features. A conformance
case must freeze the exact adapter coefficients, including nonzero DDOT.

Record identity hashes canonical normalized content including the stable
source/dataset, object, epoch, model/elements, aliases and validity. It excludes
retrieval time, snapshot membership and the whole source payload hash. Thus
unchanged elements keep identity if another record in the downloaded payload
changes. Reject duplicate **record hashes**; preserve distinct records for the
same object. A snapshot source descriptor binds each stable dataset key to
the exact raw payload evidence; the initial profile permits one descriptor
per source/dataset in a snapshot.

## Snapshot and validated reader

The acquisition manifest binds schema/profile version, sanitized provider and
dataset identity, underlying source lineage, source URI/query, retrieved time,
optional provider generation/validators, original byte hash/length, normalized
payload hash/length, record/object counts, epoch range, coverage and terms
references. It also binds parser/normalizer identities. Credentials and
secret-bearing query values are forbidden. Validators are evidence, not
permission to issue conditional requests.

The normalized payload is deterministically ordered by record identity.
Use typed canonical JSON and the existing CaptureContractJson canonical writer;
normalize hash casing/UTC/numbers before calling it. Property sorting alone
does not normalize numeric spellings or collection order. SHA256 is uppercase
hex in the executable contract, consistent with existing capture helpers.
Compute the manifest identity with its self-identity field excluded; a repeat
acquisition can have a different manifest while preserving normalized bytes
and record identities. Fixtures pin both exact bytes and their declared hashes.

Coverage states the requested population, known exclusions and completeness
relative to that request. Complete active-group retrieval is still partial
global streak coverage. Retrieval success, local activation availability,
record age and source validity are separate facts. Do not manufacture a
provider generation time from the HTTP Date header.

The executable validated snapshot defensively copies bounded records and
collections. It verifies hashes, counts, source bindings, duplicate record
identity, schema and model/element ranges before becoming usable. Suggested
initial ceilings from #527 are 128 MiB expanded / 100,000 records / 32 sources;
do not inherit projected-scene's smaller rendering limits.

The host-provided reader has only these responsibilities:

```csharp
ValueTask<OrbitalSnapshotReadResult> ReadCurrentAsync(CancellationToken cancellationToken);
ValueTask<OrbitalSnapshotReadResult> ReadExactAsync(string snapshotSha256, CancellationToken cancellationToken);
```

The result is a validated immutable materialized snapshot or an explicit
unavailable reason. Exact reads never substitute the current snapshot. Host
adapters pin storage while loading; calculations hold immutable memory rather
than repeatedly touching the filesystem. Cache by identity outside the frame
path. A new public lease framework requires measured need, not speculation.

## Pure record selection

Selection binds snapshot, object, requested UTC interval and the complete
versioned policy/source ranking. For initial GP, validate the entire interval
against provider validity where known and policy epoch offsets (-24 to +168
hours); stale is >48 hours in absolute age. The #527 policy defines eligible
diagnostics versus withheld/expired outcomes. Missing validity remains unknown
even when policy permits calculation.

Rank eligible records by configured approved source priority, absolute epoch
distance to interval midpoint, newest epoch for equal distance, then ordinal
record SHA. Return the selected record identity, signed endpoint ages,
freshness/eligibility, tie-break and reason codes. Do not select an unranked
source, use arrival order, silently change source precedence or switch records
within a continuous track. This rule does not qualify SupGP selection.

Zero matches, missing input, unsupported source/model, invalid interval,
future/stale/expired data and incomplete population remain distinct. No empty
result asserts that no satellite exists or that a measured streak is a meteor.

## Timed prediction and pass products

Freeze the following small products/interfaces in #1402; #529 implements them:

- Exposure query/result: exact requested interval, immutable snapshot/selection
  policy, observer, resolved geometry/timing/model facts and work bounds;
  per-object failures plus evaluated/returned counts and completeness.
- Track: product identity, selected record/source/snapshot lineage, requested
  and actually covered intervals, observer, geometry/calibration/readout,
  clock/EOP/model facts, ordered timed samples, continuous drawable segment
  boundaries, sampling error and independent uncertainty/reason facts.
- Sample: UTC, existing `AltAzPoint`, range/range rate/angular speed where
  available, illumination enum including unknown, and optional `PixelPoint`.
- Pass query/summary/result: bounded search interval/population, rise or window
  start, peak and set or window end, maximum elevation, resolved/partial
  boundary facts, source age/coverage and search/budget/error status. A full
  pass and its photographed segment have distinct intervals and identities.

Samples are ordered; a display must not connect separate horizon/aperture,
sensor or projection runs. Query limits cover objects, time window, propagation
evaluations, returned passes/tracks/samples and cancellation. Budget/deadline
exhaustion yields explicit partial/unavailable status, not a complete empty
list. No `Visible` or `Detected` boolean and no synthetic magnitude belongs
in the orbital geometry product.

Reuse `ObserverLocation`, `AltAzPoint`, `PixelPoint`, `ProjectionContext`,
`ProjectedSceneImageTransformV1` and `AstrometricClockFacts`. Geometry facts bind
configured-rig versus accepted-measured basis, rig/readout/calibration identity
and any accepted assessment/uncertainty. Use RigProjectionContextFactory or the
accepted AstrometricMapping projection; project each sample's actual horizontal
direction through IImageProjector, then apply the readout transform once.
AstrometricMapping's midpoint SkyToPixel conversion is not a timed satellite
transform. Never copy rig sensor/lens/pointing fields into a satellite setup.

Exposure/readout timing explicitly states actual, reported, estimated or
unknown with its evidence. Capture command/status boundaries do not imply
photosensor precision. Model facts include propagator/source version, gravity
and operation mode, TEME conversion, UTC/UT1/EOP/leap assumptions, height datum,
refraction and solar/shadow identity. Unknown orbit uncertainty is not zero;
clock, calibration, physical orbit and sampling errors remain separate.

## Host refresh read model

CameraAgent.Common owns a separate operational DTO for #528/#1392. It includes
observed UTC/revision, enabled state, current manifest identity, last attempt,
retrieval success and activation, next due and next permitted attempt, current
run, provider halt, candidate quarantine, reasons and permitted recovery action.
Reference existing automation definition/run/revision identities. Connectivity,
halt and per-record scientific age are independent. A failed refresh does not
erase the active manifest or make old elements new. Refresh commands and their
authorization/durability are host implementation, not Astronomy interfaces.

## Conformance and handoff

#1402 publishes one remotely accessible commit and a small immutable fixture
manifest; both lanes verify its schema/profile versions and hashes. Include
six/nine-digit IDs, missing aliases, valid multiple records, invalid duplicates,
unchanged reacquisition, future/stale/expired selection, source ties, unknown
validity, missing/partial population, invalid metadata/numerics, corrupt hashes
and defensive-copy checks. Freeze configured/measured geometry and readout
identities, separate full-pass/exposure products, clipping gaps and unknown
illumination/error components. Test strict round trips and bounded parse failures.

#528 adds provider failure/crash/activation/retention tests and adapter
conformance; #529 adds independent near/deep-space numerical, transform,
sampling and pass tests. Contract examples alone do not qualify either runtime.
Assessment-referenced originals and later retrospective inputs remain separate
and append-only. No source download, math engine, scheduler, storage adapter,
host page or detector redesign is implemented by this contract issue.
