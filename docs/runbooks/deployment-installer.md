# CameraAgent Deployment Installer

The `hvo-sky-install` self-contained Linux CLI installs one local, standalone
VirtualSky CameraAgent without a repository checkout or a target-host .NET
runtime. Docker Engine and the Compose plugin are the only application-runtime
prerequisites; the host must also pass the [host prerequisites](#host-prerequisites)
that install and `install --dry-run` evaluate. The CameraAgent image comes from a
published release's files ([Release Install](#release-install)) or from an image
the operator names, and the catalog from a verified local bundle.

`hvo-sky-install --version` prints the installer's version and the commit it was built from, as
`<version>+<40-character commit>`, and exits 0. Like `--help`, it needs no Docker and does not read the
[host policy](#production-product-root-on-another-filesystem), so it works even where that policy is refused. A build
given no version is the product version's development build, `X.Y.Z-dev` (`VersionPrefix` in `Directory.Build.props`,
[releases.md](releases.md#the-product-version)), and a build without a checkout or a supplied commit prints
`X.Y.Z-dev+unknown`.
[Build Evidence](#build-evidence) describes how a publish is stamped.

## HVO Install Contract

The shared [HVO product installation and release contract](../hvo-install-contract.md)
is the install experience this installer is being brought to under epic
[#1318](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1318). This
runbook describes the installer as it behaves today.

**Current differences:**

| Contract | Today | Issue |
| --- | --- | --- |
| Installer `hvo-sky-install`, runtime client `hvo-sky` with a terminal UI, and an installer wizard | The `hvo-sky-install` CLI, configured by flags or an [answers file](#answers-file); no runtime client or wizard | [#1317](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1317) |
| `install.sh` entry point with `--check`, `--from DIR`, and `--install-docker` and the like | [`install.sh`](#installsh) checks the host, then verifies the release's installer and small files and starts it, online or with `--from DIR`; it gives `cameraagent install` and `upgrade` the release and the catalog. With no arguments it runs `cameraagent install`. It installs no Docker and changes no boot setting; it names the remedy. The installer itself still needs a command | [#1311](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1311) |
| Command words `install`, `upgrade`, `rollback`, `uninstall`, `status` | Instance commands are grouped under `cameraagent` (`cameraagent install`, `cameraagent upgrade`, ...), because one host holds several instances and the catalogs beside them; `status` is top level | Recorded for contract draft 4 |
| Settings catalogue served by the API | CameraAgent serves the [operator settings catalogue and group writes](../contracts/cameraagent-operator-settings-v1.md) through its operations API; the owner creates API keys for non-browser clients | [#1316](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1316) |
| Install and upgrade refuse a host that cannot enforce the container limits; a rollback proceeds with a loud warning (contract section 7) | Install refuses such a host ([Host Prerequisites](#host-prerequisites)); upgrade and rollback do not check | [#1357](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1357) |
| Upgrade and rollback decide between a swap and a breaking change by comparing `stateContracts` (contract section 6, D1) | `release.json` and the image's `io.hvo.skymonitor.state-contracts` label declare `stateContracts`, but the installer still decides a state migration from the image's state boundary labels ([CameraAgent Lifecycle](#cameraagent-lifecycle)) | Epic [#1318](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1318); no issue yet |

## install.sh

Each release carries `install.sh`, the contract's one-line entry point (section 5). The release tool writes the
release's version into it when it builds the release's files; the repository's own copy names no release and refuses
to run. It checks the machine, gets that release's `hvo-sky-install` and the release's small files, verifies them,
and starts the installer with the remaining arguments:

```bash
# Check this machine for the release, changing nothing:
curl -fsSL https://github.com/HualapaiValley/HVO.SkyMonitor/releases/download/v<version>/install.sh | bash -s -- --check

# Install the release, asking on the terminal for what the installer needs (no arguments is cameraagent install):
curl -fsSL https://github.com/HualapaiValley/HVO.SkyMonitor/releases/download/v<version>/install.sh | bash

# Install the release with its settings given; CameraAgent's image is pulled from ghcr.io by its digest:
curl -fsSL https://github.com/HualapaiValley/HVO.SkyMonitor/releases/download/v<version>/install.sh \
  | bash -s -- cameraagent install --friendly-name "North All-Sky Camera" --bind-address 127.0.0.1 --port 5130 ...

# Install from a downloaded release folder, offline:
bash "$release/install.sh" --from "$release" cameraagent install --friendly-name "North All-Sky Camera" ...
```

An integration pre-release is never `latest`, so name its tag as above. Run it as the Docker-capable runtime user, not
as root: the installer refuses root and asks `sudo` itself for the steps that need it. Its own options come before the
installer's arguments, which start at the first argument that is not one of them, or after `--`:

| Option | Meaning |
| --- | --- |
| `--check` | Run the checks below, then stop. Nothing is downloaded or changed |
| `--from DIR` | Take everything from `DIR`, a folder holding the release's files, instead of GitHub and ghcr.io: the installer, `SHA256SUMS`, the catalog and the offline image archive. ghcr.io is never contacted. `DIR`, and every folder above it, must be one that only you and root can change; `DIR` itself must be writable by no one but its owner, even when it is sticky; and the release's files in it must be yours or root's, writable by no one else and not links: the installer reads `release.json`, `SHA256SUMS` and the archive there after `install.sh` has checked them. `DIR` is resolved to its physical path once, when the command line is read, and everything uses that path |
| `-h`, `--help` | Its usage. `--help` after the installer's arguments is the installer's |

**What the installer is given.** `install.sh` adds to the installer's arguments only what the installer is not
already given:

| Command | Online | With `--from DIR` |
| --- | --- | --- |
| `cameraagent install` | `--release <private folder> --image-source registry --catalog-bundle <catalog>` | `--release DIR --no-download --catalog-bundle <catalog>` |
| `cameraagent upgrade` | `--release <private folder> --image-source registry` | `--release DIR --no-download` |
| no arguments | `cameraagent install`, then as above | `cameraagent install`, then as above |
| any other command, or options with no command | nothing | nothing |

- Online, the private folder holds the release's `release.json` and `SHA256SUMS`. Docker pulls CameraAgent's image by
  the digest `release.json` names, after the installer's [pre-import checks](#release-install); the offline image
  archive is never downloaded. While the `skymonitor-cameraagent` package is private, sign in to `ghcr.io` first.
- The catalog is the release's `<catalog version>.bundle.tar.gz`, from `DIR` or downloaded, checked, and unpacked in
  `${XDG_CACHE_HOME:-~/.cache}/hvo-sky/catalogs/<catalog version>.bundle`. It is unpacked there afresh each time, not
  in the temporary folder, because the installer binds an install to the catalog folder it is given: a second run, or a
  `--resume`, must name the same folder. The archive must hold only files in that one folder.
- With no installer arguments, `install.sh` runs `cameraagent install`, as the install contract has it, with an
  install's checks, downloads and hand-over; the installer asks on the terminal for what it needs. `--check` alone
  still stops after the checks.
- `--release`, `--image-ref` or `--image-archive` given by you means no `--release` is added; `--catalog-bundle`
  means no catalog is added; `--image-source` is not added twice; `--config`, which names the whole request, gets
  nothing but `--from`'s `--no-download`. Online, `--image-source archive` is refused, because the archive is not
  downloaded: use `--from DIR`.

**Checks.** All of them run, and every failure is reported, before anything is downloaded or changed:

- Linux on 64-bit ARM or x86-64, with a 64-bit system and glibc, run by someone other than root;
- `curl`, `tar` and `sha256sum`;
- 300 MB free in `TMPDIR` for the download; for an install, 50 MB where the catalog is unpacked, a folder that only you
  and root can change;
- github.com answers, and this machine's clock is within five minutes of its `Date`. With `--from`, github.com not
  answering is only noted. An unsynchronised clock (`timedatectl`) is noted;
- with `--from`, the folder is one only you and root can change and holds, each yours or root's and writable by no one
  else, the installer for this machine, `SHA256SUMS`, `release.json` and, for an install or
  an upgrade, the offline image archive for this machine and, for an install, the one catalog archive `SHA256SUMS`
  lists. Otherwise, for an install or an upgrade, ghcr.io answers;
- for an install (`cameraagent install`, or no command) or an upgrade: Docker answers you and is 20.10 or later with
  Compose v2; `docker info` reports `MemoryLimit`, `CPUCfsQuota` and `PidsLimit` as `true` and at least 2 CPUs; and
  Docker's `DockerRootDir` has 3 GB free. Online, `docker buildx` is 0.8 or later, which the installer uses to read the
  image from the registry before it pulls it. On a Raspberry Pi whose Docker cannot limit memory it names the remedy
  in [Host Prerequisites](#host-prerequisites): append a space and `cgroup_enable=memory` to the single line in
  `/boot/firmware/cmdline.txt`, then reboot. Less memory than the 4 GiB limit is only noted, as the installer notes it.

Other commands, such as `status`, `cameraagent rollback` and `cameraagent uninstall`, check what they need
themselves, so a host that has lost Docker's limits can still be inspected, rolled back or cleaned up. The installer's
own [host prerequisites](#host-prerequisites) still run, with exact sizes.

**Verification.** The installer for this machine (`hvo-sky-install-linux-arm64` or `hvo-sky-install-linux-x64`),
`SHA256SUMS` and, as the table above needs them, `release.json` and the catalog archive are downloaded, or copied
from `--from`, into a new folder of mode `0700` under `TMPDIR`. `TMPDIR` must be a folder that only you and root can
change. Then:

1. each file's SHA-256 must be the one `SHA256SUMS` lists for it, once;
2. when `gh` is installed and signed in, the build-provenance attestations of `SHA256SUMS` and of the installer must
   name `.github/workflows/prerelease.yml` (for a `-dev.<n>` pre-release; `release.yml` otherwise) at the release's
   tag, built on a GitHub-hosted runner. Without `gh` this is noted and skipped. `SHA256SUMS` is what every other file,
   and the installer's own checks of `release.json` and the image, are checked against;
3. the installer's `--version` must succeed and print `<version>+<40-character commit>`.

The installer is never started before all of this, not even for its `--help` or `--version`, and never as root.

It then runs as you, with your umask, reading the terminal when there is one (never the download), and the folder is
removed afterwards. `install.sh` prints its own messages on standard error, so standard output is the installer's alone:
`--json` output can be redirected to a file. Only `--help` prints on standard output.

**Exit codes**, the install contract's: the installer's when it ran; otherwise `0` when `--check` passed, `1` when a
download failed (running it again continues), `2` for an invalid command line, `3` when a check or the verification
refused and nothing was changed, and `130` when it was interrupted.

`scripts/test:install-sh` tests it with fake commands, as `curl ... | bash` runs it; `scripts/test:release-scripts`
runs those tests ([ci-pipeline.md](ci-pipeline.md#release-script-tests)). For tests only,
`HVO_SKY_INSTALL_TEST_RELEASE_URL` names another address to download the release's files from, so a locally
built release can be served; nothing else should set it. It must be an absolute `https://` URL with a host (anything
else is a usage error), it is handed to curl as `--url`, never as an option, and still fetched over HTTPS alone, and
`install.sh` announces it on standard error whenever it is set.

## Command Surface

The command surface follows section 5 of the install contract (#1315). Instance
commands are grouped under `cameraagent`; every command takes `--json` and
`-h`/`--help`, and `--version` stands alone.

### Exit codes

Every command ends with one of these codes:

| Code | Meaning | Examples |
| --- | --- | --- |
| `0` | Done. With `--plan`, the plan can be carried out | An install, a re-run with nothing to change, `status`, `--help` |
| `1` | A step failed. Re-running the same command continues, with `--resume` where the message says so | Docker or CameraAgent did not respond, a verification failed, an install record that could not be written |
| `2` | The command line or the answers file is invalid, or a question had no one to answer it | An unknown option, an answers file with an unknown member, a missing `--friendly-name` without a terminal |
| `3` | Refused, and nothing was changed | Unmet [host prerequisites](#host-prerequisites), a host policy that cannot be trusted, running as root, a re-run that describes a different instance, an incompatible preflight, a [release](#release-install) the installer will not use or whose image is not the release's, an upgrade to the image already active or to an older release, a rollback with no previous image |
| `130` | Interrupted by Ctrl+C or SIGTERM | |

With `--json`, a failure or refusal writes one JSON object to standard error:
`{"schemaVersion": 1, "errorCode": "...", "message": "..."}`. A refusal's
`errorCode` is `refused`, except `host-prerequisites` and `state-incompatible`,
which keep their own codes. Without `--json` a refusal reads
`Installation refused: <reason> Nothing was changed.`

Every refusal under [Release Install](#release-install) exits `3`, including a registry the
installer cannot read before it pulls: each is decided before anything is imported, or after an import that is then
removed. When Docker does not remove that import, or its image listing cannot show that it did, the refusal becomes a
failed step instead and exits `1`, naming the images. A load or pull that Docker itself rejects is a failed step and
exits `1`. An upgrade loads an offline
archive after its journal entry, so a loaded image that is not the release's is refused there: the journal settles
it as refused, so it blocks no later operation, and it exits `1` (see below).

Every command makes each check that can refuse before its first write, and a refusal
there is never journaled:

- An install decides the host, the release, the answers against the installed
  instance, the catalog bundle and the persisted state before it prepares anything.
- A lifecycle command decides, before it takes a lock or writes its journal, the
  instance's condition, a rollback with no previous image, an older release, an
  unfinished operation that demands `--resume`, the purge gates, and for an upgrade
  whose candidate is known without importing it (the image Docker holds, or a
  release's image read from its archive or registry), the already-active and
  state-contract checks and the state preflight. A registry release image is pulled
  at that point, before any lock, and removed again if it is refused.

Once a command has written, it cannot say nothing changed: a check that stops it
after that point is a failure (exit `1`), journaled so that a re-run or `--resume`
continues. Examples are the state preflight a fresh install repeats once its
configuration is written, and an upgrade candidate that is only known once it is
pulled after the journal entry.

### Plan

`--plan` reports what the command would create, change, leave unchanged or remove,
and writes nothing of the product's: no file, folder, lock or Compose file. It renders
the Compose files in memory, admits the catalog bundle where it lies, and takes only
the locks that already exist. One read needs scratch space: the state preflight reads
a database it catches with an uncommitted transaction (a hot rollback journal)
through a private temporary copy in `TMPDIR`, because SQLite cannot read such a
database safely in place, and removes the copy when it is done. `--dry-run` is its older name and does the
same. Install, its re-run, `cameraagent upgrade`, `rollback`, `reinstall`, `uninstall`
and `purge` report a plan; `reset-state` and the catalog commands report their
existing dry-run output.

A plan makes the same checks the command makes before it changes anything: host
prerequisites, the host policy, the release, the answers against the installed
instance, an unfinished lifecycle operation that demands `--resume`, and whether the
instance's persisted state suits its image. A plan that would be refused is not
printed: the refusal is reported and the command exits `3`, so a printed plan can be
carried out. A fresh install's plan reports `composeModelSha256` as empty, because
Docker renders a Compose model only from files. A release image Docker does not hold
yet, such as the registry image `install.sh` hands an install or an upgrade, is
planned from the image read and checked from the release's archive or registry,
since a plan imports nothing.

```console
$ hvo-sky-install cameraagent install --answers camera.json --memory 6g --plan
...
Plan:
  unchanged  folder     /var/lib/hvo-sky/cameraagents/<uuid> (instance)
  change     file       /var/lib/hvo-sky/cameraagents/<uuid>/config/compose/compose.yml (Compose model, 2 CPUs, 6 GiB of memory and 256 PIDs)
  change     file       /var/lib/hvo-sky/cameraagents/<uuid>/instance-manifest.json (instance manifest, 2 CPUs, 6 GiB of memory and 256 PIDs)
  change     file       /var/lib/hvo-sky/cameraagents/<uuid>/state/deployment/installation-result.json (installation result, its Compose model)
  change     container  hvo-skymonitor-<uuid> (recreated with 2 CPUs, 6 GiB of memory and 256 PIDs)
  change     record     /etc/hvo-sky/install.json (instance <uuid>)
0 to create, 5 to change, 1 unchanged.
```

With `--json` the plan is the result's `plan` member: `items`, each with `action`
(`Create`, `Change`, `Unchanged` or `Remove`), `kind`, `target` and `detail`, and the
`summary`. A completed install also reports the plan of what it did, under
`Changes:`. Neither is retained in `installation-result.json`.

### Answers file

`cameraagent install --answers FILE` reads the install from strict JSON with a
schema version. The members are those `--config` reads, under the same names:

```json
{
  "schemaVersion": 1,
  "instanceId": "8f6a1d0e-6c1b-4e57-9a3e-2d4b5c6e7f80",
  "friendlyName": "North All-Sky Camera",
  "ownerEmail": "owner@example.org",
  "bindAddress": "127.0.0.1",
  "port": 5130,
  "catalogBundle": "/srv/hvo/hyg-v4.2-p3-s2-r1.bundle",
  "imageReference": "sha256:<loaded-image-id>",
  "latitudeDegrees": 35.2,
  "longitudeDegrees": -114.1,
  "elevationMeters": 800,
  "timeZoneId": "America/Phoenix",
  "cpus": 2,
  "memory": "4g"
}
```

- `schemaVersion` is required and must be `1`. A member the installer does not
  read, a member given twice, or a file that cannot be read is a usage error (exit
  `2`) naming the file.
- `instanceId` and `friendlyName` are required, so running the file again addresses
  the same instance. Every other member defaults as the command-line option does.
- The other members are `productRoot`, `release` and `imageSource` (`Archive` or
  `Registry`, as `--release` and `--image-source`; see [Release Install](#release-install)),
  `imageArchive`, `imageArchiveSha256`, `passwordFile`, `acknowledgePlaintextHttp`,
  `generatePassword`, `noDownload` and `replayProfile` (`InProcess` or `LocalRunner`).
  A release install names `release` and leaves out `imageReference`. The run flags
  `dryRun`, `resume` and `json` are not answers: give `--plan`, `--resume` and
  `--json` on the command line.
- No secret is ever an answer. A password is given in its own owner-only file
  (`passwordFile`) or generated (`generatePassword`).
- Only `--cpus`, `--memory`, `--release`, `--image-source`, `--catalog-bundle` and
  `--no-download` may accompany `--answers`. Each replaces the file's member: the
  limits, so a re-run with the same file changes the limits (see
  [Re-running an install](#re-running-an-install)), and the release and catalog, so
  [`install.sh`](#installsh) can hand the installer its verified release and catalog
  beside an answers file. A file that names `imageReference` or `imageArchive` cannot
  be given `--release`; leave the image to the release.

`--config FILE` is deprecated. It installs exactly as before and warns: on standard
error, or with `--json` as an `option-deprecated` entry in the result's `warnings`.
To convert a configuration file, add `"schemaVersion": 1` and remove `dryRun`,
`resume` and `json`.

### Re-running an install

Running a completed install again with the same answers prints every item
unchanged and `Nothing to change: the instance is installed as the answers
describe.` It validates the installed instance against its records and checks it is
healthy, and then stops: it writes nothing, not even a file with the same content,
and starts and restarts nothing.

A re-run puts back whatever no longer matches the records: Compose files that differ
from what the records describe are rewritten, a container that is not running healthy
on the recorded image is started again, a result behind its manifest is repaired,
and an install record behind the instance's records is settled. Each is listed as a
change, in the plan as in the run.

A re-run is held to the instance its records describe, not to the bytes of the first
request: the camera name, owner email, bind address, port, product root, location,
time zone, replay profile, image and catalog must match the installed instance. Any
difference is refused with exit `3`, names each differing option, and changes
nothing. Only the container limits change with a re-run (see
[Container Resources](#container-resources)). After an upgrade, a re-run names the
image the instance now runs; the image itself changes only with `cameraagent upgrade`.

An unfinished install is different: `--resume` continues it only with exactly the
inputs it began with.

### Install record

The installer keeps a machine install record at `/etc/hvo-sky/install.json`
(contract section 5), beside the host policy and checked as the policy is: the file
and every directory above it root-owned and not writable by group or other users,
no symbolic or hard link. It holds no secret: one entry per instance under the
production product root.

```json
{
  "schemaVersion": 1,
  "product": "HVO.SkyMonitor",
  "productRoot": "/var/lib/hvo-sky",
  "installerVersion": "0.9.0-dev+<commit>",
  "updatedUtc": "2026-10-18T03:00:00+00:00",
  "instances": [
    {
      "component": "CameraAgent",
      "instanceId": "8f6a1d0e-6c1b-4e57-9a3e-2d4b5c6e7f80",
      "friendlyName": "North All-Sky Camera",
      "condition": "Installed",
      "version": null,
      "previousVersion": null,
      "rolledBackFrom": null,
      "imageId": "sha256:...",
      "previousImageId": null,
      "url": "http://127.0.0.1:5130/",
      "resources": { "cpus": 2, "memoryBytes": 4294967296, "pidsLimit": 256, "cpusSetByOperator": false, "memorySetByOperator": false },
      "updatedUtc": "2026-10-18T03:00:00+00:00",
      "lastImageChange": null
    }
  ]
}
```

- The instance's release records (`image-distribution.json` and
  `image-distribution.records.json` in its deployment state, see
  [Release Install](#release-install)) own its installed version. The install
  record keeps no version of its own: whenever it settles an entry it reads
  `version` (the release the running image came from), `previousVersion` and
  `rolledBackFrom` from those records, by image ID. A version is `null` for an image
  given by `--image-ref`, which no release names.
- `lastImageChange` (`Upgrade`, `Rollback` or `null`) is the only fact the record
  adds: which way the image last changed. After an upgrade, `previousVersion` is the
  release before it; after a rollback, `rolledBackFrom` is the release the rollback
  left and `previousVersion` is `null`.
- The instance's own records stay authoritative. Each operation that completes on
  an instance (install, its re-run, upgrade, rollback, reinstall, uninstall) settles
  that instance's entry from them before it records its completion, and `purge`
  removes the entry. A write happens only when the entry changes.
- The installer runs as the runtime user, so it changes the root-owned record
  through one narrow `sudo` helper (`internal-record-instance`). The helper accepts
  only the product root and one entry, re-reads the host policy itself, writes only
  for the product root the policy resolves, and holds the host-policy lock. The
  record is read back as the runtime user before the change counts.
- A record that cannot be written, or one the installer did not write and cannot
  read, fails the operation at that point with exit `1`, after its other changes
  are complete; the record is never silently overwritten. Remove a damaged record,
  then re-run the install or rerun a lifecycle command with `--resume` to finish:
  the operation stays resumable until the record is settled. An upgrade settles the
  record after capture has resumed, so a record failure never holds capture paused.
- An admitted test product root (`HVO_INSTALLER_ALLOW_TEST_ROOT=1`) keeps its own
  record, owner-only, at `<product root>/operations/install.json`, so a test never
  writes the machine record.

## Inputs

Supply a downloaded release ([Release Install](#release-install)), or an immutable
CameraAgent repository digest or an already loaded image ID, plus the verified
production catalog bundle. An offline image archive named directly additionally
requires its independently trusted lowercase SHA-256. Never use a mutable image
tag or pass a password on the command line.

The compatibility form below consumes the internally verified local catalog
directory produced by the deterministic catalog tooling:

```bash
hvo-sky-install cameraagent install \
  --friendly-name "North All-Sky Camera" \
  --bind-address 127.0.0.1 \
  --port 5130 \
  --catalog-bundle /srv/hvo/hyg-v4.2-p3-s2-r1.bundle \
  --image-ref sha256:<loaded-image-id> \
  --latitude 35.2 \
  --longitude -114.1 \
  --elevation 800 \
  --time-zone America/Phoenix
```

`--catalog-bundle` is required by an install and by `catalog install`, and names
the bundle directory itself. A release carries its approved bundle as its own
versioned asset, `<catalog version>.bundle.tar.gz`: extract it and pass the
bundle directory. The installer does not take the catalog from a release folder.

`--no-download` performs no Docker registry pull, so the image must come from an
offline archive or already be on the host. With `--release --image-source
registry` that means the host already holds the release's image by its digest;
otherwise the install fails naming that digest reference.

Use `--image-archive /srv/hvo/cameraagent.tar` together with
`--image-archive-sha256 <sha256>` for an offline image load.

A new CameraAgent's first sign-in is the documented default, user name `admin`
and password `skymonitor`, and CameraAgent requires that password to be
replaced at first sign-in (see [Bootstrap And Recovery](#bootstrap-and-recovery)).
`--owner-email` is optional: it sets the owner's durable email, which is also
accepted as a sign-in name, and defaults to the non-routable
`owner@cameraagent.local`. Instead of the default password, use
`--password-file <owner-only-path>` for an operator-chosen temporary password of
8 to 256 characters without control characters, or `--generate-password` for a
random one written to the owner-only `config/owner-bootstrap/temporary-password`.
A password file is read as UTF-8 with only its trailing line ending removed, by
the installer and CameraAgent alike, so leading, trailing, and inner spaces are
part of the password.
`--json` selects the versioned structured result. Its `warnings` array lists each
[advisory host finding](#host-prerequisites) as a `code` and a `message`, and is
empty when there is none; the retained `state/deployment/installation-result.json`
does not carry it, nor the [`plan`](#plan). `--plan` (or its older name `--dry-run`) validates the catalog, Docker daemon, loaded image, generated
configuration, and rendered Compose model using private temporary storage without
creating the product root, loading/pulling an image, or starting a container, and
reports the [plan](#plan).

Missing required values are prompted only on an interactive terminal; without one,
a missing value is a usage error (exit `2`). The install can instead be read from an
[answers file](#answers-file) with `--answers FILE`. `--config FILE`, its deprecated
predecessor, still works and warns: it reads the same members without a schema
version and with the run flags, and cannot be mixed with value options. A
configuration that does not name `productRoot` installs into the production product
root, as the command-line form does; one that names a root keeps it and is validated
like `--product-root`.

To serve the camera on a LAN address, bind it there and acknowledge plaintext
HTTP:

```bash
hvo-sky-install cameraagent install ... \
  --bind-address <host-lan-address> --port 5130 --acknowledge-plaintext-http
```

After `compose up`, the installer waits for health and bootstraps the owner at
the instance's public URL, the same `url` the result reports and every lifecycle
probe uses: the bind address itself, or `localhost` for `0.0.0.0`/`::`. It never
probes `127.0.0.1` for an instance bound elsewhere, because that instance is not
listening there.

## Host Prerequisites

`cameraagent install` and `cameraagent install --dry-run` evaluate every host
prerequisite before the product root is prepared or anything else is created,
and report every finding together so the host can be fixed in one pass:

| Code | Requires |
| --- | --- |
| `docker-memory-limit-unsupported` | `docker info` reports `MemoryLimit: true`, so the Compose `mem_limit` is enforced |
| `docker-cpu-quota-unsupported` | `docker info` reports `CpuCfsQuota: true`, so the Compose `cpus` quota is enforced |
| `docker-pids-limit-unsupported` | `docker info` reports `PidsLimit: true`, so the Compose `pids_limit` is enforced |
| `docker-host-cpus-insufficient` | `docker info` reports at least as many CPUs (`NCPU`) as the CameraAgent [CPU limit](#container-resources); Docker refuses to create a container with a larger quota |
| `tar-missing` | an executable `tar` on `PATH`. No lifecycle command runs it: [`backup`](#backup-and-restore) writes its archive itself, and upgrade and rollback archive nothing |
| `product-root-storage-insufficient` | room on the filesystem that holds the product root: twice the catalog bundle and image archive together, plus 1 GiB |
| `docker-root-storage-insufficient` | room on the filesystem that holds Docker's `DockerRootDir`: twice the image archive and 1 GiB |
| `docker-root-unreported` | `docker info` reports an absolute `DockerRootDir` |

One finding is advisory and never refuses: `docker-host-memory-below-limit`
reports that Docker's host has less memory (`MemTotal`) than the CameraAgent
memory limit. The limit is still applied, but it cannot stop CameraAgent from
exhausting that host's memory. The installation proceeds either way. The human
form renders the finding on standard error with the rest of the report. With
`--json` install, standard error is reserved for an error object, so the result on
standard output reports it in `warnings` instead, for the install and the dry run
alike.

A capability `docker info` does not report counts as unsupported. Free space is
measured on the filesystem that actually stores each root, at its nearest
existing ancestor, not on `/`; when the product root and Docker's data root
share one filesystem their requirements are added together. An upgrade likewise
measures the staged archive and room for two copies of the small databases (the
snapshot and a restore's temporary copy) against the product root's own
filesystem. Bulk capture data is never copied, so it is never counted.

A host that cannot enforce the limits would otherwise run CameraAgent unbounded,
so it is refused. On a Raspberry Pi the firmware adds `cgroup_disable=memory` to
the kernel command line, so it is not in `/boot/firmware/cmdline.txt` and there
is nothing there to change. The remedy is to append a space and
`cgroup_enable=memory` to the end of the single line in that file, reboot, and
confirm that `docker info` reports `MemoryLimit`, `CpuCfsQuota`, and `PidsLimit`
as `true`. Each limit finding quotes the cgroup version,
`/sys/fs/cgroup/cgroup.controllers` when it is readable, and Docker's own limit
warnings.

The human form renders every finding and its remedy on standard error. With
`--json` the structured report is written to standard output and standard error
carries one error object with `errorCode` `host-prerequisites`; the exit code is
`3` (refused, nothing changed) either way. The CLI uses SQLite in-process and never runs `sqlite3` or `jq`,
so neither is required. Uninstall, purge, and the other lifecycle commands do not
evaluate these prerequisites, so a host that loses limit support can still be
cleaned up.

**A private place to unpack the installer.** `hvo-sky-install` is one
self-contained file. Each time it starts, the .NET host extracts the native SQLite
library bundled into it under `$DOTNET_BUNDLE_EXTRACT_BASE_DIR`, or by default
`~/.net` in the home directory of the user running it, and reuses what an earlier
run extracted there without checking it again. Whoever can write that directory
decides what the installer loads. Run the installer as a user whose home directory
is writable by that user and nobody else, or set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to
a directory only that user can write (mode `0700`). The `sudo`-run preparation
step extracts under root's own home.

## Container Resources

The installer limits the CameraAgent container to **2 CPUs, 4 GiB of memory and
256 PIDs** unless the operator sets other limits, as section 7 of the shared HVO
install contract requires. Raise them when installing:

```bash
hvo-sky-install cameraagent install ... --cpus 3 --memory 6g
```

- `--cpus` is a number from 2 to 1024 with at most two decimal places, such as
  `2.5`.
- `--memory` is whole mebibytes or gibibytes, such as `6144m` or `6g`, from `4g`
  to `1024g`.
- In an [answers file](#answers-file) (or a deprecated `--config` file) they are the
  `cpus` number and the `memory` string, for example `"cpus": 3, "memory": "6g"`.
  Leaving a member out, or setting it to `null`, leaves that limit to the default on
  a first install, and keeps the recorded limit on a re-run.
- A limit below 2 CPUs or 4 GiB is **refused** as a usage error with exit code
  `2`. No acknowledgement admits one: the contract makes these limits a floor,
  and [#1044](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1044)
  records the out-of-memory cold start the former 1 GiB limit produced.
- The PIDs limit stays at 256 and is not set by the operator. It bounds runaway
  process and thread creation rather than capacity, and the .NET runtime adds
  only about one thread for each CPU it is given.
- The optional [local replay runner](#local-replay-runner) keeps its own 0.5 CPU,
  2 GiB and 64 PID limits. They bound one sandboxed replay at a time and are not
  changed by these options.

Install and `install --plan` (`--dry-run`) check the limits against the host Docker runs on
before anything is created (see [Host Prerequisites](#host-prerequisites)). A
CPU limit above the CPUs Docker reports is refused, because Docker would refuse
to create the container. A memory limit above the host's memory is the advisory
`docker-host-memory-below-limit`, and the installation proceeds: a 4 GB
Raspberry Pi reports slightly less than 4 GiB, and refusing it would turn away a
host that runs CameraAgent.

The limits are recorded as `resources` in the instance's
`instance-manifest.json`:

```json
"resources": {
  "cpus": 2,
  "memoryBytes": 4294967296,
  "pidsLimit": 256,
  "cpusSetByOperator": false,
  "memorySetByOperator": false
}
```

A `...SetByOperator` flag is `true` when the operator gave that limit, even if
the value equals the default. A limit the operator did not give follows the
installer default:

- **Upgrade** keeps every limit the operator set and moves every other limit to
  the upgrading installer's default.
- **Rollback** runs the previous image with the Compose model retained from
  before the upgrade, authenticated against its recorded identity, and gives it
  the limits the instance records. A rollback never returns an instance to limits
  it has left.
- **Reinstall** keeps the recorded limits.
- **A re-run** of a completed install changes them (contract section 7). A limit
  given on the re-run replaces the recorded one and counts as set by the operator; a
  limit left out keeps what the instance records. The same limit written another way,
  such as `6g` and `6144m`, changes nothing. Giving the default explicitly changes no
  limit, only records it as the operator's, so the next upgrade keeps it.
- A failed upgrade candidate restores the exact prior Compose file and manifest,
  limits included.
- The [host check](#host-prerequisites) runs against the limits a re-run moves to,
  before anything changes: a CPU limit above the CPUs Docker reports is refused
  with exit `3`, and the 2 CPU and 4 GiB floor still refuses a lower limit as a
  usage error.

To change the limits of an installed instance, re-run its install with the new
limits, from the answers file it was installed with or the same options:

```bash
hvo-sky-install cameraagent install --answers camera.json --cpus 3 --memory 6g --plan
hvo-sky-install cameraagent install --answers camera.json --cpus 3 --memory 6g
```

The re-run first validates the instance at the limits it records and checks it is
healthy. It then rewrites the Compose file with the new limits, Compose recreates
the container, and the instance must come back healthy and verified. Only then are
the instance manifest, the installation result and the
[install record](#install-record) rewritten, in that order. The manifest is the
change's commit point:

- Until the manifest is written, anything that stops the change, an interruption
  included, rewrites the Compose file with the recorded limits, recreates the
  container with them and puts the recorded manifest back before the failure is
  reported (exit `1`, or `130` when interrupted). The restore has its own four-minute
  bound.
- Once the manifest is written, the container and the instance's records agree on
  the new limits. An interruption while the result or the install record is written
  leaves them behind, and the next re-run settles them.
- A power loss before the manifest is written, which leaves the container on the new
  limits and the records on the old ones, is settled by the next re-run too: the
  records decide, so a re-run that gives no limits puts the container back, and one
  that gives the new limits finishes the change.

The installation stays completed throughout.

An instance installed before limits were recorded has no `resources` member. It
runs the original 1 CPU, 1 GiB and 256 PID limits. No operator could set them,
because the installer had no option for them, so none counts as set by the
operator. Its next `cameraagent upgrade` moves it to 2 CPUs and 4 GiB and records
`resources` with both flags `false`; until then a re-run of its install keeps the
original limits exactly, unless the re-run gives a limit: the original 1 CPU and
1 GiB are below the floor, so the limit not given moves to the default. A runtime `docker update` override, such as a memory
override approved during recovery, is not an installer setting: it is not
recorded, and the container an upgrade creates carries the recorded limits.

An upgrade refuses, before it touches anything, a Compose file whose CameraAgent
limit lines differ from the limits its manifest records. Lifecycle commands do not
run the host check, so on a host with fewer than 2 CPUs the upgrade that moves an
instance to the defaults fails when Docker refuses the candidate, and the
automatic restore returns it to its prior image and limits.

Confirm what Docker applied with:

```bash
docker inspect --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.PidsLimit}}' \
  hvo-skymonitor-<instance-uuid-without-dashes>
```

The defaults report `2000000000 4294967296 256`.

## Release Install

> `linux/arm64` installation is qualified natively: the `open(2)` flag defect
> ([#603](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/603)) is fixed,
> and the advisory arm64 workflow runs this CLI's Unit suite. Every integration
> pre-release is installed from its arm64 offline archive and upgraded by digest
> from GHCR on the dedicated arm64 runner before it is published
> ([prereleases.md](prereleases.md)).

`--release <folder>` installs or upgrades from a release's files downloaded into one folder (install contract
section 4). The folder needs `release.json`, `SHA256SUMS` and, unless the image is pulled, the offline archive for the
platform being installed. `--image-ref` and `--image-archive` instead name an image the operator has already
established. `--release` cannot be combined with them, so an installation never both follows a release and accepts an
operator-supplied image.

```bash
hvo-sky-install cameraagent install \
  --release /srv/hvo/releases/1.2.0-dev.3 \
  --catalog-bundle /srv/hvo/releases/1.2.0-dev.3/catalog/hyg-v4.2-p3-s2-r1.bundle \
  --friendly-name "North All-Sky Camera" \
  --owner-email admin@home.lan \
  --latitude 35.2 --longitude -114.1 --elevation 800 --time-zone America/Phoenix
```

`--image-source` chooses how the image reaches Docker:

| `--image-source` | The image |
| --- | --- |
| `archive` (the default) | Loaded from the platform's offline archive in the folder. Nothing is pulled, so an air-gapped host needs only the folder |
| `registry` | Pulled as `<repository>@<digest>`, the digest `release.json` names, never by tag, once its metadata has been read from the registry and checked (below). The host needs `docker buildx` 0.8 or later, and must be signed in to `ghcr.io` while the package is private |

Before it contacts Docker, the installer checks the folder. It refuses, naming the reason and changing nothing, when:

- `SHA256SUMS` is not a list of `<sha256>  <name>` lines in printable ASCII ending in a newline, or lists a file twice;
- `SHA256SUMS` does not list `release.json`, or `release.json` does not match its SHA-256 there;
- `release.json` does not follow schema 1: its version (which carries no build metadata), tag, prerelease flag,
  commit, time, repository, image, offline archives, assets and upgrade notes are each checked;
- `release.json` and `SHA256SUMS` list different files, or give a file different SHA-256s;
- the release does not publish the platform: this host's for an install, the one recorded for the instance's Docker
  daemon for an upgrade;
- with `archive`, the release has no offline archive for the platform, the archive is not in the folder, or its size
  or SHA-256 is not the one the release gives;
- with `archive`, the archive does not hold the release's image, which is read from the archive itself before anything
  is loaded. Its OCI layout must name the release's digest, and only `<repository>:<version>` when it names the image.
  The release's index must list the platform once, as the manifest `release.json` names, and that manifest must name the
  configuration `release.json` names. The configuration must be for the platform and labelled CameraAgent at the
  release's version, commit and created time (`org.opencontainers.image.created`, exactly `release.json`'s `created`).
  Docker's own `manifest.json`, which its classic store loads instead, must describe the
  same image and tag only `<repository>:<version>`, so a load cannot retag another image. Every layer the manifest
  names must be in the archive, the size the manifest gives it and the blob its digest names, so Docker cannot reject
  the load for a missing or damaged layer. The image must also declare the contracts the instance needs, as below.
  Every blob is checked against its digest and the size its descriptor gives;
- with `archive`, an entry of the archive could be read differently by a loader. Docker and containerd clean each entry
  name, and a later entry replaces an earlier one, so every name must be the one canonical spelling of its path: no
  `.` or `..` segment, no empty segment (`//`), no leading `/`, no backslash, and a trailing `/` only on a directory.
  It must also be part of the OCI image layout (`oci-layout` at version 1.0.0, `index.json`, `manifest.json`, the
  `blobs/sha256` directories and the blobs the image uses), appear once, and be a regular file, or a directory where
  the layout has one. A link, a duplicate, an alias such as `metadata/../index.json`, or a blob the image does not use
  is refused;
- with `archive`, a JSON field a loader could read differently. Docker and containerd decode the layout, the index,
  the manifest, `manifest.json` and the configuration with Go, which matches a field's name regardless of case,
  Unicode folds included (`ſ` reads as `s`, the Kelvin sign as `k`), and lets a later spelling replace an earlier one.
  So `oci-layout`, `index.json` and its image entry, the image index and its entries, the platform manifest and its
  descriptors, and each `manifest.json` item may hold only the fields the release tool writes, spelled exactly:
  `manifest.json` holds `Config`, `RepoTags` and `Layers`, and nothing else. In every object of the archive's JSON,
  and of `release.json`, a property name must be ASCII and must not differ from another of the same object only in
  case. A second `repotags` or `RepoTagſ` beside `RepoTags` is refused before anything is loaded;
- `release.json`, or any JSON in the archive, names the same property twice in one object, compared after escapes are
  decoded, so two readers could see different values.

Each file is opened as a regular file with a single link, so a symbolic link, hard link, FIFO or folder under a
release file's name is refused rather than followed. Copy the files into the folder; do not link them.

With `registry`, the image is read from the registry before anything is pulled. The installer runs `docker buildx
imagetools inspect` (buildx 0.8 or later, which added the `--format` template and its `.Image` field). buildx uses
this user's Docker credentials and credential helpers, so the installer never handles them. It reads, by the digest `release.json` names:

- the release's image index (or, for a one-platform release, its manifest) exactly as the registry serves it, which must
  be the content that digest names;
- the index's one entry for the platform, and that manifest exactly as served, which must be the content its digest
  names and, when `release.json` lists an offline archive for the platform, the manifest it names;
- the configuration that manifest names, which buildx fetches by its digest: it must be the configuration
  `release.json` names, when it names one, be for the platform, be labelled CameraAgent at the release's version,
  commit and created time, and declare the contracts the instance needs.

Anything else is refused with nothing pulled, naming what disagrees. When the registry cannot be read this way (no
`docker buildx`, a buildx older than 0.8, no network, or no credentials for a private package), the installer refuses
the registry source before pulling anything and names the default archive source as the alternative.

A new installation then imports the release's image, by loading its archive or pulling it, before it claims anything:
no product root, state, application identity, catalog, staged archive or journal entry exists yet. A load or pull that
Docker rejects, or that is cancelled, therefore leaves nothing for `--resume` or purge to recover, and running the same
install again completes it. A resumed or repeated installation, which already has state, imports in its image step
from the same verified inputs. An upgrade pulls a registry image before its journal entry, once every check that
only reads has passed, the state preflight included, so no refusal follows the pull; a refusal under the instance lock
that still changes nothing removes the pulled image again. It loads an archive after its journal entry, and a load
Docker still rejects there is settled as a terminal refusal that blocks no later operation.

A refusal says that nothing changed, so after removing an image it imported the installer lists every image Docker
holds (`docker image ls --all --digests --no-trunc`). A name is proven gone only when that listing succeeds, reads
completely and does not show it. If the listing still shows any name the import added, or fails, or holds a line the
installer cannot read, the command fails with exit `1` instead of refusing, names the images, and gives the
`docker image rm` command that removes them. Names Docker held before the import are never removed.

Before it claims anything, the installer also requires the release's image, from the archive or the pull, to declare
the configuration and catalog contracts this instance needs (an upgrade also requires the current state contract).
After Docker resolves the image, the installer again requires it to be one the release names: the image index, the
platform's manifest or its configuration digest, as `release.json` or the registry gave them. It must also be labelled
CameraAgent at the release's version, commit and created time. Otherwise the installer refuses, naming the release, and removes what
that step loaded or pulled, unless Docker held it before. It removes it too when the load or pull fails or the
operation is cancelled while it runs.

The installer checks that a release's files agree with each other; it does not check who built them. Verify that with
`sha256sum --check SHA256SUMS` and `gh attestation verify` before installing, as
[prereleases.md](prereleases.md#installing-a-pre-release-on-skymonitor01) shows.

An archive install retains the verified archive under
`<instance-root>/state/deployment/image-archive.tar`, so a resumed or repeated
operation does not read the folder again. It is the size of the image and is
included in instance backups; size the instance filesystem accordingly.

The installed release is retained beside the other deployment evidence:

```text
<instance-root>/state/deployment/image-distribution.json
```

It is schema version 3. It records the release's version, tag and commit; the SHA-256s of the `release.json` and
`SHA256SUMS` it was verified with; the image's digest reference and platform; the source, `archive` or `registry`; the
archive's name and SHA-256 for an archive install; the image ID Docker gave the image; and when it was recorded. A
schema 1 or 2 record, written for a signed release by an earlier installer, is read as no record.

The record follows the image the instance actually runs. It is written only once
the image has been prepared and accepted, and a refused install or upgrade leaves
none behind. An upgrade to an image no release names withdraws it. The record of
the image the instance ran before is kept beside it, in
`image-distribution.records.json`, which holds both records and is written
atomically. A rollback restores the record of the image it returns to, including
when the rollback is resumed, so an upgrade after it is still refused when it would
go back. An upgrade reads the version to show the right upgrade notes and to refuse
an older target (see [Upgrade notes and older targets](#upgrade-notes-and-older-targets)).

An upgrade accepts the same options. It selects the platform from the architecture recorded for the instance's Docker
daemon at installation, not from the machine running the CLI, so a remote or cross-architecture daemon is served the
image it can run:

```bash
hvo-sky-install cameraagent upgrade \
  --instance-id <uuid> \
  --release /srv/hvo/releases/1.2.0-dev.4 \
  --image-source registry
```

`scripts/test:release-install --release <folder>` proves a release folder against the real Docker daemon of the host
it runs on. It installs a throwaway instance from the offline archive and moves it to a stand-in image. It then
upgrades it from the release and checks that the upgrade showed the release's notes and recorded the release. Finally
it checks that a second upgrade to the same release is refused, and removes everything it added. The pre-release
workflow runs it on the arm64 runner; [prereleases.md](prereleases.md#building-a-release-locally) shows how to build a
release folder to run it against.

The generated CameraAgent rig and pipeline are immutable instance configuration.
The installer-virtualsky-v3 profile changes only the generated **new-install**
20-second ASI174 combined-preview display stretch (white percentile 0.9997,
asinh strength 8 instead of 0.9999 and 4); raw and combined Mono16 samples are
unchanged. Upgrading the image on an existing v2 instance does **not** replace
its rig or pipeline and therefore does not apply the new stretch to its captures.
The scene-layer producer version changes independently: new processing with the
new image draws star names at 14 source pixels on a 1936x1216 frame while
retaining the same cardinal/corner text geometry, even on an existing instance;
retained layers and previews are immutable and do not change retroactively.
To adopt both installer defaults, provision a new isolated instance through a
clean install and migrate operational state only under the supported lifecycle;
do not edit an installed profile in place or treat an image upgrade as a clean
install. These are display choices, not a physical visibility model.

The installer-virtualsky-v4 profile changes only the generated **new-install**
optics to `horizontalFlip: true`. For an upward-looking all-sky camera that is
the physical orientation: with North up, East appears on the **left** of the
frame, as seen when looking up at the sky. The unflipped setting (`false`) is
the mirrored, map-style view with East on the right. Nothing else in the
generated rig, pipeline, or schedule changes. Upgrading the image on an existing
v3 (or earlier) instance does **not** replace its rig, so an installed instance
keeps the flip it was generated with. To change it on an installed instance,
use **Operations > Camera & rig > Edit active rig** in the CameraAgent UI: that
records new optics and rig revisions, stages them, and applies them when
CameraAgent next restarts. It does not rewrite the installed rig in place, and
previously captured frames and layers are not changed. In the installed
container, whose `restart: unless-stopped` policy starts CameraAgent again, the
pending-rig banner offers **Restart now** to operators with Operations change
rights. On a host with no supervisor, restart CameraAgent manually;
`CameraAgent:Restart:Supervised` (`true` or `false`) overrides the container
detection.

`cameraagent preflight` evaluates a planned upgrade's image against persisted state first without loading,
pulling, or starting anything; it takes the image as `--image-ref`, see
[State Compatibility Boundary](#state-compatibility-boundary).

`--migration-backward-compatible` is deprecated. A candidate that declares a
state migration has its small databases snapshotted before it starts instead; see
[Swap upgrade and database snapshots](#swap-upgrade-and-database-snapshots). The flag
is still accepted and changes nothing, and the command warns about it: on standard
error before it runs, or with `--json` as an `option-deprecated` entry in the result's
`warnings`. It is not removed yet because it is part of an upgrade's journaled request
identity, so an interrupted upgrade begun with it still resumes with it (#1315).

### Edge work persisted across an upgrade

Node `PlanSha256` includes two explicit code version inputs as well as the step
configuration: `ICaptureProcessingStep.ImplementationVersion` (initially `"1"`)
and the resolved shared step version. The latter uses `SharedStepVersion` when
provided, otherwise the built-in recipe's `ImplementationVersion`, otherwise
`cameraagent-window-v2:<alias>` or `cameraagent-step-v2:<alias>`. Step authors must
bump the step's `ImplementationVersion` for incompatible execution behaviour
changes, including dependency handling and side effects; shared recipe/algorithm
changes must bump their shared version. Neither version comes from operator
options or the assembly/package version. Unchanged configuration and versions
produce the same hash.

Introducing these version inputs changes every earlier node hash once, even
when the pipeline configuration is identical. Saved revisions containing
processing nodes compiled before this upgrade become superseded; a graph with
no enabled steps has no implementation plan to invalidate. There is no
processing SQLite schema change (schema version 8). This one-time transition intentionally uses the
standard-work abandon path described below. Raw evidence is retained.

Captures accepted by the previous version keep their raw evidence and their
frozen processing plan. When the new version builds a different node plan for
the same pipeline, standard processing of that earlier work cannot run:

- Each such capture's standard work is **abandoned** with reason
  `plan-superseded`, and its execution is expired with
  `processing.plan-superseded`. The raw stays held until its upload completes,
  and a scene-required upload completes with the raw alone. Ingress keeps
  accepting captures. Abandoned work is not a terminal failure and does not
  quarantine the lane.
- Live work that exceeds its maximum queue age is abandoned with
  `processing-expired` instead of quarantined. On startup, standard work that an
  earlier version quarantined for `processing-expired` is converted to
  abandoned, so it stops blocking ingress.
- Genuinely corrupt or missing raw evidence still quarantines and refuses new
  captures until an authorized operator releases each affected capture.
- An archived replay accepted by the previous version, whose frozen plan this
  version no longer builds, is **abandoned** on its first claim. Its execution
  and work become `Expired` with `processing.plan-superseded`. Its unfinished
  nodes record `TerminalFailure` with the same reason; nodes it had already
  completed keep their status. It spends no further attempt, releases its
  pinned inputs, and leaves its raw evidence untouched. It is counted in
  `ReplayAbandonedCount` on the `capture-processing` health check (gauge
  `camera_agent.processing.replay.abandoned`) and in `abandonedCount` of the
  `archivedReplay` section of the operations summary
  (`/api/v1/operations/summary`), not in `ReplayTerminalCount` or
  `terminalCount`, so it does not degrade health. The replay page explains
  this. To run that capture again, submit a new replay against a revision this
  version compiles.
  A replay submitted against the old revision is refused, by the same plan
  comparison that abandons the accepted one.
- Abandonment needs intact evidence. Before abandoning, the replay checks its
  own raw capture and every historical raw input frozen for a window step. If
  any is missing or damaged, the replay is not abandoned. It retries as
  `processing.replay-exception`, because no step may run under the old plan to
  find the damage, then fails once its attempts are spent and counts in
  `ReplayTerminalCount`. That is the same outcome as damage under a current
  plan, where the window step finds it and records `processing.step-exception`.
- A replay whose frozen configuration this version cannot build at all, for
  example because a step was removed, is not abandoned. It retries as
  `processing.replay-exception` and fails terminally once its attempts are
  spent. That failure counts in `ReplayTerminalCount` and leaves health
  **Degraded** with `replay-terminal`.

A replay whose host stopped without releasing its lease, for example after a
crash or a kill, is recovered once the lease expires. The execution returns to
`Pending` with `processing.replay-lease-expired`, and every node attempt the
dead lease left `Running` is recorded as `Interrupted` with that reason. The
lost attempt still counts toward `ReplayMaximumAttempts`, so a replay that
keeps killing its host cannot loop forever. Within its remaining attempts and
deadline it retries on its own. When they are spent it fails terminally, as
any replay does.

Open **Quarantine → Raw evidence quarantine and releases**
(`/operations/quarantine?kind=RawEvidence`). Review the capture identity and
failure, then choose one of these audited actions:

When the journal is valid but raw evidence is missing or corrupt, the host keeps
the authenticated operator surface available while capture admission and lane
processing wait for audited recovery. Configuration, schedule, named-rig and
calibration metadata still initialize against the validated journal. Pending
calibration-acquisition recovery waits for evidence release and preserves its
job if the host stops meanwhile. A persisted capture pause remains paused after
release; resume it explicitly when appropriate. Unsupported schemas and other
integrity failures still fail startup.

- **Revalidate:** recover the exact original payload and sidecar at their
  committed paths from trusted recovery evidence. Preserve the quarantined
  copies. The action verifies the complete manifest, capture identity, payload
  length and SHA-256, synchronizes the recovered files, and durably records
  actor, `evidence-restored`, and UTC time before releasing the failure. Merely
  putting files back or restarting no longer releases an existing failure.
  Evidence-failed standard work remains terminal and its lane is abandoned with
  `raw-evidence-revalidated`; use explicitly requested archived replay for that
  recovered capture. Other evidence-failed lanes return to their queue with their
  original age and expiry rules. Unrelated quarantines remain unchanged.
- **Write off:** explicitly acknowledge irrecoverable evidence or approved loss.
  This records a terminal disposition with actor, controlled reason and UTC
  time, abandons that capture's pending lanes, and preserves its original
  identity and all remaining raw files. Written-off evidence stays protected
  from retention and cannot be reaccepted or automatically recovered on restart.
  This action does not reclaim disk space. A capture with a leased lane must
  settle that lease before either action can apply.

Both actions keep the released capture's raw evidence protected from ordinary
retention, including after lane holds are recomputed. Revalidation also does not
reclaim disk space.

The owner-authenticated operations API exposes the same bounded history through
`GET /api/v1/operations/raw-evidence?pageSize=25`. Use its action-specific token
in `POST /api/v1/operations/raw-evidence/revalidate` or
`POST /api/v1/operations/raw-evidence/write-off`, with JSON
`{ "actionToken": "<token>", "reasonCode": "<controlled reason>" }`, an
`Idempotency-Key` header, and the ordinary antiforgery token. Write-off reasons
are `irrecoverable-evidence` and `operator-approved-loss`. A duplicate request
returns the original audit receipt; reusing its key for another command
conflicts. Paths are not returned by this API. Releases persist in the raw
journal's reserved `raw-evidence-release.*` capture events, and write-off also
records `raw-evidence-written-off` as its terminal disposition.

The next admission attempt reinitializes ingress; other unresolved evidence
failures continue to refuse captures. No release deletes or rewrites raw data.

Audited recovery requires raw-journal version 15. A canonical v13 or v14 journal
upgrades transactionally through the declared `canonical-v13-v14-to-v15`
transition. The image must declare schema 15 and that
migration. Older readers cannot preserve these dispositions, so installer
preflight refuses rollback to an image declaring an earlier raw-journal version;
the earlier runtime also refuses the v15 journal. Select a compatible image
instead of deleting or resetting evidence to force a rollback.

A Named active revision compiled by the earlier version cannot run any live work.
In that case:

- `capture-processing` health is **Degraded**, with reason
  `active-revision-superseded` and the revision in `SupersededActiveRevisionId`.
- CameraAgent logs one Error (event 2097) naming the node and both plan hashes.
- Captures are still accepted and uploaded, but their standard processing is
  abandoned.

To recover, use the **Named graphs** page (`/operations/pipeline/graphs`)
to do one of the following:

- Create, validate and activate a revision of the same pipeline, which this
  version compiles.
- Roll back to configured-basic, which is recompiled from configuration at every
  startup.

Health clears within one refresh, and an Information entry (event 2098) records
it. No restart is needed. Configured-basic is never reported as superseded.

New activation, rollback selection and archived replay commands reject a saved
revision whose node plans no longer match the installed compiler, including a
changed node set or a pipeline the compiler can no longer build. The API returns
HTTP 409 and the operator UI directs you to create or select a compatible
revision. Refreshing the page alone does not repair an obsolete revision.
Rejection leaves the registry, revision, replay queue and raw evidence unchanged.
An exact retry of an already acknowledged command still returns its recorded
result; it does not schedule new work or reapply the selection. Replay checks use
the retained capture configuration, rather than the current camera settings.

For a saved revision that was Validated before this upgrade, selecting it for
activation, rollback or a new archived replay now returns the compatibility
rejection above. On the Named graphs page, create a new revision from the same
pipeline definition, validate it with the installed version, then activate it
(or choose another revision compiled by this version). Re-validating an already
Validated revision does not replace its immutable saved plans. Configured-basic
is an immediate alternative while preparing the replacement revision.

Already-accepted archived replay retains its frozen hashes, so after this
upgrade it takes the archived replay abandonment path above. On its first
claim it becomes `Expired` with `processing.plan-superseded`. It spends no
further attempt and does not retry, and it counts in `ReplayAbandonedCount`
rather than `ReplayTerminalCount`. A replay whose evidence is damaged fails
instead, as described above.

Activating a compatible revision cannot repair that job's immutable frozen plan.
To run the capture again, submit a fresh replay against a compatible revision.
You do not need to cancel the old job first, because it abandons itself on its
first claim. Until then it holds one slot of `ReplayMaximumPendingCount`.
**Request cancellation** on its execution page frees that slot sooner. Raw
evidence is never deleted to bypass the mismatch.

The version inputs do not change recipe output keys or manifest bytes by
themselves. They prevent an earlier frozen plan from running even when its
recipe identity is unchanged. Later implementation version bumps use the same
supersession and recovery paths.

Rollback continues to use the retained previous image identity and never
consults a release train.

### Archived replay across a lifecycle transition

Lifecycle state (`/api/internal/deployment/lifecycle/state`) reports archived
replay in its own `archivedReplay` section, separate from live
`captureProcessing`. The section contains pending, leased, retry, terminal and
abandoned counts, pending bytes and the oldest pending time. The operations
summary carries the same section. Each recorded continuity boundary keeps the
replay counts as `ReplayPending` and `ReplayLeased`. A
lifecycle CLI reading an image that predates the section records zero.

The pause-and-drain boundary deliberately does not wait for archived replay:

- A capture pause stops new exposures, but accepted work keeps processing, and
  that includes operator-requested replay. The replay queue can hold
  `ReplayMaximumPendingCount` executions (1000 by default), each with a deadline
  of `ReplayDeadlineSeconds` (one hour by default). Waiting for that queue could
  hold any upgrade, rollback or catalog transition well past the two-minute
  drain deadline.
- When the host stops, in-flight replay is returned to its queue as deferred
  work. Its node attempt is recorded as `Interrupted` with reason
  `processing.replay-host-stopping`, and no replay attempt is consumed. A
  replay limited to one attempt therefore still has that attempt after the
  restart. Replay that loses its lease while the host keeps running is still a
  retryable failure.
- Catalog selection and rollback refuse uncommitted live processing, but not
  pending archived replay. Archived replay has no dependency on the mounted
  catalog. An annotation node in replay draws from the projected scene that
  live processing committed and that submission pinned, or from scene
  provenance recorded with the frame. It never builds a scene from whichever
  catalog is mounted when it runs. A retained node that draws constellation
  lines with neither a projected-scene dependency nor frame scene provenance
  therefore fails closed in replay, with reason
  `processing.missing-projected-scene`. A catalog switch cannot change what
  an accepted replay produces, so replay pending at the boundary does not
  refuse the transition.

If a transition times out while it waits for the drain, the error message
reports any replay pending or leased and states that the drain does not wait
for it.

That guarantee belongs to the running image, so the image declares it.
Lifecycle state carries `archivedReplayCatalogIndependent: true` on every image
that fails replay closed this way. Before catalog selection or rollback changes
anything, the CLI reads lifecycle state and refuses the transition unless the
running image declares it. The check assumes the running container uses the
image the deployment retains, because every remount the transition performs,
including failure recovery, recreates the retained image. An image that
predates the guard omits the declaration and could still annotate accepted
replay from the newly mounted catalog. The refusal happens before pause,
before the catalog setting changes and before any container is recreated for
the new catalog. Upgrade the CameraAgent image, then retry.
The check reads the declaration, not the replay counts, because those counts
are a periodic snapshot and an empty count does not prove that no replay was
accepted under the current catalog.

When `--resume` continues an interrupted catalog selection, the CLI first
restores the original catalog and then applies the same check before it
selects again. A refusal at that point leaves the original catalog mounted.

Three paths stay outside the check:

- **An image change after a catalog change.** Image upgrade and image rollback
  keep the current catalog, and the CLI cannot read a declaration from an
  image that is not running yet. Rolling back or downgrading to an image that
  predates the declaration after a catalog transition can therefore run replay
  accepted under the earlier catalog against the current one. Before such an
  image change, let pending archived replay finish or cancel it.
- **A selection interrupted by an older CLI on an older image.** Resuming it
  with this CLI restores the original catalog, restarting the older image on
  it, and only then refuses. Replay that image accepted under the interrupted
  selection can run against the restored catalog in between. An older CLI
  resuming its own operation performs no check.
- **A running image that differs from the retained one.** The CLI reads the
  declaration from the running container but does not compare that
  container's image with the image the deployment retains. If a guarded image
  is running while the deployment still retains an image that predates the
  declaration, the check passes and the transition then recreates the older
  image on the new catalog. Before a catalog transition, make sure the running
  CameraAgent image is the one the deployment retains.

## Local Replay Runner

Archived replay remains in the CameraAgent process by default. Select the
optional long-lived local process boundary only during installation:

```bash
hvo-sky-install cameraagent install \
  --replay-profile local-runner \
  <other-required-inputs>
```

`--replay-profile in-process` is the explicit form of the default. The selected
profile is part of the retained installation request and rendered Compose model;
changing it requires a new converged installation rather than an ad hoc Compose
edit. The LocalRunner topology uses the distinct `cameraagent-compose-v3`
contract; legacy and current in-process installations remain on
`cameraagent-compose-v2`. The split-host development Compose scripts remain
in-process only.

The local-runner profile starts the self-contained replay executable from the
same immutable CameraAgent image. CameraAgent still owns SQLite replay leases,
frozen input selection, graph orchestration, output publication, and stale-lease
fencing. Only one built-in recipe invocation and its declared immutable payloads
cross the owner-only Unix socket at a time. Live processing never uses the
runner.

The installer generates a 256-bit authentication key under `config/secrets`,
mounts only that secret and the shared socket directory into the runner, disables
its network, drops all capabilities, enables `no-new-privileges`, and does not
mount CameraAgent raw, archive, catalog, identity, provisioning, or SQLite state.
The protocol additionally authenticates each replay job against its execution,
graph, node, durable-attempt, lease, and deadline metadata and verifies input and
output checksums.

Runner absence, saturation, authentication/capability mismatch, heartbeat loss,
or disconnect never falls back to in-process execution after the local-runner
profile has been selected. The replay returns to pending with reason
`processing.replay-runner-unavailable` without consuming a replay attempt.
Malformed output is a retryable execution failure and consumes the normal
bounded attempt budget, but output checksums, semantic role, ordered recipe lineage,
recipe identity, and lease fencing prevent it from being committed. Already
committed nodes remain fenced and reusable. Confirm that the
`replay-runner` Compose service is running, that both services share
`/run/hvo-replay`, and that the owner-only authentication-key file exists. A
healthy executable reports protocol, recipe, architecture, and warmup evidence
without opening the service socket:

```bash
docker compose exec replay-runner \
  /app/replay-runner/HVO.SkyMonitor.CameraAgent.ReplayRunner --capabilities
```

The container health check authenticates the running socket with the same
owner-only key. Run the same probe manually with:

```bash
docker compose exec replay-runner \
  /app/replay-runner/HVO.SkyMonitor.CameraAgent.ReplayRunner --probe
```

Loopback TCP exists for explicitly secured non-Compose environments, but Unix
sockets are the deployment default. TCP rejects non-loopback endpoints and both
transports require a 32-to-4096-byte authorization key when the external profile
is active.

LocalRunner performance evidence is intentionally split by concern. The manual
`LocalReplayRunnerPerformanceTests.W1W2AndW6SizedPreviewInProcessAndLocalRunnerEvidence`
campaign isolates process-boundary overhead, durable backlog/drain, runner outage,
and live-preemption behavior using a single Preview recipe at W1, W2, and W6 frame
dimensions. It is supplemental evidence, not the canonical W6 workload. Run
`scripts/test:cameraagent-standalone-211` separately for the full 14-node
`cameraagent.standalone-w6.json` graph, production catalog, calibration and
environment prerequisites, rendered outputs, isolation, recovery, and canonical
W6 performance disposition defined in
[`performance-validation.md`](../planning/performance-validation.md).

## Persistent State

Production installation uses the production product root and the canonical
layout in [product-instance-layout.md](product-instance-layout.md). The
production product root is `/var/lib/hvo-sky`, unless the root-owned host
policy described below names another one. Every command defaults to it, so an
instance is found under a relocated root with no extra option. The
`--product-root` override is rejected for any other root unless the invocation
was admitted for a test root. Admission is decided exactly once, when the command
line is parsed: the process environment is read there and nowhere else, and only
`HVO_INSTALLER_ALLOW_TEST_ROOT=1` admits. The decision then travels on the
request itself, so a later change to the environment cannot revoke an admission
already granted or grant one already refused, and an installer configuration
file cannot reach the decision at all because the flag is never deserialized.
Tests that need a temporary root set that admission on the request they build
rather than mutating the environment of the process they share. The host policy
location is carried the same way: a test states its own policy location on the
request it parses, and production always reads `/etc/hvo-sky/host.json`.

### Production product root on another filesystem

A host that keeps application data on another filesystem, for example NVMe
mounted under `/srv`, names the production product root in the root-owned host
policy instead of bind-mounting it over `/var/lib/hvo-sky`:

```json
{"productRoot": "/srv/data/hvo-skymonitor", "mountPoint": "/srv"}
```

Write it with the convenience command, as the deployment user, **before the
first instance is installed**:

```bash
sudo install -d -o root -g root -m 0755 /srv/data   # the parent must already exist
hvo-sky-install host set-product-root /srv/data/hvo-skymonitor
```

The command escalates through one narrow `sudo`-executed internal command,
`internal-set-product-root --product-root <path>`, which accepts nothing else and
always writes `/etc/hvo-sky/host.json`. It re-reads and re-validates the
existing policy itself, refuses to move a production root whose `cameraagents` or
`logichosts` directory holds any entry, creates only the final product-root
directory (`root:root 0755`) beneath an existing validated parent, and publishes
the file atomically (`root:root 0644`). It records `mountPoint`, the mount point
of the filesystem the root resolves through at that moment (from
`/proc/self/mountinfo`; `/srv` on a host whose NVMe is mounted there). The command
then reads the policy back as the deployment user and requires it to name
exactly the requested root and mount. Restating the current root reports
`unchanged`, or `updated` when it adds a missing `mountPoint` to a hand-written
policy. It cannot replace a recorded `mountPoint` that no longer validates: the
existing policy is validated first and the command refuses, so repair it with
the procedure under [Repairing the host policy](#repairing-the-host-policy).
Relocating a root that already holds instances is not supported: their manifests
and Compose files name the old root.

The empty-root check and the publication run under a root-only lock,
`/run/hvo-sky-host-policy.lock` (`root:root 0600`, so no other user can
open or hold it). The privileged preparation holds the same lock while it re-reads
the policy and creates an instance root, so an instance can never appear under
the old root between the check and the moment the policy stops naming it: the
preparation either completes first, and the move is then refused because the root
holds an instance, or runs after the move and is refused because the policy no
longer names the root it was asked to prepare.

Without the file, behaviour is unchanged. When the file exists, every command
reads it once while parsing its command line and fails closed unless all of these
hold:

- the file and every directory from `/` to it are `root:root` and not writable by
  group or other users; the file is a regular file with one link, at most 4 KiB,
  holding a JSON object with exactly one `productRoot` string and at most one
  `mountPoint` string; a duplicate or unknown member is refused;
- `productRoot` is an absolute normalized path other than `/`, with no `.`/`..`,
  repeated or trailing `/`, and every segment uses only ASCII letters, digits,
  `.`, `_` and `-` without a leading `-`. The generated Compose environment file
  and bind mounts carry such a path literally; `$`, `#` after whitespace, `:`,
  quotes, and whitespace would be interpolated, cut off, or split by Compose;
- the product root already exists, and it and every component from `/` are
  `root:root` directories that are not symbolic links and not writable by group or
  other users;
- nothing is mounted beneath the product root, so the whole root lives on one
  mount. A mount exactly at the root is accepted;
- when the policy records `mountPoint`, which must be the root or one of its
  ancestors, the recorded path is currently a mount point in
  `/proc/self/mountinfo`, and the product root resolves through the mount
  currently visible there (its `statx` mount ID equals that mount's ID).

That is a check of the mount path, not of the filesystem behind it. The policy
records a pathname, not a device or filesystem UUID. When nothing at all is
mounted at `mountPoint`, every command stops and names the mount point, even if a
directory of the same name remains on the parent filesystem (which is what is
left behind when the filesystem was mounted exactly at the root). It does not
tell one filesystem from another at the same path:

- with stacked mounts, unmounting the top mount at `/srv` exposes the mount
  beneath it, which is still a mount at `/srv` and is accepted when the root
  resolves through it;
- a different filesystem mounted at `/srv` in place of the intended one (another
  partition, a replacement disk, or a tmpfs) is accepted the same way.

Verify the intended backing filesystem yourself after boot or any storage change,
for example `findmnt -no SOURCE,UUID /srv` (or `findmnt -T
/srv/data/hvo-skymonitor`), and compare it with the device or UUID in
`/etc/fstab`. Without `mountPoint`, which only a hand-written policy can lack, the
check is weaker still: an unmounted filesystem stops commands only when the root
directory itself disappears with it. Run `host set-product-root` with the same
root to record the mount point. The narrow `sudo` helpers for preparation and purge
also read and validate the policy themselves and act only on the root it resolves,
never on a root their arguments merely name. The clock-synchronization gate
protects every installation into the production root, wherever the policy puts
it; only an admitted test root is exempt. Compose files stay inside each instance
under `config/compose`, so an instance's configuration and state remain one
backup unit.

#### Repairing the host policy

Every command refuses a policy that does not validate, including
`host set-product-root`, which validates the existing policy before it writes a
new one. A recorded `mountPoint` that is no longer a mount, or a hand edit that
broke the file, therefore needs a privileged repair, which no `hvo-sky-install`
command performs.

A manual edit of `/etc/hvo-sky/host.json` does **not** take the
host-policy lock (`/run/hvo-sky-host-policy.lock`) that
`host set-product-root` and the privileged preparation hold. A command started
while the file is being changed can therefore resolve a different production
root, for example create a new instance under `/var/lib/hvo-sky` while
the policy is briefly absent, and that instance would then block restoring the
real root. Repair only inside a maintenance window:

1. **Open the window.** Stop or hold every automation that runs
   `hvo-sky-install`, and run no `hvo-sky-install` command by hand: no
   `cameraagent install`, `upgrade`, `rollback`, `reinstall`, `uninstall`,
   `purge`, `reset-state`, `recover-owner`, `preflight`, no `catalog` command, no
   `host` command, and no `status`, until step 5 is done. Confirm that nothing is
   running: `pgrep -af hvo-sky-install` must print nothing. Running CameraAgent
   containers are unaffected and keep running.
2. **Repair in place, preferring storage over the file.** If the intended
   filesystem can be mounted, mount it at the recorded `mountPoint`, confirm it
   with `findmnt -no SOURCE,UUID <mountPoint>`, and change nothing in the file.
3. **Otherwise edit only the mount field.** Run
   `sudo editor /etc/hvo-sky/host.json` and either correct `mountPoint`
   or delete just the `mountPoint` member so the setter records it again. Keep
   `productRoot` exactly as it was: it is the root the instances live under.
   Keep the file `root:root 0644`.
4. **Never delete the file while instances exist under its `productRoot`.**
   Without the file every command falls back to `/var/lib/hvo-sky`, so the
   instances under the policy root become unreachable: `status` and every
   lifecycle command look only in the root in force. Remove `host.json` only when
   that root is empty, after every instance under it has been uninstalled and
   purged.
5. **Re-establish and verify, then close the window.** Run
   `hvo-sky-install host set-product-root <the same productRoot>`, which validates
   the repaired policy and records the mount point that currently holds the
   root, then `hvo-sky-install status`, and confirm that every expected instance ID
   is listed. Only then resume automation and other `hvo-sky-install` commands.

The installer records owner-only files beneath the UUID instance root:

```text
instance-manifest.json
application-identity.json
config/camera-module.json
config/compose/{compose.yml,instance.env}
config/secrets/*
config/owner-bootstrap/temporary-password
state/deployment/{installation-state.json,installation-result.json,state-preflight.json}
state/identity/appsettings.local.json
operations/cameraagent-<uuid>.owner-recovery.json
operations/state-reset-<operation-uuid>.evidence.json
operations/cameraagent-<uuid>.state-reset-<operation-uuid>.appsettings.local.json   (only while a reset carries the file)
operations/owner-recovery/<operation-uuid>/temporary-password
```

The manifest binds installation, instance, application, runtime UID/GID, exact
catalog, image, Docker daemon, Compose template/model, configuration, rig,
schedule, deployment-location snapshot, and installation-verification token
hash identities. Password and verification-token content is never written to
the manifest, result, phase state, Compose environment, logs, or terminal output.
The one exception is the public, documented default owner password: when it
seeded the owner, the result records `ownerPasswordIsDefault: true` and the human
output names it.
A release install also retains the release record described in
[Release Install](#release-install).

The installer creates the product root through one narrow `sudo`-executed
internal preparation command when needed. That command prepares only the
production product root the host policy resolves, under the host-policy lock:
under the default root it may also create `/var/lib/hvo-sky` itself, and under a
policy root it validates the existing root; under either it creates the
`cameraagents`, `logichosts`, and `catalogs` namespaces as `root:root 0755`.
`catalog install` and `catalog gc` use
the same command's product form (`--product-root`, `--uid`, `--gid`, no instance)
when the runtime user is denied creating the runtime-owned `operations` directory
or an approved catalog root beneath the root-owned namespace; neither command ever
changes the shared root's ownership or mode, under the default root or a policy
root. The installer then performs catalog, configuration,
Docker, Compose, and HTTP work as the invoking Docker-capable runtime user. Do
not invoke the whole installer through `sudo`. Purge uses one other narrow
`sudo`-executed internal command, described with purge below, for the two
entries it must change in the root-owned `cameraagents` directory.

### Operator settings file

`state/identity/appsettings.local.json`, mounted in the container as `/app/App_Data/appsettings.local.json`, is
the one persisted place for settings an operator changes on the camera. CameraAgent loads it over the image's
`appsettings.json` and `appsettings.{Environment}.json`, so it survives image upgrades. The Observatory &
location page writes the site profile (observatory name, camera name, owner name and contact) to
`CameraAgent:Site` and the scene object limit to `CameraAgent:SkyMap:MaximumObjects`; any other setting can be
added by hand. The page names the file it writes.

The scene object limit (default 200, 1 to 5,000) is how many of the brightest catalog stars both the page's sky
map and every VirtualSky capture use. A capture keeps the brightest stars whose image crosses the frame during
the exposure, up to the limit, and always adds visible constellation figure stars and the planets, Sun and Moon
on top. Lower values make night images faster; raising it, even above the camera module's `maximumResults`,
deepens the star field at a higher cost per capture. A camera module that sets
`"stellarCandidateSelection": "Exhaustive"` ignores the limit and keeps its complete `maximumResults` query;
qualification profiles use that to retain full catalog depth.

- Environment variables, the command line, and the installer's key-per-file settings in `config/secrets` take
  precedence over the file. The page says when one of them sets a value it edits, because saving that value in
  the file would have no effect.
- The installer seeds the file with the friendly name as `CameraAgent:DisplayName` when the file does not
  exist, and never rewrites an existing one, so an install rerun keeps every name and setting changed since.
  The first time CameraAgent reads the site profile it moves that name to `CameraAgent:Site:CameraName`, in the
  same write that records the profile, unless an earlier release already recorded a profile for this camera, in
  which case that profile is kept and the seeded name is dropped.
- CameraAgent reads and writes a file of up to 1 MiB, the same bound the deployment tooling applies when it
  carries the file across a state reset.
- Each save from the page names the file content it read, so a save never overwrites a hand edit made after
  the page was loaded; the page asks for a refresh instead. The file is watched, so the site profile and object
  limit apply at once, the limit from the next sky map projection and the next capture of a camera module that
  follows it (an exhaustive module never does); most other settings take effect at the next CameraAgent start.
- A hand-edited file may contain comments and trailing commas, as the configuration loader allows. A save from
  the page keeps every setting but rewrites the file without its comments.
- A malformed file stops CameraAgent at startup. A malformed edit made while CameraAgent runs leaves the file's
  settings unset until it is corrected, and the page reports the file as unreadable and refuses to save over it.

Earlier installers also wrote the camera name to `config/secrets/CameraAgent__DisplayName`, a read-only setting
outside the operator's control. The installer now deletes it. An instance upgraded without an install rerun
still has it: its name is copied into the settings file the first time CameraAgent reads the site profile, after
which the file's name is shown and the secret is unused. Delete
`config/secrets/CameraAgent__DisplayName` by hand so that no read-only copy of the name remains.

## Bootstrap And Recovery

The first startup mounts the temporary password file only long enough for the
application to seed and authenticate the configured owner. The installer then
removes `LocalIdentity__AdminPasswordFile` authority, recreates the exact
Compose service, and requires the durable `owner-password-change-required`
state. The operator completes password replacement through CameraAgent.

Unless `--password-file` or `--generate-password` was given, the seeded owner
is user name `admin` with password `skymonitor`, and the installer prints:

```text
Sign in as admin / skymonitor; a password change is required at first sign-in.
```

Otherwise it prints the owner-only temporary password file instead. The
installer authenticates its own bootstrap check with the owner email, which
every supported CameraAgent image resolves; the browser accepts either the user
name or the email. The default password requires a CameraAgent image that
includes issue #1278; install an older image with `--generate-password` or
`--password-file`.

> **Takeover window.** Until the first sign-in replaces the default, anyone who
> can reach the CameraAgent port can sign in as `admin` / `skymonitor` and claim
> the camera by choosing the new password. Keep the default loopback
> `--bind-address 127.0.0.1` until the password is replaced. A non-loopback
> address still requires `--acknowledge-plaintext-http`; when you use one, sign
> in and replace the default immediately after the install completes, or install
> with `--password-file` or `--generate-password` instead.

The replacement, and every later password, needs at least 8 characters and has
no character-class rules; it cannot be `skymonitor` in any letter case, the
user name, or the current password. Five failed sign-ins lock the account for
five minutes, and the sign-in page says so. Owner recovery below also clears a
lockout. An installation upgraded from an installer that predates issue #1278
keeps its owner's email as the user name; sign in with that email.

If the owner loses the durable password after initial setup, recover access from
the CameraAgent host as the Docker-capable deployment runtime user, never as
root:

```bash
hvo-sky-install cameraagent recover-owner --instance-id <uuid> --generate-password
```

Alternatively, provide an absolute owner-only mode `0400` or `0600` file with
`--password-file`, holding 8 to 256 printable ASCII characters without spaces;
CameraAgent refuses the documented default as a recovery password. Password
input is never accepted on the command line. The
command validates the installed manifest/result, runtime UID/GID, product and
instance locks, and the retained lifecycle-control credential. It contacts the
installed CameraAgent directly through `<state-root>/identity/owner.sock`, which
the existing Identity state mount exposes inside the container as
`/app/App_Data/owner.sock`; redirects and system proxies are disabled. The CLI
requires the socket directory to be mode `0700` and the socket to be a
single-link mode `0600` Unix socket owned by the exact installation runtime
UID/GID. The recovery endpoints return `404` on every TCP listener, including
loopback, bridge, published, and reverse-proxy requests. The command never sends
the lifecycle credential or temporary password to a public listener and never
edits Identity SQLite directly.

Recovery socket activation requires Linux, explicit
`LocalIdentity:AllowMissingAdminPassword=true`, removed runtime password
authority, and a configured lifecycle-control credential. Generated Compose
already mounts the Identity state directory, so an existing installation gains
the socket when upgraded to a supporting CameraAgent image without a Compose
enable flag or host-port restriction. Lifecycle stop, uninstall, and rollback
remove only a socket whose type, ownership, mode, and link count still match the
installed runtime identity. After an ungraceful stop, CameraAgent startup locks
and authenticates the owner-only parent directory, refuses a concurrent startup,
active listener, or unexpected node, and rechecks an inactive runtime-owned
socket immediately before removing it and binding again. The owner-only parent
remains the access boundary if termination occurs in the brief interval before
the new socket is restricted to mode `0600`.

Before disclosing either recovery secret, the CLI sends a random nonce and
requires the Unix-socket listener to return an operation-bound HMAC-SHA256 proof
using the retained lifecycle credential. It then obtains a five-minute Data
Protection challenge bound to the operation, configured owner, and current
security stamp. The challenge, password, and resulting bootstrap state use
bounded `application/octet-stream` messages rather than JSON. Completion
requires exactly one configured site owner, removed bootstrap password
authority, and an unchanged challenge. It resets that owner only, restores the
mandatory temporary-password gate, rotates the security stamp to revoke
existing cookies and interactive authorization, and records the operation
idempotently in existing Identity tables. Capture and durable storage continue
throughout the Identity-only transaction. An older or disabled CameraAgent
fails closed as unsupported.

If acknowledgement is interrupted or the outcome is ambiguous, rerun the exact
command with `--resume`; the same operation ID and staged password are reused.
A definitive pre-completion rejection closes that attempt and requires a fresh
command without `--resume`. Human output prints only the operation ID, resulting
state, and owner-only password-file path; `--json` omits the path. Sign in with
that temporary password, replace it immediately, then securely remove the
recovery password file. Never retain its content in shell history, logs,
tickets, or evidence.

The lifecycle-control credential is site-owner-equivalent recovery authority.
Keep its retained file and mirror owner-only, preserve its manifest hash
binding, and rotate the whole installed instance through an approved lifecycle
when compromise is suspected. Device verification material and
`device-secrets.dat` are not owner-recovery proof.

Each mutation publishes a durable phase. A failed run retains redacted state and
diagnostics. Resume only an explicitly known instance:

```bash
hvo-sky-install cameraagent install --resume --instance-id <uuid> <same-inputs>
```

Immutable input drift fails closed before production configuration or container
mutation. A completed rerun returns the retained result when healthy; if the
container is stopped, it revalidates the catalog, image, configuration, owner,
deployment location, and Compose identities and converges the same instance back
to healthy without regenerating identity or credentials.

## State Compatibility Boundary

The CameraAgent image declares the durable state contract it reads and writes rather than a generic
compatibility promise, through these labels:

```text
io.hvo.skymonitor.state-compatibility
io.hvo.skymonitor.minimum-compatible-revision
io.hvo.skymonitor.identity-migration
io.hvo.skymonitor.raw-ingress-schema
io.hvo.skymonitor.raw-ingress-migration
io.hvo.skymonitor.catalog-manifest-version
```

The `LABEL` instruction in `src/HVO.SkyMonitor.CameraAgent/Dockerfile` is the source of their current values;
this runbook does not restate them. Read the values an image actually carries with
`docker image inspect --format '{{json .Config.Labels}}' <image>`.

**CameraAgent state produced before `70ecdd3` is an incompatible source for a direct in-place upgrade.** Those
revisions wrote catalog manifest version 1, Identity migration `20251125021552_CreateLocalIdentity`, and
raw-ingress schema 11. The current image requires the catalog manifest version, Identity migration, and
raw-ingress schema its labels declare, and migrates an earlier canonical raw journal only through the transition
its `io.hvo.skymonitor.raw-ingress-migration` label names, as described in
[Edge work persisted across an upgrade](#edge-work-persisted-across-an-upgrade). There is no supported automatic
migration from schema 12 or earlier: a populated schema-12 journal is rejected by preflight before drain, snapshot,
stop, or Compose mutation, and runtime startup also refuses it without changing its contents.
Upgrading such an instance requires the CameraAgent-only reset below or an equivalent explicit
state-disposition procedure. The superseded `backward-compatible` label value remains readable only so an
already installed image stays inspectable; it is never accepted as an upgrade candidate declaration.

Deployment preflight compares the declared boundaries against the persisted state before Compose starts the
container, so an incompatible instance fails once with the complete boundary list instead of through container
restart loops. Install, upgrade, and rollback all run it before any snapshot, drain, stop, or `docker compose`
invocation and before the container starts. The candidate image is inspected first because its
labels are the expected values being compared, and on install the catalog bundle and Compose files
are written first because the preflight reads the selected catalog and the bind sources they
establish. Install and upgrade retain the report at `state/deployment/state-preflight.json`. Run
it on demand without starting, loading, pulling, or mutating anything:

```bash
hvo-sky-install cameraagent preflight --instance-id <uuid> --json
hvo-sky-install cameraagent preflight --instance-id <uuid> \
  --image-ref <repository@sha256:digest>
```

With `--json`, an install or lifecycle invocation reports on standard error only in one-line JSON objects: any events,
such as an upgrade's notes (see [Upgrade notes and older targets](#upgrade-notes-and-older-targets)), then at most one
error object, last. The rendered report is therefore written there only in human form. Read the complete report from
`state/deployment/state-preflight.json`, or from `cameraagent preflight --json` on standard output; a
`--dry-run` deliberately retains nothing, and its error message still enumerates every blocking code.

Without a candidate the installed image's declaration is evaluated; naming one evaluates that candidate's
declaration and requires the current contract, matching an in-place upgrade. `--image-ref` names an image this
host already holds and is read from the local Docker image store. The command takes no `--release`: to evaluate a
release's image first, load its offline archive or pull it by the digest `release.json` names, and pass that digest
reference as `--image-ref`. An upgrade from `--release` runs the same preflight against the release's image before it
changes anything, and the report it retains names the release's tag beside the image ID.

The on-demand preflight is strictly read-only. A persisted database whose
journal carries unreplayed recovery state without a wal-index is read through a private temporary copy, so
replay never touches the instance. A database a writer currently holds is instead read in place, which
registers a reader in the wal-index that already exists, exactly as the instance's own readers do: no file is
created and no durable state changes. That is preferred over copying a database under a live writer, since such
a copy can tear and report a busy instance as unreadable. Unlike install and upgrade the on-demand command retains
no `state-preflight.json`.

**A compatible preflight is still not a promise that the upgrade will proceed.** It compares persisted state
against this instance. Gates that need the image itself run only during the upgrade: a release's image must be one
the release names and carry its version, commit and created time (see [Release Install](#release-install)), the candidate's
contract identities must match this instance, and whether the candidate declares a state migration decides
whether its small databases are snapshotted (see [CameraAgent Lifecycle](#cameraagent-lifecycle)).

The command exits `0` when compatible and `3` (refused, nothing changed) with error code `state-incompatible`
otherwise. Each finding
names its boundary code, path, observed
value, expected value, and remediation. The checked boundaries are the selected catalog manifest version and
catalog identity, the Identity migration lineage recorded in `__EFMigrationsHistory`, the raw-ingress
`PRAGMA user_version`, and the ownership and mode of every writable Compose bind source.

An in-place upgrade requires a candidate that declares `cameraagent-state-v2`. Installing an image, and
rolling back to one, are not state migrations and therefore also accept the superseded declaration. Any
boundary label a candidate omits, whichever contract it declares, is skipped rather than assumed met, and the
report records an advisory `candidate-boundaries-undeclared` finding naming exactly which labels were
omitted; an image predating the correction declares none of them. The catalog manifest version is still
compared against the version the runtime resolver enforces even when the image declares none. Rolling a state
boundary backwards is not supported: if an instance has run a newer state contract, restore it through the
reset procedure below rather than by rolling back to a pre-`70ecdd3` image.

The installer and every lifecycle mutation pre-create all writable bind sources
(`state/identity`, `state/data-protection`, `state/provisioning`, `state/raw`, `state/archive`, and
`state/replay-runner`) with the configured runtime UID/GID and mode `0700` before Compose runs. Docker would
otherwise create a missing nested bind source as `root:root` mode `0755`, and the capability-dropped container
cannot restrict it. A bind source owned by another identity fails closed with the exact path and both
identities rather than being silently adopted.

## CameraAgent State Reset

Reset deletes CameraAgent-owned local runtime state only. It never touches deployment configuration, secrets,
credentials, installation identity, shared catalogs, another instance, or a shared service. It requires an
explicit confirmation that repeats the instance UUID, and it refuses to run until the instance has completed a
preserve-by-default uninstall and its container no longer exists.

Destructive paths, all beneath `<instance-root>/state`:

```text
state/identity          # local Identity SQLite database and owner recovery socket
state/data-protection   # Data Protection key ring
state/provisioning      # device identity and device secrets
state/raw               # raw-ingress journal, frames, index, quarantine, outboxes
state/archive           # archived artifacts
state/replay-runner     # local replay runner socket directory
```

Preserved: `instance-manifest.json`, `application-identity.json`, the whole `config/` tree including
`config/secrets`, `config/owner-bootstrap`, `config/lifecycle-control`, `config/installation-verification`,
and `config/compose`, plus `<product-root>/catalogs`, `<product-root>/operations`, and instance backups.

The operator settings file `state/identity/appsettings.local.json` shares the Identity mount but is operator
configuration, not runtime state, so the reset carries it across the deletion. It is read before anything is
deleted, kept as `operations/cameraagent-<uuid>.state-reset-<operation-uuid>.appsettings.local.json` until it
is written back into the recreated `state/identity`, and then that copy is removed. A settings file that is a
symbolic link, has hard links, or is larger than 1 MiB stops the reset before anything is deleted.

A reset interrupted after the deletion but before the write-back leaves the file only in that copy. The next
install rerun or `reset-state` that is not a dry run puts the newest copy back before it seeds or deletes
anything, and removes a copy only once the settings file holds the same content, so a rerun never seeds a fresh
file over the operator's settings. A copy whose content differs from the settings file is left in place for the
operator.

```bash
hvo-sky-install cameraagent uninstall --instance-id <uuid>
hvo-sky-install cameraagent reset-state --instance-id <uuid> \
  --confirm-instance-id <uuid> --dry-run
hvo-sky-install cameraagent reset-state --instance-id <uuid> \
  --confirm-instance-id <uuid>
hvo-sky-install cameraagent install --instance-id <uuid> <same immutable inputs>
```

`--dry-run` records the same authenticated deletion inventory and prints the exact destructive and preserved
paths without deleting anything. Both forms write owner-only evidence to
`<product-root>/operations/state-reset-<operation-uuid>.evidence.json` containing the operation ID, request
hash, host identity, instance manifest, destructive roots, preserved paths, and the complete per-entry
ownership/mode/inode inventory captured before deletion. Deletion re-authenticates every entry's type,
ownership, mode, link count, and filesystem before unlinking it and rejects symbolic links, foreign
filesystems, and hard links.

After deletion each state directory is recreated empty with the runtime UID/GID and mode `0700`, the retained
completed installation result is withdrawn, and the retained installation phase returns to `Preflight` so the
final `install` rerun re-seeds the owner from the preserved temporary password and converges the same
instance identity back to healthy. Take an instance backup first if the deleted capture history matters; see
[product-instance-layout.md](product-instance-layout.md).

## CameraAgent Lifecycle

Run lifecycle commands as the Docker-capable deployment user, never as root.
Image and catalog transitions authenticate through the lifecycle-control credential
provisioned during installation and bound to the instance manifest. Candidate
health, installation identity, owner identity, and runtime ownership are verified
without accepting an operator password file. Image references must be immutable digests:

```bash
hvo-sky-install status --instance-id <uuid> --json
hvo-sky-install cameraagent upgrade --instance-id <uuid> \
  --image-ref <repository@sha256:digest>
hvo-sky-install cameraagent rollback --instance-id <uuid>
hvo-sky-install cameraagent uninstall --instance-id <uuid>
hvo-sky-install cameraagent reinstall --instance-id <uuid>
hvo-sky-install cameraagent backup --instance-id <uuid>
hvo-sky-install cameraagent restore <archive> --instance-id <uuid>
```

An upgrade whose candidate declares the installed state contract is a pure swap. The candidate declares a state
migration, and the upgrade then snapshots the small databases before the candidate starts, when any of these holds:

- A persisted-state boundary label differs between the installed image and the candidate, compared exactly:
  `io.hvo.skymonitor.state-compatibility` (on the installed side, the contract the instance manifest records),
  `minimum-compatible-revision`, `identity-migration`, `raw-ingress-schema`, `raw-ingress-migration`, or
  `catalog-manifest-version`.
- Either image omits one of those labels other than `raw-ingress-migration`, so the two cannot be compared.
  Only an image that migrates an earlier raw journal declares `raw-ingress-migration`, so both omitting it is
  agreement.
- The persisted raw-ingress journal exists and is initialized, and its `PRAGMA user_version` is not the
  candidate's `raw-ingress-schema`, or the journal cannot be read. A journal at `user_version` 0 with no schema
  objects is one the runtime has not initialized yet, which is a fresh start rather than a migration.

The check runs after the state preflight, so an incompatible persisted state is reported first, and before the
drain and any Compose change. The journal's `stateMigration` names every difference, for example
`io.hvo.skymonitor.raw-ingress-schema installed 14, candidate 15`. The contract-identity labels (`component`,
`configuration-contract`, `catalog-contract`, `replay-runner-contract`) are not state: a mismatch there is refused.
The candidate must also declare the current `cameraagent-state-v2` contract, and the persisted state must satisfy
every declared boundary described in [State Compatibility Boundary](#state-compatibility-boundary). A rollback
target installed before that label correction is accepted as a known contract.

Each transition pins the Docker endpoint and daemon identity, validates the
rendered Compose model, records pre-mutation continuity, and journals mutation
intent before pause or stop. Candidate Compose, manifest, and result identities
commit while capture remains paused; resume is the final idempotent action. If
final acknowledgement is lost, rerun the exact command with `--resume`. A failed
candidate restores the exact prior Compose, image, identity records and, for a
declared migration, databases before capture resumes. Noncurrent manifests and
results are rejected before lifecycle state mutation; invalid candidate images or
rollback models are rejected before runtime mutation.

### Backup and restore

`backup` is the one command that copies everything an instance holds, bulk captures included; an upgrade or rollback
never copies bulk data (install contract draft 4).

```bash
hvo-sky-install cameraagent backup --instance-id <uuid> [--plan] [--json] [--resume]
hvo-sky-install cameraagent restore <archive> --instance-id <uuid> [--plan] [--json] [--resume]
```

**Where backups are kept.** Each backup is one file in `<product-root>/backups`, named
`hvo-sky-<instance>-<yyyyMMddTHHmmssZ>-<operation>.tar.gz`, with its record `<archive>.json` beside it. Under the
production product root, the folder and both files are `root:root` and owner-only, because a backup holds the
instance's secrets: its Identity database, Data Protection keys and tokens. The installer reaches them through one
narrow `sudo` helper, `internal-backup-vault`. That helper re-reads the host policy itself, accepts only archive names
the installer gives, and streams one archive through its standard input or output. Under a test product root, the
folder and files belong to the runtime user. Copy a backup off the host as root, both files together. Backups are kept
until you remove them.

**What a backup holds.** It is a gzip-compressed PAX tar:
- under `instance/`, every folder and regular file of the instance root, except the instance lock and the owner
  recovery socket;
- under `snapshots/<operation>/<snapshot>/`, a verified snapshot of the instance's SQLite stores, taken while the
  instance is stopped through the SQLite online backup API, as an upgrade takes one
  ([Swap upgrade and database snapshots](#swap-upgrade-and-database-snapshots)), and the snapshot the manifest keeps
  for a rollback, so a restored instance can still roll back;
- as its last entry, the manifest `hvo-sky-backup.json`: every entry's path, type, mode, size and SHA-256, and the
  instance, image, release, catalog, product root and runtime user the backup belongs to.

A link, device or other special file in the instance refuses the backup before anything changes.

**How a backup runs.**
1. Before anything changes, it checks that there is room on each filesystem it writes: the archive at its worst case
   (no compression), the snapshot, and 256 MiB of headroom on each, with the bytes of any partial archive an earlier
   interrupted backup of the instance left counted as free. Too little room refuses it (exit `3`).
2. A running instance's capture is paused and drained, and its container stopped, only while the snapshot and the
   archive are written. The container is then started, verified and resumed before the archive is read back.
3. The archive is written as `<archive>.partial`, made durable, read back in full, and proved whole against its
   manifest and the hash taken as it was written. Only then does the backup commit it: the record is written first,
   then the partial gets its name.
4. Standard error reports progress at most every two seconds. With `--json` each report is a `backup-progress` event.
   The backup has no fixed deadline, so a large instance is never cut off part of the way.
5. Once complete, it removes every database snapshot of the instance that nothing refers to (see below).

A failure or Ctrl+C discards the partial and puts the instance back as it was (exit `1` or `130`). After an
interruption, `--resume` keeps nothing written before the archive was complete. From then on, it reads the archive
back, commits it and finishes. A partial or unrecorded archive is never restorable.

**How a restore runs.** `restore` puts back an instance that is uninstalled and preserved, or absent (after `purge`,
or on a rebuilt host with the same product root and Docker daemon). It never replaces an installed instance: uninstall
it first. `<archive>` is the backup's name, or its path in `<product-root>/backups`. Before anything changes, the
restore reads the whole archive and refuses it (exit `3`) when:
- it is partial, has no record, or its bytes are not the ones the record names;
- an entry is not the one its manifest names, the manifest is not last, or the archive cannot be read to its end;
- it belongs to another instance, product root or runtime user;
- the catalog it needs is not installed, Docker is another daemon, the instance's container exists, or there is not
  enough room to stage it.

A `--plan` makes the same checks and writes nothing.

The restore then:
1. records the counters the replaced instance handed out;
2. reads the archive a second time, staging its files in `<instance-root>/.hvo-restore/<operation>` and verifying
   every byte again;
3. swaps the staged files in, keeping what they replace until it completes;
4. puts the databases back through the same staged, durable and verified snapshot restore an upgrade's recovery uses,
   carrying every counter past what the replaced instance handed out;
5. revokes again the credentials the live instance revoked after the backup: an API key the backup holds and the live
   store does not is deleted, and an account whose password or security stamp changed keeps the live password and
   stamp. With no live credential store to compare (an absent instance), it warns loudly that every key and password
   valid at the backup is valid again. With `--json`, both are a `credentials-restored` event.

The instance is restored uninstalled, with its release records and install record settled from the restored manifest.
Start it with `hvo-sky-install cameraagent reinstall --instance-id <uuid>`, which brings it up through the verified
reinstall path. The Docker image the backup names must still be on the host for that.

Before the swap, a failure or Ctrl+C removes what was staged and changes nothing else. After it, the restore can only
go forward: `--resume` finishes it.

**Database snapshot cleanup.** When a backup or restore completes, it removes every snapshot under
`<product-root>/operations/lifecycle/<operation>/state-snapshots/` that its manifest says belongs to this instance and
that nothing refers to. A snapshot is kept when:
- the instance manifest keeps it for a rollback;
- an unfinished operation in the instance's journal wrote it or names it;
- the manifest that operation saved names it.

A snapshot whose manifest cannot be read, or that belongs to another instance, is never touched.

### Swap upgrade and database snapshots

An upgrade or rollback never copies bulk capture data. Frames, `state/raw`, `state/archive` and derived products
stay where they are (install contract section 6):

1. The previous image is kept. Capture is paused and drained, and the instance is stopped.
2. Only when a migration is declared (above), or when a rollback undoes a migrating upgrade, the small SQLite
   databases are snapshotted: the Identity database, the raw-ingress journal, the fleet, environment, evidence,
   time-lapse, nightly-product and automation stores, and the artifact outbox of every storage root. Each is copied
   with the SQLite online backup API into `<product-root>/operations/lifecycle/<operation>/state-snapshots/<snapshot>/`,
   and each copy must pass `PRAGMA integrity_check` and match its source's user version, schema and per-table row
   counts. A manifest binds the files by SHA-256, and the journal records the manifest's hash before the candidate
   may start.
3. The candidate starts and must pass the same health and installation-identity checks as before.
4. If it fails, the candidate is stopped, the snapshot (if any) is put back, and the previous image starts on the
   restored databases. A write-ahead log or journal the candidate left beside a store is removed first, so it can
   never be replayed over the restored bytes.

**The outboxes are found from configuration, never by walking the data.** A FileStorage step keeps its outbox at
`<storageRoot>/outbox/artifact-outbox.db`, and its root comes from `config/camera-module.json` or from a capture-schedule
revision, whose processing steps replace the file's at runtime. The inventory is the fixed stores above plus the
outbox under every `storageRoot` named in `camera-module.json` or in any revision the raw-ingress journal keeps, mapped
from its container path through the writable bind mounts (`/app/data/raw` to `state/raw`, `/app/data/archive` to
`state/archive`, `/app/App_Data` to `state/identity`). A root outside every persisted mount holds nothing that
survives the container, so there is nothing to snapshot. A root that is not an absolute, normalized container path
refuses the upgrade before anything changes. The unit tests compare this inventory by location with the stores the
CameraAgent actually creates (`CameraAgentStateInventoryTests` and `PreviousReleaseStoreReadabilityTests`).

**A store under a root named after the snapshot is removed by the restore.** A capture schedule created through the API
after the snapshot can name a new storage root, and its outbox there has nothing in the snapshot to go back to.
Removing it is what the restore warning promises: the database changes since the snapshot are lost. The previous
release then never meets it, even if the root is used again.

Before it replaces any store, the restore journals two lists (`stateRestorePlan`):
- every location the configured inventory lists now, together with any an earlier attempt recorded and, for a rollback,
  every location the rollback's own snapshot holds;
- every location the target snapshot records.

The inventory reads schedule revisions from the journal the restore is about to replace, so an interrupted retry still
knows a root whose revision the first attempt already rewound. A store at a current location that the target snapshot
does not record is removed with its SQLite sidecars. The directory, and every bulk file under that root, stays.

If a candidate damaged a store beyond reading, or left a value in a shape the installer does not read, its recovery
is not blocked:
- the inventory falls back to `camera-module.json`;
- that store's counters are carried only as far as the journal recorded them;
- the restore says so on standard error, naming what it could not read and why, or with `--json` as a
  `state-snapshot-restore-incomplete` event.

Every value a restore reads from a store the replaced release wrote is checked before it is used, never cast:
- **Schedule revisions.** A profile must be a JSON blob of at most 4 MiB, valid UTF-8 and well-formed JSON. A profile
  stored as text or as null, a blob that is not UTF-8, malformed JSON, or a root whose outbox location a snapshot
  cannot record (over 511 characters, or containing a control character) makes the journal's roots unreadable.
- **Counters.** The store must be encoded as UTF-8. A key must be the integer or text the registry names for it. A
  value must be an integer from 0 to 2^63 - 2. A carried automation revision must be a 64-character hexadecimal hash
  and well-formed JSON text. Any other shape, and any failure of any kind while reading, makes that store's counters
  unreadable.

A rollback reads the stores the release it undoes wrote in the same way: an unreadable journal narrows the rollback's
own snapshot to the configured roots and the stores the retained snapshot records, and the restore that follows says
so. Every store a rollback replaces is in its own snapshot, so rolling forward again brings each back. An upgrade still refuses an unreadable
journal before anything changes, because the installed release wrote it. The audit of every value read on a recovery
or restore path is in the #1314 correction-round-3 ledger.

**Retention and cleanup are held for the whole swap.** CameraAgent deletes frames, archived and derived products,
and unowned projected-scene stages only while capture admission is running. The installer pauses admission durably
before it stops the previous image and resumes it only after the candidate commits or the previous image is restored,
so a candidate starts with the hold in force. Only the deletions wait: capacity and disk pressure are still measured
and reported. An operator pause holds them too; a paused instance captures nothing new, so the hold never grows the
data it protects. While held, the log says
`<maintenance> held while capture admission is Paused; nothing was deleted`.

The pause is a deletion barrier. Every CameraAgent path that can delete, move or rewrite in place a file a restored
database row may name holds a capture maintenance lease. It takes the lease before it decides to delete, and keeps it
through its lock waits and the deletion. A lease is granted only while admission is running. A pass already admitted
holds the pause back until it has finished, so the installer sees `Paused` only when nothing more will be deleted.

The paths, and what each does while the barrier is held:

| Path | While capture is paused |
| --- | --- |
| retention | deletes nothing |
| derived-product reconciliation | deletes nothing |
| projected-scene stage reconciliation | deletes nothing |
| removal of a payload after its upload is acknowledged | records the acknowledgement and keeps the payload; it is no longer pending, so retention removes it later |
| removal of a projected-scene stage after its processing node commits | deferred until capture opens; if the image restarts first, the node's durable completion releases the stage and stage reconciliation removes it once capture runs |
| in-place upgrade of a frame sidecar to name its producer | fails retryably, and the node runs again once capture opens |
| raw-ingress evidence quarantine when the journal opens | finds mismatched or orphan evidence but moves none, reports Degraded (`reconciliation-held`), and quarantines it once capture opens |
| stage cleanup when the journal opens | deletes nothing |
| calibration bundle quarantine at host start | leaves the move planned for a later start |

A first initialization reads the durable state the journal records, because admission initialization waits for it, so no
pause can be acknowledged before it ends. A later re-initialization takes a lease without initializing admission.

The sweep of every deletion path, with why each remaining one cannot touch data a restore depends on, is in the #1314 correction-round-2
ledger. `--resume` keeps the pause from its recovery through its retry: the previous image restarts with capture
still paused, and the retry's candidate starts under the same pause. Capture resumes only once the retry has decided
the swap.

**A restore loses later database changes.** Restoring a snapshot writes this warning to standard error before it
changes anything (with `--json`, a one-line `state-snapshot-restore` event):

```text
Warning: restoring the CameraAgent databases from the snapshot taken at <time> (snapshot <uuid>). Every database
change made after that point is lost, including captures, settings and accounts recorded since then. Files written
since then stay on disk. When it starts, CameraAgent records again the raw frames it can verify and may process and
upload them again; other products written since then are no longer indexed.
```

**What happens to the files written since the snapshot.** A restore rewinds only the databases. Every file stays where
it is, and the previous image handles each kind of file differently:

- **Raw frames** (`state/raw/frames/**/Raw`). When raw ingress starts, before capture admission is consulted, it records
  again each frame whose sidecar is current and whose payload matches it: the frame's own capture identity and sequence,
  committed and retention-held, with its compatibility-index line repaired if missing. The capture-sequence floor
  above means a new capture never takes that sequence. A frame that fails verification, or that conflicts with a
  restored capture's identity, is moved to `state/raw/quarantine/`, never deleted, and raw ingress reports Degraded.
  A recorded frame is queued for its capture lanes again. It is processed again only if it arrived within the live
  queue age (`LiveMaximumQueueAgeSeconds`, 900 seconds by default) of the restart; older frames are abandoned as
  `processing-expired`. When uploads are enabled its raw manifest is queued for upload again whatever its age.
  Reprocessing adopts the files it already wrote when they match, and fails the node when they do not.
- **Stored artifacts under a storage root** (for example `state/archive`). An outbox never scans for files, so an
  artifact whose outbox row was rewound is not uploaded again unless its frame is reprocessed. It loses its upload
  hold and is removed on the root's ordinary age schedule (`retentionDays`).
- **Derived products** (`state/raw/derived`). Once capture resumes, derived-product reconciliation treats a product no
  processing row claims as an orphan. It keeps the product for `OrphanRecoveryWindowMinutes` (30 by default) after
  first seeing it, in case reprocessing claims it again, and then moves it to `state/raw/processing-quarantine/`
  rather than deleting it.
- **Projected-scene stages** (`state/raw/staging/projected-scenes`). Once capture is running, a stage no committed
  capture references is deleted. Stages of re-recorded frames count as referenced and are kept.
- **Time-lapse and nightly products** (`state/raw/.time-lapses/products`, `state/raw/nightly-products`). These are never
  rediscovered and no retention removes them, and a regenerated product with the same identity replaces the file.
- **A time-lapse publication the snapshot caught in progress is rolled back at startup, deleting that product's files.**
  The snapshot can record a publication that had begun but not finished. When the previous image's time-lapse worker
  starts, it rolls that publication back and deletes `state/raw/.time-lapses/products/<product>.*`. It does so even if
  the video was finished after the snapshot was taken, so a time-lapse that existed before the rollback can be gone
  after it. The job the snapshot recorded as working is queued again, so it can publish the video again. The restore
  warning does not name this case; this is accepted behaviour.

The unit tests prove these behaviours against a snapshot restore:
`InitializeAsync_AfterASnapshotRollback_RecordsFramesWrittenSinceTheSnapshotAgainWithTheirOwnSequences`,
`RunAsync_AfterASnapshotRollback_KeepsAProductWrittenSinceTheSnapshotThroughTheWindowThenQuarantinesIt` and
`ApplyRetentionAsync_AfterASnapshotRollback_AnArtifactWhoseOutboxRowWasRewoundExpiresOnItsAgeSchedule`.

**A restore never lets a client-held sequence or version be issued again.** Every counter in the CameraAgent stores
that hands out a sequence or version that leaves the instance, or that a client sends back, is carried forward
(`CameraAgentStateCounters`). Each one is carried in one of three ways:

- **High-water.** These are raised to at least their highest value:
  - the per-agent capture sequence (`raw_capture_sequences.last_sequence`), reserved before a capture's files exist;
  - the fleet-report sequence (`fleet_status_metadata.next_sequence`), which keeps advancing while capture is idle or
    paused;
  - the capture-control version (`capture_control_state.version`). The installer pauses before every snapshot and
    every restore, so the restored admission state and the live one are the same paused state.
- **AUTOINCREMENT row IDs that client action and reference tokens name.** These are the environmental outbox and journal
  records and the execution-evidence units. Their `sqlite_sequence` is raised the same way, so a rewound table never
  reissues an ID a token names.
- **Optimistic-concurrency versions a client sends back and the store compares equal.** These are:
  - the capture schedule;
  - the named-rig selection;
  - the calibration library;
  - the processing-graph registry;
  - each automation definition.

  A restored value below the highest one handed out moves above it, so every token issued after the snapshot is stale
  and the next change issues a version no client has seen.

Two cases need more than a raise:
- **An automation definition the snapshot lacks.** It is recorded in the restored store as removed, at a version above
  its highest. The store's own rule, that a recreated definition continues its revision line, then keeps its old
  versions from being issued again.
- **A lazily created singleton the snapshot lacks**, such as the schedule state or the graph registry. The previous
  release creates it at version 1 from the same configuration. A floor of 1 needs nothing. A higher floor cannot be
  carried into a row the snapshot lacks, so the restore says so and goes on (below).

Before it replaces any store, the installer journals each counter's highest value (`stateCounterFloors`): the higher of
the live stores and any floor already journaled, so a repeated or resumed restore never lowers it. Each restored store
has its counters carried, and read back, in one synced transaction before the previous image starts. The rollback's
continuity check compares against the journaled boundary and passes.

**Carrying counters is best effort and never blocks a restore.** On every attempt, before any store is replaced, the
installer plans what each store of the target snapshot receives:
1. Each floor is screened against the target. It is put back as the snapshot recorded it when:
   - the counter is unknown;
   - its value is outside what a carry can write and then read back (a state token writes one above its floor, so its
     floor must be at most 2^63 - 3);
   - its key is one the target column cannot hold, such as a NUL character, or a definition ID outside 1 to 64
     characters;
   - the target cannot carry it at all, such as a row the snapshot lacks beyond the lazy-row allowance.
2. What remains for a store is carried and verified, in one transaction, on a scratch copy of the target store under
   the operation directory. This proves the carry against that store's real CHECK constraints and column types.
3. A store whose trial fails in any way, or whose own values the installer cannot interpret, is put back without its
   counters. A store not encoded as UTF-8 is one such case. The snapshot's authentication proves its bytes, not that the
   installer can read its values.

The restore never writes a counter into the live store. For each store it:
1. copies the snapshot to a staged file and checks its SHA-256;
2. carries the planned floors into that copy in one transaction, which commits only when every floor reads back;
3. closes it, and refuses it if a rollback journal or write-ahead log is left beside it;
4. runs `PRAGMA integrity_check`, reads every floor back again, and syncs the file;
5. removes the live store's sidecars, renames the copy over the live store, syncs the directory, and checks that the
   installed file has the staged copy's hash.

If the carry fails on the staged copy in any way, the copy and its sidecars are discarded, the snapshot is copied again
pure (and its SHA-256 checked), and that copy is installed instead, so the live store is only ever the snapshot's bytes
or a verified carried copy. If that pure copy fails too, the restore stops without recording `stateSnapshotRestoredUtc`,
and `--resume` restores again. Whatever was not carried is named, with why, on standard error, or with `--json` as a
`state-snapshot-restore-incomplete` event:

```text
Warning: the restore of the CameraAgent databases from snapshot <uuid> could not carry the <counter> counter of
'<store>' (<why>). Each is restored as the snapshot recorded it, so a sequence or version handed out since the
snapshot may be handed out again; the restore itself completes.
```

The journal keeps the marks themselves, and the plan is made again on every retry, so a mark a retry cannot carry never
fails the retry. The audit of every read, validation and apply step is in the #1314 correction-round-4 ledger, and the
audit of every write to a store after it replaces the live one, of which there are none, in the cap-exception round-2
ledger.

A guard test classifies every counter-like column in every store image as carried or as never leaving the instance,
with the reason (`CameraAgentStateCountersAuditTests`). A new one fails until it is classified. Revoked credentials are
another matter: an API key or session revoked after the snapshot comes back with the Identity database. This is
covered by the restore warning ("accounts recorded since then"); revoke them again after a rollback.

A failed candidate is restored while capture is still paused, so only what the candidate itself wrote is lost. A
committed migrating upgrade records its snapshot in `instance-manifest.json` as `previousStateSnapshot`. A later
`rollback` verifies that snapshot, runs the state preflight against its copies rather than the migrated stores,
shows the warning before it pauses anything, snapshots the current databases, and then puts the retained snapshot
back before the previous image starts. A rollback whose image fails restores the databases it replaced. The rollback
records its own snapshot as `previousStateSnapshot`, so rolling back again returns to the upgraded release with the
state it had. A tampered or missing retained snapshot refuses the rollback before anything changes. A pure swap
records no snapshot, and its rollback is a pure swap too. A [state reset](#cameraagent-state-reset) leaves the manifest,
and this reference, in place. No lifecycle operation can run on the reset instance, because its installation result is
withdrawn, until the documented `install` rebuilds the manifest, and that rebuild omits `previousStateSnapshot`. A
rollback after a reset and reinstall is therefore a pure swap to the previous image onto the reinstalled databases. It
never brings back the databases from before the reset, and it shows no restore warning. The installer campaign runs this
sequence against a real container.

**Every step resumes after a crash or power loss.** The journal records `stateSnapshotRequired` and the reason
(`stateMigration`) before the drain, the snapshot reference (`stateSnapshot`) once every copy and the manifest are
synced, and `stateSnapshotRestoredUtc` once a restore is synced and before the previous image starts. Each snapshot
file, the manifest and each restored store is written under a temporary name, synced, renamed and its directory
synced. Every directory a published reference depends on is durable first, and each is synced along with every
directory above it, up to the product root:

- the operation directory, before the journal first records that mutation started;
- the snapshot directory, after the snapshot manifest and before the journal names it;
- the rollback recovery files' directory, before the manifest commit, and again after a recovery puts them back;
- the Compose directory, after every rewrite of the live Compose files;
- the directory of every store a restore puts back or removes, at the end of each restore and before
  `stateSnapshotRestoredUtc` or any other completion record. This holds even when the directory already existed, since
  an interrupted attempt may have created it without syncing its parent.

A power loss can therefore never leave a reference, or a completion record, to a file whose directory entry was lost.

An interrupted operation whose snapshot is recorded but not yet restored recovers onto that snapshot, whatever the
candidate left behind. Every recovery authenticates that snapshot before it changes any container: `--resume` of an
upgrade or a rollback (a rollback previews its retained snapshot for the preflight but still restores its own first),
`--restore-only`, and the automatic recovery of a failed candidate, which leaves the candidate running rather than
stopping it to find the snapshot damaged. `--restore-only` serves upgrades only and refuses a rollback before anything
changes. `--resume` and `--restore-only` then check the original image's state boundaries against the snapshot
copies, not the live stores, so a candidate that left a newer or unreadable journal cannot block its own recovery. `--resume` stops whatever is running, restores the snapshot, starts the
previous image with capture still paused, and retries. It never restores again after the previous image has started on
restored stores, so it cannot overwrite what that image has captured since. `--restore-only` restores the same way
(below) and then resumes capture instead of retrying. A retry takes a new snapshot. Snapshots are kept with their
operation and are not deleted automatically.

**Migration rule.** A CameraAgent schema change must stay readable by the previous release for one release: add
first, remove in a later release. The unit test `PreviousReleaseStoreReadabilityTests` holds every CameraAgent SQLite
store to it. `tests/fixtures/state-compatibility/previous-release/` freezes each of the previous release's stores as a
SQL image at the location the instance keeps it, for the storage roots the fixture's `manifest.json` names. Each image
holds every table, its metadata rows, every index, trigger and view, and the user version. Every CameraAgent store
validates its own schema exactly, so the test compares the complete schema object by object as normalized SQL. It also
compares the rows that record a store's version: the version column of a `*_schema` or `*_metadata` table, and the EF
migration history. It fails in either of two cases:

- the stores the current code creates differ from the images;
- the previous release's stores, once the current code has opened and migrated them, differ from the images.

A processing table gaining a column while `capture_processing_schema` stays at 8 fails it. So does a new table, index
or column in any other store, a changed user version, a fleet `schema_version` bump, or an added EF migration. The
fixture changes only in the release pull request that cuts a version (`HVO_WRITE_PREVIOUS_RELEASE_FIXTURES`
regenerates it). The fixture also freezes the previous release's image labels (`imageLabels`).

A change that cannot meet the rule is declared in the fixture's `snapshotRollbackExceptions`, naming:

- the store;
- its changed objects;
- an image state-boundary label;
- that label's value on the previous release (`previousValue`, which must equal the frozen one);
- its value on this release (`candidateValue`, which must equal this release's Dockerfile).

`CameraAgentSnapshotRollbackExceptionTests` then reads both label sets through the installer's own label reading and
boundary comparison (`CameraAgentStateMigration.DeclaredChanges`). It accepts the exception only when that comparison
reports a change for the label, which is what makes the upgrade snapshot the stores and the rollback restore them. An
unchanged label, a label the installer does not compare, and values that do not match either release are all refused.

### Upgrade notes and older targets

An upgrade from a release reads the release's `release.json` (install contract sections 4 and 6). Before it changes
anything, including before the lifecycle journal, it does two things:

- It prints the upgrade notes of every release after the installed version, up to the release the target belongs to.
  A release candidate shows its release's notes. `--dry-run` shows them too.
- It refuses a target older than the installed version, says that an upgrade never goes back, and names
  `hvo-sky-install cameraagent rollback --instance-id <uuid>`.

Upgrade-note text follows one terminal-safety rule. CRLF becomes LF; LF and TAB are retained, so tabs keep their
existing terminal spacing behavior. All other Unicode controls (Cc), all format characters (Cf), line and paragraph
separators (Zl/Zp), private-use characters (Co), and Unicode noncharacters are refused. This includes bidi controls,
LRM/RLM/ALM, zero-width formatting, BOM (U+FEFF), and soft hyphen. A refusal names the note's version and the first
unsafe code point as `U+XXXX` (at least four hexadecimal digits), without echoing its text. Ordinary letters,
punctuation, symbols, combining marks and spaces in any script remain valid.

Human rendering applies the same rule as defense in depth for directly constructed notes: unsafe code points appear
visibly as `<U+XXXX>`, including lone CRs and U+2028/U+2029. Only CRLF is normalized to LF; unsafe Unicode separators
are escaped rather than turned into extra lines.

With `--json`, standard output carries only the one result, which is written when the upgrade ends, so the notes are
written to standard error first as one `upgrade-notes` event on a single line:

```json
{"schemaVersion":1,"event":"upgrade-notes","upgradeNotes":{"installedVersion":"1.0.0","targetVersion":"1.2.0","notes":[{"version":"1.1.0","text":"Nothing to do by hand."},{"version":"1.2.0","text":"Read the Camera \u0026 rig page after upgrading."}]}}
```

- The event comes before the journal, the pause and the image change, so a caller sees the notes before
  anything changes. A failed upgrade has already written it, and its error object follows it.
- `installedVersion` is `null` when it is not recorded. `text` keeps its line breaks as `\n`.
- The result repeats the notes in its `upgradeNotes` member. A result with no `release.json` behind it has no such
  member.
- A refused older target writes no event, only the error object.
- Read standard error line by line: an object with `event` is an event, one with `errorCode` is the error, and callers
  ignore event names they do not know.

The installed version is the one the retained release record names for the running image. An instance installed from an
image reference records none. Its upgrade then shows every note up to the target, says that the installed version is not
recorded, and cannot apply the older-target refusal.

An upgrade from `--release` reads the notes from the release's `release.json`, and records the installed release
version once the upgrade completes (see [Release Install](#release-install)). An upgrade from `--image-ref` has no
`release.json`, so it shows no notes, withdraws the release record, and leaves the next upgrade without an installed
version. A rollback restores the record of the image it returns to, so the older-target refusal applies again.

### Restore Only After an Interrupted Upgrade

Ordinary `upgrade --resume` retries the upgrade, including the database snapshot
and candidate startup. **Do not use it merely to restore service after a failed
upgrade.** For an operator-image upgrade whose exact original runtime is already
healthy, the bounded recovery-only form is:

```bash
hvo-sky-install cameraagent upgrade \
  --instance-id <original-instance-uuid> \
  --image-ref <original-request-candidate-reference> \
  <same-original-request-options> \
  --resume --restore-only --operation-id <interrupted-operation-uuid>
```

Retain the original image reference, archive checksum, `--no-download`, and
compatibility acknowledgement exactly as supplied to that upgrade. The command
checks the original request hash and operation UUID; it does not acquire or
inspect the candidate image. This first recovery slice supports operator-image
upgrades only, not `--release` upgrades, rollback, or catalog operations.

The original manifest/result and operation snapshots must agree exactly, including
the prior rollback history. Owner-only bounded snapshot reads, canonical configuration
hash, rendered original Compose hash, daemon identity, root ownership/inode checks,
container image/user/mount/port/security checks, protected schema-1 bound application
identity, original image schema boundaries and candidate request/platform correlation,
protected installation identity/owner verification, and both retained tokens remain
required. Admission must match the recorded paused version and capture sequence.
Changed state, candidate-running, absent/unhealthy original runtime, foreign admission,
expired/rejected credentials, and committed or partially committed candidates fail
closed. No backup archive, including a leftover partial archive, authorizes recovery.

An upgrade that recorded a database snapshot is recovered onto it. The snapshot is
authenticated, and the original image's boundaries are checked against its copies
rather than the live stores, before anything changes. A snapshot that fails
authentication refuses before any container is touched. Restore-only then stops
the instance, puts back a snapshot that is not yet restored (with the restore
warning and counter floors described above), starts the original image from its
authenticated Compose file when it is not already running on the restored stores,
and waits for it to be healthy before the checks below.

Without a recorded database snapshot this form never pauses capture,
stops/recreates/restarts a container, writes Compose, changes identity, or resets
state. With one, it stops and starts only the instance's own container as above, and
it still never pauses capture, writes Compose, changes identity, or resets state. An approved Docker runtime memory override remains
untouched; it is not a change to the authenticated Compose file. It does not provide a
cold-start or candidate-to-old-image restoration procedure.

Before resuming admission the journal retains a command UUID. Retries reuse that
UUID and the original pause version, so a lost acknowledgement cannot create a new
command or override a later operator pause. The authenticated executed receipt is
validated separately from current admission: if a lost acknowledgement was followed
by an operator pause, replay proves the earlier Running result while leaving that
newer pause intact, including after restart. This settles recovery instead of leaving
an interrupted operation permanently blocking further lifecycle work. Normal success reports
`restored-previous-healthy-admission-resumed`; the original upgrade remains terminal
`Failed` / `Restored`, with `mutationStarted=false` and the original failure retained.
Older interrupted journals lacking a failure detail explicitly record that it was
not retained, rather than inventing a cause. Superseded success reports
`restored-previous-healthy-resume-superseded-current-admission-preserved`; the journal
retains the executed resume receipt separately from the current post-recovery boundary.
Both are validated on read, including nonnegative counters, initialization, versions,
capture sequence and receipt timing. The identical complete invariant is checked
before terminal publication against one captured post-resume boundary. Inconsistent
timestamps (including a future receipt or a backward clock) leave the operation
`Restoring` with its mutation flag and command UUID retained for a later valid replay;
they never publish terminal success. Repeating a successful restore-only request
verifies the current result without another resume command. It never reports the
upgrade completed, and ordinary `--resume` cannot restart a settled recovery.

The explicit [`backup` command](#backup-and-restore)
([#1353](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1353)) checks its
capacity, reports progress, has no fixed deadline, and discards its partial archive
on cancellation; issue [#1044](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1044)
keeps the qualification of a large (61 GiB) instance. Do not manually edit the
lifecycle journal or reduce an approved memory override to work around a limit.

The protected installation-verification GET used by both the pre-mutation owner
state read and full installation identity verification has one finite **120-second
request deadline**, including response-body receipt. Each call makes one request;
this is not an unlimited retry or a change to the separate health/startup, login,
or drain limits. An elapsed request deadline reports
`CameraAgent installation verification request timed out.` without transport
details or credentials. Caller cancellation remains cancellation, not a timeout.
Before mutation, a timeout follows the existing refusal policy without pause,
stop, snapshot, or image replacement; caller cancellation leaves an unmutated
operation resumable. Do not manually repair the journal or reset the instance.
Issue [#1041](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1041) records
an old-image response of about 35 seconds, beyond the former 15-second client
limit; this CLI compatibility allowance does not fix or attribute the endpoint's
underlying repeated work.

Capture-admission initialization is a CameraAgent startup responsibility that
runs after configuration initialization and before the processing and capture
workers; it does not depend on the camera capture worker reaching its loop. The
lifecycle client treats uninitialized `Initializing` or `Unavailable` snapshots
as transient startup observations. An authenticated pause or resume request made
before that initialization completes returns `503 Service Unavailable` with a
one-second `Retry-After` header and does not try to initialize capture admission
on the request thread. The installer retries that response, `408`, `429`, and
non-`500` 5xx responses with one idempotent command ID inside the existing drain
deadline, honoring `Retry-After` when present. A `500` or a non-transient status
remains terminal. Once capture admission is initialized, a
`Running` or terminal `Unavailable` snapshot fails a drain confirmation after
the first state read instead of consuming the full drain deadline.

Uninstall removes only the selected Compose runtime and preserves config,
secrets, state, evidence, rollback identities, and shared catalogs. Purge is a
separate operation and requires `--confirm-instance-id <same-uuid>` after
uninstall. Purge quarantines the inode-authenticated instance tree and resumes
deletion from that tombstone after interruption; it never republishes a partially
deleted tree.

`<productRoot>/cameraagents` stays `root:root 0755`, so the runtime user cannot
rename or remove its entries. Purge first tries each of its two namespace
mutations as the runtime user. Only when the kernel denies that does it run
`sudo -- /proc/<pid>/exe internal-purge-namespace` for exactly one of them:

- **quarantine** renames `<instance-id>` to
  `.purge-<instance-id>-<operation-id>` without replacing an existing entry;
- **remove** removes that tombstone after the runtime user has emptied it
  through the inventory-bound deletion.

The helper re-reads and validates the host policy itself and accepts only the
production product root it resolves, and only when `sudo` reports the same
non-root UID and GID that the command names. It opens every component from `/`
to `cameraagents` without following links and requires each to be root-owned
and not writable by group or other users; under a host-policy root every
component must also belong to group `root`. It derives both entry names
from the instance and operation identifiers. It acts only on a runtime-owned
directory with the authenticated inode and device, on the namespace's own mount,
and it removes only a tombstone that holds no subdirectories; the kernel refuses
any remaining file. It never deletes recursively and never widens a permission.
The CLI re-authenticates the result after the helper returns. A denied or failed
helper leaves the journal at `Purge / Mutating`; rerun the same command with
`--resume`, which continues from whichever side of the atomic rename the
interruption left. Once the journal records the mutation, the CLI adopts an
instance root only if it is the evidenced tree. A retry before quarantine reuses
the deletion evidence that its first attempt recorded and never replaces it. It
continues only when all of these match:

- the evidence names this operation, request and host, and records no
  settlement;
- the evidence records exactly the current manifest and, when it recorded one,
  the current `cameraagents` directory;
- the current tree is exactly the evidenced inventory.

An added, missing or replaced entry stops the retry before anything is renamed.
The CLI also refuses any instance root that reappears after the purge committed
its deletion.

The `Manual` contracts in `PurgeNamespaceRootTests` prove this boundary with
real root ownership. Each run uses a disposable private mount namespace, so the
host's `/var/lib` and installed instances are never touched. The run needs a
non-root user with non-interactive `sudo`, `unshare`, `setpriv`, a reachable
Docker daemon, and a published CLI:

```bash
dotnet publish src/HVO.SkyMonitor.Deployment.Cli -r linux-x64 -c Release -o /tmp/hvo-cli
HVO_DEPLOYMENT_CLI_PUBLISHED=/tmp/hvo-cli/HVO.SkyMonitor.Deployment.Cli \
  dotnet test tests/HVO.SkyMonitor.Deployment.Cli.Tests --configuration Release \
  --filter "TestCategory=Manual&FullyQualifiedName~PurgeNamespaceRootTests"
```

Sometimes `--resume` finds neither the instance root nor its tombstone, although
the journal never committed deletion. The tree is gone from its name, but that
does not prove it was removed, because it may have been moved instead.

The CLI therefore never settles that case on its own. It refuses and prints the
operation ID. Once the operator has confirmed that the tree was removed outside
the CLI, they rerun the same command with that ID as an explicit disposition:

```bash
hvo-sky-install cameraagent purge --instance-id <uuid> --confirm-instance-id <uuid> \
  --resume --confirm-removed-operation-id <operation-id>
```

The disposition is accepted only with `cameraagent purge --resume` and a
canonical, nonempty UUID. It is not part of the request hash. It must name the
interrupted purge. The CLI refuses it whenever the instance root or the
tombstone exists, and whenever the purge already committed its own deletion.

Even with the disposition, the CLI settles only when all of these hold:

- the journal and the retained deletion evidence match the request and this
  host;
- the Docker daemon matches and no container references the instance;
- `cameraagents` is the directory that held the evidenced tree, when the
  evidence recorded it. Evidence written before that identity existed relies on
  the operator's disposition alone for this check;
- `cameraagents` is on the device that held the evidenced tree, so an unmounted
  data filesystem is not mistaken for a deletion;
- nothing exists at the instance name, not even a dangling link;
- no other tombstone of the instance exists;
- the evidenced tree is not under any other name in `cameraagents`.

The CLI then flushes `cameraagents` to disk so that the observed absence is
durable. Only after that does it record `"settlement": "already-absent"` with a
time in the evidence, complete the journal, and report the outcome
`already-absent`, not `completed`. It does not claim it deleted the tree, and it
creates no path. A committed purge whose emptied tombstone has gone also flushes
`cameraagents` before it completes. If a flush fails, the journal stays
resumable. A completed or settled purge is terminal: another `--resume` reports
that no matching interrupted purge exists.

The CLI cannot see a tree that was moved out of `cameraagents`. That is why the
disposition is required, and the operator who supplies it is attesting removal.

Catalog versions are explicit per instance:

```bash
hvo-sky-install catalog install --catalog-bundle /owner-private/catalog.bundle
hvo-sky-install catalog select --instance-id <uuid> --catalog-version <version>
hvo-sky-install catalog rollback --instance-id <uuid>
hvo-sky-install catalog gc --catalog-version <version>
```

Each approved catalog ID has its own root,
`<productRoot>/catalogs/<catalogId>/versions/<package-version>`, so HYG 4.2
(`hyg-v42-production`) and HYG 4.4 (`hyg-v44-production`) install side by side.
`install` takes the catalog ID from the bundle manifest and refuses an ID,
package version, lineage or hash that the embedded approved-catalog registry
does not name. It never changes a selection. Package versions are unique across
lineages, so `--catalog-version` names exactly one root. A `select` that changes
the catalog ID rewrites `Catalog__RequiredCatalogId` and the `HVO_CATALOG_ROOT`
mount and reauthenticates the Compose model. It requires an image whose catalog
contract label is `hvo-approved-catalogs-v1`, retires the retained image
rollback, and pins the previous identity as a historical reference. `rollback`
returns to the previous selection across lineages. The exact identities and
selection behavior are documented in
[HYG 4.4 catalog snapshot](../catalog/hyg-v44.md).

Mutating garbage collection requires one explicit version. A dry run without a
version plans collection across every installed approved catalog ID. It fails closed on
unknown instance roots, malformed selection pointers, active Docker mounts,
manifests, backups, operations, rollback slots, or historical reconstruction
references. Interrupted deletion continues from an authenticated tombstone and
never restores a partial immutable catalog. Resume requires the same explicit
version and `--resume`; request hash, operation ID, host, daemon, root inode, and
the original tree inventory must still match. Missing original entries are
accepted after partial deletion, while additions and replacements fail closed.

LogicHost lifecycle, remote orchestration, and physical-camera discovery remain
future lifecycle scope.

## Harness Inputs For A Published Image

Both container harnesses can exercise a published image instead of building one,
so a release can be proved against the bytes an operator receives:

```bash
# Install, health, replay-runner health, stop-and-reinstall idempotence, preflight,
# bind-source ownership and mode, uninstall, the explicit reset, and the
# migration-assertion upgrades, against a published archive rather than a locally
# built image. The authenticated owner recovery and rollback contract, with its
# upgrade from an older baseline, belongs to the baseline-revision run and is not
# exercised here.
case "$(uname -m)" in
  x86_64) release_architecture=amd64 ;;
  aarch64|arm64) release_architecture=arm64 ;;
  *) printf 'Unsupported machine architecture: %s\n' "$(uname -m)" >&2; exit 2 ;;
esac
HVO_PRODUCTION_CATALOG_BUNDLE=<bundle> \
HVO_INSTALLER_RELEASE_ARCHIVE=<release>/skymonitor-cameraagent-<version>-linux-${release_architecture}.oci.tar \
  ./scripts/test:deployment-installer

# Two-agent standalone smoke against a published image. Diagnostic mode only:
# this run records the local worktree revision, so it cannot produce citable
# evidence for an image built elsewhere.
HVO_CAMERAAGENT_SMOKE_IMAGE=<tag, repository digest, or image ID> \
  ./scripts/test:cameraagent-dual-standalone-smoke
```

Neither variable changes anything when unset; both harnesses build their own
image as before. `HVO_INSTALLER_BASELINE_REVISION` takes precedence, because a
baseline upgrade contract needs two images built from two revisions.

The harness derives its native platform once from `uname -m`: `x86_64` selects
`linux/amd64` and the `linux-x64` deployment CLI, while `aarch64` or `arm64`
selects `linux/arm64` and the `linux-arm64` CLI. Any other architecture is
refused. The Docker server must be local Linux of the same normalized
architecture, and every archive loaded by the harness must report that native
platform. An emulated image or an ARM host driving an x64 Docker server is not
native qualification evidence.

### Published ARM64 release smoke

Every integration pre-release runs the published-release path on native ARM64
before it is published: the workflow's arm64 job installs a throwaway instance
from the draft's arm64 offline archive on `github-runner-pi-01` and upgrades it
by digest from GHCR ([prereleases.md](prereleases.md#what-the-workflow-does)).
To repeat it by hand on a qualified ARM64 host, download a pre-release's files
as [prereleases.md](prereleases.md#installing-a-pre-release-on-skymonitor01)
shows and run `scripts/test:release-install --release <folder>`. Never use a
production CameraAgent host for it. A final release or release candidate runs the same test from its draft on both
linux-x64 (a GitHub-hosted runner) and linux-arm64 before the owner can approve publishing it
([releases.md](releases.md#what-the-workflow-does)).

## Build Evidence

Build the two supported self-contained binaries with:

```bash
dotnet publish src/HVO.SkyMonitor.Deployment.Cli/HVO.SkyMonitor.Deployment.Cli.csproj \
  --configuration Release --runtime linux-x64
dotnet publish src/HVO.SkyMonitor.Deployment.Cli/HVO.SkyMonitor.Deployment.Cli.csproj \
  --configuration Release --runtime linux-arm64
dotnet publish src/HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj \
  --configuration Release --runtime linux-x64
dotnet publish src/HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj \
  --configuration Release --runtime linux-arm64
```

Each installer publish is one self-contained file. The native SQLite library that catalog validation opens is bundled
into it, and the .NET host extracts it when the installer starts, under `$DOTNET_BUNDLE_EXTRACT_BASE_DIR` or, by default,
`~/.net`; the user running the installer needs a writable home for that. `scripts/release:assets` refuses a publish that
leaves anything the installer loads beside the executable.

Each installer publish is stamped with its version and commit. A publish given no version is the product version's
development build, `X.Y.Z-dev+<commit>`. A release passes `-p:Version=<version> -p:SourceRevisionId=<40-character
commit>`. The build refuses a version that is not the product version or a pre-release of it (`Directory.Build.targets`,
[releases.md](releases.md#the-product-version)), a version that is not 1-128 letters, digits, periods and hyphens, and
a commit that is not 40 lowercase hexadecimal digits or `unknown`. `scripts/ci:component-publish delivery`, the Architecture & Publish job and the release workflow
each check that `--version` prints exactly `<version>+<commit>`. On ARM64 they check that the binary contains that
string.

Run the x64 output with `--capabilities`; require protocol version `1` and
completed runtime/native-library warmup. On an ARM64 builder, the existing
`scripts/test:cameraagent-arm64-ci` gate performs the equivalent native publish,
capability, image, and constrained-container checks.

The campaign additionally proves the state preflight, the operator-confirmed CameraAgent-only reset, and the
reset-then-reinstall convergence against a real image and a real product root.

It then proves the migration assertion against two variants of the installed image that differ from it only in
added labels. An upgrade to the variant that declares the same state boundaries completes without
`--migration-backward-compatible`. The variant that declares a different `minimum-compatible-revision` is refused
without it, leaving the instance, its manifest, and its running image unchanged and the refusal journalled as
terminal, and the same upgrade then completes with it. A run with `HVO_INSTALLER_BASELINE_REVISION` skips this
step, because its baseline image may predate the boundary labels, and keeps the assertion on its own baseline
upgrade.

Run the disposable installer contract against the current image with
`./scripts/test:deployment-installer`. To validate an owner-recovery image
transition, use a clean committed worktree with an ancestor pre-support commit
available locally:

```bash
HVO_INSTALLER_BASELINE_REVISION=<pre-support-commit> \
  ./scripts/test:deployment-installer
```

The extended path builds the baseline image and candidate image/CLI from exact
Git archives, completes the initial password replacement to establish an
owner-ready baseline, verifies a completed installer rerun preserves that state,
proves unsupported recovery retains resumable state, performs an image-only
upgrade, authenticates the mounted owner-only socket, and resumes the same
recovery operation without restarting the CameraAgent process. It rejects the
prior durable password and authenticated session, verifies the
replacement-password gate, hard-restarts and replays recovery idempotently,
verifies capture control remains running, and rolls back while preserving
installation, configuration, catalog, and recovered Identity state. The exact
production catalog bundle remains required through
`HVO_PRODUCTION_CATALOG_BUNDLE`.

Final releases are cut by `.github/workflows/release.yml` ([releases.md](releases.md)), and integration pre-releases
by `.github/workflows/prerelease.yml` ([prereleases.md](prereleases.md)). The key-signed design, its independent
installer and catalog trains and its bootstrap script were retired by the owner on 2026-10-16 and removed (#1310).
