# Small-Body Ephemeris Contract v1 (#1396)

Status: design checkpoint contract under the
[#1396 source and contract decision](../planning/sky-layers-source-decision-1396.md),
dated 2026-11-27. This document defines the bounded host-neutral schemas,
provider-specific identity and solution-matching rules, motion-sensitive
interpolation/projection semantics, observer binding, exact-manifest replay
pinning, and operational refresh read model to be implemented after Slice S0
(approved immutable fixture publication) under `#1396`. It does not claim that
any runtime implementation exists yet.

This is a bounded watchlist small-body (comets and asteroids) ephemeris
contract. It is **not** a satellite GP/SGP4 contract, **not** a full
minor-planet survey catalog, and **not** a generalized data-feed platform.

**Repository fixture publication gate (`S0-Clearance`):** Consistent with
Section A.2 of [`sky-layers-source-decision-1396.md`](../planning/sky-layers-source-decision-1396.md),
no blanket exemption is claimed for committing verbatim JPL Horizons, SBDB, or
Lookup query headers or raw wire payloads into this repository. Until/unless
primary JPL/Caltech reuse/redistribution terms are directly verified and
recorded under Gate **`S0-Clearance`**, all repository-committed Slice S0
fixtures under `tests/fixtures/small-bodies/v1/` MUST use strictly HVO-authored
synthetic inputs, with live-API wire-format and numerical parity verified
locally/on-demand against an uncommitted local cache.

## 1. Ownership and Architectural Boundaries

- **`HVO.SkyMonitor.Astronomy` (`HVO.SkyMonitor.Astronomy.SmallBodies`)** owns
  the canonical identity, orbit-solution, ephemeris-knot, snapshot, manifest,
  reader interface, interpolation, and horizontal/image-projection evaluators.
  It reuses `ObserverLocation`, `AltAzPoint`, `PixelPoint`, `ProjectionContext`,
  `IImageProjector`, `ProjectorFactory`, `RigProjectionContextFactory`,
  `AstrometricMapping.Projection`, `AtmosphericRefraction`,
  `HorizonObstructionEvaluator`, and `OrbitalTimeData`. It has no HTTP, SQLite,
  filesystem, ASP.NET, or SkiaSharp dependencies and **does not extend the
  closed `SolarSystemBody` enum**.
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
- **`HVO.SkyMonitor.AgentCore`** (owning `HorizonObstructionProfile`,
  `CameraRigConfig`, `FrameReadoutDescriptor`) and
  **`HVO.SkyMonitor.Catalog.Sqlite`** are unchanged.

## 2. Canonical Object Identity vs Orbit-Solution Identity

Object identity and orbit-solution identity are strictly separate immutable
records. Display names, provisional designations, and historical aliases are
never used as primary keys.

### 2.1 `SmallBodyIdentity` and Provider Field Precedence

| Field | JSON Type / Format | Provider Precedence & Required Semantics |
| --- | --- | --- |
| `authority` | string (`"jpl-spk"`) | Fixed v1 authority identifier |
| `spkId` | string (ASCII digits, no leading zeros, 1–12 digits) | Canonical JPL SPK-ID (`sbdb.object.spkid`, verified equal to Horizons Lookup `spkid`, e.g., `"20000433"` for 433 Eros, `"1000036"` for 1P/Halley). Historical 7-digit asteroid SPK-IDs (e.g. `"2000433"`) are resolved via SBDB/Lookup to their canonical 8-digit SPK-ID before storage |
| `bodyKind` | enum (`"Asteroid"`, `"Comet"`) | Physical small-body class from SBDB `object.kind` (`an`/`au` → `"Asteroid"`, `cn`/`cu` → `"Comet"`) |
| `primaryDesignation` | string (1–64 chars) | **Authoritatively sourced from SBDB `object.des`** (e.g. `"433"`, `"1P"`, `"73P-C"`, `"C/2023 A3"`). **Never overwritten by Horizons Lookup `pdes`** (for example, live checks on `433 Eros` show SBDB `object.des = "433"` while Horizons Lookup `pdes = "A898 PA"`). If Lookup `pdes` differs from `primaryDesignation`, `pdes` is preserved in `aliases[]` |
| `fragmentCode` | string or `null` | Explicit comet fragment suffix when applicable (e.g., `"C"` for `73P-C`), else `null` |
| `fullName` | string (1–128 chars) | Authoritatively from SBDB `object.fullname` (e.g., `"433 Eros (A898 PA)"`, `"1P/Halley"`); fallback to Horizons `Target body name` stripped of `{source: ...}`, then Lookup `name` |
| `shortName` | string or `null` | From SBDB `object.shortname` when present (e.g., `"433 Eros"`), else `null` (e.g., absent for `1P/Halley`) |
| `mpcPackedDesignation` | string or `null` | MPC packed designation when explicitly supplied, else `null` |
| `aliases` | array of unique strings (ordinal sorted, $\le 128$ entries) | Deduplicated ordinal-sorted union of SBDB `object.des_alt`, Horizons Lookup `alias[]`, and Horizons Lookup `pdes` (whenever distinct from `primaryDesignation`, e.g., `"A898 PA"` for `433 Eros`). Never contains `primaryDesignation` duplicated if identical |
| `orbitClassCode` | string or `null` | SBDB `object.orbit_class.code` (e.g. `"AMO"`, `"APO"`, `"HTC"`, `"JFc"`, `"HYP"`) |
| `isNeo` | boolean or `null` | Near-Earth object flag from SBDB `object.neo` (`null` if unknown) |
| `isPha` | boolean or `null` | Potentially hazardous asteroid flag from SBDB `object.pha` (`null` if unknown) |

**Ambiguity rule:** Resolution queries (`horizons_lookup.api` / `sbdb.api`) that
match more than one canonical object (for example, `"73P"` matching parent
apparitions plus split fragments, or `"Halley"` matching multiple objects) must
return an explicit `AmbiguousDesignation` result with bounded candidate
identities ($\le 32$ candidates) and **never** auto-select an arbitrary record.
Watchlist entries always store the resolved `spkId` and `bodyKind`; comet
ephemeris queries pin `DES=<spkId>;CAP;NOFRAG` (or exact fragment `spkId` with
`CAP`).

### 2.2 `SmallBodyOrbitSolution`, Timezone/Partial-Date Rules, and Horizons↔SBDB Cross-Check

| Field | JSON Type / Format | Required Semantics |
| --- | --- | --- |
| `solutionAuthority` | string (`"JPL"`) | Orbit producer authority from SBDB `orbit.source` |
| `orbitId` | string (1–32 chars) | Canonical compound solution ID `"<solutionAuthority>#<sbdb.orbit.orbit_id>"` (e.g. `"JPL#659"`, `"JPL#75"`), matching Horizons header `{source: JPL#659}` |
| `horizonsRecordNumber` | string or `null` | Horizons `Rec #:` identifier (e.g. `"433"`, `"90000030"`, `"90000740"`) |
| `solutionDateSourceRaw` | string (`"YYYY-MM-DD HH:MM:SS"`) or `null` | Verbatim JPL `orbit.soln_date` (normalized from Horizons `YYYY-Mon-DD_HH:MM:SS` when compared), recorded in JPL Pasadena local time |
| `solutionDateSourceTimeZone` | string (`"America/Los_Angeles"`) or `null` | Explicit IANA timezone for `solutionDateSourceRaw` (`"America/Los_Angeles"` whenever `solutionDateSourceRaw` is non-null, else `null`) |
| `solutionDateUtc` | UTC ISO-8601 string or `null` | `solutionDateSourceRaw` deterministically converted from `America/Los_Angeles` (PST/PDT) to UTC (e.g., `"2021-05-24 17:55:05"` PDT → `"2021-05-25T00:55:05Z"`). If an ambiguous DST fall-back local wall time ever occurs, `solutionDateUtc` MUST be set to `null` rather than guessing while `solutionDateSourceRaw` is preserved |
| `epochTdbJd` | finite number | Osculating orbit epoch in **TDB** Julian Date from matched SBDB `orbit.epoch`, explicitly labelled TDB and never treated as UTC (note: Horizons `OBJ_DATA` may report a different reference epoch for the same `JPL#<id>` solution) |
| `firstObsDateRaw` | string (`^\d{4}-(\d{2}\|\?\?)-(\d{2}\|\?\?)$`) or `null` | Unparsed partial-or-full date string verbatim from SBDB `orbit.first_obs` (e.g., `"1893-10-29"`, `"1919-??-??"`). **Must never be parsed with strict ISO date/timestamp parsers** |
| `lastObsDateRaw` | string (`^\d{4}-(\d{2}\|\?\?)-(\d{2}\|\?\?)$`) or `null` | Unparsed partial-or-full date string verbatim from SBDB `orbit.last_obs`. **Must never be parsed with strict ISO date/timestamp parsers** |
| `dataArcDays` | integer $\ge 0$ or `null` | Observation arc length in days from SBDB `orbit.data_arc` |
| `observationCount` | integer $\ge 0$ or `null` | Number of observations used (`orbit.n_obs_used`) |
| `conditionCode` | integer `0..9` or `null` | JPL orbit condition code (`0` best constrained, `9` highly uncertain, `null` when absent) |
| `residualRmsArcsec` | finite number $\ge 0$ or `null` | Normalized astrometric fit RMS in arcseconds (`orbit.rms`) |
| `hasCovariance` | boolean | Whether JPL reported covariance (`(+COV)` in Horizons header / covariance available in SBDB) |
| `magnitudeModel` | `SmallBodyMagnitudeModel` object or `null` | Explicit photometric model parameters from SBDB `phys_par` / Horizons `OBJ_DATA`: `{ kind ("AsteroidHG" \| "CometM1K1M2K2" \| "Unknown"), absoluteMagnitudeH, slopeParameterG, totalMagnitudeM1, totalSlopeK1, nuclearMagnitudeM2, nuclearSlopeK2, phaseCoefficient }` (all numeric parameters finite or `null`) |
| `nonGravitationalModel` | object or `null` | Present when SBDB/Horizons declares non-gravitational parameters (`A1`, `A2`, `A3`, `DT`, `S0` with units) |
| `notValidBeforeUtc` | UTC ISO-8601 string or `null` | Source-declared earliest validity instant from SBDB (`not_valid_before`), else `null` |
| `notValidAfterUtc` | UTC ISO-8601 string or `null` | Source-declared latest validity instant from SBDB (`not_valid_after`), else `null` |
| `planetaryEphemeris` | string (e.g. `"DE441"`) | Planetary ephemeris model reported by Horizons header |
| `smallBodyPerturbers` | string or `null` | Perturber model reported by Horizons header (e.g. `"SB441-N16"`) |
| `horizonsEopFile` | string or `null` | JPL EOP file name reported by Horizons header (e.g. `"eop.261126.p270222"`) |
| `horizonsEopCoverage` | object or `null` | Structured Horizons EOP coverage parsed from the Horizons `EOP coverage : DATA-BASED <start> TO <end>. PREDICTS-> <end>` header (`additionalProperties: false`): `{ dataBasedStartDate, dataBasedEndDate, predictsEndDate }` (each ISO calendar date `YYYY-MM-DD` or `null`, e.g., `dataBasedStartDate: "1962-01-20"`, `dataBasedEndDate: "2026-11-26"`, `predictsEndDate: "2027-02-21"`). `null` only when the Horizons header omits `EOP coverage` |

**Mandatory Horizons ↔ SBDB solution-metadata consistency check:** Because
Horizons `OBSERVER` ephemeris tables and SBDB orbit metadata come from two
separate endpoints, a JPL orbit update can land between the two requests. Before
pairing SBDB metadata with a Horizons `OBSERVER` position series, the normalizer
MUST assert **all three**:

1. Canonical `spkId` matches;
2. Horizons `Target body name` header `{source: <AUTHORITY>#<ID>}` (e.g.,
   `JPL#659`, `JPL#75`) matches `<sbdb.orbit.source>#<sbdb.orbit.orbit_id>`
   exactly (case-insensitive authority normalization to uppercase); and
3. Horizons `OBJ_DATA='YES'` header `Soln.date:` (e.g., `2021-May-24_17:55:05`),
   normalized to `"YYYY-MM-DD HH:MM:SS"`, matches SBDB `orbit.soln_date`
   (`"2021-05-24 17:55:05"`).

Any mismatch **fails closed** with `SolutionMetadataMismatch` for that object
(quarantining the inconsistent candidate pair and applying the partial-refresh
carry-forward rule in Section 5 rather than attaching mismatched SBDB metadata
to a Horizons position series).

## 3. Bounded Ephemeris Snapshot Schema (`hvo-small-body-ephemeris-v1`)

All objects in `hvo-small-body-ephemeris-v1` and `hvo-small-body-manifest-v1`
enforce strict JSON schema semantics (`additionalProperties: false`).

### 3.1 Observer Binding

Every snapshot is bound to one terrestrial observer location:

```json
{
  "deploymentLocationId": "loc-hvo-primary",
  "deploymentLocationVersion": 3,
  "elevationMeters": 1000,
  "heightDatum": "AssumedEllipsoidal",
  "latitudeDegrees": 35.5599378,
  "longitudeDegrees": -113.9119818,
  "observerBindingSha256": "08A23071971928E2783C214B9EC5FBF307D87219E4E37C392C6660B52DD3E8B5"
}
```

- `observerBindingSha256` hashes canonical observer JSON (excluding
  `observerBindingSha256`: WGS84 geodetic latitude, east-positive longitude,
  elevation in meters, height datum, and optional `deploymentLocationId` +
  `deploymentLocationVersion`).
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
- `recordSha256`: 64-char uppercase SHA-256 of canonical series JSON excluding
  `recordSha256`, `seriesRetrievedUtc`, `carriedForward`, and
  `originManifestSha256` (so per-series producing provenance — `parserVersion`,
  `normalizerVersion`, `horizonsApiVersion`, `sbdbApiVersion`,
  `lookupApiVersion` — is bound into `recordSha256` and content identity stays
  100% invariant across carry-forward, matching `orbital-elements-v1`)
- `seriesRetrievedUtc`: UTC ISO-8601 instant when **this specific series** was
  retrieved from JPL (preserved verbatim when carried forward across a partial
  refresh failure so staleness is never masked)
- `carriedForward`: boolean (`false` when freshly fetched in this manifest;
  `true` when carried forward verbatim from the prior active manifest for the
  same `observerBindingSha256`)
- `originManifestSha256`: 64-char uppercase hex string or `null` (`null` iff
  `carriedForward == false`, since the enclosing manifest is its origin;
  **required non-null** when `carriedForward == true`, pointing to the exact
  immutable `manifestSha256` of the originating manifest where this series was
  first produced, and preserved unchanged across any subsequent multi-hop
  carry-forwards)
- `parserVersion`, `normalizerVersion`, `horizonsApiVersion`, `sbdbApiVersion`,
  `lookupApiVersion`: exact producing parser, normalizer, and JPL API signature
  versions that built **this specific series** (bound inside `recordSha256` and
  preserved verbatim on carry-forward — never overwritten by a newer manifest's
  tool versions)
- `ephemerisProfile`: `"jpl-horizons-observer-airless-v1"`
- `center`: `"coord@399"` (ITRF93/WGS84 topocentric)
- `refractionApplied`: `false` (always `AIRLESS` in stored knots)
- `stepSeconds`: uniform positive integer step between consecutive knots in
  **`{3600, 900, 300, 120, 60}`** (selected by the combined distance-tier and
  motion-sensitive rule in Section 4.2)
- `validFromUtc` / `validUntilUtc`: inclusive usable UTC evaluation span
- `paddedStartUtc` / `paddedEndUtc`: exact first and last knot timestamps,
  strictly equal to `validFromUtc - stepSeconds` and `validUntilUtc + stepSeconds`
  (guaranteeing a centered 4-point cubic stencil everywhere inside
  `[validFromUtc, validUntilUtc]`)
- `minimumRangeAu`: minimum topocentric range $\Delta_{\min}$ across knots
- `interpolationVerificationMethod`: enum (`"HalfStepSample"`,
  `"AnalyticalDerivativeBound"`, `"Unverified"`) recording the midpoint
  verification evidence backing this series (Section 4.2)
- `maxVerifiedMidpointResidualArcsec`: non-negative finite number or `null`
  (`null` iff `interpolationVerificationMethod == "Unverified"`); records the
  maximum verified midpoint angular residual (or rigorous analytical remainder
  bound) across all usable intervals in arcseconds, covering both `Q=1`
  astrometric and `Q=2` apparent directions and **never understating** the
  measured/bounded residual
- `interpolationBoundVerified`: boolean (`true` **iff**
  `interpolationVerificationMethod != "Unverified"`,
  `maxVerifiedMidpointResidualArcsec != null`, and every usable interval in the
  series has verified midpoint accuracy $\le 1.0''$; `false` otherwise, failing
  closed with `InterpolationBoundExceeded`)
- `knots`: strictly ascending uniform UTC array (`count >= 4`, `count <= 1024`)

Each `SmallBodyEphemerisKnot` contains (aligned identically with
[`sky-layers-source-decision-1396.md`](../planning/sky-layers-source-decision-1396.md)):

| Field | Units / Type | Source (`Horizons OBSERVER`) | Semantics |
| --- | --- | --- | --- |
| `utc` | UTC ISO-8601 | `Date__(UT)__HR:MN` (`TIME_TYPE='UT'`, `CAL_TYPE='GREGORIAN'`) | Exact knot UTC instant |
| `astrometricRaDeg` | degrees `[0, 360)` | `QUANTITIES=1` (`R.A.___(ICRF)`) | Topocentric light-time-corrected ICRF RA (no aberration/deflection/refraction) |
| `astrometricDecDeg` | degrees `[-90, 90]` | `QUANTITIES=1` (`DEC____(ICRF)`) | Topocentric light-time-corrected ICRF Dec |
| `apparentRaDeg` | degrees `[0, 360)` | `QUANTITIES=2` (`R.A.__(a-app)`) | Topocentric apparent RA of true equator & equinox of date, **airless** |
| `apparentDecDeg` | degrees `[-90, 90]` | `QUANTITIES=2` (`DEC___(a-app)`) | Topocentric apparent Dec of true equator & equinox of date, **airless** |
| `predictedTotalMagnitude` | finite number or `null` | `QUANTITIES=9` (`APmag` for asteroids, `T-mag` for comets) | Uncertain predicted visual/total magnitude (`null` when `n.a.`) |
| `predictedNuclearOrSurface` | finite number or `null` | `QUANTITIES=9` (`S-brt` for asteroids, `N-mag` for comets) | Surface brightness or nuclear magnitude (`null` when `n.a.`) |
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
  `lookupApiVersion`, `parserVersion`, `normalizerVersion` (recording the active
  refresh run's tool/API versions that built this manifest and match every
  `carriedForward: false` series; carried-forward series retain their own
  authoritative per-series producing provenance and `originManifestSha256`)
- `termsReference`: `"https://ssd-api.jpl.nasa.gov/doc/"` and exact
  `attributionText` (`NASA/JPL-Caltech Solar System Dynamics...`)
- `observerBinding`: Section 3.1 object
- `retrievedUtc`: UTC ISO-8601 instant of the refresh run that built this manifest
- `watchlistCount`, `refreshedCount` (must be $\ge 1$ to activate a new manifest),
  `carriedForwardCount` (count of watchlist bodies carried forward verbatim from
  the prior active manifest for the same `observerBindingSha256`), and
  `failedRefreshObjects[]` (array of `{ spkId, reasonCode, carryForwardApplied }`,
  recording per-body refresh failures without dropping carried-forward bodies)
- `overallValidFromUtc`, `overallValidUntilUtc`
- `rawSources[]`: sanitized per-object source descriptor (`spkId`, sanitized
  `querySummary` without secrets, `rawSha256`, `rawBytes`, `httpStatus`)

**Hard validation, canonical serialization, & memory ceilings (Proposed defaults):**
- Maximum watched bodies per snapshot: **32**
- Maximum knots per body: **1,024**
- Maximum raw Horizons/SBDB wire response per request: **2 MiB** (decoded **4 MiB**)
- Maximum normalized snapshot JSON payload: **16 MiB**
- **Exact canonical JSON serialization rule (matching `.NET System.Text.Json` /
  `CaptureContractJson` / `OrbitalCanonicalJson` and RFC 8785 JCS):**
  - Compact UTF-8 JSON (no insignificant whitespace), object properties ordered
    by `StringComparer.Ordinal`, uppercase 64-hex SHA-256 digests;
  - **Canonical number encoding:** finite IEEE 754 numbers serialized in shortest
    round-trip decimal form with `-0.0` normalized to `0` and **integer-valued
    finite numbers serialized WITHOUT a trailing `.0`** (for example, `1000`
    rather than `1000.0`, matching `.NET System.Text.Json` and
    `tests/fixtures/orbital/contract-v1/pass.json`);
  - UTC ISO-8601 `"Z"` timestamps (`yyyy-MM-ddTHH:mm:ssZ`, trimming trailing
    fractional zeros only when sub-second);
  - Reject `NaN`/`Infinity`, duplicate keys, unknown fields
    (`additionalProperties: false`), or nesting $> 64$ levels.

### 3.4 Canonical JSON Examples (`433 Eros`, Schema Verification Example)

The following normalized schema-verification examples use numerical values
observed during a local 2026-11-26/27 JPL Horizons `OBSERVER` + SBDB/Lookup
probe for `433 Eros` (`spkid="20000433"`, `object.des="433"`, Lookup
`pdes="A898 PA"` preserved in `aliases[]`, `JPL#659`,
`soln_date="2021-05-24 17:55:05"` `America/Los_Angeles` → `2021-05-25T00:55:05Z`,
`EOP file: eop.261126.p270222`, `EOP coverage: DATA-BASED 1962-JAN-20 TO
2026-NOV-26. PREDICTS-> 2027-FEB-21`). No verbatim upstream Horizons header
text or raw wire payload is committed here; repository-committed Slice S0
fixtures obey Gate `S0-Clearance` in
[`sky-layers-source-decision-1396.md`](../planning/sky-layers-source-decision-1396.md).
The recorded midpoint residual `0.000238"` (`HalfStepSample`) reflects the
exact measured cubic unit-vector residual against the independent Horizons
`2026-11-27 00:30 UTC` sample (`0.000237907"` astrometric ICRF, `0.000222838"`
apparent airless). All SHA-256 hashes and byte lengths are computed under the
exact Section 3.3 `.NET System.Text.Json` / RFC 8785 JCS canonical serializer
(without trailing `.0` on integer-valued numbers).

**Normalized payload (`hvo-small-body-ephemeris-v1`):**

```json
{
  "observerBinding": {
    "deploymentLocationId": "loc-hvo-primary",
    "deploymentLocationVersion": 3,
    "elevationMeters": 1000,
    "heightDatum": "AssumedEllipsoidal",
    "latitudeDegrees": 35.5599378,
    "longitudeDegrees": -113.9119818,
    "observerBindingSha256": "08A23071971928E2783C214B9EC5FBF307D87219E4E37C392C6660B52DD3E8B5"
  },
  "schemaVersion": "hvo-small-body-ephemeris-v1",
  "series": [
    {
      "carriedForward": false,
      "center": "coord@399",
      "ephemerisProfile": "jpl-horizons-observer-airless-v1",
      "horizonsApiVersion": "1.2",
      "identity": {
        "aliases": ["1956 PC", "2000433", "A898 PA", "I98P00A"],
        "authority": "jpl-spk",
        "bodyKind": "Asteroid",
        "fragmentCode": null,
        "fullName": "433 Eros (A898 PA)",
        "isNeo": true,
        "isPha": false,
        "mpcPackedDesignation": null,
        "orbitClassCode": "AMO",
        "primaryDesignation": "433",
        "shortName": "433 Eros",
        "spkId": "20000433"
      },
      "interpolationBoundVerified": true,
      "interpolationVerificationMethod": "HalfStepSample",
      "knots": [
        {
          "apparentDecDeg": -25.355799762,
          "apparentRaDeg": 274.162586305,
          "astrometricDecDeg": -25.364016581,
          "astrometricRaDeg": 273.749854519,
          "dec3SigmaArcsec": 0.006,
          "phaseAngleDeg": 15.7692,
          "predictedNuclearOrSurface": 4.255,
          "predictedTotalMagnitude": 14.242,
          "ra3SigmaArcsec": 0.009,
          "rangeAu": 2.56309135056611,
          "rangeRateKmPerSec": 10.606327,
          "solarElongationDeg": 29.1254,
          "solarElongationSide": "Trailing",
          "utc": "2026-11-26T23:00:00Z"
        },
        {
          "apparentDecDeg": -25.352960347,
          "apparentRaDeg": 274.189520636,
          "astrometricDecDeg": -25.361240234,
          "astrometricRaDeg": 273.77680674,
          "dec3SigmaArcsec": 0.006,
          "phaseAngleDeg": 15.76,
          "predictedNuclearOrSurface": 4.105,
          "predictedTotalMagnitude": 14.242,
          "ra3SigmaArcsec": 0.009,
          "rangeAu": 2.5633474712505,
          "rangeRateKmPerSec": 10.6775971,
          "solarElongationDeg": 29.1079,
          "solarElongationSide": "Trailing",
          "utc": "2026-11-27T00:00:00Z"
        },
        {
          "apparentDecDeg": -25.350097477,
          "apparentRaDeg": 274.216485288,
          "astrometricDecDeg": -25.358441807,
          "astrometricRaDeg": 273.803793706,
          "dec3SigmaArcsec": 0.006,
          "phaseAngleDeg": 15.7508,
          "predictedNuclearOrSurface": 4.315,
          "predictedTotalMagnitude": 14.242,
          "ra3SigmaArcsec": 0.009,
          "rangeAu": 2.5636051446178,
          "rangeRateKmPerSec": 10.7345627,
          "solarElongationDeg": 29.0903,
          "solarElongationSide": "Trailing",
          "utc": "2026-11-27T01:00:00Z"
        },
        {
          "apparentDecDeg": -25.347214993,
          "apparentRaDeg": 274.243492038,
          "astrometricDecDeg": -25.355625554,
          "astrometricRaDeg": 273.830826161,
          "dec3SigmaArcsec": 0.006,
          "phaseAngleDeg": 15.7416,
          "predictedNuclearOrSurface": 4.138,
          "predictedTotalMagnitude": 14.242,
          "ra3SigmaArcsec": 0.009,
          "rangeAu": 2.56386397255976,
          "rangeRateKmPerSec": 10.7729359,
          "solarElongationDeg": 29.0726,
          "solarElongationSide": "Trailing",
          "utc": "2026-11-27T02:00:00Z"
        }
      ],
      "lookupApiVersion": "1.1",
      "maxVerifiedMidpointResidualArcsec": 0.000238,
      "minimumRangeAu": 2.56309135056611,
      "normalizerVersion": "small-body-normalizer-v1",
      "originManifestSha256": null,
      "paddedEndUtc": "2026-11-27T02:00:00Z",
      "paddedStartUtc": "2026-11-26T23:00:00Z",
      "parserVersion": "jpl-horizons-csv-v1",
      "recordSha256": "E536448B4F827DB627B9E7E4613357BF8AB57CABBF0716B76C107087C12D3377",
      "refractionApplied": false,
      "sbdbApiVersion": "1.3",
      "seriesRetrievedUtc": "2026-11-26T23:47:29Z",
      "solution": {
        "conditionCode": 0,
        "dataArcDays": 46582,
        "epochTdbJd": 2461200.5,
        "firstObsDateRaw": "1893-10-29",
        "hasCovariance": true,
        "horizonsEopCoverage": {
          "dataBasedEndDate": "2026-11-26",
          "dataBasedStartDate": "1962-01-20",
          "predictsEndDate": "2027-02-21"
        },
        "horizonsEopFile": "eop.261126.p270222",
        "horizonsRecordNumber": "433",
        "lastObsDateRaw": "2021-05-13",
        "magnitudeModel": {
          "absoluteMagnitudeH": 10.4,
          "kind": "AsteroidHG",
          "nuclearMagnitudeM2": null,
          "nuclearSlopeK2": null,
          "phaseCoefficient": null,
          "slopeParameterG": 0.46,
          "totalMagnitudeM1": null,
          "totalSlopeK1": null
        },
        "nonGravitationalModel": null,
        "notValidAfterUtc": null,
        "notValidBeforeUtc": null,
        "observationCount": 9130,
        "orbitId": "JPL#659",
        "planetaryEphemeris": "DE441",
        "residualRmsArcsec": 0.3,
        "smallBodyPerturbers": "SB441-N16",
        "solutionAuthority": "JPL",
        "solutionDateSourceRaw": "2021-05-24 17:55:05",
        "solutionDateSourceTimeZone": "America/Los_Angeles",
        "solutionDateUtc": "2021-05-25T00:55:05Z"
      },
      "stepSeconds": 3600,
      "validFromUtc": "2026-11-27T00:00:00Z",
      "validUntilUtc": "2026-11-27T01:00:00Z"
    }
  ]
}
```

**Matching manifest (`hvo-small-body-manifest-v1`):**

```json
{
  "attributionText": "Small-body ephemerides and orbit metadata: NASA/JPL-Caltech Solar System Dynamics (Horizons / Small-Body Database). Displayed by HVO SkyMonitor; not endorsed by NASA, JPL, or Caltech.",
  "carriedForwardCount": 0,
  "failedRefreshObjects": [],
  "horizonsApiVersion": "1.2",
  "lookupApiVersion": "1.1",
  "manifestSha256": "0F6ACCBA929C3245602B46B74362F9D6BB28C10EEFAAB971D3FFE02EF744728B",
  "normalizedPayloadBytes": 4039,
  "normalizedPayloadSha256": "0F4E39DB0A64DE1ACB764FA54E240522EDACC65D2FD41ECF4B38D6FDA85C68F7",
  "normalizerVersion": "small-body-normalizer-v1",
  "observerBinding": {
    "deploymentLocationId": "loc-hvo-primary",
    "deploymentLocationVersion": 3,
    "elevationMeters": 1000,
    "heightDatum": "AssumedEllipsoidal",
    "latitudeDegrees": 35.5599378,
    "longitudeDegrees": -113.9119818,
    "observerBindingSha256": "08A23071971928E2783C214B9EC5FBF307D87219E4E37C392C6660B52DD3E8B5"
  },
  "overallValidFromUtc": "2026-11-27T00:00:00Z",
  "overallValidUntilUtc": "2026-11-27T01:00:00Z",
  "parserVersion": "jpl-horizons-csv-v1",
  "provider": "nasa-jpl-ssd",
  "rawSources": [
    {
      "httpStatus": 200,
      "querySummary": "horizons.api OBSERVER DES=20000433; coord@399 AIRLESS Q=1,2,9,20,23,24,36 step=3600s",
      "rawBytes": 14239,
      "rawSha256": "328B71E9AC42D134E0AB6B0086E868586885443878195B08046636403560965A",
      "spkId": "20000433"
    }
  ],
  "refreshedCount": 1,
  "retrievedUtc": "2026-11-26T23:47:29Z",
  "sbdbApiVersion": "1.3",
  "schemaVersion": "hvo-small-body-manifest-v1",
  "termsReference": "https://ssd-api.jpl.nasa.gov/doc/",
  "watchlistCount": 1
}
```

## 4. Reader, Interpolation, Evaluation, and Replay Semantics

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
  monotonicity, uniform `stepSeconds ∈ {3600, 900, 300, 120, 60}`, finite
  numbers, and defensive immutability once outside the frame path.

### 4.2 Deterministic Motion-Sensitive Interpolation and Horizontal/Measured Projection

1. **Domain & verification check:** For query instant `utc`:
   - Reject if `utc < series.validFromUtc || utc > series.validUntilUtc` →
     `OutOfRange` / `Expired` (no extrapolation).
   - Reject if SBDB `notValidBeforeUtc`/`notValidAfterUtc` excludes `utc` →
     `OutsideSourceValidity`.
   - Reject if `observer` does not match `snapshot.observerBinding` →
     `ObserverMismatch`.
   - Reject if `utc` crosses an unsupported leap second → `UnsupportedLeapCrossing`.
   - Reject if `!series.interpolationBoundVerified`,
     `series.interpolationVerificationMethod == "Unverified"`,
     `series.maxVerifiedMidpointResidualArcsec == null`, or the bracketing
     interval lacks verified midpoint accuracy $\le 1.0''$ →
     `InterpolationBoundExceeded`.
2. **Motion-sensitive step selection & mandatory independent half-step midpoint verification:**
   - Acquisition selects the largest supported uniform step
     `stepSeconds ∈ {3600, 900, 300, 120, 60}` that satisfies **both**:
     1. **Distance-tier ceiling** (diurnal parallax): $\Delta_{\min} \ge 0.2\text{ AU} \to \le 3600\text{ s}$; $0.01 \le \Delta_{\min} < 0.2\text{ AU} \to \le 900\text{ s}$; $0.002 \le \Delta_{\min} < 0.01\text{ AU} \to \le 300\text{ s}$; $\Delta_{\min} < 0.002\text{ AU} \to$ `CloseApproachUnsupported`.
     2. **Motion-sensitive angular-step cap:** $\omega_{\max} \cdot h \le 2.5^\circ$ ($0.04363\text{ rad}$), where $\omega_{\max}$ is the maximum apparent angular speed (estimated from $v_{\perp,\max}/\Delta_{\min}$ or observed $\max_k \angle(\hat{\mathbf{u}}_{k+1}, \hat{\mathbf{u}}_k)/h$). Window duration is reduced when needed so stored knot count stays $\le 1024$ (e.g., 24 h at `120 s` = 723 knots, 12 h at `60 s` = 723 knots).
   - **Mandatory independent midpoint verification (integer-knot-only checks prohibited as proof):**
     Integer-knot-only `2h`-stride leave-one-out checks are **NOT sufficient**
     to verify midpoint accuracy: for example, on a 7-knot series with an
     oscillatory perturbation $2'' \sin(\pi t / h)$, the curve passes the
     $\omega_{\max} h \le 2.5^\circ$ cap and evaluates to `0.000000"` residual
     at every integer knot (including `k = 3`), yet has a **`2.000000"` error**
     at every usable midpoint $t_{k+1/2} = t_k + h/2$. Therefore, every usable
     interval $[t_k, t_{k+1}]$ ($k \in [1, N-3]$) MUST be verified $\le 1.0''$
     using one of two approved methods:
     - **Method A (`HalfStepSample` — required for Horizons acquisition and live/cached verification):**
       Fetch the Horizons `OBSERVER` ephemeris at half-step resolution $h/2$
       ($h/2 \in \{1800, 450, 150, 60, 30\}\text{ s}$ — returned in a single
       Horizons HTTP request, keeping even-indexed samples as the retained
       $h$-spaced knots and odd-indexed samples as independent half-step
       verification points). For every usable interval $k \in [1, N-3]$,
       evaluate the 4-point centered cubic Lagrange unit-vector interpolant on
       $\{k-1, k, k+1, k+2\}$ at $u = 0.5$ ($t_{k+1/2}$) and measure
       $e_{k+1/2} = \max\bigl(\angle(\hat{\mathbf{u}}_{\text{astrom}}^{\text{interp}}, \hat{\mathbf{u}}_{\text{astrom}}^{\text{ref}}),\; \angle(\hat{\mathbf{u}}_{\text{app}}^{\text{interp}}, \hat{\mathbf{u}}_{\text{app}}^{\text{ref}})\bigr)$.
       Record `interpolationVerificationMethod = "HalfStepSample"` and
       `maxVerifiedMidpointResidualArcsec = max_k e_{k+1/2}` (rounded upward if
       formatted, never understated).
     - **Method B (`AnalyticalDerivativeBound` — allowed only for closed-form analytical/synthetic fixtures):**
       Prove a rigorous conservative upper bound on the cubic midpoint remainder
       $\frac{3}{128} h^4 \sup_{\xi \in [t_{k-1}, t_{k+2}]} \|\mathbf{u}^{(4)}(\xi)\| \le 1.0''$
       (plus spherical re-normalization bound) over every usable interval, and
       record `interpolationVerificationMethod = "AnalyticalDerivativeBound"`
       with that conservative bound in `maxVerifiedMidpointResidualArcsec`.
     - **Fail-closed withholding rule:** If any usable interval has
       $e_{k+1/2} > 1.0''$, acquisition refines to the next smaller
       `stepSeconds` (down to `60 s`) and re-verifies. Any usable interval that
       **lacks** verified midpoint accuracy evidence (`Unverified`) or still
       exceeds `1.0"` at `60 s` MUST be withheld (`interpolationBoundVerified = false`,
       failing closed with `InterpolationBoundExceeded`).
   - **Verified benchmarks (mandatory Slice S0/S1 tests):**
     - *Rectilinear fast flyby ($\Delta_{\min}=0.002\text{ AU}$, $v_\perp=75\text{ km/s}$):*
       At `h = 300 s`, half-step midpoint max error is `1.115471"` (continuous
       max `1.115644"` — rejected as `> 1.0"`). Under $\omega_{\max} h \le 2.5^\circ$
       ($\omega_{\max}=51.7048''/\text{s} \implies h \le 174.1\text{ s}$), the
       contract selects **`h = 120 s`**, yielding **`0.028812"`** half-step
       midpoint max error (continuous max `0.028818"`), well under `1.0"`.
     - *Oscillatory counterexample ($2'' \sin(\pi t/h)$ on 7 knots):*
       Integer-knot `2h`-stride at `k = 3` reports `0.000000"`, whereas Method A
       half-step verification at $t = 1.5h$ measures **`2.000000"`** and
       rejects/withholds the interval.
     - *`433 Eros` Section 3.4 example (1 h knots vs independent Horizons
       `2026-11-27 00:30 UTC` sample):* **`0.000237907"`** astrometric ICRF and
       **`0.000222838"`** apparent airless → recorded conservatively as
       **`0.000238"`**.
3. **4-point centered cubic Lagrange on unit vectors:**
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
     finite) `predictedTotalMagnitude` / `predictedNuclearOrSurface`. For
     `solarElongationSide`, preserve the common value when both bracketing knots
     agree, or emit `"Unknown"` if the bracketing interval crosses opposition or
     conjunction (`/L` ↔ `/T` transition). For 3-sigma uncertainties, take the
     conservative envelope $\max(\text{linear interpolation}, \text{adjacent knot max})$
     when finite, or `null` (`UncertaintyUnavailable`) if either bracketing knot
     is `null`.
