# Small-Body Ephemeris Contract v1 (#1396)

Status: design checkpoint contract under the
[#1396 source and contract decision](../planning/sky-layers-source-decision-1396.md),
dated 2026-11-27. This document defines the bounded host-neutral schemas,
identity rules, interpolation/projection semantics, observer binding, and
operational refresh read model to be implemented in follow-on slices of `#1396`.
It does not claim that any runtime implementation exists yet.

This is a bounded watchlist small-body (comets and asteroids) ephemeris
contract. It is **not** a satellite GP/SGP4 contract, **not** a full
minor-planet survey catalog, and **not** a generalized data-feed platform.

## 1. Ownership and Architectural Boundaries

- **`HVO.SkyMonitor.Astronomy` (`HVO.SkyMonitor.Astronomy.SmallBodies`)** owns
  the canonical identity, orbit-solution, ephemeris-knot, snapshot, manifest,
  reader interface, interpolation, and horizontal/image-projection evaluators.
  It has no HTTP, SQLite, filesystem, ASP.NET, or SkiaSharp dependencies and
  **does not extend the closed `SolarSystemBody` enum**.
- **`HVO.SkyMonitor.Processing`** owns the optional `PredictedSmallBodyLayer`
  presentation payload and source envelope, strictly separate from
  `PredictedSatelliteTracks` and `TransientObservation`.
- **`HVO.SkyMonitor.CameraAgent.Common`** owns the JPL Horizons / SBDB / Lookup
  HTTP adapter, durable `.small-bodies/` STRICT SQLite store, content-addressed
  immutable snapshots, quarantine, `SmallBodyEphemerisRefresh` automation task
  adapter, operational read model, health check, and metrics.
- **`HVO.SkyMonitor.CameraAgent`** owns owner-only status/watchlist/refresh/resume
  endpoints, Software & Catalog status panel, Automations setup link, and
  Observatory planning/layer UI.
- **`HVO.SkyMonitor.AgentCore`** and **`HVO.SkyMonitor.Catalog.Sqlite`** are
  unchanged.

## 2. Canonical Object Identity vs Orbit-Solution Identity

Object identity and orbit-solution identity are strictly separate immutable
records. Display names, provisional designations, and historical aliases are
never used as primary keys.

### 2.1 `SmallBodyIdentity`

| Field | JSON Type / Format | Required Semantics |
| --- | --- | --- |
| `authority` | string (`"jpl-spk"`) | Fixed v1 authority identifier |
| `spkId` | string (ASCII digits, no leading zeros, 1–12 digits) | Canonical JPL SPK-ID (e.g., `"20000433"` for 433 Eros, `"1000036"` for 1P/Halley). Historical 7-digit asteroid SPK-IDs are resolved to their canonical 8-digit SPK-ID before storage |
| `bodyKind` | enum (`"Asteroid"`, `"Comet"`) | Physical small-body class from JPL SBDB/Horizons (`an`/`au` → `Asteroid`, `cn`/`cu` → `Comet`) |
| `primaryDesignation` | string (1–64 chars) | Canonical IAU/JPL primary designation (`pdes`, e.g. `"433"`, `"1P"`, `"73P-C"`, `"C/2023 A3"`) |
| `fragmentCode` | string or `null` | Explicit comet fragment suffix when applicable (e.g., `"C"` for `73P-C`), else `null` |
| `fullName` | string (1–128 chars) | Full display name from JPL (e.g., `"433 Eros (A898 PA)"`, `"1P/Halley"`) |
| `shortName` | string or `null` | Short label when supplied by JPL (e.g., `"433 Eros"`) |
| `mpcPackedDesignation` | string or `null` | MPC packed designation when supplied |
| `aliases` | array of unique strings (ordinal sorted, $\le 128$ entries) | Historical/provisional designations and alternate SPK-IDs preserved verbatim from SBDB `des_alt` / Horizons Lookup `alias` |
| `orbitClassCode` | string or `null` | JPL orbit class code (e.g. `"AMO"`, `"APO"`, `"HTC"`, `"JFc"`, `"HYP"`) |
| `isNeo` | boolean or `null` | Near-Earth object flag from SBDB (`null` if unknown) |
| `isPha` | boolean or `null` | Potentially hazardous asteroid flag from SBDB (`null` if unknown) |

**Ambiguity rule:** Resolution queries (`horizons_lookup.api` / `sbdb.api`) that
match more than one canonical object (for example, `"73P"` matching parent
apparitions plus split fragments, or `"Halley"` matching multiple objects) must
return an explicit `AmbiguousDesignation` result with bounded candidate
identities ($\le 32$ candidates) and **never** auto-select an arbitrary record.
Watchlist entries always store the resolved `spkId` and `bodyKind`; comet
ephemeris queries pin `DES=<spkId>;CAP;NOFRAG` (or exact fragment `spkId` with
`CAP`).

### 2.2 `SmallBodyOrbitSolution`

| Field | JSON Type / Format | Required Semantics |
| --- | --- | --- |
| `solutionAuthority` | string (`"JPL"`) | Orbit producer authority |
| `orbitId` | string (1–32 chars) | Canonical solution ID (e.g. `"JPL#659"`, `"JPL#75"`) |
| `horizonsRecordNumber` | string or `null` | Horizons `Rec #:` identifier (e.g. `"433"`, `"90000740"`) |
| `solutionDateUtc` | UTC ISO-8601 string or `null` | JPL `soln_date` normalized to UTC |
| `epochTdbJd` | finite number | Osculating orbit epoch in **TDB** Julian Date (`orbit.epoch`), explicitly labelled TDB and never treated as UTC |
| `firstObsDate` | ISO date string or `null` | First astrometric observation date |
| `lastObsDate` | ISO date string or `null` | Last astrometric observation date |
| `dataArcDays` | integer $\ge 0$ or `null` | Observation arc length in days |
| `observationCount` | integer $\ge 0$ or `null` | Number of observations used (`n_obs_used`) |
| `conditionCode` | integer `0..9` or `null` | JPL orbit condition code (`0` best constrained, `9` highly uncertain, `null` when absent/comet) |
| `residualRmsArcsec` | finite number $\ge 0$ or `null` | Normalized astrometric fit RMS in arcseconds |
| `hasCovariance` | boolean | Whether JPL reported covariance (`(+COV)` in Horizons header / covariance available in SBDB) |
| `nonGravitationalModel` | object or `null` | Present when SBDB/Horizons declares non-gravitational parameters (`A1`, `A2`, `A3`, `DT`, `S0` with units) |
| `notValidBeforeUtc` | UTC ISO-8601 string or `null` | Source-declared earliest validity instant from SBDB (`not_valid_before`), else `null` |
| `notValidAfterUtc` | UTC ISO-8601 string or `null` | Source-declared latest validity instant from SBDB (`not_valid_after`), else `null` |
| `planetaryEphemeris` | string (e.g. `"DE441"`) | Planetary ephemeris model reported by Horizons header |
| `smallBodyPerturbers` | string or `null` | Perturber model reported by Horizons header (e.g. `"SB441-N16"`) |
| `horizonsEopFile` | string or `null` | JPL EOP file name reported by Horizons header |

## 3. Bounded Ephemeris Snapshot Schema (`hvo-small-body-ephemeris-v1`)

### 3.1 Observer Binding

Every snapshot is bound to one terrestrial observer location:

```json
{
  "deploymentLocationId": "loc-...",
  "deploymentLocationVersion": 3,
  "latitudeDegrees": 35.5599378,
  "longitudeDegrees": -113.9119818,
  "elevationMeters": 1000.0,
  "heightDatum": "AssumedEllipsoidal",
  "observerBindingSha256": "<64-HEX-UPPER>"
}
```

- `observerBindingSha256` hashes canonical observer coordinates (WGS84 geodetic
  latitude, east-positive longitude, elevation in meters, height datum, and
  optional `deploymentLocationId` + `deploymentLocationVersion`).
- Evaluating a snapshot against any observer whose canonical
  `observerBindingSha256` does not match the snapshot's `observerBindingSha256`
  **fails closed** with `SmallBodyEvaluationUnavailableReason.ObserverMismatch`
  (strict identity equality, matching `horizon-obstruction-v1.md` and
  `orbital-elements-v1.md`; no fuzzy coordinate tolerance).
- Advancing or replacing CameraAgent's `DeploymentLocation` invalidates active
  small-body snapshot usability for live planning and triggers cache
  invalidation; historical snapshots remain readable by exact `manifestSha256`
  for archived captures taken at the prior location.

### 3.2 Per-Object Ephemeris Table (`SmallBodyEphemerisSeries`)

Each watched body in the normalized snapshot carries:

- `identity`: `SmallBodyIdentity`
- `solution`: `SmallBodyOrbitSolution`
- `recordSha256`: 64-char uppercase SHA-256 of canonical object+solution+knots
  (excluding retrieval time and outer snapshot membership, matching the
  `orbital-elements-v1` stability rule)
- `ephemerisProfile`: `"jpl-horizons-observer-airless-v1"`
- `center`: `"coord@399"` (ITRF93/WGS84 topocentric)
- `refractionApplied`: `false` (always `AIRLESS` in stored knots)
- `stepSeconds`: uniform positive integer step between consecutive knots
  (`3600`, `900`, or `300`)
- `validFromUtc` / `validUntilUtc`: inclusive usable UTC evaluation span
- `paddedStartUtc` / `paddedEndUtc`: exact first and last knot timestamps,
  strictly equal to `validFromUtc - stepSeconds` and `validUntilUtc + stepSeconds`
  (guaranteeing a centered 4-point cubic stencil everywhere inside
  `[validFromUtc, validUntilUtc]`)
- `minimumRangeAu`: minimum topocentric range $\Delta_{\min}$ across knots
- `knots`: strictly ascending uniform UTC array (`count >= 4`, `count <= 1024`)

Each `SmallBodyEphemerisKnot` contains:

| Field | Units / Type | Source (`Horizons OBSERVER`) | Semantics |
| --- | --- | --- | --- |
| `utc` | UTC ISO-8601 | `Date__(UT)__HR:MN` (`TIME_TYPE='UT'`, `CAL_TYPE='GREGORIAN'`) | Exact knot UTC instant |
| `astrometricRaDeg` | degrees `[0, 360)` | `QUANTITIES=1` (`R.A.___(ICRF)`) | Topocentric light-time-corrected ICRF RA (no aberration/deflection/refraction) |
| `astrometricDecDeg` | degrees `[-90, 90]` | `QUANTITIES=1` (`DEC____(ICRF)`) | Topocentric light-time-corrected ICRF Dec |
| `apparentRaDeg` | degrees `[0, 360)` | `QUANTITIES=2` (`R.A.__(a-app)`) | Topocentric apparent RA of true equator & equinox of date, **airless** |
| `apparentDecDeg` | degrees `[-90, 90]` | `QUANTITIES=2` (`DEC___(a-app)`) | Topocentric apparent Dec of true equator & equinox of date, **airless** |
| `predictedMagnitude` | finite number or `null` | `QUANTITIES=9` (`APmag` for asteroids, `T-mag` for comets) | Uncertain predicted visual/total magnitude (`null` when `n.a.`) |
| `predictedSecondaryMagnitude` | finite number or `null` | `QUANTITIES=9` (`S-brt` for asteroids, `N-mag` for comets) | Surface brightness or nuclear magnitude (`null` when `n.a.`) |
| `rangeAu` | finite number $> 0$ | `QUANTITIES=20` (`delta`) | Topocentric distance in AU |
| `rangeRateKmPerSec` | finite number | `QUANTITIES=20` (`deldot`) | Topocentric range rate in km/s |
| `solarElongationDeg` | finite number `[0, 180]` | `QUANTITIES=23` (`S-O-T`) | Sun-Observer-Target angle |
| `solarElongationSide` | enum (`"Leading"`, `"Trailing"`, `"Unknown"`) | `QUANTITIES=23` (`/r`: `/L`, `/T`) | Morning (`/L`) vs evening (`/T`) elongation side |
| `phaseAngleDeg` | finite number `[0, 180]` | `QUANTITIES=24` (`S-T-O`) | Sun-Target-Observer phase angle |
| `ra3SigmaArcsec` | finite number $\ge 0$ or `null` | `QUANTITIES=36` (`RA_3sigma`) | Formal 3-sigma RA uncertainty (`null` when `n.a.`, **never `0.0`**) |
| `dec3SigmaArcsec` | finite number $\ge 0$ or `null` | `QUANTITIES=36` (`DEC_3sigma`) | Formal 3-sigma Dec uncertainty (`null` when `n.a.`, **never `0.0`**) |

### 3.3 Manifest (`hvo-small-body-manifest-v1`) and Hard Ceilings

The manifest binds:
- `schemaVersion`: `"hvo-small-body-manifest-v1"`
- `manifestSha256`: uppercase SHA-256 of canonical manifest JSON with
  `manifestSha256` excluded
- `normalizedPayloadSha256` and `normalizedPayloadBytes`
- `provider`: `"nasa-jpl-ssd"`, `horizonsApiVersion`, `sbdbApiVersion`,
  `lookupApiVersion`, `parserVersion`, `normalizerVersion`
- `termsReference`: `"https://ssd-api.jpl.nasa.gov/doc/"` and exact attribution text
- `observerBinding`: Section 3.1 object
- `retrievedUtc`, `watchlistCount`, `succeededCount`, `failedObjects[]` (per-body
  failure reason codes without failing the rest of a valid watchlist),
  `overallValidFromUtc`, `overallValidUntilUtc`
- `rawSources[]`: sanitized per-object source descriptor (`spkId`, sanitized
  query summary without secrets, `rawSha256`, `rawBytes`, `httpStatus`)

**Hard validation & memory ceilings (Proposed defaults):**
- Maximum watched bodies per snapshot: **32**
- Maximum knots per body: **1,024**
- Maximum raw Horizons/SBDB wire response per request: **2 MiB** (decoded **4 MiB**)
- Maximum normalized snapshot JSON payload: **16 MiB**
- Canonical JSON serialization uses the repository's canonical writer with
  ordinal property order, uppercase 64-hex SHA-256, normalized `-0.0` → `0`,
  UTC timestamps, and rejection of `NaN`/`Infinity`, duplicate keys, unknown
  fields, or nesting $> 64$ levels.

## 4. Reader, Interpolation, and Evaluation Semantics

### 4.1 Validated Snapshot Reader (`ISmallBodySnapshotReader`)

Mirrors `IOrbitalSnapshotReader`:

```csharp
ValueTask<SmallBodySnapshotReadResult> ReadCurrentAsync(CancellationToken cancellationToken);
ValueTask<SmallBodySnapshotReadResult> ReadExactAsync(string manifestSha256, CancellationToken cancellationToken);
```

- Exact reads validate 64 uppercase hex characters before I/O and **never**
  substitute the current snapshot.
- Unavailable reasons: `InvalidIdentity`, `NotConfigured`, `NoActiveSnapshot`,
  `UnknownIdentity`, `Retired`, `IntegrityFailure`, `StorageUnavailable`.
- Snapshot materialization verifies hashes, schema, observer binding, knot
  monotonicity, uniform `stepSeconds`, finite numbers, and defensive immutability
  once outside the frame path.

### 4.2 Deterministic Interpolation and Horizontal Projection

1. **Domain check:** For query instant `utc`:
   - Reject if `utc < series.validFromUtc || utc > series.validUntilUtc` →
     `OutOfRange` / `Expired` (no extrapolation).
   - Reject if SBDB `notValidBeforeUtc`/`notValidAfterUtc` excludes `utc` →
     `OutsideSourceValidity`.
   - Reject if `observer` does not match `snapshot.observerBinding` →
     `ObserverMismatch`.
   - Reject if `utc` crosses an unsupported leap second → `UnsupportedLeapCrossing`.
2. **4-point centered cubic Lagrange on unit vectors:**
   - Locate knot index $k$ such that $t_k \le t \le t_{k+1}$ with stencil
     $\{k-1, k, k+1, k+2\}$ (always in-bounds because of the 1-knot padding).
   - Convert the 4 stencil knots' `(apparentRaDeg, apparentDecDeg)` to 3D unit
     vectors $\hat{\mathbf{u}}_j \in \mathbb{R}^3$, evaluate the cubic Lagrange
     interpolant $\mathbf{u}(t)$ at normalized $u = (t - t_k)/h \in [0, 1]$,
     and normalize $\hat{\mathbf{u}}(t) = \mathbf{u}(t) / \|\mathbf{u}(t)\|$.
   - Apply the same stencil independently to `(astrometricRaDeg, astrometricDecDeg)`
     for ICRF coordinates, and scalar cubic Lagrange to `rangeAu`,
     `rangeRateKmPerSec`, `solarElongationDeg` (clamped to `[0, 180]`),
     `phaseAngleDeg` (clamped to `[0, 180]`), and (when all 4 stencil knots are
     finite) `predictedMagnitude` / `predictedSecondaryMagnitude`. For
     `solarElongationSide`, preserve the common value when both bracketing knots
     agree, or emit `Unknown` if the bracketing interval crosses opposition or
     conjunction (`/L` ↔ `/T` transition). For 3-sigma uncertainties, take the
     conservative envelope $\max(\text{linear interpolation}, \text{adjacent knot max})$
     when finite, or `null` (`UncertaintyUnavailable`) if either bracketing knot
     is `null`.
3. **Horizontal & image projection:**
   - Convert interpolated **apparent true-of-date** $(\alpha_{\text{app}}(t),
     \delta_{\text{app}}(t))$ to **geometric unrefracted `AltAzPoint`** using:
     - **Primary (`eop-gast-polar-motion-v1`):** resolved `OrbitalTimeData`
       (UT1 via DUT1, IAU equation of equinoxes for GAST, polar motion
       $(x_p, y_p)$), recording `earthOrientationSha256`, `leapSecondsSha256`,
       and `Final`/`Predicted`/`Mixed` status.
     - **Planning-only degraded fallback (`ut1-approximated-by-utc-v1`):** when
       EOP does not cover `utc` and caller requests `SmallBodyPredictionUse.Planning`,
       compute GMST/GAST using UTC≈UT1 with explicit degraded reason code and
       `timingBasisUncertaintyArcsec = 15.0`. **`MeasuredAssociation` rejects
       this fallback.**
   - Evaluate `#1394` `HorizonObstructionEvaluator` on the **unrefracted
     geometric `AltAzPoint`** (if an active rig horizon profile exists).
   - Apply `AtmosphericRefraction.Apply` (`bennett-1982-standard-atmosphere-v1`)
     **only** when the caller explicitly requests refracted display coordinates.
   - Project through existing `RigProjectionContextFactory` / `IImageProjector`
     (or accepted `AstrometricMapping` when `#523` measured basis is explicitly
     supplied and matches exposure/observer/EOP) and apply
     `ProjectedSceneImageTransformV1` once.
4. **Short-path sampling (Observatory & optional layer):**
   - Bounded short path over `[startUtc, endUtc]` (capped at $\le 72\text{ h}$
     and $\le 256$ samples per body per query, $\le 32$ bodies).
   - Samples are split into connected visible segments at geometric horizon,
     `#1394` site-horizon obstructions/unknown terrain, and sensor/image-circle
     clipping boundaries; displays **never** bridge across clipped/obstructed
     gaps.

### 4.3 Label Withholding and Distinction of Evidence

Every evaluated `SmallBodyPositionProduct` carries:
- `geometricDirection`: unrefracted `AltAzPoint` (plus optional refracted `AltAzPoint` and `PixelPoint`)
- `uncertainty`: `ra3SigmaArcsec`, `dec3SigmaArcsec`, `conditionCode`,
  `uncertaintyStatus` (`Formal3SigmaAvailable`, `Unavailable`, `LargeUncertainty`)
- `photometry`: `predictedMagnitude`, `predictedSecondaryMagnitude`,
  `photometryQualification = "UncertainSourcePredictionNotDetection"`
- `freshness`: `Fresh`, `Stale`, `Expired`, `OutOfRange`, `OutsideSourceValidity`
- `labelDisposition`: `Eligible`, `EligibleWithCaveat`, or `Withheld` (with
  explicit reason: `LargeSkyUncertainty`, `StaleSnapshot`, `HorizonObstructed`,
  `BelowHorizon`, `OutsideSensorBounds`)
- **No `Visible` or `Detected` boolean**, no coma/tail geometry, and no claim of
  image-based identification.

## 5. Operational Refresh Policy and Read Model (`hvo-small-body-refresh-status-v1`)

CameraAgent.Common exposes an operational status DTO mirroring
`hvo-orbital-refresh-status-v1`:

- `enabled`, `watchlistCount`, `currentManifestSha256`, `currentPayloadSha256`,
  `observerBindingMatchesCurrentSite` (boolean)
- `lastAttemptUtc`, `lastRetrievalSuccessUtc`, `lastActivationUtc`,
  `nextDueUtc`, `nextPermittedAttemptUtc`
- `automation`: reserved definition ID (`"small-body-ephemeris-refresh"`),
  task kind (`SmallBodyEphemerisRefresh`), target (`"jpl-ssd:watchlist"`),
  revision hash, last run key
- `providerHalt`: `sinceUtc`, `httpStatus` (nullable), `reason`
- `candidateQuarantine`: `observedUtc`, `rawSha256`, `reason`
- `perObjectSummary[]`: `spkId`, `displayName`, `orbitId`, `validUntilUtc`,
  `freshness`, `lastErrorReason` (no paths, no credentials, no raw provider bodies)
- `reason` and `recoveryAction`

**Provider request guards (enforced by store before outbound I/O):**
- Strictly serial (1 outbound HTTP request at a time across Horizons, SBDB, and
  Lookup) with $\ge 1,000\text{ ms}$ pacing delay between requests in a refresh run.
- Minimum refresh floor: **21,600 s (6 hours)**; default scheduled interval:
  **86,400 s (24 hours)**; maximum: **604,800 s (7 days)**.
- Fixed HTTPS endpoints only (`https://ssd.jpl.nasa.gov/api/horizons.api`,
  `https://ssd.jpl.nasa.gov/api/horizons_lookup.api`,
  `https://ssd-api.jpl.nasa.gov/sbdb.api`), no redirects, no cookies, no
  credentials, 30 s per-request timeout, no generic retry loops.
- Persisted provider halt on HTTP `403`, 3 consecutive `429`s, or unrecognized
  `signature.version` major bump / repeated format drift; requires explicit
  operator resume (respecting `nextPermittedAttemptUtc`).
- Last-known-good snapshot remains active and readable through halts, transport
  failures, and quarantined candidates.
