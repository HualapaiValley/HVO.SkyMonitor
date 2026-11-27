# Optional Sky Layers Source and Contract Decision Checkpoint (#1396)

Date: 2026-11-27. Issue: [#1396](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1396)
(`Optional sky layers: comets/asteroids and meteor showers`, incorporating folded
[#1397](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1397) per the
2026-11-11 issue triage). Target branch: `development/v1`.

## Plain-Language Synopsis

- **Why now:** `#1396` (including folded `#1397`) gates all small-body and
  meteor-shower runtime implementation on a reviewed source, terms, numerical
  convention, and bounded host-neutral contract decision.
- **Outcome:** Freezes the authoritative source selection, license and
  redistribution boundaries, physical/coordinate/time conventions, staleness
  and offline-replay rules, shared contract schemas, and implementation slice
  plan for both optional sky layers.
- **Benefit:** Prevents speculative N-body propagation, invalid SGP4 reuse,
  unapproved third-party data redistribution, PDF/HTML scraping, or scheduler
  duplication before code is written.
- **Unlocks:** Follow-on executable contract, immutable fixture, acquisition/import,
  evaluator, and Observatory/Software & Catalog UI pull requests under `#1396`.
- **Not included:** Production C# implementation, runtime configuration changes,
  automated polling code, generalized feed/catalog/notification platforms,
  full minor-planet survey catalogs, individual meteor prediction, or automatic
  meteor classification.

## Status and Authority

This document, together with the bounded host-neutral contracts
[`docs/contracts/small-body-ephemeris-v1.md`](../contracts/small-body-ephemeris-v1.md)
and [`docs/contracts/meteor-shower-context-v1.md`](../contracts/meteor-shower-context-v1.md),
constitutes the **First Checkpoint: reviewed source and contract decision** for
`#1396` (`Refs #1396`).

Nothing in this checkpoint adds production code, runtime configuration, network
requests, database migrations, or release redistribution of third-party data.
**`#1396` remains open** after this checkpoint merges; executable contracts,
frozen golden fixtures, providers/importers, evaluators, UI, and qualification
tests belong to follow-on implementation slices.

Each decision carries one of three explicit dispositions (following
[#527](orbital-data-decision-527.md) and [#1398](space-weather-source-decision-1398.md)):

- **Approved (Pending Review Merge):** Proposed for acceptance in this PR's
  independent review. Merging the reviewed commit records approval of this
  software design checkpoint; it does not claim implementation or physical
  qualification has passed.
- **Proposed:** Safe engineering default for follow-on implementation PRs. A
  maintainer may adjust it before or during implementation without reopening
  the architectural source decision.
- **Unresolved / External Gate:** Blocks only the specific capability named;
  initial implementation excludes that gated behavior until resolved and
  recorded.

---

## Decision Ledger

| ID | Domain | Decision | Status | Remaining Boundary |
| --- | --- | --- | --- | --- |
| `SB-01` | Small bodies | **JPL Horizons API** (`EPHEM_TYPE='OBSERVER'`, `CENTER='coord@399'`, `APPARENT='AIRLESS'`, `QUANTITIES='1,2,9,20,23,24,36'`) as primary bounded ephemeris source for an explicit operator watchlist | Approved (Pending Review Merge) | Bounded to explicit watchlist ($\le 32$ bodies); no full minor-planet survey catalog |
| `SB-02` | Small bodies | **JPL SBDB API** (`sbdb.api` v1.3) and **Horizons Lookup API** (`horizons_lookup.api` v1.1) as authoritative object-identity, alias, disambiguation, and orbit-solution metadata companions | Approved (Pending Review Merge) | Strict separation between canonical object identity (`spkid`) and orbit-solution identity (`orbit_id` / `soln_date`) |
| `SB-03` | Small bodies | **MPC** (`CometEls.txt`, `AllCometEls.txt`, `MPCORB.DAT.gz`, `mpcorb_extended.json.gz`) **not selected** as primary propagation authority; **satellite GP/TLE/SGP4** explicitly prohibited | Approved (Pending Review Merge) | MPC comet exports may only serve as an optional external reference or future separately-qualified discovery aid |
| `SB-04` | Small bodies | **Horizons `VECTORS` / binary `SPK`** not selected for v1; retained as documented future multi-site extension if ever needed | Approved (Pending Review Merge) | Avoids vendoring a binary DAF/SPK parser and duplicating comet photometric/covariance projection in v1 |
| `SB-05` | Small bodies | **Topocentric unrefracted coordinates**: retain both astrometric ICRF (`Q=1`) and apparent true-of-date airless (`Q=2`) knots on UTC (`TIME_TYPE='UT'`, `CAL_TYPE='GREGORIAN'`), bound to exact `DeploymentLocation` | Approved (Pending Review Merge) | Site/observer mismatch fails closed (`ObserverMismatch`); refraction applied only at display/projection time via `AtmosphericRefraction` after `#1394` geometric horizon check |
| `SB-06` | Small bodies | **Local interpolation & error bound**: 4-point centered cubic Lagrange interpolation on 3D topocentric unit vectors ($S^2$) with 1-knot padding at both ends and adaptive step sizes by $\Delta_{\min}$; never interpolate `AltAz` directly | Approved (Pending Review Merge) | Target interpolation residual $\le 1.0''$ against independent Horizons mid-knot reference points; never extrapolate beyond `[validFromUtc, validUntilUtc]` |
| `SB-07` | Small bodies | **Local Earth rotation & time inputs**: reuse existing bundled/refreshed `OrbitalTimeData` (`finals2000A.daily.extended` + `leap-seconds.list`) for UT1 + polar motion when covering the query instant; explicit degraded `ut1-approximated-by-utc-v1` fallback allowed for planning only, never for measured astrometric association | Approved (Pending Review Merge) | Leap-second-crossing intervals explicitly withheld; EOP provenance hashed in every product |
| `SB-08` | Small bodies | **Acquisition topology & rate policy**: one CameraAgent-local single-flight serial refresher per deployment via the existing local automation registry (`SmallBodyEphemerisRefresh`); no browser calls (NASA CORS), no second scheduler, $\ge 1\text{ s}$ inter-request pacing, 6 h floor, 24 h default cadence, persisted halts, and last-known-good retention | Proposed (cadence/pacing defaults) / Approved (architecture & serial Fair Use rules) | Watchlist $\le 32$ objects; disabled by default until explicitly enabled and populated |
| `SB-09` | Small bodies | **Closed `SolarSystemBody` preserved**: new `HVO.SkyMonitor.Astronomy.SmallBodies` types and separate optional `PredictedSmallBodyLayer` in `Processing`; never extend `SolarSystemBody` enum with arbitrary body names | Approved (Pending Review Merge) | Distinguishes geometric position, uncertain predicted magnitude (`T-mag`/`N-mag`/`APmag`), and measured detection; no coma/tail synthesis |
| `MS-01` | Meteor showers | **IAU Meteor Data Center (MDC) Shower Database (SD)** as authoritative source for shower identity (`IAU No.`, 3-letter `Code`, canonical `Name`), status (`Established` / `Working` / `Removed` / `Group`), mean geocentric radiant (J2000), peak $\lambda_\odot$, and $V_g$ | Approved (Pending Review Merge) | Offline curated/imported snapshot only; no automated polling of `ta3.sk` |
| `MS-02` | Meteor showers | **IMO Meteor Shower Calendar / Working List** evaluated for annual activity context: **no live HTML scraping and no PDF parsing**; IMO policy allows attribution-bearing use in apps but requires contacting IMO before republishing a full calendar or standalone dataset | **Unresolved Gate `M1`** for bundled IMO redistribution; Approved for local schema & operator-import boundary | Bundling IMO-derived annual activity tables in HVO.SkyMonitor releases is gated on recorded written IMO confirmation (`M1`). Without `M1`, releases bundle IAU MDC identity/radiant only, and operators may explicitly import a validated local annual activity file |
| `MS-03` | Meteor showers | **No second scheduler and no dependency on satellite refresh**: bounded versioned local snapshot (`hvo-meteor-showers-v1`) distributed via release-curated bundle + explicit verified local override/import (`SnapshotPath` + `SnapshotSha256`) | Approved (Pending Review Merge) | Zero network calls on Observatory, capture, or frame paths; last-known-good preserved on invalid import |
| `MS-04` | Meteor showers | **Solar-longitude anchor & annual expiry**: anchor windows to J2000.0 geocentric ecliptic solar longitude $\lambda_\odot \in [0^\circ, 360^\circ)$ to bridge `Dec 31 -> Jan 1` boundaries cleanly; calendar-year peak timestamps expire explicitly at `(YYYY+1)-01-15T00:00:00Z` | Approved (Pending Review Merge) | Expired annual forecasts are never silently recycled into a later year; HVO-derived UTC conversions from $\lambda_\odot$ are labelled separately from source calendar UTCs |
| `MS-05` | Meteor showers | **Observatory card required; radiant labels optional**: v1 requires an optional Observatory information card (active/upcoming showers, peak window/uncertainty, qualified idealized ZHR, radiant altitude, Sun/Moon context, optional `#1004` cloud evidence where missing stays `Unknown`); separately togglable radiant labels are an optional later slice | Approved (Pending Review Merge) | Card-only delivery may close `#1396`'s meteor-shower slice when optional radiant labels are explicitly recorded as omitted; zero forced capture annotation |
| `MS-06` | Meteor showers | **Strict epistemic separation**: informational forecast context only; **prohibited** from automatic meteor classification, individual streak prediction, inferred shower membership from radiant proximity, or IMO/AMS fireball-catalog ingestion | Approved (Pending Review Merge) | Enforced by separate types, UI copy rules, and regression tests |

---

## Part A — Small-Body (Comets & Asteroids) Source and Contract Decision

### A.1 Live-Verified Source Comparison (Verified 2026-11-27)

All candidate URLs and policies listed in `#1396` were verified directly on
2026-11-27 using sequential HTTPS requests without credentials or cookies.

| Source & Product | Verified Endpoint & Version | Coverage & Physical Model | Measured Footprint / Cadence | Access, Terms, License & Attribution | Offline / Caching & Operational Fit | Decision |
| --- | --- | --- | --- | --- | --- | --- |
| **JPL Horizons API — `OBSERVER`** | `https://ssd.jpl.nasa.gov/api/horizons.api` (JSON `signature.version: "1.2"`, docs v1.3 2025 June; batch `horizons_file.api`) | All numbered/unnumbered asteroids and comets in JPL SSD/DASTCOM; full N-body DE441 + SB441-N16 small-body perturbers + non-gravitational comet forces (`A1,A2,A3,DT,S0`) + Sun relativistic light bending + ITRF93 topocentric parallax + JPL EOP + predicted `APmag`/`T-mag`/`N-mag` + propagated 3-sigma RA/Dec uncertainty (`Q=36`) | Live sample (2026-11-26): 3 hourly steps for `433 Eros` = 13,983 B JSON; 14-day hourly table (339 rows) ≈ 40–50 KiB raw (~8–10 KiB gzip) per object. Watchlist of 32 objects ≈ 1.5 MiB/day raw. Orbits updated as new MPC/radar astrometry arrives | NASA/JPL SSD **Fair Use Policy** (`https://ssd-api.jpl.nasa.gov/doc/`): **strictly 1 request at a time**, cache/reuse locally, back off on errors, descriptive `User-Agent` (product, version, contact/project URL), **no browser/website embedding (NASA CORS policy)**, check `signature.version`, best-effort SLA. U.S. Government / NASA-JPL public-domain scientific output; no NASA/JPL endorsement implied | One HTTP request per watched body yields complete topocentric astrometric + apparent positions, range, elongation, phase, predicted magnitude, and 3-sigma sky uncertainty ready for offline cubic interpolation and replay. Observer-bound (must invalidate on site change) | **Selected primary ephemeris source (v1)** |
| **JPL Horizons API — `VECTORS`** | `https://ssd.jpl.nasa.gov/api/horizons.api` (`EPHEM_TYPE='VECTORS'`, `CENTER='500@399'`, `REF_SYSTEM='ICRF'`, `VEC_TABLE='2'`, `VEC_CORR='LT'` or `'LT+S'`) | Same DE441 + SB441-N16 + non-gravitational trajectory accuracy as `OBSERVER`, returning geocentric ICRF $(X,Y,Z,V_x,V_y,V_z)$ | Live sample (2026-11-26): 3 steps for `433 Eros` = 6,675 B JSON; 14-day 6-hour table (59 rows) ≈ 12 KiB/object | Same NASA/JPL SSD Fair Use Policy and CORS prohibition | Observer-independent (ideal for multi-site fleet distribution) and supports cubic Hermite interpolation, **but omits predicted magnitudes (`T-mag`/`N-mag`/`APmag`), solar elongation, phase angle, and 3-sigma plane-of-sky uncertainty (`Q=36`)**, requiring either a second `OBSERVER` call or custom comet photometry + covariance projection | **Not selected for v1**; documented future multi-site extension |
| **JPL Horizons API — `SPK`** | `https://ssd.jpl.nasa.gov/api/horizons.api` (`EPHEM_TYPE='SPK'`, small bodies only) | NAIF SPICE binary DAF/SPK segments covering `START_TIME..STOP_TIME` | Tens of KiB binary per body | Same NASA/JPL SSD Fair Use Policy; reading SPK requires vendoring or implementing a binary DAF/SPK Type 1/2/21 evaluator (or native CSPICE) | Unnecessary binary parser / native-library complexity for a $\le 32$-object watchlist; still lacks photometric magnitude and plane-of-sky 3-sigma uncertainty | **Rejected for v1** |
| **JPL SBDB API + Horizons Lookup API** | `https://ssd-api.jpl.nasa.gov/sbdb.api` (v1.3, 2021 Sept); `https://ssd-api.jpl.nasa.gov/sbdb_query.api` (v1.0); `https://ssd.jpl.nasa.gov/api/horizons_lookup.api` (v1.1, 2025 Mar) | Authoritative object metadata (`spkid`, `des`, `pdes`, `fullname`, `shortname`, `kind`, `des_alt`/`alias`), orbit-solution identity (`orbit_id`, `soln_date`, `epoch`, `first_obs`, `last_obs`, `data_arc`, `n_obs_used`, `condition_code`, `rms`, `not_valid_before`, `not_valid_after`, `model_pars`), physical parameters (`phys-par`), and ambiguity resolution (`73P` returned 74 distinct parent/fragment/epoch entries on 2026-11-26) | Live sample (2026-11-26): `433 Eros` = 8,309 B JSON; `1P/Halley` = 7,405 B JSON; `horizons_lookup` for `Halley` = 707 B JSON | Same NASA/JPL SSD Fair Use Policy (`https://ssd-api.jpl.nasa.gov/doc/`) and U.S. Government / NASA-JPL attribution | Ideal low-cost companion for object lookup, alias preservation, ambiguity detection, and orbit-solution provenance; osculating elements alone are **not** used as a local two-body propagator | **Selected identity & orbit-solution companion** |
| **IAU MPC Bulk Orbits & Comet Exports** | `https://minorplanetcenter.net/data`; `https://minorplanetcenter.net/iau/MPCORB/CometEls.txt` & `AllCometEls.txt`; `Soft00Cmt.txt`; `MPCORB.DAT.gz`; `mpcorb_extended.json.gz`; `nea_extended.json.gz`; format docs `https://docs.minorplanetcenter.net/mpc-ops-docs/orbits/comet-orbit-format/` | All MPC-published minor planets and comets as single-epoch osculating elements; standard 160-char comet export has **no non-gravitational `A1/A2/A3` parameters** and **no formal covariance matrix** (minor planets carry only integer `U` uncertainty code) | Live `HEAD` measurements (2026-11-26/27): `MPCORB.DAT.gz` = **94.88 MB**; `mpcorb_extended.json.gz` = **183.30 MB** (>1 GiB expanded); `nea_extended.json.gz` = **5.03 MB**; `AllCometEls.txt` = **789.5 KB**; `CometEls.txt` / `Soft00Cmt.txt` = **166.0 KB**; updated daily (NEAs multiple times/day) | Open public scientific archive under IAU/SAO; requested attribution snippet (`https://minorplanetcenter.net/data`): *"This research has made use of data and/or services provided by the International Astronomical Union's Minor Planet Center."* No formal SPDX license file published; treat bulk mirrors carefully | `MPCORB` is a full minor-planet survey catalog (explicitly out of scope in `#1396`); single-epoch osculating elements cannot match Horizons/DE441 perturbed + non-gravitational comet accuracy without writing and qualifying a full N-body integrator | **Not selected as ephemeris/propagation engine**; optional external cross-check only |
| **Satellite GP / TLE / SGP4** | Existing `#527`/`#528`/`#529` satellite lane | Geocentric Earth-gravity/drag analytical mean-element theory (TEME) | N/A | N/A | Physically invalid for heliocentric ellipses, parabolas, hyperbolas, planetary perturbations, and comet non-gravitational accelerations | **Explicitly prohibited** |

### A.2 Exact Terms, Licenses, and Attribution (Small Bodies)

1. **NASA/JPL Solar System Dynamics (SSD) Fair Use Policy** (read verbatim from
   `https://ssd-api.jpl.nasa.gov/doc/` on 2026-11-26):
   - Limit requests to those reasonably necessary for the application.
   - Automated processes must not make unnecessarily frequent, repetitive, or
     redundant requests; cache and reuse results locally.
   - Submit **only one API request at a time** (no simultaneous/parallel requests).
   - Handle errors, rate limits, and service unavailability by backing off or
     reducing request rates rather than aggressively retrying.
   - Provide a valid, application-specific `User-Agent` request header including
     product name, version, and contact/project information.
   - **Never embed these APIs in client-side website/browser code** (NASA CORS
     policy). All acquisition runs server-side on CameraAgent outside the frame
     and browser paths.
   - Always inspect the JSON `signature.version` field to detect format changes.
   - Service is best-effort with no permanent availability guarantee.
2. **Data License & Attribution:**
   - NASA/JPL SSD ephemerides and small-body database outputs are U.S.
     Government-funded scientific data products (publicly accessible for lawful
     scientific/educational/software use without implied NASA or JPL
     endorsement; no NASA/JPL logo). Note: `https://www.jpl.nasa.gov/copyrights.php`
     returned HTTP 403 to non-browser clients on 2026-11-26; the applicable
     service terms are the SSD Fair Use Policy on `https://ssd-api.jpl.nasa.gov/doc/`
     plus NASA public-domain/non-endorsement practice matching
     `tests/fixtures/horizons/issue-518/manifest.json`.
   - **Required UI & provenance attribution text:**
     > *"Small-body ephemerides and orbit metadata: NASA/JPL Solar System Dynamics (Horizons / Small-Body Database). Displayed by HVO SkyMonitor; not endorsed by NASA or JPL."*
   - **Proposed `User-Agent`:**
     `HVO.SkyMonitor.CameraAgent/<version> (+https://github.com/HualapaiValley/HVO.SkyMonitor)`
     (build metadata stripped; no personal email address committed).
   - **No bulk redistribution:** Retained Horizons/SBDB raw responses stay in
     CameraAgent's local store for offline computation, replay, and local backup
     (never republished as a public feed/API). Small immutable test fixtures
     in the repository record exact query parameters, retrieval UTC, SHA-256,
     and NASA/JPL attribution (matching `tests/fixtures/horizons/issue-518/`).
3. **Software Dependencies:**
   - **Zero new third-party NuGet or native propagation dependencies** are
     required for small-body ephemerides in v1.
   - Reuses existing pinned **Astronomy Engine 2.1.19** (MIT, recorded in
     `THIRD-PARTY-NOTICES.md`) for offline Sun/Moon context, existing bundled/refreshed
     **USNO `finals2000A.daily.extended` + IERS `leap-seconds.list`** (public
     release / public domain, `docs/astronomy/bundled-time-data-v1.md`) for
     UT1/EOP conversion, and HVO-authored pure C# cubic Lagrange interpolation
     and coordinate transforms in `HVO.SkyMonitor.Astronomy`.

### A.3 Physical, Coordinate, Time, and Uncertainty Conventions

| Concern | Selected Convention | Rationale & Verification |
| --- | --- | --- |
| **Center & Observer Binding** | Topocentric geodetic WGS84/ITRF93 site: `CENTER='coord@399'`, `COORD_TYPE='GEODETIC'`, `SITE_COORD='<lon_deg>,<lat_deg>,<alt_km>'` (east-positive longitude; `ObserverLocation.ElevationMeters / 1000.0`). Height is explicitly labelled `AssumedEllipsoidal` when no surveyed datum exists (matching `#527`/`#1393`) | Horizons `OBSERVER` applies exact ITRF93 topocentric parallax, critical for close-approaching NEAs (up to tens of arcminutes at lunar distances). Snapshot manifest binds `observerLocationSha256` (DeploymentLocation ID/version + exact lat/lon/elev). **Any site mismatch fails closed with `ObserverMismatch`**; changing site triggers cache invalidation and requires a fresh acquisition |
| **Coordinate Frames** | Retain **both** `QUANTITIES=1` (**topocentric astrometric RA/Dec in ICRF**, light-time corrected, no aberration/deflection/precession/refraction) and `QUANTITIES=2` (**topocentric apparent RA/Dec of true equator & equinox of date**, light-time + annual/diurnal stellar aberration + solar gravitational light deflection + IAU precession/nutation, **airless**) per knot (`ANG_FORMAT='DEG'`, `EXTRA_PREC='YES'`) | `Q=1` provides exact J2000/ICRF coordinates for catalog/WCS cross-reference; `Q=2` provides the exact true-of-date apparent direction for horizontal (`AltAzPoint`) and calibrated rig projection without reimplementing solar light-time iteration, aberration, or relativistic light bending |
| **Time Scale** | `TIME_TYPE='UT'`, `CAL_TYPE='GREGORIAN'`. Horizons `UT` is UTC post-1962; every stored knot timestamp is normalized UTC (`DateTimeOffset` with offset `0`). Orbit solution `epoch` in metadata is preserved explicitly in **TDB** (Julian Date `epochTdbJd`) and never conflated with UTC | Matches `HVO.SkyMonitor.Astronomy` UTC query contract while preserving JPL's exact TDB osculating epoch provenance. Leap-second-crossing intervals are explicitly withheld using `OrbitalTimeData` leap-table checks |
| **Atmospheric Refraction** | **Strictly unrefracted (`APPARENT='AIRLESS'`)** in retained ephemeris snapshots and geometric evaluation. Optional atmospheric refraction is applied **only** at projection/display time via existing `AtmosphericRefraction.Apply` (`bennett-1982-standard-atmosphere-v1`) **after** `#1394` geometric horizon-obstruction evaluation | Mandatory per `docs/contracts/horizon-obstruction-v1.md`: geometric horizon and site-obstruction masks must never receive refracted/apparent elevations; keeps small bodies consistent with stars, planets, and satellites |
| **Solar Ephemeris & EOP Inputs** | **Upstream ephemeris generation:** JPL Horizons DE441 planetary ephemeris, SB441-N16 perturbers, and JPL's active EOP file — exact header values (`DE441`, `EOP file`, `EOP coverage`) are parsed and recorded in snapshot provenance.<br>**Local horizontal conversion (`Q=2` → `AltAzPoint`):** Reuse existing `OrbitalTimeData` (`finals2000A.daily.extended` + `leap-seconds.list`) for exact UT1 (via DUT1), GAST (IAU equation of equinoxes), and polar motion $(x_p, y_p)$ when covering the query instant. If EOP is unavailable or uncovered, planning mode may fall back to `ut1-approximated-by-utc-v1` (via `AstronomyTime`) with explicit degraded provenance and an uncalibrated $\le 15''$ timing-basis caveat; **measured astrometric association rejects UTC≈UT1 fallback**.<br>**Local Sun/Moon darkness context:** Reuse existing `IPlanetEphemeris` (`AstronomyEnginePlanetEphemeris`), `SolarAltitudeClassifier`, `SolarDiskEphemeris`, and `ISolarEventCalculator` | Avoids duplicating EOP storage or solar ephemeris models; guarantees sub-arcsecond agreement with Horizons `QUANTITIES=4` airless Alt/Az when EOP covers the instant while remaining explicitly honest about degraded accuracy when EOP is absent |
| **Object Identity vs Orbit-Solution Identity** | **Object identity:** `authority = "jpl-spk"`, canonical decimal `spkId` (e.g. `"20000433"` for 433 Eros, `"1000036"` for 1P/Halley), `bodyKind` (`Asteroid` / `Comet`), `primaryDesignation` (`"433"`, `"1P"`, `"73P-C"`), optional `fragmentCode`.<br>**Aliases:** `fullName`, `shortName`, optional `mpcPackedDesignation`, and full immutable `aliases[]` preserved from SBDB/Lookup (`des_alt` / `alias`) — never used as primary key.<br>**Orbit-solution identity:** `solutionAuthority` (`"JPL"`), `orbitId` (e.g. `"JPL#659"`, `"JPL#75"`), `horizonsRecordNumber` (e.g. `"433"`, `"90000740"`), `solutionDateUtc`, `epochTdbJd`, `firstObsDate`, `lastObsDate`, `dataArcDays`, `observationCount`, `conditionCode` (0–9 or `null`), `residualRmsArcsec`, `nonGravitationalModel` (e.g. `A1/A2/A3/DT/S0` when present), `notValidBeforeUtc`, `notValidAfterUtc` | Live 2026-11-26 probes confirmed `73P` matches **74** Horizons records across multiple apparition epochs (`90000734`–`90000740`) and fragments (`73P-A`..`73P-BT`) with separate SPK-IDs, and `2000433` (historical 7-digit SPK-ID) maps to canonical 8-digit `20000433`. Ambiguous queries **must fail closed** with `AmbiguousDesignation` and return candidate identities; comet fetches pin explicit `DES=<spkid>;CAP;NOFRAG` (or exact fragment `spkid` + `CAP`) |
| **Validity Interval & Extrapolation** | Every per-object ephemeris carries `[validFromUtc, validUntilUtc]` (the usable knot window, excluding the 1-knot padding at each end) plus any SBDB `notValidBeforeUtc`/`notValidAfterUtc`. Queries outside the intersection **never extrapolate** and return explicit `OutOfRange` / `Expired` | Prevents silent polynomial blow-up or unvalidated orbital projections beyond fetched bounds |
| **Covariance & Uncertainty Availability** | Parse `QUANTITIES=36` (`ra3SigmaArcsec`, `dec3SigmaArcsec`) per knot and `(+COV)` header presence; retain SBDB `conditionCode`, `dataArcDays`, and `solutionDateUtc`. Live 2026-11-26 probes confirmed `433 Eros` returns finite `0.009", 0.006"` while `1P/Halley` returns `n.a., n.a.`. Missing/unavailable values **must remain `null`** with explicit `UncertaintyStatus = Unavailable` — **never coerced to `0.0`**. Label withholding rule (Proposed): if `max(ra3Sigma, dec3Sigma) > 1800"` ($0.5^\circ$) or projects to $> 10\text{ px}$ in the active rig, display an explicit large-uncertainty warning or withhold the point label | Preserves scientific honesty when comet outgassing or short discovery arcs lack formal covariance or carry large sky-plane uncertainty |
| **Predicted Magnitude vs Detection** | Retain `QUANTITIES=9` (`predictedTotalMagnitude` = `APmag`/`T-mag`, `predictedNuclearOrSurface` = `S-brt`/`N-mag`, plus magnitude model metadata `H/G` or `M1/K1/M2/K2`) as **uncertain predicted values** (`null` when `n.a.`). Comets can brighten or fade by multiple magnitudes unpredictably. Products carry **no `Visible` or `Detected` boolean**, no coma/tail synthesis, and no image-identification claim | Satisfies `#1396` requirement separating geometric position, uncertain predicted magnitude/activity, and measured camera detection |
| **Interpolation & Error Bounds** | **4-point centered cubic Lagrange interpolation** on 3D unit vectors $\hat{\mathbf{u}}(t) \in S^2$ (re-normalized to unit length) over uniform UTC knots, applied separately to `Q=1` (ICRF) and `Q=2` (apparent of date), and cubic/linear on range $\Delta$, elongation, phase, and magnitude (returning `null` if any stencil knot is `null`). Request **1 extra padding knot before `validFromUtc` and 1 after `validUntilUtc`** so every instant in `[validFromUtc, validUntilUtc]` uses a full centered 4-point stencil.<br>**Adaptive step-size tiers by minimum geocentric distance $\Delta_{\min}$:**<br>• Standard ($\Delta_{\min} \ge 0.2\text{ AU}$): `STEP_SIZE='1 h'`, 14-day window (339 knots incl. padding, ~45 KiB).<br>• Close NEA ($0.01 \le \Delta_{\min} < 0.2\text{ AU}$): `STEP_SIZE='15 m'`, 3-day window (291 knots, ~38 KiB).<br>• Very close flyby ($0.002 \le \Delta_{\min} < 0.01\text{ AU}$): `STEP_SIZE='5 m'`, 24-hour window (291 knots, ~38 KiB).<br>• Ultra-close ($\Delta_{\min} < 0.002\text{ AU}$, $< 0.78$ lunar distances): explicitly `CloseApproachUnsupported` in v1.<br>**Mathematical diurnal-parallax bound:** For 4-point cubic Lagrange on $A\sin(\omega_\oplus t)$, $|E| \le \frac{3}{128} A (\omega_\oplus h)^4$. At $\Delta = 0.05\text{ AU}$ ($A \approx 176''$) and $h = 1\text{ h}$, $|E| \le 0.019''$; at $\Delta = 0.01\text{ AU}$ ($A \approx 880''$) and $h = 15\text{ m}$, $|E| \le 0.0004''$. Declared implementation acceptance gate: **$\le 1.0''$ ($0.00028^\circ$) maximum interpolation residual** against independent Horizons mid-knot reference positions | Eliminates zenith/azimuth-wrap singularities (by never interpolating `AltAz`), eliminates boundary-order drops (via 1-knot padding), and guarantees sub-arcsecond accuracy across both ordinary comets and fast close-approaching NEAs within tiny memory/wire budgets |
| **Staleness, Refresh & Last-Known-Good** | **Freshness vs Validity kept distinct:**<br>• Retrieval freshness warning (`Stale`): retrieval age $> 72\text{ h}$ for comets/NEAs or $> 168\text{ h}$ for numbered main-belt/outer asteroids (Proposed defaults).<br>• Ephemeris validity (`Expired` / `OutOfRange`): `now` outside `[validFromUtc, validUntilUtc]` or outside SBDB `notValidBefore..notValidAfter`.<br>• **Refresh policy:** Disabled by default. Single-flight durable task (`SmallBodyEphemerisRefresh`) registered in CameraAgent's existing local automation store (same seam as `OrbitalElementsRefresh` and `SpaceWeatherRefresh`). Default interval 86,400 s (24 h), hard floor 21,600 s (6 h), max 604,800 s (7 d). Sequential requests only, $\ge 1,000\text{ ms}$ inter-request delay, 30 s per-request timeout, no redirects, no cookies, no automatic retry loops. HTTP 403 or 3 consecutive 429s trigger a persisted provider halt requiring explicit operator resume; 5xx/timeout backs off and retains last-known-good. Unchanged or failed refresh **never erases last-known-good**. Historical snapshots remain readable by exact `manifestSha256` for offline replay and rollback | Strictly adheres to JPL SSD Fair Use Policy and mirrors the proven `#528`/`#1398` durable store, quarantine, halt, and rollback guarantees |

---

## Part B — Meteor-Shower (Folded `#1397`) Source and Contract Decision

### B.1 Live-Verified Source Qualification (Verified 2026-11-27)

| Source | Live Verification (2026-11-27) | Authoritative Scope | Status Handling & Data Model | Terms, Redistribution & Maintainability | Decision |
| --- | --- | --- | --- | --- | --- |
| **IAU Meteor Data Center (MDC) — Shower Database (SD)** | `https://www.iaumeteordatacenter.org/` returned HTTP 200 (12,868 B). Linked `ta3.sk` database hosts (`https://ceres.ta3.sk/iaumdcdb`, `https://ceresiaumdc.ta3.sk`, `https://www.ta3.sk/IAUC22DB/MDC2022/`) experienced connection timeouts and `Errno 65` (no route) during live probes | Official central registry of meteor shower names, IAU numbers, 3-letter codes, shower groups, and published mean geocentric radiant / orbit / solar-longitude datasets (maintained under IAU Commission F1 & WG on Meteor Shower Nomenclature). Explicit MDC caveat: MDC is a registry of published showers, not an authority certifying observational quality or real-time activity | Five official lists:<br>1. **Established** (IAU General Assembly approved)<br>2. **Working List** (published in peer-reviewed astronomy journals, *WGN*, or *eMeteorNews*, awaiting confirmation)<br>3. **Removed** (duplicates, spurious, or removed per published criteria)<br>4. **All Showers** (all non-removed)<br>5. **Shower Groups** (complexes).<br>Coordinates are J2000.0 equatorial mean geocentric radiant $(\alpha_g, \delta_g)$, peak solar longitude $\lambda_\odot$, and geocentric velocity $V_g$ (km/s) | Public astronomical database for research and reference; cite IAU MDC (Commission F1, `https://www.iaumeteordatacenter.org/`) plus MDC snapshot/export date and reference papers. **Slow/intermittent institutional host (`ta3.sk`) makes automated per-deployment live polling operationally unsound** | **Selected authoritative identity & radiant source via offline versioned snapshot (curated at release or imported locally); never polled automatically by CameraAgent** |
| **International Meteor Organization (IMO) — Meteor Shower Calendar & Working List** | Legacy URL `https://www.imo.net/resources/calendar/` returned **HTTP 404**; `/members/imo_showers/working_shower_list/` returned a rebuild placeholder (*"This IMO tool is being rebuilt"*). Current live pages are `https://www.imo.net/calendar/` (2026/2027 HTML calendar), `https://www.imo.net/meteor-shower-calendar-2027/` (37th ed., ed. Jürgen Rendtel), `https://www.imo.net/files/data/calendar/cal2027.pdf`, and `https://www.imo.net/about/using-imo-data/` (Last-Modified 2026-11-23) | Annual observing guidance: active date/$\lambda_\odot$ intervals, predicted peak date/time/$\lambda_\odot$ (including annual filament/outburst notes), idealized zenithal hourly rate (`ZHR`), population index (`r`), radiant drift, and lunar-interference commentary | Calendar year explicit (e.g., 2026, 2027); predictions revised annually with occasional corrections. **No public structured JSON/CSV API** exists; only human-readable web pages and annual PDFs | **Verbatim policy (`https://www.imo.net/about/using-imo-data/`):**<br>• **Freely permitted:** use, reproduce, and share data from the Meteor Shower Calendar and Working List of Visual Meteor Showers (including in apps/services) with credit (`Source: International Meteor Organization (IMO) – www.imo.net`, direct link `https://www.imo.net`, edition/year), no endorsement/logo, keeping derived values clearly distinct from IMO values, and keeping data reasonably up to date.<br>• **Contact IMO first before:** reproducing the Meteor Shower Calendar or other IMO publications in full or in substantial part, OR **republishing an IMO dataset in full as a standalone file, database or API**, or reusing third-party photos/articles.<br>• **Fireball reports excluded:** Not owned by IMO (collected with allsky.org / American Meteor Society); explicitly excluded | **Qualified with External Gate `M1`**:<br>1. **Never** scrape HTML or treat `calYYYY.pdf` as a live structured API.<br>2. Before any HVO.SkyMonitor release bundles an IMO-derived annual activity table, obtain and record written IMO confirmation (`M1`) covering a bounded subset bundled in the offline app.<br>3. Until `M1` is recorded, releases bundle IAU MDC established identities/radiants only, and operators may explicitly import a locally curated, schema-validated annual activity file |
| **Third-party community mirrors / historical fireball catalogs (AMS / IMO VMDB / SonotaCo / RMOB)** | Checked `https://www.imo.net/resources/databases/` (SonotaCo J14, VMDB, VISDAT, IMONET, MSSWG, RMOB) | Observational archives of past individual meteors/fireballs, not authoritative shower nomenclature or general-purpose annual observing forecasts | N/A | Separate licenses/ownership (e.g., AMS owns fireball reports; individual authors own articles/catalogs) | **Explicitly excluded from v1** (`#1397`: *"Historical fireball catalogs are observations and are not required here"*) |

### B.2 Status Handling, Time Conventions, Drift, Uncertainty, and Epistemic Rules

1. **IAU MDC Status Handling:**
   - `Established`: Included by default in the Observatory meteor-shower card
     and optional radiant overlay.
   - `Working`: Stored with explicit `status: "Working"` badge and caveat
     (*"IAU Working List — not yet officially established"*); hidden by default
     unless the operator explicitly enables **"Show working-list showers"**.
   - `Removed`: Never listed as active or plotted as a radiant; retained only as
     a compact tombstone record (`iauNumber`, `iauCode`, `removedNote`,
     optional `supersededByIauNumber`) so explicit lookups explain why a code
     was retired.
   - `Group`: Preserved only as optional `showerGroupId` grouping metadata.
2. **Time Convention, Solar Longitude, and Season/Year Boundaries:**
   - Physical activity intervals and nominal peaks are stored in **J2000.0
     geocentric ecliptic solar longitude** $\lambda_\odot \in [0^\circ, 360^\circ)$
     with explicit circular wrap handling across $360^\circ / 0^\circ$ (vernal
     equinox) and across calendar-year rollovers (e.g., **Ursids `URS`** Dec
     17–26 and **Quadrantids `QUA`** Dec 28–Jan 12).
   - Annual calendar-year activity records carry explicit `[validFromUtc,
     validUntilUtc]` covering `YYYY-01-01T00:00:00Z` through
     `(YYYY+1)-01-15T00:00:00Z` so December–January transitions never split a
     year-boundary shower.
   - Peak timestamps distinguish:
     - **`SourceCalendarUtc`**: explicit annual peak UTC (and/or peak window
       `[peakWindowStartUtc, peakWindowEndUtc]`) provided by a verified annual
       activity source (e.g., approved IMO edition);
     - **`DerivedFromSolarLongitudeUtc`**: deterministic UTC solved offline
       from the shower's J2000.0 $\lambda_\odot$ for the requested year by
       root-finding against **J2000.0 geometric geocentric solar ecliptic
       longitude** computed from pinned **Astronomy Engine 2.1.19**
       `GeoVector(Body.Sun, time, Aberration.None)` rotated by the IAU J2000.0
       mean obliquity $\varepsilon_{2000} = 23^\circ 26' 21.448''$
       ($23.439291111^\circ$) — **not** Astronomy Engine's of-date
       `SunPosition`/`SearchSunLongitude` directly, which is referenced to true
       equinox of date and differs from J2000.0 by $\sim 0.38^\circ$ ($\sim 9\text{ h}$)
       in 2026 due to precession. Explicitly labelled as **HVO-derived from
       J2000 solar longitude** (complying with IMO's *"make it clear which
       values are yours"* rule and providing accurate nominal timing even when
       no annual calendar overlay is loaded).
   - **Peak uncertainty:** Represented explicitly via nullable
     `peakUncertaintyHours` / `peakUncertaintySolarLongitudeDeg`, `peakProfile`
     (`Sharp`, `Broad`, `Variable`, `OutburstSuspected`, `Unknown`), and
     `activityConfidence` (`NominalAnnual`, `VariableOrUncertain`, `ModelPrediction`,
     `Unknown`). Missing uncertainty stays `null` / `Unknown`, never `0`.
   - **UTC and site-local display:** All contract timestamps are UTC. Observatory
     cards display both site-local civil date/time (via existing `SiteTimeZone`
     IANA rules) and UTC reference, clearly labelling local observing nights
     (e.g., *"Night of Dec 13–14 local"*) and flagging DST boundaries cleanly.
   - **Expired annual data:** Once `now > validUntilUtc`, annual activity
     forecasts are explicitly marked **`Expired`** and **never silently recycled
     for a later year**; static IAU MDC identity/radiant records remain
     separately inspectable with an explicit `"Annual activity data expired /
     unavailable"` notice.
3. **Radiant Altitude, Drift, and Optional Radiant Labels:**
   - Each shower stores reference geocentric radiant $(\alpha_0, \delta_0)$ in
     J2000.0 degrees at reference solar longitude $\lambda_{\odot,0}$ plus
     optional linear drift rates $(\Delta\alpha/\Delta\lambda_\odot,
     \Delta\delta/\Delta\lambda_\odot)$ (degrees per degree of solar longitude,
     nullable).
   - At instant $t$ with solar longitude $\lambda_\odot(t)$, when drift rates
     are present and $t$ lies inside the shower's active $\lambda_\odot$ window,
     the drifted J2000 radiant is evaluated via circular signed offset
     $\Delta\lambda_\odot = \text{wrap}_{[-180,180)}(\lambda_\odot(t) - \lambda_{\odot,0})$
     and clamped cleanly at poles; when drift is `null`, the peak radiant is
     used with explicit `radiantDriftBasis: "FixedPeakRadiantDriftUnavailable"`.
   - Horizontal radiant altitude/azimuth for the active `ObserverLocation` is
     computed locally via existing `EquatorialPrecession.PrecessJ2000` +
     `CoordinateTransforms.EquatorialToHorizontal` (unrefracted geometric
     altitude, consistent with `SolarAltitudeClassifier`).
   - **Hemispheric & below-horizon behavior:** A shower whose radiant stays
     below the geometric horizon (`maxAltitudeDeg <= 0°`, e.g., northern
     circumpolar Ursids from mid-southern latitudes or southern showers from
     northern latitudes) is explicitly labelled **`Below horizon at this site`**
     (never clamped to `0°`, never plotted above horizon, never claimed visible).
   - **Optional radiant labels:** Card-only delivery in Observatory is the
     required initial deliverable. Separately togglable radiant markers/labels
     on the Observatory sky plot (and optional presentation layer) are an
     explicit optional follow-on slice carrying epoch/date/drift semantics and
     projection provenance. Card-only delivery may close `#1396`'s meteor-shower
     scope if the optional radiant-label slice is explicitly recorded as
     omitted. **No forced annotation in capture imagery.**
4. **Local Darkness, Moon, and Optional `#1004` Cloud Context:**
   - Reuses existing `ISolarEventCalculator` (`AstronomyEngineSolarEventCalculator`)
     and `SolarAltitudeClassifier` for site darkness/astronomical twilight
     windows (`Sun <= -18°`).
   - Reuses existing `SolarDiskEphemeris` for Moon topocentric geometric
     altitude and illuminated fraction during the shower's local dark window.
   - Consumes optional `#1004` `cloud-assessment-v1` evidence only when a recent
     valid assessment exists for the site; **missing or unquantified cloud
     evidence remains explicitly `Unknown`** (never defaulted to clear!).
5. **Refresh / Import Policy (Zero Second Scheduler, Zero Satellite Dependency):**
   - Bounded versioned local snapshot (`hvo-meteor-showers-v1`, $\le 2\text{ MiB}$
     ceiling) with `provenance.json` and SHA-256 verification.
   - **No automated network poller and no automation task in v1.**
   - Precedence matches `docs/astronomy/bundled-time-data-v1.md`:
     1. Explicit verified local override/import (`CameraAgent:MeteorShowers:SnapshotPath`
        + `SnapshotSha256`, or owner-only local import into
        `<RawIngressRoot>/.meteor-showers/` with atomic pointer commit and
        last-known-good preservation). An invalid or hash-mismatched override
        fails closed with an explicit reason and never silently falls back or
        erases an intact last-known-good snapshot.
     2. Otherwise, the release-bundled verified snapshot beside the application
        (`meteor-showers/snapshot.json` + `provenance.json`, subject to gate
        `M1` for any IMO-derived activity fields).
   - Completely independent of `OrbitalElementsRefresh`, `TimeDataRefresh`,
     `SpaceWeatherRefresh`, and `SmallBodyEphemerisRefresh`.
6. **Strict Epistemic & Wording Boundaries:**
   - **Rate qualification:** Idealized source ZHR values are always displayed
     with their explicit qualifier (e.g., *"Source idealized ZHR (dark sky,
     radiant at zenith, limiting mag 6.5) — not a predicted count for this
     camera or site"*).
   - **Explicitly forbidden claims and behaviors:**
     - No automatic meteor classification of detected streaks/transients.
     - No individual meteor or streak arrival prediction.
     - No inferred shower membership from angular proximity of a captured
       streak to a shower radiant.
     - No ingestion of historical fireball observation catalogs.

---

## Part C — Shared Architectural Boundaries, Coordination, Risk Tier & Implementation Plan

### C.1 Ownership and Repository Boundaries

| Project / Layer | Owned Responsibilities (Follow-On Implementation) | Explicit Prohibitions |
| --- | --- | --- |
| `HVO.SkyMonitor.AgentCore` | **Unchanged** (reuses existing `ObserverLocation`, `RigInstallationHorizonProfile`, deployment-location/capture identities) | No ephemeris, shower, HTTP, SQLite, or provider types |
| `HVO.SkyMonitor.Astronomy` | Pure host-neutral types, canonical JSON contracts/validators, `SmallBodies` (`SmallBodyIdentity`, `SmallBodyOrbitSolution`, `SmallBodyEphemerisSnapshot`, `ISmallBodySnapshotReader`, `SmallBodyInterpolator`, `SmallBodyPathEvaluator`) and `MeteorShowers` (`MeteorShowerIdentity`, `MeteorShowerSnapshot`, `IMeteorShowerSnapshotReader`, `MeteorShowerSolarLongitudeEvaluator`, `MeteorShowerRadiantEvaluator`). Reuses `ObserverLocation`, `AltAzPoint`, `PixelPoint`, `ProjectionContext`, `IImageProjector`, `RigProjectionContextFactory`, `AtmosphericRefraction`, `HorizonObstructionEvaluator`, `OrbitalTimeData`, `IPlanetEphemeris`, `SolarAltitudeClassifier`, `SolarDiskEphemeris`, `ISolarEventCalculator`, `SiteTimeZone` | **Never extend closed `SolarSystemBody` enum**; no ASP.NET, EF Core, SQLite, HTTP, filesystem, or SkiaSharp dependencies; no SGP4 reuse |
| `HVO.SkyMonitor.Processing` | Optional separate presentation/layer contracts (`PredictedSmallBodyLayer`, optional `MeteorRadiantOverlayLayer`), strict separation from `PredictedSatelliteTracks` and `TransientObservation` | Never mutates raw/calibrated detector inputs; never attaches shower-membership labels to measured transients |
| `HVO.SkyMonitor.Catalog.Sqlite` | **Unchanged** (`HYG` / `OpenNGC` remain read-only static star/DSO snapshots) | Do not mix dynamic small-body ephemerides or annual meteor-shower calendars into HYG/OpenNGC SQLite packages |
| `HVO.SkyMonitor.CameraAgent.Common` | Dedicated isolated stores under `<RawIngressRoot>/.small-bodies/` (SQLite STRICT + WAL + content-addressed immutable snapshots/quarantine, mirroring `.orbital-elements/`) and `<RawIngressRoot>/.meteor-showers/` (plain verified files + atomic `current.json` pointer, mirroring `.time-data/`). Single-flight `SmallBodyEphemerisRefresh` task registered via existing `LocalAutomationTaskKind` periodic seam; provider HTTP client (no redirects, no cookies, serial pacing, version check, persisted halt); health & metrics; `MeteorShowerSnapshotLoader` | **No second scheduler**; **no generalized feed platform**; **no network or filesystem I/O inside capture or per-frame processing**; **zero dependency on satellite refresh or space-weather jobs** |
| `HVO.SkyMonitor.CameraAgent` | Owner-only status/watchlist/refresh/resume endpoints; **Software & Catalog** (`/operations/software`) status panels; **Automations** (`/operations/automations`) setup link for `small-body-ephemeris-refresh`; **Observatory & location** (`/operations/site`) optional small-body planning/layer panel and optional meteor-shower information card (and optional later radiant toggle) | Never blocks Observatory, satellites, space-weather notices, or capture if either layer is disabled, missing, stale, halted, or unavailable |

### C.2 Coordination with Adjacent Issues & Recovery Ordering Gate

- **`#1392` (Software & Catalog / Automations) and `#1393` (Observatory & Sky Map):**
  Add small-body status and meteor-shower snapshot status as independent
  composable panels on `SoftwarePage.razor` and `ObservatoryLocationPage.razor`
  (matching `OrbitalSnapshotPanel`, `SatelliteSetupPanel`, and
  `SatellitePassPlanner`), sharing no state or backend dependency with satellite
  refresh or planning.
- **`#1394` (Rig Installation Horizon Obstruction):**
  Both small-body paths/positions and optional meteor radiant markers consume
  **unrefracted geometric `AltAzPoint`** directions and evaluate site-horizon
  eligibility through `HorizonObstructionEvaluator` before optional display
  refraction. Unknown terrain is never treated as clear sky.
- **`#1004` (Cloud Assessment):**
  Observatory small-body planning and meteor-shower cards may read an existing
  recent `cloud-assessment-v1` fact as optional context; missing, stale, or
  unquantified cloud evidence remains explicitly **`Unknown`**.
- **`#523` (Measured Calibration):**
  Configured-rig projection is the baseline for planning and optional overlays.
  Claims using accepted measured astrometric calibration require `#523`'s
  accepted `AstrometricFrameAssessment` matching the exact capture, observer,
  and non-zero EOP (rejecting UTC≈UT1 fallback).
- **`#1395` (Performance & Cadence Qualification):**
  Per-frame lookup and projection for enabled small-body (or optional radiant)
  layers must be bounded in memory and CPU ($\le 32$ small bodies, immutable
  in-memory snapshot cached outside the frame path, zero per-frame disk/network
  I/O) and included in cadence measurements whenever enabled.
- **SkyMonitor Recovery Ordering Gate (`#1606`, `#1607`, `#1615`):**
  Per the recorded keep-current outcome (`docs/validation/upgrade-recovery-cost-1615.md`
  and `docs/planning/skymonitor-upgrade-recovery.md`), this design adds **no**
  changes to the raw ingress journal, processing schema, or environmental store:
  - `.meteor-showers/` uses plain verified files + atomic `current.json` (like
    `.time-data/`), which older binaries safely ignore.
  - `.small-bodies/` uses a dedicated isolated SQLite store (like
    `.orbital-elements/`) with its own lock and `user_version = 1`.
  - Adding `SmallBodyEphemerisRefresh` to `LocalAutomationTaskKind` follows the
    established **forward-only** automation-store boundary (`docs/runbooks/cameraagent-local-automation.md`):
    once an operator creates the reserved automation definition, older binaries
    fail that automation store closed and recovery is by upgrading forward.
    Meteor showers add no automation task kind and therefore impose no
    automation-store forward-only boundary.
- **Priority & Non-Blocking Isolation:**
  Meteor showers are lowest priority in this set; neither small bodies nor
  meteor showers may block satellite tracking, space-weather notices, capture,
  or Observatory rendering. Both features are **opt-in and disabled/inactive by
  default**.

### C.3 Risk Tier Classification

| Workstream | Risk Tier | Justification |
| --- | --- | --- |
| **This Checkpoint PR (docs/contracts only)** | **HIGH review tier (policy/contract design)**; **docs-only validation** | Establishes cross-component scientific contracts, coordinate/time conventions, and third-party terms/redistribution boundaries before code; requires independent Dual-CLEAN review (`Sol` + `Opus`) per `AGENTS.md` |
| **Small-body contracts, interpolation, coordinate/EOP math, Horizons/SBDB acquisition & Observatory/layer UI** | **Tier C** (per `#1396`) | New shared numerical interpolation/projection math in `HVO.SkyMonitor.Astronomy`, durable state/SQLite acquisition in `CameraAgent.Common`, and cross-component UI/layer integration |
| **Meteor-shower snapshot loader & Observatory information card** | **Tier B** (promoted to **Tier C** if shared solar-longitude inversion / radiant-drift numerical helpers are added to `HVO.SkyMonitor.Astronomy`) | Pure informational card and bounded local snapshot; promoted to Tier C when adding shared numerical astronomy math |

### C.4 Follow-On Implementation Slices and Verification Matrix

Implementation proceeds only after this checkpoint merges and approval is
recorded on `#1396`:

1. **Slice S1 (Small-Body Executable Contracts & Frozen Fixtures — `HVO.SkyMonitor.Astronomy`):**
   - Implement `HVO.SkyMonitor.Astronomy.SmallBodies` contracts, canonical JSON
     serializer/validator (`hvo-small-body-ephemeris-v1`, `hvo-small-body-manifest-v1`),
     4-point centered cubic unit-vector interpolator, and EOP-aware/fallback
     `AltAzPoint` + `IImageProjector` evaluator.
   - Commit immutable offline golden fixtures under `tests/fixtures/small-bodies/v1/`
     containing at least:
     - **One comet case** (e.g., `1P/Halley` or a current bright periodic/long-period
       comet with `T-mag`/`N-mag`, non-gravitational model metadata, and `n.a.`
       or finite `QUANTITIES=36` uncertainty) with independent Horizons mid-knot
       reference `QUANTITIES=4` airless Alt/Az positions;
     - **One asteroid case** (e.g., `433 Eros` or a close-approaching NEA with
       finite `APmag` and finite `QUANTITIES=36` 3-sigma RA/Dec uncertainty)
       with independent Horizons mid-knot reference positions;
     - **Ambiguity fixture** (e.g., `73P` multi-apparition + fragments);
     - **Negative/edge fixtures:** wrong observer (`ObserverMismatch`), stale
       retrieval, expired/out-of-range query (refusing extrapolation), missing
       EOP (planning fallback vs measured rejection), leap-second crossing,
       horizon/obstruction clipping (`#1394`), corrupt SHA-256, and missing
       magnitude/uncertainty (`n.a.` → `null`, never `0.0`).
   - Verify $\le 1.0''$ interpolation residual with EOP (and documented bound
     under UTC≈UT1 planning fallback), exact round-trip canonical hashes, and
     equivalent host outputs.
2. **Slice S2 (Small-Body Acquisition, Store & Refresh — `CameraAgent.Common`):**
   - Implement `.small-bodies/` STRICT SQLite store, content-addressed raw/normalized
     snapshots, quarantine, rollback, serial JPL Horizons + SBDB + Lookup client
     (enforcing 1-in-flight, $\ge 1\text{ s}$ pacing, `User-Agent`, `signature.version`,
     no redirects/cookies, 6 h floor, persisted halts, last-known-good),
     `SmallBodyEphemerisRefresh` automation task, health check, and metrics.
   - Unit + integration tests with fake HTTP handler covering 200/403/429/5xx,
     timeout, version drift, ambiguous target, watchlist cap (32), observer-change
     invalidation, crash/restart reconciliation, offline replay by exact
     `manifestSha256`, and rollback.
3. **Slice S3 (Small-Body Presentation & UI — `Processing` + `CameraAgent`):**
   - Optional `PredictedSmallBodyLayer` (separate from `PredictedSatelliteTracks`),
     Software & Catalog status panel, Automations link, and Observatory
     location-specific small-body planning/layer toggle with explicit uncertainty,
     validity, and attribution display; label withholding when uncertainty
     exceeds threshold; bounded per-frame lookup/projection benchmark.
4. **Slice M1 (External Governance Gate + Meteor-Shower Executable Contracts & Fixtures):**
   - Record resolution of gate `M1` (written IMO confirmation if bundling annual
     IMO activity fields, or explicitly record MDC-only bundled baseline +
     operator-imported activity overlay).
   - Implement `HVO.SkyMonitor.Astronomy.MeteorShowers` contracts (`hvo-meteor-showers-v1`),
     solar-longitude window/wrap evaluator, radiant drift & geometric horizontal
     evaluator, and frozen fixtures under `tests/fixtures/meteor-showers/v1/`
     covering:
     - `Established`, `Working`, and `Removed` status filtering/tombstones;
     - Season/year boundaries (`URS` Dec 17–26, `QUA` Dec 28–Jan 12, vernal
       equinox $359^\circ \to 0^\circ$ wrap);
     - UTC vs site-local civil dates (including negative/positive UTC offsets
       crossing midnight and DST transitions);
     - Uncertain/broad/variable peaks and missing drift (`null` ≠ `0`);
     - Northern vs southern hemisphere sites and below-horizon radiants;
     - Absent, corrupt, and expired annual snapshots (no silent year recycling);
     - Optional `#1004` cloud evidence present vs absent (`Unknown`).
5. **Slice M2 (Meteor-Shower Local Snapshot Store & Observatory Card — `CameraAgent.Common` + `CameraAgent`):**
   - Implement verified local snapshot loader (bundled + explicit override/import,
     atomic pointer, last-known-good, zero scheduler/network dependency),
     Observatory optional meteor-shower information card with qualified wording
     and exact attribution, Software & Catalog read-only snapshot status, and
     browser screenshot verification proving informational wording and
     optional-layer-off behavior.
6. **Slice M3 (Optional Later Acceptance — Separately Togglable Radiant Labels):**
   - Optional Observatory sky-plot radiant labels with epoch/date/radiant-drift
     semantics and projection provenance (never forced on capture imagery; may
     be explicitly omitted while closing `#1396` with card-only delivery).

---

## Verification Evidence Recorded at This Checkpoint

- Live HTTPS verification performed 2026-11-26/27 UTC against:
  - `https://ssd-api.jpl.nasa.gov/doc/` (SSD Fair Use Policy verbatim)
  - `https://ssd-api.jpl.nasa.gov/doc/horizons.html` (v1.3 doc), `sbdb.html` (v1.3), `sbdb_query.html` (v1.0), `horizons_lookup.html` (v1.1)
  - `https://ssd-api.jpl.nasa.gov/sbdb.api` (`433 Eros`, `1P/Halley`)
  - `https://ssd.jpl.nasa.gov/api/horizons.api` (`433 Eros` OBSERVER & VECTORS; `1P/Halley` OBSERVER; `73P` ambiguity)
  - `https://ssd.jpl.nasa.gov/api/horizons_lookup.api` (`Halley`, `73P`)
  - `https://minorplanetcenter.net/data`, `https://minorplanetcenter.net/iau/MPCORB.html`, `https://docs.minorplanetcenter.net/mpc-ops-docs/orbits/comet-orbit-format/`, and `HEAD` size checks for `MPCORB.DAT.gz`, `mpcorb_extended.json.gz`, `nea_extended.json.gz`, `CometEls.txt`, `AllCometEls.txt`, and `Soft00Cmt.txt`
  - `https://www.iaumeteordatacenter.org/` and `ta3.sk` endpoint reachability probes
  - `https://www.imo.net/`, `https://www.imo.net/about/using-imo-data/`, `https://www.imo.net/calendar/`, `https://www.imo.net/meteor-shower-calendar-2027/`, `https://www.imo.net/resources/calendar/` (404), and `https://www.imo.net/members/imo_showers/working_shower_list/` (rebuild placeholder)
- Contract specifications frozen alongside this decision:
  - [`docs/contracts/small-body-ephemeris-v1.md`](../contracts/small-body-ephemeris-v1.md)
  - [`docs/contracts/meteor-shower-context-v1.md`](../contracts/meteor-shower-context-v1.md)