4. **Horizontal & image projection (Configured-Rig and `#523` Measured Basis):**
   - Convert interpolated **apparent true-of-date** $(\alpha_{\text{app}}(t),
     \delta_{\text{app}}(t))$ to **geometric unrefracted `AltAzPoint`** using:
     - **Primary (`eop-gast-polar-motion-v1`):** resolved `OrbitalTimeData`
       (UT1 via DUT1, IAU equation of equinoxes for GAST, polar motion
       $(x_p, y_p)$), recording `earthOrientationSha256`, `leapSecondsSha256`,
       `timeDataBundleId`, and `Final`/`Predicted`/`Mixed` status.
     - **Planning-only degraded fallback (`ut1-approximated-by-utc-v1`):** when
       EOP does not cover `utc` and caller requests `SmallBodyPredictionUse.Planning`,
       compute GMST/GAST using UTC≈UT1 with explicit degraded reason code and
       `timingBasisUncertaintyArcsec = 15.0`. **`MeasuredAssociation` rejects
       this fallback.**
   - Evaluate `#1394` `HorizonObstructionEvaluator` on the **unrefracted
     geometric `AltAzPoint`** (if an active `HorizonObstructionProfile` exists).
   - Apply `AtmosphericRefraction.Apply` (`bennett-1982-standard-atmosphere-v1`)
     **only** when the caller explicitly requests refracted display coordinates.
   - **Image projection interface (explicitly NOT `AstrometricMapping.SkyToPixel`):**
     - **Configured-rig baseline:** build `ProjectionContext` via
       `RigProjectionContextFactory` and project the computed horizontal
       `AltAzPoint` via `ProjectorFactory.Create(projection).Project(horizontal)`.
     - **Accepted `#523` measured calibration basis:** **never** pass an
       EOP-corrected apparent direction through `AstrometricMapping.SkyToPixel`
       (which expects mean-of-date `EquatorialPoint`, runs its own non-EOP
       `CoordinateTransforms.EquatorialToHorizontal`, and whose
       `AstrometricFrameAssessment` carries no EOP identity). Instead, follow
       the exact `OrbitalGeometryFacts.ValidateMeasuredEvidence` pattern
       (`src/HVO.SkyMonitor.Astronomy/Orbital/OrbitalPredictionFacts.cs`):
       1. Validate the accepted `AstrometricCalibration` +
          `AstrometricFrameAssessment` (`HasMeasuredMapping`, matching
          `CalibrationIdentitySha256`, `ReadoutIdentitySha256`,
          `AssessmentIdentitySha256`, observer, and exposure instant);
       2. Obtain the measured `ProjectionContext` via
          **`AstrometricMapping.Projection(calibration, assessment)`**;
       3. Project the EOP-computed horizontal `AltAzPoint` directly via
          **`ProjectorFactory.Create(measuredProjection).Project(horizontal)`**;
       4. Apply `ProjectedSceneImageTransformV1` at most once (only when
          projecting in `NativeSensor` coordinates; never double-applied on
          `ReadoutView`); and
       5. Validate the prediction's non-zero EOP provenance
          (`earthOrientationSha256`, `leapSecondsSha256`, `Final`/`Predicted`/`Mixed`,
          rejecting `ut1-approximated-by-utc-v1`) **separately** in the
          small-body prediction model facts.
