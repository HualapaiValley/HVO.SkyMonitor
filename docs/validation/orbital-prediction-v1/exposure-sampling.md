# Exposure sampling kernel (#529)

This records the bounded implementation delegate's component evidence. The
[predeclared gates](README.md), frozen v1 products and root engine qualification
remain authoritative. These synthetic tests do not qualify the SGP4 domain,
physical orbit accuracy, optical visibility or integrated frame cadence.

## Integration API and accounting

The internal API is:

```csharp
OrbitalExposureSamplingResult OrbitalExposureSampler.Sample(
    OrbitalExposureQuery query,
    OrbitalProductLineage lineage,
    Func<DateTimeOffset, OrbitalTopocentricState?> evaluate,
    OrbitalWorkBudget budget);

internal sealed record OrbitalExposureSamplingResult(
    OrbitalExposureTrackV1? Track, OrbitalPredictionReason Reason, string Detail);
```

The callback owns actual propagation reservations, including any initialization
evaluations. The sampler never calls `TryEvaluation` or `TryTrack`. It caches
successful and failed callbacks by UTC tick for this object only. A callback
returning null is a point failure unless the shared budget identifies a work
limit. Cancellation propagates through `budget.CanContinue()`; it is not
converted into partial success.

In accordance with the root integration addendum, a fully resolved, excluded
interval returns `(null, None, "interval-geometrically-excluded")`. Its probes
consume evaluations, but no output sample or track reservations. An unresolved
candidate never takes that success path. Root reserves a track after receiving
a nonnull product and owns aggregate result-limit handling.

Coverage points are staged without reservations until a drawable point is
retained. Materialization reserves each unique emitted sample exactly once,
including preceding excluded coverage. Validation probes are not output
samples. Once materialized, a track retains null-pixel coverage samples as well
as drawable samples. Its achieved maximum step is calculated between **all**
emitted samples, including gaps, exactly as v1 requires. A partial product may
therefore honestly report an achieved step larger than requested.

## Frozen numerical method

Seed intervals do not exceed the requested maximum step (at most one second).
Every cell uses nine actual UTC probes. The existing `OrbitalNumericalPath`
fits the even quarter nodes in Cartesian ENU, tests the independent odd eighth
nodes and forms a Bernstein direction tube. Its remainder is
`max(1e-6 km, 8 * largest odd-eighth Cartesian residual)`.

The Bernstein hull strictly bounds the fitted polynomial. The remainder of the
actual point function is an **empirical numerical estimate**, not a formal
all-input SGP4 interval certificate. A callback with an arbitrary narrow feature
invisible to both the fitted polynomial and independent probes is outside that
claim. Root must independently qualify the supported orbital domain and retain
off-phase failures; this component does not invent a global speed bound.

Ordinary cells require a parent/child convergence comparison: the child
remainder must be no larger than the maximum of 0.8 times its parent's
remainder, four Cartesian floors, and the UTC-tick plateau described below.
The first seed normally subdivides once to establish this comparison. At the
refinement floor, a cell can be resolved when its remainder is already within
the numerical plateau and all geometry/chord checks succeed.

UTC has 100 ns ticks. Eight-tick-divisible cells split onto eight-tick-divisible
children, preserving uniform probe times. Other lengths necessarily have
rounded eighths. The plateau allowance is
`16 * max(adjacent Cartesian displacement / adjacent UTC tick count)`, in km,
with the four-floor minimum above. It recognizes tick-rounding residuals when
checking convergence; it does **not** subtract or replace the residual in the
tube or achieved pixel error. This is a numerical convergence rule, not a
derivative certificate.

`OrbitalSampleProjector.Classify` checks the complete direction tube against
the geometric horizon, optical aperture and remaining sensor/crop boundaries.
It uses `ProjectionSamplingBounds.MaximumPixelsPerRadian` and the existing
projectors. All-outside endpoint, midpoint or even all-nine classifications
cannot exclude a mixed tube. A narrow excursion between probes remains a
refinement candidate. Sensor/crop exclusion requires a smooth optical domain;
singular or aperture-overlapping cells remain candidates.

For a wholly drawable cell, a second quartic fits the five actual emitted-pixel
nodes. Its chord bound is the maximum norm of each Bernstein control minus the
corresponding endpoint-chord control. Add the larger of eight times the
independent odd-eighth pixel residual and the Cartesian remainder mapped through
the cap Jacobian and remaining binning scale, plus `1e-8 px` roundoff allowance.
Thus the bound covers the complete fitted curve, including S-curves whose
midpoint residual vanishes. Acceptance uses half the requested chord tolerance
as qualification headroom.

Only the canonical optical projector and
`ProjectedSceneImageTransform.Apply` produce emitted pixels. The latter is
called once per retained projection. Chart probes use the existing optical
family with aperture/sensor clipping removed, while retaining its supported
domain; they never apply ROI/binning a second time.

## Boundaries, partial products and hard limits

Cells with mixed clipping subdivide. At durations at most `1e-4 s`, a boundary
can be resolved if the nine probes show at most one transition, identify one
boundary, and its scalar quartic has derivative controls of one strict sign,
exceeding 32 times its independent residual plus four scalar remainder floors.
An endpoint within four scalar floors can represent a crossing exactly on a
subdivision endpoint. The complete tube's mapped image diameter must also fit
half the requested chord tolerance. Only adjacent drawable probes in this
resolved cell are joined. This is a numerical boundary bracket, not an exact
physical event timestamp.

