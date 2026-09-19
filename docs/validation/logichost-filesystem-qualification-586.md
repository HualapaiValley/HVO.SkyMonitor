# Filesystem Topology Qualification (#586)

Status: correction in progress after independent review returned NO-GO. Issue
#506 remains gated. Earlier campaign results remain historical evidence; the
corrected backup exclusion, durability, CI ownership, runbook, and complete
resource-evidence changes require exact-head validation and correction review.

## Candidate

One LogicHost replica on native Linux amd64 and arm64, running with a fixed
non-root UID/GID and a read-only container root. Object storage is a dedicated
same-host ext4 bind mount with two deployment-precreated buckets. Other state
mounts are explicitly declared and must not overlap object storage.

NFS, SMB, NAS, other local filesystems, arbitrary volume drivers, multiple
writers, and emulated native-platform results are not qualified by this work.

## Initial Findings

The initial audit used development/v1 revision `bfd368d7`. Local corrections
are on `feature/586-filesystem-qualification`; the final evidence must name an
immutable reviewed revision and exact image digests.

| Gate | Finding and remaining evidence |
| --- | --- |
| Exact backup inventory | Regressions reproduced: metadata-only changes and unexpected live descriptors passed verification. Local corrections compare content type and exact modified time, reject metadata drift before restore swap, and count extra descriptors. |
| Empty buckets and invalid descriptors | Regressions reproduced: backups omitted empty bucket directories and accepted invalid content types. Local corrections preserve empty roots and validate descriptor metadata before completing backup. |
| File and directory durability | Corrected code syncs copied descriptors and nested directory chains, verifies the completed copied tree before publishing the completion checksum, and syncs every newly created top-level backup/restore directory through its first pre-existing ancestor. Native ARM64 container probes passed file/directory sync, rename, hard-link and unlink durability. Two full Pi reboots preserved byte-identical object-store checksums and automatic ext4 loop remount. Exact-head correction review remains. |
| Inventory input validation | Local regressions now reject null collections/entries, duplicate identities, unlisted buckets, unsafe names, invalid generation/digest/metadata, inconsistent totals, and restore scratch-name collisions before creating destination state. |
| Source/target isolation | Local library checks reject equal or ancestor/descendant roots and symlink/reparse ancestors before mutation. Bind-mount aliases and concurrent path substitution remain topology-preflight/exclusion concerns. |
| Offline maintenance exclusion and recovery | Corrected implementation holds one exclusive root lock across runtime lifetime, backup, verify, and destructive restore; rejects a second replica or concurrent maintenance; writes a durable prepared/committed whole-store marker; rolls all buckets back from prepared state; and only cleans rollback copies after exact verification and committed publication. Native ARM64 SIGKILL after a durable prepared marker recovered on the first restore retry from a runtime-owned baseline and exact verification passed. |
| Bounded reconciliation | Local implementation now divides every pass budget across descriptor, data, retirement-stamp and temporary phases, with a cursor per bucket/phase. A tiny-budget regression proves retired data and stale temporaries converge despite a larger live descriptor inventory. Exact-topology backlog timing remains required. |
| Persistent health | Local health is degraded until the first pass completes, while any pass is truncated or failed, while quarantine persists across later passes, and when the last evidence is older than two cadences. Inaccessible-bucket and capacity-threshold topology evidence remains required. |
| Deployment preflight | Reject unsupported filesystem/mounts, missing roots, wrong ownership/modes, symlinks, insufficient bytes/inodes, overlapping state, and extra writers. |
| Native and destructive campaign | Historical ARM64 and x64 campaigns completed preflight, native build/publish, container filesystem probes, read-only remount, permission loss, block/inode exhaustion, interrupted backup/restore, and reboot recovery. The corrected maintenance-lock head requires affected native reruns. |
| Performance and observability | Historical five-trial W1/W2/W3M/W3P/W4 and backup/restore timing is recorded below. The corrected harness now records CPU, allocations, before/after/peak RSS and Linux process I/O for every phase, and validates exact list order and copied payload SHA-256. New five-trial retained machine-readable evidence is required before GO. |

## Local Validation

The backup test suite contains 26 cases, including offline backup/verify/runtime
exclusion and exact backup verification regressions.
The ARM64 campaign additionally exercised the exact image and runtime identity
on the real ext4 qualification mount, including destructive and reboot cases.

```bash
dotnet test tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj \
  --no-restore -c Release --filter 'FullyQualifiedName~FilesystemObjectBackupTests'
```

Run the complete Tier M candidate gate only after the correction candidate is
stable. Retain a checksummed machine-readable projection of all five native
trials in tracked validation evidence, bind it to the candidate identity, and
obtain independent correction review before a go decision.

## Candidate Artifacts

- `deploy/qualification/compose.logichost-filesystem.yml` is a qualification-only
  overlay. Combined with `deploy/split-host/compose.logichost.yml`, it fixes the
  runtime UID/GID, retains the read-only container root, selects the filesystem
  provider, and exposes only the dedicated object-store bind mount at
  `/var/lib/hvo/object-store` in addition to existing declared mounts. It is not
  the supported deployment adoption owned by #506.