5. **Short-path sampling (Observatory & optional layer):**
   - Bounded short path over `[startUtc, endUtc]` (capped at $\le 72\text{ h}$
     and $\le 256$ samples per body per query, $\le 32$ bodies).
   - Samples are split into connected visible segments at geometric horizon,
     `#1394` site-horizon obstructions/unknown terrain, and sensor/image-circle
     clipping boundaries; displays **never** bridge across clipped/obstructed
     gaps.

### 4.3 Label Withholding and Distinction of Evidence

Every evaluated `SmallBodyPositionProduct` carries:
- `geometricDirection`: unrefracted `AltAzPoint` (plus optional refracted `AltAzPoint` and `PixelPoint`)
- `provenance`: `manifestSha256`, `recordSha256`, `observerBindingSha256`,
  `earthOrientationSha256` (nullable only in Planning degraded mode),
  `leapSecondsSha256`, `timeDataBundleId`, `earthOrientationStatus`
- `uncertainty`: `ra3SigmaArcsec`, `dec3SigmaArcsec`, `conditionCode`,
  `uncertaintyStatus` (`Formal3SigmaAvailable`, `Unavailable`, `LargeUncertainty`)
- `photometry`: `predictedTotalMagnitude`, `predictedNuclearOrSurface`,
  `magnitudeModel`, `photometryQualification = "UncertainSourcePredictionNotDetection"`
