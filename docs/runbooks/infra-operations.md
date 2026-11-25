# CameraAgent development infrastructure

Use the [installer runbook](deployment-installer.md) and
[Silo runbook](cameraagent-silo-runtime.md) for installed instances. This
runbook covers the standalone development container in `docker-compose.apps.yml`.

## Start, rebuild and stop

Prepare `.env` and the approved catalog using the [local development
runbook](local-dev.md), then use the repository wrappers.

```bash
./scripts/infra:start
./scripts/infra:status
./scripts/infra:start --rebuild cameraagent
./scripts/infra:stop
```

The development container publishes the HTTP port configured by
`CAMERA_AGENT_HTTP_PORT`, defaulting to 5130. For health checks, poll
`/alive` and `/health` on that endpoint.

## Local state and reset

Under `HVO_RUNTIME_DATA_ROOT` (default `./data`), the development container
binds `cameraagent/identity`, `cameraagent/dataprotection`,
`cameraagent/provisioning`, `agent`, and `archive`. Keep these directories
private. The catalog is mounted read-only from `catalog`.

`./scripts/infra:reset cameraagent` removes the development container and
clears those five state directories. `./scripts/infra:start --reset cameraagent`
also rebuilds the image and starts the container. The catalog and other
entries remain in place. Back up any development state you need before reset.

The start, reset and development catalog helpers share the runtime-parent
operation lock. If a restore marker from the former paired application
workflow is present, use the revision that created it to complete recovery
before using these helpers. For standalone instance backups and restores, use
the installer's [backup and restore commands](deployment-installer.md).

## Validate the wrappers

```bash
./scripts/test:infra
./scripts/docs:audit-operations
```

The infrastructure test uses a disposable directory and a fake Docker
executable. The independent [shared Redis and Mailpit stack](../../deploy/hvo-docker/README.md)
has its own operating procedure.
