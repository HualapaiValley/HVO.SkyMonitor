# Meteor-Shower Context Contract v1 (#1396 / folded #1397)

Status: design checkpoint contract under the
[#1396 source and contract decision](../planning/sky-layers-source-decision-1396.md),
dated 2026-11-27. This document defines the bounded host-neutral data snapshot,
provenance and redistribution gates (`M1`, `M2`), MDC-only baseline semantics,
occurrence-window and overlapping-edition rules, solar-longitude and radiant
evaluation semantics (including explicit RA degrees-to-hours conversion and a
frozen golden horizontal vector), Observatory card contract, optional
radiant-label boundary, and strict epistemic prohibitions for meteor-shower
information to be implemented after Slice M0 (approved immutable fixture
publication). It does not claim that any runtime implementation exists yet.

This contract provides **informational observing context only**. A meteor-shower
forecast **cannot** predict individual meteors or establish that a captured
streak belongs to a meteor shower.

## 1. Ownership and Architectural Boundaries

- **`HVO.SkyMonitor.Astronomy` (`HVO.SkyMonitor.Astronomy.MeteorShowers`)** owns
  the canonical snapshot/provenance schemas, parser/validator, reader interface,
  solar-longitude activity evaluator, occurrence-window selector, and geometric
  radiant altitude/drift evaluator. It reuses `ObserverLocation`, `AltAzPoint`,
  `EquatorialPoint`, `EquatorialPrecession`, `CoordinateTransforms`,
  `SiteTimeZone`, `IPlanetEphemeris`, `SolarAltitudeClassifier`,
  `SolarDiskEphemeris`, `ISolarEventCalculator`, and
  `HorizonObstructionEvaluator` (`#1394`). It has no HTTP, SQLite, filesystem,
  ASP.NET, or SkiaSharp dependencies.
- **`HVO.SkyMonitor.CameraAgent.Common`** owns the verified local snapshot
  loader (explicit verified override/import under
  `<RawIngressRoot>/.meteor-showers/`, plus optional gate-compliant release
  bundle), atomic pointer activation, last-known-good retention, and read-only
  Observatory/Software & Catalog projection services. **No background scheduler,
  no live network poller, and no dependency on `OrbitalElementsRefresh` or
  `SmallBodyEphemerisRefresh`.**
- **`HVO.SkyMonitor.CameraAgent`** owns the optional Observatory information
  card/list, read-only Software & Catalog snapshot status, and optional later
  radiant-label toggle.
- **`HVO.SkyMonitor.Processing`** is untouched for card-only v1 delivery; if the
  optional radiant-label presentation slice is implemented later, it adds a
  separately togglable presentation payload strictly separate from
  `TransientObservation` and `PredictedSatelliteTracks`.

## 2. Source Governance, Redistribution Gates (`M1`, `M2`), and Attribution Contract

Every snapshot carries explicit machine-readable provenance (`hvo-meteor-shower-provenance-v1`)
separating identity authority from optional annual activity authority:

1. **Identity & Radiant Authority — IAU Meteor Data Center (MDC) Shower Database (External Gate `M2`):**
   - Source URL: `https://www.iaumeteordatacenter.org/`
   - **External Redistribution Gate `M2`:** IAU MDC compiles shower definitions
     and radiants published across external astronomy journals and literature,
     and the `ta3.sk` database hosts were unreachable during live 2026-11-26/27
     probes (`Errno 65` / timeout), so no primary redistribution license file
     could be verified. Therefore, **releases do NOT bundle MDC redistribution
     tables** until primary MDC redistribution terms or written confirmation
     from IAU MDC are verified and recorded (`identitySource.redistributionApprovalRef`
     is required on any release-bundled snapshot). Until `M2` is resolved,
     operators may explicitly import locally acquired MDC snapshots with SHA-256
     verification (`redistributionApprovalRef: null` permitted only for local
     operator imports, never for release bundles).
   - Required provenance fields: `providerName`, `sourceUrl`, `mdcExportDate`,
     `mdcSourceReference`, `mdcRawSha256` (nullable), `redistributionApprovalRef`
     (required for release bundles, `null` for local operator imports), and
     required citation text:
     > *"Meteor shower nomenclature and reference radiants: IAU Meteor Data Center (MDC) Shower Database, IAU Commission F1 (https://www.iaumeteordatacenter.org/)."*
2. **Optional Annual Activity / Calendar Authority (e.g., IMO or Operator-Imported Curated Source — External Gate `M1`):**
   - **No PDF parsing or HTML scraping at runtime.**
   - **External Redistribution Gate `M1`:** Under IMO's verbatim `"Using IMO Data"`
     policy (`https://www.imo.net/about/using-imo-data/`, verified 2026-11-27),
     republishing an IMO dataset in full as a standalone file/database/API or
     reproducing the Meteor Shower Calendar in substantial part requires
     contacting IMO first. Therefore, any release-bundled snapshot containing
     IMO-derived activity fields requires a recorded written IMO confirmation
     reference (`activitySource.redistributionApprovalRef`) in `provenance.json`.
     Without `M1`, annual activity fields are supplied only through an explicit
     local operator import (`activitySource: null` for MDC-only snapshots).
   - Required attribution when IMO data is displayed:
     > *"Source: International Meteor Organization (IMO) - www.imo.net (IMO Meteor Shower Calendar <YYYY>). Displayed by HVO SkyMonitor; not endorsed by the IMO. Site altitude, darkness, Moon, and solar-longitude UTC conversions are computed locally by HVO SkyMonitor."*
   - **Prohibited sources:** IMO/allsky.org/American Meteor Society fireball
     report catalogs are excluded (different ownership, observational records,
     not shower forecasts).

## 3. Bounded Versioned Snapshot Schema (`hvo-meteor-showers-v1`)

All objects in `hvo-meteor-showers-v1` enforce strict JSON schema semantics
(`additionalProperties: false`).

### 3.1 Top-Level Snapshot Envelope

| Field | JSON Type / Format | Required Semantics |
| --- | --- | --- |
| `schemaVersion` | string (`"hvo-meteor-showers-v1"`) | Exact schema version |
| `snapshotId` | string (1–64 chars) | Human-readable version slug (e.g. `"local-mdc-2026-activity-2027-r1"`, `"local-mdc-only-2026-r1"`) |
| `snapshotSha256` | 64-char uppercase hex | SHA-256 of canonical snapshot JSON with `snapshotSha256` excluded |
| `generatedUtc` | UTC ISO-8601 string | Curation/build timestamp |
| `identitySource` | object | IAU MDC provenance: `providerName`, `sourceUrl`, `mdcExportDate`, `mdcSourceReference`, `mdcRawSha256` (nullable), `redistributionApprovalRef` (required non-null for release-bundled snapshots under Gate `M2`; `null` for local operator imports), `citationText` |
| `activitySource` | object or `null` | Annual calendar/activity provenance (`null` when snapshot contains IAU MDC identity/radiant data only): `providerName`, `editionYear` (integer), `sourceUrl`, `attributionText`, `termsUrl`, `redistributionApprovalRef` (required non-null for release-bundled third-party data under Gate `M1`; `null` for local operator imports), `validFromUtc`, `validUntilUtc` |
| `showers` | array of `MeteorShowerRecord` ($\le 1,500$ entries, sorted by `iauNumber` ascending) | Active/established/working shower definitions |
| `removedShowers` | array of `RemovedMeteorShowerTombstone` ($\le 1,500$ entries, sorted by `iauNumber` ascending) | Tombstones for IAU MDC Removed showers |

**Hard validation ceilings (Proposed defaults):**
- Maximum snapshot JSON file size: **2 MiB**; `provenance.json` $\le 64\text{ KiB}$.
- Reject duplicate `iauNumber`, duplicate active `iauCode`, nonfinite numbers,
  `NaN`/`Infinity`, out-of-range coordinates (`raDeg` $\notin [0, 360)$, `decDeg`
  $\notin [-90, 90]$, $\lambda_\odot \notin [0, 360)$), invalid occurrence
  windows (`windowEndUtc <= windowStartUtc`), invalid edition validity
  (`validUntilUtc <= validFromUtc`), unknown JSON properties
  (`additionalProperties: false`), or nesting $> 64$ levels.

### 3.2 `MeteorShowerRecord` (Aligned with Decision Doc & MDC-Only Baseline)

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
| `parentBodyName` | string or `null` | Informational parent-body text only (e.g. `"(196256) 2003 EH1"`); never used for orbit computation |
| `nominalActivity` | object or `null` | **Optional/nullable** J2000.0 solar-longitude activity metadata so MDC-only records never fabricate bounds:<br>• `source`: enum (`"IAU-MDC"`, `"AnnualActivitySource"`, `"OperatorCurated"`)<br>• `peakSolarLonDeg`: finite number `[0, 360)` or `null`<br>• `activeStartSolarLonDeg`: finite number `[0, 360)` or `null` (**must stay `null` if source omits active start**)<br>• `activeEndSolarLonDeg`: finite number `[0, 360)` or `null` (**must stay `null` if source omits active end**)<br>If both start and end are non-null, handles circular wrap where `start > end` (e.g. `355° → 15°`). If either is `null`, both must be `null` (no one-sided fabricated interval) |
| `annualForecasts` | array of `MeteorShowerAnnualForecast` (0–2 entries, sorted by `occurrenceYear` ascending then `windowStartUtc` ascending; `[]` when `activitySource` is `null` or does not cover this shower) | Each entry binds one specific shower occurrence:<br>• `editionYear` (integer, e.g., `2026`, `2027`)<br>• `occurrenceYear` (integer, calendar year of the occurrence's nominal peak)<br>• `windowStartUtc` (UTC ISO-8601 start of this occurrence's active window)<br>• `peakUtc` (UTC ISO-8601 or `null`)<br>• `windowEndUtc` (UTC ISO-8601 exclusive end of this occurrence's active window, strictly `> windowStartUtc`)<br>• `validFromUtc` / `validUntilUtc` (edition validity, half-open `validFromUtc <= queryUtc < validUntilUtc`; typically `(editionYear-1)-12-01T00:00:00Z` to `(editionYear+1)-01-15T00:00:00Z`)<br>• `peakTimeSource` (`"SourceCalendarUtc"` or `"DerivedFromSolarLongitudeUtc"`)<br>• `peakWindowStartUtc` / `peakWindowEndUtc` (nullable UTC bounds)<br>• `peakUncertaintyHours` (finite number $\ge 0$ or `null`)<br>• `peakUncertaintySolarLongitudeDeg` (finite number $\ge 0$ or `null`)<br>• `peakProfile` (`"Sharp"`, `"Broad"`, `"Variable"`, `"OutburstSuspected"`, `"Unknown"`)<br>• `activityConfidence` (`"NominalAnnual"`, `"VariableOrUncertain"`, `"ModelPrediction"`, `"Unknown"`)<br>• `idealizedZhrMin` / `idealizedZhrMax` (nullable integers $\ge 0$, or `null` when variable/unknown)<br>• `idealizedZhrQualifier` (string, e.g. `"Variable"`, `"Nominal"`, `"OutburstModel"`)<br>• `populationIndexR` (finite number or `null`)<br>• `notes` (short plain-text non-HTML observing note $\le 280$ chars or `null`) |

### 3.3 `RemovedMeteorShowerTombstone`

- `iauNumber`, `iauCode`, `formerName`, `removedNote`, optional
  `supersededByIauNumber` (nullable integer).
- Never included in active/upcoming shower lists or radiant projections.

### 3.4 Canonical JSON Examples

All SHA-256 hashes below are computed over compact ordinal-sorted canonical JSON
with `snapshotSha256` excluded.

#### Example A: Annual Activity + IAU MDC (Local Operator Import, 2027 Quadrantids Occurrence)

```json
{
  "activitySource": {
    "attributionText": "Source: International Meteor Organization (IMO) - www.imo.net (IMO Meteor Shower Calendar 2027). Displayed by HVO SkyMonitor; not endorsed by the IMO. Site altitude, darkness, Moon, and solar-longitude UTC conversions are computed locally by HVO SkyMonitor.",
    "editionYear": 2027,
    "providerName": "OperatorImportedAnnualActivity",
    "redistributionApprovalRef": null,
    "sourceUrl": "https://www.imo.net/meteor-shower-calendar-2027/",
    "termsUrl": "https://www.imo.net/about/using-imo-data/",
    "validFromUtc": "2026-12-01T00:00:00Z",
    "validUntilUtc": "2028-01-15T00:00:00Z"
  },
  "generatedUtc": "2026-11-27T00:00:00Z",
  "identitySource": {
    "citationText": "Meteor shower nomenclature and reference radiants: IAU Meteor Data Center (MDC) Shower Database, IAU Commission F1 (https://www.iaumeteordatacenter.org/).",
    "mdcExportDate": "2026-11-01",
    "mdcRawSha256": null,
    "mdcSourceReference": "IAU MDC SD (local operator import)",
    "providerName": "IAU-MDC",
    "redistributionApprovalRef": null,
    "sourceUrl": "https://www.iaumeteordatacenter.org/"
  },
  "removedShowers": [
    {
      "formerName": "alpha Capricornids (legacy duplicate)",
      "iauCode": "ACX",
      "iauNumber": 9999,
      "removedNote": "Synthetic example tombstone illustrating Removed status handling.",
      "supersededByIauNumber": 1
    }
  ],
  "schemaVersion": "hvo-meteor-showers-v1",
  "showers": [
    {
      "annualForecasts": [
        {
          "activityConfidence": "NominalAnnual",
          "editionYear": 2027,
          "idealizedZhrMax": 120,
          "idealizedZhrMin": 60,
          "idealizedZhrQualifier": "Nominal",
          "notes": "Sharp peak; idealized ZHR assumes dark sky, zenith radiant, and limiting magnitude 6.5.",
          "occurrenceYear": 2027,
          "peakProfile": "Sharp",
          "peakTimeSource": "SourceCalendarUtc",
          "peakUncertaintyHours": 2.0,
          "peakUncertaintySolarLongitudeDeg": 0.08,
          "peakUtc": "2027-01-04T02:00:00Z",
          "peakWindowEndUtc": "2027-01-04T04:00:00Z",
          "peakWindowStartUtc": "2027-01-04T00:00:00Z",
          "populationIndexR": 2.1,
          "validFromUtc": "2026-12-01T00:00:00Z",
          "validUntilUtc": "2028-01-15T00:00:00Z",
          "windowEndUtc": "2027-01-13T00:00:00Z",
          "windowStartUtc": "2026-12-28T00:00:00Z"
        }
      ],
      "geocentricVelocityKmPerSec": 41.0,
      "iauCode": "QUA",
      "iauNumber": 10,
      "mdcDatasetRef": "MDC-0010-REF",
      "name": "Quadrantids",
      "nominalActivity": {
        "activeEndSolarLonDeg": 292.0,
        "activeStartSolarLonDeg": 276.0,
        "peakSolarLonDeg": 283.16,
        "source": "AnnualActivitySource"
      },
      "parentBodyName": "(196256) 2003 EH1",
      "radiantDrift": {
        "dDecDegPerSolarLonDeg": -0.1,
        "dRaDegPerSolarLonDeg": 0.6
      },
      "referenceRadiant": {
        "decDeg": 49.0,
        "equinox": "J2000",
        "raDeg": 230.0,
        "referenceSolarLongitudeDeg": 283.16
      },
      "showerGroupId": null,
      "status": "Established"
    }
  ],
  "snapshotId": "local-mdc-2026-activity-2027-r1",
  "snapshotSha256": "106E7D11183EAAF704BE9F3B97BA5C54964762A2A56440C5D13404E6BD2D0FF7"
}
```

#### Example B: IAU MDC-Only Baseline (No Fabricated Activity Bounds or ZHR)

```json
{
  "activitySource": null,
  "generatedUtc": "2026-11-27T00:00:00Z",
  "identitySource": {
    "citationText": "Meteor shower nomenclature and reference radiants: IAU Meteor Data Center (MDC) Shower Database, IAU Commission F1 (https://www.iaumeteordatacenter.org/).",
    "mdcExportDate": "2026-11-01",
    "mdcRawSha256": null,
    "mdcSourceReference": "IAU MDC SD (local operator import)",
    "providerName": "IAU-MDC",
    "redistributionApprovalRef": null,
    "sourceUrl": "https://www.iaumeteordatacenter.org/"
  },
  "removedShowers": [],
  "schemaVersion": "hvo-meteor-showers-v1",
  "showers": [
    {
      "annualForecasts": [],
      "geocentricVelocityKmPerSec": 41.0,
      "iauCode": "QUA",
      "iauNumber": 10,
      "mdcDatasetRef": "MDC-0010-REF",
      "name": "Quadrantids",
      "nominalActivity": {
        "activeEndSolarLonDeg": null,
        "activeStartSolarLonDeg": null,
        "peakSolarLonDeg": 283.16,
        "source": "IAU-MDC"
      },
      "parentBodyName": "(196256) 2003 EH1",
      "radiantDrift": null,
      "referenceRadiant": {
        "decDeg": 49.0,
        "equinox": "J2000",
        "raDeg": 230.0,
        "referenceSolarLongitudeDeg": 283.16
      },
      "showerGroupId": null,
      "status": "Established"
    }
  ],
  "snapshotId": "local-mdc-only-2026-r1",
  "snapshotSha256": "B75523E0C0FC7A86D123E1F6934996F0919B00D7292BB8D4FD11193B2D8A442C"
}
```

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
   - **Optional Release-Bundled Snapshot (Gated on `M2` and `M1`):** When no
     override/import is active and a gate-compliant release-bundled snapshot is
     installed under `meteor-showers/` beside the application (verified against
     `provenance.json` SHA-256 and requiring non-null `redistributionApprovalRef`
     on `identitySource` under Gate `M2` and on `activitySource` when non-null
     under Gate `M1`), CameraAgent loads that verified bundle. Before Gates `M2`
     / `M1` are recorded, releases ship no third-party meteor-shower bundle.
   - **Disabled / Absent:** When disabled or no snapshot is installed, Observatory
     omits or marks the card `Unavailable` with zero impact on satellites,
     space-weather notices, or capture.
2. **Freshness, Occurrence-Window Binding, Overlapping Editions, and Exclusive Expiry:**
   - **Identity freshness:** Static IAU MDC identity/radiant records remain
     usable offline with their recorded `mdcExportDate`.
   - **Edition validity (exact half-open boundary):** An annual forecast entry's
     edition is valid at `queryUtc` iff **`validFromUtc <= queryUtc < validUntilUtc`**.
     At and past `validUntilUtc` (`queryUtc >= validUntilUtc`), `annualForecastStatus`
     is strictly **`Expired`**; expired peak times and ZHRs are **never**
     silently presented as current-year forecasts.
   - **Occurrence-window binding:** A valid edition entry is eligible for display
     at `queryUtc` with lookahead duration $L$ (Proposed default: `30 days`) iff:
     - **Active:** `validFromUtc <= queryUtc < validUntilUtc` **and**
       `windowStartUtc <= queryUtc < windowEndUtc`.
     - **Upcoming:** `queryUtc < validUntilUtc` **and**
       `validFromUtc <= min(queryUtc + L, windowEndUtc)` **and**
       `queryUtc < windowStartUtc < queryUtc + L`.
     - **Past occurrence exclusion:** Any forecast with **`windowEndUtc <= queryUtc`**
       is strictly excluded from active and upcoming lists even while
       `queryUtc < validUntilUtc`.
   - **Overlapping-edition precedence:** When a snapshot (or merged active +
     next-year operator import) supplies multiple non-expired forecast entries
     for the same `(iauNumber, occurrenceYear)`, select the entry with the
     **highest `editionYear`** (breaking ties by `generatedUtc` descending, then
     `snapshotId` ordinal).
   - **Verified boundary cases (mandatory Slice M0/M1 tests):**
     - **Case (a) — `2027-01-03T12:00:00Z` with only a 2026 edition present**
       (`QUA` entry: `editionYear = 2026`, `occurrenceYear = 2026`,
       `windowStartUtc = 2025-12-28T00:00:00Z`, `peakUtc = 2026-01-03T21:00:00Z`,
       `windowEndUtc = 2026-01-13T00:00:00Z`, `validUntilUtc = 2027-01-15T00:00:00Z`):
       Because `windowEndUtc (2026-01-13) <= queryUtc (2027-01-03)`, the January
       2026 Quadrantids entry is **NOT** surfaced as active or upcoming.
       Quadrantids instead reports `NoForecastForOccurrence` (optional
       HVO-derived solar-longitude estimate only, with `idealizedZhr = null`).
     - **Case (b) — `2026-12-28T00:00:00Z` with 30-day lookahead and both 2026
       and 2027 editions present:** The 2026 edition's `occurrenceYear = 2026`
       entry is excluded (`windowEndUtc <= queryUtc`), and for
       `occurrenceYear = 2027` (`windowStartUtc = 2026-12-28T00:00:00Z`,
       `peakUtc = 2027-01-04T02:00:00Z`, `windowEndUtc = 2027-01-13T00:00:00Z`),
       overlapping-edition precedence selects the **2027 edition**
       (`editionYear = 2027 > 2026`).
     - **Case (c) — Exact expiry boundary `queryUtc == validUntilUtc`**
       (`2027-01-15T00:00:00Z` for a 2026 edition): strictly `Expired`.
   - **Fallback when annual activity is absent or expired:** If `activitySource`
     is `null` or no valid occurrence forecast exists, the evaluator may compute
     a nominal peak date from J2000 solar longitude (`peakTimeSource =
     "DerivedFromSolarLongitudeUtc"`) **only** when `peakSolarLonDeg` (or
     `referenceRadiant.referenceSolarLongitudeDeg`) is present, with explicit
     qualifier *"Annual calendar forecast unavailable/expired; reference peak
     date below is derived locally from J2000 solar longitude by HVO SkyMonitor"*
     and `idealizedZhr = null` — **never** fabricating active start/end bounds.

## 5. Evaluation Semantics (Solar Longitude, Radiant, Darkness, Moon, Cloud)

### 5.1 Circular Solar-Longitude Active Window

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
- When `nominalActivity` supplies both `activeStartSolarLonDeg` and
  `activeEndSolarLonDeg`, define circular arc test
  `IsInCircularInterval(lambda, start, end)`:
  - If `start <= end`: `start <= lambda && lambda <= end`;
  - If `start > end` (wraps across $360^\circ / 0^\circ$): `lambda >= start || lambda <= end`.
- When `nominalActivity` is `null` or `activeStartSolarLonDeg`/`activeEndSolarLonDeg`
  are `null` (MDC-only baseline), active-window membership is explicitly
  `Unknown` (`activeWindow = null`) rather than fabricated.
- Upcoming lookahead (Proposed default: **30 days** forward) uses the
  occurrence-window and overlapping-edition rules in Section 4.2 to resolve
  year-boundary transitions cleanly (e.g., querying on `2026-12-28` selects
  the January 2027 Quadrantids occurrence from the 2027 edition).

### 5.2 Radiant Position, Explicit RA Unit Conversion, Drift, and Site Visibility

- Within a known active window, if `radiantDrift` is non-null:
  - $\Delta\lambda_\odot = ((\lambda_\odot(t) - \lambda_{\odot,0} + 540^\circ) \bmod 360^\circ) - 180^\circ$
  - $\alpha_{\text{deg}}(t) = (\alpha_0 + \Delta\lambda_\odot \cdot \frac{\Delta\alpha}{\Delta\lambda_\odot}) \bmod 360^\circ$
  - $\delta_{\text{deg}}(t) = \text{clamp}(\delta_0 + \Delta\lambda_\odot \cdot \frac{\Delta\delta}{\Delta\lambda_\odot}, -90^\circ, 90^\circ)$
  - Record `radiantDriftBasis = "LinearSolarLongitudeDrift"`.
- If `radiantDrift` is `null` (or no active window is known): use
  $(\alpha_0, \delta_0)$ and record
  `radiantDriftBasis = "FixedPeakRadiantDriftUnavailable"`.
- **Mandatory RA degrees-to-hours conversion before `PrecessJ2000`:**
  `EquatorialPoint` (`src/HVO.SkyMonitor.Astronomy/Projection.cs`) defines
  `RightAscensionHours` in **hours** `[0, 24)`, whereas snapshot JSON stores
  `raDeg` in **degrees** `[0, 360)`. Every evaluator MUST convert explicitly:
  ```csharp
  var raHours = raDeg / 15.0; // e.g., Quadrantids 230.0 deg -> 15.333333333 h
  var j2000 = new EquatorialPoint(raHours, decDeg);
  var ofDate = EquatorialPrecession.PrecessJ2000(j2000, utc);
  var horizontal = CoordinateTransforms.EquatorialToHorizontal(
      ofDate, utc, observer.LatitudeDegrees, observer.LongitudeDegrees);
  ```
  Slice M0/M1 unit tests MUST assert both an `raDeg >= 24°` case (`QUA`, where
  omitting `/ 15.0` throws `ArgumentOutOfRangeException`) and an `raDeg < 24°`
  case (e.g., `raDeg = 15.0° -> 1.0 h`, where omitting `/ 15.0` silently
  evaluates `15.0 h = 225°`, a `210°` pointing error without throwing!).
- **Frozen golden horizontal vector (verified bit-for-bit against
  `AstronomyTime.cs`, `Precession.cs`, and `Projection.cs`):**

  | Quantity | Exact Frozen Value |
  | --- | --- |
  | Shower & reference J2000 radiant | Quadrantids (`QUA`, IAU #10): `raDeg = 230.0°` (`raHours = 15.333333333 h`), `decDeg = +49.0°` (zero drift) |
  | Observer (`ObserverLocation`) | HVO reference site: `latitudeDegrees = 35.5599378`, `longitudeDegrees = -113.9119818`, `elevationMeters = 1000.0` |
  | Query instant (`utc`) | `2027-01-04T11:00:00Z` (`JD = 2461409.9583333335`, `GMST = 268.830818°`, `LMST = 154.918836°`) |
  | Precessed mean-of-date (`PrecessJ2000`) | `RightAscensionHours = 15.347572454 h` (`230.213587°`), `DeclinationDegrees = 48.903563°` |
  | Unrefracted geometric horizontal (`EquatorialToHorizontal`) | **`altitudeDeg = 35.029920°`**, **`azimuthDeg = 50.936604°`** |

- Over the site's local night window, compute radiant altitude at the query
  instant, maximum radiant altitude during local astronomical darkness, and
  whether the radiant clears the geometric horizon (`RadiantAboveHorizon` vs
  `RadiantBelowHorizon`). When an active `#1394` `HorizonObstructionProfile`
  exists, optionally note site-horizon obstruction without altering geometric
  altitude.

### 5.3 Darkness, Moon, and Optional `#1004` Cloud Context

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
- **Activity & Peak (when valid annual forecast is present):** Occurrence window
  (`windowStartUtc`–`windowEndUtc`) and peak time in **both** site-local civil
  date/time (`SiteTimeZone`) and UTC, peak uncertainty (`peakUncertaintyHours` /
  `peakUncertaintySolarLongitudeDeg`), `peakProfile`, `activityConfidence`, and
  `peakTimeSource` (`SourceCalendarUtc` vs `DerivedFromSolarLongitudeUtc`).
- **Qualified Rate (when valid annual forecast is present):** Source idealized
  ZHR (or `Unknown`/`Variable`) accompanied by mandatory informational
  disclaimer: *"Idealized source ZHR under ideal dark zenith conditions — not an
  expected count for this camera or site."*
- **Identity/Radiant-Only Mode (MDC-only baseline or missing/expired annual activity):**
  When `activitySource == null`, `annualForecasts` has no valid entry for the
  current/upcoming occurrence, or `nominalActivity` lacks start/end bounds, the
  card **never** displays fabricated start/end dates or placeholder ZHR values.
  It displays shower identity, reference radiant / current & dark-window
  geometric radiant altitude (or `"Below horizon at this site"`), optional
  locally derived nominal peak reference date (if `peakSolarLonDeg` is present,
  explicitly labelled as derived from J2000 solar longitude), and exact UI copy:
  *"Activity window and ZHR unavailable (identity/radiant snapshot only — no
  annual activity source loaded)."* (or *"Annual activity forecast expired —
  no current-year forecast loaded."* when expired).
- **Site Context:** Current & max dark-window geometric radiant altitude (or
  explicit `"Below horizon at this site"`), local darkness window, Moon
  illumination & altitude, and optional `#1004` cloud state (`Unknown` when absent).
- **Provenance & Attribution Footer:** Snapshot ID, IAU MDC export date, annual
  calendar edition/expiry status (or `"None (identity/radiant only)"`), and
  verbatim source attribution (Section 2).

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
4. Present idealized ZHR as a site/camera detection guarantee, or fabricate
   missing activity bounds/ZHR when only identity/radiant data is loaded; or
5. Ingest or redistribute unapproved third-party MDC/IMO snapshots or fireball
   observation catalogs.
