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
| File and directory durability | Local corrections sync copied descriptors and nested directory chains, publish the completion checksum after the inventory flush, and sync the first restore rename. Real crash/restart evidence remains absent. Newly created backup/restore root entries still require parent durability treatment. |
| Inventory input validation | Local regressions now reject null collections/entries, duplicate identities, unlisted buckets, unsafe names, invalid generation/digest/metadata, inconsistent totals, and restore scratch-name collisions before creating destination state. |
| Source/target isolation | Local library checks reject equal or ancestor/descendant roots and symlink/reparse ancestors before mutation. Bind-mount aliases and concurrent path substitution remain topology-preflight/exclusion concerns. |
| Restore exclusion and recovery | Local implementation now holds an exclusive root lock across runtime lifetime and destructive restore, rejects a second replica, writes a durable prepared/committed whole-store marker, rolls all buckets back from prepared state, and only cleans rollback copies after exact verification and committed publication. Runtime refuses unresolved, malformed, directory, link, and impossible marker states. Process-kill and host-restart evidence remains required. |
| Bounded reconciliation | Demonstrate eventual cleanup progress beyond the descriptor budget, and account for all scan phases in the work bound. |
| Persistent health | Prove quarantines, inaccessible buckets, stale/incomplete passes, and capacity pressure remain visible until resolved. |
| Deployment preflight | Reject unsupported filesystem/mounts, missing roots, wrong ownership/modes, symlinks, insufficient bytes/inodes, overlapping state, and extra writers. |
| Native and destructive campaign | Reserve isolated amd64/arm64 targets; record kernel, Docker, ext4/device/cache identities and run process-kill, reboot, disk/inode exhaustion, permission and read-only faults. Do not run these against shared application state. |
| Performance and observability | Re-run applicable W1/W2/W3M/W3P/W4 workloads on the exact container/mount; include backup/restore timing, resource scope, logs, metrics, traces and leakage checks. The #585 in-process benchmark is not topology qualification. |

## Local Validation

The backup test suite currently contains 26 cases, including the new regressions.
These are functional tests on temporary directories, not process-kill, native
ARM64, host-restart, or power-loss qualification.

```bash
dotnet test tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj \
  --no-restore -c Release --filter 'FullyQualifiedName~FilesystemObjectBackupTests'
```

Run the complete Tier M candidate gate only after the candidate is stable.
Retain raw destructive/performance evidence outside tracked source, bind it to
the candidate identity, and obtain independent review before a go decision.
