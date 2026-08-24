# CameraAgent Deployment Installer

The `hvo-skymonitor` self-contained Linux CLI installs one local, standalone
VirtualSky CameraAgent without a repository checkout or a target-host .NET
runtime. Docker Engine and the Compose plugin are the only application-runtime
prerequisites. The first release consumes operator-supplied local assets;
signed online resolution and GitHub Release publication are owned by issue
[#417](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/417).

## Inputs

Supply an immutable CameraAgent repository digest or an already loaded image ID,
plus the verified production catalog bundle. An offline image archive additionally
requires its independently trusted lowercase SHA-256. Never use a mutable image
tag or pass a password on the command line.

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

Use `--image-archive /srv/hvo/cameraagent.tar` together with
`--image-archive-sha256 <sha256>` for an offline image load. Use
`--password-file <owner-only-path>` for an operator-supplied temporary password;
otherwise the installer generates one. `--json` selects the versioned structured
result. `--dry-run` validates the catalog, Docker daemon, loaded image, generated
configuration, and rendered Compose model using private temporary storage without
creating the product root, loading/pulling an image, or starting a container.

Missing required values are prompted only on an interactive terminal. A strict
JSON config may be supplied with `--config`; value options cannot be mixed with
it. Flags such as `--dry-run`, `--json`, and the explicit non-loopback HTTP
acknowledgement may still be applied.

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

Upgrade, rollback, uninstall, purge, backup/restore, LogicHost installation,
remote orchestration, physical-camera discovery, and online asset acquisition
are not implemented by this first slice.

## Build Evidence

Build the two supported self-contained binaries with:

```bash
dotnet publish src/HVO.SkyMonitor.Deployment.Cli/HVO.SkyMonitor.Deployment.Cli.csproj \
  --configuration Release --runtime linux-x64
dotnet publish src/HVO.SkyMonitor.Deployment.Cli/HVO.SkyMonitor.Deployment.Cli.csproj \
  --configuration Release --runtime linux-arm64
```

Architecture & Publish CI retains SHA-256 manifests for both RIDs. Signing,
release archives, SBOMs, indexes, and public-path verification remain #417.