- `freshness`: `Fresh`, `Stale`, `Expired`, `OutOfRange`, `OutsideSourceValidity`
- `labelDisposition`: `Eligible`, `EligibleWithCaveat`, or `Withheld` (with
  explicit reason: `LargeSkyUncertainty`, `StaleSnapshot`, `HorizonObstructed`,
  `BelowHorizon`, `OutsideSensorBounds`, `InterpolationBoundExceeded`)
- **No `Visible` or `Detected` boolean**, no coma/tail geometry, and no claim of
  image-based identification.

### 4.4 Exact-Manifest Replay and Time-Data Pinning

Because CameraAgent's managed time-data store retains only the active EOP/leap
pair plus one intact predecessor (`docs/astronomy/bundled-time-data-v1.md`), an
archived small-body snapshot manifest can outlive the exact EOP/leap bytes used
at capture time. Replay semantics are therefore strictly defined as follows:

1. **Historical record self-sufficiency:** Every persisted evaluation or
   presentation artifact (`SmallBodyPositionProduct` / `PredictedSmallBodyLayer`)
   pins the exact `(manifestSha256, recordSha256, earthOrientationSha256,
   leapSecondsSha256, timeDataBundleId, earthOrientationStatus)` tuple **AND**
   retains the exact evaluated horizontal outputs (`geometricDirection`,
   optional `apparentDirection` and `pixel`). Inspecting historical records
   reads those retained outputs directly without requiring pruned time-data bytes.
