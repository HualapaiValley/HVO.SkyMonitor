# Filesystem Topology Qualification (#586)

Status: qualification in progress; no go decision. Issue #506 remains gated.
This record tracks gaps, not certification or proof of power-loss survival.

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
| File and directory durability | Local corrections sync copied descriptors and nested directory chains, publish the completion checksum after the inventory flush, and sync the first restore rename. Native ARM64 container probes passed file/directory sync, rename, hard-link and unlink durability. Two full Pi reboots preserved byte-identical object-store checksums and automatic ext4 loop remount. Newly created backup/restore root parent durability still needs final review. |
| Inventory input validation | Local regressions now reject null collections/entries, duplicate identities, unlisted buckets, unsafe names, invalid generation/digest/metadata, inconsistent totals, and restore scratch-name collisions before creating destination state. |
| Source/target isolation | Local library checks reject equal or ancestor/descendant roots and symlink/reparse ancestors before mutation. Bind-mount aliases and concurrent path substitution remain topology-preflight/exclusion concerns. |
| Restore exclusion and recovery | Local implementation holds an exclusive root lock across runtime lifetime and destructive restore, rejects a second replica, writes a durable prepared/committed whole-store marker, rolls all buckets back from prepared state, and only cleans rollback copies after exact verification and committed publication. Native ARM64 SIGKILL after a durable prepared marker recovered on the first restore retry from a runtime-owned baseline and exact verification passed. |
| Bounded reconciliation | Local implementation now divides every pass budget across descriptor, data, retirement-stamp and temporary phases, with a cursor per bucket/phase. A tiny-budget regression proves retired data and stale temporaries converge despite a larger live descriptor inventory. Exact-topology backlog timing remains required. |
| Persistent health | Local health is degraded until the first pass completes, while any pass is truncated or failed, while quarantine persists across later passes, and when the last evidence is older than two cadences. Inaccessible-bucket and capacity-threshold topology evidence remains required. |
| Deployment preflight | Reject unsupported filesystem/mounts, missing roots, wrong ownership/modes, symlinks, insufficient bytes/inodes, overlapping state, and extra writers. |
| Native and destructive campaign | ARM64 complete on native Pi 5 for preflight, build/publish, container filesystem probe, two reboots, read-only remount, permission loss, block/inode exhaustion, interrupted backup and interrupted restore. Exact x64 campaign remains. |
| Performance and observability | Re-run applicable W1/W2/W3M/W3P/W4 workloads on the exact container/mount; include backup/restore timing, resource scope, logs, metrics, traces and leakage checks. The #585 in-process benchmark is not topology qualification. |

## Local Validation

The backup test suite currently contains 26 cases, including the new regressions.
The ARM64 campaign additionally exercised the exact image and runtime identity
on the real ext4 qualification mount, including destructive and reboot cases.

```bash
dotnet test tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj \
  --no-restore -c Release --filter 'FullyQualifiedName~FilesystemObjectBackupTests'
```

Run the complete Tier M candidate gate only after the candidate is stable.
Retain raw destructive/performance evidence outside tracked source, bind it to
the candidate identity, and obtain independent review before a go decision.

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

- Run the same exact candidate campaign on the isolated x64 target.
- Run W1/W2/W3M/W3P/W4, reconciliation backlog, backup and restore timings on
  the ARM64 candidate, with loop-backed topology named in every result.
- Exercise full HTTP health/log/metric/trace behavior where supported SQL Server
  is available. Microsoft SQL Server has no supported native ARM64 Linux image,
  so the Pi campaign does not substitute another database implementation.
- Complete exact-head independent review, Tier M candidate gates, and a final
  go/no-go decision before #506 starts.
