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

**Hard validation & canonical serialization ceilings (Proposed defaults):**
- Maximum snapshot JSON file size: **2 MiB**; `provenance.json` $\le 64\text{ KiB}$.
- **Exact canonical JSON serialization rule (matching `.NET System.Text.Json` /
  `CaptureContractJson` / `OrbitalCanonicalJson` and RFC 8785 JCS):**
  - Compact UTF-8 JSON (no insignificant whitespace), `StringComparer.Ordinal`
    property order, uppercase 64-hex SHA-256 digests;
  - Finite IEEE 754 numbers serialized in shortest round-trip decimal form with
    `-0.0` normalized to `0` and **integer-valued finite numbers serialized
    WITHOUT a trailing `.0`** (for example, `41`, `49`, `230`, `276`, `292`
    rather than `41.0`, matching `.NET System.Text.Json` and
    `tests/fixtures/orbital/contract-v1/pass.json`);
  - UTC ISO-8601 `"Z"` timestamps.
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
| `annualForecasts` | array of `MeteorShowerAnnualForecast` (0–2 entries, sorted by `occurrenceYear` ascending then `windowStartUtc` ascending; `[]` when `activitySource` is `null` or does not cover this shower) | Each entry binds one specific shower occurrence:<br>• `editionYear` (integer, e.g., `2026`, `2027`)<br>• `occurrenceYear` (integer, calendar year of the occurrence's nominal peak)<br>• `windowStartUtc` (UTC ISO-8601 start of this occurrence's active window)<br>• `peakUtc` (UTC ISO-8601 or `null`)<br>• `windowEndUtc` (UTC ISO-8601 exclusive end of this occurrence's active window, strictly `> windowStartUtc`)<br>• `validFromUtc` / `validUntilUtc` (edition validity, half-open `validFromUtc <= queryUtc < validUntilUtc`; typically `(editionYear-1)-12-01T00:00:00Z` to `(editionYear+1)-01-15T00:00:00Z`)<br>• `peakTimeSource` (`"SourceCalendarUtc"` or `"DerivedFromSolarLongitudeUtc"`)<br>• `peakWindowStartUtc` / `peakWindowEndUtc` (nullable UTC bounds)<br>• `peakUncertaintyHours` (finite number $\ge 0$ or `null`)<br>• `peakUncertaintySolarLongitudeDeg` (finite number $\ge 0$ or `null`)<br>• `peakProfile` (`"Sharp"`, `"Broad"`, `"Variable"`, `"OutburstSuspected"`, `"Unknown"`)<br>• `activityConfidence` (`"NominalAnnual"`, `"VariableOrUncertain"`, `"ModelPrediction"`, `"Unknown"`)<br>• `idealizedZhrNominal` (nullable integer $\ge 0$, source nominal idealized ZHR when published, e.g. `80`)<br>• `idealizedZhrMin` / `idealizedZhrMax` (nullable integers $\ge 0$, observed/published variation bounds when supplied, e.g. `60`–`200`, or `null` when unavailable)<br>• `idealizedZhrQualifier` (string, e.g. `"Nominal"`, `"NominalWithObservedVariation"`, `"Variable"`, `"OutburstModel"`)<br>• `populationIndexR` (finite number or `null`)<br>• `notes` (short plain-text non-HTML observing note $\le 280$ chars or `null`) |

### 3.3 `RemovedMeteorShowerTombstone`

- `iauNumber`, `iauCode`, `formerName`, `removedNote`, optional
  `supersededByIauNumber` (nullable integer).
- Never included in active/upcoming shower lists or radiant projections.

### 3.4 Canonical JSON Examples

All SHA-256 hashes below are computed under the exact Section 3.1
`.NET System.Text.Json` / RFC 8785 JCS canonical serializer (compact
ordinal-sorted JSON, integer-valued finite numbers without trailing `.0`, with
`snapshotSha256` excluded).

#### Example A: Annual Activity + IAU MDC (Local Operator Import, 2027 Quadrantids Occurrence — Verified Against IMO 2027 Calendar `cal2027.pdf#page=4`)

Values match `cal2027.pdf` page 4 verbatim: active December 28–January 12,
maximum January 4 `03:25 UTC` ($\lambda_\odot = 283.15^\circ$), nominal
`ZHR = 80` (observed variation `~60–200`), timing uncertainty `~2.5 h`
(`~0.1°`), $V_\infty = 41\text{ km/s}$, $r = 2.1$ at maximum.

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
          "idealizedZhrMax": 200,
          "idealizedZhrMin": 60,
          "idealizedZhrNominal": 80,
          "idealizedZhrQualifier": "NominalWithObservedVariation",
          "notes": "Sharp peak (2027 IMO calendar p. 4: Jan 4 03:25 UTC, nominal ZHR 80, variation ~60-200, timing uncertainty ~2.5 h); idealized ZHR assumes dark sky, zenith radiant, and limiting magnitude 6.5.",
          "occurrenceYear": 2027,
          "peakProfile": "Sharp",
          "peakTimeSource": "SourceCalendarUtc",
          "peakUncertaintyHours": 2.5,
          "peakUncertaintySolarLongitudeDeg": 0.1,
          "peakUtc": "2027-01-04T03:25:00Z",
          "peakWindowEndUtc": null,
          "peakWindowStartUtc": null,
          "populationIndexR": 2.1,
          "validFromUtc": "2026-12-01T00:00:00Z",
          "validUntilUtc": "2028-01-15T00:00:00Z",
          "windowEndUtc": "2027-01-13T00:00:00Z",
          "windowStartUtc": "2026-12-28T00:00:00Z"
        }
      ],
      "geocentricVelocityKmPerSec": 41,
      "iauCode": "QUA",
      "iauNumber": 10,
      "mdcDatasetRef": "MDC-0010-REF",
      "name": "Quadrantids",
      "nominalActivity": {
        "activeEndSolarLonDeg": 292,
        "activeStartSolarLonDeg": 276,
        "peakSolarLonDeg": 283.15,
        "source": "AnnualActivitySource"
      },
      "parentBodyName": "(196256) 2003 EH1",
      "radiantDrift": {
        "dDecDegPerSolarLonDeg": -0.1,
        "dRaDegPerSolarLonDeg": 0.6
      },
      "referenceRadiant": {
        "decDeg": 49,
        "equinox": "J2000",
        "raDeg": 230,
        "referenceSolarLongitudeDeg": 283.15
      },
      "showerGroupId": null,
      "status": "Established"
    }
  ],
  "snapshotId": "local-mdc-2026-activity-2027-r1",
  "snapshotSha256": "5196D5290715CE913E210E8265AC789F1F096FC2BA71310E4934AA5F1DAB618F"
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
      "geocentricVelocityKmPerSec": 41,
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
        "decDeg": 49,
        "equinox": "J2000",
        "raDeg": 230,
        "referenceSolarLongitudeDeg": 283.16
      },
      "showerGroupId": null,
      "status": "Established"
    }
  ],
  "snapshotId": "local-mdc-only-2026-r1",
  "snapshotSha256": "520B979494E8EBBC3FCE953A2CC42E8B9C315B37D116AB144D00BE475E26BDFA"
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
   - **Stage 1 — Edition validity gate (applied FIRST, before occurrence selection
     and before overlapping-edition precedence):** An annual forecast entry's
     edition is eligible at `queryUtc` **iff**
     **`validFromUtc <= queryUtc < validUntilUtc`**. Any entry with
     `queryUtc < validFromUtc` (`NotYetValid`) or `queryUtc >= validUntilUtc`
     (`Expired`) is strictly excluded before Stage 2 runs; expired or
     not-yet-valid editions are **never** surfaced as active or upcoming and
     expired peak times/ZHRs are **never** silently recycled.
   - **Stage 2 — Occurrence-window binding & overlapping-edition precedence
     (applied ONLY to Stage-1-valid entries):**
     - **Past occurrence exclusion:** Any forecast with
       **`windowEndUtc <= queryUtc`** is strictly excluded from active and
       upcoming lists even while `validFromUtc <= queryUtc < validUntilUtc`.
     - **Overlapping-edition precedence:** Among remaining Stage-1-valid,
       non-past entries for the same `(iauNumber, occurrenceYear)`, select the
       entry with the **highest `editionYear`** (breaking ties by `generatedUtc`
       descending, then `snapshotId` ordinal).
     - **Active:** `windowStartUtc <= queryUtc < windowEndUtc` (with Stage 1
       already enforcing `validFromUtc <= queryUtc < validUntilUtc`).
     - **Upcoming (lookahead duration $L$, Proposed default `30 days`):**
       `queryUtc < windowStartUtc < queryUtc + L` (with Stage 1 already
       enforcing `validFromUtc <= queryUtc < validUntilUtc` — an edition whose
       `validFromUtc > queryUtc` is **never** admitted into `Upcoming` merely
       because `windowStartUtc` falls inside `queryUtc + L`).
   - **Verified boundary cases (mandatory Slice M0/M1 tests):**
     - **Case (a) — `2027-01-03T12:00:00Z` with only a 2026 edition present**
       (`QUA` entry: `editionYear = 2026`, `occurrenceYear = 2026`,
       `windowStartUtc = 2025-12-28T00:00:00Z`, `peakUtc = 2026-01-03T21:00:00Z`,
       `windowEndUtc = 2026-01-13T00:00:00Z`, `validUntilUtc = 2027-01-15T00:00:00Z`):
       Because `windowEndUtc (2026-01-13) <= queryUtc (2027-01-03)`, the January
       2026 Quadrantids entry is **NOT** surfaced as active or upcoming.
       Quadrantids instead reports `NoForecastForOccurrence` (optional
       HVO-derived solar-longitude estimate only when `nominalActivity.peakSolarLonDeg`
       is present, with `idealizedZhr = null`).
     - **Case (b) — `2026-12-28T00:00:00Z` with 30-day lookahead and both 2026
       and 2027 editions present:** Both editions pass Stage 1 at `2026-12-28`.
       The 2026 edition's `occurrenceYear = 2026` entry is excluded
       (`windowEndUtc <= queryUtc`), and for `occurrenceYear = 2027`
       (`windowStartUtc = 2026-12-28T00:00:00Z`, `peakUtc = 2027-01-04T03:25:00Z`,
       `windowEndUtc = 2027-01-13T00:00:00Z`), overlapping-edition precedence
       selects the **2027 edition** (`editionYear = 2027 > 2026`).
     - **Case (c) — Exact upper expiry boundary `queryUtc == validUntilUtc`**
       (`2027-01-15T00:00:00Z` for a 2026 edition): excluded by Stage 1 as
       strictly `Expired`.
     - **Case (d) — Pre-validity lookahead boundary `queryUtc = 2026-11-30T00:00:00Z`
       with 30-day lookahead ($L = 30\text{ d}$) against the canonical 2027 edition**
       (`validFromUtc = 2026-12-01T00:00:00Z`, `windowStartUtc = 2026-12-28T00:00:00Z`,
       `validUntilUtc = 2028-01-15T00:00:00Z`): Even though `windowStartUtc`
       (`2026-12-28`) lies within `queryUtc + 30 d` (`2026-12-30`), Stage 1
       strictly excludes the 2027 edition because `queryUtc < validFromUtc`.
       The 2027 edition becomes eligible starting at `2026-12-01T00:00:00Z`.
   - **Fallback when annual activity is absent or expired (no peak date from
     radiant reference longitude alone):** If `activitySource` is `null` or no
     Stage-1/Stage-2 valid occurrence forecast exists, the evaluator may compute
     a nominal peak date from J2000 solar longitude (`peakTimeSource =
     "DerivedFromSolarLongitudeUtc"`) **ONLY** when a source-qualified
     **`nominalActivity.peakSolarLonDeg`** is non-null — **NEVER** from
     `referenceRadiant.referenceSolarLongitudeDeg` alone. When `nominalActivity`
     is `null` or `nominalActivity.peakSolarLonDeg` is `null`, no derived peak
     date is emitted (`derivedPeakUtc = null`), and
     `referenceRadiant.referenceSolarLongitudeDeg` serves solely as the radiant
     reference longitude. When a derived peak date is emitted from
     `nominalActivity.peakSolarLonDeg`, it carries the explicit qualifier
     *"Annual calendar forecast unavailable/expired; reference peak date below
     is derived locally from J2000 solar longitude by HVO SkyMonitor"* and
     `idealizedZhr = null` — **never** fabricating active start/end bounds.

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
- **Frozen golden horizontal vectors (verified bit-for-bit against
  `AstronomyTime.cs`, `Precession.cs`, and `Projection.cs`):**

  | Quantity | Exact Frozen Value |
  | --- | --- |
  | Shower & reference J2000 radiant | Quadrantids (`QUA`, IAU #10): `raDeg = 230` (`raHours = 15.333333333 h`), `decDeg = 49` (zero drift) |
  | Observer (`ObserverLocation`) | HVO reference site: `latitudeDegrees = 35.5599378`, `longitudeDegrees = -113.9119818`, `elevationMeters = 1000` |
  | **Vector 1 — HVO pre-dawn dark-window instant** (`2027-01-04T11:00:00Z`, 04:00 MST, after the `03:25 UTC` IMO peak) | `JD = 2461409.9583333335`, `GMST = 268.830818°`, `LMST = 154.918836°`; precessed `RightAscensionHours = 15.347572454 h` (`230.213587°`), `DeclinationDegrees = 48.903563°`; **`altitudeDeg = 35.029920°`**, **`azimuthDeg = 50.936604°`** (`RadiantAboveHorizon`) |
  | **Vector 2 — Exact 2027 IMO peak instant** (`2027-01-04T03:25:00Z`, 20:25 MST) | `JD = 2461409.6423611110`, `GMST = 154.769381°`, `LMST = 40.857399°`; precessed `RightAscensionHours = 15.347571998 h` (`230.213580°`), `DeclinationDegrees = 48.903566°`; **`altitudeDeg = -5.127134°`**, **`azimuthDeg = 353.840761°`** (`RadiantBelowHorizon` at HVO) |

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
- **Card Mode Selection (Driven Strictly by Annual Activity Forecast State, Never by `nominalActivity` Bounds):**
  1. **Annual Activity Mode (`activitySource != null` AND a Stage-1/Stage-2 valid
     `MeteorShowerAnnualForecast` entry matches the active/upcoming occurrence):**
     Displays the occurrence window (`windowStartUtc`–`windowEndUtc`) and peak
     time in **both** site-local civil date/time (`SiteTimeZone`) and UTC, peak
     uncertainty (`peakUncertaintyHours` / `peakUncertaintySolarLongitudeDeg`),
     `peakProfile`, `activityConfidence`, `peakTimeSource` (`SourceCalendarUtc`
     vs `DerivedFromSolarLongitudeUtc`), and qualified source idealized ZHR
     (`idealizedZhrNominal` and/or `idealizedZhrMin`–`idealizedZhrMax`, or
     `Unknown`/`Variable`) with mandatory disclaimer: *"Idealized source ZHR
     under ideal dark zenith conditions — not an expected count for this camera
     or site."* This mode applies **even when `nominalActivity` is `null` or
     omits solar-longitude start/end bounds** (absent nominal bounds never
     demote a valid annual occurrence into `"no annual activity source loaded"`
     mode).
  2. **Annual Forecast Expired / Missing-Occurrence Mode (`activitySource != null`,
     but the edition is expired/not-yet-valid or no Stage-1/Stage-2 valid
     `annualForecasts` entry covers this shower's current/upcoming occurrence):**
     Never displays fabricated start/end dates or placeholder/expired ZHR values.
     Displays shower identity, reference radiant / current & dark-window
     geometric radiant altitude (or `"Below horizon at this site"`), optional
     locally derived nominal peak reference date **only** when a source-qualified
     `nominalActivity.peakSolarLonDeg` is non-null (**never** from
     `referenceRadiant.referenceSolarLongitudeDeg` alone), and exact UI copy:
     *"Annual activity forecast expired — no current-year forecast loaded."*
     (when expired) or *"No current-year annual activity forecast available for
     this shower."* (when not yet valid or omitted for this occurrence).
  3. **Identity/Radiant-Only Mode (`activitySource == null`, MDC-only baseline):**
     Never displays fabricated start/end dates or placeholder ZHR values.
     Displays shower identity, reference radiant / current & dark-window
     geometric radiant altitude (or `"Below horizon at this site"`), optional
     locally derived nominal peak reference date **only** when a source-qualified
     `nominalActivity.peakSolarLonDeg` is non-null (**never** from
     `referenceRadiant.referenceSolarLongitudeDeg` alone), and exact UI copy:
     *"Activity window and ZHR unavailable (identity/radiant snapshot only — no
     annual activity source loaded)."*
  - **Mandatory Slice M0/M1 mode-separation tests:** Verify (a) an MDC-only
    record with `nominalActivity: null` and non-null
    `referenceRadiant.referenceSolarLongitudeDeg` emits **no** derived peak
    date; and (b) a record with `activitySource != null`, a valid
    `annualForecasts` entry (source window + ZHR), and `nominalActivity: null`
    renders in **Annual Activity Mode** (never `"no annual activity source
    loaded"`).
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
