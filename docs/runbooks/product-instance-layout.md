# Product and Instance Persistent Layout

SkyMonitor owns one production product root: `/var/lib/hvo-sky` by
default, or the root named by the root-owned host policy
`/etc/hvo-sky/host.json` (`{"productRoot": "/srv/data/hvo-skymonitor", "mountPoint": "/srv"}`),
which places it on another filesystem without a bind mount. Application
instances and catalogs are separate identity domains beneath that root:

```text
<product-root>/                       # /var/lib/hvo-sky unless the host policy names another
|-- operations/                       # product-wide runtime operation lock
|-- catalogs/<catalog-id>/{current,previous,versions/}
`-- cameraagents/<instance-uuid>/{config,state}   # config/compose holds the instance's Compose files
```

Persistent deployment rejects any product root other than the production one.
The host policy, its validation, and `hvo-sky-install host set-product-root` are
described in
[deployment-installer.md](deployment-installer.md#production-product-root-on-another-filesystem):
the file and every component of the root must be `root:root` and not writable by
group or other users, the root must exist, use only portable path characters,
contain no symbolic link, live on one mount, and resolve through the mount
currently at the recorded `mountPoint` path, and a root that already holds
instances is never moved. The `mountPoint` check confirms that a mount exists at
that path, not which filesystem it is, so verify the backing filesystem with
`findmnt -no SOURCE,UUID <mountPoint>` after storage changes. Each instance
keeps its Compose files inside its own `config/compose`, so configuration and
state stay one backup unit wherever the root lives. Isolated contract tests may
use a disposable root beneath their private `/tmp/hvo-deploy-test.*` directory.
The deployment never treats `/var/lib/hvo-sky`, a hostname, target name, friendly
name, container name, port, or application identity as an instance ID. The
self-contained local installer enforces this root and records its exact
manifest, phase, and result contracts as described in
[deployment-installer.md](deployment-installer.md).

## Instance Identity

The installer generates one UUID when it creates a CameraAgent instance and
stores its canonical lowercase representation as `instanceId`. The UUID is the
filesystem and Compose-project key. `friendlyName` is mutable display metadata;
renaming it does not move state, rename containers, or change the authentication
cookie. `applicationIdentity` binds the CameraAgent agent ID separately from the
instance ID. Its record and `instance-manifest.json` must agree with the
installed configuration. Release upgrades retain the instance identity.

Each CameraAgent has a UUID-derived cookie name, container name and Compose
project, with its own port, state and owner. Existing retired `logichosts`
entries can still block host-root changes or catalog garbage collection
because they may hold state or catalog references.

## Catalog Identity

`catalogId` is a stable logical identifier declared by the catalog specification,
required as `catalog.id` in manifest version 2,
for example `hyg-v44-production` (package lineage `hyg-v44-production-p4-s3`).
The embedded approved registry defines the catalog ID, package version prefix
and pinned content in
`src/HVO.SkyMonitor.Catalog.Sqlite/ApprovedCatalogSpecifications.json`.
The installer validates IDs against the approved specifications. Exact snapshots remain identified by
package version, schema/preprocessing versions, database SHA-256, byte length,
and row count. The source bundle, installed manifest, ledger and selected version must agree.

Several CameraAgent instances can use the same verified catalog installation.
The installer mounts the selected catalog read-only. Installed hosts pin both `Catalog:RequiredCatalogId` and
`Catalog:RequiredPackageVersion`. Each frame's scene provenance records the
catalog ID and package version, and changing a selection never rewrites recorded
frames ([HYG 4.4 catalog snapshot](../catalog/hyg-v44.md)).

## Backup Unit

Back up an instance's `instance-manifest.json`, `config/`, and `state/` together.
For CameraAgent this includes Identity, the operator settings file
(`state/identity/appsettings.local.json`), Data Protection keys, provisioning
secrets, protected deployment-location history, schedule/profile revisions, raw
ingress and capture sequence, frames, processing/outbox state, calibration, and
local secrets. Restoring only part of that set can invalidate protected data,
rebind an owner account, or regress durable sequence state. The installed
lifecycle defines which layouts support backup or restore. [Installer backup and restore](deployment-installer.md#backup-and-restore).

Catalogs are immutable deployment artifacts, not part of an instance backup.
Record the selected catalog ID, exact package version and database SHA-256, then
reinstall or restore that verified catalog independently. Also restore every
historically pinned package that recorded frames name, so they keep resolving to
their recorded identity. Lifecycle operations must use
the exact UUID instance root and ownership manifest; they must not enumerate or
delete sibling roots. The deployment does not accept or migrate the former
singular product layout.

## Upgrade Recovery Boundary (Approved Target)

The [SkyMonitor recovery contract](../hvo-install-contract.md#skymonitor-approved-target-upgrade-time-checkpoint-recovery)
in #1606 is approved target behavior, not a new snapshot backend or a change to
the host policy enforced today. The single-mount rule and separate instance and
catalog binds above remain in force; #1292 owns host-policy implementation.
Do not repartition, format, create subvolumes, relocate SQLite or move instance
roots to satisfy this documentation change. #1607 requires a clear net benefit before recommending a filesystem or layout change.

An upgrade checkpoint must cover the entire instance's mutable DB/files/config,
identity, key ring and referenced payloads at one fenced point, including any
configured external storage roots. The explicit backup unit above is a coverage
inventory, not an instruction to create a full archive on every upgrade. SQLite
backup copies remain a valid candidate if referenced immutable files are pinned
and every mutable part is recovered coherently.

Keep operation journal, protected/unprotected status, checkpoint/image pairing,
restore progress, final receipt and minimal non-rewindable fact capsule outside
all reverted trees (an explicitly excluded product `operations/` location may
serve this role). A product-wide filesystem revert must not roll them or sibling
instances back. Qualify single-instance recovery while a sibling continues
working. Pin verified immutable catalog packages for each instance, including
historical references, without rewinding a shared catalog selector or deleting
another instance's package. Retained checkpoint bytes after commit confer no
later rollback authority.