- `scripts/qualify:filesystem-object-store` validates a precreated canonical
  same-host ext4 mount, non-root ownership, exact `0750` root/bucket modes, both
  logical buckets, configured free-byte/inode thresholds, and disjoint state
  paths. Success emits `hvo-filesystem-object-store-preflight-v1` JSON with
  kernel, architecture, mount, block-device, identity and capacity facts.
- `scripts/test:filesystem-object-store-qualification` exercises the pass case
  and rejects wrong filesystem, read-only mount, wrong mode, low bytes/inodes,
  overlap, nested bucket mounts, bind aliases (including `FSROOT=/`), signed
  integer overflow, and a missing bucket. These fixture tests do not qualify a
  real host.
- Protected CI's classifier assigns both qualification scripts to Deployment
  Contracts, whose lightweight contract step executes the fixture on every
  selected head. Classifier contract tests require this ownership and invocation.

The preflight intentionally does not claim to settle privileged mount mutation,
the complete block-device parent/controller/cache chain, LSM/user-namespace
access, or the exact container's effective write/fsync/rename behavior. Those
facts belong to the isolated candidate campaign, which must run preflight and
container startup under one recorded operational exclusion and retain the
rendered Compose model and exact image digest.

## Native ARM64 Evidence

- Host: `allsky01`, Raspberry Pi 5, Cortex-A76, 4 cores, 16 GiB, native
  `aarch64`, Debian 13, kernel `6.18.34+rpt-rpi-2712`, wired `eth0`
  `192.168.2.49`, Docker 29.6.2, Compose 5.3.1, SDK 10.0.401.
- Candidate storage: 20 GiB sparse image on NVMe/XFS, `/dev/loop1`, ext4 UUID
  `60f60c10-d77e-46a0-a641-5396e9413535`, mounted at
  `/var/lib/hvo-qualification/object-store`, UID/GID `4242:4343`, root and
  bucket modes `0750`. This qualifies loop-backed ext4 correctness; performance
  must be labeled as ext4-over-loop-over-XFS/NVMe, not direct ext4 NVMe.
- Preflight evidence SHA-256:
  `8cd8f6d1d5e6359d355fafca64beffa9d8b7a8449645bb2851d2438f7153a10a`.
- Native Release solution build: warning-clean, 9m17s. Storage.FileSystem Unit:
  61/61. LogicHost filesystem/backup/reconciliation: 77 pass, 1 expected skip.
- Self-contained ARM64 publish: 509 files; ELF checksum
  `ceecff21ceca6cbb16d1099ee8dfdf230e045a7e6ce01c947252ae05a3e8a42e`.
- Corrected candidate image:
  `sha256:2edfb42b989398179f5b6c0e612ae23144d61dd93911ce3a04522ac4cb40f9e2`.
  Read-only root and numeric runtime identity container probe passed both buckets.
- Offline image backup/verify/destructive restore/verify passed exact key,
  metadata, generation, length and SHA-256 comparison. The campaign found that
  restored bucket mode could become `0755`; commit `665e17fe` preserves existing
  mode or uses `0750` for fresh roots, and the corrected ARM64 restore preserved
  both buckets as `4242:4343` mode `0750`.
- Read-only remount and permission loss failed closed and recovered without
  leaking a physical path or object key. Dedicated ext4 fixtures proved neutral
  `Capacity` for 100% block use and 0 free inodes, followed by successful writes
  after release. Startup requires enough free inodes to create/open the runtime
  coordination lock and is protected by the preflight inode threshold.
- SIGKILL during backup left no completion checksum. SIGKILL during restore left
  a durable `prepared` marker and rollback trees; first retry from a valid
  runtime-owned baseline recovered automatically and exact verification passed.
  A deliberately root-owned manually seeded rollback subtree remained fenced
  until operator ownership repair, which is the required fail-closed behavior.
- Two reboot checkpoints returned on the reserved wired address in 15 seconds,
  automatically reattached `/dev/loop1`, and reproduced byte-identical object
  checksums. Final Pi telemetry was `throttled=0x0`, 44.4 C.
- Raw evidence is retained outside the candidate filesystem under
  `/var/lib/hvo-qualification/evidence` on `allsky01`.

## Remaining Gates

- Retain the limitation that Microsoft SQL Server has no supported native ARM64
  Linux image; full HTTP host evidence is therefore x64, while ARM64 evidence is
  native provider/image/offline-operation evidence. No substitute database was
  introduced.
- Rerun affected native backup/verify exclusion and complete five-trial resource
  evidence on x64 and ARM64 at one immutable correction revision.
- Commit the checksum-bound evidence projection and complete exact-head
  correction review, Tier M candidate gates, and a final go/no-go decision
  before #506 starts.

## Native X64 Evidence

