# Infrastructure Operations Runbook

This runbook covers persistent shared services on `hvo-docker.hvo.lan` and the
local application containers that consume them.

Repository infrastructure helpers require the Linux/devcontainer GNU toolchain
(`bash`, GNU `realpath`, `tar`, `find`, `sort`, `stat`, `flock`, `sha256sum`,
`cmp`, and Perl). Run them from the supported devcontainer on macOS or Windows
hosts.

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

Production SQL migration and runtime credentials are separate. Follow
[`logichost-database-initialization.md`](logichost-database-initialization.md)
for principal grants, reviewed SQL evidence, controlled execution, and recovery.

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

Before a coordinated platform smoke, generate and verify the owner-only smoke
contract. This does not mutate services:

```bash
./scripts/smoke:env init
./scripts/smoke:env validate
./scripts/smoke:env preflight
```

See [`docs/runbooks/smoke-test.md`](smoke-test.md) for the complete input,
generated-state, and reset-policy boundaries.

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

`./scripts/test:infra` validates command selection, locks, transaction phases,
archive rejection, file/mode round trips, and sentinel preservation with an
isolated temporary runtime-data root and mocked Docker/health commands. Its
shared-service checks are filesystem sentinels, not actual Docker-volume calls.
It does not touch repository `data/`, start containers, prove Docker volume
behavior, or replace the separate W2/W3 performance workloads.

## Redis Safety

The shared-service Compose topology disables Redis `default` and configures the
`skymonitor-app` ACL user for only `skymonitor:*` keys, with administrative and
dangerous command categories denied. Use that application identity for normal
operations, not an unrestricted server credential. Use cursor-based inspection
of only `skymonitor:*` keys. Confirm host, port, logical database, prefix, and
the reviewed key-name manifest before deletion.
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

The schema-v5 split-host workflow is separate from local `infra:*` ownership.
In isolated `services.mode: deploy`, it creates a run database with distinct
initializer/runtime SQL users, a prefix-scoped Redis ACL user, and a MinIO user
limited to the two run buckets. In `existing` mode it does not create or alter
service identities. See
[`split-host-preflight.md`](split-host-preflight.md) for the exact inventory and
controlled-start sequence.

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

Keep both applications stopped while restoring MinIO and SQL Server, with MinIO
available before SQL object references are validated. Then restore the local
application state as one unit. The application restore starts LogicHost and
waits for `/health` before it starts CameraAgent and waits for CameraAgent
`/health`. Run auth, durable-heartbeat, object-inventory, and checksum checks
afterward. See the [identity operations runbook](../identity/operations-runbook.md)
for the ordered procedure.

Use `./scripts/infra:backup-app-state BACKUP_DIRECTORY` and
`./scripts/infra:restore-app-state APPLICATION_STATE_ARCHIVE` for the mounted
application portion. New `.tgz` archives contain only the seven supported paths
listed above, never `data/catalog` or other runtime-root content. They include a
versioned file/directory inventory with modes, file sizes, and SHA-256 digests,
and retain the adjacent one-line `.sha256` transport-checksum format used by
legacy archives. Restore accepts those legacy archive/checksum pairs, validates
new inventories before installation and after extraction, preserves the target
catalog independently, and retains exact pre-restore state for rollback. For an
inventory-v1 archive, every regular file and every directory beneath a supported
root must have exactly one canonical inventory entry and every inventory entry
must exist after extraction with the recorded type and mode; implicitly created
nested directories are rejected. Legacy archives have no internal inventory and
therefore retain reduced verification: transport checksum, canonical header and
type safety, collision-safe extraction, and post-walk filesystem type checks,
without requiring old archives to list every extraction scaffold directory. A
failed application restore attempts collision-safe exact rollback and
intentionally leaves both applications stopped. If exact rollback cannot
complete, it preserves the durable transaction marker and rollback state for
operator recovery rather than claiming success. These scripts do not back up
or restore SQL Server or MinIO.

