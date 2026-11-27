# Optional Sky Layers Source and Contract Decision Checkpoint (#1396)

Date: 2026-11-27. Issue: [#1396](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1396)
(`Optional sky layers: comets/asteroids and meteor showers`, incorporating folded
[#1397](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1397) per the
2026-11-11 issue triage). Target branch: `development/v1`.

## Plain-Language Synopsis

- **Why now:** `#1396` (including folded `#1397`) gates all small-body and
  meteor-shower runtime implementation on a reviewed source, terms, numerical
  convention, bounded host-neutral contract, and approved immutable fixture
  checkpoint.
- **Outcome:** Freezes the authoritative source selection, primary-source license
  and redistribution boundaries (including explicit external gates `M1` and
  `M2`), physical/coordinate/time conventions, motion-sensitive interpolation
  and replay-pinning rules, strict aligned contract schemas with validated
  canonical JSON examples, live/computed reference vectors, and a strict
  fixture-before-code slice plan (`S0 -> S1`, `M0 -> M1`).
- **Benefit:** Prevents speculative N-body propagation, invalid SGP4 reuse,
  unapproved third-party data redistribution, PDF/HTML scraping, scheduler
  duplication, or production math/parser implementation ahead of approved
  immutable fixtures.
- **Unlocks:** Follow-on approved immutable fixture publication slices (`S0`,
  `M0`), followed by executable contract, acquisition/import, evaluator, and
  Observatory/Software & Catalog UI pull requests under `#1396`.
- **Not included:** Production C# implementation, runtime configuration changes,
  automated polling code, generalized feed/catalog/notification platforms,
  full minor-planet survey catalogs, individual meteor prediction, automatic
  meteor classification, or bundled third-party redistribution tables.

## Status and Authority

This document, together with the bounded host-neutral contracts
[`docs/contracts/small-body-ephemeris-v1.md`](../contracts/small-body-ephemeris-v1.md)
and [`docs/contracts/meteor-shower-context-v1.md`](../contracts/meteor-shower-context-v1.md),
constitutes **Part 1 of the First Checkpoint (Reviewed Source, Terms & Contract
Decision)** for `#1396` (`Refs #1396`).

Nothing in this PR adds production code, runtime configuration, network
requests, database migrations, or release redistribution of third-party data.
**`#1396` remains open** after this checkpoint merges.

**Strict First-Checkpoint Fixture-Before-Code Gate:** Issue `#1396`'s third
First Checkpoint requirement mandates publishing approved immutable input/output
fixtures and bounded host-neutral contracts **before** provider/parser or
production math implementation. Accordingly:

1. This PR publishes the reviewed source/terms decision, strict host-neutral
   contracts with programmatically validated RFC 8785 (JCS) canonical JSON
   examples, and strictly synthetic small-body reference vectors plus computed
   Quadrantids (`QUA`) geometric horizontal vectors (with live Horizons/SBDB
   wire and numerical parity verified locally against an uncommitted cache
   pending Gate `S0-Clearance`).
2. **Slice S0** (small bodies) and **Slice M0** (meteor showers) MUST publish
   and record approval of the full immutable offline golden input/output fixture
   sets (`tests/fixtures/small-bodies/v1/` and `tests/fixtures/meteor-showers/v1/`,
   with exact provenance, SHA-256 hashes, and verified terms/gate compliance)
   **strictly before** any production math, parser, or provider code in `src/`
   begins in Slice S1 or Slice M1. Until/unless primary JPL/Caltech reuse and
   redistribution terms are directly verified and recorded under Gate
   **`S0-Clearance`**, all repository-committed Slice S0 fixtures MUST use
   **strictly HVO-authored synthetic inputs** (with live Horizons/SBDB wire and
   numerical parity verified locally/on-demand against an uncommitted local
   cache rather than committing uncleared upstream headers or payloads).
3. Any release-bundled third-party meteor-shower fixture or snapshot remains
   additionally gated behind external redistribution gates **`M1`** (IMO) and
   **`M2`** (IAU MDC).

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
| `SB-01` | Small bodies | **JPL Horizons API** (`EPHEM_TYPE='OBSERVER'`, `CENTER='coord@399'`, `APPARENT='AIRLESS'`, `QUANTITIES='1,2,9,20,23,24,36'`) as primary bounded ephemeris source for an explicit operator watchlist | Approved (Pending Review Merge) for local runtime acquisition / **Unresolved Gate `S0-Clearance`** for committing verbatim upstream fixtures | Bounded to explicit watchlist ($\le 32$ bodies); local runtime caching only, no bulk redistribution or full minor-planet survey catalog; repository-committed S0 fixtures use strictly synthetic inputs unless Gate `S0-Clearance` records verified primary JPL/Caltech reuse terms |
| `SB-02` | Small bodies | **JPL SBDB API** (`sbdb.api` v1.3) and **Horizons Lookup API** (`horizons_lookup.api` v1.1) as authoritative object-identity, alias, disambiguation, and orbit-solution metadata companions | Approved (Pending Review Merge) for local runtime acquisition / **Unresolved Gate `S0-Clearance`** for committing verbatim upstream fixtures | Strict separation between canonical object identity (`spkid`, SBDB `object.des`) and orbit-solution identity (`orbit_id` / `soln_date`); mandatory Horizons ↔ SBDB solution match before pairing |
| `SB-03` | Small bodies | **MPC** (`CometEls.txt`, `AllCometEls.txt`, `MPCORB.DAT.gz`, `mpcorb_extended.json.gz`) **not selected** as primary propagation authority; **satellite GP/TLE/SGP4** explicitly prohibited | Approved (Pending Review Merge) | MPC comet exports may only serve as an optional external reference or future separately-qualified discovery aid |
| `SB-04` | Small bodies | **Horizons `VECTORS` / binary `SPK`** not selected for v1; retained as documented future multi-site extension (`REF_SYSTEM='ICRF'`, `REF_PLANE='FRAME'` required for equatorial ICRF) | Approved (Pending Review Merge) | Avoids vendoring a binary DAF/SPK parser and duplicating comet photometric/covariance projection in v1 |
| `SB-05` | Small bodies | **Topocentric unrefracted coordinates**: retain both astrometric ICRF (`Q=1`) and apparent true-of-date airless (`Q=2`) knots on UTC (`TIME_TYPE='UT'`, `CAL_TYPE='GREGORIAN'`), bound to exact `DeploymentLocation` | Approved (Pending Review Merge) | Site/observer mismatch fails closed (`ObserverMismatch`); refraction applied only at display/projection time via `AtmosphericRefraction` after `#1394` geometric horizon check |
| `SB-06` | Small bodies | **Local interpolation & error bound**: 4-point centered cubic Lagrange interpolation on 3D topocentric unit vectors ($S^2$) with 1-knot padding at both ends, **motion-sensitive step sizing** ($\omega_{\max} h \le 2.5^\circ$ over `stepSeconds ∈ {3600, 900, 300, 120, 60}` + distance-tier diurnal-parallax caps), and **mandatory independent half-step midpoint verification** (or rigorous analytical 4th-derivative remainder bound for closed-form fixtures); never interpolate `AltAz` directly | Approved (Pending Review Merge) | Verified midpoint accuracy $\le 1.0''$ required for every usable interval (integer-knot-only `2h`-stride checks are insufficient and cannot mark a series verified); refines step down to `60 s` or withholds any unverified or $> 1.0''$ interval with `InterpolationBoundExceeded`; never extrapolates beyond `[validFromUtc, validUntilUtc]` |
| `SB-07` | Small bodies | **Local Earth rotation, time inputs & replay pinning**: reuse existing bundled/refreshed `OrbitalTimeData` (`finals2000A.daily.extended` + `leap-seconds.list`) for UT1 + polar motion when covering the query instant; pin `manifestSha256`, `earthOrientationSha256`, `leapSecondsSha256`, and `timeDataBundleId` alongside retained evaluated outputs; explicit degraded `ut1-approximated-by-utc-v1` fallback allowed for planning only, never for measured astrometric association | Approved (Pending Review Merge) | Leap-second-crossing intervals explicitly withheld; exact recomputation fails closed with `TimeDataUnavailable` if pinned time-data bytes were pruned |
| `SB-08` | Small bodies | **Acquisition topology, rate policy & partial-refresh carry-forward**: one CameraAgent-local single-flight serial refresher per deployment via the existing local automation registry (`SmallBodyEphemerisRefresh`); no browser calls (NASA CORS), no second scheduler, $\ge 1\text{ s}$ inter-request pacing, 6 h floor, 24 h default cadence, persisted halts, per-body carry-forward on partial refresh, and all-failed last-known-good retention | Proposed (cadence/pacing defaults) / Approved (architecture, serial Fair Use & carry-forward rules) | Watchlist $\le 32$ objects; disabled by default until explicitly enabled and populated; carried-forward bodies retain their original retrieval/validity/staleness metadata, per-series producing `parserVersion`/`normalizerVersion`/API versions, unchanged `recordSha256`, and immutable `originManifestSha256` |
| `SB-09` | Small bodies | **Closed `SolarSystemBody` preserved**: new `HVO.SkyMonitor.Astronomy.SmallBodies` types and separate optional `PredictedSmallBodyLayer` in `Processing`; never extend `SolarSystemBody` enum with arbitrary body names | Approved (Pending Review Merge) | Distinguishes geometric position, uncertain predicted magnitude (`predictedTotalMagnitude`/`predictedNuclearOrSurface` + `magnitudeModel`), and measured detection; no coma/tail synthesis |
| `MS-01` | Meteor showers | **IAU Meteor Data Center (MDC) Shower Database (SD)** as authoritative source for shower identity (`IAU No.`, 3-letter `Code`, canonical `Name`), status (`Established` / `Working` / `Removed` / `Group`), mean geocentric radiant (J2000), peak $\lambda_\odot$, and $V_g$ | **Approved** for source authority & local schema / **Unresolved Gate `M2`** for release bundling | `ta3.sk` was unreachable during probes and MDC compiles externally published journal records; releases do **not** bundle MDC tables until `M2` verifies primary redistribution terms. Operators may explicitly import locally acquired MDC snapshots with SHA-256 verification; never polled automatically by CameraAgent |
| `MS-02` | Meteor showers | **IMO Meteor Shower Calendar / Working List** evaluated for annual activity context: **no live HTML scraping and no PDF parsing**; IMO policy allows attribution-bearing use in apps but requires contacting IMO before republishing a full calendar or standalone dataset | **Unresolved Gate `M1`** for bundled IMO redistribution; Approved for local schema & operator-import boundary | Bundling IMO-derived annual activity tables in HVO.SkyMonitor releases is gated on recorded written IMO confirmation (`M1`). Without `M1`, annual activity fields come only from explicit local operator import; MDC-only records leave `nominalActivity` start/end and ZHR `null` without fabrication |
| `MS-03` | Meteor showers | **No second scheduler and no dependency on satellite refresh**: bounded versioned local snapshot (`hvo-meteor-showers-v1`) distributed via explicit verified local override/import (`SnapshotPath` + `SnapshotSha256`) and (only after `M2` / `M1`) optional release-curated bundle | Approved (Pending Review Merge) | Zero network calls on Observatory, capture, or frame paths; last-known-good preserved on invalid import |
| `MS-04` | Meteor showers | **Solar-longitude anchor, edition-validity-first occurrence binding & exclusive annual expiry**: anchor physical windows to J2000.0 geocentric ecliptic solar longitude $\lambda_\odot \in [0^\circ, 360^\circ)$; enforce import-time `activitySource` vs `annualForecasts[]` edition/subset consistency (`editionId`, `editionYear`, `[forecast.validFromUtc, forecast.validUntilUtc) ⊆ [activitySource.validFromUtc, activitySource.validUntilUtc)`) and gate Stage 1 on intersected edition validity `max(activitySource.validFromUtc, forecast.validFromUtc) <= queryUtc < min(activitySource.validUntilUtc, forecast.validUntilUtc)` **before** occurrence selection and overlapping-edition precedence over `(editionId, editionYear, occurrenceYear, windowStartUtc, peakUtc, windowEndUtc)` | Approved (Pending Review Merge) | Mismatched `editionId`/`editionYear`, widened forecast validity, not-yet-valid (`queryUtc < effectiveValidFromUtc`), expired (`queryUtc >= effectiveValidUntilUtc`), and past (`windowEndUtc <= queryUtc`) entries are strictly excluded and never silently recycled; higher `editionYear` wins among valid entries for the same `(iauNumber, occurrenceYear)`; HVO-derived UTC conversions require a source-qualified activity peak (`nominalActivity.peakSolarLonDeg`) and are never derived from radiant reference longitude alone |
| `MS-05` | Meteor showers | **Observatory card required; radiant labels optional**: v1 requires an optional Observatory information card (active/upcoming showers, peak window/uncertainty, qualified idealized ZHR or explicit MDC-only identity/radiant wording, radiant altitude, Sun/Moon context, optional `#1004` cloud evidence where missing stays `Unknown`); separately togglable radiant labels are an optional later slice | Approved (Pending Review Merge) | Card-only delivery may close `#1396`'s meteor-shower slice when optional radiant labels are explicitly recorded as omitted; zero forced capture annotation |
| `MS-06` | Meteor showers | **Strict epistemic separation**: informational forecast context only; **prohibited** from automatic meteor classification, individual streak prediction, inferred shower membership from radiant proximity, or IMO/AMS fireball-catalog ingestion | Approved (Pending Review Merge) | Enforced by separate types, UI copy rules, and regression tests |

---

## Part A — Small-Body (Comets & Asteroids) Source and Contract Decision

### A.1 Live-Verified Source Comparison (Verified 2026-11-26/27)

All candidate URLs and policies listed in `#1396` were verified directly on
2026-11-26/27 using sequential HTTPS requests without credentials or cookies.

| Source & Product | Verified Endpoint & Version | Coverage & Physical Model | Measured Footprint / Cadence | Access, Terms, License & Attribution | Offline / Caching & Operational Fit | Decision |
| --- | --- | --- | --- | --- | --- | --- |
| **JPL Horizons API — `OBSERVER`** | `https://ssd.jpl.nasa.gov/api/horizons.api` (JSON `signature.version: "1.2"`, docs v1.3 2025 June; batch `horizons_file.api`) | All numbered/unnumbered asteroids and comets in JPL SSD/DASTCOM; full N-body DE441 + SB441-N16 small-body perturbers + non-gravitational comet forces (`A1,A2,A3,DT,S0`) + Sun relativistic light bending + ITRF93 topocentric parallax + JPL EOP + predicted `APmag`/`T-mag`/`N-mag` + propagated 3-sigma RA/Dec uncertainty (`Q=36`) | Measured local probe footprint (2026-11-26): 4 hourly CSV steps ≈ 14 KiB JSON; 14-day hourly table (339 rows) ≈ 40–50 KiB raw (~8–10 KiB gzip) per object. Watchlist of 32 objects ≈ 1.5 MiB/day raw. Orbits updated as new MPC/radar astrometry arrives | NASA/JPL SSD **Fair Use Policy** (`https://ssd-api.jpl.nasa.gov/doc/`): **strictly 1 request at a time**, cache/reuse locally, back off on errors, descriptive `User-Agent` (product, version, contact/project URL), **no browser/website embedding (NASA CORS policy)**, check `signature.version`, best-effort SLA. See Section A.2 for primary NASA/JPL-Caltech terms and local-caching vs redistribution boundaries | One HTTP request per watched body yields complete topocentric astrometric + apparent positions, range, elongation, phase, predicted magnitude, and 3-sigma sky uncertainty ready for offline cubic interpolation and replay. Observer-bound (must invalidate on site change) | **Selected primary ephemeris source (v1)** |
| **JPL Horizons API — `VECTORS`** | `https://ssd.jpl.nasa.gov/api/horizons.api` (`EPHEM_TYPE='VECTORS'`, `CENTER='500@399'`, `REF_SYSTEM='ICRF'`, **`REF_PLANE='FRAME'`**, `VEC_TABLE='2'`, `VEC_CORR='LT'` or `'LT+S'`) | Same DE441 + SB441-N16 + non-gravitational trajectory accuracy as `OBSERVER`, returning geocentric equatorial ICRF $(X,Y,Z,V_x,V_y,V_z)$. **Local live probe (2026-11-26/27, uncommitted cache):** omitting `REF_PLANE='FRAME'` defaults to `REF_PLANE='ECLIPTIC'` and reports an ecliptic-of-J2000.0 reference frame in the response header, whereas sending `REF_PLANE='FRAME'` reports equatorial ICRF. Any future `VECTORS` client MUST send `REF_PLANE='FRAME'` and verify the returned response header reports equatorial ICRF rather than ecliptic of J2000.0 (raw wire-line comparison runs locally/on-demand against the operator's cache) | Live sample (2026-11-26): 2–3 steps for `433 Eros` ≈ 6.7 KiB JSON; 14-day 6-hour table (59 rows) ≈ 12 KiB/object | Same NASA/JPL SSD Fair Use Policy, CORS prohibition, and Section A.2 terms | Observer-independent (ideal for multi-site fleet distribution) and supports cubic Hermite interpolation, **but omits predicted magnitudes (`T-mag`/`N-mag`/`APmag`), solar elongation, phase angle, and 3-sigma plane-of-sky uncertainty (`Q=36`)**, requiring either a second `OBSERVER` call or custom comet photometry + covariance projection | **Not selected for v1**; documented future multi-site extension |
| **JPL Horizons API — `SPK`** | `https://ssd.jpl.nasa.gov/api/horizons.api` (`EPHEM_TYPE='SPK'`, small bodies only) | NAIF SPICE binary DAF/SPK segments covering `START_TIME..STOP_TIME` | Tens of KiB binary per body | Same NASA/JPL SSD Fair Use Policy and Section A.2 terms; reading SPK requires vendoring or implementing a binary DAF/SPK Type 1/2/21 evaluator (or native CSPICE) | Unnecessary binary parser / native-library complexity for a $\le 32$-object watchlist; still lacks photometric magnitude and plane-of-sky 3-sigma uncertainty | **Rejected for v1** |
| **JPL SBDB API + Horizons Lookup API** | `https://ssd-api.jpl.nasa.gov/sbdb.api` (v1.3, 2021 Sept); `https://ssd-api.jpl.nasa.gov/sbdb_query.api` (v1.0); `https://ssd.jpl.nasa.gov/api/horizons_lookup.api` (v1.1, 2025 Mar) | Authoritative object metadata (`spkid`, SBDB `object.des`, Lookup `pdes`, `fullname`, `shortname`, `kind`, `des_alt`/`alias`), orbit-solution identity (`source`, `orbit_id`, `soln_date` in `America/Los_Angeles`, `epoch`, `first_obs`, `last_obs`, `data_arc`, `n_obs_used`, `condition_code`, `rms`, `not_valid_before`, `not_valid_after`, `model_pars`), physical parameters (`phys-par`), and ambiguity resolution (`73P` returned 74 distinct parent/fragment/epoch entries on 2026-11-26) | Live sample (2026-11-26): `433 Eros` = 8,309 B JSON; `1P/Halley` = 7,405 B JSON; `horizons_lookup` for `433` = 247 B JSON, `Halley` = 707 B JSON | Same NASA/JPL SSD Fair Use Policy (`https://ssd-api.jpl.nasa.gov/doc/`) and Section A.2 primary-source terms (noting `phys_par[].ref` and underlying astrometry derive from third-party literature/MPC contributors) | Ideal low-cost companion for object lookup, alias preservation, ambiguity detection, and orbit-solution provenance; osculating elements alone are **not** used as a local two-body propagator | **Selected identity & orbit-solution companion** |
| **IAU MPC Bulk Orbits & Comet Exports** | `https://minorplanetcenter.net/data`; `https://minorplanetcenter.net/iau/MPCORB/CometEls.txt` & `AllCometEls.txt`; `Soft00Cmt.txt`; `MPCORB.DAT.gz`; `mpcorb_extended.json.gz`; `nea_extended.json.gz`; format docs `https://docs.minorplanetcenter.net/mpc-ops-docs/orbits/comet-orbit-format/` | All MPC-published minor planets and comets as single-epoch osculating elements; standard 160-char comet export has **no non-gravitational `A1/A2/A3` parameters** and **no formal covariance matrix** (minor planets carry only integer `U` uncertainty code) | Live `HEAD` measurements (2026-11-26/27): `MPCORB.DAT.gz` = **94.88 MB**; `mpcorb_extended.json.gz` = **183.30 MB** (>1 GiB expanded); `nea_extended.json.gz` = **5.03 MB**; `AllCometEls.txt` = **789.5 KB**; `CometEls.txt` / `Soft00Cmt.txt` = **166.0 KB**; updated daily (NEAs multiple times/day) | Open public scientific archive under IAU/SAO; requested attribution snippet (`https://minorplanetcenter.net/data`): *"This research has made use of data and/or services provided by the International Astronomical Union's Minor Planet Center."* No formal SPDX license file published; treat bulk mirrors/redistribution carefully | `MPCORB` is a full minor-planet survey catalog (explicitly out of scope in `#1396`); single-epoch osculating elements cannot match Horizons/DE441 perturbed + non-gravitational comet accuracy without writing and qualifying a full N-body integrator | **Not selected as ephemeris/propagation engine**; optional external cross-check only |
| **Satellite GP / TLE / SGP4** | Existing `#527`/`#528`/`#529` satellite lane | Geocentric Earth-gravity/drag analytical mean-element theory (TEME) | N/A | N/A | Physically invalid for heliocentric ellipses, parabolas, hyperbolas, planetary perturbations, and comet non-gravitational accelerations | **Explicitly prohibited** |

### A.2 Exact Terms, Licenses, and Attribution (Small Bodies)

1. **NASA/JPL Solar System Dynamics (SSD) Fair Use Policy** (verified HTTP 200
   from `https://ssd-api.jpl.nasa.gov/doc/` on 2026-11-26):
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
2. **Primary-Source Data Terms, Copyright Nuances, and Redistribution Boundary:**
   - **Verified primary NASA policies (checked 2026-11-26/27):**
     - **NASA Media Usage Guidelines** (`https://www.nasa.gov/nasa-brand-center/images-and-media/`,
       HTTP 200 verified): U.S. Government-created NASA content is generally not
       subject to copyright in the United States (`17 U.S.C. § 105`) and may be
       used for educational and informational purposes in a factual manner that
       does not imply endorsement, without requiring prior permission. However,
       NASA insignia, logotypes, and identifiers are protected by law, and NASA
       explicitly warns that third-party copyright-protected material referenced
       or hosted by NASA does not convey rights to redistribute that third-party
       material.
     - **NASA Science Information Policy (`SPD-41a`)**
       (`https://science.nasa.gov/researchers/science-information-policy/`,
       HTTP 200 verified): NASA holds scientific research information, data, and
       software as a public trust under open-science principles.
   - **Caltech / JPL FFRDC distinction & third-party observation inputs:**
     - JPL is a Federally Funded Research and Development Center (FFRDC)
       operated for NASA by the California Institute of Technology (Caltech)
       under federal contract — it is not solely staffed by federal civil
       servants, so blanket "all JPL outputs are automatically `17 U.S.C. § 105`
       public domain" claims are **not** asserted here without verifying the
       specific JPL terms page. Both `https://www.jpl.nasa.gov/copyrights.php`
       and `https://www.jpl.nasa.gov/jpl-image-use-policy/` returned **HTTP 403**
       to non-browser HTTP clients on 2026-11-26/27.
     - Additionally, SBDB and Horizons orbit fits and physical-parameter tables
       incorporate third-party astrometric observations collated through the IAU
       Minor Planet Center (MPC) and published literature parameters (e.g.,
       `phys_par[].ref` citing `PDS3 (MPC 17258)`, `ICQ 2005 Handbook`, or
       external journal papers).
   - **Explicit Operational Caching vs Redistribution Boundary:**
     - **Local runtime acquisition & caching (Approved):** Each CameraAgent
       deployment queries Horizons/SBDB/Lookup directly on behalf of its
       operator for a bounded local watchlist ($\le 32$ objects) and caches the
       responses locally for offline computation, replay, and local backup.
       CameraAgent **never** mirrors, republishes, or exposes raw SBDB/Horizons
       responses as a public feed, API, or bundled release catalog.
     - **Repository immutable verification fixtures (Slice S0 — Gate `S0-Clearance`):**
       Because NASA/JPL SSD's policy (`https://ssd-api.jpl.nasa.gov/doc/`)
       governs API access and local caching rather than public repository
       redistribution, and both `https://www.jpl.nasa.gov/copyrights.php` and
       `https://www.jpl.nasa.gov/jpl-image-use-policy/` returned HTTP 403 to
       non-browser clients, **no blanket exemption is claimed** for committing
       verbatim JPL Horizons, SBDB, or Lookup query headers or responses into
       this repository. Repository publication of any verbatim upstream
       provider material (including Horizons query headers or raw JSON/CSV
       payloads) is explicitly gated on **`S0-Clearance`** — a recorded,
       directly reachable primary JPL/Caltech reuse/redistribution basis (or
       written JPL/Caltech clearance) verified prior to committing such files.
       Until/unless **`S0-Clearance`** is recorded, all repository-committed
       Slice S0 unit/contract verification fixtures under
       `tests/fixtures/small-bodies/v1/` MUST use **strictly HVO-authored
       synthetic inputs** (synthetic Horizons-format grammar/CSV/JSON payloads
       and closed-form analytical trajectories), while live-API wire-format
       and numerical parity are verified locally/on-demand against the
       developer's or operator's uncommitted local cache rather than
       committing uncleared upstream headers or payloads. Even if
       `S0-Clearance` is recorded later, committed fixtures **must never**
       bundle bulk MPC catalogs or reproduce third-party copyrighted
       literature text from `phys_par[].ref`. Any other redistribution of
       JPL/SBDB-derived data beyond bounded local runtime caching likewise
       remains gated on recorded primary JPL/Caltech clearance.
   - **Required UI & provenance attribution text:**
     > *"Small-body ephemerides and orbit metadata: NASA/JPL-Caltech Solar System Dynamics (Horizons / Small-Body Database). Displayed by HVO SkyMonitor; not endorsed by NASA, JPL, or Caltech."*
   - **Proposed `User-Agent`:**
     `HVO.SkyMonitor.CameraAgent/<version> (+https://github.com/HualapaiValley/HVO.SkyMonitor)`
     (build metadata stripped; no personal email address committed).
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
| **Center & Observer Binding** | Topocentric geodetic WGS84/ITRF93 site: `CENTER='coord@399'`, `COORD_TYPE='GEODETIC'`, `SITE_COORD='<lon_deg>,<lat_deg>,<alt_km>'` (east-positive longitude; `ObserverLocation.ElevationMeters / 1000.0`). Height is explicitly labelled `AssumedEllipsoidal` when no surveyed datum exists (matching `#527`/`#1393`) | Horizons `OBSERVER` applies exact ITRF93 topocentric parallax, critical for close-approaching NEAs (up to tens of arcminutes at lunar distances). Snapshot manifest binds `observerBindingSha256` (DeploymentLocation ID/version + exact lat/lon/elev). **Any site mismatch fails closed with `ObserverMismatch`**; changing site triggers cache invalidation and requires a fresh acquisition |
| **Coordinate Frames** | Retain **both** `QUANTITIES=1` (**topocentric astrometric RA/Dec in ICRF**, light-time corrected, no aberration/deflection/precession/refraction) and `QUANTITIES=2` (**topocentric apparent RA/Dec of true equator & equinox of date**, light-time + annual/diurnal stellar aberration + solar gravitational light deflection + IAU precession/nutation, **airless**) per knot (`ANG_FORMAT='DEG'`, `EXTRA_PREC='YES'`) | `Q=1` provides exact J2000/ICRF coordinates for catalog/WCS cross-reference; `Q=2` provides the exact true-of-date apparent direction for horizontal (`AltAzPoint`) and calibrated rig projection without reimplementing solar light-time iteration, aberration, or relativistic light bending |
| **Time Scale** | `TIME_TYPE='UT'`, `CAL_TYPE='GREGORIAN'`. Horizons `UT` is UTC post-1962; every stored knot timestamp is normalized UTC (`DateTimeOffset` with offset `0`). Orbit solution `epoch` in metadata is preserved explicitly in **TDB** (Julian Date `epochTdbJd`) and never conflated with UTC | Matches `HVO.SkyMonitor.Astronomy` UTC query contract while preserving JPL's exact TDB osculating epoch provenance. Leap-second-crossing intervals are explicitly withheld using `OrbitalTimeData` leap-table checks |
| **Atmospheric Refraction** | **Strictly unrefracted (`APPARENT='AIRLESS'`)** in retained ephemeris snapshots and geometric evaluation. Optional atmospheric refraction is applied **only** at projection/display time via existing `AtmosphericRefraction.Apply` (`bennett-1982-standard-atmosphere-v1`) **after** `#1394` geometric horizon-obstruction evaluation | Mandatory per `docs/contracts/horizon-obstruction-v1.md`: geometric horizon and site-obstruction masks must never receive refracted/apparent elevations; keeps small bodies consistent with stars, planets, and satellites |
| **Solar Ephemeris, EOP Inputs & Replay Pinning** | **Upstream ephemeris generation:** JPL Horizons DE441 planetary ephemeris, SB441-N16 perturbers, and JPL's active EOP file — exact header values (`planetaryEphemeris: "DE441"`, `smallBodyPerturbers: "SB441-N16"`, `horizonsEopFile`, and structured `horizonsEopCoverage: { dataBasedStartDate, dataBasedEndDate, predictsEndDate }` parsed from the Horizons EOP coverage header) are recorded in strict `SmallBodyOrbitSolution` provenance.<br>**Local horizontal conversion (`Q=2` → `AltAzPoint`):** Reuse existing `OrbitalTimeData` (`finals2000A.daily.extended` + `leap-seconds.list`) for exact UT1 (via DUT1), GAST (IAU equation of equinoxes), and polar motion $(x_p, y_p)$ when covering the query instant. If EOP is unavailable or uncovered, planning mode may fall back to `ut1-approximated-by-utc-v1` (via `AstronomyTime`) with explicit degraded provenance and an uncalibrated $\le 15''$ timing-basis caveat; **measured astrometric association rejects UTC≈UT1 fallback**.<br>**Replay pinning:** Because managed time-data retains only the active pair plus one intact predecessor (`docs/astronomy/bundled-time-data-v1.md`), every recorded evaluation/replay artifact pins `(manifestSha256, earthOrientationSha256, leapSecondsSha256, timeDataBundleId)` **and retains exact evaluated horizontal outputs**. Exact recomputation against pruned time-data bytes fails closed with `TimeDataUnavailable` (never silently substituting a newer EOP pair).<br>**Local Sun/Moon darkness context:** Reuse existing `IPlanetEphemeris` (`AstronomyEnginePlanetEphemeris`), `SolarAltitudeClassifier`, `SolarDiskEphemeris`, and `ISolarEventCalculator` | Avoids duplicating EOP storage or solar ephemeris models; guarantees sub-arcsecond agreement with Horizons `QUANTITIES=4` airless Alt/Az when EOP covers the instant while remaining fail-closed and auditable across time-data retirement |
| **Object Identity vs Orbit-Solution Identity (Provider Precedence & Cross-Check)** | **Object identity & field precedence:** `authority = "jpl-spk"`, canonical decimal `spkId` from SBDB `object.spkid` / Lookup `spkid` (resolving 7-digit historical asteroid SPK-IDs such as `2000433` to canonical 8-digit `20000433`), `bodyKind` (`Asteroid` / `Comet`), **`primaryDesignation` authoritatively from SBDB `object.des`** (e.g. `"433"`, `"1P"`, `"73P-C"` — never overwritten by Horizons Lookup `pdes`, which returns `"A898 PA"` for `433 Eros`; any differing Lookup `pdes` is preserved in `aliases[]`), optional `fragmentCode`.<br>**Aliases:** `fullName` (SBDB `object.fullname`), `shortName` (SBDB `object.shortname`, nullable), optional `mpcPackedDesignation`, and deduplicated ordinal-sorted `aliases[]` (SBDB `des_alt` + Lookup `alias[]` + differing `pdes`) — never used as primary key.<br>**Orbit-solution identity & timezone/partial-date rules:** `solutionAuthority` (`"JPL"`), `orbitId` (`"<source>#<orbit_id>"`, e.g. `"JPL#1"`), `horizonsRecordNumber`, verbatim `solutionDateSourceRaw` (`"YYYY-MM-DD HH:MM:SS"`) with explicit `solutionDateSourceTimeZone = "America/Los_Angeles"` plus deterministically converted `solutionDateUtc` (nullable if an ambiguous fall-back wall time occurs), `epochTdbJd` (from matched SBDB `orbit.epoch`), **unparsed partial-date strings** `firstObsDateRaw` / `lastObsDateRaw` (matching `^\d{4}-(\d{2}\|\?\?)-(\d{2}\|\?\?)$`, e.g. `"1919-??-??"`, never parsed as strict ISO timestamps), `dataArcDays`, `observationCount`, `conditionCode` (0–9 or `null`), `residualRmsArcsec`, `magnitudeModel`, `nonGravitationalModel`, `notValidBeforeUtc`, `notValidAfterUtc`.<br>**Mandatory Horizons ↔ SBDB match:** Before attaching SBDB metadata to a Horizons `OBSERVER` series, canonical `spkId`, Horizons `{source: <AUTHORITY>#<ID>}` vs `<sbdb.orbit.source>#<sbdb.orbit.orbit_id>`, and Horizons `Soln.date:` vs SBDB `orbit.soln_date` MUST match; mismatch fails closed with `SolutionMetadataMismatch` | Local 2026-11-26 probes confirmed `433 Eros` returns SBDB `object.des="433"` vs Lookup `pdes="A898 PA"`, `soln_date` in JPL Pasadena `America/Los_Angeles` local time, partial observation dates in SBDB (`1919-??-??`), and `73P` matching **74** Horizons records across apparition epochs and fragments (`73P-A`..`73P-BT`). Ambiguous queries fail closed with `AmbiguousDesignation`; comet fetches pin `DES=<spkid>;CAP;NOFRAG` (or exact fragment `spkid` + `CAP`) |
| **Validity Interval & Extrapolation** | Every per-object ephemeris carries `[validFromUtc, validUntilUtc]` (the usable knot window, excluding the 1-knot padding at each end) plus any SBDB `notValidBeforeUtc`/`notValidAfterUtc`. Queries outside the intersection **never extrapolate** and return explicit `OutOfRange` / `Expired` / `OutsideSourceValidity` | Prevents silent polynomial blow-up or unvalidated orbital projections beyond fetched bounds |
| **Covariance & Uncertainty Availability** | Parse `QUANTITIES=36` (`ra3SigmaArcsec`, `dec3SigmaArcsec`) per knot and `(+COV)` header presence; retain SBDB `conditionCode`, `dataArcDays`, and `solutionDateUtc`. Local 2026-11-26 probes confirmed well-observed asteroids return finite 3-sigma values while `1P/Halley` returns `n.a., n.a.`. Missing/unavailable values **must remain `null`** with explicit `UncertaintyStatus = Unavailable` — **never coerced to `0.0`**. Label withholding rule (Proposed): if `max(ra3Sigma, dec3Sigma) > 1800"` ($0.5^\circ$) or projects to $> 10\text{ px}$ in the active rig, display an explicit large-uncertainty warning or withhold the point label | Preserves scientific honesty when comet outgassing or short discovery arcs lack formal covariance or carry large sky-plane uncertainty |
| **Predicted Magnitude vs Detection** | Retain `QUANTITIES=9` (`predictedTotalMagnitude` = `APmag`/`T-mag`, `predictedNuclearOrSurface` = `S-brt`/`N-mag`) per knot plus explicit `magnitudeModel` metadata (`AsteroidHG`: `H`/`G`; `CometM1K1M2K2`: `M1`/`K1`/`M2`/`K2`/`PHCOF`; or `Unknown`) on `SmallBodyOrbitSolution` as **uncertain predicted values** (`null` when `n.a.`). Field names match [`small-body-ephemeris-v1.md`](../contracts/small-body-ephemeris-v1.md) identically. Products carry **no `Visible` or `Detected` boolean**, no coma/tail synthesis, and no image-identification claim | Satisfies `#1396` requirement separating geometric position, uncertain predicted magnitude/activity, and measured camera detection |
| **Interpolation & Error Bounds (Motion-Sensitive + Independent Half-Step Verified)** | **4-point centered cubic Lagrange interpolation** on 3D unit vectors $\hat{\mathbf{u}}(t) \in S^2$ (re-normalized to unit length) over uniform UTC knots, applied separately to `Q=1` (ICRF) and `Q=2` (apparent of date), and scalar cubic/linear on range $\Delta$, elongation, phase, and magnitude (returning `null` if any stencil knot is `null`). Request **1 extra padding knot before `validFromUtc` and 1 after `validUntilUtc`** so every instant in `[validFromUtc, validUntilUtc]` uses a full centered 4-point stencil.<br>**Combined distance-tier + motion-sensitive step rule** over supported uniform `stepSeconds ∈ {3600, 900, 300, 120, 60}`:<br>1. Distance-tier ceiling (bounds diurnal parallax): $\Delta_{\min} \ge 0.2\text{ AU} \to h \le 3600\text{ s}$ (14 d, 339 knots); $0.01 \le \Delta_{\min} < 0.2\text{ AU} \to h \le 900\text{ s}$ (3 d, 291 knots); $0.002 \le \Delta_{\min} < 0.01\text{ AU} \to h \le 300\text{ s}$ (24 h, 291 knots); $\Delta_{\min} < 0.002\text{ AU} \to$ `CloseApproachUnsupported` in v1.<br>2. **Motion-sensitive angular-step cap:** enforce $\omega_{\max} \cdot h \le 2.5^\circ$ ($0.04363\text{ rad}$, where $\omega_{\max}$ is maximum apparent angular speed $\approx v_{\perp,\max}/\Delta_{\min}$ or observed $\max_k \angle(\hat{\mathbf{u}}_{k+1},\hat{\mathbf{u}}_k)/h$), selecting the largest supported $h \in \{3600, 900, 300, 120, 60\}\text{ s}$ satisfying **both** caps (bounding stored knots $\le 1024$: e.g. 24 h at `120 s` = 723 knots; 12 h at `60 s` = 723 knots).<br>3. **Mandatory independent half-step midpoint verification (integer-knot-only checks prohibited as proof):** An integer-knot-only `2h`-stride check misses oscillatory perturbations such as $2'' \sin(\pi t/h)$, which vanish at every integer knot (`0.000000"` at $k=3$) yet produce a `2.000000"` error at every usable midpoint $t_{k+1/2}$. Therefore, every usable interval $[t_k, t_{k+1}]$ MUST be verified $\le 1.0''$ by either: (a) **`HalfStepSample`** — fetching Horizons at half-step resolution $h/2$ (one HTTP request returning both retained $h$-spaced knots and independent $t_{k+1/2}$ midpoint samples for `Q=1` and `Q=2`) and verifying $\max(\angle_{\text{astrom}}, \angle_{\text{app}}) \le 1.0''$ at every usable midpoint; or (b) **`AnalyticalDerivativeBound`** (closed-form analytical fixtures only) — proving $\frac{3}{128} h^4 \sup \|\mathbf{u}^{(4)}\| \le 1.0''$. If any usable interval exceeds `1.0"`, acquisition refines to the next smaller `stepSeconds` (down to `60 s`) and re-verifies; **any interval lacking verified midpoint accuracy evidence (`Unverified`) or still exceeding `1.0"` is explicitly withheld with `InterpolationBoundExceeded`**.<br>**Verified benchmarks:**<br>• *Fast flyby ($\Delta_{\min}=0.002\text{ AU}$, $v_\perp=75\text{ km/s}$, $\omega_{\max}=51.7048''/\text{s}$, closest approach at $t=0$, aligned knot grid $t_k = k \cdot h$):* At `h = 300 s`, aligned-grid midpoint max is **`1.115471"`** and aligned-grid continuous max ($u \in [0, 1]$ on fixed aligned grid) is **`1.115503"`** (shifting the knot-grid phase off $t=0$ reaches all-phase supremum **`1.115682"`**, e.g. `1.115680"` at $t_k = 4.06\,h, u = 0.498$ — rejected as `> 1.0"` across all phases). Motion-sensitive cap selects **`h = 120 s` (`2 m`)**, yielding **`0.028812"`** aligned-grid midpoint max, **`0.028813"`** aligned-grid continuous max, and **`0.028818"`** all-phase supremum — well inside `<= 1.0"` across all phases.<br>• *Oscillatory counterexample ($2'' \sin(\pi t/h)$ on 7 knots):* integer-knot `2h`-stride reports `0.000000"`, while mandatory half-step verification catches **`2.000000"`** at $t=1.5h$ and withholds the interval.<br>• *Synthetic `999999 Synthetic Demo` Section 3.4 example (1 h analytical knots vs exact `2026-11-27T00:30:00Z` midpoint):* exact analytical midpoint residual **`0.000030341575"`** (`0.000029546627"` astrometric / `0.000029396657"` apparent from stored 9-dp JSON knots) → recorded conservatively as **`0.000031"`** (live Horizons/SBDB parity runs locally against an uncommitted cache pending Gate `S0-Clearance`) | Eliminates zenith/azimuth-wrap singularities (never interpolating `AltAz`), eliminates boundary-order drops (1-knot padding), catches oscillatory inter-knot errors, and guarantees verified $\le 1.0''$ midpoint accuracy or fail-closed withholding |
| **Staleness, Refresh & Per-Body Carry-Forward** | **Freshness vs Validity kept distinct:**<br>• Retrieval freshness warning (`Stale`): per-series `seriesRetrievedUtc` age $> 72\text{ h}$ for comets/NEAs or $> 168\text{ h}$ for numbered main-belt/outer asteroids (Proposed defaults).<br>• Ephemeris validity (`Expired` / `OutOfRange`): `now` outside `[validFromUtc, validUntilUtc]` or outside SBDB `notValidBeforeUtc..notValidAfterUtc`.<br>• **Refresh policy:** Disabled by default. Single-flight durable task (`SmallBodyEphemerisRefresh`) registered in CameraAgent's existing local automation store (same seam as `OrbitalElementsRefresh` and `SpaceWeatherRefresh`). Default interval 86,400 s (24 h), hard floor 21,600 s (6 h), max 604,800 s (7 d). Sequential requests only, $\ge 1,000\text{ ms}$ inter-request delay, 30 s per-request timeout, no redirects, no cookies, no automatic retry loops. HTTP 403 or 3 consecutive 429s trigger a persisted provider halt requiring explicit operator resume.<br>• **Unambiguous partial-refresh rule (Per-Body Carry-Forward with Per-Series Provenance):** If **some** watchlist bodies refresh cleanly and others fail, the new atomic manifest **carries forward** each failed body's prior valid `SmallBodyEphemerisSeries` for the **same** `observerBindingSha256` verbatim — preserving its original `seriesRetrievedUtc`, `[validFromUtc, validUntilUtc]`, `solution`, **per-series producing provenance (`parserVersion`, `normalizerVersion`, `horizonsApiVersion`, `sbdbApiVersion`, `lookupApiVersion`)**, exact unchanged `recordSha256`, and resulting `freshness`, while setting `carriedForward: true` and pinning immutable `originManifestSha256` to the originating manifest's `manifestSha256` — and recording the failed attempt in `failedRefreshObjects[]` (`carryForwardApplied: true`). Thus a v2 refresh that refreshes Eros and carries Halley forward from a v1 manifest preserves Halley's exact v1 producing parser/normalizer provenance and origin manifest SHA-256. If **zero** bodies succeed (total transport failure, halt, or candidate quarantine), **no new manifest is activated** and the existing last-known-good manifest remains active untouched. Historical snapshots remain readable by exact `manifestSha256` | Strictly adheres to JPL SSD Fair Use Policy, eliminates partial-failure vs last-known-good ambiguity, preserves exact per-body producing-tool provenance across upgrades and carry-forwards, never masks staleness, and mirrors the proven `#528`/`#1398` durable store, quarantine, halt, and rollback guarantees |

---

## Part B — Meteor-Shower (Folded `#1397`) Source and Contract Decision

### B.1 Live-Verified Source Qualification (Verified 2026-11-26/27)

| Source | Live Verification (2026-11-26/27) | Authoritative Scope | Status Handling & Data Model | Terms, Redistribution & Maintainability | Decision |
| --- | --- | --- | --- | --- | --- |
| **IAU Meteor Data Center (MDC) — Shower Database (SD)** | `https://www.iaumeteordatacenter.org/` returned HTTP 200 (12,868 B). Linked `ta3.sk` database hosts (`https://ceres.ta3.sk/iaumdcdb`, `https://ceresiaumdc.ta3.sk`, `https://www.ta3.sk/IAUC22DB/MDC2022/`) experienced connection timeouts and `Errno 65` (no route) during live probes | Official central registry of meteor shower names, IAU numbers, 3-letter codes, shower groups, and published mean geocentric radiant / orbit / solar-longitude datasets (maintained under IAU Commission F1 & WG on Meteor Shower Nomenclature). Explicit MDC caveat: MDC is a registry of published showers, not an authority certifying observational quality or real-time activity | Five official lists:<br>1. **Established** (IAU General Assembly approved)<br>2. **Working List** (published in peer-reviewed astronomy journals, *WGN*, or *eMeteorNews*, awaiting confirmation)<br>3. **Removed** (duplicates, spurious, or removed per published criteria)<br>4. **All Showers** (all non-removed)<br>5. **Shower Groups** (complexes).<br>Coordinates are J2000.0 equatorial mean geocentric radiant $(\alpha_g, \delta_g)$, peak solar longitude $\lambda_\odot$, and geocentric velocity $V_g$ (km/s). **Note:** MDC records do not always supply active start/end solar-longitude bounds or ZHR | MDC compiles shower records published across external astronomy journals and literature, and the `ta3.sk` database host was unreachable during live probes, so **no primary redistribution license file could be verified**. Cite IAU MDC (Commission F1, `https://www.iaumeteordatacenter.org/`) plus MDC export date and reference papers. **Slow/intermittent institutional host (`ta3.sk`) also makes automated per-deployment live polling operationally unsound** | **Selected authoritative identity & radiant source with External Gate `M2`**:<br>1. **Never** poll `ta3.sk` automatically from CameraAgent.<br>2. **Gate `M2` (IAU MDC redistribution confirmation):** until primary MDC redistribution license terms or written IAU MDC confirmation are verified and recorded, HVO.SkyMonitor releases **do NOT bundle** MDC redistribution tables.<br>3. Operators may still explicitly import locally acquired, SHA-256-verified MDC snapshots (`LocalOperatorImport`) without fabricating missing activity bounds |
| **International Meteor Organization (IMO) — Meteor Shower Calendar & Working List** | Legacy URL `https://www.imo.net/resources/calendar/` returned **HTTP 404**; `/members/imo_showers/working_shower_list/` returned a rebuild placeholder (*"This IMO tool is being rebuilt"*). Current live pages are `https://www.imo.net/calendar/` (2026/2027 HTML calendar), `https://www.imo.net/meteor-shower-calendar-2027/` (37th ed., ed. Jürgen Rendtel), `https://www.imo.net/files/data/calendar/cal2027.pdf`, and `https://www.imo.net/about/using-imo-data/` (Last-Modified 2026-11-23) | Annual observing guidance: active date/$\lambda_\odot$ intervals, predicted peak date/time/$\lambda_\odot$ (including annual filament/outburst notes), idealized zenithal hourly rate (`ZHR`), population index (`r`), radiant drift, and lunar-interference commentary | Calendar year explicit (e.g., 2026, 2027); predictions revised annually with occasional corrections. **No public structured JSON/CSV API** exists; only human-readable web pages and annual PDFs | **Verbatim policy (`https://www.imo.net/about/using-imo-data/`):**<br>• **Freely permitted:** use, reproduce, and share data from the Meteor Shower Calendar and Working List of Visual Meteor Showers (including in apps/services) with credit (`Source: International Meteor Organization (IMO) – www.imo.net`, direct link `https://www.imo.net`, edition/year), no endorsement/logo, keeping derived values clearly distinct from IMO values, and keeping data reasonably up to date.<br>• **Contact IMO first before:** reproducing the Meteor Shower Calendar or other IMO publications in full or in substantial part, OR **republishing an IMO dataset in full as a standalone file, database or API**, or reusing third-party photos/articles.<br>• **Fireball reports excluded:** Not owned by IMO (collected with allsky.org / American Meteor Society); explicitly excluded | **Qualified with External Gate `M1`**:<br>1. **Never** scrape HTML or treat `calYYYY.pdf` as a live structured API.<br>2. Before any HVO.SkyMonitor release bundles an IMO-derived annual activity table, obtain and record written IMO confirmation (`M1`) covering a bounded subset bundled in the offline app.<br>3. Until `M1` (and `M2` for bundled MDC identity) are recorded, releases bundle neither unverified third-party table, and operators may explicitly import locally curated, schema-validated snapshots (either MDC-only or MDC + annual activity) |
| **Third-party community mirrors / historical fireball catalogs (AMS / IMO VMDB / SonotaCo / RMOB)** | Checked `https://www.imo.net/resources/databases/` (SonotaCo J14, VMDB, VISDAT, IMONET, MSSWG, RMOB) | Observational archives of past individual meteors/fireballs, not authoritative shower nomenclature or general-purpose annual observing forecasts | N/A | Separate licenses/ownership (e.g., AMS owns fireball reports; individual authors own articles/catalogs) | **Explicitly excluded from v1** (`#1397`: *"Historical fireball catalogs are observations and are not required here"*) |

### B.2 Status Handling, Time Conventions, Drift, Uncertainty, and Epistemic Rules

1. **IAU MDC Status Handling:**
   - `Established`: Included by default in the Observatory meteor-shower card
     and optional radiant overlay.
   - `Working`: Stored with explicit `status: "Working"` badge and caveat
     (*"IAU Working List — not yet officially established"*); hidden by default
     unless the operator explicitly enables **"Show working-list showers"**.
   - `Removed`: Never listed as active or plotted as a radiant; retained only as
     a compact tombstone record (`iauNumber`, `iauCode`, `formerName`,
     `removedNote`, optional `supersededByIauNumber`) so explicit lookups
     explain why a code was retired.
   - `Group`: Preserved only as optional `showerGroupId` grouping metadata.
2. **MDC-Only Baseline & Separation of Radiant/Nominal Metadata from Annual Activity:**
   - `nominalActivity` on `MeteorShowerRecord` is **optional/nullable** and each
     subfield (`peakSolarLonDeg`, `activeStartSolarLonDeg`, `activeEndSolarLonDeg`)
     is individually nullable and source-qualified (`source`: `"IAU-MDC"`,
     `"AnnualActivitySource"`, or `"OperatorCurated"`).
   - **No peak date from radiant reference longitude alone:** A locally derived
     nominal peak date (`DerivedFromSolarLongitudeUtc`) may be emitted **only**
     when a source-qualified activity peak (`nominalActivity.peakSolarLonDeg`,
     or a valid annual forecast entry) is present — **never** from
     `referenceRadiant.referenceSolarLongitudeDeg` alone. When `nominalActivity`
     is `null` or `nominalActivity.peakSolarLonDeg` is `null` (and no valid
     annual forecast exists), `referenceRadiant.referenceSolarLongitudeDeg` is
     strictly the radiant reference longitude and no peak date is emitted.
   - **Card mode driven strictly by annual activity forecast state:** The
     Observatory card enters Annual Activity Mode whenever `activitySource != null`
     and a valid non-expired `annualForecasts` entry matches the current/upcoming
     occurrence — **even when `nominalActivity` is `null` or omits solar-longitude
     bounds**. When `activitySource == null` (MDC-only baseline), evaluators
     **must never fabricate** active window start/end bounds or placeholder ZHR
     values; the card displays shower identity, reference radiant / site geometric
     radiant altitude, an optional locally derived nominal reference date **only**
     when `nominalActivity.peakSolarLonDeg` is present, and explicit wording:
     *"Activity window and ZHR unavailable (identity/radiant snapshot only — no
     annual activity source loaded)."*
3. **Time Convention, Solar Longitude, Edition-Validity-First Occurrence Binding, and Exclusive Expiry:**
   - Physical activity intervals (when supplied) and nominal peaks are stored in
     **J2000.0 geocentric ecliptic solar longitude** $\lambda_\odot \in [0^\circ, 360^\circ)$
     with explicit circular wrap handling across $360^\circ / 0^\circ$ (vernal
     equinox) and across calendar-year rollovers (e.g., **Ursids `URS`** Dec
     17–26 and **Quadrantids `QUA`** Dec 28–Jan 12).
   - Each shower carries `annualForecasts[]` (0..2 entries sorted by
     `occurrenceYear` ascending, then `windowStartUtc` ascending). Every annual
     forecast entry binds an explicit **occurrence window**:
     - `editionId` (non-empty string matching `activitySource.editionId`, e.g. `"imo-calendar-2027"`)
     - `editionYear` (calendar edition year matching `activitySource.editionYear`, e.g. `2026`, `2027`)
     - `occurrenceYear` (calendar year of the shower's nominal peak, e.g. `2027`
       for the Dec 28, 2026 – Jan 12, 2027 Quadrantids)
     - `windowStartUtc`, `peakUtc` (nullable), `windowEndUtc` (exclusive end of
       that specific occurrence, strictly `> windowStartUtc`)
     - Edition validity `[validFromUtc, validUntilUtc)` — typically
       `(YYYY-1)-12-01T00:00:00Z` through `(YYYY+1)-01-15T00:00:00Z` — evaluated
       strictly as **half-open** `validFromUtc <= queryUtc < validUntilUtc`.
   - **Mandatory import-time `activitySource` vs `annualForecasts[]` consistency & two-stage runtime rules:**
     - **Import-time invariant:** Reject any snapshot where `activitySource == null`
       with non-empty `annualForecasts`, where any forecast has
       `forecast.editionId != activitySource.editionId` or
       `forecast.editionYear != activitySource.editionYear`, or where
       `[forecast.validFromUtc, forecast.validUntilUtc)` is not a subset of
       `[activitySource.validFromUtc, activitySource.validUntilUtc)`.
     - **Stage 1 (Edition identity & intersected-validity gate FIRST):** Verify
       `activitySource != null`, matching `editionId`/`editionYear`, and the
       explicit window intersection
       `max(activitySource.validFromUtc, forecast.validFromUtc) <= queryUtc < min(activitySource.validUntilUtc, forecast.validUntilUtc)`
       **before** occurrence selection (both `Active` and `Upcoming`) and
       **before** overlapping-edition precedence. Thus querying on
       **`2026-11-30T00:00:00Z`** with a 30-day lookahead strictly excludes the
       canonical 2027 edition (`validFromUtc = 2026-12-01T00:00:00Z`) even
       though `windowStartUtc = 2026-12-28T00:00:00Z` falls within 30 days, and
       defensively rejects any mismatched forecast window such as
       `forecast.validFromUtc = 2026-11-01` under `activitySource.validFromUtc =
       2026-12-01`.
     - **Stage 2 (Occurrence binding & overlapping-edition precedence on valid entries):**
       - An occurrence with `windowEndUtc <= queryUtc` is **never** returned as
         active or upcoming, even if its parent edition's `validUntilUtc` is still
         in the future (e.g., querying on **`2027-01-03T12:00:00Z`** with only a
         2026 edition present does **not** surface the January 2026 Quadrantids
         occurrence whose `windowEndUtc = 2026-01-13T00:00:00Z`).
       - When multiple Stage-1-valid editions cover the same `(iauNumber, occurrenceYear)`
         within the active/lookahead window (e.g., querying on **`2026-12-28T00:00:00Z`**
         with a 30-day lookahead when both 2026 and 2027 editions are available),
         the **higher `editionYear`** takes precedence — selecting the January 2027
         Quadrantids occurrence from the **2027 edition**.
       - Once **`queryUtc >= validUntilUtc`** (exact exclusive upper boundary),
         annual activity forecasts are explicitly marked **`Expired`** and **never
         silently recycled for a later year**; static IAU MDC identity/radiant
         records remain separately inspectable with an explicit `"Annual activity
         forecast expired — no current-year forecast loaded."` notice.
   - Peak timestamps distinguish:
     - **`SourceCalendarUtc`**: explicit annual peak UTC (and/or peak window
       `[peakWindowStartUtc, peakWindowEndUtc]`) provided by a verified annual
       activity source;
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
       J2000 solar longitude**.
   - **Peak uncertainty & confidence (aligned identically with [`meteor-shower-context-v1.md`](../contracts/meteor-shower-context-v1.md)):**
     Represented explicitly via nullable `peakUncertaintyHours` and
     `peakUncertaintySolarLongitudeDeg`, `peakProfile` (`Sharp`, `Broad`,
     `Variable`, `OutburstSuspected`, `Unknown`), and `activityConfidence`
     (`NominalAnnual`, `VariableOrUncertain`, `ModelPrediction`, `Unknown`).
     Missing uncertainty stays `null` / `Unknown`, never `0`.
   - **UTC and site-local display:** All contract timestamps are UTC. Observatory
     cards display both site-local civil date/time (via existing `SiteTimeZone`
     IANA rules) and UTC reference, clearly labelling local observing nights
     (e.g., *"Night of Dec 13–14 local"*) and flagging DST boundaries cleanly.
4. **Radiant Altitude, Explicit RA Degrees-to-Hours Conversion, Drift, and Optional Radiant Labels:**
   - Each shower stores reference geocentric radiant $(\alpha_0, \delta_0)$ in
     J2000.0 **degrees** at reference solar longitude $\lambda_{\odot,0}$ plus
     optional linear drift rates $(\Delta\alpha/\Delta\lambda_\odot,
     \Delta\delta/\Delta\lambda_\odot)$ (degrees per degree of solar longitude,
     nullable).
   - At instant $t$ with solar longitude $\lambda_\odot(t)$, when drift rates
     are present and $t$ lies inside a supplied active $\lambda_\odot$ window,
     the drifted J2000 radiant is evaluated via circular signed offset
     $\Delta\lambda_\odot = \text{wrap}_{[-180,180)}(\lambda_\odot(t) - \lambda_{\odot,0})$
     and clamped cleanly at poles; when drift is `null` or no active window is
     supplied, the peak radiant is used with explicit
     `radiantDriftBasis: "FixedPeakRadiantDriftUnavailable"`.
   - **Mandatory unit conversion at `HVO.SkyMonitor.Astronomy` handoff:**
     `EquatorialPoint` (`src/HVO.SkyMonitor.Astronomy/Projection.cs`) stores
     `RightAscensionHours` in **hours** `[0, 24)`, not degrees. Callers MUST
     convert **`raHours = raDeg / 15.0`** before invoking
     `EquatorialPrecession.PrecessJ2000(new EquatorialPoint(raDeg / 15.0, decDeg), utc)`
     followed by `CoordinateTransforms.EquatorialToHorizontal` (unrefracted
     geometric altitude, consistent with `SolarAltitudeClassifier`). See the
     frozen Quadrantids (`QUA`, `230.0° -> 15.333333333 h`, `+49.0°`) golden
     horizontal vector in [`meteor-shower-context-v1.md`](../contracts/meteor-shower-context-v1.md#52-radiant-position-explicit-ra-unit-conversion-drift-and-site-visibility).
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
5. **Local Darkness, Moon, and Optional `#1004` Cloud Context:**
   - Reuses existing `ISolarEventCalculator` (`AstronomyEngineSolarEventCalculator`)
     and `SolarAltitudeClassifier` for site darkness/astronomical twilight
     windows (`Sun <= -18°`).
   - Reuses existing `SolarDiskEphemeris` for Moon topocentric geometric
     altitude and illuminated fraction during the shower's local dark window.
   - Consumes optional `#1004` `cloud-assessment-v1` evidence only when a recent
     valid assessment exists for the site; **missing or unquantified cloud
     evidence remains explicitly `Unknown`** (never defaulted to clear!).
6. **Refresh / Import Policy (Zero Second Scheduler, Zero Satellite Dependency):**
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
     2. Otherwise, an optional release-bundled verified snapshot beside the
        application (`meteor-showers/snapshot.json` + `provenance.json`),
        **strictly gated on Gate `M2`** for bundled IAU MDC identity/radiant
        records and **Gate `M1`** for bundled IMO-derived annual activity
        fields. Before those gates are recorded, no third-party meteor-shower
        table is bundled in releases.
   - Completely independent of `OrbitalElementsRefresh`, `TimeDataRefresh`,
     `SpaceWeatherRefresh`, and `SmallBodyEphemerisRefresh`.
7. **Strict Epistemic & Wording Boundaries:**
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
| `HVO.SkyMonitor.AgentCore` | **Unchanged** (reuses existing `HorizonObstructionProfile`, `CameraRigConfig`, `FrameReadoutDescriptor`, and deployment-location/capture identities) | No ephemeris, shower, HTTP, SQLite, or provider types |
| `HVO.SkyMonitor.Astronomy` | Owns existing `ObserverLocation` (`VisibleScene.cs`), `AltAzPoint`, `EquatorialPoint`, `EquatorialPrecession`, `CoordinateTransforms`, `PixelPoint`, `ProjectionContext`, `IImageProjector`, `ProjectorFactory`, `RigProjectionContextFactory`, `AstrometricMapping`, `AtmosphericRefraction`, `HorizonObstructionEvaluator`, `OrbitalTimeData`, `IPlanetEphemeris`, `SolarAltitudeClassifier`, `SolarDiskEphemeris`, `ISolarEventCalculator`, and `SiteTimeZone`. Follow-on slices add pure host-neutral `SmallBodies` (`SmallBodyIdentity`, `SmallBodyOrbitSolution`, `SmallBodyEphemerisSnapshot`, `ISmallBodySnapshotReader`, `SmallBodyInterpolator`, `SmallBodyPathEvaluator`) and `MeteorShowers` (`MeteorShowerIdentity`, `MeteorShowerSnapshot`, `IMeteorShowerSnapshotReader`, `MeteorShowerSolarLongitudeEvaluator`, `MeteorShowerRadiantEvaluator`) | **Never extend closed `SolarSystemBody` enum**; no ASP.NET, EF Core, SQLite, HTTP, filesystem, or SkiaSharp dependencies; no SGP4 reuse |
| `HVO.SkyMonitor.Processing` | Optional separate presentation/layer contracts (`PredictedSmallBodyLayer`, optional `MeteorRadiantOverlayLayer`), strict separation from `PredictedSatelliteTracks` and `TransientObservation` | Never mutates raw/calibrated detector inputs; never attaches shower-membership labels to measured transients |
| `HVO.SkyMonitor.Catalog.Sqlite` | **Unchanged** (`HYG` / `OpenNGC` remain read-only static star/DSO snapshots) | Do not mix dynamic small-body ephemerides or annual meteor-shower calendars into HYG/OpenNGC SQLite packages |
| `HVO.SkyMonitor.CameraAgent.Common` | Dedicated isolated stores under `<RawIngressRoot>/.small-bodies/` (SQLite STRICT + WAL + content-addressed immutable snapshots/quarantine, mirroring `.orbital-elements/`) and `<RawIngressRoot>/.meteor-showers/` (plain verified files + atomic `current.json` pointer, mirroring `.time-data/`). Single-flight `SmallBodyEphemerisRefresh` task registered via existing `LocalAutomationTaskKind` periodic seam; provider HTTP client (no redirects, no cookies, serial pacing, version check, persisted halt, per-body carry-forward); health & metrics; `MeteorShowerSnapshotLoader` | **No second scheduler**; **no generalized feed platform**; **no network or filesystem I/O inside capture or per-frame processing**; **zero dependency on satellite refresh or space-weather jobs** |
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
  eligibility through `HorizonObstructionEvaluator` (`HorizonObstructionProfile`)
  before optional display refraction. Unknown terrain is never treated as clear sky.
- **`#1004` (Cloud Assessment):**
  Observatory small-body planning and meteor-shower cards may read an existing
  recent `cloud-assessment-v1` fact as optional context; missing, stale, or
  unquantified cloud evidence remains explicitly **`Unknown`**.
- **`#523` (Measured Calibration):**
  Configured-rig projection (`RigProjectionContextFactory` + `ProjectorFactory.Create`)
  is the baseline for planning and optional overlays. Claims using accepted
  measured astrometric calibration follow the exact existing
  `OrbitalGeometryFacts.ValidateMeasuredEvidence` pattern
  (`src/HVO.SkyMonitor.Astronomy/Orbital/OrbitalPredictionFacts.cs`): validate
  the accepted `AstrometricCalibration` + `AstrometricFrameAssessment` matching
  the capture and observer, construct the measured `ProjectionContext` via
  **`AstrometricMapping.Projection(calibration, assessment)`**, project the
  EOP-computed horizontal `AltAzPoint` via
  **`ProjectorFactory.Create(measuredProjection).Project(horizontal)`** —
  **never** via `AstrometricMapping.SkyToPixel` (which expects mean-of-date
  equatorial coordinates and runs its own non-EOP horizontal transform) — and
  validate the prediction's non-zero EOP provenance separately (rejecting
  UTC≈UT1 fallback).
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
| **Slices S0 & M0 (Approved Immutable Golden Fixture Publication Gates)** | **HIGH review tier (contract & reference fixture freeze)** | Completes `#1396`'s First Checkpoint immutable fixture publication gate strictly before any `src/` math/parser code |
| **Small-body contracts, interpolation, coordinate/EOP math, Horizons/SBDB acquisition & Observatory/layer UI (`S1`–`S3`)** | **Tier C** (per `#1396`) | New shared numerical interpolation/projection math in `HVO.SkyMonitor.Astronomy`, durable state/SQLite acquisition in `CameraAgent.Common`, and cross-component UI/layer integration |
| **Meteor-shower snapshot loader & Observatory information card (`M1`–`M3`)** | **Tier B** (promoted to **Tier C** if shared solar-longitude inversion / radiant-drift numerical helpers are added to `HVO.SkyMonitor.Astronomy`) | Pure informational card and bounded local snapshot; promoted to Tier C when adding shared numerical astronomy math |

### C.4 Follow-On Implementation Slices and Verification Matrix

Implementation obeys the **strict fixture-before-code gate** from `#1396`. No
production parser, provider, or math code in `src/` starts before its preceding
immutable fixture publication slice (`S0` or `M0`) is reviewed, merged, and
explicitly approved on `#1396`:

1. **Slice S0 (First-Checkpoint Completion — Small-Body Approved Immutable Fixture & Golden Reference Publication Gate; zero `src/` edits):**
   - **Gate `S0-Clearance` compliance:** Until/unless a directly reachable
     primary JPL/Caltech reuse/redistribution basis (or written clearance) is
     verified and recorded under **Gate `S0-Clearance`**, all committed fixtures
     under `tests/fixtures/small-bodies/v1/` MUST use **strictly HVO-authored
     synthetic inputs** (zero uncleared verbatim JPL Horizons/SBDB/Lookup
     headers or raw wire payloads; live-API wire-format and numerical parity are
     verified locally/on-demand against an uncommitted local cache). Even if
     `S0-Clearance` is recorded later, fixtures must never bundle bulk MPC
     mirrors or third-party copyrighted literature text from `phys_par[].ref`.
   - Publish and obtain recorded approval on `#1396` for immutable offline test
     fixtures under `tests/fixtures/small-bodies/v1/` (with exact provenance,
     SHA-256 manifest, and verified **strict RFC 8785 (JCS)** canonical JSON
     serialization, including the frozen JCS edge-case test vectors):
     - **Asteroid contract & parser fixture** (finite `APmag`, finite
       `QUANTITIES=36` 3-sigma RA/Dec uncertainty, `AsteroidHG` model, parsed
       `horizonsEopFile` + structured `horizonsEopCoverage`, independent
       half-step `HalfStepSample` midpoint verification);
     - **Comet contract & parser fixture** (`T-mag`/`N-mag`, `CometM1K1M2K2` +
       non-gravitational model metadata, `n.a.` `QUANTITIES=36` → `null`, never
       `0.0`, independent half-step midpoint verification);
     - **Fast close-approach rectilinear/NEA analytical test vector**
       ($\Delta_{\min}=0.002\text{ AU}$, $v_\perp=75\text{ km/s}$, closest
       approach at $t=0$, aligned knot grid $t_k = k \cdot h$) proving `h = 300 s`
       exceeds `1.0"` (`1.115471"` aligned midpoint / `1.115503"` aligned
       continuous max / `1.115682"` all-phase supremum, e.g. `1.115680"` at
       $t_k = 4.06\,h, u = 0.498$) and motion-sensitive `h = 120 s` passes across
       all phases (`0.028812"` aligned midpoint / `0.028813"` aligned continuous
       max / `0.028818"` all-phase supremum);
     - **Oscillatory inter-knot counterexample fixture** ($2'' \sin(\pi t/h)$ on
       7 knots) proving integer-knot-only `2h`-stride checks yield `0.000000"`,
       while mandatory independent half-step verification catches `2.000000"` and
       withholds the interval (`InterpolationBoundExceeded`), plus an `Unverified`
       series fixture proving intervals lacking midpoint verification evidence
       fail closed;
     - **Ambiguity, provider-precedence & mixed-version carry-forward fixtures:**
       multi-apparition + fragment ambiguity (`AmbiguousDesignation`), SBDB
       `object.des` vs Lookup `pdes` precedence, `America/Los_Angeles` `soln_date`
       (PST, PDT, and ambiguous fall-back → `solutionDateUtc: null`), partial
       observation dates (`1919-??-??`), Horizons/SBDB solution-ID mismatch, and
       a v2 manifest refreshing one body while carrying another forward from a v1
       manifest (preserving v1 per-series `parserVersion`/`normalizerVersion`,
       unchanged `recordSha256`, and `originManifestSha256`);
     - **Negative/edge fixtures:** wrong observer (`ObserverMismatch`), stale
       retrieval, expired/out-of-range query (refusing extrapolation), pruned
       time-data replay (`TimeDataUnavailable`), missing EOP (planning fallback
       vs measured rejection), leap-second crossing, horizon/obstruction clipping
       (`#1394`), corrupt SHA-256, and `n.a.` → `null` (never `0.0`).
   - **Gate rule:** Production code in Slice S1 MUST NOT start until Slice S0
     merges and approval is explicitly recorded on `#1396`.
2. **Slice S1 (Small-Body Executable Contracts, Parser & Math — `HVO.SkyMonitor.Astronomy`):**
   - Implement `HVO.SkyMonitor.Astronomy.SmallBodies` contracts, **strict
     RFC 8785 (JCS)** canonical JSON serializer/validator
     (`hvo-small-body-ephemeris-v1`, `hvo-small-body-manifest-v1` — explicitly
     avoiding raw default `System.Text.Json` / `JavaScriptEncoder.Default`
     divergences and passing all frozen JCS test vectors), 4-point centered cubic
     unit-vector interpolator with motion-sensitive step enforcement +
     **mandatory independent half-step midpoint verification** (withholding any
     unverified or $> 1.0''$ interval), EOP-aware/fallback `AltAzPoint`
     evaluator, and
     `ProjectorFactory.Create(AstrometricMapping.Projection(calibration, assessment))`
     measured projection path.
   - Verify all Slice S0 fixtures offline: verified $\le 1.0''$ midpoint
     residual with EOP, exact round-trip canonical hashes, fail-closed
     `TimeDataUnavailable` replay, and equivalent host outputs.
3. **Slice S2 (Small-Body Acquisition, Store & Refresh — `CameraAgent.Common`):**
   - Implement `.small-bodies/` STRICT SQLite store, content-addressed raw/normalized
     snapshots, quarantine, rollback, serial JPL Horizons + SBDB + Lookup client
     (enforcing 1-in-flight, $\ge 1\text{ s}$ pacing, `User-Agent`, `signature.version`,
     no redirects/cookies, 6 h floor, persisted halts, Horizons↔SBDB solution
     matching, per-body carry-forward on partial failure, and all-failed
     last-known-good retention), `SmallBodyEphemerisRefresh` automation task,
     health check, and metrics.
   - Unit + integration tests with fake HTTP handler covering 200/403/429/5xx,
     timeout, version drift, solution mismatch, partial-refresh carry-forward,
     all-failed preservation, ambiguous target, watchlist cap (32), observer-change
     invalidation, crash/restart reconciliation, offline replay by exact
     `(manifestSha256, earthOrientationSha256, leapSecondsSha256)`, and rollback.
4. **Slice S3 (Small-Body Presentation & UI — `Processing` + `CameraAgent`):**
   - Optional `PredictedSmallBodyLayer` (separate from `PredictedSatelliteTracks`),
     Software & Catalog status panel, Automations link, and Observatory
     location-specific small-body planning/layer toggle with explicit uncertainty,
     validity, and NASA/JPL-Caltech attribution display; label withholding when
     uncertainty exceeds threshold; bounded per-frame lookup/projection benchmark.
5. **Slice M0 (First-Checkpoint Completion — Meteor-Shower External Gates `M1`/`M2` + Approved Immutable Fixture Publication Gate; zero `src/` edits):**
   - Record disposition of **Gate `M2`** (IAU MDC redistribution confirmation for
     any bundled MDC snapshot) and **Gate `M1`** (written IMO confirmation for
     any bundled annual activity fields), or explicitly record operator-import-only
     delivery using synthetic/public-domain reference fixtures for CI tests.
   - Publish and obtain recorded approval on `#1396` for immutable offline
     fixtures under `tests/fixtures/meteor-showers/v1/` (without redistributing
     ungated third-party datasets) covering:
     - Both annual-activity snapshots (including a valid annual forecast with
       `nominalActivity: null`, verifying the card stays in Annual Activity Mode)
       and **MDC-only baseline** snapshots (`activitySource: null`,
       `nominalActivity: null` verifying NO derived peak date is emitted from
       `referenceRadiant.referenceSolarLongitudeDeg` alone, plus
       `nominalActivity.peakSolarLonDeg` present without fabricated start/end
       bounds, `annualForecasts: []`);
     - `Established`, `Working`, and `Removed` status filtering/tombstones;
     - Occurrence-window, edition-validity-first, `activitySource`/forecast
       consistency, & year-boundary cases (`URS` Dec 17–26, `QUA` Dec 28–Jan 12,
       vernal equinox $359^\circ \to 0^\circ$ wrap, `2026-11-30` with 30-day
       lookahead excluding the 2027 edition whose `validFromUtc = 2026-12-01`
       and admitting it at `2026-12-01`, `2027-01-03` with 2026 edition only,
       `2026-12-28` lookahead with 2026 + 2027 editions, exact exclusive expiry
       `queryUtc == validUntilUtc`, and import-time + Stage-1 rejection of
       mismatched `editionId`/`editionYear` or `forecast.validFromUtc =
       2026-11-01` under `activitySource.validFromUtc = 2026-12-01` at
       `queryUtc = 2026-11-30`);
     - Frozen radiant horizontal golden vectors (`QUA` `230.0° -> 15.333333333 h`
       plus an `RA < 24°` case catching missing `/ 15.0`);
     - UTC vs site-local civil dates (crossing midnight and DST transitions),
       uncertain/broad/variable peaks, missing drift (`null` ≠ `0`), northern vs
       southern hemisphere sites and below-horizon radiants, absent/corrupt/expired
       snapshots, and optional `#1004` cloud evidence present vs absent (`Unknown`).
   - **Gate rule:** Production code in Slice M1 MUST NOT start until Slice M0
     merges and approval is explicitly recorded on `#1396`.
6. **Slice M1 (Meteor-Shower Executable Contracts & Evaluators — `HVO.SkyMonitor.Astronomy`):**
   - Implement `HVO.SkyMonitor.Astronomy.MeteorShowers` strict contracts
     (`hvo-meteor-showers-v1`), J2000 solar-longitude window/wrap evaluator,
     occurrence-window + overlapping-edition selector, and radiant drift +
     explicit `raDeg / 15.0` geometric horizontal evaluator, verified offline
     against all Slice M0 fixtures.
7. **Slice M2 (Meteor-Shower Local Snapshot Store & Observatory Card — `CameraAgent.Common` + `CameraAgent`):**
   - Implement verified local snapshot loader (explicit override/import +
     gate-compliant optional bundled snapshot, atomic pointer, last-known-good,
     zero scheduler/network dependency), Observatory optional meteor-shower
     information card with qualified ZHR wording, MDC-only wording, and exact
     attribution, Software & Catalog read-only snapshot status, and browser
     screenshot verification proving informational wording and optional-layer-off
     behavior.
8. **Slice M3 (Optional Later Acceptance — Separately Togglable Radiant Labels):**
   - Optional Observatory sky-plot radiant labels with epoch/date/radiant-drift
     semantics and projection provenance (never forced on capture imagery; may
     be explicitly omitted while closing `#1396` with card-only delivery).

---

## Verification Evidence Recorded at This Checkpoint

- Live HTTPS verification performed 2026-11-26/27 UTC (sequential, 1 request at
  a time, descriptive `User-Agent`, no credentials/cookies) against:
  - `https://ssd-api.jpl.nasa.gov/doc/` (SSD Fair Use Policy verbatim, HTTP 200)
  - `https://www.nasa.gov/nasa-brand-center/images-and-media/` (NASA Media Usage Guidelines, HTTP 200)
  - `https://science.nasa.gov/researchers/science-information-policy/` (NASA SPD-41a Science Information Policy, HTTP 200)
  - `https://www.jpl.nasa.gov/copyrights.php` and `https://www.jpl.nasa.gov/jpl-image-use-policy/` (both HTTP 403 to non-browser clients — recorded transparently; no unverified blanket public-domain claim made)
  - `https://ssd-api.jpl.nasa.gov/doc/horizons.html` (v1.3 doc), `sbdb.html` (v1.3), `sbdb_query.html` (v1.0), `horizons_lookup.html` (v1.1)
  - `https://ssd-api.jpl.nasa.gov/sbdb.api` (HTTP 200, v1.3: verified `object.des`, `orbit_id`, Pasadena local-time `soln_date`, and partial observation-date formats on local probes)
  - `https://ssd.jpl.nasa.gov/api/horizons_lookup.api` (HTTP 200, v1.1: verified `pdes` vs SBDB `object.des` divergence on `433 Eros`, canonical `spkid`, and multi-record ambiguity on `73P`)
  - `https://ssd.jpl.nasa.gov/api/horizons.api` (HTTP 200, v1.2: verified locally against uncommitted cache without committing verbatim headers, EOP strings, or live knots pending Gate `S0-Clearance`):
    - Verified `OBSERVER` CSV response structure, `Q=1,2,9,20,23,24,36` fields, finite vs `n.a.` 3-sigma uncertainty handling, `{source: <AUTHORITY>#<ID>}` / `Soln.date:` cross-check alignment with SBDB, and sub-milliarcsecond half-step cubic interpolation agreement on an asteroid (`433 Eros`) and comet (`1P/Halley`, `DES=1000036;CAP;NOFRAG`);
    - Verified `VECTORS` confirms `REF_SYSTEM='ICRF'` alone reports an ecliptic-of-J2000.0 reference frame in the response header while sending `REF_SYSTEM='ICRF'` + `REF_PLANE='FRAME'` reports equatorial ICRF (raw wire-line comparison runs locally/on-demand against the operator's cache);
    - Verified `73P` ambiguity (74 matching records)
  - `https://minorplanetcenter.net/data`, `https://minorplanetcenter.net/iau/MPCORB.html`, `https://docs.minorplanetcenter.net/mpc-ops-docs/orbits/comet-orbit-format/`, and `HEAD` size checks for `MPCORB.DAT.gz`, `mpcorb_extended.json.gz`, `nea_extended.json.gz`, `CometEls.txt`, `AllCometEls.txt`, and `Soft00Cmt.txt`
  - `https://www.iaumeteordatacenter.org/` (HTTP 200) and `ta3.sk` endpoint reachability probes (timeout / `Errno 65` — recorded under Gate `M2`)
  - `https://www.imo.net/`, `https://www.imo.net/about/using-imo-data/`, `https://www.imo.net/calendar/`, `https://www.imo.net/meteor-shower-calendar-2027/`, `https://www.imo.net/files/data/calendar/cal2027.pdf` (page 4 Quadrantids: active Dec 28–Jan 12, max Jan 4 `03:25 UTC`, $\lambda_\odot = 283.15^\circ$, nominal ZHR `80`, observed variation `~60–200`, timing uncertainty `~2.5 h` / `0.1°`), `https://www.imo.net/resources/calendar/` (404), and `https://www.imo.net/members/imo_showers/working_shower_list/` (rebuild placeholder — recorded under Gate `M1`)
- Programmatic numerical & schema checks verified at this checkpoint:
  - Fast close-approach flyby ($\Delta_{\min}=0.002\text{ AU}$, $v_\perp=75\text{ km/s}$, closest approach at $t=0$, aligned knot grid $t_k = k \cdot h$, verified at 65-digit decimal precision): `h = 300 s` gives `1.115471"` aligned midpoint max, `1.115503"` aligned continuous max, and `1.115682"` all-phase supremum (`1.115680"` at $t_k = 4.06\,h, u = 0.498$ — all $> 1.0"$); motion-sensitive cap selects `h = 120 s` giving `0.028812"` aligned midpoint max, `0.028813"` aligned continuous max, and `0.028818"` all-phase supremum (all well below `1.0"`). Oscillatory counterexample $2'' \sin(\pi t/h)$ on 7 knots gives `0.000000"` integer-knot `2h`-stride vs `2.000000"` half-step caught and rejected.
  - Strictly synthetic `999999 Synthetic Demo` Section 3.4 small-body example: exact analytical midpoint residual `0.000030341575"` (`0.000029546627"` astrometric / `0.000029396657"` apparent from stored 9-dp JSON knots), conservatively recorded as `maxVerifiedMidpointResidualArcsec = 0.000031`, with zero verbatim Horizons/SBDB headers or live knots committed.
  - Quadrantids (`QUA`, `raDeg = 230 -> 15.333333333 h`, `decDeg = +49`) at HVO (`35.5599378°N, -113.9119818°E`) verified against `HVO.SkyMonitor.Astronomy` exact IAU 1976 precession + GMST/LMST horizontal transforms: at HVO pre-dawn dark-window instant `2027-01-04T11:00:00Z`, `altitudeDeg = 35.029920°`, `azimuthDeg = 50.936604°`; at the 2027 IMO peak instant `2027-01-04T03:25:00Z`, `altitudeDeg = -5.127134°`, `azimuthDeg = 353.840761°` (`RadiantBelowHorizon`).
  - All 5 strict canonical JSON examples across both contracts and all 6 frozen RFC 8785 (JCS) canonicalization edge-case vectors (`0.000001`, `-0.0`, `1000`, `"A+B"`, `1e-7`, and combined object) validated programmatically for strict schema compliance (`additionalProperties: false`), `activitySource` vs `annualForecasts[]` consistency (import-time + Stage-1 runtime intersection), and bit-for-bit **strict RFC 8785 (JCS)** UTF-8 byte sequences, byte lengths, and SHA-256 digests.
- Contract specifications frozen alongside this decision:
  - [`docs/contracts/small-body-ephemeris-v1.md`](../contracts/small-body-ephemeris-v1.md)
  - [`docs/contracts/meteor-shower-context-v1.md`](../contracts/meteor-shower-context-v1.md)
