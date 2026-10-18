# Changelog

This file records the changes to HVO.SkyMonitor that operators and users notice. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

No version has been released yet. `[Unreleased]` starts with the work merged to `development/v1` from 2026-10-16
onward; earlier development is recorded in its pull requests. A release renames `[Unreleased]` to its version and adds
`docs/upgrade-notes/X.Y.Z.md` (install contract section 3).

## [Unreleased]

### Added

- CameraAgent serves an operator settings catalogue and versioned group writes through its operations API. The
  site owner can create and revoke Read or ReadWrite API keys for non-browser clients. (#1316)
- `hvo-sky-install --version` prints the installer's version and the commit it was built from, as
  `<version>+<commit>`. A development build prints `0.0.0-dev+<commit>`, or `0.0.0-dev+unknown` if built without a
  checkout. (#1312)
- **Operations > Camera & rig** offers **Sky lighting** for a VirtualSky camera: uniform daylight (the default),
  realistic daylight, or no daylight. With no daylight, capture control treats every frame as night. (#1304)

### Changed

- Container builds and local development use .NET SDK 10.0.402, and .NET runtime base images use 10.0.13. (#1336, #1337)
- OpenTelemetry 1.20.0 corrects HTTP 5xx error tags and reduces routine OTLP exporter HTTP log noise. (#1339)
- A CameraAgent image names its version and commit, `<version>+<commit>`, in four places: the System > Software page,
  the startup log line, telemetry `service.version`, and the `org.opencontainers.image.version` image label. That label
  is no longer inherited from the Ubuntu base image. (#1305)
- `hvo-sky-install --help`, `-h` and `help` print usage on standard output and exit 0, and so does help for a single
  command (`hvo-sky-install cameraagent install --help`). A command-line mistake prints the reason and usage on standard
  error and exits 2, without the `Installation failed:` prefix. (#1302)
- The installer is `hvo-sky-install` (it was `hvo-skymonitor`), and `hvo-sky` is reserved for the runtime client.
  Release archives are `hvo-sky-install-v<version>-linux-<arch>.tar.gz`. Fixed locations use the flat `hvo-sky` style:
  the host policy is `/etc/hvo-sky/host.json`, the default product root is `/var/lib/hvo-sky`, the host-policy lock is
  `/run/hvo-sky-host-policy.lock`, per-user distribution state and cache are under `~/.local/state/hvo-sky` and
  `~/.cache/hvo-sky`, and the replay runner's default socket is `/run/hvo-sky/replay-runner.sock`. Nothing had been
  released, so there is no alias or migration: reinstall development hosts. (#1313)
- A CameraAgent upgrade or rollback no longer archives the instance's configuration and state with `tar`, so its
  time no longer grows with retained frames. It is a swap: the previous image is kept and restored if the candidate
  fails. Only a candidate that declares a state migration has the small SQLite databases snapshotted and verified
  first; a failed candidate is restored onto them, and a later `rollback` puts them back with a warning that database
  changes since the upgrade are lost. The snapshot covers the outbox of every configured storage root, and a restore
  never rewinds the capture-sequence, capture-control-version or fleet-report counters. An interrupted upgrade
  recovers onto its snapshot with `--resume` or `--restore-only`. Every client-held version token is carried too:
  the schedule, rig-selection, calibration, processing-graph and automation versions, and the row IDs in outbox
  action tokens. A store under a storage root named after the snapshot is removed by the restore, keeping its
  files. Every recovery authenticates its snapshot before it changes a container, and every restored directory is
  synced before the restore is recorded. `--migration-backward-compatible` is no longer needed and changes nothing.
  A candidate that left a store unreadable, or a value in it in a shape the installer does not read (a schedule
  profile stored as text, say), cannot block its own recovery or a rollback: the restore carries what it can and says
  on standard error what it could not read. Counter carrying is best effort: a counter that cannot be carried into the
  restored snapshot is named on standard error, and the restore completes. CameraAgent holds every deletion a restore could depend on while capture
  is paused, including upload cleanup, raw evidence quarantine and post-commit stage removal. A stage removal that a
  restart forgets is reclaimed from the projected-scene node's durable completion once capture runs. CameraAgent
  acknowledges a pause only after every deletion already running has finished. (#1314)
- Successful raw ingress SQLite commits and checkpoints are logged at Debug instead of Warning. A manual location
  override superseded by configuration is logged at Information. Warnings now mean something did not work. (#1303)

### Fixed

- Upgrade notes reject unsafe Unicode and visibly escape it in human output, preventing invisible formatting from
  disguising warnings or command names. (#1312)
- The Archive calendar (`/archive/calendar`) loads its video summaries again. Before this fix, every summary query
  failed with a SQLite syntax error. (#1301)

[Unreleased]: https://github.com/HualapaiValley/HVO.SkyMonitor/commits/development/v1
