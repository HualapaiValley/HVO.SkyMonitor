# LogicHost Signed Packaging and Lifecycle Contract

Preparation contract for [#856](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/856),
under [#540](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/540) and
`RM-018` / [#531](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/531).
Source checkpoint: `27ec07492ac14245c99fe936089c06d73ef4e579`, 2026-10-15 UTC.
Approval [#847](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/847)
permits this preparation independently. **The requirements below describe future
LogicHost packaging and lifecycle implementation; they do not enable commands,
publish a package, or certify a new candidate.**

## Delivered Inputs and Remaining Gates

The opening wording of #856 and #540 predates prerequisite closeout. Live #531
records #499, #535 and #513 closed. Consume the delivered results as follows:

| Input | Delivered authority | Limit retained by this contract |
| --- | --- | --- |
| Filesystem provider and adoption (`RM-016`) | [Final #586 disposition](../validation/logichost-filesystem-qualification-586.md#final-disposition); #506 adoption in [PR #926](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/926), merge `fb07a6ccd3be8d5b72a6f7643fc5e27978804c1d`; #499 closeout [PR #1146](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1146), merge `1204a0feb99ee2d70bd9216ef7b593e7777b9ce4` | One trusted native Linux writer, same-host ext4, fixed identity and declared mounts. Source merges, measured assemblies and exercised image digests are distinct identities. A later package must supply its own exact-candidate evidence. |
| Standalone CameraAgent (`RM-017`) | [Roadmap delivered record](../roadmap.md): #535 campaign `issue-211-final-20260922T033329Z`, generation 1, `acceptanceReady=true`, source `8388002ce69aa1fa194e7644bd8f35352ac10acf`; #513 and milestone 2 closed | Historical standalone qualification is satisfied. It does not qualify a new CameraAgent/LogicHost image pair, physical deployment, or publication. No edge installer or state changes belong here. |
| Central schema and initialization | [Database initialization](../runbooks/logichost-database-initialization.md), [state policy](../project-plan.md#311-database-engineering-and-shared-server-operations), initializer/runtime role scripts in `deploy/sql/` | LogicHost is unreleased: one canonical initial EF migration and fresh empty database; only attributable same-revision initialization retries. Migration IDs do not promise upgrades of earlier unreleased state. |
| Distribution and catalog verification | [Signed distribution](../runbooks/release-distribution.md), [production catalog](../catalog/production-install.md), [approved catalog specifications](../../src/HVO.SkyMonitor.Catalog.Sqlite/ApprovedCatalogSpecifications.json) | Existing image train and compatibility fields are CameraAgent-specific. Reusing their mechanism requires an explicit LogicHost format and verifier change. Locally signed rehearsals do not prove production-key custody or public download. |
| Topology and operations | [Product layout](../runbooks/product-instance-layout.md), [split-host preflight](../runbooks/split-host-preflight.md), [infra operations](../runbooks/infra-operations.md), [SQL operations](../runbooks/sql-server-operations.md) | Existing split-host orchestration and offline object modes are inputs, not an independent signed LogicHost installer. Official installation must work without a repository checkout or CameraAgent population. |

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
| Operations and recovery sets | Product `operations/` and a separately declared private backup/evidence destination, outside live object/state roots | Serialize conflicting operations; preserve ownership-bound journal, rollback copies, encrypted off-host backups and retained signed inputs. Never use CameraAgent instance paths. |

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

## Signed Inputs and Artifact Identity

The following is the **required information model**, not a schema accepted by
today's verifier. The implementation story must assign a versioned LogicHost
manifest and train namespace that cannot collide with CameraAgent
`image-v*`/indexes, define strict parsing/size/path bounds and introduce reader
support before publishing that format. Missing, duplicate, contradictory or
unsupported fields fail before extraction, loading, service stop or state change.

| Identity | Required signed claim and independent verification |
| --- | --- |
| Release envelope | Component `LogicHost`, package/train/version, exact 40-hex source revision, manifest schema version, creation/build identity, trust-key ID, supported installer/verifier versions and compatibility policy. Sign exact UTF-8 manifest bytes; no parse/reserialize verification. Bind every asset by safe relative name, kind, byte length and SHA-256. |
| OCI runtime | Registry/repository and immutable index digest when multi-platform, plus each supported platform's manifest digest, configuration/image ID, offline archive hash/length and executable architecture. Re-derive platform, blob digests, loaded image ID and compatibility declarations from archive bytes; require agreement with registry and runtime inspection. A tag is discovery only. |
| SBOM and scan | Per-platform component SPDX inventory bound to that exact image ID, plus file inventory for distribution archives. Hash both in the signed envelope. Record scanner/version/database/time, exact covered image IDs and severity counts; reject missing coverage and critical findings under the existing release policy. An archive file list is not a component SBOM. |
| Provenance | Exact source/tree, builder/workflow identity and run, pinned build/runtime base digests, Dockerfile/build-input hashes, architecture, SDK/tool versions and output subjects. Signed asset hash binds provenance to image/archive/SBOM. Do not claim reproducible bytes or an attestation format not actually produced and verified. |
| Central schema/state | Canonical EF migration ID/set, controlled initialization target, generated SQL hash, seed-chain/recipe identities, object-layout/backup format and configuration-contract versions, supported predecessor/reader ranges and explicit rollback policy. Compare signed claims with actual image declarations, initialization history and durable state. No CameraAgent raw-journal compatibility fields substitute for central schema identity. |
| Catalog | Approved catalog ID, exact package version, manifest version/hash, schema and preprocessing versions, database hash/length/row count, source/topology provenance and license/attribution hashes. Verify signed outer archive and strict inner manifest against approved specifications. Pin `RequiredCatalogId` and `RequiredPackageVersion`; never accept fixture packages in production. |
| Bundle dependencies | Signed CLI archive for the selected architecture, trusted verification/bootstrap inputs, Compose templates, SQL role/initialization artifacts, configuration schema, runbook, licenses/notices, dependency identity/route requirements, and catalog bundle or exact separately signed catalog reference. Any offline dependency must be present and verified; no network fallback or source checkout. |
| Installation receipt | Persist signed manifest/signatures/checksums/index sequence and hash, original/resolved asset locations, exact loaded image, selected catalog/schema/backend, instance/host identity, redacted configuration digest, service versions, effective Compose digest and operation ID. This local ownership-bound receipt is distinct from the publisher's signature. |

Use the existing ECDSA P-256/SHA-256 exact-byte mechanism and embedded public
trust root as the reuse baseline. No production key creation, rotation, export,
signing or workflow dispatch occurs under #856. Format/key changes require
reader-first rollout and explicit review. Verify both metadata signature and
every asset before use, including cache hits and offline media. Verify a signed
append-only index and retained sequence/hash state; an intentional rollback
selects a retained verified release through the lifecycle policy, not a replayed
older default index. Bound HTTPS redirects, download time/size, retries,
cancellation, disk space and safe extraction; reject links/path traversal and
unexpected files. Mirrors preserve exact bytes and add no trust.

An air-gapped bundle must contain every selected OCI archive, CLI, production
catalog, verification material and required license/notice, with external SQL,
Redis, TLS and secret prerequisites declared in advance. It must verify/load
without registry access. Dependency redistribution eligibility is a release
input, not permission inferred from a digest. Use ephemeral rehearsal keys only
in isolated candidates with a correspondingly built verifier; the product
verifier must reject those candidates. Production custody, registry index
agreement and anonymous public-download verification remain #540 publication
evidence, not consequences of a successful local rehearsal.

## Lifecycle and Evidence Matrix

These operation names describe future LogicHost-only capabilities; they are not
new CLI examples. Existing `--host-mode=database-initialize` and
`--host-mode=object-store-backup`, `object-store-verify`, `object-store-restore`
are the documented primitives to compose. No CameraAgent stop/reset/drain,
installer change or remote fleet orchestration is part of the contract.

Every mutation requires an owner-authorized explicit target and durable operation
ID. Bind the operation to instance UUID, host/daemon, source/target release,
topology/configuration hashes and recovery set. Record durable intent before
side effects and completion only after verification; file and parent-directory
synchronization must survive process/host loss. Resume accepts the identical
bound request, rechecks actual resources and continues idempotently. Changed
inputs, foreign ownership, an unknown partial outcome or a recreated resource
fail closed without overwriting retained evidence. Timeouts/cancellation do not
mean success. Automatic recovery needs a separate bounded path to the prior
healthy state; repeating an upgrade must not be the only way to recover service.

| Phase | Required success evidence | Reject/fault and recovery evidence |
| --- | --- | --- |
| Preflight | Read-only identity/ownership, signed inputs and architecture verification; Docker/Compose, CPU/memory and declared limits; free bytes/inodes for live, staging, backup and rollback; exact ext4 source/UUID/mount and disjoint roots; numeric runtime access; SQL/Redis versions, reachability/ACLs and distinct roles; TLS/OIDC/proxy/certificate/key-ring checks; production catalog and configuration compatibility | Refuse bad signatures/asset swaps, unsupported readers/platforms/backends, wrong/missing mount, nested mount/bind alias, symlink, foreign root, extra writer, insufficient capacity, DDL-capable runtime, missing initialization secret, fixture catalog and port collision before mutation. Record checks actually performed, including unavailable tools. Recheck identity/mount immediately before mutation under operational exclusion. |
| Prepare and clean install | Create only approved owned instance paths; stage verified CLI/image/catalog and private config. Record preserve policy, empty-database proof, canonical SQL hash and initializer attempt. Run exactly one controlled initializer; verify `Completed` exact target and runtime grants, remove initializer secret from runtime; start single exact image | Existing unreleased database of another revision requires explicit disposition/new empty database, never automatic deletion or history edits. Initialization lock contention, `Running`/`Failed` state or missing/unknown migration blocks runtime. Resume same attributable revision; preserve partial state/logs for diagnosis. |
| Verify installation | Compare receipt, actual image/platform/labels, schema/init/seed, catalog/backend/config and rendered mounts. Require `/alive`, dependency-aware `/health`, current object reconciliation with no persistent quarantine, representative authorized API/UI, login/OIDC, object checksum/retrieval and a durable central work result | Liveness alone cannot complete installation. Deny unauthorized API/UI; no agents configured must not fail central health. Health/identity disagreement leaves failed operation and owned recovery state. No edge acquisition dependency is introduced. |
| Backup | Close central ingress/mutation and fence worker/runner result publication; stop every central writer and prove quiescence/root-lock exclusion. Under one recorded cutoff, create encrypted SQL backup plus verified completed object backup, matching instance identity/config/secrets/key ring/certificates, immutable catalog references and signed releases. Commit a top-level recovery-set inventory only after every component verifies | No distributed atomic snapshot is assumed. Prevent writes across the whole backup interval; any uncertain quiescence or failed component leaves an incomplete set that cannot restore. Preserve runtime exclusion and record downtime, sizes, checksums, SQL backup/LSN identity and UTC cutoff. SQL `VERIFYONLY` alone does not prove a coherent restore. |
| Restore/recovery drill | With writers fenced, validate all recovery-set hashes and ownership before mutation; retain exact previous state. Restore objects using their offline maintenance protocol, SQL using the matching backup and host state/key ring/config as one recovery set. Verify SQL restore/CHECKDB and invariants, exact object inventory/generations, SQL-to-object references, auth and durable job/lineage behavior before admitting work | Refuse mixed-cutoff SQL/objects, partial completion, damaged/missing catalogs, wrong UUID/backend/mount or lost decryption material. Preserve provider prepared/committed restore markers and rollback trees; runtime stays fenced until offline recovery verifies all buckets. Never delete quarantine/markers to manufacture health. Record orphan/missing-reference disposition explicitly. |
| Upgrade | Verify current and candidate receipts and reader/state compatibility before stop. Take a complete verified recovery set; journal transition, stop/fence old writer, initialize only the approved schema path, start candidate, compare all identities and run semantic health/work/auth checks before commit | Fresh prerelease revision changes do not imply in-place upgrades. First released predecessor support needs a reviewed migration/seed policy. Fault after candidate mutation restores the complete prior coherent set when permitted, including exact image/Compose/config/catalog pointers and SQL/object/host state; otherwise remain stopped with a bounded recovery instruction. Never retry into unverified state. |
| Rollback | Predeclare image-only compatibility or full recovery-set restoration and accepted data-loss/RPO window. Verify retained signed predecessor and its schema/seed/catalog/object readers; prove prior service and auth/work behavior | Reject old binaries against newer incompatible SQL/seed/object state before mutation. Seed-chain rollback can leave a newer global default unreadable even without an EF migration. Never edit seed rows/history or declare a caller compatibility switch sufficient proof. If a coherent rollback cannot be verified, roll forward or require explicit recovery disposition. |
| Uninstall | Stop and remove only the exact owned LogicHost container/project; preserve SQL, objects, config, key ring, catalogs, release receipts, backups and operation history by default; record stopped/absent proof | Never delete a shared database, Redis instance/prefix, catalog or sibling instance. Purge is separate future authorization/disposition work, excluded from these implementation stories. Resume distinguishes exact not-found from daemon/permission/transport failure. |

Set site-approved RPO/RTO, maximum backup/restore duration, maintenance timeout,
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
| Recovery-set commit before upgrade mutation | Resume revalidates the entire set and current state; no stale-backup reuse against changed inputs. |
| Restore staging, prepared marker, each bucket swap and committed cleanup | Whole-store provider recovery preserves prior or restored generations coherently, holds exclusion and verifies before removing rollback copies. |
| Between object, SQL and host-state restore | All central writers remain fenced; do not expose a mixed recovery point. Resume exact set or restore the verified previous set. |
| Candidate starts before semantic checks or receipt commit | Failure attempts verified prior recovery; health alone cannot commit. |
| Recovery itself is interrupted or fails | Retain journal, marker and both evidence sets; bounded retry or operator repair, never a success receipt. |
| Success receipt before process exit / repeated completed request | Re-probe actual instance and identities, return the same terminal disposition without repeating destructive actions. |
| Host reboot / mount missing or changed | Preflight rejects bare/wrong root before runtime or restore; no silent new object store. |
| Uninstall after stop, before/after exact container removal | Preserve all authoritative state and receipts; resume only owned actions and prove exact absence. |

Additional mandatory rejection pairs are foreign/untrusted key, wrong key ID,
signature over different bytes, swapped archive/SBOM/platform, image/metadata
compatibility disagreement, index rollback, damaged catalog, insufficient
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
| [LHP-1 / #1242](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1242): LogicHost signed format and local candidate builder | #856 merged; audit shared distribution readers and reserve any shared-file work. Add a collision-free LogicHost manifest/train, exact archive-derived identity, central compatibility declarations, per-platform SBOM/scan/provenance and offline candidate assembly. Local ephemeral trust only; no publisher/key changes. | Strict parser/trust/asset/architecture/catalog/compatibility rejection tests; round-trip offline verification; product verifier rejects rehearsal trust; existing CameraAgent/installer/catalog verification remains green. Reader support must precede any new format publication. No production publish authority. |
| [LHP-2 / #1243](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1243): LogicHost preflight and clean offline install | #856 and LHP-1 delivered; choose amd64 exact candidate and declared existing-service topology. Implement owner-bound receipt/journal, read-only preflight, explicit prepare and empty-database initialization, runtime role isolation and preserve uninstall. No upgrade or purge. | No-checkout/offline real-container install reaches authorized healthy state with zero CameraAgents; wrong mount/UUID/roles/catalog/capacity/signature fails before mutation; init and receipt cuts resume or refuse safely; no secret leakage or CameraAgent mutation. Release waits for LHP-3/LHP-4 and #540 exact-candidate evidence. |
| [LHP-3 / #1244](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1244): Coherent central backup and restore | #856 merged and current SQL/object/host-state contracts available. May develop isolated recovery fixtures independently of LHP-2; integration needs its receipt/exclusion contract. Add one completed recovery-set inventory, consistent cutoff, verified restore and recovery of interrupted restore. | Real SQL + ext4 object + identity/key-ring/config restore proves exact generations/checksums/references/auth/lineage; mixed, partial, wrong-instance and damaged sets rejected before mutation; every restore/backup cut remains fenced; SQL CHECKDB and invariants pass. Document measured RPO/RTO and retained encrypted off-host set. No pre-release schema upgrade promise. |
| [LHP-4 / #1245](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1245): Upgrade, rollback and interrupted service recovery | LHP-1/2/3 delivered; explicitly select two committed candidates and supported schema/seed relation. Implement coherent verified predecessor recovery, resume and recovery-only path; preserve uninstall history. Do not retrofit compatibility for disposable unreleased schemas. | Two-candidate signed local rehearsal proves upgrade, post-mutation automatic recovery, intentional rollback, failed/interrupted recovery and incompatible reader/seed refusal; exact prior receipts and SQL/object/host identities agree. No production key/publication claims. After first release, supported predecessor migrations require their own published policy/evidence. |

#540 retains production release ownership: reviewed supported platform/service
matrix, exact-candidate native integration and interruption matrix, independent
review, component qualification, license/security disposition, controlled
production signing/publication and anonymous public re-verification. Publication
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
