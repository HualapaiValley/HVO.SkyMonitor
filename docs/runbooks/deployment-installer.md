# CameraAgent Deployment Installer

The `hvo-skymonitor` self-contained Linux CLI installs one local, standalone
VirtualSky CameraAgent without a repository checkout or a target-host .NET
runtime. Docker Engine and the Compose plugin are the only application-runtime
prerequisites. Catalog releases can be resolved from signed local metadata,
HTTPS mirrors, or signed release indexes; every source uses the same pinned
signature, length, checksum, archive, and internal catalog validation.

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
  --owner-email admin@home.lan \
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
  --owner-email admin@home.lan \
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
`--image-archive-sha256 <sha256>` for an offline image load. Use
`--password-file <owner-only-path>` for an operator-supplied temporary password;
otherwise the installer generates one. `--json` selects the versioned structured
result. `--dry-run` validates the catalog, Docker daemon, loaded image, generated
configuration, and rendered Compose model using private temporary storage without
creating the product root, loading/pulling an image, or starting a container.

Missing required values are prompted only on an interactive terminal. A strict
JSON config may be supplied with `--config`; value options cannot be mixed with
it. Flags such as `--dry-run`, `--json`, `--no-download`, and the explicit non-loopback HTTP
acknowledgement may still be applied.

## Signed Image Release

