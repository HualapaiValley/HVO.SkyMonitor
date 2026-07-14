# Infrastructure Operations Runbook

This runbook covers persistent shared services on `hvo-docker.hvo.lan` and the
local application containers that consume them.

Repository infrastructure helpers require the Linux/devcontainer GNU toolchain
(`bash`, GNU `realpath`, `tar`, `sha256sum`, and `stat`). Run them from the
supported devcontainer on macOS or Windows hosts.

## Service Layout and Ownership

`deploy/hvo-docker/docker-compose.shared-services.yml` defines Redis, MinIO, and
Mailpit. SQL Server is provisioned separately. `docker-compose.apps.yml` defines
only LogicHost and CameraAgent.

| Resource | Repository ownership |
| --- | --- |
| SQL Server | LogicHost migrates and seeds only database `SkyMonitor`. Do not point it at a database owned by another repository. |
| Redis | SkyMonitor cache keys use physical prefix `skymonitor:`. Redis is not authoritative identity or job state. |
| MinIO | SkyMonitor uses only `skymonitor-diagnostics` and `skymonitor-artifacts`. |
| Mailpit | Development email capture; non-authoritative and disposable. |
| LogicHost | Central application and file-backed Data Protection key ring. |
| CameraAgent | Local Identity, Data Protection, provisioning state, capture state, and local application data. |

Application Compose uses the `skymonitor-network` bridge and connects to shared
services through the endpoints configured in `.env`. It is a Development,
HTTP-only topology, not a production TLS deployment.

## Persistent Application State

Default bind mounts are rooted at `./data`. `HVO_RUNTIME_DATA_ROOT` can override
that root for isolated command testing.

| Host | Host path | Container path |
| --- | --- | --- |
| LogicHost | `data/logichost/dataprotection` | `/app/DataProtection-Keys` |
| LogicHost | `data/logichost/home` | `/home/app` |
| Both hosts | `data/catalog` | `/app/catalog` (read-only) |
| CameraAgent | `data/cameraagent/identity` | `/app/App_Data/identity` |
| CameraAgent | `data/cameraagent/dataprotection` | `/app/DataProtection-Keys` |
| CameraAgent | `data/cameraagent/provisioning` | `/app/data/provisioning` |
| CameraAgent | `data/agent` | `/workspaces/HVO.SkyMonitor/data/agent` |
| CameraAgent | `data/archive` | `/workspaces/HVO.SkyMonitor/data/archive` |

A rebuild or ordinary container recreation preserves these mounts. An explicit
reset deletes the selected host's listed state. CameraAgent provisioning and its
Data Protection key ring are one recovery unit. The verified catalog root is
preserved by application reset and can be reinstalled independently.

## Common Operations

Install the verified production catalog before starting either application. The
build and offline installation procedure is in
[`docs/catalog/production-install.md`](../catalog/production-install.md).

Start both applications or a selected application:

```bash
./scripts/infra:start
./scripts/infra:start logichost
./scripts/infra:start cameraagent
```

Rebuild an image while preserving mounted state:

```bash
./scripts/infra:start --rebuild logichost
```

Inspect status and logs:

```bash
./scripts/infra:status
./scripts/infra:logs logichost
./scripts/infra:logs cameraagent
```

Stop applications without clearing state:

```bash
./scripts/infra:stop logichost cameraagent
```

Run current health and authentication-boundary probes:

```bash
curl --fail http://localhost:${LOGIC_HOST_HTTP_PORT:-5174}/health
curl --fail http://localhost:${CAMERA_AGENT_HTTP_PORT:-5130}/health
./scripts/identity:smoke
```

After installation or an image rebuild, verify the mounted active catalog from
both containers:

```bash
docker exec skymonitor-logichost sha256sum /app/catalog/current/hyg_v42.sqlite
docker exec skymonitor-cameraagent sha256sum /app/catalog/current/hyg_v42.sqlite
```

The expected digest is
`b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2`.
The fixture digest is never valid for a production host.

