# Shared orbital contracts v1 (#1402)

Status: executable contract delivery under #1402, following the approved
[#527 checkpoint](../planning/orbital-data-decision-527.md) merged in #1403.
Consumer readiness requires the reviewed, merged #1402 commit and both lanes
verifying the conformance pack; the document alone is not that handoff. This is a
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

Result construction consumes the validated exposure or pass query, preserving
geometry, timing, minimum elevation and requested work/sampling limits. A
frame-specific accepted astrometric assessment must match the requested
exposure and observer; no cross-frame reuse policy is approved here. Complete
results cannot exceed the requested deadline or sampling tolerances, and
sampling-budget clipping requires an explicit partial budget outcome.

Samples are ordered; a display must not connect separate horizon/aperture,
sensor or projection runs. Query limits cover objects, time window, propagation
evaluations, returned passes/tracks/samples and cancellation. Budget/deadline
exhaustion yields explicit partial/unavailable status, not a complete empty
list. No `Visible` or `Detected` boolean and no synthetic magnitude belongs
in the orbital geometry product.

The exposure, future-pass and pass-arc producer methods honor caller cancellation
by throwing `OperationCanceledException`, including during final freezing. They
do not return a prefix after cancellation. Useful prefixes belong to explicit
budget/deadline or unresolved numerical outcomes while the request remains active;
this differs from cancellation of the request itself. Consumers may retain a
previous independently completed result, but cannot treat a canceled request as
a newly completed or partial result. The sampler/pass-search qualification and
existing cancellation tests already exercise this exception behavior.

Engine-produced results also carry immutable, query-local `WorkDiagnostics`:
requested/visited/selected object slots, actual initialization and point-call
objects/calls, static support rejections, and actual image-projection
objects/attempts/successes. Counts include failed, excluded and budget-prefix
work; cached probes add no core attempts. Initialization plus point core calls
equal `PropagationEvaluations`. Original public result constructors preserve
their validation and leave diagnostics null, meaning unknown. These execution
facts do not enter canonical products, schemas or model identities, and do not
assert population completeness. Operation boundaries and qualification are in
[work accounting](../astronomy/orbital-work-accounting-v1.md).

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


## Executable surface and adapter agreement (#1402)

The canonical implementation is `src/HVO.SkyMonitor.Astronomy/Orbital/`.
`OrbitalContract.CreateRecord`, `OrbitalSnapshot.Create/Parse` and
`OrbitalRecordSelector.Select` implement the normalized contract and pure
selection only. `IOrbitalSnapshotReader` is implemented by #528;
`IOrbitalPredictionEngine` by #529. `OrbitalPredictionJson` creates, validates,
identifies and strictly round-trips separate track/pass products.
`CameraAgent.Common/Orbital/OrbitalRefreshStatus.cs` owns operational status.
No host DI, scheduled job, HTTP parser, propagator or UI is activated here.

The manifest explicitly binds `hvo-orbital-elements-v1` and
`celestrak-gp-json-v1`. Identity strings are uppercase SHA256, catalog text
is canonical decimal, and software `ImplementationSha256` identifies the
retained implementation/source package, not a platform-specific runtime DLL.
Canonical JSON uses the existing capture canonical writer, typed numeric and
UTC values, ordered collections and a final numeric-zero normalization. Exact
parsing rejects unknown/duplicate/missing members, unsupported metadata,
noncanonical number spellings, bad source bindings and corrupt identities.
Source bytes are hashed unchanged by the acquisition adapter; the shared
snapshot validates their manifest binding, not unavailable original bytes.

Reader unavailable reasons are `InvalidIdentity`, `NotConfigured`,
`NoActiveSnapshot`, `UnknownIdentity`, `Retired`, `IntegrityFailure` and
`StorageUnavailable`. Exact reads use the **manifest** identity and return
`InvalidIdentity` for anything except 64 uppercase hex characters before I/O.
They never substitute current. Cancellation throws `OperationCanceledException`.
`Retired` requires durable retirement evidence; a known manifest with
unexpected missing/corrupt bytes is `IntegrityFailure`. Halted or disabled
refresh does not erase last-known-good readability. Keep active,
previous-active, loading and unretired assessment/product references protected,
including supporting original/model inputs. Other retention count/duration
limits are host-owned. No change token or public lease framework is added.

A repeat acquisition with unchanged payload gets its own manifest/history row;
normalized bytes may be deduplicated by payload hash. B owns the
`unchanged-payload` activation-history representation, durable refresh enum/persisted kind
`OrbitalElementsRefresh` (the kebab-case wording is a prose/telemetry label),
task/DI registration and rollback guidance. Existing immutable revisions keep
new task kinds even after disable/removal: an older binary fails that store
closed. Preserve history; #528 must state the supported recovery boundary and
must not silently discard history or lower durable counters during downgrade.

`OrbitalSelectionPolicy` defaults to withholding stale records. Its hashed
`AllowStaleDiagnostics` opt-in is for explicitly qualified diagnostic use;
`OrbitalPredictionUse.MeasuredAssociation` rejects that policy and nominal
zero EOP. Products preserve signed age and source validity separately.
`OrbitalExposureQuery` requires exact accepted calibration/assessment evidence
for a measured basis and rejects a zero-EOP measured combination. This verifies
existing evidence; it does not deliver #523 or approve a calibration workflow.
External astrometry hashes are compared with their existing casing convention;
orbital provenance stores their uppercase representation.

EOP coverage/status/interpolation and leap-table coverage/hash are product facts.
The #529 engine owns resolved immutable DUT1/polar-motion and leap tables,
loaded before a query, and must verify their identity/coverage against these
facts, reject unsupported leap-crossing intervals and never resolve them over
network or filesystem inside the frame path. These contracts do not implement
time conversion or infer a leap table from a hash. Zero EOP is a declared
configured-rig diagnostic only. Missing solar/shadow identity requires unknown
illumination. Observer height datum and clock/sensor-timing basis remain explicit.

A track binds requested/achieved step and chord error, with separate physical,
clock, calibration and sampling error components. Unknown is nullable, never
zero. Complete tracks require full interval coverage and achieved sampling
bounds; complete results cannot conceal budget/deadline failures or partial
children. Results bind the request's snapshot, policy, interval, observer,
model, population and object set; materialized child collections are defensive.
Pass window-boundary labels must coincide with the search endpoint. Product
fact validation is not numerical or physical qualification.

The immutable examples and file hashes live in
[`tests/fixtures/orbital/contract-v1`](../../tests/fixtures/orbital/contract-v1/README.md).
They are synthetic schema examples, including illustrative pixels, not new
satellite truth. #528 must add reader/provider adapter conformance and #529
independent numerical/projection/sampling validation. Both lanes record the
merged commit, schema/profile and pack hashes on #1402/#1391 before adoption.

## Additive canonical sky pass arc (#529)

The supplemental `hvo-orbital-pass-arc-v1` schema is defined by
`OrbitalPassArcV1` / `OrbitalPassArcJson`, independently of the existing record,
snapshot, refresh, exposure-track and pass-summary schemas. Their original
conformance bytes remain unchanged. `IOrbitalPassArcEngine` is a separate
interface implemented by the same `OrbitalPredictionEngine`; the existing
`IOrbitalPredictionEngine` acquires no members. No host registration or
acquisition contract changes are required by this supplement.

`OrbitalPassArcQuery` binds an immutable canonical parent pass and the original
`OrbitalPassQuery`. It validates snapshot manifest/payload, observer, model,
minimum elevation, population coverage, selection policy, object membership,
record/source/epoch/validity and the parent content hash. It validates record
selection once over **the original search interval**, then executes that exact
record. It never selects against the narrower pass interval. Parent signed ages
and freshness continue to refer to `Pass.SearchInterval`.

`SearchQuerySha256` is SHA256 of a canonical explicitly versioned projection:
`schemaVersion = hvo-orbital-pass-search-identity-v1`, `use`,
`snapshotManifestSha256`, `normalizedPayloadSha256`, `selectionPolicySha256`,
`interval`, `objects` in original request canonical order, `observer`, full
`model`, original `bounds`, and `minimumElevationDegrees`. It never serializes
or hashes the entire snapshot. The arc query hash covers canonical
`{schemaVersion: hvo-orbital-pass-arc-query-v1, searchQuerySha256,
parentPassSha256, bounds}`. The arc product hash covers all product fields
except `productSha256`; the canonical nested parent retains its own hash.
Canonical byte/hash examples are in
[`pass-arc-v1`](../../tests/fixtures/orbital/pass-arc-v1/README.md).

The dedicated `OrbitalPredictionResult<OrbitalPassArcV1>` constructor accepts
one requested object, at most one product and one failure for that object;
`RequestedInterval` is exactly `Pass.PassInterval`. It enforces arc/query/search
and parent hashes and exact angular/work bounds. Complete requires one complete
product, one evaluated object and no failures. Existing exposure/pass result
routes retain their previous generic-kind and provenance validation.

Every sample has UTC, geometric AltAz and optional range/range-rate/angular
speed; `Pixel` is always null and illumination remains Unknown. No exposure,
readout, rig, calibration, clock bound, sky-to-pixel mapping or optical visibility
is manufactured. Physical orbit error is null degrees with
`physical-orbit-error-unqualified`. Numerical sampling is a separate empirical
estimate. The initial producer requires actual resolved, non-nominal EOP and
leap data with matching hashes, coverage and conventions. Nominal zero EOP
returns Unavailable / MissingEarthOrientation; the configured-rig-only
Diagnostic exception is unchanged.

Sampling uses time-synchronous shortest ENU unit-vector spherical interpolation
(`unit-vector-slerp-by-utc-v1`), including north wrap and zenith. Antipodal
endpoints subdivide or remain explicitly unresolved. Samples contain exact
parent start/peak/end UTCs when available; identical anchors share one index.
The same-record/model peak elevation must agree with the parent within
`1e-8` degrees. No second peak search edits the parent. Only consecutive samples
within a segment may be joined. Missing/unresolved work stops the current
producer's useful prefix; a partial parent stays partial, including its original
boundary labels and reason. `CoveredInterval` describes retained first/last UTC,
not implied continuity across arbitrary segment gaps. Complete requires the
full interval, all anchors, one connected segment and achieved step/error
within bounds. No useful samples yields no product and Unavailable.

`OrbitalPassArcBounds` accepts positive finite angular tolerance at most
`0.25` degrees, step at most one second, at most ten million actual core calls,
one million requested samples and 600,000ms. Both propagator initialization
calls and every uncached evaluation consume work; cached probes do not. Output
samples consume a separate allowance. The 16MiB serialized ceiling reserves
256KiB metadata plus a conservative 2KiB per sample/possible singleton segment,
limiting materialized samples to **8,064** even with a larger requested bound.
The producer stops with SampleBudget; parser streaming preflight checks array
counts before typed allocation and canonical creation checks before copying.
Final serialized size is checked exactly. Cancellation and deadline checks cover
probes, materialization, validation, freezing, hashing and final return.

`quartic-cartesian-to-time-slerp-empirical-v1` bounds the fitted Cartesian path's
departure from the UTC-parameterized spherical path and adds an inflated
odd-probe orbit remainder and UTC tick rounding. It splits at EOP interpolation
knots. This is an empirical numerical estimator qualified on frozen independent
inputs, not a global SGP4 proof or physical covariance. Its derivation, fixed
`0.001` degree qualification target, `1e-6` degree independent off-phase allowance,
`1e-8` degree same-UTC anchor allowance, and separate 20ms external event gate
are recorded in [arc qualification](../astronomy/orbital-pass-arc-qualification-v1.md).
