# Changelog

This file records the changes to HVO.SkyMonitor that operators and users notice. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

`[Unreleased]` records the work merged to `development/v1` since the last release. A release renames `[Unreleased]`
to its version and adds `docs/upgrade-notes/X.Y.Z.md` (install contract section 3). 0.9.0, the first release, starts
with the work merged from 2026-10-16 onward; earlier development is recorded in its pull requests.

## [Unreleased]

### Changed

- The approved SkyMonitor recovery contract now limits rollback to a pending upgrade's complete checkpoint and matching image/config/catalog, with an operator-managed CameraAgent path when checkpoint protection is unavailable. Runtime adoption and qualification remain separate work. (#1606)

- An optional, off-by-default **Time-data refresh** (`timeDataRefresh.enabled` in the web UI and `hvo-sky config`) keeps satellite planning's Earth-orientation and leap-second pair current between releases. The reserved daily `time-data-refresh` automation downloads the files from IERS/USNO over HTTPS at most once a day, checks the providers' SHA-512 and `#h` checksums, parses the pair and adopts it atomically under `state/raw/.time-data/`; any failure keeps the current pair. Without an override, planning uses whichever verified pair (refreshed, its intact retained predecessor, or bundled) covers the planned time and covers later (the bundled pair on a tie), so a refresh never takes away coverage of the current day. The setup panel, the new read-only `GET /api/v1/operations/time-data` and `hvo-sky time-data` show the active source, coverage and expiry, whether any pair covers the current time, the last refresh attempt and the last adoption. (#1474)

- Satellite planning works without hand-placed time files: each CameraAgent release bundles one hash-verified IERS/USNO finals2000A Earth-orientation file and one IERS `leap-seconds.list` with their provenance, and the satellite setup panel shows the active source, final and predicted Earth-orientation coverage and the leap-second expiry. Hand-configured `EarthOrientationPath`/`LeapSecondsPath` overrides keep precedence; an incomplete or invalid override stops planning instead of falling back, and planning stops with a reason when the bundled coverage runs out. (#1474)

- Current Sky streams neutral loading progress during its first HTTP page load, gives that read up to 30 seconds, identifies later five-second page refresh misses beside the image, retries automatically and logs the slow refresh phase without implying a capture fault. (#1472)

- Regression tests verify bounded CameraAgent snapshot reads reject invalid timeouts and release their gates after projection failures. (#1477)

- Space-weather storage rejects disguised application schema objects and resolves orphan notices correctly when linked groups join. (#1398)

- A dedicated STRICT SQLite store keeps space-weather snapshots, alert revisions and interrupted-attempt state durable, with bounded history and local diagnostics. (#1398)


### Fixed
- Interrupted existing-services teardown can resume after a CameraAgent has stopped, verifying its pinned Docker state without requiring HTTP from the stopped application. (#1589)
- Weather and cloud overlay labels honor short and zero character budgets, including when labels are hidden, without failing the preview layout. (#1564)
