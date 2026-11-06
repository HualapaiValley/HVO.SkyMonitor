# LogicHost Packaging and Lifecycle Contract

Preparation contract for [#856](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/856),
under [#540](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/540) and
`RM-018` / [#531](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/531).
Source checkpoint: `27ec07492ac14245c99fe936089c06d73ef4e579`, 2026-10-15 UTC.
Approval [#847](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/847)
permits this preparation independently. **The requirements below describe future
LogicHost packaging and lifecycle implementation; they do not enable commands,
publish a package, or certify a new candidate.**

Revised 2026-10-23 by [#1606](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1606):
the [install contract](../hvo-install-contract.md) owns current release trust and
upgrade-time checkpoint policy. It supersedes this preparation's earlier
key-signed train and post-commit/image-only rollback assumptions. #1608 studies
the smallest coherent SQL Server/object/config checkpoint; existing #586
evidence and native maintenance tools are inputs, not a new qualification.
The [#1608 bounded decision](../validation/logichost-upgrade-recovery-1608.md)
retains COPY_ONLY/restore and the existing offline object primitives. Native SQL
snapshot/revert worked in three disposable CU26 trials but broke the log backup
chain and demonstrated no overall benefit sufficient to add another mechanism.
This settles backend selection, not coherent upgrade implementation/qualification.

## Delivered Inputs and Remaining Gates

The opening wording of #856 and #540 predates prerequisite closeout. Live #531
records #499, #535 and #513 closed. Consume the delivered results as follows:

| Input | Delivered authority | Limit retained by this contract |
| --- | --- | --- |
| Filesystem provider and adoption (`RM-016`) | [Final #586 disposition](../validation/logichost-filesystem-qualification-586.md#final-disposition); #506 adoption in [PR #926](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/926), merge `fb07a6ccd3be8d5b72a6f7643fc5e27978804c1d`; #499 closeout [PR #1146](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1146), merge `1204a0feb99ee2d70bd9216ef7b593e7777b9ce4` | One trusted native Linux writer, same-host ext4, fixed identity and declared mounts. Source merges, measured assemblies and exercised image digests are distinct identities. A later package must supply its own exact-candidate evidence. |
| Standalone CameraAgent (`RM-017`) | [Roadmap delivered record](../roadmap.md): #535 campaign `issue-211-final-20260922T033329Z`, generation 1, `acceptanceReady=true`, source `8388002ce69aa1fa194e7644bd8f35352ac10acf`; #513 and milestone 2 closed | Historical standalone qualification is satisfied. It does not qualify a new CameraAgent/LogicHost image pair, physical deployment, or publication. No edge installer or state changes belong here. |
| Central schema and initialization | [Database initialization](../runbooks/logichost-database-initialization.md), [state policy](../project-plan.md#311-database-engineering-and-shared-server-operations), initializer/runtime role scripts in `deploy/sql/` | LogicHost is unreleased: one canonical initial EF migration and fresh empty database; only attributable same-revision initialization retries. Migration IDs do not promise upgrades of earlier unreleased state. |
| Distribution and catalog verification | [Releases](../runbooks/releases.md), [production catalog](../catalog/production-install.md), [approved catalog specifications](../../src/HVO.SkyMonitor.Catalog.Sqlite/ApprovedCatalogSpecifications.json) | Use release.json, SHA256SUMS and GitHub build-provenance attestations under install contract section 4. LogicHost still needs component-specific declarations and verifier support. No revival of retired key-signed manifests/indexes; local assembly does not prove public distribution. |
| Topology and operations | [Product layout](../runbooks/product-instance-layout.md), [split-host preflight](../runbooks/split-host-preflight.md), [infra operations](../runbooks/infra-operations.md), [SQL operations](../runbooks/sql-server-operations.md) | Existing split-host orchestration and offline object modes are inputs, not an independent LogicHost installer. Official installation must work without a repository checkout or CameraAgent population. |

The retained #586 measurements identify revision
`84b8b0b6e9d744d45e7f6029e6b32e95df070fc0`, assembly SHA-256
`d2cbdaa47ab7ea3e0332e2b1872b221d5690aa93675ce18ba5b3f90950fde985`,
and [performance JSON](../validation/logichost-filesystem-qualification-586-performance.json)
SHA-256 `e33784f696df93d0c8a145cc94195ca7a705b48ea5db7eb0b38ba7fb80671825`.
The report's native image digests remain their original campaign identities.
Neither #1146's closeout merge nor this document replaces them.

There is no remaining open #499/#535 prerequisite for preparation. Remaining
storage work for #540 is candidate integration: mount/identity verification,
coherent SQL/object/host-state recovery, and exact-image fault and lifecycle
evidence. A new filesystem, multiple writers, remote mount, S3 or Azure profile
requires separate `RM-019` qualification/adoption; none is a hidden prerequisite
for the filesystem-only release. #538 importer and #539 workspace retain their
own owners and acceptance; publication must name the central features it actually
ships. #541 owns optional exact-digest combined acceptance.

Before overlapping recovery/profile/layout work or a substantial forward upgrade
path commits to an approach, #1244/#1245/#589/#591 cite #1608's recorded bounded
keep/change outcome and their supported forward inputs under
[the ordering gate](agent-execution.md#skymonitor-recovery-ordering-gate).
Keeping the current mechanism satisfies it. Clean-install preparation, release
verification and unrelated features stay independent. A separately declared
forward-only conversion specifies validation, interruption/resume and operator
recovery/data-loss expectations; it does not require reverse migration solely
due to size or waive ordinary protected checkpoint recovery.

## Supported Topology and State Ownership

The first release targets one LogicHost replica with a dedicated same-host Linux
ext4 object mount, runtime UID:GID `4242:4343`, precreated root and both configured
bucket directories at mode `0750`, and a read-only container root. Canonical bucket
names come from the selected deployment configuration (currently
`skymonitor-artifacts` and `skymonitor-diagnostics`), never an installer guess.
Do not permit another writer or external modification. Preserve the root's
exclusive runtime/maintenance lock; deleting a lock file is not recovery.

The tested x64 storage was direct ext4; the Pi 5 ARM64 result was ext4 over a loop
device over XFS/NVMe. Preserve that distinction for performance and durability
claims. NFS, SMB, NAS, XFS/ZFS object roots, arbitrary Docker volumes, clustered
filesystems and multiple writers remain unsupported. Full HTTP application
evidence is x64 with a fixture catalog (aggregate health remained degraded);
it does not establish production-catalog qualification. ARM64 has native
provider/image/offline-operation evidence and
no supported native Linux SQL Server image. An ARM64 full-stack claim needs a
separately reviewed service topology and native host evidence, not emulation or
a substitute database. The initial full-stack packaging story targets
`linux/amd64`; ARM64 inclusion is an explicit additional release gate.

| State/input | Planned location and authority | Lifecycle obligation |
| --- | --- | --- |
| Instance identity | `/var/lib/hvo-sky/logichosts/<instance-uuid>/instance-manifest.json` and referenced application binding | Canonical UUID and component identity are stable across rename/upgrade. Never infer identity from port, hostname or release revision. Reject UUID/component reuse or unproved adoption. |
| Configuration and certificates | Instance `config/`; separate `runtime-secrets`, `initializer-secrets`, certificates and rendered Compose inputs | Owner-only files; mount secrets/certificates read-only. Runtime receives no initializer credential. Public evidence carries redacted configuration identity, secret references/version IDs and certificate fingerprints, never secret bytes or low-entropy secret hashes. |
| Host state | Instance `state/`, including `data-protection` mounted at `/app/DataProtection-Keys` | Restore with matching SQL identity/authentication state and configuration. Preserve key ring and signing/encryption certificate references. Temporary `/tmp` is disposable. |
| Objects | Site-declared dedicated ext4 root, mounted at `/var/lib/hvo/object-store`, disjoint from every other root | Deployment precreates and proves mount UUID, permissions, buckets and capacity; never silently create a bare fallback directory. Preserve logical bucket/key, opaque generation, content type, modified time, length and SHA-256. |
| SQL Server | Externally provisioned application database and distinct initializer/runtime principals | SQL owns Identity/OpenIddict, jobs, leases, lineage and metadata. Installer neither deletes a shared server/database nor grants runtime DDL. Pin supported engine/compatibility and service artifact in the candidate. Existing diagnostic/recovery scripts require SQL Server 2022 / level 160. |
| Redis | Explicit service endpoint, ACL identity and application prefix | Cache/notification acceleration, not authoritative job state. Rebuild caches after recovery; never flush a shared Redis instance. Retain configuration/ACL references, not cache contents as recovery authority. |
| Catalog | `/var/lib/hvo-sky/catalogs/<catalog-id>/{current,previous,versions/}`, mounted read-only at `/app/catalog` | Immutable separately verified production bundles. Pin selected and historically referenced packages; no runtime download/rebuild. Shared catalogs cannot be removed by instance uninstall. |
| Operations, upgrade checkpoints and explicit backups | Product `operations/` and a separately declared private checkpoint/backup/evidence destination, outside all reverted SQL/object/config/state roots | Serialize conflicting operations; preserve ownership-bound journal, checkpoint/image pairing, decision/restore receipts and minimal non-rewindable capsule. Explicit encrypted off-host backups remain separate. Never use CameraAgent instance paths. |

The [current Compose model](../../deploy/split-host/compose.logichost.yml) mounts
initializer and runtime secrets separately, starts `logic-init` only in its
initialization profile and does not include SQL/Redis/Mailpit containers. Consume
existing SQL/Redis services through explicit routes. Any future managed-service
option needs pinned redistributable artifacts, license review, storage and
recovery contracts; automatic cloud provisioning is excluded. Mailpit is
development/test-only and absent from official installation. Required production
notification settings must be explicit; an unconfigured optional CameraAgent
population is healthy. The shipped Compose endpoint is HTTP: production requires
preprovisioned HTTPS termination, exact trusted proxies, public OIDC authority,
and certificate checks as described by split-host preflight.

## Release Inputs and Artifact Identity

The following is the **required information model**, not a schema accepted by
today's verifier. The implementation story must add LogicHost component/image
and state declarations to the install contract's release.json model, define
strict parsing/size/path bounds and introduce reader support before publishing.
Missing, duplicate, contradictory or
unsupported fields fail before extraction, loading, service stop or state change.

| Identity | Required release claim and independent verification |
| --- | --- |
| Release envelope | Component `logicHost`, product version/tag, exact 40-hex source revision, manifest schema version, creation/build identity, supported installer/verifier versions and compatibility policy. Bind every asset by safe relative name, kind, byte length and SHA-256; verify SHA256SUMS and GitHub build-provenance attestations under install contract section 4. |
| OCI runtime | Registry/repository and immutable index digest when multi-platform, plus each supported platform's manifest digest, configuration/image ID, offline archive hash/length and executable architecture. Re-derive platform, blob digests, loaded image ID and compatibility declarations from archive bytes; require agreement with registry and runtime inspection. A tag is discovery only. |
| SBOM and scan | Per-platform component SPDX inventory bound to that exact image ID, plus file inventory for distribution archives. Bind both through verified release assets. Record scanner/version/database/time, exact covered image IDs and severity counts; reject missing coverage and critical findings under the existing release policy. An archive file list is not a component SBOM. |
| Provenance | Exact source/tree, builder/workflow identity and run, pinned build/runtime base digests, Dockerfile/build-input hashes, architecture, SDK/tool versions and output subjects. Attestations bind provenance to exact assets/images. Do not claim reproducible bytes or an attestation format not actually produced and verified. |
| Central schema/state | Canonical EF migration ID/set, controlled initialization target, generated SQL hash, seed-chain/recipe identities, object-layout/checkpoint/backup format and configuration versions, declared supported forward-upgrade inputs and exact checkpoint recovery policy. Compare release claims with actual image declarations, initialization history and durable state. No reverse-reader obligation or CameraAgent raw-journal fields substitute for central schema identity. |
| Catalog | Approved catalog ID, exact package version, manifest version/hash, schema and preprocessing versions, database hash/length/row count, source/topology provenance and license/attribution hashes. Verify attested outer archive and strict inner manifest against approved specifications. Pin `RequiredCatalogId` and `RequiredPackageVersion`; never accept fixture packages in production. |
| Bundle dependencies | Attested CLI asset for the selected architecture, trusted verification/bootstrap inputs, Compose templates, SQL role/initialization artifacts, configuration schema, runbook, licenses/notices, dependency identity/route requirements, and catalog bundle or exact separately verified catalog reference. Any offline dependency must be present and verified; no network fallback or source checkout. |
| Installation receipt | Persist release.json/checksums/attestation verification evidence, original/resolved asset locations, exact loaded image, selected catalog/schema/backend, instance/host identity, redacted configuration digest, service versions, effective Compose digest and operation ID. This ownership-bound receipt records local installation, not publisher authorization. |

Use the release trust model in install contract section 4; the owner retired
ECDSA manifests, embedded key trust roots and signed indexes on 2026-10-16.
No key creation, rotation, export, signing or workflow dispatch occurs here.
Format changes require reader-first rollout and explicit review. Verify every
asset before use, including cache hits and offline media. Pending-upgrade
recovery selects the exact retained verified image/checkpoint pair, never an
older release index or an old binary against migrated state. Bound HTTPS redirects, download time/size, retries,
cancellation, disk space and safe extraction; reject links/path traversal and
unexpected files. Mirrors preserve exact bytes and add no trust.

An air-gapped bundle must contain every selected OCI archive, CLI, production
catalog, verification material and required license/notice, with external SQL,
Redis, TLS and secret prerequisites declared in advance. It must verify/load
without registry access. Dependency redistribution eligibility is a release
input, not permission inferred from a digest. Local candidates may rehearse
integrity/identity and recovery without claiming public attestation verification.
Registry digest agreement, authorized release workflow provenance and anonymous public-download verification remain #540 publication
evidence, not consequences of a successful local rehearsal.

## Lifecycle and Evidence Matrix

These operation names describe future LogicHost-only capabilities; they are not
new CLI examples. Existing `--host-mode=database-initialize` and
`--host-mode=object-store-backup`, `object-store-verify`, `object-store-restore`
are the documented primitives to compose. No CameraAgent stop/reset/drain,
installer change or remote fleet orchestration is part of the contract.

Every mutation requires an owner-authorized explicit target and durable operation
ID. Bind the operation to instance UUID, host/daemon, source/target release,
topology/configuration hashes and checkpoint or explicit recovery set. Record durable intent before
side effects and completion only after verification; file and parent-directory
synchronization must survive process/host loss. Resume accepts the identical
bound request, rechecks actual resources and continues idempotently. Changed
inputs, foreign ownership, an unknown partial outcome or a recreated resource
fail closed without overwriting retained evidence. Timeouts/cancellation do not
mean success. Automatic recovery needs a separate bounded path to the prior
healthy state; repeating an upgrade must not be the only way to recover service.

| Phase | Required success evidence | Reject/fault and recovery evidence |
| --- | --- | --- |
| Preflight | Read-only identity/ownership, release attestation/checksum and architecture verification; Docker/Compose, CPU/memory and declared limits; free bytes/inodes for live, staging, checkpoint and restore; exact ext4 source/UUID/mount and disjoint roots; numeric runtime access; SQL/Redis versions, reachability/ACLs and distinct roles; TLS/OIDC/proxy/certificate/key-ring checks; production catalog and configuration compatibility | Refuse untrusted provenance/asset swaps, unsupported forward inputs/platforms/backends, wrong/missing mount, nested mount/bind alias, symlink, foreign root, extra writer, insufficient capacity, DDL-capable runtime, missing initialization secret, fixture catalog and port collision before mutation. Record checks actually performed, including unavailable tools. Recheck identity/mount immediately before mutation under operational exclusion. |
| Prepare and clean install | Create only approved owned instance paths; stage verified CLI/image/catalog and private config. Record preserve policy, empty-database proof, canonical SQL hash and initializer attempt. Run exactly one controlled initializer; verify `Completed` exact target and runtime grants, remove initializer secret from runtime; start single exact image | Existing unreleased database of another revision requires explicit disposition/new empty database, never automatic deletion or history edits. Initialization lock contention, `Running`/`Failed` state or missing/unknown migration blocks runtime. Resume same attributable revision; preserve partial state/logs for diagnosis. |
| Verify installation | Compare receipt, actual image/platform/labels, schema/init/seed, catalog/backend/config and rendered mounts. Require `/alive`, dependency-aware `/health`, current object reconciliation with no persistent quarantine, representative authorized API/UI, login/OIDC, object checksum/retrieval and a durable central work result | Liveness alone cannot complete installation. Deny unauthorized API/UI; no agents configured must not fail central health. Health/identity disagreement leaves failed operation and owned recovery state. No edge acquisition dependency is introduced. |
| Backup (explicit command) | Close central ingress/mutation and fence worker/runner result publication; stop every central writer and prove quiescence/root-lock exclusion. Under one recorded cutoff, create encrypted SQL backup plus verified completed object backup, matching instance identity/config/secrets/key ring/certificates, immutable catalog references and verified releases. Commit a top-level recovery-set inventory only after every component verifies | No distributed atomic snapshot is assumed. Prevent writes across the whole backup interval; any uncertain quiescence or failed component leaves an incomplete set that cannot restore. Preserve runtime exclusion and record downtime, sizes, checksums, SQL backup/LSN identity and UTC cutoff. SQL `VERIFYONLY` alone does not prove a coherent restore. This full archive is not an automatic prerequisite for each upgrade. |
| Restore/recovery drill | With writers fenced, validate all recovery-set hashes and ownership before mutation; retain exact previous state. Restore objects using their offline maintenance protocol, SQL using the matching backup and host state/key ring/config as one recovery set. Verify SQL restore/CHECKDB and invariants, exact object inventory/generations, SQL-to-object references, auth and durable job/lineage behavior before admitting work | Refuse mixed-cutoff SQL/objects, partial completion, damaged/missing catalogs, wrong UUID/backend/mount or lost decryption material. Preserve provider prepared/committed restore markers and rollback trees; runtime stays fenced until offline recovery verifies all buckets. Never delete quarantine/markers to manufacture health. Record orphan/missing-reference disposition explicitly. |
| Upgrade | Verify current/candidate receipts and supported forward schema/seed inputs. Fence ingress, workers, publication, object maintenance, config/identity/key writers and every external writer; create and verify one SQL/object/config/identity/key checkpoint before candidate mutation. Journal exact prior image/Compose/catalog pairing outside reverted roots. Start candidate with admission closed; apply install contract section 6's semantic DB/reference/auth/dependency/probe health checks, durably commit, then resume ingress/work | Fresh prerelease revisions do not imply supported in-place upgrades. A missing/partial/corrupt or space-exhausted checkpoint refuses protected mutation; CameraAgent's operator-managed allowance is not a LogicHost policy. Before commit, a failed candidate restores only the exact paired coherent point; if recovery cannot verify, remain fenced with a bounded repair instruction. No full recovery archive or new backend is automatically required. |
| Rollback (pending upgrade recovery only) | Restore the exact pre-upgrade SQL/object/config/identity/key point with its verified prior image, programs and pinned catalogs; preserve and verify minimal non-rewindable external facts/floors outside reverted roots. Prove restored DB/reference/auth/lineage health, record recovery, then release admission | No image-only downgrade against candidate-mutated state, post-commit rollback window or daily snapshots. Seed changes require the same paired recovery even without an EF migration. Never edit history to manufacture compatibility. Interrupted recovery stays fenced until the exact point and capsule verify; uncertain state cannot run an old binary. Explicit backup/restore is separate. |
| Uninstall | Stop and remove only the exact owned LogicHost container/project; preserve SQL, objects, config, key ring, catalogs, release receipts, backups and operation history by default; record stopped/absent proof | Never delete a shared database, Redis instance/prefix, catalog or sibling instance. Purge is separate future authorization/disposition work, excluded from these implementation stories. Resume distinguishes exact not-found from daemon/permission/transport failure. |

Set site-approved RPO/RTO for explicit backup/restore, maximum checkpoint/restore duration, maintenance timeout,
capacity reserve and recovery retention before qualification. Measure achieved
downtime, bytes/I/O, CPU/RSS, throughput/backlog recovery and integrity at the
candidate's declared scale. No values are certified by this preparation. Backup
sets containing secrets require encryption and owner-only access; public ledgers
retain sanitized hashes/counts and links to private evidence, never credentials,
tokens, raw SQL diagnostic text or private key material.

### Mandatory Interruption Cuts

Run each applicable cut against real containers and the exact candidate. Retain
before/after identities, command exit/timeout, durable journal/provider marker,
logs, health, checksums and the successful retry or explicit refusal.

| Cut | Required durable outcome on restart/resume |
| --- | --- |
| Download/extraction/image load before prepare | No service/state mutation; partial cache never treated as verified input. |
| Intent publication, before/after directory or Compose mutation | Request/ownership-bound journal distinguishes not started, intended and completed effects; no duplicate instance or foreign adoption. |
| Initialization before DDL, during DDL, after schema before seed/completion | Runtime remains blocked until exact-revision controlled initialization converges; no fabricated EF history or singleton. |
| Stop/fence before backup and during any backup component | Prior service can be deliberately recovered through a verified path; partial recovery set never gets a completion record. |
| Checkpoint commit before upgrade mutation | Resume revalidates the entire paired point and current state; no partial or stale checkpoint reuse against changed inputs. |
| Restore staging, prepared marker, each bucket swap and committed cleanup | Whole-store provider recovery preserves prior or restored generations coherently, holds exclusion and verifies before removing rollback copies. |
| Between object, SQL and host-state restore | All central writers remain fenced; do not expose a mixed recovery point. Resume exact set or restore the verified previous set. |
| Candidate starts before semantic checks or receipt commit | Ingress and all external acknowledgements remain fenced. Failure attempts verified paired recovery; liveness alone cannot commit. |
| Final commit before admission release / restart after resumed work | Reconcile the durable decision and actual identities; never rewind committed acknowledged work onto the pre-upgrade checkpoint. |
| Recovery itself is interrupted or fails | Retain journal, marker and both evidence sets; bounded retry or operator repair, never a success receipt. |
| Success receipt before process exit / repeated completed request | Re-probe actual instance and identities, return the same terminal disposition without repeating destructive actions. |
| Host reboot / mount missing or changed | Preflight rejects bare/wrong root before runtime or restore; no silent new object store. |
| Uninstall after stop, before/after exact container removal | Preserve all authoritative state and receipts; resume only owned actions and prove exact absence. |

Additional mandatory rejection pairs are untrusted/mismatched attestation subject,
checksum mismatch, swapped archive/SBOM/platform, image/metadata
compatibility disagreement, older upgrade target, damaged catalog, insufficient
bytes/inodes, permission/read-only fault and second writer/maintenance lock
contention. Fixtures establish refusal logic; native exact-candidate drills
establish lifecycle evidence. Neither broadens #586's support envelope.

## Bounded Implementation Stories and Release Ownership

The following child stories belong to #540. They are defined for future explicit
assignment, **unclaimed and unstarted by #856**. Starting a story is distinct
from enabling or releasing it. Each must retain its own review, risk-tier local
evidence and target-branch protected CI; durable lifecycle implementation is
Tier C and final release qualification is Tier M.

| Story | Start prerequisites and bounded outcome | Acceptance and release gate |
| --- | --- | --- |
| [LHP-1 / #1242](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1242): LogicHost release declarations and local candidate builder | #856 merged; audit shared distribution readers and reserve shared-file work. Add LogicHost to release.json with exact archive-derived identity, central state declarations, per-platform SBOM/scan/provenance and offline assembly. Use install contract section 4 trust, not the retired key-signed train. | Strict parser/provenance/asset/architecture/catalog/compatibility rejection tests; offline verification; local integrity rehearsal makes no public attestation claim; existing CameraAgent/installer/catalog verification remains green. Reader support precedes publication. No production publish authority. |
| [LHP-2 / #1243](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1243): LogicHost preflight and clean offline install | #856 and LHP-1 delivered; choose amd64 exact candidate and declared existing-service topology. Implement owner-bound receipt/journal, read-only preflight, explicit prepare and empty-database initialization, runtime role isolation and preserve uninstall. No upgrade or purge. | No-checkout/offline real-container install reaches authorized healthy state with zero CameraAgents; wrong mount/UUID/roles/catalog/capacity/signature fails before mutation; init and receipt cuts resume or refuse safely; no secret leakage or CameraAgent mutation. Release waits for LHP-3/LHP-4 and #540 exact-candidate evidence. |
| [LHP-3 / #1244](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1244): Coherent central backup and restore | #856 merged and current SQL/object/host-state contracts available. May develop isolated recovery fixtures independently of LHP-2; integration needs its receipt/exclusion contract. Add one completed recovery-set inventory, consistent cutoff, verified restore and recovery of interrupted restore. | Real SQL + ext4 object + identity/key-ring/config restore proves exact generations/checksums/references/auth/lineage; mixed, partial, wrong-instance and damaged sets rejected before mutation; every restore/backup cut remains fenced; SQL CHECKDB and invariants pass. Document measured RPO/RTO and retained encrypted off-host set. No pre-release schema upgrade promise. |
| [LHP-4 / #1245](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1245): Upgrade-time checkpoint rollback and interrupted recovery | LHP-1/2/3 delivered; consume #1608's bounded backend recommendation and #1606's invariants; select two committed candidates and supported forward schema/seed inputs. Reuse coherent recovery primitives without requiring a full archive on every upgrade. Implement paired checkpoint recovery/resume while admission stays fenced. | Two-candidate rehearsal proves forward upgrade, semantic health/commit then admission, pre-commit post-mutation automatic recovery, failed/interrupted restore and rejection of arbitrary/post-commit downgrade; exact SQL/object/config/key/catalog/image and non-rewindable facts agree. Restore one instance with sibling unchanged. No production publication claim or disposable-schema compatibility retrofit. |

#540 retains production release ownership: reviewed supported platform/service
matrix, exact-candidate native integration and interruption matrix, independent
review, component qualification, license/security disposition, controlled
attested production publication and anonymous public re-verification. Publication
uses approved `main` release policy only after separately authorized promotion;
this v1 preparation does not authorize promotion or dispatch. If shared
distribution or transport contracts are missing, create a named shared
prerequisite rather than hiding a cross-host change in a LogicHost story.

The contract traces `STATE-001`–`STATE-004`, `COMPONENT-001`–`COMPONENT-006`,
`OBJ-001`–`OBJ-005` and `CENTRAL-PRODUCT-001`–`CENTRAL-PRODUCT-008` through the
[requirements crosswalk](requirements-crosswalk.md). #856 validation is Tier B
contract/source review, document/link/whitespace checks, classifier-selected
local controls and `profile=development-v1` protected checks. It performs no
runtime, hardware, destructive, production signing or end-to-end qualification
campaign and makes no new storage qualification claim.
