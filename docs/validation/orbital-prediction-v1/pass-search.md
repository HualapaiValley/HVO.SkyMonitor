# Bounded future-pass search (#529)

This component implements numerical geometric pass search from one selected
immutable record. It does not establish physical orbit accuracy, optical
visibility, solar illumination or integrated capture cadence. The predeclared
[qualification gates](README.md) and [model decision](../../planning/orbital-data-decision-527.md)
remain authoritative. The root engine owns the supported numerical domain and
independent held-out qualification; this document records component calibration
and regression evidence, not an all-input certification.

## Integration API and responsibilities

```csharp
internal static OrbitalPassSearchResult OrbitalPassSearch.Search(
    OrbitalPassQuery query,
    OrbitalProductLineage lineage,
    OrbitalMeanElements elements,
    Func<DateTimeOffset, OrbitalTopocentricState?> evaluate,
    OrbitalWorkBudget budget);

internal sealed record OrbitalPassSearchResult(
    IReadOnlyList<OrbitalPassSummaryV1> Passes,
    OrbitalPredictionReason Reason,
    string Detail);
```

The caller selects one record for the entire query, resolves and validates
immutable EOP/leap inputs, and rejects unsupported **epoch-to-query** leap spans
before any SGP4 evaluation. It supplies observer-relative geometric states using
the declared WGS72/AFSPC-a, TEME/GMST82, WGS84-observer convention. `null` means a
point failure or work limit. The callback owns `budget.TryEvaluation()` immediately
before actual propagation; this component never charges it a second time.
Cache hits perform no propagation. Query bounds, including the seven-day public
ceiling, remain the frozen v1 contracts' responsibility.

The search reserves `TryPass()` when an above-threshold pass opens. This permits
retaining a useful open pass after the evaluation/deadline budget has stopped
accepting reservations. A numerically ambiguous zero-duration candidate need not
produce a product. The root must not reserve these pass results again. Internal
probes do not consume the emitted exposure-sample budget. Each retained product
uses `OrbitalPredictionJson.CreatePass`; its search interval and lineage describe
the original query, and its pass interval describes the retained contiguous run.
The root aggregates the returned single reason as at most one failure per object.

## Frozen numerical procedure

The initial seed duration is
`min(300 s, P*(1-e)^(3/2)/(64*sqrt(1+e)))`, rounded down to integral UTC ticks.
It is a heuristic, never an exclusion rule. Seeds stop exactly at the query end
and split at UTC midnight EOP interpolation knots. An impractically small seed
is explicitly outside this component's numerical profile.

Each cell evaluates nine actual states at eighth fractions. Even nodes fit a
Cartesian ENU quartic; odd nodes independently estimate the residual through
the shared `OrbitalNumericalPath` implementation. Its remainder is
`max(1e-6 km, 8*maximum odd-node Cartesian residual)`. Its Bernstein controls and
inflated direction cap provide whole-fitted-curve threshold bounds. At zero
elevation, the Up control hull gives a tighter scalar bound. All accepted cells
also require positive enclosed observer range. Equal-sign endpoints, midpoint,
or even nine below-threshold probes cannot discard a cell whose tube overlaps
the threshold. The narrow-crossing regression deliberately puts both crossings
between every initial probe.

An estimated remainder above `8e-6 km` must decrease by at least 10% against its
parent fit before a cell is accepted. Otherwise the cell subdivides. Below that
level the independently measured residual is at or below the coordinate
comparison resolution, and tick rounding can prevent monotonic reduction; the
**full computed inflated remainder is still used**, without reducing it to the
floor. Refinement depth is at most 32 and the minimum unresolved cell duration
is 10 microseconds. Nonconvergence or an observer singularity at either floor
returns `IncompleteInterval`.

Rise/set brackets are at most 0.01 seconds. Acceptance requires opposite-sign
(or exactly zero endpoint) actual elevations, a definite fitted derivative
sign, monotonic actual probes, and endpoint elevation change greater than four
times the angular remainder allowance. Exact shared-node crossings are consumed
once. The returned event is the bracket midpoint, or the exact zero endpoint.
Window-clipped boundaries equal the exact query endpoint. A threshold touch
does not establish a positive-duration pass: unresolved touch versus crossing
returns `IncompleteInterval`, never a fabricated short pass or a complete empty
answer for the unresolved interval.

Every above-threshold cell competes for the pass's global peak. For fitted
Cartesian coordinates `(e,n,u)`, the altitude derivative has the sign of the
degree-eleven Bernstein polynomial
`u'*(e²+n²) - u*(e*e' + n*n')`. Its coefficient hull excludes fitted interior
extrema in monotone cells. Remaining cells subdivide, with actual refits and
convergence checks, until at most 0.005 seconds; competing cells can also be
excluded by their upper elevation bound. Thus a connected pass can contain
multiple local maxima. Query endpoints participate. Elevations within
`1e-12 degrees` tie in favor of the earliest evaluated UTC; a flat cell uses its
earliest point. No universal peak-time accuracy is asserted for a truly flat
or numerically indistinguishable maximum.

