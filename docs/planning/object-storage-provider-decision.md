# LogicHost Object-Storage Provider Decision

Status date: 2026-09-05

Decision owner: `RM-016`, epic [#499](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/499)

## Decision

The first supported LogicHost release will use a product-owned durable local
filesystem provider behind the existing `IObjectStore` application boundary.
The canonical topology is one LogicHost replica using a same-host Linux ext4
bind mount. It is an object-store provider inside LogicHost, not an S3 server or
a test fake.

The AWS SDK S3 adapter remains available for development and future remote
profiles. A native Azure Blob adapter can implement the same application
contract later. Remote filesystem, S3, and Azure qualification do not block
`RM-016`; that vNext work belongs to `RM-019`, epic
[#588](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/588).
The first supported release profile is filesystem-only: installer and production
preflight reject S3 selection until #589 qualifies and enables an exact profile.

## Constraints

- Local development and first-release installation must work offline, without a
  license service or cloud account.
- HVO must be able to distribute every required first-release component.
- Object payloads can live on the LogicHost Docker host; larger future
  installations may need a separately qualified service on the local network.
- Central workflows retain logical bucket/key identity, streaming, application
  SHA-256 and byte-length authority, opaque generations, conditional reads,
  copy, delete, listing, normalized failures, recovery, and observability.
- CameraAgent remains independently correct and releasable without LogicHost or
  central object storage.
- Shared CI-classifier, architecture-test, and combined Testcontainers fixture
  changes are coordinated with active `RM-017` ownership and must rerun affected
  CameraAgent and combined regression evidence.

## Options Considered

This comparison is a 2026-09-05 snapshot: MinIO AIStor Free under the agreement
published on that date; PGSTY Silo `RELEASE.2026-08-06T00-00-00Z`;
VersityGW `v1.8.0`; SeaweedFS `4.45`; Garage `2.3.0`; Ceph `20.2.4`;
RustFS `1.0.0-rc.5`; and Scality CloudServer `9.4.3`. A future qualification
must select and re-audit a then-current exact artifact rather than relying on
this comparison.

| Option | Strengths | Material concern | Disposition |
| --- | --- | --- | --- |
| HVO filesystem provider | No extra service, license, credential plane, or network hop; smallest offline topology; behavior can exactly match the application contract | HVO owns crash consistency, key mapping, concurrency, retired-generation reclamation, reconciliation, backup, and filesystem qualification | **Selected for the first release**, subject to #584, #592, #585, #586, and adoption in #506 |
| [Archived MinIO OSS](https://github.com/minio/minio) | Existing deployment and evidence | Upstream is archived; retained release `RELEASE.2025-09-07T16-13-09Z` is affected by [GHSA-jjjj-jwhf-8rgr](https://github.com/advisories/GHSA-jjjj-jwhf-8rgr), as recorded in #494 | Retain only until #506; not a supported release default |
| MinIO AIStor Free | Maintained single-node product familiar to current operations | Proprietary BYOL; the [Free Agreement](https://www.min.io/legal/aistor-free-agreement) grants a non-transferable license and prohibits redistribution, so HVO cannot assume it may bundle the software or share a key | Not a distributable default; operator BYOL could be reconsidered as an external profile with written license clarity |
| [PGSTY Silo 2026-08-06](https://github.com/pgsty/silo/releases/tag/RELEASE.2026-08-06T00-00-00Z) | Active community MinIO continuation; AGPL; publishes multiarchitecture artifacts, SBOMs, signatures, and attestations | Young downstream project; preserves MinIO-specific configuration and on-disk behavior; still requires full independent qualification | Future external-S3 candidate under #589 |
| [VersityGW 1.8.0](https://github.com/versity/versitygw/releases/tag/v1.8.0) | Apache-2.0 S3 gateway over local or mounted POSIX storage; the [POSIX matrix](https://github.com/versity/versitygw/wiki/POSIX-Backend) covers the required object operations | Adds a gateway and credential plane that the same-host case does not need; xattr-preserving backup, non-root operation, and each filesystem topology require proof | Strong future LAN S3 candidate under #589 |
| [SeaweedFS 4.45+](https://github.com/seaweedfs/seaweedfs/releases/tag/4.45) | Apache-2.0, multiarchitecture, single-node option; releases after 4.44 address the known HVO qualification findings | 4.44 already failed HVO policy; rapid churn and supply-chain/topology evidence require a complete new qualification | Future external-S3 candidate, never an automatic retry |
| [Garage 2.3.0](https://garagehq.deuxfleurs.fr/_releases.html) | Lightweight distributed S3 implementation with multiarchitecture artifacts and documented [S3 compatibility](https://garagehq.deuxfleurs.fr/documentation/reference-manual/s3-compatibility/) | AGPL and its production durability guidance favors multiple nodes/copies, conflicting with the first-release single-host goal | Future candidate only if a distributed topology is wanted |
| [Ceph RGW 20.2.4](https://www.ceph.io/en/news/blog/2026/v20-2-4-v19-2-6-combo-released/) | Mature, broad [S3 support](https://docs.ceph.com/en/latest/radosgw/s3/) | Operational and privilege footprint is disproportionate for a bundled single-host service | Supported only as a future profile when an operator already owns Ceph |
| [RustFS 1.0.0-rc.5](https://github.com/rustfs/rustfs/releases) | Promising non-root, multiarchitecture, attested artifacts | Pre-GA release line and rapid storage/API change | Watch for stable GA before qualification |
| [Scality CloudServer 9.4.3](https://github.com/scality/cloudserver/releases/tag/9.4.3) | Apache-2.0 S3 implementation | No official native arm64 artifact was established and local topology is more complex than required | Rejected for the current roadmap |
| In-process fake S3 server | Could preserve an S3-shaped wire endpoint | Reimplements HTTP/auth/signing/pagination plus storage durability without helping the application boundary | Rejected; implement `IObjectStore` directly |

All third-party dispositions are time-bound research snapshots. Issue #589 must
reassess current releases, licenses, advisories, artifacts, and topology before
claiming a remote profile.

## Shared Filesystem Code and CameraAgent

CameraAgent and LogicHost store similar immutable bytes for different lifecycle
reasons. They should share filesystem mechanisms, not one host-level storage
contract.

A small host-neutral infrastructure project introduced by #592 owns only root
containment, symlink/reparse rejection, atomic same-filesystem publication,
durable file flush, Linux directory synchronization, and bounded cleanup/error
helpers. It contains no capture, manifest, bucket/key, object-generation,
retention, replay, SQLite, web, or provider concepts.

The new project is registered explicitly in the solution dependency graph,
architecture rules, component coverage policy, and CI classifier. It must not be
left to the classifier's unknown-project/complete-plan fallback.

LogicHost keeps `IObjectStore`, provider selection, logical buckets, generations,
and remote-provider errors. CameraAgent keeps `IFrameStorageService`, raw ingress,
canonical relative artifact paths, payload/sidecar commit order, SQLite journals,
outbox, retention holds, gallery, replay, and local recovery authority. CameraAgent
adoption of the shared primitives is isolated in post-`RM-017` issue
[#587](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/587); it is not part
of active `RM-016` implementation.

## Supported First-Release Envelope

- One LogicHost process/replica.
- Linux amd64 and arm64.
- Same-host ext4 Docker bind mount.
- Fixed non-root UID/GID and read-only container root.
- One dedicated writable object-data mount with two precreated logical bucket
  roots and no broader host filesystem visibility.
- Coordinated stop/fence for backup and relocation.

NFS, SMB, NAS appliances, XFS, ZFS, clustered filesystems, arbitrary Docker
volume drivers, multiple LogicHost writers, and remote-mount outage behavior are
not certified by this decision. They require separate topology evidence.

## Delivered By #584

The provider-neutral surface is in place and every central workflow consumes only it:

- **Selection.** `ObjectStorage:Provider` is `Filesystem` or `S3` (default `S3` while it is the
  only delivered adapter). S3 transport settings live in the `ObjectStorage:S3` group; the
  flattened `ObjectStorage:ServiceEndpoint`-style keys the deployment inventory writes forward
  to it, so no deployment changed. `ObjectStorage:Filesystem:Root` is reserved for #585.
- **Fail-closed validation.** Naming one provider while carrying the other's settings fails
  startup with the contradiction named; a filesystem root must be absolute and normalized.
  Installer preflight, not this validation, is what restricts the supported release profile.
- **Logical identity.** Every persisted `StorageReference` is `object://<bucket>/<key>`; the
  scheme names an identity every provider resolves, so switching providers changes no row.
  The unreleased schema has one canonical migration and needed no change: the column is a
  binary-collated string with no scheme constraint.
- **Neutral names.** The dependency health check is `object-store`; telemetry carries a
  bounded `object_store.provider` tag beside the transport-specific `addressing_style`; AWS
  SDK types are confined to `LogicHost/Infrastructure/ObjectStorage` and the S3 client
  factory reads only the S3 group.
- **Failure categories.** `Capacity` (not retryable, not terminal, operator action) and
  `CorruptState` (terminal, operator action) join the existing kinds with
  `RequiresOperator` semantics for health reporting.
- **Conformance.** `ObjectStoreConformanceSuite` takes a capability set; the universal
  assertions run for every provider, and the transport-fault assertions run only where the
  harness declares it can inject them.
- **#592 boundary.** `StorageFileSystemBoundaryIsNarrowWhenPresent` asserts the future
  project references no project, takes no host/persistence/provider package, and is consumed
  only by `LogicHost` and `CameraAgent.Common`.

## Delivery and Future Order

1. [#584](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/584) neutralizes provider selection, configuration, telemetry, health, and durable identity.
2. [#592](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/592) introduces and registers the host-neutral filesystem durability primitives.
3. [#585](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/585) implements the LogicHost filesystem provider on those primitives.
4. [#586](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/586) qualifies the exact same-host Linux ext4 topology.
5. [#506](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/506) adopts it across supported deployment, tests, CI, and operations and removes MinIO ownership.
6. After `RM-017`, [#587](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/587) may move CameraAgent onto the shared primitives without changing its contracts.
7. In vNext, [#591](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/591) qualifies one exact remote filesystem mount for storage-server capacity, [#589](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/589) qualifies and enables one exact external S3/LAN or AWS profile, and [#590](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/590) adds and qualifies Azure Blob.