The maximum recursion depth is 24 and the temporal floor is eight UTC ticks
(`8e-7 s`); cells shorter than sixteen ticks cannot split. Exact/near tangencies,
stationary boundary tubes, singular ranges, failed point intervals and
nonconvergent/tolerance-limited cells remain explicit partial failures. Ranges
no larger than four Cartesian floors cannot support a direction enclosure and
are unresolved immediately. A stationary boundary tube is also withheld early
when subdivision cannot reduce its irreducible floor.

Accepted drawable edges are recorded separately from samples. Product segments
join only these accepted edges, never samples merely adjacent in time. Failed
refinement preserves isolated samples and unaffected portions, with
`ProjectionFailure` ends rather than a chord across the gap. Resolved boundaries
retain `WindowBoundary`, `Horizon`, `Aperture` or `Sensor`. Work-limited ends use
`SamplingBudget` only with the corresponding sample/evaluation/deadline reason.

The shared budget limits every callback, emitted sample and monotonic deadline.
Rejected cells use the same budget and cache. Cache/staged-point storage is
bounded by distinct actual callback times; edge and emitted-point storage is
bounded by emitted samples. Every numerical stencil has nine slots and
recursion is bounded. Final preparation and canonical product creation retain
cancellation/deadline checks and reclassify a deadline reached during product
preparation as partial.

The v1 serialized product limit is 16 MiB, independently of the public sample
count limit. A conservative local allowance reserves 256 KiB for validated
metadata and 768 bytes per sample plus its possible segment. It limits a single
materialized product to 21,504 samples, preserving a valid partial product with
`IncompleteInterval` rather than attempting an oversized canonical product.
This local allowance does not pretend the shared sample counter was exhausted.

Sampling uncertainty equals the maximum accepted drawable-edge estimate and
names `quartic-bernstein-numerical-remainder-v1` explicitly as a numerical
estimate. Isolated points have no drawable chord. Physical orbital error stays
unknown. Clock uncertainty uses the supplied maximum error, else the supplied
estimated error with that distinction in its reason, else unknown. Accepted
measured geometry keeps its evidence identities without inventing pixel
calibration accuracy; configured geometry remains identified as configured.
Illumination is always `Unknown`.

## Focused evidence

Run on .NET SDK 10.0.402, Release, with warnings treated as errors:

```bash
dotnet test tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj \
  --configuration Release --no-restore -warnaserror \
  --filter 'FullyQualifiedName~OrbitalExposureSamplerTests|FullyQualifiedName~OrbitalSampleProjectorTests' \
  --logger 'console;verbosity=detailed'
```

Result: 28 passed (26 sampler cases and the two existing projector cases),
zero failures and zero warnings. The focused class run includes compilation of
its project dependencies; no solution-wide or performance suite ran here.

The callback in every synthetic kernel test charges its own shared evaluation
budget. Dense verification uses `(j + 0.3819660112501051) / N` fractions, different
from both interpolation and validation nodes, and checks the actual projector
against each emitted time-parameterized chord. It checks the reported numerical
estimate as well as the requested tolerance and rejects any drawable gap.

Observed component evidence for the 20 s smooth ISS-like analytic Cartesian
path, at 1 s requested step with positive radial distortion:

- Every optical family used 321 point evaluations and 41 emitted samples.
- Equidistant: estimate `0.000681161 px`; maximum dense residual `0.000510650 px`.
- Equisolid: estimate `0.000656298 px`; maximum dense residual `0.000492007 px`.
- Orthographic: estimate `0.000584382 px`; maximum dense residual `0.000438082 px`.
- Stereographic: estimate `0.000739536 px`; maximum dense residual `0.000554421 px`.
- Perspective: estimate `0.000997659 px`; maximum dense residual `0.000747902 px`.

The narrow perspective fixture uses 200,000 px focal length and a quadratic
Cartesian excursion missed by all nine seed probes. It completes with one
sensor-clipped segment, 2,012 evaluations, 178 samples and `0.0995515 px` maximum
estimate. A separate quadratic excursion crosses the horizon twice between all
nine below-horizon seed probes. Both retain drawable portions.

At the smaller requested 0.07 s step, the 5 s smooth fixture uses 1,153
evaluations / 145 samples, estimate `3.61365e-6 px`, dense `2.49308e-6 px`; the
100 s fixture uses 22,865 evaluations / 2,859 samples, estimate `4.16652e-6 px`,
dense `2.89321e-6 px`. These are operation counts for synthetic numerical
fixtures, not a production performance benchmark.

Additional cases cover a 0.005 px S-curve request with zero midpoint residual,
zenith, north wrap, crop/bin/mirror/rotation in native and readout coordinates,
actual projector aperture/sensor/horizon clipping, exact boundary endpoints,
unresolved tangencies and singular range, disjoint point failures, one-point
and no-point outcomes, sample/evaluation/deadline limits, cancellation during
refinement, zero/short exposure, measured/configured calibration provenance,
clock maximum/estimate/unknown facts, zero-cost excluded products, and a valid
partial product at the conservative serialized output allowance.

Root owns actual SGP4 exposure/reference integration, independent held-out
domain qualification, full query accounting, component resource measurements,
classifier-selected final gates and lifecycle. No live GP fetch, host change,
public contract edit or physical-visibility claim is part of this component.
