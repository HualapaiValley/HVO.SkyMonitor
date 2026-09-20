# Infrastructure Operations Runbook

This runbook covers persistent shared services on `hvo-docker.hvo.lan` and the
local application containers that consume them.

Repository infrastructure helpers require the Linux GNU toolchain
(`bash`, GNU `realpath`, `tar`, `find`, `sort`, `stat`, `flock`, `sha256sum`,
`cmp`, and Perl). Run them natively on Linux or WSL2. On macOS or Windows, the
optional devcontainer provides a compatible environment. Keep Windows checkouts
on the WSL2 Linux filesystem so repository scripts receive normal Unix
filesystem semantics.

## Service Layout and Ownership

`deploy/hvo-docker/docker-compose.shared-services.yml` defines Redis and
Mailpit. SQL Server is provisioned separately. `docker-compose.apps.yml` defines
only LogicHost and CameraAgent. Object storage is not a shared service: it is a
dedicated filesystem root on the LogicHost host.

| Resource | Repository ownership |
| --- | --- |
| SQL Server | LogicHost migrates and seeds only database `SkyMonitor`. Do not point it at a database owned by another repository. |
| Redis | SkyMonitor cache keys use physical prefix `skymonitor:`. Redis is not authoritative identity or job state. |
| Object store | A dedicated same-host ext4 root owned by the LogicHost runtime user, containing only `skymonitor-artifacts` and `skymonitor-diagnostics`. |
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
| LogicHost, filesystem object storage | Dedicated same-host ext4 mount, site-defined host path | `/var/lib/hvo/object-store` |
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

The qualified filesystem object-store root is separate from `data/` and every
other application-state mount. Provision it before container startup with owner
`4242:4343`, mode `0750`, and precreated `skymonitor-artifacts` and
`skymonitor-diagnostics` directories with the same owner and mode. One trusted
LogicHost writer is supported. NFS, SMB, NAS, XFS/ZFS object roots, clustered
filesystems, arbitrary volume drivers, nested bucket mounts, bind aliases, and
multiple writers are not qualified.

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
  Redis, Mailpit, or object-store data.
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

## Object-Store Safety

The object store has no network endpoint, service account, or access policy.
Its entire authorization boundary is filesystem ownership and mode: the root
and both bucket directories are owned by the LogicHost runtime user
(`4242:4343`) with mode `0750`, and LogicHost runs with a read-only container
root. There is nothing to rotate, but the ownership and mode must be reasserted
after any operator action that could change them, and
`./scripts/qualify:filesystem-object-store` must pass before applications are
restarted.

LogicHost holds an exclusive lock on the root for its lifetime, so a second
writer cannot start against the same store. Never let another process, user, or
host write into the root; external mutation is unqualified and is detected as
corruption rather than repaired.

The split-host workflow is separate from local `infra:*` ownership.
In isolated `services.mode: deploy`, it creates a run database with distinct
initializer/runtime SQL users and a prefix-scoped Redis ACL user, and it
qualifies the declared object-store root before the hosts start. In `existing`
mode it does not create or alter service identities. See
[`split-host-preflight.md`](split-host-preflight.md) for the exact inventory and
controlled-start sequence.

## Backup and Restore Boundary

A complete recovery set contains:

- encrypted SQL Server backup of `SkyMonitor`;
- one completed LogicHost filesystem-object-store backup;
- LogicHost Data Protection files;
- LogicHost Development certificate-store home for the supported Compose
  topology;
- CameraAgent Identity, Data Protection, and provisioning directories together.
- CameraAgent sample payload, sidecar, index, archive, and outbox state under
  `data/agent` and `data/archive`.

Redis cache and Mailpit messages are not authoritative recovery inputs. SQL
Server and S3 storage are operator-managed services, so their exact backup location,
encryption, retention, restore command, and verification belong to the site's
service runbook. Do not use a command for another database engine or copy SQL
metadata without its matching object-store generation.

Keep both applications stopped while restoring object storage and SQL Server,
with the selected object provider available before SQL object references are
validated. Then restore the local
application state as one unit. The application restore starts LogicHost and
waits for `/health` before it starts CameraAgent and waits for CameraAgent
`/health`. Run auth, durable-heartbeat, object-inventory, and checksum checks
afterward. See the [identity operations runbook](../identity/operations-runbook.md)
for the ordered procedure.

