# Changelog

This file records the changes to HVO.SkyMonitor that operators and users notice. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

`[Unreleased]` records the work merged to `development/v1` since the last release. A release renames `[Unreleased]`
to its version and adds `docs/upgrade-notes/X.Y.Z.md` (install contract section 3). 0.9.0, the first release, starts
with the work merged from 2026-10-16 onward; earlier development is recorded in its pull requests.

## [Unreleased]

### Fixed

- Historical database-only restores refuse before planning or replacing stores when surviving authenticated location history requires a missing retained-integrity ledger, or when authority paths are malformed or uninspectable. Genuine unsealed legacy history remains compatible. Refs #1041

- A tamper found by the recent startup check now forces full byte verification before lifecycle readiness can recover. Complete-archive restore discards its staged clean checkpoint so preserved timestamps cannot inherit incremental trust. Refs #1041

- Protected installation verification uses writer-maintained integrity checkpoints. Startup checks a bounded recent capture window before serving health and recovery; full history qualification and capture recovery run in the background. Lifecycle operations refuse pending or invalid history within the existing 120-second budget. Clean restarts reuse authenticated unchanged-file metadata; full scrubs detect out-of-band tampering before the original daily deadline. Deferred configuration failures report unhealthy capture readiness and retry briefly; ordinary polls coalesce work. Managed pruning, missing-ledger refusal and paired upgrade/rollback recovery preserve retained evidence. Refs #1041
