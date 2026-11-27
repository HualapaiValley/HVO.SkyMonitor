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
| `orbitId` | string (1–32 chars) | Canonical compound solution ID `"<solutionAuthority>#<sbdb.orbit.orbit_id>"` (e.g. `"JPL#1"`), matching the Horizons header `{source: JPL#<id>}` token |
| `horizonsRecordNumber` | string or `null` | Horizons `Rec #:` identifier (e.g. `"999999"`, `"90000030"`) |
| `solutionDateSourceRaw` | string (`"YYYY-MM-DD HH:MM:SS"`) or `null` | Verbatim JPL `orbit.soln_date` (normalized from Horizons `YYYY-Mon-DD_HH:MM:SS` when compared), recorded in JPL Pasadena local time |
| `solutionDateSourceTimeZone` | string (`"America/Los_Angeles"`) or `null` | Explicit IANA timezone for `solutionDateSourceRaw` (`"America/Los_Angeles"` whenever `solutionDateSourceRaw` is non-null, else `null`) |
| `solutionDateUtc` | UTC ISO-8601 string or `null` | `solutionDateSourceRaw` deterministically converted from `America/Los_Angeles` (PST/PDT) to UTC (synthetic PDT example: `"2026-06-15 17:30:00"` PDT → `"2026-06-16T00:30:00Z"`). If an ambiguous DST fall-back local wall time ever occurs, `solutionDateUtc` MUST be set to `null` rather than guessing while `solutionDateSourceRaw` is preserved |
| `epochTdbJd` | finite number | Osculating orbit epoch in **TDB** Julian Date from matched SBDB `orbit.epoch`, explicitly labelled TDB and never treated as UTC (note: Horizons `OBJ_DATA` may report a different reference epoch for the same `JPL#<id>` solution) |
| `firstObsDateRaw` | string (`^\d{4}-(\d{2}\|\?\?)-(\d{2}\|\?\?)$`) or `null` | Unparsed partial-or-full date string verbatim from SBDB `orbit.first_obs` (e.g., `"2020-01-15"`, `"1919-??-??"`). **Must never be parsed with strict ISO date/timestamp parsers** |
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
| `horizonsEopFile` | string or `null` | JPL EOP file name reported by Horizons header (synthetic example: `"eop.260601.p260901"`) |
| `horizonsEopCoverage` | object or `null` | Structured Horizons EOP coverage parsed from the Horizons EOP coverage header (`additionalProperties: false`): `{ dataBasedStartDate, dataBasedEndDate, predictsEndDate }` (each ISO calendar date `YYYY-MM-DD` or `null`; synthetic example: `dataBasedStartDate: "1970-01-01"`, `dataBasedEndDate: "2026-06-01"`, `predictsEndDate: "2026-09-01"`). `null` only when the Horizons header omits EOP coverage |

**Mandatory Horizons ↔ SBDB solution-metadata consistency check:** Because
Horizons `OBSERVER` ephemeris tables and SBDB orbit metadata come from two
separate endpoints, a JPL orbit update can land between the two requests. Before
pairing SBDB metadata with a Horizons `OBSERVER` position series, the normalizer
MUST assert **all three**:

1. Canonical `spkId` matches;
2. Horizons `Target body name` header `{source: <AUTHORITY>#<ID>}` (e.g.,
   `JPL#<id>`) matches `<sbdb.orbit.source>#<sbdb.orbit.orbit_id>`
   exactly (case-insensitive authority normalization to uppercase); and
