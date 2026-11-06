# Bundled and refreshed Earth-orientation and leap-second data

Decision note for #1474 part B: slice B1 (the bundled pair) and slice B2 (the
managed refresh). The owner approved delivery on 2026-10-21: one approved pair
bundled with each release for offline bootstrap, then a separately enabled,
bounded refresh.

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
  validators by running `CameraAgentBundledTimeDataTests`. From the first file it
  replaces until that parse passes, any exit (an error, Ctrl+C, `SIGTERM` or a
  failed parse) restores the previous snapshot. Restored files get a fresh
  modification time, so an incremental build copies them again instead of keeping
  the rejected pair in its output. It prints the coverage for the release notes.
  `scripts/test:cameraagent-r2r` proves the rollback with fake `curl` and `dotnet`.
- **Publish check.** `scripts/release:check-cameraagent-publish` requires
  `time-data/provenance.json` in every release publish and verifies each file it
  lists against its SHA-256, so a release cannot ship without the pair or with
  bytes that differ from the record.

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
  never falls back to the bundled or the refreshed pair, and CameraAgent never
  writes the hand-configured files.
- Otherwise CameraAgent verifies and parses the bundled pair and, when the refresh
  has adopted one, the adopted managed pair and its retained predecessor,
  independently of whether the adopted pair still verifies.
  **Only pairs that continuously cover the time being planned are eligible: the
  planning window, or the current time for the status. Among them the pair whose
  planning coverage ends later is active. On a tie the bundled pair is active.**
  Planning coverage ends at the earlier of the last Earth-orientation row and the
  leap-second expiry.
- Why coverage of the planned time comes first: after USNO's daily update (about
  17:11 UTC) the file has only rapid (`I`) values for the current UTC day, which
  planning treats as a gap. A pair downloaded then cannot evaluate the rest of that
  day, although its coverage ends later. The pair it replaced stays active until
  00:00 UTC, when the new pair covers the day and takes over. A refresh is
  therefore used only when it covers the planned time and extends coverage, it
  never takes away coverage of the current day, and a newer release whose bundled
  pair covers at least as long is never shadowed by an older refresh.