- Primary target: `gh-runner-01`, Ubuntu 24.04 x64 KVM VM, 4 CPUs, 12 GiB,
  Docker 29.8.1, SDK 10.0.401. Dedicated 20 GiB `/dev/sdb` was reformatted
  ext4 with UUID `fa678c86-1c12-4eba-8f04-c84256772089` and mounted directly at
  `/var/lib/hvo-qualification/object-store` with the same `4242:4343` and
  `0750` contract.
- Native Release solution build was warning-clean. Storage.FileSystem Unit was
  61/61. LogicHost filesystem/backup/reconciliation was 77 pass and one expected
  skip. Self-contained `linux-x64` publish produced an x86-64 ELF. The exact
  x64 image was `sha256:28be739ceb5ffbdef0fa385f7dee24262f44a5896da91807c102928135f60621`.
- Read-only-root/non-root container probe passed file and directory sync,
  atomic rename, hard-link/unlink semantics, cleanup and root-write refusal.
- Full host startup used real pinned SQL Server, Redis and Mailpit containers,
  no MinIO, a valid fixture catalog package, the ext4 filesystem provider, a
  read-only root and the declared Data Protection mount. Database migration and
  seeding passed; `/alive` was 200; database, Redis, SMTP and object-store health
  were Healthy; the aggregate remained Degraded only because the fixture catalog
  correctly reports that production catalog data is not active. Object-store
  health reported zero quarantine/failures/backlog and current reconciliation.
  Logs, Prometheus metrics, health JSON and exact container inspection were
  retained under `/var/lib/hvo-qualification/evidence`.
- Read-only remount and permission loss failed closed and recovered. Dedicated
  ext4 fixtures proved neutral `Capacity` for 100% blocks and zero free inodes,
  followed by successful writes after release. SIGKILL during backup left no
  completion checksum. SIGKILL after a prepared restore marker recovered on the
  first retry and exact verify passed. A VM reboot automatically mounted
  `/dev/sdb`, retained byte-identical store checksums and passed exact backup
  verification. The GitHub runner was restored online afterward.

## Exact-Mount Performance

These historical five independent trials ran the same Docker-free provider
workload at candidate revision `525312ef` on each exact mount. Their raw JSON
remains with each host and does not contain complete resource metrics for every
phase; it is not the final correction evidence. Values below are median with
observed min-max where material.

| Metric | x64 direct ext4 | ARM64 ext4 loop over NVMe/XFS |
| --- | ---: | ---: |
| W1 median latency | 57.7 ms (56.5-61.6) | 255.2 ms (62.6-452.3) |
| W1 p95 | 63.0 ms (60.3-67.5) | 321.1 ms (74.3-486.9) |
| W1 throughput | 17.24 ops/s (16.12-17.70) | 3.78 ops/s (2.20-16.20) |
| W2 median latency | 146.0 ms (142.8-147.8) | 684.2 ms (158.7-1342.5) |
| W2 p95 | 161.2 ms (150.6-164.8) | 755.7 ms (707.4-1409.5) |
| W2 throughput | 6.86 ops/s (6.75-6.96) | 1.46 ops/s (0.78-1.93) |
| W4 c1/c4/c8 throughput | 17.70 / 27.66 / 42.68 ops/s | 3.71 / 6.77 / 9.19 ops/s |
| W3M list 10,000 | 83.7 ms (80.0-87.2) | 352.2 ms (320.2-381.6) |
| W3P drain 100 x W2 | 42.5 ms (38.9-43.8) | 164.4 ms (23.6-291.4) |
| Restart + reconciliation | 117.6 ms (115.9-127.0) | 437.5 ms (427.6-466.9) |
| Backup one W2 object | 61.8 ms (59.8-63.2) | 357.4 ms (324.5-385.8) |
| Restore one W2 object | 58.3 ms (56.8-63.0) | 146.8 ms (115.7-309.0) |

The x64 direct-ext4 topology is stable. ARM64 shows material durability-latency
variance despite `throttled=0x0` and temperatures of 42-47 C; this is attributed
only to the measured Pi 5 loop-backed stack and is not generalized to direct
ext4 storage. Its median W2 rate still exceeds the supported handful-of-rigs
ingest envelope, W4 scales to 9.19 operations/s, restart reconciliation remains
under half a second, and memory/allocation behavior stayed bounded. The ARM64
performance result is therefore acceptable for correctness and supported load,
with the loop-backed topology and high tail variance retained as explicit
operational limits rather than hidden as noise.

## Current Disposition

The exact support envelope remains one trusted LogicHost writer on native Linux
amd64 or arm64, with a fixed non-root identity, read-only container root and a
dedicated same-host ext4 mount. Direct ext4 x64 and loop-backed ext4 Pi 5 are the
tested storage forms; NFS, SMB, NAS, XFS/ZFS object roots, clustered filesystems,
arbitrary Docker volumes and multiple writers remain unsupported. The current
decision is **NO-GO pending correction evidence and rereview**. Historical
campaign results support the envelope, but #506 remains gated until the corrected
head passes the required native reruns, complete Tier M gate, and exact-range
independent correction review.