3. Horizons `OBJ_DATA='YES'` header `Soln.date:` (format `YYYY-Mon-DD_HH:MM:SS`,
   synthetic example `2026-Jun-15_17:30:00`), normalized to `"YYYY-MM-DD HH:MM:SS"`,
   matches SBDB `orbit.soln_date` (synthetic example `"2026-06-15 17:30:00"`).

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
| `utc` | UTC ISO-8601 (`YYYY-MM-DDTHH:MM:SSZ`) | Seconds-precision UTC time column (`TIME_TYPE='UT'`, `CAL_TYPE='GREGORIAN'`, `TIME_DIGITS='SECONDS'`) | Exact knot UTC instant (seconds precision ensures half-step verification at `150 s` and `30 s` resolves every sample without minute truncation). Because Horizons API v1.2 with `TIME_DIGITS='SECONDS'` and unitless `STEP_SIZE='<N>'` can emit fractional-second timestamp column/value forms (for example, an optional `.000` fractional-seconds suffix in CSV rows), the parser MUST accept both whole-second and fractional-second timestamp column/value forms, verify exact alignment to the requested whole-second UTC grid (rejecting non-zero sub-second drift beyond strict sub-millisecond tolerance rather than silently truncating), and normalize parsed timestamps to canonical ISO-8601 UTC (`YYYY-MM-DDTHH:MM:SSZ`) |
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
- **Sole normative canonical JSON byte encoding — Strict RFC 8785 (JCS):**
  All SHA-256 digests (`observerBindingSha256`, `recordSha256`,
  `normalizedPayloadSha256`, `manifestSha256`) and canonical payload byte
  lengths (`normalizedPayloadBytes`) MUST be computed over exact **RFC 8785
  (JSON Canonicalization Scheme, JCS)** UTF-8 bytes — **strict RFC 8785 (JCS)
  is the sole normative canonical encoding** for both this contract and
  [`meteor-shower-context-v1.md`](meteor-shower-context-v1.md):
  - **Compact UTF-8 & property order (RFC 8785 §3.2.1, §3.2.3):** Zero
    insignificant whitespace outside strings; object properties sorted
    lexicographically by UTF-16 code units (equivalent to C#
    `StringComparer.Ordinal`); SHA-256 digests formatted as 64 uppercase ASCII
    hex characters.
  - **Canonical number encoding (RFC 8785 §3.2.2.3 / ES2015 `Number::toString`):**
    Finite IEEE 754 binary64 numbers serialized in shortest round-trip decimal
    form with `-0.0` normalized to `0`:
    - Fixed-point decimal (no exponent) for $10^{-6} \le |x| < 10^{21}$, with
      **no trailing `.0`** on integer-valued numbers (e.g., `1000` / `1000.0` →
      `1000`, `0.000001` → `0.000001`, `0.000031` → `0.000031`);
    - Scientific notation with lowercase `e`, explicit `+`/`-` sign, and **no
      leading zero** in the exponent for $|x| < 10^{-6}$ or $|x| \ge 10^{21}$
      (e.g., `1e-7` → `1e-7`, `1e21` → `1e+21`).
  - **Canonical string escaping (RFC 8785 §3.2.2.2):** Emit only minimal JSON
    escapes (`\"`, `\\`, `\b`, `\t`, `\n`, `\f`, `\r`, and lowercase `\u00xx`
    for other control characters `U+0000..U+001F`). All other valid Unicode
    scalar values — including `+`, `<`, `>`, `&`, `'`, `/`, and non-ASCII UTF-8
    characters — MUST be emitted as raw unescaped UTF-8 bytes. Reject lone
    UTF-16 surrogates.
  - **Explicit `.NET System.Text.Json` divergence warning (DO NOT use raw
    default `Utf8JsonWriter` / `JsonSerializer`):** Raw default .NET
    `System.Text.Json` diverges from RFC 8785 (JCS) on valid contract inputs:
    (1) default .NET formats `0.000001` as `1E-06` (scientific notation below
    `1e-4` with uppercase `E` and zero-padded exponent) whereas RFC 8785 emits
    `0.000001`; and (2) default `JavaScriptEncoder.Default` HTML-escapes ASCII
    characters (`"A+B"` → `"A\u002BB"`) whereas RFC 8785 emits `"A+B"`. C#
    canonicalizers MUST implement an explicit RFC 8785 number formatter
    (shortest round-trip IEEE 754 digits formatted under RFC 8785 §3.2.2.3
    thresholds) and minimal RFC 8785 string escaping (never
    `JavaScriptEncoder.Default`, and normalizing `U+0000..U+001F` control
    escapes to lowercase `\u00xx`).
  - **UTC timestamps & structural rejection:** UTC ISO-8601 `"Z"` timestamps
    (`yyyy-MM-ddTHH:mm:ssZ`, trimming trailing fractional zeros only when
    sub-second); reject `NaN`/`Infinity`, duplicate keys, unknown fields
    (`additionalProperties: false`), or nesting $> 64$ levels.

