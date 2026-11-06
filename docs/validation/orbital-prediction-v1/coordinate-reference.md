# Coordinate and time component evidence (#529)

This slice is software/virtual component fidelity. It establishes no physical
orbit/clock/site accuracy, optical visibility, production source approval or
five-second integrated cadence. The predeclared gates in [README](README.md)
remain unchanged.

## Frozen numerical convention

`OrbitalTimeData.ConversionVersion` is
`teme-gmst82-ut1-pm-no-sp-nominal-rate-v1`:

- Propagated input is TEME position in km and velocity in km/s. It is neither
  J2000 nor a stellar midpoint direction. The reference GP initializer explicitly
  calls Python `Satrec.sgp4init(WGS72, 'a', ...)`, including direct GP DOT/DDOT
  unit conversion. The old mode-`i` POC supplies no expectations here.
- UTC uses `DateTimeOffset` with offset zero. Tick-of-day and integer day are
  split **before** floating conversion. UT1 adds interpolated DUT1 to the small
  fraction and normalizes day rollover; it never first rounds a full Julian
  date. SGP4 elapsed time remains UTC, subject to the epoch-span restriction below.
- GMST82 follows the Appendix C/Skyfield polynomial. With
  `t = (JD_UT1 - 2451545) / 36525`,
  `g = 67310.54841 + (8640184.812866 + (0.093104 - 0.0000062*t)*t)*t`
  seconds, the rotation is `Rz(-theta)`, where
  `theta = 2*pi*frac(frac(JD_UT1) + frac(g/86400))`.
- The velocity rotation subtracts `omega cross r_PEF`, using
  `omega = (1 + g'(t)/(86400*36525))*2*pi/86400` rad/s. This is the analytic
  GMST82 derivative per UT1 second, used as a nominal UTC rate. There is **no**
  extra LOD correction and no derivative of the linearly interpolated DUT1.
  Thus returned velocity is the declared conventional instantaneous state,
  not the full time derivative of every varying table term.
