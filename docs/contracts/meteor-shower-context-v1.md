# Meteor-Shower Context Contract v1 (#1396 / folded #1397)

Status: design checkpoint contract under the
[#1396 source and contract decision](../planning/sky-layers-source-decision-1396.md),
dated 2026-11-27. This document defines the bounded host-neutral data snapshot,
provenance, solar-longitude and radiant evaluation semantics, Observatory card
contract, optional radiant-label boundary, and strict epistemic prohibitions for
meteor-shower information. It does not claim that any runtime implementation
exists yet.

This contract provides **informational observing context only**. A meteor-shower
forecast **cannot** predict individual meteors or establish that a captured
streak belongs to a meteor shower.

## 1. Ownership and Architectural Boundaries

- **`HVO.SkyMonitor.Astronomy` (`HVO.SkyMonitor.Astronomy.MeteorShowers`)** owns
  the canonical snapshot/provenance schemas, parser/validator, reader interface,
  solar-longitude activity evaluator, and geometric radiant altitude/drift
  evaluator. It reuses `ObserverLocation`, `AltAzPoint`, `EquatorialPoint`,
  `EquatorialPrecession`, `CoordinateTransforms`, `SiteTimeZone`,
  `IPlanetEphemeris`, `SolarAltitudeClassifier`, `SolarDiskEphemeris`,
  `ISolarEventCalculator`, and `HorizonObstructionEvaluator` (`#1394`). It has
  no HTTP, SQLite, filesystem, ASP.NET, or SkiaSharp dependencies.
- **`HVO.SkyMonitor.CameraAgent.Common`** owns the verified local snapshot
  loader (release-bundled + explicit verified override/import under
  `<RawIngressRoot>/.meteor-showers/`), atomic pointer activation,
  last-known-good retention, and read-only Observatory/Software & Catalog
  projection services. **No background scheduler, no live network poller, and no
  dependency on `OrbitalElementsRefresh` or `SmallBodyEphemerisRefresh`.**
- **`HVO.SkyMonitor.CameraAgent`** owns the optional Observatory information
  card/list, read-only Software & Catalog snapshot status, and optional later
  radiant-label toggle.
- **`HVO.SkyMonitor.Processing`** is untouched for card-only v1 delivery; if the
  optional radiant-label presentation slice is implemented later, it adds a
  separately togglable presentation payload strictly separate from
  `TransientObservation` and `PredictedSatelliteTracks`.

## 2. Source Governance and Attribution Contract

Every snapshot carries explicit machine-readable provenance (`hvo-meteor-shower-provenance-v1`)
separating identity authority from optional annual activity authority:

1. **Identity & Radiant Authority — IAU Meteor Data Center (MDC) Shower Database:**
   - Source URL: `https://www.iaumeteordatacenter.org/`
   - Required provenance fields: `mdcExportDate`, `mdcSourceReference`,
     `mdcRawSha256` (when curated from an export), and required citation text:
     > *"Meteor shower nomenclature and reference radiants: IAU Meteor Data Center (MDC) Shower Database, IAU Commission F1 (https://www.iaumeteordatacenter.org/)."*
2. **Optional Annual Activity / Calendar Authority (e.g., IMO or Operator-Imported Curated Source):**
   - **No PDF parsing or HTML scraping at runtime.**
   - **External Redistribution Gate `M1`:** Under IMO's verbatim `"Using IMO Data"`
     policy (`https://www.imo.net/about/using-imo-data/`, verified 2026-11-27),
     republishing an IMO dataset in full as a standalone file/database/API or
     reproducing the Meteor Shower Calendar in substantial part requires
     contacting IMO first. Therefore, any release-bundled snapshot containing
     IMO-derived activity fields requires a recorded written IMO confirmation
     reference (`redistributionApprovalRef`) in `provenance.json`. Without `M1`,
     bundled snapshots carry IAU MDC identities/radiants only, and annual
     activity fields are supplied only through an explicit local operator import.
   - Required attribution when IMO data is displayed:
     > *"Source: International Meteor Organization (IMO) – www.imo.net (IMO Meteor Shower Calendar <YYYY>). Displayed by HVO SkyMonitor; not endorsed by the IMO. Site altitude, darkness, Moon, and solar-longitude UTC conversions are computed locally by HVO SkyMonitor."*
   - **Prohibited sources:** IMO/allsky.org/American Meteor Society fireball
     report catalogs are excluded (different ownership, observational records,
     not shower forecasts).

## 3. Bounded Versioned Snapshot Schema (`hvo-meteor-showers-v1`)

### 3.1 Top-Level Snapshot Envelope

| Field | JSON Type / Format | Required Semantics |
| --- | --- | --- |
| `schemaVersion` | string (`"hvo-meteor-showers-v1"`) | Exact schema version |
| `snapshotId` | string (1–64 chars) | Human-readable version slug (e.g. `"iau-mdc-2026-activity-2027-r1"`) |
| `snapshotSha256` | 64-char uppercase hex | SHA-256 of canonical snapshot JSON with `snapshotSha256` excluded |
| `generatedUtc` | UTC ISO-8601 string | Curation/build timestamp |
| `identitySource` | object | IAU MDC provenance (name, URL, export date, citation, optional raw SHA-256) |
| `activitySource` | object or `null` | Annual calendar/activity provenance (`null` when snapshot contains IAU MDC identity/radiant data only): `providerName`, `editionYear` (integer), `sourceUrl`, `attributionText`, `termsUrl`, `redistributionApprovalRef` (required for bundled third-party data), `validFromUtc`, `validUntilUtc` |
| `showers` | array of `MeteorShowerRecord` ($\le 1,500$ entries, sorted by `iauNumber` ascending) | Active/established/working shower definitions |
| `removedShowers` | array of `RemovedMeteorShowerTombstone` ($\le 1,500$ entries, sorted by `iauNumber` ascending) | Tombstones for IAU MDC Removed showers |

**Hard validation ceilings (Proposed defaults):**
- Maximum snapshot JSON file size: **2 MiB**; `provenance.json` $\le 64\text{ KiB}$.
- Reject duplicate `iauNumber`, duplicate active `iauCode`, nonfinite numbers,
  `NaN`/`Infinity`, out-of-range coordinates (`RA` $\notin [0, 360)$, `Dec`
  $\notin [-90, 90]$, $\lambda_\odot \notin [0, 360)$), unknown JSON properties,
  or nesting $> 64$ levels.

### 3.2 `MeteorShowerRecord`

| Field | JSON Type / Format | Required Semantics |
| --- | --- | --- |
| `iauNumber` | integer $> 0$ | Authoritative IAU MDC shower number (e.g., `7` for Perseids, `4` for Geminids, `10` for Quadrantids) |
| `iauCode` | string (3 uppercase ASCII letters) | Canonical IAU 3-letter code (`"PER"`, `"GEM"`, `"QUA"`, `"URS"`) |
| `name` | string (1–96 chars) | Canonical IAU shower name |
| `status` | enum (`"Established"`, `"Working"`) | IAU MDC list membership (`Removed` belongs exclusively in `removedShowers`) |
| `showerGroupId` | string or `null` | Optional IAU MDC shower group/complex identifier |
| `mdcDatasetRef` | string or `null` | IAU MDC `AdNo` / bibliographic dataset identifier |
| `referenceRadiant` | object | J2000.0 geocentric radiant at `referenceSolarLongitudeDeg`: `raDeg` `[0, 360)`, `decDeg` `[-90, 90]`, `referenceSolarLongitudeDeg` `[0, 360)`, `equinox: "J2000"` |
| `radiantDrift` | object or `null` | Optional linear drift per degree of solar longitude: `dRaDegPerSolarLonDeg`, `dDecDegPerSolarLonDeg`. `null` means unknown drift (never silently treated as zero without labelling) |
| `geocentricVelocityKmPerSec` | finite number $> 0$ or `null` | Mean geocentric velocity $V_g$ (km/s) |
| `parentBodyName` | string or `null` | Informational parent-body text only (e.g. `"109P/Swift-Tuttle"`); never used for orbit computation |
| `nominalActivity` | object | Circular J2000.0 solar-longitude interval: `activeStartSolarLonDeg` `[0, 360)`, `peakSolarLonDeg` `[0, 360)`, `activeEndSolarLonDeg` `[0, 360)` (handles wrap where `start > end`, e.g. `270° → 292°` or `355° → 15°`) |
| `annualForecast` | object or `null` | Present only when `activitySource` is non-null and covers this shower for `editionYear`:<br>• `calendarYear` (integer)<br>• `validFromUtc` / `validUntilUtc` (typically `YYYY-01-01T00:00:00Z` to `(YYYY+1)-01-15T00:00:00Z`)<br>• `peakTimeSource` (`"SourceCalendarUtc"` or `"DerivedFromSolarLongitudeUtc"`)<br>• `peakUtc` (UTC ISO-8601 or `null`)<br>• `peakWindowStartUtc` / `peakWindowEndUtc` (nullable UTC bounds)<br>• `peakUncertaintyHours` (finite number $\ge 0$ or `null`)<br>• `peakUncertaintySolarLonDeg` (finite number $\ge 0$ or `null`)<br>• `peakProfile` (`"Sharp"`, `"Broad"`, `"Variable"`, `"OutburstSuspected"`, `"Unknown"`)<br>• `idealizedZhrMin` / `idealizedZhrMax` (nullable integers $\ge 0$, or `null` when variable/unknown)<br>• `idealizedZhrQualifier` (string, e.g. `"Variable"`, `"Nominal"`, `"OutburstModel"`)<br>• `populationIndexR` (finite number or `null`)<br>• `notes` (short plain-text non-HTML observing note $\le 280$ chars or `null`) |

### 3.3 `RemovedMeteorShowerTombstone`

- `iauNumber`, `iauCode`, `formerName`, `removedNote`, optional
  `supersededByIauNumber`.
- Never included in active/upcoming shower lists or radiant projections.

## 4. Local Loading, Precedence, Validity, and Last-Known-Good

1. **Source Precedence (No Network Polling, No Second Scheduler):**
   - **Override / Local Import:** When `CameraAgent:MeteorShowers:SnapshotPath`
     and `SnapshotSha256` are configured, or an owner-approved local import is
     activated under `<RawIngressRoot>/.meteor-showers/current.json`, CameraAgent
     verifies the file's SHA-256 and parses the full `hvo-meteor-showers-v1`
     schema before use. An invalid or mismatched override fails closed with an
     explicit reason (`MeteorShowerSnapshotUnavailableReason`) and never silently
     substitutes unverified bytes. An invalid new import **never overwrites** an
     intact last-known-good snapshot.
   - **Bundled Snapshot:** When no override/import is active, CameraAgent loads
     the verified release-bundled snapshot from `meteor-showers/` beside the
     application (verified against `provenance.json` SHA-256).
   - **Disabled / Absent:** When disabled or no snapshot is installed, Observatory
     omits or marks the card `Unavailable` with zero impact on satellites,
     space-weather notices, or capture.
2. **Freshness and Annual Expiry:**
   - **Identity freshness:** Static IAU MDC identity/radiant records remain
     usable offline with their recorded `mdcExportDate`.
   - **Annual activity validity:** Evaluated strictly against
     `annualForecast.validFromUtc <= queryUtc < annualForecast.validUntilUtc`.
     Past `validUntilUtc`, `annualForecastStatus` becomes **`Expired`**;
     expired peak times and ZHRs are **never** silently presented as current
     year forecasts. If the snapshot has no `activitySource` (or expired annual
     activity), the evaluator may still compute **HVO-derived nominal dates from
     J2000 solar longitude** (`peakTimeSource = "DerivedFromSolarLongitudeUtc"`)
     **only** with an explicit `"Annual calendar forecast unavailable/expired;
     dates below are nominal solar-longitude estimates derived locally by HVO
     SkyMonitor"` qualifier and `idealizedZhr = null`.

## 5. Evaluation Semantics (Solar Longitude, Radiant, Darkness, Moon, Cloud)

1. **Circular Solar-Longitude Active Window:**
   - Compute current **J2000.0 geometric geocentric solar ecliptic longitude**
     $\lambda_\odot(t) \in [0^\circ, 360^\circ)$ offline from pinned **Astronomy
     Engine 2.1.19** `GeoVector(Body.Sun, time, Aberration.None)` rotated from
     J2000 equatorial into J2000 ecliptic coordinates by the IAU J2000.0 mean
     obliquity $\varepsilon_{2000} = 23.439291111^\circ$
     (`astronomy-engine-2.1.19-solar-lon-j2000-v1`). Do **not** substitute
     Astronomy Engine's of-date `SunPosition`/`SearchSunLongitude` without
     frame conversion, as true-equinox-of-date precession introduces a
     $\sim 0.38^\circ$ ($\sim 9\text{ h}$) bias in 2026 against IAU MDC J2000.0
     $\lambda_\odot$ tables.
   - Define circular arc test `IsInCircularInterval(lambda, start, end)`:
     - If `start <= end`: `start <= lambda && lambda <= end`;
     - If `start > end` (wraps across $360^\circ / 0^\circ$): `lambda >= start || lambda <= end`.
   - Upcoming lookahead (Proposed default: **30 days** forward) resolves next
     active start and peak UTC across year boundaries cleanly (e.g., querying on
     2026-12-20 shows active `URS` and upcoming `QUA` peaking 2027-01-03/04 UTC).
2. **Radiant Position, Drift, and Site Visibility:**
   - Within the active window, if `radiantDrift` is non-null:
     - $\Delta\lambda_\odot = ((\lambda_\odot(t) - \lambda_{\odot,0} + 540^\circ) \bmod 360^\circ) - 180^\circ$
     - $\alpha(t) = (\alpha_0 + \Delta\lambda_\odot \cdot \frac{\Delta\alpha}{\Delta\lambda_\odot}) \bmod 360^\circ$
     - $\delta(t) = \text{clamp}(\delta_0 + \Delta\lambda_\odot \cdot \frac{\Delta\delta}{\Delta\lambda_\odot}, -90^\circ, 90^\circ)$
     - Record `radiantDriftBasis = "LinearSolarLongitudeDrift"`.
   - If `radiantDrift` is `null`: use $(\alpha_0, \delta_0)$ and record
     `radiantDriftBasis = "FixedPeakRadiantDriftUnavailable"`.
   - Transform J2000 $(\alpha(t), \delta(t))$ to **unrefracted geometric
     `AltAzPoint`** at `ObserverLocation` via `EquatorialPrecession.PrecessJ2000`
     + `CoordinateTransforms.EquatorialToHorizontal`.
   - Over the site's local night window, compute radiant altitude at the query
     instant, maximum radiant altitude during local astronomical darkness, and
     whether the radiant clears the geometric horizon (`RadiantAboveHorizon` vs
     `RadiantBelowHorizon`). When an active `#1394` horizon profile exists,
     optionally note site-horizon obstruction without altering geometric altitude.
3. **Darkness, Moon, and Optional `#1004` Cloud Context:**
   - Compute site astronomical dusk/dawn (`Sun <= -18°`) via existing
     `ISolarEventCalculator` and current solar regime via `SolarAltitudeClassifier`.
   - Compute Moon topocentric geometric altitude and illuminated fraction at
     peak/darkness midpoint via existing `SolarDiskEphemeris`.
   - Attach optional `#1004` `cloud-assessment-v1` summary only when explicitly
     supplied and valid for the site; otherwise `cloudContextStatus = "Unknown"`
     (never assumed clear).

## 6. Observatory Card and Optional Radiant-Label Contract

### 6.1 Required v1 Deliverable: Optional Observatory Information Card (`hvo-meteor-shower-card-v1`)

Displayed on **Observatory & location** (`/operations/site`) only when enabled
and independent of satellite planning. Each listed shower row displays:
- **Identity:** `name`, `iauCode`, `iauNumber`, and `status` badge (`Established`
  or `Working` when opted in).
- **Activity & Peak:** Active window and peak time in **both** site-local civil
  date/time (`SiteTimeZone`) and UTC, peak uncertainty/profile, and
  `peakTimeSource` (`SourceCalendarUtc` vs `DerivedFromSolarLongitudeUtc`).
- **Qualified Rate:** Source idealized ZHR (or `Unknown`/`Variable`) accompanied
  by mandatory informational disclaimer: *"Idealized source ZHR under ideal dark
  zenith conditions — not an expected count for this camera or site."*
- **Site Context:** Current & max dark-window geometric radiant altitude (or
  explicit `"Below horizon at this site"`), local darkness window, Moon
  illumination & altitude, and optional `#1004` cloud state (`Unknown` when absent).
- **Provenance & Attribution Footer:** Snapshot ID, IAU MDC export date, annual
  calendar edition/expiry status, and verbatim source attribution (Section 2).

### 6.2 Optional Follow-On Slice: Separately Togglable Radiant Labels

- Off by default; independent toggle from stars, constellations, satellites, and
  small bodies.
- Plots only above-horizon, in-frame radiants for active (and optionally
  near-peak) showers, carrying `iauCode`, evaluation date/UTC,
  `radiantDriftBasis`, and projection/horizon provenance.
- Never forced into capture imagery or raw/calibrated artifacts.
- **Closure rule:** Card-only delivery may close `#1396`'s meteor-shower
  acceptance if optional radiant labels are explicitly recorded as omitted.

### 6.3 Non-Negotiable Epistemic Prohibitions

No contract field, UI string, API endpoint, or processing step under this
specification may:
1. Automatically classify a detected camera transient/streak as a meteor;
2. Predict individual meteor streaks or arrival times;
3. Infer shower membership from geometric proximity of a measured streak to a
   shower radiant;
4. Present idealized ZHR as a site/camera detection guarantee; or
5. Ingest or redistribute unapproved third-party calendar files or fireball
   observation catalogs.