- If no verified pair covers the planned time (all have expired, or the only pair
  was retrieved after that day's update), the same rule ranks every verified pair.
  The planner then reports the coverage end or the gap, and the status reports
  that no pair covers the current time.
- A managed pair that no longer verifies (a changed byte or a missing file) is
  reported as invalid. With a readable pointer, an intact retained predecessor
  remains eligible independently of the current pair; it competes with the bundled
  pair under the same coverage rule. A damaged pointer makes the managed pairs
  unavailable. If no pair verifies, planning stops with a reason.
  Every completed refresh invalidates the source's managed verification cache,
  including rejected, not-newer and failed attempts that leave the pointer
  unchanged. The next status or planning read rechecks the files; already loaded
  immutable data remains usable by concurrent planning reads during the refresh.
- The rule is deterministic: the same files, options and requested time always
  select the same pair. It does not depend on the refresh switch, so turning the
  refresh off keeps an already adopted pair eligible.

## Coverage and expiry

The setup panel shows the active source, each file's status, path, SHA-256 and
provenance, the final (Bulletin B) and predicted (Bulletin A) Earth-orientation
spans, whether the checked window uses final or predicted values, and the
leap-second list's exclusive expiry. Planning is supported up to the earlier of the
last Earth-orientation row and the leap-second expiry. Past that, the planner
returns `TimeInputsUnavailable` with the coverage end, and the panel reports
`coverage-expired`. Neither ever substitutes nominal zero Earth orientation.

The snapshot retrieved on 2026-10-21 has final values to 2026-09-01, rapid values to
2026-10-21, predictions from 2026-10-22 to 2027-10-28 and a leap-second list that
expires on 2027-06-28. Rapid values are a gap, so planning with it is supported up
to 2026-09-01 and from 2026-10-22 00:00 UTC until 2027-06-27 23:59:59 UTC.

## Managed refresh

Slice B2 adds a separately enabled refresh that keeps the pair current between
releases. It is off by default.

- **Switch.** `timeDataRefresh.enabled` in the settings catalogue
  (`CameraAgent:SatellitePlanning:TimeDataRefreshEnabled`), shown as **Time-data
  refresh** in Observatory & location and in `hvo-sky config`. It applies
  immediately. Environment variables and `config/secrets` override it as for the
  other switches.
- **Trigger.** Only the reserved automation `time-data-refresh` (task
  `TimeDataRefresh`, target `iers-usno`, periodic, exactly 86,400 seconds) runs it,
  so the automation runner owns its cadence, run journal, Run now and restart
  recovery, as for the orbital and space-weather refreshes. Nothing refreshes time
  data on a capture or planning request. Saving the definition while the switch is
  off is allowed; runs then skip with `time-data.disabled`.
- **At most daily, single flight.** One attempt runs at a time in a process, and an
  attempt starts at most once per 24 hours. The start time is written to
  `refresh.json` before the first request, so neither Run now nor a restart adds
  an attempt. A daily occurrence up to two minutes early waits out the remainder.
  This is an application limit; neither provider publishes an allowance.
- **Endpoints and bounds.** Fixed in code, HTTPS only, no redirects, cookies or
  credentials, one request each with a 60-second deadline and no retry:
  - `https://maia.usno.navy.mil/ser7/finals2000A.daily.extended` (8 MiB bound);
  - `https://maia.usno.navy.mil/ser7/checksums.sha512` (64 KiB bound);
  - `https://hpiers.obspm.fr/iers/bul/bulc/ntp/leap-seconds.list` (64 KiB bound).
  These are the sources and terms of the bundled pair. The refresh records the same
  terms statements. It checks the leap file's own public-domain statement on every
  download; the USNO products page is checked by `scripts/time-data:update` at
  release time rather than fetched daily.
- **Verification before adoption.** The Earth-orientation file must match the
  SHA-512 that `checksums.sha512` lists for it exactly once, and the leap file must
  match its own `#h` SHA-1. Both files are then parsed with the product validators;
  a pair without Earth-orientation values is refused. Coverage always comes from
  the parsed files, never from the download time, so a successful download never
  extends coverage by itself and nominal-zero Earth orientation is never
  substituted.
- **Unchanged and not newer.** A download identical to the adopted pair is recorded
  as `unchanged`. One whose planning coverage ends before the adopted pair's is
  `not-newer` and is not adopted. A download is also `not-newer` when neither it
  nor any intact adopted current pair covers the current time and adopting it
  would retire the intact predecessor, which does. This guard also applies when
  the current pair is damaged. Two attempts on one UTC day can also reach it
  when `refresh.json` was lost or the clock went back.
- **Atomic adoption.** The pair is kept under the CameraAgent's writable state at
  `<RawIngressRoot>/.time-data/` (`state/raw/.time-data/` on an installed host), so
  it survives restarts and upgrades. Each adopted pair is an immutable directory
  `pairs/<id>/` holding the original bytes and a `provenance.json` (schema
  `hvo-managed-time-data-v1`, the same fields as the bundled record: source URL,
  retrieval time, HTTP `Last-Modified`, length, SHA-256, provider checksum, terms).
  The files are flushed first; replacing `current.json` is the single commit point.
  A stop before it leaves the prior pair active and an unreferenced directory that
  the next adoption removes. The current pair and an intact predecessor are kept
  when one exists. The predecessor can skip a damaged current pair or retain an
  older intact pair when it alone covers the adoption time. Retirement never
  deletes that referenced pair.
  `current.json` also keeps the last 32 adoptions (pair, adoption and retrieval
  times, both SHA-256 values), so results computed with a retired pair stay
  attributable.
- **Failure.** Any failed download, checksum, parse or write leaves the adopted pair
  active and is recorded in `refresh.json` with a fixed `time-data.*` reason code.
  A refresh failure never marks the active pair expired, and an expired pair is
  reported whatever the last refresh did.
- **Status.** The satellite setup panel, `GET /api/v1/operations/time-data`
  (operations read access, the same owner session or API key as the other
  operations reads) and `hvo-sky time-data` show the active source (`override`,
  `managed` or `bundled`), its planning coverage end, Earth-orientation end and
  leap-second expiry, whether it has expired, whether it covers the current time
  (`coversNow`; false means planning from now fails), the switch and whether the daily
  automation exists, the last attempt with its outcome (`never`, `in-progress`,
  `interrupted`, `adopted`, `unchanged`, `not-newer` or `failed`), reason and next
  permitted attempt, and the last adoption.
- **State contract.** `.time-data/` holds plain files, not a schema-versioned
  database, so it is not in the image's state-contract label or the installer's
  snapshot set. A release that does not know it ignores it, and a damaged or
  unknown pointer is reported as an invalid managed pair while the bundled pair is
  used. Like the other appended automation task kinds, a created
  `time-data-refresh` definition names `TimeDataRefresh`, which a release from
  before this slice cannot read: upgrade forward.
