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
- Before an upgrade from a release, `hvo-sky-install cameraagent upgrade` prints the upgrade notes of every release
  after the installed version, up to the target, and refuses a target older than the installed version. With `--json`
  the notes are one `upgrade-notes` event on standard error. Note text follows one terminal-safety rule: CRLF becomes
  LF, and a note holding any other control character except LF and TAB, a format character, a line or paragraph
  separator, a private-use character or a noncharacter is refused, naming its `U+XXXX`. Such a character in a note
  built another way is shown as `<U+XXXX>`. (#1312)
- `hvo-sky-install cameraagent install` and `cameraagent upgrade` take `--release <folder>`, a release's downloaded
  files. Before contacting Docker, the installer checks `release.json` against `SHA256SUMS` and every file it uses
  against both, and refuses a folder whose files disagree. `--image-source archive`, the default, loads the platform's
  offline archive; `--image-source registry` pulls the image by the digest `release.json` names. The image must carry
  the release's version and commit. The instance records the release it runs, which is how an upgrade knows the
  installed version. (#1309)
- Integration pre-releases. An annotated `vX.Y.Z-dev.<n>` tag that the owner pushes on a green `development/v1` commit
  publishes a GitHub pre-release. It carries both installers, the CameraAgent offline archives, the catalog bundle,
  `release.json` and `SHA256SUMS`, and pushes `ghcr.io/hualapaivalley/skymonitor-cameraagent:<version>`. Every file and
  the image have a build-provenance attestation. A dedicated arm64 runner installs and upgrades from the draft before it
  is published, and a pre-release never moves `latest`. (#1309)

### Changed

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
- Successful raw ingress SQLite commits and checkpoints are logged at Debug instead of Warning. A manual location
  override superseded by configuration is logged at Information. Warnings now mean something did not work. (#1303)
- `hvo-sky-install` is one self-contained file. Its native SQLite library is bundled in, rather than published beside
  it, and is extracted when it starts, under `~/.net` or `$DOTNET_BUNDLE_EXTRACT_BASE_DIR`. (#1309)

### Removed

- The signed-release options, which the owner retired on 2026-10-16: `--channel`, `--image-manifest`, `--image-index`,
  `--image-version`, `--catalog-manifest`, `--catalog-index` and `--asset-base-url`, and `--catalog-version` on
  `cameraagent install`. With them go `catalog check`, the installer's download cache and index rollback state, and
  its use of `HVO_GITHUB_TOKEN`. Install from a release folder with `--release` instead. `cameraagent install` and
  `catalog install` require `--catalog-bundle`, and `cameraagent preflight` takes the candidate only as
  `--image-ref`. (#1309)

### Fixed

- The Archive calendar (`/archive/calendar`) loads its video summaries again. Before this fix, every summary query
  failed with a SQLite syntax error. (#1301)

[Unreleased]: https://github.com/HualapaiValley/HVO.SkyMonitor/commits/development/v1