- Polar motion is `Rx(yp) Ry(xp)` with arcseconds converted to radians, applied
  to both position and velocity. Polar-motion derivative is zero; TIO `s'` is
  zero. Modern Skyfield ITRS's additional `s'` must not be substituted into this
  comparison. No nutation/precession or equation-of-equinoxes term is added.
- Observer geometry is WGS84, `a = 6378.137 km`, inverse flattening
  `298.257223563`, geodetic latitude, east-positive longitude and **ellipsoidal**
  height. An assumed datum remains explicitly assumed in request facts.
  The observer is stationary in Earth-fixed coordinates.
- Geometric local ENU gives altitude, azimuth, range, range-rate and angular
  speed. `|unitDirection cross relativeVelocity| / range` avoids azimuth
  differentiation, wrap and zenith singularities. Exact zenith returns an
  arbitrary azimuth zero; the direction vector is authoritative. No refraction,
  illumination, sunlight or visibility assertion is made.

## Immutable resolved inputs and engine integration

Construct `OrbitalTimeData.FromFinals2000A(eopBytes, leapBytes)` once during
initialization. The object computes hashes from actual bytes and retains only
private materialized value arrays. Mutation of caller buffers cannot change it.
Inputs are bounded to 8 MiB EOP / 64 KiB leap, with bounded row counts. There is
no path, stream, filesystem, network or updater dependency in evaluation.

For each request call:

```csharp
timeData.ValidateModel(request.Model, request.Interval, request.Use, geometryBasis);
// For EVERY selected record, before any propagation:
timeData.ValidatePropagationInterval(record.EpochUtc, request.Interval);
var time = timeData.Evaluate(sampleUtc);
var earthFixed = OrbitalCoordinateConverter.ToEarthFixed(temeState, time);
var observerFrame = OrbitalObserverFrame.Create(request.Observer); // reuse per request
var topocentric = observerFrame.Evaluate(earthFixed);
```

`OrbitalTimeData` and `OrbitalJulianDate` are public numerical input APIs;
conversion and observer methods use the root-owned internal Cartesian carriers.
No additional serialized orbital contract is introduced. Root must bind the
exact `ConversionVersion`, `InterpolationVersion`, hashes, coverage and status
properties into model facts. `ValidateModel` compares them against the actual
resolved input, not against assertions supplied by a caller. Case-insensitive
hex hashes follow the shared contract's convention.

`ArgumentException.Message` carries stable failure reasons such as
`resolved-time-model-mismatch`, `eop-outside-coverage`,
`eop-missing-daily-bracket`, `leap-outside-coverage`,
`unsupported-leap-crossing`, and `zero-eop-diagnostic-only`. Root maps these
failures into the shared result/reason vocabulary; it must not replace them with
complete empty results. A nonfinite/zero observer range fails explicitly.

`CreateNominalZeroDiagnostic(leapBytes)` is a separate explicit constructor.
Model validation permits it only for `Diagnostic` plus `ConfiguredRig`; leap
identity, coverage and epoch-span validation remain required. The engine must
retain the shared degraded diagnostic reason and withhold measured precision.

## EOP selection and unsupported intervals

[USNO's fixed-width format](https://maia.usno.navy.mil/ser7/readme.finals2000A)
defines Bulletin B final values separately from Bulletin A observed/predicted
values. This parser uses complete Bulletin B PM-x/PM-y/DUT1 triples as `Final`;
otherwise it accepts complete Bulletin A triples with both flags `P` as
`Predicted`. A table containing both is `Mixed`, and a bracket spanning their
transition reports `Mixed`. Rapid-only `I` rows are not relabeled final; absent
B or incompatible component statuses remain unavailable gaps. Partial B triples,
malformed values/flags, duplicates and unordered epochs fail. LOD is neither
required nor used in this declared convention.

Coverage is the first through last usable knot, inclusive; it does not promise
that its interior has no holes. Exact knots evaluate as stored. Interpolation
requires two immediately adjacent daily usable knots; a missing day, skipped
rapid row, irregular spacing or coverage overrun fails. `ValidateModel` checks
all daily brackets of the requested interval, including holes not hit by the
endpoints. No extrapolation or implicit filling is performed.

The independent IERS leap artifact covers 1972-01-01 through, but excluding,
2027-06-28T00:00:00Z. The inclusive contract coverage ends one tick before
expiration. It is not inferred from DUT1. Leap insertion itself is not
representable by `DateTimeOffset`; an interval crossing any retained offset
change is unsupported. This applies **also between element epoch and query**:
an exposure entirely after an insertion cannot use a pre-insertion element epoch
through simple UTC subtraction. The root engine must call the epoch check for
every selected object, including a query earlier than its epoch.

This first implementation also rejects interpolation inside a daily DUT1
bracket crossing an insertion, even if a point or short query lies before the
insertion. For example, non-knot times on 2016-12-31 are unavailable; the stored
2016-12-31T00:00 knot, the 2017-01-01T00:00 knot and post-insertion brackets can
be evaluated individually. No continuous-time leap-aware DUT1 interpolation or
pre-1972 UTC-rate model is claimed.

## Portable inputs, attribution and reproduction

[coordinate-source-manifest.json](coordinate-source-manifest.json) pins exact
bytes, sizes, original source URLs, retrieval times, transformation and software
source hashes. It covers three small EOP subsets, the independent
raw leap input, coordinate/state outputs, event outputs, generator and licenses.
The retained full EOP file is **not** a portable dependency: its pinned original
hash is `BFA2066AAB0400B075357F573AF6081E0068A518993609EEE0C5CA991BAB0845`;
the generator's optional extraction switch checks that hash before selecting
lines. The subsets trim trailing ASCII spaces and normalize line endings to LF;
all numeric fixed columns are preserved. The selected windows are MJD 53100..53103 (2004-04-05..08), 57752..57756
(2016-12-30..2017-01-03), and 61331..61338 (2026-10-18..25).

The [official IERS leap file](https://hpiers.obspm.fr/iers/bul/bulc/ntp/leap-seconds.list)
was fetched once on 2026-10-19 at 22:11 UTC (minute precision), and retained
unchanged. Its SHA256 is
`DB5A895F16853B03BFC865E8D68F9FC8710EF1740E3400C701CD46A5BBBC3433`.
It explicitly declares public-domain status and its own expiration. This resolves
the missing independent leap fixture; it does not authorize production source
adoption, refresh scheduling or physical/clock accuracy claims.

Credit IERS/Paris Observatory and USNO for the scientific time/EOP facts; the
EOP raw artifact carries no bespoke license grant and this work does not infer
institutional production/redistribution approval. Credit David Vallado and
coauthors for AIAA 2006-6753 Rev2 Appendix C. The independent Skyfield 1.55
TEME/ITRF helper, WGS84 observer and test constants are MIT; preserve
[its license](coordinate-SKYFIELD-LICENSE.txt). Python-sgp4 2.27 is MIT;
[its license](coordinate-PYTHON-SGP4-LICENSE.txt) is retained. Their libraries
are offline oracle dependencies, not production dependencies. The six GP facts
and source/terms remain pinned by the existing orbital-reference-v1 pack;
no new GP acquisition occurred.

Use a separate Python environment with `skyfield==1.55 sgp4==2.27 numpy==2.5.4
jplephem==2.24` installed, then run offline:

```bash
python docs/validation/orbital-prediction-v1/coordinate-reference-generator.py
python docs/validation/orbital-prediction-v1/coordinate-reference-generator.py --verify
```

This uses the committed small raw subsets and writes the independent outputs
and source manifest. The optional `--retained-finals <file>` re-extracts the
same fixed-column subsets from the exact retained full EOP input. No execution
path downloads inputs or calls HVO to manufacture expectations. Byte-for-byte
regeneration is checked separately from numerical HVO validation.

## Numerical and focused-test evidence

Release build on .NET SDK 10.0.402, Linux x64, is warning-clean. Scoped
`dotnet format --verify-no-changes` passes for all five new C# files. Offline
regeneration is byte-identical for all nine manifest entries plus the manifest,
and the hash-only verification passes. Both focused
classes pass (nine tests); no full solution matrix, protected CI or resource
harness was run in this delegated slice. Actual session metadata confirms
`gpt-6.1-sol`, `xhigh`, primary T3 provider `codex`, isolated branch
`t3/orbital-coordinate-time-slice` based on
`1761432a1ccb8743c45b7d2f1c83e33522c67d5c`.

| Stage | Worst residual | Case |
| --- | --- | --- |
| Appendix C position, maximum component | 4.8867150326259434e-8 km | Literal represented UT1 from upstream |
| Appendix C velocity, maximum component | 6.63223631569565e-8 km/s | Literal represented UT1 from upstream |
| Exact-convention position norm | 7.279509460923746e-12 km | GP 2866, 2026-10-20T13:04:21.123456Z |
| Exact-convention velocity norm | 2.220446049250313e-15 km/s | GP 100000, same UTC |
| Range | 9.094947017729282e-13 km | GP 44718 and pole cases |
| Range-rate | 1.7319479184152442e-14 km/s | Synthetic near-zenith observer case |
| Direction separation | 2.6872885310086732e-15 rad | Synthetic near-zenith observer case |
| Angular-speed difference | 6.661338147750939e-16 deg/s | Synthetic near-zenith observer case |

Appendix C compares its **literal represented single-double UT1**, exactly as
retained by the independent source; it does not replace it with the more precise
split UTC timestamp. Split UTC subsecond ticks/day rollover are qualified in a
separate test. No tolerance changed after failure. An initially added scalar
azimuth assertion failed at near zenith by 4.262731181370327e-7 degrees despite
a 2.69e-15 rad direction residual. That extra, ill-conditioned scalar assertion
was replaced with reconstruction of the returned alt/az into a direction,
checked against the original predeclared 1e-8 rad gate. The production transform
and independent fixture were unchanged by that test correction.

Run the focused checks with:

```bash
dotnet build tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj --no-restore --configuration Release -warnaserror
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj --no-build --configuration Release --filter 'FullyQualifiedName~OrbitalCoordinateConversionTests|FullyQualifiedName~OrbitalTimeDataTests'
```

The root owner still owns category inventory, shared notices, changelog, engine
integration, classifier-selected candidate gates, independent review and target
branch CI. Add `OrbitalCoordinateConversionTests` and `OrbitalTimeDataTests` to
the Astronomy category inventory. The preconfigured fixture-copy wildcard is
sufficient; no project-file edit is needed.

## Event pack for later root qualification

`coordinates/pass-references.json` retains independent WGS72/mode-`a` geometric
ISS Seattle and HVO events plus LES-5/HVO deep-space events for the 60 hours from
2026-10-19T12:00Z. There are 38/40/1 horizon crossings and 19/20/1 above-horizon
local peaks respectively; window endpoint altitudes remain explicit. Every
crossing stores direction, endpoint altitudes and a bracket, not a timestamp
with implied precision. The largest crossing/peak bracket is
0.0004693585797213018 seconds, below the 0.001-second oracle target.

Each ISS case also has a deliberately synthetic threshold 0.00001 degree below
its highest peak, producing a near-tangent excursion. These cases qualify
altitude/bracket handling separately, not uniform timestamp accuracy. The
30-second ISS / 60-second deep-space scan seeds and numerical maximization are
reference construction details, **not** a general completeness proof or the HVO
sampling algorithm. A root implementation must independently prove/expose
search completeness and compare its well-conditioned roots with brackets at
most 0.01 s and reference agreement at most 0.02 s.

Still owned by root: consume these event references in pass-search tests;
constructed two-crossing/threshold-touch/flat-peak/multiple-peak/window clipping
kernels; other orbital regimes and unresolved-interval behavior; adaptive image
sampling, projection/readout and resource/cancellation evidence. There is no
solar binary, sunlight/visible label or camera harness in this pack.
