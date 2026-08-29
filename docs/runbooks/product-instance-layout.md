# Product and Instance Persistent Layout

SkyMonitor owns `/var/lib/hvo/skymonitor`. Application instances and catalogs
are separate identity domains beneath that root:

```text
/var/lib/hvo/skymonitor/
|-- operations/                       # product-wide runtime operation lock
|-- catalogs/<catalog-id>/{current,previous,versions/}
|-- cameraagents/<instance-uuid>/{config,state}
`-- logichosts/<instance-uuid>/{config,state}
```

Persistent deployment rejects any other product root. Isolated contract tests
may use a disposable root beneath their private `/tmp/hvo-deploy-test.*`
directory. The deployment never treats `/var/lib/hvo`, a hostname, target name,
friendly name, container name, port, or application identity as an instance ID.
The self-contained local installer enforces this root and records its exact
manifest, phase, and result contracts as described in
[deployment-installer.md](deployment-installer.md).

## Instance Identity

Generate one UUID when a CameraAgent or LogicHost instance is first created and
store its canonical lowercase representation as `instanceId`. The UUID is the
filesystem and Compose-project key. `friendlyName` is mutable display metadata;
renaming it does not move state, rename containers, or change the authentication
cookie. `applicationIdentity` explicitly binds the separate current CameraAgent
agent ID or LogicHost identity. CameraAgent startup rejects an inventory binding
that differs from the selected module document's `agentId`.

`up` creates `instance-manifest.json` and its referenced
`application-identity.json` binding at the instance root with mode `0600`. The
manifest binds schema, product, component kind, UUID, creating installation, and
the current independent application identity through that binding. Bootstrap
atomically advances only the binding from the declared pre-provisioning agent ID
to the resulting device ID. An existing manifest must have the exact immutable
identity fields and its creating installation; an identity binding explicitly
records `pre-provisioning` with the configured ID or `bound` with both the
configured and provisioned device IDs. A later `up` validates that binding before
staging, requires the preserved provisioning gate to be `false` and upload to be
`true`, and records those remotely read values in phase evidence. Missing,
malformed, or contradictory bound state fails before staging or Compose
mutation. The deployment preserves bound provisioning configuration. The
binding must match its declared/restored lifecycle state; UUID reuse, component
reuse, or unproved identity rebinding fails closed. Release revision is not
creation identity and therefore does not change this manifest during upgrades.

Each CameraAgent receives a UUID-derived cookie name, container name, Compose
project, unique owner-password reference, host port, and explicitly bound agent
identity. Inventory validation rejects duplicate UUIDs, application identities,
cookie names, mutable roots on the same host, ports on the same host, unsafe
ancestry, and unknown catalog references.

Multiple application instances may share one correlated SSH host and Docker
daemon. Their aliases, UUID roots, ports, Compose projects, container names,
cookies, application identities, and owner-password references remain distinct.

## Catalog Identity

`catalogId` is a stable logical identifier declared by the catalog specification,
required as `catalog.id` in manifest version 2, and repeated exactly in inventory,
for example `hyg-v42-production`. It is not a random installation UUID and is
never inferred from a package filename or display name. The HYG v4.2 production
specification assigns `hyg-v42-production`; exact snapshots remain identified by
package version, schema/preprocessing versions, database SHA-256, byte length,
and row count. Inventory declares `schemaVersion` and `preprocessingVersion`;
source bundle, installed manifest, ledger, and active `current` selection must
match exactly.

Every application target selects one `catalogId`. Multiple targets may select
the same installation root and mount it read-only at `/app/catalog`; another
target may select a different ID and independent `current`/`previous` pointers.
Inventory rejects duplicate IDs, duplicate catalog roots, roots outside
`<productRoot>/catalogs/<catalogId>`, and catalog/application ancestry overlap.
The catalog phase records target, catalog ID, package version, and checksum.

## Backup Unit

Back up an instance's `instance-manifest.json`, `config/`, and `state/` together.
For CameraAgent this includes Identity, Data Protection keys, provisioning
secrets, protected deployment-location history, schedule/profile revisions, raw
ingress and capture sequence, frames, processing/outbox state, calibration, and
local secrets. Restoring only part of that set can invalidate protected data,
rebind an owner account, or regress durable sequence state. LogicHost backup
likewise keeps its configuration, certificates/secrets, Data Protection keys,
and host-local state together; SQL Server, Redis, and object storage follow their
own application-consistent backup procedures.

Catalogs are immutable deployment artifacts, not part of an instance backup.
Record the selected catalog ID and exact active snapshot identity, then reinstall
or restore that verified catalog independently. Lifecycle operations must use
the exact UUID instance root and ownership manifest; they must not enumerate or
delete sibling roots. The deployment does not accept or migrate the former
singular product layout.