Ordinary start, rebuild, reset, backup, restore, and production-catalog
install/rollback share one nonblocking operation lock outside the runtime root.
The lock helper creates a missing runtime parent but never chmods an existing
parent. Before creating it, the nearest existing ancestor must satisfy the same
current-user ownership, nonsymlink, and no-group/world-write policy. An unsafe
ancestor is rejected without creating the missing parent or lock. The parent
must remain safe after creation; an existing safe mode such as `0751` is
preserved. Control files must be owned nonsymlink regular files. A restore
passes its already-held file descriptor and inode identity to its internal
LogicHost/CameraAgent starts; environment variables without that held descriptor
cannot bypass the lock. Direct start/reset and catalog mutation refuse a durable
restore marker and direct the operator through `infra:restore-app-state`
recovery.

Backup validates the completed private partial archive against its generated
inventory before publication. It syncs and publishes the checksum first, then
atomically renames the archive as the usable commit point and syncs the
destination directory. A handled publication failure removes the incomplete
checksum; an uncatchable process or machine failure can leave a checksum with no
archive, which restore treats as unusable and a later same-name backup treats as
a collision.

The seven supported sensitive state roots must be owned by the current user.
Ordinary start normalizes those known roots to `0700`; backup refuses roots that
are not already private. Restore validates archived metadata first, then its
internal starts normalize restored known roots to `0700`. This policy never
chmods the runtime parent. An existing runtime root and its `logichost` and
`cameraagent` parents must be current-user-owned, nonsymlink real directories
without group/world write permission before any operation validates,
normalizes, or traverses leaf state. Unsafe parent state is rejected rather than
repaired in place. The same ownership, nonsymlink, and no-group/world-write
policy applies to an existing backup destination before publication.

Restore copies the selected archive into private staging, verifies that copy,
and performs all header validation and extraction against it. It rejects path
aliases, duplicate canonical targets, absolute/traversal/control paths,
link/special entries, and tar extension/path-override records. The historical
single `./` prefix from repository legacy archives is canonicalized before
duplicate detection. Bounded GNU long-name records from historical GNU tar
archives are resolved and canonicalized before those checks, allowing supported
long paths without accepting GNU long-link, sparse, or PAX override records.
Staged filesystem swaps are individual same-filesystem
renames coordinated by a durable phase marker; they are not a transaction that
is atomic with SQL Server, MinIO, container startup, or health checks.

## Cross-Store Recovery Inventory

LogicHost persists a leased recovery checkpoint in SQL Server. After startup
and once every 24 hours it verifies canonical `Available` artifacts against
MinIO in bounded SQL and generated-key partitions. Missing objects return to
`Pending`; length or checksum conflicts become `Quarantined`; pending lineage
and rig references retry with capped recurring backoff. Unknown final objects
are never adopted from bytes alone. A durable disposition copies each orphan to
`quarantine/orphans/`, verifies the copy checksum, and only then removes the
source key. Ingest, derivative publication, retention, and recovery hold the
same hashed SQL application lock across each final object copy or deletion so a
new SQL owner cannot race orphan cleanup.

MinIO 7 does not expose a caller-supplied continuation token. Canonical keys are
therefore partitioned and bounded; the noncanonical catch-all is one linear,
cancellable namespace audit per recovery generation. Its operational cost is
proportional to objects in the two artifact prefixes and must be included in
site recovery capacity evidence. It does not run every 30 seconds.

Central consistency health is `Degraded` while the first inventory is
incomplete or durable findings await review and `Unhealthy` when progress is
stalled. Inspect events `2120`-`2129`, span `central-artifact.reconcile`, and
`skymonitor.central.recovery.*` metrics for scanned/matched/missing/corrupt/
orphan counts and bytes, cycle duration, retry, and backlog. These signals use
bounded outcomes only and never object keys or payload paths. Do not delete
quarantine objects or disposition rows until SQL/object counts, checksums,
lineage, jobs, and retention references have been reviewed.

Every recovery or error rollback must first stop both LogicHost and CameraAgent
successfully. If stop fails, no runtime or rollback tree is renamed or deleted;
the marker remains and the operator must stop both applications before rerunning
the same restore command. Marker rollback names are transaction-bound and a
mismatch requires operator review.

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
