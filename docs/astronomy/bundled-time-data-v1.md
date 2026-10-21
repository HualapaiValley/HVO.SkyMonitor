# Bundled Earth-orientation and leap-second data

Decision note for #1474 part B, slice B1. The owner approved delivery on
2026-10-21: one approved pair bundled with each release for offline bootstrap
(this slice), then a separately enabled, bounded refresh (slice B2).

## Decision

CameraAgent ships one IERS/USNO finals2000A Earth-orientation file and one IERS
`leap-seconds.list` with every release. Satellite planning uses that pair unless
an operator configures an override.

- **Vendored snapshot.** The files live in the repository under
  `src/HVO.SkyMonitor.CameraAgent.Common/OrbitalPlanning/TimeData/`, byte for byte
  as the providers publish them, with `provenance.json`. Every build output and the
  CameraAgent image carry them at `time-data/` beside the application
  (`/app/time-data/` in the image). Builds, images and CI read only the committed
  snapshot and never use the network for it. CameraAgent never writes these files.
- **Not a release asset.** A separate asset installed by the installer would need
  Deployment CLI, release-manifest and installer changes, and a second artifact to
  keep in step with each image. The vendored files need one project item, so they
  are the smaller change.
- **Update command.** Before a release tag, the coordinator runs
  `scripts/time-data:update` on a Linux host and commits the result. The command
  downloads from the approved HTTPS endpoints without redirects and within the
  loader's size bounds. It verifies each provider checksum and terms statement,
  rewrites the files and `provenance.json`, and parses the pair with the product's
  validators by running `CameraAgentBundledTimeDataTests`. If that parse fails, it
  restores the previous snapshot. It prints the coverage for the release notes.

## Product choice

| Product | Content | Predictions | Size |
| --- | --- | --- | --- |
| `finals2000A.daily` | last 90 days | 90 days | about 34 KB |
| `finals2000A.daily.extended` (chosen) | since 2 January 1973, updated daily | one year | about 3.8 MB |

The existing parser accepts both, and both are well inside its 8 MiB and 100,000-row
bounds. It uses Bulletin B values as final rows and Bulletin A `P` rows as
predicted rows. Bulletin A rapid observed-only (`I`) rows stay gaps, so neither
product covers the few weeks between the last Bulletin B value and the file date.
The difference is the prediction span. With the daily product, a release's pair
would expire about three months after the snapshot, often before the next
release. The extended product predicts a year ahead, so the leap-second list's
semi-annual expiry becomes the practical limit. It also gives final coverage for
past windows. Its history rows rarely change between updates, so Git stores each
refresh as a small delta. `finals2000A.all` has the same content but is only
updated weekly.

The leap-second list comes from the IERS Earth Orientation Center, the primary
source. On 2026-10-21 its bytes were identical to the IANA tz database copy
(`https://data.iana.org/time-zones/tzdb/leap-seconds.list`), which remains an
alternative.

## Provider terms

Checked 2026-10-21. The update command refuses to proceed if either statement
disappears.

- **USNO.** The [daily products page](https://maia.usno.navy.mil/products/daily.htm)
  states "Distribution Statement A. Approved for public release: distribution
  unlimited." USNO publishes SHA-512 checksums for these files in
  [`checksums.sha512`](https://maia.usno.navy.mil/ser7/checksums.sha512).
- **IERS.** [`leap-seconds.list`](https://hpiers.obspm.fr/iers/bul/bulc/ntp/leap-seconds.list)
  states "This file is in the public domain." It carries its own SHA-1 (`#h`) over
  the update time, the expiry and the leap rows, as its
  [README](https://hpiers.obspm.fr/iers/bul/bulc/ntp/sources/README) describes.
  The [tz licence](https://data.iana.org/time-zones/tzdb/LICENSE) also places the
  IANA copy in the public domain.

`THIRD-PARTY-NOTICES.md`, which ships with CameraAgent, names both sources and
their statements. The files keep their original headers.

## Provenance and integrity

`provenance.json` (schema `hvo-bundled-time-data-v1`) records, for each file: the
file name, product, source URL, retrieval time, the HTTP `Last-Modified` time, the
length, the SHA-256 of the exact bytes, the provider checksum with its algorithm
and URL, and the terms statement with its URL.

On each adoption, CameraAgent reads `provenance.json` within a 64 KiB bound. It
requires simple file names, HTTPS source URLs and a UTC retrieval time, then
verifies each file's SHA-256 against the record and parses the pair with the
existing validators. A mismatch, a missing file or a malformed record makes the
bundled pair unavailable; nothing is substituted. SHA-256 identifies the bytes.
TLS to the approved endpoints, the provider checksums verified at update time and
the reviewed commit establish their origin.

## Source precedence

- If any of `EarthOrientationPath`, `EarthOrientationSha256`, `LeapSecondsPath` or
  `LeapSecondsSha256` is set, the override is the active source and all four must
  be valid. An incomplete or invalid override stops planning with a reason. It
  never falls back to the bundled pair, and CameraAgent never writes the
  hand-configured files.
- Otherwise the bundled pair is the active source.
- Either source is pinned: only an options change or a new release adopts a
  different pair.

## Coverage and expiry

The setup panel shows the active source, each file's status, path, SHA-256 and
provenance, the final (Bulletin B) and predicted (Bulletin A) Earth-orientation
spans, whether the checked window uses final or predicted values, and the
leap-second list's exclusive expiry. Planning is supported up to the earlier of the
last Earth-orientation row and the leap-second expiry. Past that, the planner
returns `TimeInputsUnavailable` with the coverage end, and the panel reports
`coverage-expired`. Neither ever substitutes nominal zero Earth orientation.

The snapshot retrieved on 2026-10-21 has final values to 2026-09-01, predictions to
2027-10-28 and a leap-second list that expires on 2027-06-28, so planning with it
is supported until 2027-06-27 23:59:59 UTC.

## Seams for slice B2

`ICameraAgentOrbitalTimeSource.ResolveAsync` returns the active source with the
pair. A managed, refreshed pair would become a third source, ranked between the
override and the bundled pair, which stays the offline fallback. B2 owns its
download bounds, single-flight schedule, atomic adoption that keeps the prior
valid pair, and operator switch. B1 makes no network request at runtime.