Use `./scripts/infra:backup-app-state BACKUP_DIRECTORY` and
`./scripts/infra:restore-app-state APPLICATION_STATE_ARCHIVE` for the mounted
application portion. New `.tgz` archives contain only the seven supported paths
listed above, never `data/catalog` or other runtime-root content. They include a
versioned file/directory inventory with product and component identity, UID/GID
ownership, modes, file sizes, and SHA-256 digests, plus an adjacent relocatable
one-line `.sha256` transport checksum. Restore requires inventory-v1, validates
its exact one-to-one mapping before installation and after extraction, preserves
the target catalog independently, and retains exact pre-restore state for
rollback. Every regular file and directory beneath a supported root must have
exactly one canonical inventory entry and every inventory entry must exist after
extraction with the recorded component, ownership, type, and mode. Implicitly
created nested directories and archives without the inventory are rejected. A
failed application restore attempts collision-safe exact rollback and
intentionally leaves both applications stopped. If exact rollback cannot
complete, it preserves the durable transaction marker and rollback state for
operator recovery rather than claiming success. These scripts do not back up
or restore SQL Server or the object store.

### Filesystem object-store backup and restore

When `ObjectStorage:Provider` is `Filesystem`, the object-store portion of the
recovery set is produced and consumed by LogicHost itself, offline, with the
same configuration the runtime uses so the root and bucket names cannot drift:

```bash
dotnet HVO.SkyMonitor.LogicHost.dll --host-mode=object-store-backup  --path=/var/backups/skymonitor/objects/<stamp>
dotnet HVO.SkyMonitor.LogicHost.dll --host-mode=object-store-verify  --path=/var/backups/skymonitor/objects/<stamp>
dotnet HVO.SkyMonitor.LogicHost.dll --host-mode=object-store-restore --path=/var/backups/skymonitor/objects/<stamp>
```

Stop LogicHost first and prove the container/process is absent. Backup, verify,
and restore all acquire the same exclusive root lock as the runtime and fail if
LogicHost or another maintenance operation owns it. Do not bypass or delete the
lock file. All three modes start no listener, open no database
connection, and exit: `0` success, `1` the operation failed or verification
found a mismatch, `2` the mode does not apply (the provider is S3, the root is
invalid, or the path is inside the root). With the S3 provider the object
store's backup belongs to the storage service's own runbook, exactly as before.

A backup is the set of live objects, copied in the store's own layout, with
every data file hashed as it is copied and refused on any mismatch against its
descriptor, plus `inventory.json` (schema `hvo-fs-object-backup-v1`: bucket,
exact logical key, content type, length, generation, SHA-256 and modified time
for every object, sorted by bucket then key) and its `inventory.json.sha256`.
Retired generations, in-flight temporaries, retirement stamps and quarantine
are not objects and are not backed up. The target must be an empty or absent
directory; a backup never merges. A store that fails its own digest check or
has a malformed descriptor is refused: reconcile it (the host does this on
start and every ten minutes; the health check reports `QuarantinedBuckets`)
before taking the backup, so a backup is never a copy of a known-bad store. The
completed copied tree is re-read and matched to the in-memory inventory before
`inventory.json.sha256` is published. Absence of that checksum means the backup
is incomplete and must not be restored. Store backups on a separate durable
filesystem with enough free bytes and inodes; retain command output, inventory,
checksum, source revision, image digest, mount identity, and UTC time.

Restore is destructive by contract and staged: every configured bucket is
rebuilt in full under `<bucket>.restoring`, with each data file re-hashed
against the inventory, and only after every bucket has staged completely is
each swapped into place through `<bucket>.replaced`. Before the first swap it
publishes a durable whole-store marker in phase `prepared`; after exact
verification it advances that marker to `committed`, removes rollback trees,
and finally removes the marker. A damaged backup is
therefore discovered before any existing bucket is touched.
Restore refuses a backup that lacks a configured bucket rather than leaving it
empty and refuses a bucket, staging or replaced directory that is a link. If an
interrupted restore is recovered as one transaction on the next restore attempt:
`prepared` rolls every configured bucket back before retry, while `committed`
keeps every restored bucket and finishes cleanup. Ordinary runtime startup stays
fenced while a marker exists. Do not manually remove the marker, `.restoring`,
or `.replaced` trees. If ownership, mode, a link, or contradictory state prevents
automatic recovery, preserve the marker and rollback trees as evidence, repair
only the reported host ownership/mount fault, and retry the same restore command.
After the swap the mode runs verify and exits non-zero on any mismatch. Restore
reproduces exact keys, metadata, lengths, generations and
digests, so SQL rows that reference `object://bucket/key` resolve unchanged.

