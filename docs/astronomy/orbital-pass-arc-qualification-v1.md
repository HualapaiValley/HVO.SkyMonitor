# Canonical sky pass-arc software qualification v1 (#529)

This additive Astronomy producer returns the unprojected geometric path of one
immutable parent pass, even when the entire path falls outside a camera's field
of view. It uses the original full-search record selection; a detail request
cannot silently select a different epoch. It shares the real SGP4 evaluator,
coordinate/time model and actual-call budget with exposure/pass production.
The separate `IOrbitalPassArcEngine` leaves existing interfaces, model hashes,
source schemas and original conformance fixture bytes unchanged.

This is bounded software evidence. It is not physical orbit covariance,
illumination/visibility, measured association, on-sky accuracy, a global SGP4
completeness proof, host acquisition/DI/UI delivery or integrated five-second
cadence qualification. Geometry-free arcs require resolved non-nominal EOP;
the old configured-rig nominal-zero diagnostic exception is not extended.

## Fixed gates

The binding decisions were recorded before implementation in
[#529 direction](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529#issuecomment-6279868009)
and [numerical gates](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529#issuecomment-6279921029).
Angular tolerance is positive finite and at most 0.25 degrees; maximum step is
at most one second. Qualification requests **0.001 degrees** and allows a
separate **1e-6 degree** independent oracle discrepancy on off-phase comparisons.
At exact independent UTCs, start/peak/end anchor directions must agree within
**1e-8 degrees**. Same-record/model reevaluated peak altitude must agree with
the parent within **1e-8 degrees**. Independent pass-event time agreement remains
**20ms**, separately from angular agreement. Expectations and gates were not
adjusted to fit HVO output.

## Interpolation and empirical estimator

Consumers join samples only within declared segments, at the requested UTC
fraction, with shortest ENU-unit-vector spherical interpolation. The existing
`EnuVector.SphericalInterpolate` provides this operation and handles coincident
vectors and the antipodal exclusion. Scalar azimuth interpolation is invalid
at north wrap and zenith. The producer subdivides antipodal endpoints; unresolved
or missing cells stop a useful prefix rather than drawing across a gap.

For a candidate interval parameterized by UTC fraction `t`, nine actual probes
are evaluated, with cache reuse free and every uncached core call charged.
Even-index probes fit a quartic Cartesian ENU position `P(t)` in Bernstein form;
odd-index probes estimate its orbit remainder. Let endpoint Cartesian positions
be `p0=r0*u0`, `p1=r1*u1`, with shortest angular separation `theta`, and
`L(t)=(1-t)*p0+t*p1`. The elevated linear Bernstein controls are
`(1-i/4)*p0+(i/4)*p1`. The convex-hull norm of the difference between the quartic
controls and these linear controls bounds `|P(t)-L(t)|` for the fitted polynomial.

Define `Q(t)=((1-t)*r0+t*r1)*slerp(u0,u1,t)`. Its endpoint values match `L`.
Since spherical interpolation has constant angular speed in `t`,
`max|Q''| <= 2*|r1-r0|*theta + max(r0,r1)*theta^2`. The endpoint-zero linear
interpolation remainder gives `max|Q-L| <= max|Q''|/8`. Add these two Cartesian
bounds, the empirical orbit remainder
`max(1e-6 km, 8*max odd-probe residual)`, and eight times one UTC tick of maximum
reported Cartesian direction/range motion. If the resulting Cartesian error is
`delta < min(r0,r1)`, the angular estimate is
`asin(delta/min(r0,r1))` in degrees plus a `1e-10` degree numerical floor.
Otherwise the cell subdivides or remains unresolved. EOP interpolation knots
(integer-MJD UTC midnights) and exact peak UTC split cells explicitly.

The polynomial hull and spherical-path calculus describe the fitted functions;
the actual SGP4 remainder remains an **empirical** inflated probe estimate.
The version `quartic-cartesian-to-time-slerp-empirical-v1` preserves that scope.
It is neither a physical orbit-error estimate nor an all-function interval
certificate. Existing pixel `ChordError` is not reused as angular error.

## Immutable independent inputs

The complete portable pack is stored once under
[`tests/fixtures/orbital/pass-arc-v1/independent`](../../tests/fixtures/orbital/pass-arc-v1/independent/README.md).
Its independent helper imported no HVO code, used no HVO expectations and made
no new network requests. Raw retained GP/TLE facts, bounded actual EOP subsets,
leap inputs, source attribution, software/license bytes and generator are pinned
in its original artifact manifest. All 23 entries were verified before copying.

- `cases.json`: 10,467,363 bytes, SHA256
  `582D78B90C17E890C6757D997293C69658739ABDC36A7CC59FB3EF6A53EB1EDC`.
- `artifact-manifest.json`: SHA256
  `CB2DB87F3227087E200E6541E2386A472C4AA02E391E7C71AEF64D8DDEC248E6`.
- Original contract pack manifest remains SHA256
  `15627A25F62FD52A07CA804440280F11E63976BFAB6BB277A62F8FC6FDEB7643`,
  from contract commit `771a0c91da55c57a1bfae9b49de7405cef244a76`.

The oracle uses Python sgp4 2.27, explicit WGS72/AFSPC a, Skyfield 1.55 coordinate
rotation/observer routines, NumPy 2.5.4, exact decimal epoch-elapsed propagation,
split UTC/UT1 and actual DUT1/polar-motion interpolation. UTC samples retain
100ns ticks, including the 0.1234567s off-phase. Its independently refined
primary and half-step/shifted scans and brackets are retained, not fitted to
HVO search output. Actual predicted Bulletin A P and final Bulletin B inputs
remain distinct. Historical cases are near their own element epochs.

## Numerical evidence

`OrbitalPassArcNumericalTests` constructs canonical synthetic parent passes from
independent event facts to compare exact-UTC anchors, then separately runs the
real pass search followed by arc production. The latter compares fixed oracle
UTCs only within the common covered interval. A 20ms event allowance is never
substituted for the 1e-8 degree exact-UTC anchor allowance.

| Full-pass case | Independent samples | Worst off-phase direction error, degrees | Declared maximum estimate, degrees | Actual calls / output samples for independent parent | Serialized bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| ISS Seattle, Predicted EOP | 4,822 | 0.000573868311 | 0.000986222701 | 6,707 / 839 | 263,765 |
| Vanguard1 Sydney, Final EOP | 6,854 | 0.000092879482 | 0.000266752676 | 13,003 / 1,626 | 509,006 |
| Molniya08195 HVO, Final EOP | 9,989 | 0.000000028955 | 0.000000053903 | 58,411 / 7,302 | 2,307,344 |

All 21,665 independent states pass the 0.001+1e-6 degree request gate and the
returned empirical-estimate+1e-6 degree gate. The table conservatively combines
the maximum residual/estimate from the independent-parent and real-search cases.
The worst exact-UTC anchor error is `1.283e-11` degrees. Largest rise/peak/set
residuals are respectively `2.0287ms`, `1.3515ms`, `1.9256ms`, below the separate
20ms gate. The low-elevation, high-eccentricity 12-hour resonant Molniya case
uses actual final EOP newly subset from the retained source; previous nominal
high-eccentricity references are not relabeled as actual-EOP arc evidence.

These selected-pass counts and byte sizes demonstrate bounded output; they are
not a performance benchmark or five-second live adoption claim. Duration varies
by host and includes defensive materialization/hash validation in deadline
checks. Both actual propagator initialization calls are included in call counts.

### Selected-pass resource baseline

The Manual `OrbitalPassArcResourceEvidenceTests` loads the same offline inputs,
runs the real parent search, then observes two consecutive public arc calls per
case. On 2026-10-20, Ubuntu 24.04.4 LTS, eight logical processors, x64 and
.NET 10.0.13, all six returned Complete with identical repeated canonical hashes.
The [machine-readable observations](../validation/orbital-prediction-v1/pass-arc-resources.json)
retain CPU, global allocations, working set, GC counts, exact parent/product
hashes and actual call/sample counts. The Astronomy binary is stamped
`bc03d43192407cadba26b3202a0363e922185c89`; the only uncommitted compiled changes
were this Manual harness and widening the numerical input helper to internal.
No production source changed for the measurement.

| Case | Public boundary first / repeat, ms | Allocations first / repeat, bytes | Serialized bytes, real-search parent |
| --- | ---: | ---: | ---: |
| ISS | 95.12 / 66.47 | 36,811,280 / 36,078,864 | 263,329 |
| Vanguard1 | 138.48 / 114.66 | 68,431,344 / 67,900,072 | 508,920 |
| Molniya08195 | 502.15 / 380.32 | 343,450,720 / 334,357,376 | 2,299,960 |

The bounds are 100,000 actual core evaluations, 100,000 requested samples
(the stricter 8,064 serialized-sample ceiling still applies), 1,500ms, a one-second
maximum step and 0.001 degrees. This is an absolute baseline, not an improvement
or statistically isolated cold/warm comparison. No forced GC was performed.
Public boundary timing includes query validation and final result construction;
the result's internal elapsed field starts after query validation and is captured
before its final defensive construction, so it is not substituted for that
boundary. Loading, parent search, wire serialization, artifact I/O and assertions
are outside timing. Process peak includes fixtures/JIT/prior cases; CPU/global
allocation counters can include instrumentation. Different real-search parent
times/hashes explain the byte-size difference from the independent-parent table.
No full-population or integrated five-second frame claim follows from this run.

Reproduce after a warning-clean Release build with:

```bash
dotnet test tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj \
  --no-build --configuration Release \
  --filter FullyQualifiedName~OrbitalPassArcResourceEvidenceTests \
  --logger trx --results-directory /tmp/hvo-pass-arc-resources
```

The TRX attachments include six actual canonical arc files and the resource
summary. Choose a new output directory to preserve previous observations.

## Semantic and resource evidence

Focused tests cover strict canonical parse/hash/type/provenance checks, original
versus narrowed epoch selection, original age/search lineage, a fully off-rig
path, exact and deduplicated anchors, window/work limits, unknown physical orbit
error/illumination, wrap/zenith/coincident/antipodal paths, missing-probe prefix,
EOP knots and byte/time/sample/evaluation/cancellation limits. Parser array counts
are checked before typed materialization. The producer's conservative 16MiB
allowance limits retained samples to 8,064 before allocation, returning SampleBudget
with a useful prefix. Final freezing/hashing participates in cancellation and
deadline checks; a late deadline cannot return Complete.

The isolated implementation uses SDK 10.0.402 and one focused .NET process at a
time. Complete solution/category/performance/capture gates remain with the root
issue owner. The exact commands and final scoped results are recorded below
before handoff.

On the final isolated source/fixture candidate, the following scoped evidence
passed under SDK `10.0.402`:

```bash
dotnet restore tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj --ignore-failed-sources
dotnet build src/HVO.SkyMonitor.Astronomy/HVO.SkyMonitor.Astronomy.csproj --configuration Debug --no-restore -warnaserror
dotnet test tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~OrbitalPassArc|FullyQualifiedName~OrbitalWorkBudgetTests|FullyQualifiedName~OrbitalObjectEvaluatorTests|FullyQualifiedName~OrbitalPredictionContractTests|FullyQualifiedName~OrbitalContractTests' -warnaserror --logger 'console;verbosity=detailed'
```

The final Release command built the affected project graph warning-clean and
passed **64/64** cases (39 new expanded arc cases plus 25 existing boundary
cases), zero skipped, in 8.312s. The final Debug build passed with zero warnings
or errors. Both project-scoped `dotnet format --no-restore --verify-no-changes`
checks passed, with `--include` restricted to the nine changed orbital C# files
and four new arc test files respectively. `git diff --cached --check` passed.
No solution-wide, category-discovery, capture, performance or protected-CI run
was started by the implementation child.

Starting commit: `26de8e2b9c3b1a50f6ad0fb4eaac364fea86373e`.
The SHA256 of the staged binary diff from that commit, restricted to Astronomy
source, its test project and `tests/fixtures/orbital/pass-arc-v1`, is
`5989F48B2EC23AB18928BE6D74D7A6B530C9168487A9C9EE7BDB029A9D12C882`.
Documentation is excluded from that source/fixture fingerprint. The commit
handoff binds the complete tree; root owns final integrated candidate selection,
full affected Astronomy Unit/CI-control evidence, independent deep review and
the `development/v1` protected profile. Local fixture attributes preserve pinned
bytes on every host, including the raw upstream TLE's intentional CRLF/trailing
comment space; no oracle bytes are normalized by this implementation.
