# Changelog

This file records the changes to HVO.SkyMonitor that operators and users notice. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

No version has been released yet. `[Unreleased]` starts with the work merged to `development/v1` from 2026-10-16
onward; earlier development is recorded in its pull requests. A release renames `[Unreleased]` to its version and adds
`docs/upgrade-notes/X.Y.Z.md` (install contract section 3).

## [Unreleased]

### Added

- `hvo-skymonitor --version` prints the installer's version and the commit it was built from, as
  `<version>+<commit>`. A development build prints `0.0.0-dev+<commit>`, or `0.0.0-dev+unknown` if built without a
  checkout. (#1312)
- **Operations > Camera & rig** offers **Sky lighting** for a VirtualSky camera: uniform daylight (the default),
  realistic daylight, or no daylight. With no daylight, capture control treats every frame as night. (#1304)

### Changed

- A CameraAgent image names its version and commit, `<version>+<commit>`, in four places: the System > Software page,
  the startup log line, telemetry `service.version`, and the `org.opencontainers.image.version` image label. That label
  is no longer inherited from the Ubuntu base image. (#1305)
- `hvo-skymonitor --help`, `-h` and `help` print usage on standard output and exit 0, and so does help for a single
  command (`hvo-skymonitor cameraagent install --help`). A command-line mistake prints the reason and usage on standard
  error and exits 2, without the `Installation failed:` prefix. (#1302)
- Successful raw ingress SQLite commits and checkpoints are logged at Debug instead of Warning. A manual location
  override superseded by configuration is logged at Information. Warnings now mean something did not work. (#1303)

### Fixed

- Upgrade notes reject unsafe Unicode and visibly escape it in human output, preventing invisible formatting from
  disguising warnings or command names. (#1312)
- The Archive calendar (`/archive/calendar`) loads its video summaries again. Before this fix, every summary query
  failed with a SQLite syntax error. (#1301)

[Unreleased]: https://github.com/HualapaiValley/HVO.SkyMonitor/commits/development/v1