2. **Exact recomputation fails closed when pinned time-data is pruned:** When a
   caller requests **exact recomputation** of a historical artifact against its
   pinned `(manifestSha256, earthOrientationSha256, leapSecondsSha256)`:
   - The reader loads `manifestSha256` via `ReadExactAsync` (never substituting
     the current snapshot); and
   - The time-data resolver locates the exact `earthOrientationSha256` and
     `leapSecondsSha256` bytes across bundled, managed current/predecessor, or
     configured override/archive sources.
   - If the pinned EOP or leap-second bytes are no longer present on disk,
     recomputation **fails closed** with
     **`SmallBodyEvaluationUnavailableReason.TimeDataUnavailable`** — it
     **never** silently substitutes a newer EOP pair and **never** silently
     falls back to `ut1-approximated-by-utc-v1`.
3. **Explicit re-evaluation with current time-data:** An operator may separately
   invoke an explicit *"Re-evaluate with current time-data"* action that
   produces a **new** derived record referencing the archived `manifestSha256`
   paired with the new active `(earthOrientationSha256, leapSecondsSha256)`
   hashes, clearly marked as a new evaluation rather than an exact replay.

## 5. Operational Refresh Policy, Partial-Refresh Carry-Forward, and Read Model (`hvo-small-body-refresh-status-v1`)

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
- `perObjectSummary[]`: `spkId`, `displayName`, `orbitId`,
  `seriesRetrievedUtc`, `carriedForward`, `validUntilUtc`, `freshness`,
  `lastErrorReason` (no paths, no credentials, no raw provider bodies)