## Reset

Reset is destructive and requires an approved backup and rollback decision:

```bash
./scripts/infra:start --reset logichost
./scripts/infra:start --reset cameraagent
./scripts/infra:reset logichost cameraagent
```

- LogicHost reset removes its container, Data Protection key ring, and
  persisted Development certificate store. It does not delete SQL Server,
  Redis, MinIO, or Mailpit data.
- CameraAgent reset removes its container, local Identity database, Data
  Protection keys, provisioning files, packaged sample payloads, and outbox
  state.
- `infra:start --reset` rebuilds and starts the selected services after reset.
- `infra:reset` removes selected containers and state but does not restart them.
- Neither reset command deletes `data/catalog`; activation and rollback remain
  separately controlled catalog operations.

`./scripts/test:infra` validates command-selection and cleanup behavior with an
isolated temporary runtime-data root. It does not touch repository `data/` or
start Docker containers.

## Redis Safety

Use cursor-based inspection of only `skymonitor:*` keys. Confirm host, port,
logical database, prefix, and the reviewed key-name manifest before deletion.
Delete approved keys individually with `UNLINK`. Never clear an entire logical
database or server. Redis deletion does not revoke cookies, OAuth tokens, API
keys, or device credentials.

## MinIO Safety

Applications use `MINIO_ACCESS_KEY` and `MINIO_SECRET_KEY` with the policy in
`deploy/hvo-docker/minio/skymonitor-policy.json`. Root credentials are reserved
for service administration and `./scripts/infra:provision-minio-account`.

The provisioning script creates a missing service account but does not change
the secret of an existing account. Follow the MinIO operator's approved
rotation procedure, reapply and inspect the scoped policy, then validate both
approved buckets before restarting applications.

## Backup and Restore Boundary

A complete recovery set contains:

- encrypted SQL Server backup of `SkyMonitor`;
- both approved MinIO buckets with keys, metadata, and checksums;
- LogicHost Data Protection files;
- LogicHost Development certificate-store home for the supported Compose
  topology;
- CameraAgent Identity, Data Protection, and provisioning directories together.
- CameraAgent sample payload, sidecar, index, archive, and outbox state under
  `data/agent` and `data/archive`.

Redis cache and Mailpit messages are not authoritative recovery inputs. SQL
Server and MinIO are operator-managed services, so their exact backup location,
encryption, retention, restore command, and verification belong to the site's
service runbook. Do not use a command for another database engine or copy SQL
metadata without its corresponding MinIO objects.

Restore Data Protection keys before protected envelopes/secrets, restore MinIO
before validating SQL object references, start LogicHost before CameraAgent,
then run health, auth, heartbeat, object-inventory, and checksum checks. See the
[identity operations runbook](../identity/operations-runbook.md) for the
ordered procedure.

Use `./scripts/infra:backup-app-state BACKUP_DIRECTORY` and
`./scripts/infra:restore-app-state APPLICATION_STATE_ARCHIVE` for the mounted
application portion. These scripts create/verify a SHA-256 manifest and retain
pre-restore state for rollback; they do not back up SQL Server or MinIO.

## Log and Evidence Collection

Application logs can contain user, client, key, device, route, and correlation
identifiers. Collect only the minimum UTC interval, restrict access, and review
for personal data. Never retain environment dumps, request/response bodies,
authorization headers, tokens, keys, envelopes, passwords, or connection
strings.

## Change Management

1. Update the owning Compose or application configuration.
2. Reflect consumed root variables in `.env.template` and nested keys in the
   secrets guide.
3. Update persistent mounts, reset behavior, backup/recovery ownership, and
   smoke checks together.
4. Run `./scripts/docs:audit-operations`, `./scripts/test:infra`, and the
   canonical CI gates.

## Related Documentation

- [Identity operations](../identity/operations-runbook.md)
- [Secrets and configuration](../security/secrets.md)
- [Local development](local-dev.md)
- [CI pipeline](ci-pipeline.md)
