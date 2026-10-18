# Changelog

This file records the changes to HVO.SkyMonitor that operators and users notice. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

No version has been released yet. `[Unreleased]` starts with the work merged to `development/v1` from 2026-10-16
onward; earlier development is recorded in its pull requests. A release renames `[Unreleased]` to its version and adds
`docs/upgrade-notes/X.Y.Z.md` (install contract section 3).

## [Unreleased]

### Added

- `hvo-sky-install` follows the shared install contract's command surface. `--plan` (the new name of `--dry-run`)
  reports what install, a re-run of one, `upgrade`, `rollback`, `reinstall`, `uninstall` and `purge` would create,
  change, leave unchanged or remove, after the same checks the command makes, and writes nothing, not even a temporary
  file or a lock. `cameraagent install --answers FILE` installs from a strict,
  versioned JSON answers file in which an unknown member is an error. Running an install again with the same answers
  prints `Nothing to change`, and writes and restarts nothing. (#1315)
- The installer keeps a machine install record, `/etc/hvo-sky/install.json`: each instance's condition, images, URL
  and container limits, the release version it runs, the one before, and the one a rollback left, and no secret. The
  versions are read from the instance's own release records, which stay their only source. Every completed operation
  updates it, through a narrow `sudo` helper because the file is root-owned. (#1315)
- Re-running an install with new `--cpus` or `--memory` changes an installed instance's container limits: the
  container is recreated with them and must come back healthy, or the recorded limits are restored. The host check
  and the 2 CPU and 4 GiB floor still apply. (#1315)
- Each release carries `install.sh`, the one-line entry point: `curl -fsSL
  https://github.com/HualapaiValley/HVO.SkyMonitor/releases/download/v<version>/install.sh | bash -s -- cameraagent install
  ...`. Before it changes anything it checks the platform (64-bit Linux with glibc), `curl`, `tar` and `sha256sum`,
  free space, the clock, github.com and ghcr.io, and, for an install or an upgrade, Docker 20.10 or later with Compose v2
  that enforces CameraAgent's memory, CPU and PID limits (naming the `cgroup_enable=memory` remedy on a Raspberry Pi)
  and, online, `docker buildx` 0.8 or later. It then downloads that release's `hvo-sky-install`, `SHA256SUMS`,
  `release.json` and catalog, checks each against `SHA256SUMS` (and the attestations when `gh` is signed in) and the
  installer against its `--version`, and runs it as you. `cameraagent install` and `upgrade` are given the release
  (`--release`, with `--image-source registry`, so the image is pulled from ghcr.io by its digest), and `cameraagent
  install` the release's catalog (`--catalog-bundle`, unpacked in `~/.cache/hvo-sky/catalogs`). With no installer
  arguments it runs `cameraagent install`. `--check` stops after the checks. `--from DIR` takes everything from a downloaded release folder, the offline image archive included, and
  never contacts ghcr.io. Exit codes are the install contract's: 3 when it refused and nothing changed, 1 when a
  download failed, 2 for an invalid command line and 130 when interrupted. (#1311)
- CameraAgent serves an operator settings catalogue and versioned group writes through its operations API. The
  site owner can create and revoke Read or ReadWrite API keys for non-browser clients. (#1316)
- `hvo-sky-install --version` prints the installer's version and the commit it was built from, as
  `<version>+<commit>`. A development build prints the product version's development build, such as
  `0.9.0-dev+<commit>`, or `0.9.0-dev+unknown` if built without a checkout. (#1312, #1310)
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
  offline archive; `--image-source registry` pulls the image by the digest `release.json` names, and needs `docker
  buildx` 0.8 or later. The image must be the release's, carry its version and commit, and declare the contracts the
  instance needs. It is checked before anything is imported or claimed. An archive's image is read from the archive,
  every layer included, and an archive whose entries a loader could read differently (an alias such as
  `metadata/../index.json`, a duplicate, a link, or an entry no OCI layout has) is refused, and so is a JSON field
  Docker would read differently from the installer: a field the release tool does not write, or one spelled two ways
  that Docker's decoder reads as one (`RepoTags` and `repotags`, or a Unicode fold such as `RepoTagſ`). A registry
  image is read from the registry with `docker buildx imagetools inspect` before anything is pulled. A new
  installation then loads or pulls the image before it creates any state, and an import that is refused, fails or is
  cancelled is removed again. JSON in a release that names a property twice is refused. The instance records the
  release it runs, and keeps the previous image's record so a rollback restores it, which is how an upgrade knows the
  installed version and refuses an older one. (#1309)
- Integration pre-releases. An annotated `vX.Y.Z-dev.<n>` tag that the owner pushes on a green `development/v1` commit
  publishes a GitHub pre-release. It carries both installers, the CameraAgent offline archives, the catalog bundle,
  `release.json` and `SHA256SUMS`, and pushes `ghcr.io/hualapaivalley/skymonitor-cameraagent:<version>`. Every file and
  the image have a build-provenance attestation. A dedicated arm64 runner installs and upgrades from the draft before it
  is published, and a pre-release never moves `latest`. (#1309)
- Final releases and release candidates. An annotated `vX.Y.Z` or `vX.Y.Z-rc.<n>` tag that the owner pushes on `main`
  runs the CI workflow on the tagged commit, builds the same files a pre-release carries, and creates a draft release.
  A throwaway instance installs and upgrades from the draft on linux-x64 and on linux-arm64, and the draft is published
  only after the owner approves it in the `production-release` environment; the workflow refuses to publish while that
  environment requires no reviewer. A final release needs its `CHANGELOG.md` section and `docs/upgrade-notes/X.Y.Z.md`,
  whose notes `release.json` carries. Only the newest final release, by version, becomes the latest release and moves
  the image's `latest` tag; a release candidate or a fix to an older version never does. (#1310)

### Changed

- Installer exit codes follow the install contract on every command: `0` done, `1` a step failed, `2` invalid command
  line or answers, `3` refused with nothing changed, `130` interrupted. Refusals that used to exit `1` now exit `3`,
  among them unmet host prerequisites, an incompatible `cameraagent preflight`, running as root, an upgrade to the image
  already active, an untrusted host policy, and every refusal of a `--release` folder or of an image that is not the
  release's, a registry that cannot be read included; an unreadable `--config` file now exits `2`. A refusal says
  `Installation refused: ... Nothing was changed.`, or with `--json` reports `errorCode` `refused`. (#1315)
- A re-run of a completed install is checked against the instance its records describe rather than against the first
  request's exact inputs: anything but the container limits that differs is refused and named. (#1315)
- One product version: `VersionPrefix` in `Directory.Build.props`, now `0.9.0`, the first final release's (owner
  decision 2026-10-18). A release's tag, files, installers and image carry it or a pre-release of it, and a build given
  any other version is refused. A build given no version is its development build, `0.9.0-dev`:
  `hvo-sky-install --version` prints `0.9.0-dev+<commit>` rather than `0.0.0-dev+<commit>`, and a development
  CameraAgent image's version label is empty. (#1310)
- Container builds and local development use .NET SDK 10.0.402, and .NET runtime base images use 10.0.13. (#1336, #1337)
- OpenTelemetry 1.20.0 corrects HTTP 5xx error tags and reduces routine OTLP exporter HTTP log noise. (#1339)
- A CameraAgent image names its version and commit, `<version>+<commit>`, in four places: the System > Software page,
  the startup log line, telemetry `service.version`, and the `org.opencontainers.image.version` image label. That label
  is no longer inherited from the Ubuntu base image. (#1305)
- A release follows install contract draft 4. `release.json` names the CameraAgent image by its component key,
  `images.cameraAgent`, with `skymonitor-cameraagent` in its `repository`, and the installer reads no other key. It
  adds `stateContracts.cameraAgent`: for each SQLite store an instance keeps, the schema the release writes and the
  schemas it reads, as the image's new `io.hvo.skymonitor.state-contracts` label declares them. The image carries
  `org.opencontainers.image.created`, the release's `created` time. The release build, the pre-release workflow and
  the installer refuse an image without that time or with another. (#1359)
- `hvo-sky-install --help`, `-h` and `help` print usage on standard output and exit 0, and so does help for a single
  command (`hvo-sky-install cameraagent install --help`). A command-line mistake prints the reason and usage on standard
  error and exits 2, without the `Installation failed:` prefix. (#1302)
- The installer is `hvo-sky-install` (it was `hvo-skymonitor`), and `hvo-sky` is reserved for the runtime client.
  A release's installers are `hvo-sky-install-linux-x64` and `hvo-sky-install-linux-arm64`. Fixed locations use the
  flat `hvo-sky` style: the host policy is `/etc/hvo-sky/host.json`, the default product root is `/var/lib/hvo-sky`, the
  host-policy lock is `/run/hvo-sky-host-policy.lock`, per-user distribution state and cache are under `~/.local/state/hvo-sky` and
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
- `hvo-sky-install` is one self-contained file. Its native SQLite library is bundled in, rather than published beside
  it, and is extracted when it starts, under `~/.net` or `$DOTNET_BUNDLE_EXTRACT_BASE_DIR`. (#1309)

### Removed

- The signed-release options, which the owner retired on 2026-10-16: `--channel`, `--image-manifest`, `--image-index`,
  `--image-version`, `--catalog-manifest`, `--catalog-index` and `--asset-base-url`, and `--catalog-version` on
  `cameraagent install`. With them go `catalog check`, the installer's download cache and index rollback state, and
  its use of `HVO_GITHUB_TOKEN`. Install from a release folder with `--release` instead. `cameraagent install` and
  `catalog install` require `--catalog-bundle`, and `cameraagent preflight` takes the candidate only as
  `--image-ref`. (#1309)
- The key-signed release path, which the owner retired on 2026-10-16: the `Signed Distribution Release` workflow and
  its Azure Key Vault signing job, the separate installer, catalog and image release trains with their signed manifests,
  indexes, SBOM and provenance files, the embedded P-256 trust root, the bootstrap script
  `scripts/install-hvo-sky-install.sh`, and the release tool's signing and train commands. A release is trusted by
  `SHA256SUMS` and its build-provenance attestations. (#1310)

### Deprecated

- `--config FILE` is deprecated in favour of `--answers FILE` (the same members, plus `"schemaVersion": 1`, without
  `dryRun`, `resume` and `json`). `--migration-backward-compatible` is deprecated: it has had no effect since #1314 and
  now warns; it stays accepted so an interrupted upgrade begun with it still resumes. (#1315)

### Fixed

- The Archive calendar (`/archive/calendar`) loads its video summaries again. Before this fix, every summary query
  failed with a SQLite syntax error. (#1301)
- The installer's remedy for a Docker daemon that cannot enforce the CameraAgent limits
  (`docker-memory-limit-unsupported` and its CPU and PIDs companions) now says that on a Raspberry Pi the firmware, not
  `/boot/firmware/cmdline.txt`, adds `cgroup_disable=memory`. There is nothing in that file to change: append
  `cgroup_enable=memory` to its single line and reboot. (#1309)

[Unreleased]: https://github.com/HualapaiValley/HVO.SkyMonitor/commits/development/v1