- `reason` and `recoveryAction`

**Unambiguous partial-refresh & last-known-good activation rule (Per-Body Carry-Forward):**
1. **Partial success (`refreshedCount >= 1`, `failedRefreshObjects.Count >= 1`):**
   The refresher constructs and atomically activates a new manifest containing:
   - Every freshly fetched, verified, solution-matched body (`carriedForward: false`,
     new `seriesRetrievedUtc`); **and**
   - For each failed body, its **last-known-good `SmallBodyEphemerisSeries`**
     from the prior active manifest (when one exists for the **exact same**
     `observerBindingSha256`), carried forward verbatim with
     `carriedForward: true`, **`originManifestSha256`** pinned to the immutable
     originating manifest's `manifestSha256` (preserved unchanged across
     multi-hop carry-forwards), and its **original** producing tool/API
     provenance (`parserVersion`, `normalizerVersion`, `horizonsApiVersion`,
     `sbdbApiVersion`, `lookupApiVersion`), `seriesRetrievedUtc`,
     `validFromUtc`, `validUntilUtc`, `solution`, and unchanged `recordSha256`
     (so neither staleness/expiry nor producing parser/normalizer provenance is
     ever masked — for example, a v2 refresh that succeeds for Eros and carries
     Halley forward from a v1 manifest `M1` records v2 on the new manifest and
     Eros while preserving v1 provenance and `originManifestSha256 = M1` on
     Halley), while recording the failure in `failedRefreshObjects[]` with
     `carryForwardApplied: true`. If the failed body has no prior valid series
     for the current observer, it is omitted from `series[]` and recorded in
     `failedRefreshObjects[]` with `carryForwardApplied: false`.
2. **Total failure, halt, or quarantine (`refreshedCount == 0`):**
   **No new manifest is activated.** The existing last-known-good manifest
   remains active and readable untouched while `lastAttemptUtc`, `providerHalt`,
   `candidateQuarantine`, and `perObjectSummary[].lastErrorReason` record the
   failure.

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
- Last-known-good data is never erased by halts, transport failures, partial
  per-body failures (via carry-forward), or quarantined candidates.