> `linux/arm64` installation is no longer refused (the `open(2)` flag defect,
> [#603](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/603), is fixed
> and the advisory arm64 workflow runs this CLI's Unit suite natively), but no
> arm64 installer campaign and no smoke of a published arm64 release image have
> run yet; treat an arm64 installation as unqualified end to end until #598 and
> #599 close.

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
SBOM, provenance, and vulnerability-scan asset names, and the exact compatibility
boundaries the release declared. An operator can correlate a running container
with its release without network access.

The record follows the image the instance actually runs. It is written only once
the image has been prepared and accepted, a refused install or upgrade leaves
none behind, and a rollback withdraws whatever the superseded upgrade recorded.
Its `manifestDigest` is the release's multi-architecture identity; for a release
published without a registry push that value is a computed index digest and is
not resolvable with `docker pull`.

An upgrade accepts the same options and writes the same evidence before the
operation begins:

```bash
hvo-skymonitor cameraagent upgrade \
  --instance-id <uuid> \
  --channel local \
  --image-manifest /media/hvo/image-v1.5.0/image-manifest.json \
  --no-download
```

`cameraagent preflight` accepts the same release selectors and resolves them the same way, so a planned upgrade
can be evaluated against persisted state first without acquiring, loading, or starting anything; see
[State Compatibility Boundary](#state-compatibility-boundary).

`--migration-backward-compatible` remains a separate operator assertion about the
candidate's state migration and is required only when the candidate declares one.
Do not add it to a routine upgrade; doing so defeats the gate it exists for.

Rollback continues to use the retained previous image identity and never
consults a release train.

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

Production installation always uses `/var/lib/hvo/skymonitor` and the canonical
layout in [product-instance-layout.md](product-instance-layout.md). The
`--product-root` override is rejected unless
`HVO_INSTALLER_ALLOW_TEST_ROOT=1` is deliberately set by an isolated test.

The installer records owner-only files beneath the UUID instance root:

```text
instance-manifest.json
application-identity.json
config/camera-module.json
config/compose/{compose.yml,instance.env}
config/secrets/*
config/owner-bootstrap/temporary-password
state/deployment/{installation-state.json,installation-result.json,state-preflight.json}
operations/cameraagent-<uuid>.owner-recovery.json
operations/state-reset-<operation-uuid>.evidence.json
operations/owner-recovery/<operation-uuid>/temporary-password
```

The manifest binds installation, instance, application, runtime UID/GID, exact
catalog, image, Docker daemon, Compose template/model, configuration, rig,
schedule, deployment-location snapshot, and installation-verification token
hash identities. Password and verification-token content is never written to
the manifest, result, phase state, Compose environment, logs, or terminal output.
Signed installs additionally retain the release train/version/tag, exact
manifest and asset length/hash, signing key ID, source and resolved public URI,
verification result/time, and provenance identity without authentication data.

The installer creates the product root through one narrow `sudo`-executed
internal preparation command when needed, then performs catalog, configuration,
Docker, Compose, and HTTP work as the invoking Docker-capable runtime user. Do
not invoke the whole installer through `sudo`.

## Bootstrap And Recovery

The first startup mounts the temporary password file only long enough for the
application to seed and authenticate the configured owner. The installer then
removes `LocalIdentity__AdminPasswordFile` authority, recreates the exact
Compose service, and requires the durable `owner-password-change-required`
state. The operator completes password replacement through CameraAgent.

If the owner loses the durable password after initial setup, recover access from
the CameraAgent host as the Docker-capable deployment runtime user, never as
root:

```bash
hvo-skymonitor cameraagent recover-owner --instance-id <uuid> --generate-password
```

Alternatively, provide an absolute owner-only mode `0400` or `0600` file with
`--password-file`. Password input is never accepted on the command line. The
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
compatibility promise:

```text
io.hvo.skymonitor.state-compatibility=cameraagent-state-v2
io.hvo.skymonitor.minimum-compatible-revision=70ecdd3a0d02a5288aaa6438e3a5cfc8e395545f
io.hvo.skymonitor.identity-migration=20260827053715_InitialIdentity
io.hvo.skymonitor.raw-ingress-schema=12
io.hvo.skymonitor.catalog-manifest-version=2
```

**CameraAgent state produced before `70ecdd3` is an incompatible source for a direct in-place upgrade.** Those
revisions wrote catalog manifest version 1, Identity migration `20251125021552_CreateLocalIdentity`, and
raw-ingress schema 11. The current image requires manifest version 2, migration
`20260827053715_InitialIdentity`, and schema 12, and there is no supported automatic migration between them.
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

A `--json` install or lifecycle invocation reserves standard error for its single error object, so the
rendered report is written there only in human form. Read the complete report from
`state/deployment/state-preflight.json`, or from `cameraagent preflight --json` on standard output; a
`--dry-run` deliberately retains nothing, and its error message still enumerates every blocking code.

Without a candidate the installed image's declaration is evaluated; naming one evaluates that candidate's
declaration and requires the current contract, matching an in-place upgrade. `--image-ref` names an image this
host already holds and is read from the local Docker image store. `--image-manifest`, `--image-index`, and
`--image-version` instead name a signed image release from the train above and are resolved exactly as an
upgrade resolves one: the same signature and index verification, the same rollback protection, the same
offline-archive identification, and the same platform selection, which follows the host operating system's
architecture. `--asset-base-url`, `--channel`, and `--no-download` apply to them as they do to an
upgrade and are rejected without `--image-manifest` or `--image-index`. A local-media manifest is read from disk
whether or not `--no-download` is given, so the flag matters only for an `https://` manifest or index. A signed release and `--image-ref` are mutually
exclusive here for the same reason install and upgrade refuse the combination.

A signed-release preflight is strictly read-only. It evaluates the release's own signed compatibility record,
so it neither acquires the offline archive nor contacts Docker at all and it reports on a release this host has
not received yet. It writes nothing into the instance, the release media, or the distribution download cache,
and it never advances the signed-index rollback state an acquisition would commit. A persisted database whose
journal still carries unreplayed recovery state is read through a private temporary copy so that replay never
touches the instance. Unlike install and upgrade the on-demand command retains no `state-preflight.json`. The
report names the resolved release tag beside the immutable image ID it selected; install and upgrade record the
same tag in the report they retain, which is the only release evidence a refused operation leaves behind.

**A compatible signed-release preflight is not a promise that the upgrade will proceed.** It compares persisted
state against the boundaries the release *declares*. Further gates run only during the upgrade itself: the
labels the loaded image actually carries are compared against the signed record; the candidate's component,
configuration, catalog, and replay-runner contract identities and its architecture are required to match this
instance; a candidate identical to the image already running is refused outright; and a candidate declaring a
state migration still requires `--migration-backward-compatible`. A release that declares a different
configuration or catalog contract, or that the instance already runs, therefore preflights clean and is still
refused at upgrade time.

Rollback state is retained per operating-system user
(`$XDG_STATE_HOME/hvo/skymonitor/distribution`, else `~/.local/state/...`), so run the preflight as the same
user that will run the upgrade for its rollback protection to consult the same floor.

The command exits `0` when compatible and `1` with error code `state-incompatible` otherwise. Each finding
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
  --image-ref <repository@sha256:digest> --migration-backward-compatible
hvo-skymonitor cameraagent rollback --instance-id <uuid>
hvo-skymonitor cameraagent uninstall --instance-id <uuid>
hvo-skymonitor cameraagent reinstall --instance-id <uuid>
```

`--migration-backward-compatible` is the operator's explicit acknowledgement that no transactional restore
path is being reserved. It is necessary but not sufficient: the candidate must also declare the current
`cameraagent-state-v2` contract, and the persisted state must satisfy every declared boundary described in
[State Compatibility Boundary](#state-compatibility-boundary). A rollback target installed before that label
correction is accepted as a known contract because no state migration occurs on the way back.

Each transition pins the Docker endpoint and daemon identity, validates the
rendered Compose model, records pre-mutation continuity, creates and validates a
consistent backup, and journals mutation intent before pause or stop. Candidate
Compose, manifest, and result identities commit while capture remains paused;
resume is the final idempotent action. If final acknowledgement is lost, rerun
the exact command with `--resume`. A failed candidate restores the exact prior
Compose, image, and identity records before capture resumes. Noncurrent manifests
and results are rejected before lifecycle state mutation; invalid candidate images
or rollback models are rejected before runtime mutation.

Uninstall removes only the selected Compose runtime and preserves config,
secrets, state, evidence, rollback identities, and shared catalogs. Purge is a
separate operation and requires `--confirm-instance-id <same-uuid>` after
uninstall. Purge quarantines the inode-authenticated instance tree and resumes
deletion from that tombstone after interruption; it never republishes a partially
deleted tree.

Catalog versions are explicit per instance:

```bash
hvo-skymonitor catalog install --catalog-bundle /owner-private/catalog.bundle
hvo-skymonitor catalog select --instance-id <uuid> --catalog-version <version>
hvo-skymonitor catalog rollback --instance-id <uuid>
hvo-skymonitor catalog gc --catalog-version <version>
```

Mutating garbage collection requires one explicit version. It fails closed on
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
# bind-source ownership and mode, uninstall, and the explicit reset, against a
# published archive rather than a locally built image. The authenticated owner
# recovery, upgrade, and rollback contract belongs to the baseline-revision run
# and is not exercised here.
HVO_PRODUCTION_CATALOG_BUNDLE=<bundle> \
HVO_INSTALLER_RELEASE_ARCHIVE=<candidate>/cameraagent-image-v<version>-linux-amd64.tar \
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

Run the x64 output with `--capabilities`; require protocol version `1` and
completed runtime/native-library warmup. On an ARM64 builder, the existing
`scripts/test:cameraagent-arm64-ci` gate performs the equivalent native publish,
capability, image, and constrained-container checks.

The campaign additionally proves the state preflight, the operator-confirmed CameraAgent-only reset, and the
reset-then-reinstall convergence against a real image and a real product root.

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
  --index https://github.com/RoySalisbury/HVO.SkyMonitor/releases/download/installer-index-3/installer-release-index.json \
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