### Filesystem object-store topology preflight

Before first start, after a mount/configuration change, and after every reboot,
run the preflight against the exact host path while no LogicHost process or
container is running:

```bash
set -o pipefail
./scripts/qualify:filesystem-object-store \
  /srv/skymonitor/object-store \
  4242 4343 \
  00000000-0000-0000-0000-000000000000 \
  2147483648 10000 \
  skymonitor-artifacts skymonitor-diagnostics \
  /srv/skymonitor/data/logichost/dataprotection \
  /srv/skymonitor/data/logichost/home \
  | tee filesystem-object-store-preflight.json \
  || { rm -f filesystem-object-store-preflight.json; exit 1; }
sha256sum filesystem-object-store-preflight.json > filesystem-object-store-preflight.json.sha256
```

Replace the example UUID with the provisioned ext4 filesystem UUID from the
site's storage inventory. Use site-approved thresholds at least as strict as the deployment configuration.
The result must name native Linux amd64 or arm64, an `rw` ext4 mount backed by an
expected `/dev` source/UUID, the exact mountpoint, owner `4242:4343`, mode `0750`,
both canonical buckets, and sufficient free bytes/inodes. Treat a changed source,
UUID, filesystem type, mountpoint, nested mount, bind alias, owner, mode, or
capacity result as a failed start. A bare directory exposed because the intended
mount is absent is never a recovery target.

Render and retain the effective Compose model and exact image digest before
startup. The container root remains read-only and only declared state plus
`/var/lib/hvo/object-store` are writable. Start one LogicHost, confirm `/alive`
and object-store health, then prove a second replica cannot acquire the root.
After recovery, verify zero inventory mismatches and reconcile SQL object
references before starting CameraAgent.

Investigate filesystem object-store health as follows: stop writes for a
read-only mount, permission loss, inaccessible bucket, exhausted bytes/inodes,
or stale/failed reconciliation; restore the qualified mount/ownership/capacity;
rerun preflight; restart the single writer; then require current reconciliation,
zero persistent quarantine, and zero backup verification mismatches. Never
delete quarantine or provider-owned paths to make health green.

Take encrypted off-host copies on the site's recovery-point schedule and retain
multiple generations. Verify every completed backup after transfer and perform a
periodic destructive restore drill into an isolated qualified root. A drill is
complete only after exact inventory verification and retained command, checksum,
revision, image, topology, and timing evidence.

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
aliases including a leading `./`, duplicate canonical targets,
absolute/traversal/control paths, link/special entries, and tar
extension/path-override records. Bounded GNU long-name records are resolved
before those checks, allowing supported
long paths without accepting GNU long-link, sparse, or PAX override records.
Staged filesystem swaps are individual same-filesystem
renames coordinated by a durable phase marker; they are not a transaction that
is atomic with SQL Server, the object store, container startup, or health checks.
Recovery marker version 2 journals displacement, rollback restoration,
displaced-tree removal, authenticated staging removal, and marker removal.
Every destructive substep records intent before mutation and accepts either the
pre-mutation or completed filesystem state on retry. Private staging uses a
32-hex transaction name plus an owned regular transaction control file. While
holding the application-state operation lock, restore removes only staging with
an exact matching control; malformed, symlinked, foreign-owned, or writable
staging is preserved for operator review. Version-1 forward markers remain
recoverable, but their historical random staging names are not inferred or
deleted.

## Cross-Store Recovery Inventory

LogicHost persists a leased recovery checkpoint in SQL Server. After startup
and once every 24 hours it verifies canonical `Available` artifacts against
object storage in bounded SQL and generated-key partitions. Missing objects return to
`Pending`; length or checksum conflicts become `Quarantined`; pending lineage
and rig references retry with capped recurring backoff. Unknown final objects
are never adopted from bytes alone. A durable disposition copies each orphan to
`quarantine/orphans/`, verifies the copy checksum, and only then removes the
source key. Ingest, derivative publication, retention, and recovery hold the
same hashed SQL application lock across each final object copy or deletion so a
new SQL owner cannot race orphan cleanup.

The provider-neutral adapter returns every page of each prefix listing. Canonical
keys remain partitioned and bounded; the noncanonical catch-all is one linear,
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
