# Changelog

This file records the changes to HVO.SkyMonitor that operators and users notice. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

`[Unreleased]` records the work merged to `development/v1` since the last release. A release renames `[Unreleased]`
to its version and adds `docs/upgrade-notes/X.Y.Z.md` (install contract section 3). 0.9.0, the first release, starts
with the work merged from 2026-10-16 onward; earlier development is recorded in its pull requests.

## [Unreleased]

### Fixed

- CameraAgent restore warnings identify every omitted counter and its escaped key; carry trials budget for rollback journals while keeping space for the restore. Pure image swaps keep their archive/headroom admission without a snapshot reservation, and carry failure reasons escape automation keys. (#1347)
- LogicHost uses stable bounded query ordering for projected-scene checks, diagnostics and transient submission conflicts, and keeps publication event and version identities paired when reading history. (#1444)


### Added

- Shared space-weather storage contracts define snapshot comparison, atomic completion and bounded local history for the upcoming SWPC refresh service. (#1398)

- Release installs and upgrades put verified `hvo-sky` and `hvo-sky-install` commands on PATH, keep replaced programs
  as `.previous`, restore matching commands on rollback, and report their paths in plans and install records. Resume
  and reinstall hints use the durable installer; uninstall and purge honor shared ownership. Publication refuses
  unsafe folders and changed or unowned recovery copies, and program metadata uses a separate sidecar so released
  installers can still read the install record. Completed legacy install re-runs also place missing commands. (#1385)

- Space-weather fact model (#1398 S1).
- Software & Catalog shows orbital snapshot coverage, element age, refresh status and storage repair guidance, with
  bounded Refresh and Resume commands. Automations edits the same orbital refresh schedule and links to its run
  history. Missing orbital data never implies an empty sky. The Automations UI service applies the 1–128-character
  operator actor bound and logs failures without exception text. The Observatory pass area shows the same orbital
  data notice. (#1392)
- Local satellite pass planning from verified local snapshots and explicitly pinned time data for the selected observatory and rig, with separate full-pass diagrams and retained predicted capture layers, thin dashed paths and small point markers, explicit data availability and source age, and independent presentation controls. (#1393)
- Offline tools evaluate causal streak detections across retained virtual-camera sequences and compare every measured candidate with explicitly supplied satellite predictions, while preserving missing context, competing alternatives and unavailable observer or geometry facts. (#530)
- CameraAgent can acquire orbital elements locally: one bounded CelesTrak GP request for the `active` group, strictly
  parsed into the `orbital-elements-v1` contract, published as an immutable verified snapshot and activated atomically,
  with the last-known-good snapshot kept readable through every failure. Refresh is off by default. Turn it on with
  `CameraAgent:OrbitalElements:Enabled` and a reserved `orbital-elements-refresh` automation definition (at least two
  hours apart). It needs and accepts no credentials. At most one request is made per two hours, with no automatic retry
  and no conditional request. Any non-200 response, a status line outside 100 to 599, or a request whose outcome was
  never recorded halts refresh until an owner resumes it. Owners can read status, queue a refresh and resume a halt at
  `/api/v1/operations/orbital-elements` (antiforgery or a read-write API key, plus an `Idempotency-Key`). There is an
  `orbital-elements` health entry: element freshness comes from the newest element epoch against a 48-hour envelope,
  and an overdue refresh is reported separately. Metrics are on the `HVO.SkyMonitor.CameraAgent.OrbitalElements`
  meter. Raw provider bytes are never sent to LogicHost or any other service; local `cameraagent backup` archives
  include them, so keep those archives owner-only. The image's `io.hvo.skymonitor.state-contracts` label, and so
  `release.json`, now declares the `orbitalElements` store (writes 1, reads 1 to 1). Installer snapshots exclude it, so
  the provider floor and halt survive a rollback. Operation, removal, backup, restore and recovery are in
  `docs/runbooks/orbital-elements-refresh.md`.
  (#528)
- Bounded local satellite exposure tracks, future-pass predictions and complete sky arcs pinned to their original orbital record, with reproducible virtual-camera fixtures, explicit source/time/geometry provenance and unavailable or partial outcomes when inputs or work limits prevent completion. (#529)

### Changed

- Star markers are the original plain grey (`#909090`) circle outline again, without the crosshair inside them; so is
  the marker of a Sun or Moon too small to outline. Captures already retained keep the markers they were drawn with,
  and time-lapse frames, which draw no star markers, are unchanged. (#1415)
- The pre-release and release **Build** jobs may run for 120 minutes (it was 60): the arm64 image is built under
  emulation, and its publish alone has taken 46 minutes, which cancelled the first `0.9.1` pre-release. (#1410)
- The product version is `0.9.1`, the next release (owner decision 2026-10-19). Integration pre-releases are
  `0.9.1-dev.<n>`, and a build given no version is `0.9.1-dev`. (#1406)
- Sky overlays are drawn in their original colours again, instead of the purple and cyan theme that did not read well on
  the sky image: solid 2 px blue (`#60A0FF`) constellation lines, grey (`#909090`) star markers, white star names,
  cardinal letters and corner text, a 1 px grey (`#606060`) image circle at full opacity and an orange-red (`#FF4020`)
  cloud mask. The dark badge behind N, E, S and W and the dark corner-text panel stay, star markers have no crosshair
  (#1415), and the Current Sky layer swatches match the overlays. Captures already retained keep the overlays they were
  drawn with, and time-lapse frames are unchanged. (#1383)
- Before a durable copy is installed, the installer names itself by the full path it runs from, such as
  `/opt/hvo-sky/hvo-sky-install-linux-arm64`, in its usage and in every command a message tells you to run (a `Resume:`
  line, the rollback an upgrade names), so the command runs as printed. Each command's `--help` says what the command does
  and lists only its own options, each described, and `cameraagent --help`, `catalog --help` and `host --help` list
  that group's commands. An option a command does not take is still a usage error (exit 2). (#1384)
- Current Sky moves to a newer capture only once its overlays are complete, or once its processing has ended without
  them, and keeps showing the current capture until then. It changes image at most once per
  `currentSky.minimumRefreshSeconds` (default 15 seconds, 15 to 3,600), a new operator setting on the Observatory &
  location page under Operator settings, and then shows the newest complete capture, skipping any in between.
  Refresh image skips the wait. (#1382)

### Fixed

- Retained time-lapse jobs keep their released renderer and recipe identity across upgrades; new jobs restore white cardinal and corner text and the grey image circle under a distinct renderer version. (#1401)

- CameraAgent no longer warns about unordered database queries during API-key authentication or local owner recovery;
  ambiguous matches continue to be rejected. (#1437)

- CameraAgent and LogicHost images no longer warn about overriding the runtime image's HTTP port default during
  their intended startup binding; listening addresses and ports are unchanged. (#1436)

- While the newest capture is still processing, Current Sky no longer skips a finished capture it could show when the
  retained history spans two agent identities whose capture sequence numbers overlap, which could leave it showing the
  capture still being processed. (#1409)
- `cameraagent install --plan` and `cameraagent upgrade --plan` name the release's image by the ID the install or
  upgrade then reports: under Docker's containerd image store, the release's image digest rather than its
  configuration digest. (#1384)
- `/etc/hvo-sky/install.json` is written without HTML-safe escaping, so the installer version reads `0.9.0+<commit>`
  rather than `0.9.0\u002B<commit>`. It is the same JSON, read the same way by earlier installers. (#1384)
- `hvo-sky` no longer warns of plain HTTP to another machine when the address is one bound to this machine, such as the
  CameraAgent host's own LAN address, and the request goes to it directly rather than through a proxy. (#1384)
- `hvo-sky status`, the Operations page and `hvo-sky ui` no longer mark capture `(stale)` five minutes after it was
  last paused or resumed: capture control is CameraAgent's live state, and is always current. (#1384)
- Current Sky keeps the overlays an operator turned on or off when a new capture arrives, and the browser remembers
  them across page loads for each CameraAgent. An archived capture's view keeps its own choices. Restore profile
  defaults clears the remembered choice of the view it is used in. (#1382)

### Security

- `hvo-sky` reaches a loopback address (`127.0.0.1`, `localhost`, `[::1]`) directly, never through an HTTP proxy.
  With `http_proxy` set and no `no_proxy` entry for it, the owner API key went to the proxy unencrypted, without the
  plain-HTTP warning, and the request reached the proxy's own loopback rather than this machine's CameraAgent. Any
  other address still uses the proxy as before. (#1404)

## [0.9.0] - 2026-10-19

### Added

- `hvo-sky`, the command line of a running CameraAgent, ships with each release as `hvo-sky-linux-arm64` and
  `hvo-sky-linux-x64`. It shows the status and versions (`status [--watch]`, `info`), reads and changes the operator
  settings from CameraAgent's settings catalogue (`config show|get|set|diff|apply`), stages and applies rig and schedule
  revisions, and restarts CameraAgent, with `--json`, the installer's exit codes, and one deadline per command
  (`--timeout`, default 30 s) over every request and wait. It signs in with an owner API key
  that `hvo-sky login` reads from the terminal and keeps in a `0600` file, or from `HVO_SKY_API_KEY`; it never takes a
  key on the command line. CameraAgent's operations API gains `GET info` and `POST restart`, the Camera & rig page's
  restart for API clients. (#1317)
- `hvo-sky ui` opens a full-screen terminal interface over the same API: status (F1), settings (F2), camera and rig
  (F3), schedule (F4) and system (F5), with the commands' changes and checks behind prompts. It fits 80 x 24 and uses
  120 x 32, says in its header whether what shows is current (`[LIVE]`, `[STALE]`, `[OFFLINE]`), and marks values it
  could not read again `LAST KNOWN`. It is drawn in HVO Dark with 16-colour, `NO_COLOR` and ASCII fallbacks. (#1317)
- `hvo-sky-install cameraagent backup` copies an instance, bulk captures and a verified snapshot of its databases
  included, into one owner-only archive in `<product-root>/backups`. The archive is proved whole and recorded before it
  is kept; it is written with capture paused and the container stopped, after a check of the folder and of free space,
  with progress reports and no fixed deadline. A failure or Ctrl+C discards the partial archive and restarts the
  instance. On a host whose Docker cannot enforce every CameraAgent limit, a backup that restarts the instance warns
  first and restarts it as a rollback does. `hvo-sky-install cameraagent restore <archive>` puts an uninstalled or
  absent instance back from a backup, after refusing a partial, unrecorded or damaged archive with nothing changed. It
  never lowers a counter the replaced instance handed out. It revokes again the API keys, passwords and authority
  revoked since the backup, keeps the live instance's owner settings and owner, or warns loudly when there is nothing to
  compare. Both run as the instance's service account, not started with `sudo`, and refuse to run as root or as anyone
  else; like `install`, they ask `sudo` only to create the backup folder or an absent instance root.
  Both support `--plan` and `--resume`, and a completed backup or restore removes the database snapshots nothing refers
  to any more. (#1353)
- On a terminal, `hvo-sky-install cameraagent install` (and `install.sh` with no arguments) opens a wizard for what
  the command line left unanswered: the camera's name, its release and catalog, network, location, container limits
  and the owner's sign-in, each page passed over when the command line answered it. It shows the plan, checked as
  `--plan` checks it, then saves the answers to `~/.config/hvo-sky/install-answers.json` and installs with
  `--answers`, exactly as that command would. Esc or Ctrl+C leaves it with nothing changed (exit 130). It never asks
  for a password, and it is drawn in HVO Dark with 16-colour, `NO_COLOR` and ASCII fallbacks. (#1317)
- Every release ships `THIRD-PARTY-NOTICES.md` as an asset of its own (kind `notices`): the notices of what the
  release bundles, among them Terminal.Gui and the packages it brings into the installer. (#1317)

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

- `cameraagent install` no longer asks for a missing `--friendly-name` or `--image-ref` on a line of its input. On a
  terminal the wizard asks; anywhere else nothing is asked, and a missing value is a usage error (exit 2) that names it,
  such as `--release or --image-ref is required.` (#1317)
- `cameraagent upgrade` refuses a host whose Docker cannot enforce CameraAgent's memory, CPU and PID limits, or has
  fewer CPUs than its CPU limit, as `cameraagent install` does: it lists every finding and the remedy
  (`cgroup_enable=memory` and a reboot on a Raspberry Pi) and exits 3 with nothing changed, for its `--plan` and
  `--resume` too. There is no override. A rollback, its `--resume` and `upgrade --restore-only` go ahead there, because
  they are the recovery path: each prints a loud warning naming every limit Docker cannot enforce, writes it to the
  operation's install log (`operations/lifecycle/<operation>/install.log`), and keeps every limit Docker can enforce.
  On a host without CPU quota support, where Docker refuses a container with a CPU limit, every rollback and recovery
  start withholds the CPU limits; the instance keeps them recorded, and the next start on a host that enforces them
  gives them again. (#1357)
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
- The `tar-missing` host prerequisite: `cameraagent install` no longer refuses a host without `tar`, because no
  `hvo-sky-install` command runs it. `install.sh` still checks for `tar`, which it uses to unpack the catalog. (#1353)

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

[Unreleased]: https://github.com/HualapaiValley/HVO.SkyMonitor/compare/v0.9.0...development/v1
[0.9.0]: https://github.com/HualapaiValley/HVO.SkyMonitor/releases/tag/v0.9.0