**Normative RFC 8785 (JCS) Canonicalization Test Vectors (mandatory Slice S0/S1 & M0/M1 gate):**

| Input JSON Value | Exact Canonical UTF-8 | Exact UTF-8 Bytes (hex) | Bytes | Uppercase SHA-256 |
| --- | --- | --- | --- | --- |
| `0.000001` (`1e-6`) | `0.000001` | `302e303030303031` | `8` | `159FB29A827AD04B260AA6C8AB6D8637F8F2B38AF5C4F3CB49D6A21205E040F8` |
| `-0.0` | `0` | `30` | `1` | `5FECEB66FFC86F38D952786C6D696C79C2DBC239DD4E91B46729D73A27FB57E9` |
| `1000` / `1000.0` | `1000` | `31303030` | `4` | `40510175845988F13F6162ED8526F0B09F73384467FA855E1E79B44A56562A58` |
| `"A+B"` | `"A+B"` | `22412b4222` | `5` | `BF7700489FA343FC8786B38A88A4BDB4AA4647B6E74DD95A2C8932109A1600A6` |
| `1e-7` (below $10^{-6}$) | `1e-7` | `31652d37` | `4` | `5B33E02F2C5103A05D32F6BA9CB058294452BFBF393967F68BB30C1BDCBBAB22` |
| `{"z":-0.0,"c":1000.0,"b":"A+B","d":1e-6}` | `{"b":"A+B","c":1000,"d":0.000001,"z":0}` | `7b2262223a22412b42222c2263223a313030302c2264223a302e3030303030312c227a223a307d` | `39` | `C89E8481A4DF5DE2D520BA13BD0A91565F1705A2E10647CE86C53D69AE64C4EF` |

### 3.4 Strictly Synthetic Canonical JSON Examples (`999999 Synthetic Demo`)

Consistent with Gate **`S0-Clearance`** in
[`sky-layers-source-decision-1396.md`](../planning/sky-layers-source-decision-1396.md),
the following schema-verification examples are **100% HVO-authored synthetic
data** for fictional target **`999999 Synthetic Demo`** (`spkId: "20999999"`),
with synthetic solution dates, synthetic EOP filenames/coverage dates, and
analytically generated knots. **Zero verbatim JPL Horizons/SBDB/Lookup headers,
live EOP strings, live knots, or live raw payload hashes are committed here.**
Live Horizons/SBDB verification (for example, `433 Eros` and `1P/Halley`) runs
locally/on-demand against the operator's or developer's uncommitted local cache
and is never committed to this repository unless/until Gate `S0-Clearance` is
recorded.

**Documented closed-form synthetic trajectory (reproducible analytically):**
- Let $\tau = (t - \text{2026-11-27T00:00:00Z}) / 1\text{ h}$ with uniform
  hourly knots $\tau \in \{-1, 0, 1, 2\}$ (`stepSeconds = 3600`, usable span
  $\tau \in [0, 1]$).
- Topocentric ICRF astrometric vector (AU):
  $$\mathbf{r}_{\text{astrom}}(\tau) = (0.20, 0.08, 0.04) + (-0.0008, 0.0016, 0.0004)\,\tau$$
  ($\Delta_{\min} = 0.218973605715\text{ AU} \ge 0.2\text{ AU}$, max hourly step
  $\approx 0.479^\circ \ll 2.5^\circ$, IAU 2012 $1\text{ AU} = 149\,597\,870.7\text{ km}$).
- Synthetic apparent airless vector (AU): fixed synthetic orthogonal frame
  rotation $\mathbf{r}_{\text{app}}(\tau) = R_z(+0.40^\circ)\,R_x(+0.05^\circ)\,\mathbf{r}_{\text{astrom}}(\tau)$.
- **Exact analytical midpoint verification ($\tau = 0.5$, `2026-11-27T00:30:00Z`):**
  The 4-point centered cubic unit-vector interpolant has exact analytical
  midpoint residual **`0.000030341575"`** (unrounded knots, identical for
  astrometric and apparent) and **`0.000029546627"`** astrometric /
  **`0.000029396657"`** apparent when evaluated from the 9-decimal-place JSON
  knots below. Rounding upward to 6 decimal places gives
  **`maxVerifiedMidpointResidualArcsec: 0.000031`** (strictly $\ge$ the exact
  analytical midpoint residual — never understated; also exercises RFC 8785
  fixed-point formatting `0.000031` rather than .NET `3.1E-05`).
