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
state/deployment/{installation-state.json,installation-result.json}
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