All seed, cell, boundary and peak loops check `budget.CanContinue()`, including
cache hits; cancellation throws. The per-object tick cache and retained peak-cell
list are bounded by actual evaluated points and the query's evaluation ceiling.
Rejected cells' evaluations remain charged. Fixed-degree polynomial operations
and depth-limited refinements introduce no unconstrained iteration. No network,
storage access or shadow model is introduced.

## Failure and partial-result policy

Completed earlier passes survive a later point, convergence or work failure.
A useful already-open pass survives with `Partial`, `WorkLimit` and the actual
failure reason, ending at its last established covered point. If no useful run
has been established, the result can contain no passes while still carrying an
explicit failure. It must not be promoted to a complete empty search by the
caller. Exhausted result slots retain previous products and return
`ResultBudget`. Deadline, evaluation, cancellation and propagation handling are
shared with the root budget; no exception is converted into a successful empty
result.

`Complete` means numerical completion under this estimator and the independently
qualified domain. Bernstein hull geometry and polynomial derivative signs have
strict mathematical meaning for the fitted polynomial. The SGP4 interpolation
remainder, monotonicity transfer to the actual point function, and numerical
peak agreement are empirically qualified. Finite probes cannot certify an
arbitrary analytic function or exclude every unmodeled infinitesimal excursion.
The component makes no physical uncertainty or visibility claim.

## Component evidence

Run the focused selection with SDK 10.0.402:

```bash
dotnet test tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj \
  --configuration Release --no-restore \
  --filter 'FullyQualifiedName~OrbitalPassSearchTests' \
  --logger 'console;verbosity=detailed'
```

The retained tests cover a positive excursion wholly between all nine initial
probes; exact/near threshold touches; nonzero elevation threshold; window-only
monotonic passes; both window clipping directions; multiple unequal/equal peaks;
flat earliest-time policy; continuous/never-visible GEO-like vectors; evaluation
limits in fitting/events/peaks; result limits; deadline; cancellation during both
event and peak refinement; and completed/open pass preservation after a future
point failure. Singular and discontinuous point functions explicitly remain
incomplete under bounded work. Internal sample usage remains zero.

The portable mode-a `coordinates/pass-references.json` is generated independently
of this component, with provenance and hashes in
[coordinate evidence](coordinate-reference.md). Tests construct the real
`Sgp4ObjectPropagator`, resolved time data and observer frame directly, opt in to
stale diagnostics for the 60-hour windows, and validate propagation leap spans
before evaluation. All 79 reference crossings have independent brackets at most
0.001 seconds; the tests independently check signs across a 0.01-second HVO
bracket and require event differences at most 0.02 seconds. They also compare all
40 independently refined peaks, including LES-5's broad maximum.

The focused Release run on 2026-10-19 passed all 13 tests, compiling with
`-warnaserror`. Search-only counts (the subsequent explicit bracket checks are
excluded) were:

- ISS Seattle, 60 hours: 19 passes, 38 crossings, 46,245 evaluations.
- ISS HVO, 60 hours: 20 passes, 40 crossings, 46,577 evaluations.
- LES-5 HVO, 60 hours: one pass, one crossing, 6,103 evaluations.

Worst absolute reference differences were 0.0026188 seconds for a crossing,
0.0008209 seconds for a peak, and `5.331344254955184e-10 degrees` for peak
altitude. The two retained near-tangent ISS thresholds also completed, without
claiming uniform tangent accuracy: Seattle rise/set residuals were
`+0.0012512/-0.0012513 seconds` (27,919 evaluations), and HVO residuals were
`+0.0003966/-0.0023508 seconds` (10,648 evaluations). Their reported peak
altitudes were 82.1462321528904 and 88.1312093439604 degrees respectively.
These deliberately difficult two-second windows demonstrate bounded work;
they are not throughput or physical confidence claims. Ordinary full-window
search elapsed times in that test process were about 151, 167 and 22 ms;
the root's resource/held-out qualification owns reproducible performance
acceptance.

Calibration exposed two implementation defects before qualification: an event
at a shared cell node was initially consumed twice; and floating duration
addition left a one-tick terminal cell while an overly strict convergence test
repeated work on roundoff residuals. Exact endpoint ownership, integral seed
ticks and the residual-resolution convergence rule above address them. A former
`1e-9 degree` peak tie tolerance selected a point 0.213 seconds early on LES-5's
broad maximum; tightening the deterministic tie tolerance to `1e-12 degrees`
preserves the original 0.02-second gate. No fixture or acceptance tolerance was
changed. These final constants must be frozen for root's held-out qualification.