- All SHA-256 hashes and byte lengths below are computed under the **strict
  RFC 8785 (JCS)** canonical serializer defined in Section 3.3 (`rawSources[0]`
  hashes the 45-byte synthetic UTF-8 marker string
  `hvo-synthetic-small-body-demo-raw-v1:20999999`).

**Normalized payload (`hvo-small-body-ephemeris-v1` — strictly synthetic):**

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
        "aliases": ["2026 AA", "2099999"],
        "authority": "jpl-spk",
        "bodyKind": "Asteroid",
        "fragmentCode": null,
        "fullName": "999999 Synthetic Demo (2026 AA)",
        "isNeo": true,
        "isPha": false,
        "mpcPackedDesignation": null,
        "orbitClassCode": "AMO",
        "primaryDesignation": "999999",
        "shortName": "999999 Synthetic Demo",
        "spkId": "20999999"
      },
      "interpolationBoundVerified": true,
      "interpolationVerificationMethod": "HalfStepSample",
      "knots": [
        {
          "apparentDecDeg": 10.427662856,
          "apparentRaDeg": 21.719010991,
          "astrometricDecDeg": 10.409481357,
          "astrometricRaDeg": 21.32757511,
          "dec3SigmaArcsec": 0.05,
          "phaseAngleDeg": 42.6,
          "predictedNuclearOrSurface": null,
          "predictedTotalMagnitude": 16.798,
          "ra3SigmaArcsec": 0.08,
          "rangeAu": 0.219169705936,
          "rangeRateKmPerSec": -3.67069,
          "solarElongationDeg": 112.35,
          "solarElongationSide": "Trailing",
          "utc": "2026-11-26T23:00:00Z"
        },
        {
          "apparentDecDeg": 10.53830093,
          "apparentRaDeg": 22.192780755,
          "astrometricDecDeg": 10.519734891,
          "astrometricRaDeg": 21.801409486,
          "dec3SigmaArcsec": 0.05,
          "phaseAngleDeg": 42.5,
          "predictedNuclearOrSurface": null,
          "predictedTotalMagnitude": 16.8,
          "ra3SigmaArcsec": 0.08,
          "rangeAu": 0.219089023002,
          "rangeRateKmPerSec": -3.034745,
          "solarElongationDeg": 112.4,
          "solarElongationSide": "Trailing",
          "utc": "2026-11-27T00:00:00Z"
        },
        {
          "apparentDecDeg": 10.648307172,
          "apparentRaDeg": 22.667206209,
          "astrometricDecDeg": 10.629357335,
          "astrometricRaDeg": 22.275897848,
          "dec3SigmaArcsec": 0.05,
          "phaseAngleDeg": 42.4,
          "predictedNuclearOrSurface": null,
          "predictedTotalMagnitude": 16.802,
          "ra3SigmaArcsec": 0.08,
          "rangeAu": 0.219023651691,
          "rangeRateKmPerSec": -2.398164,
          "solarElongationDeg": 112.45,
          "solarElongationSide": "Trailing",
          "utc": "2026-11-27T01:00:00Z"
        },
        {
          "apparentDecDeg": 10.757656937,
          "apparentRaDeg": 23.142223534,
          "astrometricDecDeg": 10.738324124,
          "astrometricRaDeg": 22.750976343,
          "dec3SigmaArcsec": 0.05,
          "phaseAngleDeg": 42.3,
          "predictedNuclearOrSurface": null,
          "predictedTotalMagnitude": 16.804,
          "ra3SigmaArcsec": 0.08,
          "rangeAu": 0.218973605715,
          "rangeRateKmPerSec": -1.76108,
          "solarElongationDeg": 112.5,
          "solarElongationSide": "Trailing",
          "utc": "2026-11-27T02:00:00Z"
        }
      ],
      "lookupApiVersion": "1.1",
      "maxVerifiedMidpointResidualArcsec": 0.000031,
      "minimumRangeAu": 0.218973605715,
      "normalizerVersion": "small-body-normalizer-v1",
      "originManifestSha256": null,
      "paddedEndUtc": "2026-11-27T02:00:00Z",
      "paddedStartUtc": "2026-11-26T23:00:00Z",
      "parserVersion": "jpl-horizons-csv-v1",
      "recordSha256": "CE6602F995222FEE74F2FDF461E02704D0FBD95DEC6AF678B22DFFA181C394A7",
      "refractionApplied": false,
      "sbdbApiVersion": "1.3",
      "seriesRetrievedUtc": "2026-11-26T23:45:00Z",
      "solution": {
        "conditionCode": 1,
        "dataArcDays": 2338,
        "epochTdbJd": 2461200.5,
        "firstObsDateRaw": "2020-01-15",
        "hasCovariance": true,
        "horizonsEopCoverage": {
          "dataBasedEndDate": "2026-06-01",
          "dataBasedStartDate": "1970-01-01",
          "predictsEndDate": "2026-09-01"
        },
        "horizonsEopFile": "eop.260601.p260901",
        "horizonsRecordNumber": "999999",
        "lastObsDateRaw": "2026-06-10",
        "magnitudeModel": {
          "absoluteMagnitudeH": 15.2,
          "kind": "AsteroidHG",
          "nuclearMagnitudeM2": null,
          "nuclearSlopeK2": null,
          "phaseCoefficient": null,
          "slopeParameterG": 0.15,
          "totalMagnitudeM1": null,
          "totalSlopeK1": null
        },
        "nonGravitationalModel": null,
        "notValidAfterUtc": null,
        "notValidBeforeUtc": null,
        "observationCount": 150,
        "orbitId": "JPL#1",
        "planetaryEphemeris": "DE441",
        "residualRmsArcsec": 0.4,
        "smallBodyPerturbers": "SB441-N16",
        "solutionAuthority": "JPL",
        "solutionDateSourceRaw": "2026-06-15 17:30:00",
        "solutionDateSourceTimeZone": "America/Los_Angeles",
        "solutionDateUtc": "2026-06-16T00:30:00Z"
      },
      "stepSeconds": 3600,
      "validFromUtc": "2026-11-27T00:00:00Z",
      "validUntilUtc": "2026-11-27T01:00:00Z"
    }
  ]
}
```

**Matching manifest (`hvo-small-body-manifest-v1` — strictly synthetic):**

```json
{
  "attributionText": "Small-body ephemerides and orbit metadata: NASA/JPL-Caltech Solar System Dynamics (Horizons / Small-Body Database). Displayed by HVO SkyMonitor; not endorsed by NASA, JPL, or Caltech.",
  "carriedForwardCount": 0,
  "failedRefreshObjects": [],
  "horizonsApiVersion": "1.2",
  "lookupApiVersion": "1.1",
  "manifestSha256": "C38A43FF112173D5716334D0F15EA7FB687FBE150A218DC8CDEB41A64B23D532",
  "normalizedPayloadBytes": 3987,
  "normalizedPayloadSha256": "4F3BFEBB2E1C514736E06966A8594CC6D44CD1081BA5077154D2107787ECA45B",
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
      "querySummary": "synthetic-fixture OBSERVER DES=20999999; coord@399 AIRLESS Q=1,2,9,20,23,24,36 step=3600s",
      "rawBytes": 45,
      "rawSha256": "E11B4655FF56F065F810C223883EA3C7B7FBA21CC7C87A23F67B1D87681D8138",
      "spkId": "20999999"
    }
  ],
  "refreshedCount": 1,
  "retrievedUtc": "2026-11-26T23:45:00Z",
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
     interval $[t_k, t_{k+1}]$ ($k \in [1, N_{\text{knots}} - 3]$) MUST be verified $\le 1.0''$
     using one of two approved methods:
     - **Method A (`HalfStepSample` — required for Horizons acquisition and live/cached verification):**
       Fetch the Horizons `OBSERVER` ephemeris with `TIME_DIGITS='SECONDS'` at
       half-step resolution $h/2$ ($h/2 \in \{1800, 450, 150, 60, 30\}\text{ s}$)
       in a single Horizons HTTP request using unitless equal-interval stepping
       `STEP_SIZE='<N>'`, where
       $N = (\text{paddedEndUtc} - \text{paddedStartUtc}) / (h/2)$ divides the
       padded span into $N$ exact equal $h/2$ intervals ($N + 1$ uniformly
       spaced rows; unitless integer `STEP_SIZE` is required because Horizons
       calendar-unit stepping has a 1-minute floor and cannot express `150 s` or
       `30 s` half-steps). Because Horizons API v1.2 with
       `TIME_DIGITS='SECONDS'` and unitless `STEP_SIZE='<N>'` emits
       fractional-second timestamp column/value forms (for example, an optional
       `.000` fractional-seconds suffix in CSV rows), the CSV parser MUST accept
       both whole-second and fractional-second timestamp column/value forms,
       verify exact alignment of every row to the requested whole-second UTC
       grid (rejecting non-zero sub-second drift beyond strict sub-millisecond
       tolerance), and normalize parsed timestamps to canonical ISO-8601 UTC
       (`YYYY-MM-DDTHH:MM:SSZ`). Keep even-indexed samples as the retained $h$-spaced
       knots and odd-indexed samples as independent half-step verification
       points. For every usable interval $k \in [1, N_{\text{knots}}-3]$,
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
     - *Rectilinear fast flyby ($\Delta_{\min}=0.002\text{ AU} = 299\,195.7414\text{ km}$
       using IAU 2012 $1\text{ AU} = 149\,597\,870.7\text{ km}$, $v_\perp=75\text{ km/s}$,
       closest approach at $t = 0$, fixed aligned knot grid $t_k = k \cdot h$ for
       $k \in \mathbb{Z}$, evaluated symmetrically across all intervals around
       closest approach):*
       - At **`h = 300 s`**: aligned-grid midpoint max ($u = 0.5$, $k \in \{4, -5\}$)
         is **`1.115471"`** (`1.115470651063"`) and aligned-grid continuous max
         ($u \in [0, 1]$ on fixed aligned grid $t_k = k \cdot h$, $k = 4$ at
         $u = 0.497432$ and symmetric $k = -5$) is **`1.115503"`**
         (`1.115503350545"`). Shifting the knot-grid phase off $t = 0$
         ($t_k = (k + \phi)h, \phi \in [0, 1)$) reaches a slightly higher
         all-phase supremum of **`1.115682"`** (`1.115681943033"` at
         $t_k \approx 4.059673\,h, u \approx 0.497405$; for example,
         `1.115680"` / `1.115680183332"` at $t_k = 4.06\,h, u = 0.498$). Thus
         `h = 300 s` exceeds `1.0"` across all phases and is rejected.
       - Under $\omega_{\max} h \le 2.5^\circ$
         ($\omega_{\max}=51.7048''/\text{s} \implies h \le 174.07\text{ s}$),
         the contract selects **`h = 120 s`**: aligned-grid midpoint max
         ($u = 0.5$, $k \in \{11, -12\}$) is **`0.028812"`**
         (`0.028812363400"`), aligned-grid continuous max ($u \in [0, 1]$ on
         fixed aligned grid $t_k = k \cdot h$, $k = 11$ at $u = 0.498955$ and
         symmetric $k = -12$) is **`0.028813"`** (`0.028812503371"`), and the
         all-phase supremum over $\phi \in [0, 1), u \in [0, 1]$ is
         **`0.028818"`** (`0.028817857363"` at $t_k \approx 10.839060\,h,
         u \approx 0.498966$) — remaining well under `1.0"` across all phases.
     - *Oscillatory counterexample ($2'' \sin(\pi t/h)$ on 7 knots):*
       Integer-knot `2h`-stride at `k = 3` reports `0.000000"`, whereas Method A
       half-step verification at $t = 1.5h$ measures **`2.000000"`** and
       rejects/withholds the interval.
     - *Synthetic `999999 Synthetic Demo` Section 3.4 example (1 h analytical
       knots vs exact midpoint at `2026-11-27T00:30:00Z`):* exact analytical
       midpoint residual **`0.000030341575"`** (`0.000029546627"` astrometric
       ICRF / `0.000029396657"` apparent airless from the stored 9-decimal-place
       JSON knots) → recorded conservatively rounded upward as **`0.000031"`**.
       Live Horizons/SBDB parity checks (e.g., `433 Eros`) run locally against
       the operator's or developer's uncommitted cache (never committed unless/until
       Gate `S0-Clearance` is recorded).
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
