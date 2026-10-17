# CameraAgent Deployment Installer

The `hvo-skymonitor` self-contained Linux CLI installs one local, standalone
VirtualSky CameraAgent without a repository checkout or a target-host .NET
runtime. Docker Engine and the Compose plugin are the only application-runtime
prerequisites; the host must also pass the [host prerequisites](#host-prerequisites)
that install and `install --dry-run` evaluate. Catalog releases can be resolved from signed local metadata,
HTTPS mirrors, or signed release indexes; every source uses the same pinned
signature, length, checksum, archive, and internal catalog validation.

`hvo-skymonitor --version` prints the installer's version and the commit it was built from, as
`<version>+<40-character commit>`, and exits 0. Like `--help`, it needs no Docker and does not read the
[host policy](#production-product-root-on-another-filesystem), so it works even where that policy is refused. A build
given no version is `0.0.0-dev`, and a build without a checkout or a supplied commit prints `0.0.0-dev+unknown`.
[Build Evidence](#build-evidence) describes how a publish is stamped.

## HVO Install Contract

The shared [HVO product installation and release contract](../hvo-install-contract.md)
is the install experience this installer is being brought to under epic
[#1318](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1318). This
runbook describes the installer as it behaves today.

**Current differences:**

| Contract | Today | Issue |
| --- | --- | --- |
| Installer `hvo-sky-install`, runtime client `hvo-sky` with a terminal UI, and an installer wizard | One `hvo-skymonitor` CLI, configured by flags or `--config`; no runtime client or wizard | [#1313](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1313), [#1317](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1317) |
| `install.sh` entry point with `--check` and `--from DIR` | No `install.sh`; the self-contained CLI is run directly | [#1311](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1311) |
| Install and upgrade from a published release, by GHCR digest or offline archive | Nothing is published, so hosts install a locally built image by its loaded image ID | [#1309](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1309) |
| `--version`, and upgrade notes printed before an upgrade | `--version` reports the installer build identity. The release.json reader and pre-upgrade notes are implemented; the release acquisition path that supplies them is pending #1309 (see [Upgrade notes and older targets](#upgrade-notes-and-older-targets)) | [#1312](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1312) |
| `--plan`, `--answers FILE`, `backup` and `restore FILE`, exit codes 0/1/2/3/130 | `--dry-run` and `--config`; no `backup` or `restore`; a refusal such as failed host prerequisites exits `1` | [#1315](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1315) |
| Swap upgrade that never copies bulk data, snapshotting only the small databases for a declared migration | Upgrade and rollback take a `tar` backup of the instance's configuration and state | [#1314](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1314); [#1044](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1044) narrows to the `backup` command |
| Container limits kept in the install record and changed with a re-run | Limits are set at install and recorded in the instance's `instance-manifest.json`. A re-run with different `--cpus` or `--memory` is refused, and an installed instance's limits change only when an upgrade moves limits the operator did not set to the new default (see [Container Resources](#container-resources)) | [#1315](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1315) |
| Settings catalogue served by the API | CameraAgent serves the [operator settings catalogue and group writes](../contracts/cameraagent-operator-settings-v1.md) through its operations API; the owner creates API keys for non-browser clients | [#1316](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1316) |
| Machine install record `/etc/hvo-sky/install.json` | Records live beneath each instance root (`instance-manifest.json`, `state/deployment/`) | [#1315](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1315), path [#1313](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1313) |

## Inputs

Supply an immutable CameraAgent repository digest or an already loaded image ID,
plus the verified production catalog bundle. An offline image archive additionally
requires its independently trusted lowercase SHA-256. Never use a mutable image
tag or pass a password on the command line.

The compatibility form below consumes the internally verified local catalog
directory produced by the deterministic catalog tooling:

```bash
hvo-skymonitor cameraagent install \
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

For a signed offline catalog release, place `catalog-manifest.json`,
`catalog-manifest.json.sig`, and the referenced bundle together and use:

```bash
hvo-skymonitor cameraagent install \
  --channel local \
  --catalog-manifest /srv/hvo/catalog-release/catalog-manifest.json \
  --no-download \
  --friendly-name "North All-Sky Camera" \
  --image-ref sha256:<loaded-image-id>
```

For an exact online release, use an immutable versioned manifest URL. To resolve
a documented channel default or `--catalog-version`, use `--catalog-index` and a
signed immutable index snapshot. A non-GitHub mirror also requires
`--asset-base-url`; mirrored bytes retain the release tag, manifest hash, asset
hash, and signing identity. Channels are `stable`, `nightly`, `prerelease`, and
`local`; `local` is the compatibility default.

`--no-download` permits local inputs and fully verified cache hits but performs
no HTTP request and no Docker registry pull. The user cache is
`$XDG_CACHE_HOME/hvo/skymonitor/distribution`, or
`~/.cache/hvo/skymonitor/distribution` when `XDG_CACHE_HOME` is unset. Cache
hits are rehashed, partial transfers use signed lengths and HTTP ranges, invalid
complete or partial bytes are removed, and signed-index sequence/hash state
prevents rollback. Network requests are HTTPS-only with bounded redirects,
timeouts, retries, response sizes, and disk-space checks. Optional public GitHub
authentication uses `HVO_GITHUB_TOKEN`; credentials are sent only to the
original `github.com` request and never retained as evidence.

Signed-index rollback state is durable rather than cache data. It is stored
under `$XDG_STATE_HOME/hvo/skymonitor/distribution`, or
`~/.local/state/hvo/skymonitor/distribution` when `XDG_STATE_HOME` is unset, and
is not removed by ordinary cache cleanup.

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
does not carry it. `--dry-run` validates the catalog, Docker daemon, loaded image, generated
configuration, and rendered Compose model using private temporary storage without
creating the product root, loading/pulling an image, or starting a container.

Missing required values are prompted only on an interactive terminal. A strict
JSON config may be supplied with `--config`; value options cannot be mixed with
it. Its `cpus` number and `memory` string are the `--cpus` and `--memory`
[container limits](#container-resources). Flags such as `--dry-run`, `--json`, `--no-download`, and the explicit non-loopback HTTP
acknowledgement may still be applied. A configuration that does not name
`productRoot` installs into the production product root, as the command-line form
does; one that names a root keeps it and is validated like `--product-root`.

To serve the camera on a LAN address, bind it there and acknowledge plaintext
HTTP:

```bash
hvo-skymonitor cameraagent install ... \
  --bind-address 192.168.2.138 --port 5130 --acknowledge-plaintext-http
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
| `tar-missing` | an executable `tar` on `PATH`; upgrade and rollback run it for the consistent instance backup |
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
measures the instance backup and staged archive against the product root's own
filesystem.

A host that cannot enforce the limits would otherwise run CameraAgent unbounded,
so it is refused. Raspberry Pi OS boots with `cgroup_disable=memory`. The remedy
is to add `cgroup_enable=memory` to the single line in
`/boot/firmware/cmdline.txt`, reboot, and confirm that `docker info` reports
`MemoryLimit`, `CpuCfsQuota`, and `PidsLimit` as `true`. Each limit finding
quotes the cgroup version, `/sys/fs/cgroup/cgroup.controllers` when it is
readable, and Docker's own limit warnings.

The human form renders every finding and its remedy on standard error. With
`--json` the structured report is written to standard output and standard error
carries one error object with `errorCode` `host-prerequisites`; the exit code is
`1` either way. The CLI uses SQLite in-process and never runs `sqlite3` or `jq`,
so neither is required. Uninstall, purge, and the other lifecycle commands do not
evaluate these prerequisites, so a host that loses limit support can still be
cleaned up.

## Container Resources

The installer limits the CameraAgent container to **2 CPUs, 4 GiB of memory and
256 PIDs** unless the operator sets other limits, as section 7 of the shared HVO
install contract requires. Raise them when installing:

```bash
hvo-skymonitor cameraagent install ... --cpus 3 --memory 6g
```

- `--cpus` is a number from 2 to 1024 with at most two decimal places, such as
  `2.5`.
- `--memory` is whole mebibytes or gibibytes, such as `6144m` or `6g`, from `4g`
  to `1024g`.
- In a `--config` file they are the `cpus` number and the `memory` string, for
  example `"cpus": 3, "memory": "6g"`. Leaving a member out, or setting it to
  `null`, leaves that limit to the default.
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

Install and `install --dry-run` check the limits against the host Docker runs on
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
- **Reinstall** keeps the recorded limits, and so does a re-run of a completed
  install. A re-run is held to the same request: adding, dropping or changing
  `--cpus` or `--memory` is refused with `The retained installation state belongs
  to different immutable inputs`. The same limit written another way, such as
  `6g` and `6144m`, is the same request.
- A failed upgrade candidate restores the exact prior Compose file and manifest,
  limits included.
- The limits of an installed instance cannot be changed yet, apart from the
  default moving on upgrade.

An instance installed before limits were recorded has no `resources` member. It
runs the original 1 CPU, 1 GiB and 256 PID limits. No operator could set them,
because the installer had no option for them, so none counts as set by the
operator. Its next `cameraagent upgrade` moves it to 2 CPUs and 4 GiB and records
`resources` with both flags `false`; until then a re-run of its install keeps the
original limits exactly. A runtime `docker update` override, such as a memory
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

## Signed Image Release

> `linux/arm64` installation is qualified natively: the `open(2)` flag defect
> ([#603](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/603)) is fixed,
> the advisory arm64 workflow runs this CLI's Unit suite, and the complete signed
> installer lifecycle has run on aarch64 under
> [#651](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/651). The campaign
> uses locally signed published-format candidates. An archive downloaded from an
> actual production release remains untested because no production release exists;
> preserve that publication/download caveat until the first release smoke.

`--image-ref` and `--image-archive` name an image the operator has already
established. `--image-manifest`, `--image-index`, and `--image-version` instead
consume the signed CameraAgent image train described in
[release-distribution.md](release-distribution.md), and the two forms are
mutually exclusive: a signed release resolves the image, so an installation
never both trusts a release and accepts an operator-supplied image.

An air-gapped installation receives the release directory on local media and
needs neither registry access nor a source checkout:

```bash
hvo-skymonitor cameraagent install \
  --channel local \
  --image-manifest /media/hvo/image-v1.4.0/image-manifest.json \
  --catalog-manifest /media/hvo/catalog-release/catalog-manifest.json \
  --no-download \
  --friendly-name "North All-Sky Camera" \
  --owner-email admin@home.lan \
  --latitude 35.2 --longitude -114.1 --elevation 800 --time-zone America/Phoenix
```

Online, use an immutable versioned manifest URL, or `--image-index` with
`--image-version` to resolve a documented default from a signed index snapshot.
A non-GitHub mirror also requires `--asset-base-url`; that option is shared with
the catalog train, so a single invocation that names both a mirrored image
release and a catalog release resolves both from the same base. Signed indexes
are rollback-protected per train, so an older image index is refused just as an
older catalog index is.

The installation retains the verified archive under
`<instance-root>/state/deployment/image-archive.tar` so a resumed or repeated
operation does not re-acquire it. It is the size of the published image and is
included in instance backups; size the instance filesystem accordingly.

The installation resolves the release, selects the platform matching this host,
and verifies the offline archive against its signed length and checksum **before
the Docker daemon is contacted**. An unsupported architecture, a release that
does not publish this host's architecture, a tampered archive, a mismatched
signature, or a rolled-back index therefore fails while nothing has been loaded,
started, or written to instance state. After Docker resolves the image, the
labels it actually carries are compared against the signed compatibility record,
and every contradicted boundary is reported in one message.

The selected release is retained beside the other deployment evidence:

```text
<instance-root>/state/deployment/image-distribution.json
```

It records the release train, tag, version, manifest checksum, signing key,
asset name and checksum, resolved source URI, verification result, the platform
and immutable image ID that were installed, the source revision and tree, the
SBOM, provenance, and vulnerability-scan asset names, the per-platform component
inventory asset published for the installed architecture (absent for a release
published before inventories existed), and the exact compatibility boundaries
the release declared. An operator can correlate a running container with its
release, and name the component inventory to triage against, without network
access. The record is schema version 2; a version-1 record written by an earlier
installer has no inventory member and remains valid.

The record follows the image the instance actually runs. It is written only once
the image has been prepared and accepted, a refused install or upgrade leaves
none behind, and a rollback withdraws whatever the superseded upgrade recorded.
Its `manifestDigest` is the release's multi-architecture identity; for a release
published without a registry push that value is a computed index digest and is
not resolvable with `docker pull`.

An upgrade accepts the same options and writes the same evidence before the
operation begins. It selects the platform from the architecture recorded for
the instance's Docker daemon at installation, not from the machine running the
CLI, so a remote or cross-architecture daemon is served the archive it can run;
a release that does not publish that architecture is refused, naming what it
publishes, before anything is downloaded:

```bash
hvo-skymonitor cameraagent upgrade \
  --instance-id <uuid> \
  --channel local \
  --image-manifest /media/hvo/image-v1.5.0/image-manifest.json \
  --no-download
```

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

`cameraagent preflight` accepts the same release selectors and resolves them the same way, so a planned upgrade
can be evaluated against persisted state first without acquiring, loading, or starting anything; see
[State Compatibility Boundary](#state-compatibility-boundary).

`--migration-backward-compatible` remains a separate operator assertion about the
candidate's state migration and is required only when the candidate declares one;
[CameraAgent Lifecycle](#cameraagent-lifecycle) defines exactly when that is.
Do not add it to a routine upgrade; doing so defeats the gate it exists for.

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
transition. The image and signed release must declare schema 15 and that
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

### Proving the signed lifecycle in the installer campaign

`scripts/test:deployment-installer` proves the signed lifecycle against real
containers when `HVO_INSTALLER_SIGNED_RELEASE_CAMPAIGN=1` is set. The scenario
builds two release candidates with `scripts/release:cameraagent-image` from two
committed revisions in throwaway Git worktrees, so each signed manifest
describes exactly the tree it was built from, then drives one instance through
every transition the retained release record has to follow:

| Transition | Command | `image-distribution.json` |
| --- | --- | --- |
| Install from the superseded release | `cameraagent install --image-manifest <a>` | present, naming that release and the running image |
| Refused by the production trust root | the unmodified CLI, same upgrade | unchanged |
| Preflight the candidate release | `cameraagent preflight --image-manifest <b>` | unchanged; nothing is acquired or started |
| Candidate verification fails after mutation | `cameraagent upgrade --image-manifest <b>` through the campaign-only verifier fault | byte-identical; still names the restored superseded release and image |
| Resume the same upgrade to the candidate release | `cameraagent upgrade --image-manifest <b> --resume` | present, naming the new release and the new image |
| Upgrade to an operator-supplied image no signed release named | `cameraagent upgrade --image-ref <a-image>` | withdrawn (absent) |
| Upgrade back to the candidate release | `cameraagent upgrade --image-manifest <b>` | present again, naming the candidate release and image |
| Refused: signed by a key the trust root does not hold | `cameraagent upgrade --image-manifest <untrusted>` | unchanged; still names the running release |
| Refused: declares a key identity the trust root does not carry | `cameraagent upgrade --image-manifest <declared-key>` | unchanged |
| Refused: the trusted key's signature over a different release | `cameraagent upgrade --image-manifest <forged>` | unchanged |
| Refused: names an archive that is not the one it signed | `cameraagent upgrade --image-manifest <mismatched>` | unchanged |
| Rollback to the retained previous image | `cameraagent rollback` | absent |
| Refused: release contradicts the image labels | `cameraagent upgrade --image-manifest <contradicting>` | still absent |
| Refused: resuming the refused upgrade | `cameraagent upgrade --image-manifest <contradicting> --resume` | still absent; the journal is byte-identical |
| State-compatibility preflight on what the sequence left behind | `cameraagent preflight` | still absent |
| Uninstall without `--resume` after the refusal | `cameraagent uninstall` | still absent; the container is gone |

Every upgrade in the table passes `--migration-backward-compatible` only when
the two releases declare different state boundaries, as an operator would. Two
releases built from adjacent revisions normally declare the same ones, so their
upgrades then run without it. `summary.txt` records which applied.

Each refusal targets its own gate. The production-trust-root refusal and the
four derived-release refusals share the acquirer's single diagnostic, because
the CLI deliberately does not surface the inner verification cause; what
separates the derived releases is that each varies exactly one trust or
integrity condition against a release the instance has already accepted, and
that each is required to leave every durable deployment record — the release
record, the instance manifest, the installation result and state, the retained
preflight report, the staged image archive, and the lifecycle journal —
byte-identical. Only the label-agreement refusal has a diagnostic of its own,
and it is the only refusal that runs after the lifecycle journal has been
opened. The campaign asserts that the journal records it as a terminal
refusal: `status` is `Failed`, `failureCode` is `lifecycle-refused`, the
`failureMessage` carries the gate's diagnostic, and `mutationStarted` is
`false`. That record blocks nothing: resuming the refused command is refused
because no incomplete operation exists, and the closing uninstall runs without
`--resume` (the `LifecycleContractTests` prove the same for a rollback and a
different upgrade). Every transition reads the running image back through
`status`, whose `status` outcome (rather than `drifted`) means the container
really carries the recorded image. Every step retains, under
`TestResults/issue-598/<run>/`, the release record when it exists and an
explicit `image-distribution.absent` marker when it does not, the instance
manifest, the installation result and state, the retained preflight report, the
lifecycle journal and its candidate diagnostics once a lifecycle operation has
created them, a state inventory with modes, deployment and Compose checksums
(the staged image archive appears there by SHA-256 rather than as a copied
file), the container log, the container's real image and health, and the exact
CLI reports the assertions read.

Every trust or integrity refusal in the scenario happens before the upgrade
mutates anything. The separate post-mutation failure runs only after the signed
candidate has passed acquisition, image-label agreement, state compatibility,
capture drain, Compose stop, backup, Compose-up, application health, and installation-identity
verification. A campaign-local Docker shim then records the real healthy,
unprivileged candidate inspection and reports an impossible privileged runtime
to the candidate-container verifier exactly once. The existing lifecycle catch
restores the prior image, byte-identical Compose files, manifest, and result,
resumes capture, and leaves `image-distribution.json` present and byte-identical
naming that restored signed release. Every recovery command uses unmodified
Docker results, and neither production code nor either signed candidate is
changed to create the fault.

The campaign proves that the retained record follows the image the instance runs
across every transition above. The record is settled as soon as an upgrade or
rollback commit is durable and again on `--resume`, so a signed upgrade whose
final resume acknowledgement is lost still records its release when completed
with `--resume` (proved by `LifecycleContractTests`, because the campaign cannot
lose a real acknowledgement on demand), and an `--image-ref` upgrade of an
instance installed from a signed release withdraws the superseded record
(transition `03d` above, against a real container).

The refusals bracket the trust decision from several sides. The release
contradicting the image labels is genuinely signed and is refused by the
label-agreement gate after acquisition and before any mutation. Four more are
refused during acquisition:

- a manifest signed by a key the trust root does not hold, and release B's
  manifest presented with the trusted key's real signature over release A. Both
  fail signature verification: a signature the trusted key cannot verify at all,
  and a signature it verifies but not over these bytes. The forged case is the
  one that isolates signature verification, because nothing else can refuse it.
  The untrusted case also declares the untrusted key's identity, so it would
  still be refused by the declared-key comparison if verification were removed.
- a manifest declaring a key identity the trust root does not carry, signed by
  the key it does. `VerifyManifest` verifies the signature before comparing
  `signing.keyId`, so this is the only way to reach that comparison — the release
  tool refuses to sign a manifest whose declared key is not the signing key, so
  the campaign produces this signature in detached mode.
- a manifest naming the other candidate's archive under this release's signed
  length and checksum: the only case that fails on the acquired bytes rather than
  on the metadata.

None of the four derived releases or the label-agreement refusal writes,
rewrites, or resurrects the retained release record. The four derived releases
must leave every durable deployment record and the lifecycle journal
byte-identical; the label-agreement refusal legitimately journals its refused
operation as terminal and must leave every other durable record byte-identical.

Each refused release is a manifest and signature in its own directory that names
the real published archives through `--asset-base-url`, so nothing copies or
links them, each published candidate still contains exactly the files its own
`SHA256SUMS` describes — which the run verifies at the end — and the installer's
refusal to read a hard-linked input still applies to every file it opens. The
retained evidence keeps each refused manifest, the exact signature it was
presented with, and the public keys that verify them, under
`refused-releases/`.

The contradicted boundary is the signed compatibility record — the campaign
changes the release's `minimumCompatibleRevision` — because the manifest's own
consistency rules already bind the release identity, the repository, the source
revision and tree, the evidence assets, and each platform's archive to one
another. The compatibility record is the one claim about the image that the
manifest cannot check against itself, which is precisely why the installation
compares it against the labels the image actually carries.

### The ephemeral key: what the campaign does and does not establish

The production signing key is a Key Vault key that only the release workflow's
federated identity may sign with. No identity available to this campaign can
sign with or export it, so a production-signed release can be produced only by a
real publication run, which this scenario excludes. The campaign therefore signs
both candidates with an ephemeral P-256 key and publishes a campaign-only CLI,
built from the same committed revision, whose embedded trust root is that
ephemeral public key. The key is generated fresh for each run into the
disposable workspace; a durable `HVO_INSTALLER_SIGNED_WORKSPACE` retains it
alongside the candidates it signed, so reruns against that workspace reuse it.
Either way it is never a production key. Nothing else is changed: no
verification step is removed, relaxed, or bypassed.

The substitution is proved to redirect trust rather than to disable it. An
unmodified CLI, published from the same archived candidate source so that it
differs from the campaign CLI only in the embedded trusted key, is run against
the same signed release and must refuse it; the campaign CLI must refuse the
same release under a key it does not hold, must refuse the trusted key's real
signature over other bytes, and must refuse a release whose archive is not the
one it signed. A campaign whose trust root had simply been switched off would
pass none of them.

What the campaign establishes: the whole signed image lifecycle — manifest and
signature verification, asset length and checksum verification, platform
selection, the agreement between the signed compatibility record and the labels
the image actually carries, automatic exact restore after a post-mutation
candidate-verification failure, the terminal journal a pre-mutation refusal
leaves behind, and the retained release record across install, upgrade,
rollback, refusal, and the closing uninstall — against real multi-architecture release
candidates, a real Docker daemon, and a real running CameraAgent.

The manifest's declared-key-identity comparison is included: reaching it needs a
signature the trust root can verify over a manifest that declares a different
key, which the campaign produces with a detached signature.

What it does not establish: that the committed production public key matches the
Key Vault private key, that the workflow's federated identity can sign, that the
signatures Key Vault produces verify against the committed trust root, that key
custody and rotation behave as documented, or anything about the registry push
and the published index. Those remain the first real publishing run's evidence,
as [release-distribution.md](release-distribution.md) records.

## Local Replay Runner

Archived replay remains in the CameraAgent process by default. Select the
optional long-lived local process boundary only during installation:

```bash
hvo-skymonitor cameraagent install \
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
production product root is `/var/lib/hvo/skymonitor`, unless the root-owned host
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
request it parses, and production always reads `/etc/hvo/skymonitor/host.json`.

### Production product root on another filesystem

A host that keeps application data on another filesystem, for example NVMe
mounted under `/srv`, names the production product root in the root-owned host
policy instead of bind-mounting it over `/var/lib/hvo/skymonitor`:

```json
{"productRoot": "/srv/data/hvo-skymonitor", "mountPoint": "/srv"}
```

Write it with the convenience command, as the deployment user, **before the
first instance is installed**:

```bash
sudo install -d -o root -g root -m 0755 /srv/data   # the parent must already exist
hvo-skymonitor host set-product-root /srv/data/hvo-skymonitor
```

The command escalates through one narrow `sudo`-executed internal command,
`internal-set-product-root --product-root <path>`, which accepts nothing else and
always writes `/etc/hvo/skymonitor/host.json`. It re-reads and re-validates the
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
`/run/hvo-skymonitor-host-policy.lock` (`root:root 0600`, so no other user can
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
broke the file, therefore needs a privileged repair, which no `hvo-skymonitor`
command performs.

A manual edit of `/etc/hvo/skymonitor/host.json` does **not** take the
host-policy lock (`/run/hvo-skymonitor-host-policy.lock`) that
`host set-product-root` and the privileged preparation hold. A command started
while the file is being changed can therefore resolve a different production
root, for example create a new instance under `/var/lib/hvo/skymonitor` while
the policy is briefly absent, and that instance would then block restoring the
real root. Repair only inside a maintenance window:

1. **Open the window.** Stop or hold every automation that runs
   `hvo-skymonitor`, and run no `hvo-skymonitor` command by hand: no
   `cameraagent install`, `upgrade`, `rollback`, `reinstall`, `uninstall`,
   `purge`, `reset-state`, `recover-owner`, `preflight`, no `catalog` command, no
   `host` command, and no `status`, until step 5 is done. Confirm that nothing is
   running: `pgrep -af hvo-skymonitor` must print nothing. Running CameraAgent
   containers are unaffected and keep running.
2. **Repair in place, preferring storage over the file.** If the intended
   filesystem can be mounted, mount it at the recorded `mountPoint`, confirm it
   with `findmnt -no SOURCE,UUID <mountPoint>`, and change nothing in the file.
3. **Otherwise edit only the mount field.** Run
   `sudo editor /etc/hvo/skymonitor/host.json` and either correct `mountPoint`
   or delete just the `mountPoint` member so the setter records it again. Keep
   `productRoot` exactly as it was: it is the root the instances live under.
   Keep the file `root:root 0644`.
4. **Never delete the file while instances exist under its `productRoot`.**
   Without the file every command falls back to `/var/lib/hvo/skymonitor`, so the
   instances under the policy root become unreachable: `status` and every
   lifecycle command look only in the root in force. Remove `host.json` only when
   that root is empty, after every instance under it has been uninstalled and
   purged.
5. **Re-establish and verify, then close the window.** Run
   `hvo-skymonitor host set-product-root <the same productRoot>`, which validates
   the repaired policy and records the mount point that currently holds the
   root, then `hvo-skymonitor status`, and confirm that every expected instance ID
   is listed. Only then resume automation and other `hvo-skymonitor` commands.

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
Signed installs additionally retain the release train/version/tag, exact
manifest and asset length/hash, signing key ID, source and resolved public URI,
verification result/time, and provenance identity without authentication data.

The installer creates the product root through one narrow `sudo`-executed
internal preparation command when needed. That command prepares only the
production product root the host policy resolves, under the host-policy lock:
under the default root it may also create `/var/lib/hvo`, and under a policy root
it validates the existing root and creates its `cameraagents`, `logichosts`, and
`catalogs` namespaces as `root:root 0755`. `catalog install` and `catalog gc` use
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
hvo-skymonitor cameraagent recover-owner --instance-id <uuid> --generate-password
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
hvo-skymonitor cameraagent install --resume --instance-id <uuid> <same-inputs>
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
migration from schema 12 or earlier: a populated schema-12 journal is rejected by preflight before drain, backup,
stop, or Compose mutation, and runtime startup also refuses it without changing its contents.
Upgrading such an instance requires the CameraAgent-only reset below or an equivalent explicit
state-disposition procedure. The superseded `backward-compatible` label value remains readable only so an
already installed image stays inspectable; it is never accepted as an upgrade candidate declaration.

Deployment preflight compares the declared boundaries against the persisted state before Compose starts the
container, so an incompatible instance fails once with the complete boundary list instead of through container
restart loops. Install, upgrade, and rollback all run it before any backup, drain, stop, or `docker compose`
invocation and before the container starts. The candidate image is inspected first because its
labels are the expected values being compared, and on install the catalog bundle and Compose files
are written first because the preflight reads the selected catalog and the bind sources they
establish. Install and upgrade retain the report at `state/deployment/state-preflight.json`. Run
it on demand without starting, loading, pulling, or mutating anything:

```bash
hvo-skymonitor cameraagent preflight --instance-id <uuid> --json
hvo-skymonitor cameraagent preflight --instance-id <uuid> \
  --image-ref <repository@sha256:digest>
hvo-skymonitor cameraagent preflight --instance-id <uuid> \
  --image-manifest /media/hvo/image-v1.5.0/image-manifest.json
hvo-skymonitor cameraagent preflight --instance-id <uuid> \
  --channel stable --no-download \
  --image-index https://mirror.example/indexes/image-stable-index.json \
  --image-version 1.5.0 --asset-base-url https://mirror.example/releases
```

With `--json`, an install or lifecycle invocation reports on standard error only in one-line JSON objects: any events,
such as an upgrade's notes (see [Upgrade notes and older targets](#upgrade-notes-and-older-targets)), then at most one
error object, last. The rendered report is therefore written there only in human form. Read the complete report from
`state/deployment/state-preflight.json`, or from `cameraagent preflight --json` on standard output; a
`--dry-run` deliberately retains nothing, and its error message still enumerates every blocking code.

Without a candidate the installed image's declaration is evaluated; naming one evaluates that candidate's
declaration and requires the current contract, matching an in-place upgrade. `--image-ref` names an image this
host already holds and is read from the local Docker image store. `--image-manifest`, `--image-index`, and
`--image-version` instead name a signed image release from the train above and are resolved exactly as an
upgrade resolves one: the same signature and index verification, the same rollback protection, the same
offline-archive identification, and the same platform selection, which follows the architecture recorded for the
instance's Docker daemon rather than the machine running the CLI, so the report describes exactly the platform the
upgrade acquires. `--asset-base-url`, `--channel`, and `--no-download` apply to them as they do to an
upgrade and are rejected without `--image-manifest` or `--image-index`. A local-media manifest is read from disk
whether or not `--no-download` is given, so the flag matters only for an `https://` manifest or index. A signed release and `--image-ref` are mutually
exclusive here for the same reason install and upgrade refuse the combination.

A signed-release preflight is strictly read-only. It evaluates the release's own signed compatibility record,
so it neither acquires the offline archive nor contacts Docker at all and it reports on a release this host has
not received yet. It writes nothing into the instance, the release media, or the distribution download cache,
and it never advances the signed-index rollback state an acquisition would commit. A persisted database whose
journal carries unreplayed recovery state without a wal-index is read through a private temporary copy, so
replay never touches the instance. A database a writer currently holds is instead read in place, which
registers a reader in the wal-index that already exists, exactly as the instance's own readers do: no file is
created and no durable state changes. That is preferred over copying a database under a live writer, since such
a copy can tear and report a busy instance as unreadable. Unlike install and upgrade the on-demand command retains no `state-preflight.json`. The
report names the resolved release tag beside the immutable image ID it selected; install and upgrade record the
same tag in the report they retain, which is the only release evidence a refused operation leaves behind.

A signed-release preflight also evaluates the upgrade's contract-identity gate from the signed declaration,
because every value that gate compares is in the release manifest and the instance manifest. The candidate's
component, configuration contract, and catalog contract must match this instance, and a LocalRunner instance
additionally requires the release to declare the local replay-runner contract; an in-process instance imposes no
runner requirement. Each mismatch is a blocking finding under the `contract-identity` boundary with its own code
(`contract-component`, `contract-configuration`, `contract-catalog`, `contract-replay-runner`), naming the
declared and required identities. The release's platform is selected for the instance's recorded daemon
architecture before this evaluation, so an architecture mismatch is reported as a resolution error rather than a
finding. A release built for another configuration or catalog contract, or one omitting the runner contract a
LocalRunner instance needs, therefore no longer preflights clean only to be refused after acquisition.

**A compatible signed-release preflight is still not a promise that the upgrade will proceed.** It compares
persisted state and the declared contract identities against this instance. Gates that need the image itself run
only during the upgrade: the labels the loaded image actually carries are compared against the signed record, and
a candidate declaring a state migration still requires `--migration-backward-compatible` (see
[CameraAgent Lifecycle](#cameraagent-lifecycle)). When either signed
identity for the selected platform (`offlineArchiveImageId` or `manifestDigest`) equals the installed
`manifest.image.imageId`, preflight remains compatible but includes the non-blocking
`candidate-image-already-active` advisory. The advisory tells the operator that no upgrade action is needed; an
attempted upgrade to that same image is still refused outright.

Rollback state is retained per operating-system user
(`$XDG_STATE_HOME/hvo/skymonitor/distribution`, else `~/.local/state/...`), so run the preflight as the same
user that will run the upgrade for its rollback protection to consult the same floor.

The command exits `0` when compatible and `1` with error code `state-incompatible` otherwise. Each finding
names its boundary code, path, observed
value, expected value, and remediation. The checked boundaries are the selected catalog manifest version and
catalog identity, the Identity migration lineage recorded in `__EFMigrationsHistory`, the raw-ingress
`PRAGMA user_version`, the ownership and mode of every writable Compose bind source, and, for a signed release,
the contract identities described above.

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
hvo-skymonitor cameraagent uninstall --instance-id <uuid>
hvo-skymonitor cameraagent reset-state --instance-id <uuid> \
  --confirm-instance-id <uuid> --dry-run
hvo-skymonitor cameraagent reset-state --instance-id <uuid> \
  --confirm-instance-id <uuid>
hvo-skymonitor cameraagent install --instance-id <uuid> <same immutable inputs>
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
hvo-skymonitor status --instance-id <uuid> --json
hvo-skymonitor cameraagent upgrade --instance-id <uuid> \
  --image-ref <repository@sha256:digest>
hvo-skymonitor cameraagent rollback --instance-id <uuid>
hvo-skymonitor cameraagent uninstall --instance-id <uuid>
hvo-skymonitor cameraagent reinstall --instance-id <uuid>
```

An upgrade whose candidate declares the installed state contract needs no further assertion. The candidate
declares a state migration, and the upgrade then also requires `--migration-backward-compatible`, when any of
these holds:

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
backup, the drain, and any Compose change. Its refusal names every difference, for example
`io.hvo.skymonitor.raw-ingress-schema installed 14, candidate 15`, and is journalled as a terminal refusal that
blocks no later operation. The contract-identity labels (`component`, `configuration-contract`,
`catalog-contract`, `replay-runner-contract`) are not state: a mismatch there is refused whether or not the
assertion is given.

`--migration-backward-compatible` asserts that the prior image can read the state a migrating candidate leaves
behind. A candidate that fails after it starts is restored by starting the prior image again on that state; the
pre-mutation backup is not restored, so no transactional restore path is reserved. The assertion is necessary but
not sufficient: the candidate must also declare the current `cameraagent-state-v2` contract, and the persisted
state must satisfy every declared boundary described in [State Compatibility Boundary](#state-compatibility-boundary).
Supplying it for a candidate that declares no migration is accepted and changes nothing, but passing it habitually
would admit the next real migration unexamined. A rollback never requires it, and a rollback target installed
before that label correction is accepted as a known contract because no state migration occurs on the way back.

Each transition pins the Docker endpoint and daemon identity, validates the
rendered Compose model, records pre-mutation continuity, creates and validates a
consistent backup, and journals mutation intent before pause or stop. Candidate
Compose, manifest, and result identities commit while capture remains paused;
resume is the final idempotent action. If final acknowledgement is lost, rerun
the exact command with `--resume`. A failed candidate restores the exact prior
Compose, image, and identity records before capture resumes. Noncurrent manifests
and results are rejected before lifecycle state mutation; invalid candidate images
or rollback models are rejected before runtime mutation.

### Upgrade notes and older targets

An upgrade from a release reads the release's `release.json` (install contract sections 4 and 6). Before it changes
anything, including before the lifecycle journal, it does two things:

- It prints the upgrade notes of every release after the installed version, up to the release the target belongs to.
  A release candidate shows its release's notes. `--dry-run` shows them too.
- It refuses a target older than the installed version, says that an upgrade never goes back, and names
  `hvo-skymonitor cameraagent rollback --instance-id <uuid>`.

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

- The event comes before the journal, the backup, the pause and the image change, so a caller sees the notes before
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

No current upgrade source supplies a `release.json`. The reader and the upgrade step that uses it are in place. The
release acquisition that selects the candidate image from the same `release.json`, and records the installed release
version, comes with #1309. Until then, image-reference and signed-manifest upgrades show no notes.

### Restore Only After an Interrupted Upgrade

Ordinary `upgrade --resume` retries the upgrade, including backup and candidate
startup. **Do not use it merely to restore service after a failed backup.** For
an operator-image upgrade whose exact original runtime is already healthy, the
bounded recovery-only form is:

```bash
hvo-skymonitor cameraagent upgrade \
  --instance-id <original-instance-uuid> \
  --image-ref <original-request-candidate-reference> \
  <same-original-request-options> \
  --resume --restore-only --operation-id <interrupted-operation-uuid>
```

Retain the original image reference, archive checksum, `--no-download`, and
compatibility acknowledgement exactly as supplied to that upgrade. The command
checks the original request hash and operation UUID; it does not acquire or
inspect the candidate image. This first recovery slice supports operator-image
upgrades only, not signed-release selectors, rollback, or catalog operations.

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

This form never pauses capture, stops/recreates/restarts a container, writes Compose,
changes identity, or resets state. An approved Docker runtime memory override remains
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

Issue [#1044](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1044) remains
open for size-aware backup deadlines, capacity, progress, cancellation/partial cleanup,
and resource-qualified cold startup. Do not manually edit the lifecycle journal or
reduce an approved memory override to work around those remaining limits.

The protected installation-verification GET used by both the pre-mutation owner
state read and full installation identity verification has one finite **120-second
request deadline**, including response-body receipt. Each call makes one request;
this is not an unlimited retry or a change to the separate health/startup, login,
or drain limits. An elapsed request deadline reports
`CameraAgent installation verification request timed out.` without transport
details or credentials. Caller cancellation remains cancellation, not a timeout.
Before mutation, a timeout follows the existing refusal policy without pause,
stop, backup, or image replacement; caller cancellation leaves an unmutated
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
hvo-skymonitor cameraagent purge --instance-id <uuid> --confirm-instance-id <uuid> \
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
hvo-skymonitor catalog install --catalog-bundle /owner-private/catalog.bundle
hvo-skymonitor catalog select --instance-id <uuid> --catalog-version <version>
hvo-skymonitor catalog rollback --instance-id <uuid>
hvo-skymonitor catalog gc --catalog-version <version>
hvo-skymonitor catalog check --catalog-manifest <signed-manifest-url-or-path>
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

`catalog check` is read-only. It verifies the signed manifest or index, or the
previously verified cached copy when offline, and reports `available` or
`installed`. It takes no lock, downloads no bundle, and changes no catalog,
selection, cache or rollback state. It refuses an installed version whose
identity differs from the signed release.

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
so a signed release can be proved against the bytes an operator receives:

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
HVO_INSTALLER_RELEASE_ARCHIVE=<candidate>/cameraagent-image-v<version>-linux-${release_architecture}.tar \
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

The signed-release scenario is a separate opt-in section of the same campaign
and adds its own instance rather than replacing the operator-supplied image
path:

```bash
HVO_PRODUCTION_CATALOG_BUNDLE=<bundle> \
HVO_INSTALLER_SIGNED_RELEASE_CAMPAIGN=1 \
  ./scripts/test:deployment-installer
```

| Variable | Meaning |
| --- | --- |
| `HVO_INSTALLER_SIGNED_RELEASE_CAMPAIGN` | `1` runs the signed install/upgrade/post-mutation automatic-restore/rollback/refusal scenario |
| `HVO_INSTALLER_SIGNED_BASE_REVISION` | Revision of the superseded release. Default `HEAD~1` |
| `HVO_INSTALLER_SIGNED_CANDIDATE_REVISION` | Revision of the upgrade candidate, which also builds the campaign CLI. Default `HEAD`. The base must be a distinct ancestor of it, and both must own the release train |
| `HVO_INSTALLER_SIGNED_ARM64_BUILDER` | `linux/arm64` builder for the release candidates. Falls back to `HVO_RELEASE_ARM64_BUILDER`, then to `hvo-edge-01-arm64` |
| `HVO_INSTALLER_SIGNED_VERSION_A` / `_B` | Candidate versions. Default `0.0.0-598a` and `0.0.0-598b` |
| `HVO_INSTALLER_SIGNED_WORKSPACE` | Durable owner-private directory for the signing key and two candidates, created if absent and set to mode `0700`. A retained exact-candidate workspace is cryptographically reverified and reused without recopying or rebuilding; leaving it unset creates a fresh disposable workspace |
| `HVO_INSTALLER_SIGNED_EVIDENCE_ROOT` | Retained evidence directory. Default `TestResults/issue-598/<timestamp>` |

The harness derives its native platform once from `uname -m`: `x86_64` selects
`linux/amd64` and the `linux-x64` deployment CLI, while `aarch64` or `arm64`
selects `linux/arm64` and the `linux-arm64` CLI. Any other architecture is
refused. The Docker server must be local Linux of the same normalized
architecture, and every archive loaded by the harness must report that native
platform. An emulated image or an ARM host driving an x64 Docker server is not
native qualification evidence.

Every one of those preconditions — a clean worktree, `openssl`, two distinct
ancestor revisions that both own the release train, and two distinct versions —
is checked before any scenario runs, so a misconfigured invocation fails in
seconds rather than after the rest of the campaign.

The scenario requires a clean worktree because its candidates and CLI are bound
to committed revisions. On first use it builds two multi-architecture candidates
and scans four archives; later exact-candidate runs authenticate and reuse the
retained packages. A complete first run that also exercises
`HVO_INSTALLER_BASELINE_REVISION` took 35 minutes on an amd64 host with a native
`linux/arm64` builder over the network, of which about 25 minutes were the two
candidate builds. Hold the shared Docker window for the whole run. It never
pushes to a registry, and CI never sets
`HVO_INSTALLER_SIGNED_RELEASE_CAMPAIGN`, so this scenario is an operator-run gate
rather than a CI-gated one.

### Reusing signed candidates for native ARM64 qualification

Candidate creation and native execution can use different hosts without
weakening identity. First run the corrected campaign on an x64 host with an
external, owner-private `HVO_INSTALLER_SIGNED_WORKSPACE`. That run builds and
verifies both multi-architecture candidates. Copy the complete workspace — the
two release directories, campaign signing key and public key, and untrusted test
key pair — to an owner-private directory on the ARM64 host. Do not copy only the
ARM64 archives: manifest verification, refusal derivations, and final
`SHA256SUMS` checks require the complete candidates.

On the ARM64 host, use a clean checkout that contains the same two exact Git
objects and set the same base/candidate revisions and versions:

```bash
umask 077
chmod 700 <external-signed-workspace>
chmod 600 <external-signed-workspace>/*.pem

HVO_PRODUCTION_CATALOG_BUNDLE=<exact-production-bundle> \
HVO_INSTALLER_SIGNED_RELEASE_CAMPAIGN=1 \
HVO_INSTALLER_SIGNED_BASE_REVISION=<release-a-sha> \
HVO_INSTALLER_SIGNED_CANDIDATE_REVISION=<release-b-sha> \
HVO_INSTALLER_SIGNED_VERSION_A=<version-a> \
HVO_INSTALLER_SIGNED_VERSION_B=<version-b> \
HVO_INSTALLER_SIGNED_WORKSPACE=<external-signed-workspace> \
HVO_INSTALLER_SIGNED_EVIDENCE_ROOT=<external-evidence-root> \
  ./scripts/test:deployment-installer
```

Before reuse, the production verifier authenticates each retained signed manifest,
every declared asset, and the independently signed checksum list, then binds the
release and image revision, tree, version, and tag to the requested Git objects.
The complete verification repeats after the lifecycle. A campaign retains 16
transition directories: `01`,
`02`, `03a` through `03e`, `04` through `07`, `08`, `09`, `09b`, `10`, and
`11`. Every applicable `image-distribution.json` must name `linux/arm64`, the
ARM64 archive and component inventory, and the expected immutable A/B identity.
Retain `summary.txt`, the manifests, signed checksums, refusal artifacts,
container evidence, host and Docker inventories, and before/after cleanup
inventories. Keep the owner-private exact-candidate cache through review and any
bounded rerun so the 1.3 GB package need not be recopied. For exact-candidate
reuse, keep the ephemeral `signing.pem` at mode `600` in that owner-private
workspace for as long as the cached candidates remain reusable; it is never a
production key. To retire the private keys instead, securely delete them and
delete the workspace's `release-*` directories (or the whole workspace) before
the next campaign. A later run generates a new key and correctly refuses retained
candidates signed by the retired key rather than rebuilding them. Immutable public
candidates and public-key evidence may be retained separately as evidence, not as
a reusable workspace after key retirement. Never use a production CameraAgent
host for this campaign.

### First published ARM64 release smoke

The first real image release, and each materially changed release process, must
run the published archive path on a qualified native ARM64 host. This is not a
release-production command: it downloads immutable public assets after the
release exists, verifies them with the production trust root, and exercises the
archive an operator receives.

```bash
set -euo pipefail
umask 077
release_tag=image-v<version>
release_root="$(mktemp -d /srv/hvo/image-release-smoke.XXXXXX)"
evidence_root=/srv/hvo/evidence/<issue-or-release>/<timestamp>
mkdir -m 700 "$evidence_root"

test "$(gh release view "$release_tag" --repo HualapaiValley/HVO.SkyMonitor \
  --json isDraft --jq .isDraft)" = false
gh release view "$release_tag" --repo HualapaiValley/HVO.SkyMonitor \
  --json tagName,targetCommitish,url >"$evidence_root/release.json"
gh release download "$release_tag" --repo HualapaiValley/HVO.SkyMonitor \
  --dir "$release_root"

dotnet restore tools/HVO.SkyMonitor.Deployment.ReleaseTool/HVO.SkyMonitor.Deployment.ReleaseTool.csproj
dotnet build tools/HVO.SkyMonitor.Deployment.ReleaseTool/HVO.SkyMonitor.Deployment.ReleaseTool.csproj \
  --no-restore --configuration Release -warnaserror
release_tool=(dotnet run --project tools/HVO.SkyMonitor.Deployment.ReleaseTool/HVO.SkyMonitor.Deployment.ReleaseTool.csproj \
  --no-build --configuration Release --)
"${release_tool[@]}" verify \
  --manifest "$release_root/image-manifest.json" \
  --signature "$release_root/image-manifest.json.sig" \
  --asset-root "$release_root"
"${release_tool[@]}" verify-signature \
  --input "$release_root/SHA256SUMS" \
  --signature "$release_root/SHA256SUMS.sig"
(cd "$release_root" && sha256sum --check SHA256SUMS)

test "$(jq -er '[.images[0].platforms[] | select(.operatingSystem == "linux" and .architecture == "arm64")] | length' \
  "$release_root/image-manifest.json")" = 1
archive_name="$(jq -er '.images[0].platforms[] | select(.operatingSystem == "linux" and .architecture == "arm64") | .offlineArchiveAsset' \
  "$release_root/image-manifest.json")"
test "$archive_name" = "cameraagent-image-v<version>-linux-arm64.tar"
test -f "$release_root/$archive_name"

{
  uname -a
  docker info --format 'server={{.ServerVersion}} os={{.OSType}} architecture={{.Architecture}} id={{.ID}} root={{.DockerRootDir}}'
  jq -er '.images[0].sourceRevision' "$release_root/image-manifest.json"
  sha256sum "$release_root/$archive_name"
} >"$evidence_root/host-release-inventory.txt"

HVO_PRODUCTION_CATALOG_BUNDLE=<exact-production-bundle> \
HVO_INSTALLER_RELEASE_ARCHIVE="$release_root/$archive_name" \
  ./scripts/test:deployment-installer 2>&1 | tee "$evidence_root/deployment-installer.log"
```

Require an `aarch64`/`arm64` host, a local Linux ARM64 Docker daemon on qualified
non-production storage, exit zero, a healthy unprivileged CameraAgent with
capture advancing, and clean before/after container, network, image, process,
and worktree inventories. Record the public release URL/tag, manifest source
revision, archive checksum, catalog identity, host/daemon/storage inventory, and
cleanup result. Until this post-release run exists, native signed-campaign
qualification does not by itself prove a production-published ARM64 archive.

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

Each installer publish is stamped with its version and commit. A publish given no version is `0.0.0-dev+<commit>`. A
release passes `-p:InformationalVersion=<version> -p:SourceRevisionId=<40-character commit>`. The build refuses a
version that is not 1-128 letters, digits, periods and hyphens, and a commit that is not 40 lowercase hexadecimal
digits or `unknown`. `scripts/ci:component-publish delivery`, the Architecture & Publish job and the release workflow
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

The `Signed Distribution Release` workflow publishes independent installer and
catalog tags, signed manifests and indexes, checksums, SBOMs, provenance,
licenses, and attribution. The optional bootstrap script downloads and verifies
only the selected self-contained CLI archive:

```bash
./scripts/install-hvo-skymonitor.sh 1.0.0 /opt/hvo-installer-1.0.0
```

Resolve a signed installer-index default or exact indexed version with:

```bash
./scripts/install-hvo-skymonitor.sh \
  --index https://github.com/HualapaiValley/HVO.SkyMonitor/releases/download/installer-index-3/installer-release-index.json \
  --version 1.0.0 \
  /opt/hvo-installer-1.0.0
```

The optional third direct-mode argument and `--asset-base` support HTTPS mirrors
or absolute local release directories. `HVO_INSTALLER_NO_DOWNLOAD=1` permits
only local or cached bytes. `HVO_INSTALLER_CACHE` selects an owner-only bounded
2 GiB cache; mirror redirects are refused and GitHub redirects are restricted
to approved release CDN hosts. The script retains the signed manifest, signature,
and sanitized `installer-distribution.txt` beside the verified executable; it
does not download or execute a helper verifier.
