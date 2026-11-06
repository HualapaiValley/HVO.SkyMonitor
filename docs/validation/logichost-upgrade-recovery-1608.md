# LogicHost upgrade recovery mechanism decision (#1608)

2026-10-23; baseline `a20d606ea764b2001409984980708f410a001d23`, after
[#1606](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1606).
**Keep the existing native COPY_ONLY backup/restore toolkit and offline object
recovery primitives.** No new snapshot backend, persistent layout, general
framework or routine CI campaign is justified by this bounded study. This
clears the keep/change ordering decision for #1244/#1245 and applicable
#589/#591; their implementation and exact-candidate acceptance remain required.

## Credit the delivered baseline

[SQL operations evidence](issue-255-candidate.md), delivered in
[PR #299](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/299), already
proves COPY_ONLY metadata, actual isolated restore using the backup's file list,
full DBCC CHECKDB and canonical row/hash equality. Its SQL CU14 image and source
identities remain historical; this study does not claim a new CU26 run of that
campaign. The current [toolkit](../../deploy/sql/backup-restore-checkdb.sql)
requires SQL Server 2022 and compatibility 160. Its isolated verification restore
is a primitive, not an implemented in-place LogicHost upgrade rollback command.

[#586's object qualification](logichost-filesystem-qualification-586.md) already
covers exact generation/type/time/length/hash inventory, exclusive offline
maintenance, durable prepared/committed restore markers and interrupted restore.
Its one-writer, same-host native Linux ext4 envelope and exact artifact identities
remain unchanged. Neither this SQL-only Docker-volume probe nor its neighboring
database check extends that envelope or proves central application coherence.

There is no delivered LogicHost upgrade lifecycle against which to measure
savings. SQL, object storage and mutable host state remain separate consistency
domains regardless of which SQL checkpoint primitive is chosen.

## Minimal native snapshot/revert probe

The [recorded result](evidence/issue-1608/native-snapshot-result.json) and
[executed SQL sequence](evidence/issue-1608/native-snapshot-probe.sql) describe
three sequential trials on one warm synthetic database: 4,096 rows with 1,024
payload bytes each (4 MiB payload), FULL recovery, compatibility 160, plus an
unrelated two-row neighboring database. Each trial first successfully takes a
normal full backup and log backup to establish a known chain, creates a native
database snapshot, rewrites all payloads, drops the predecessor table, creates
an incompatible candidate table, then reverts and verifies. This deliberate
mutation simulates failed candidate validation; no application is running.

The image is the repository fixture's
`mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04@sha256:ba4c8329f48fb8f02e1416be6a930ebfd71268caee78aa985f3af4315e457c89`.
The engine reported `16.0.4265.3`, RTM, Developer Edition (64-bit), Ubuntu 22.04.
Host: Linux `6.8.0-142-generic` x86_64, Docker `29.8.0`, driver `overlayfs`.
The newly provisioned service had a private disposable volume, two CPUs, 3 GiB
memory limit, `--network none` and no published ports. No shared service was
accessed. Cached image and bundled sqlcmd required no downloads. A first startup
attempt timed out resolving `localhost`; it was stopped and its exact container
and volume removed, then the successful run used `tcp:127.0.0.1,1433`. Startup
and image provisioning were not timed. Credentials were generated privately and
passed through environment variables; none are in these artifacts.

| Observation | Trial 1 | Trial 2 | Trial 3 |
| --- | ---: | ---: | ---: |
| Snapshot creation, ms | 101.20 | 103.64 | 121.83 |
| Incompatible mutation, ms | 620.93 | 710.38 | 681.47 |
| Revert including single/multi-user transitions, ms | 321.09 | 358.82 | 308.12 |
| Full CHECKDB plus semantic verification, ms | 216.68 | 191.73 | 168.15 |
| Sparse bytes before mutation | 16,384 | 16,384 | 16,384 |
| Sparse bytes after mutation | 5,173,248 | 5,181,440 | 5,181,440 |

Times include docker exec, connection and sqlcmd overhead; no baseline comparison,
mutation-overhead attribution, production-scale RTO, CPU/RSS/I/O, ingress pause
or backlog claim follows. Full CHECKDB passed, all predecessor payloads/generations
returned, the incompatible table disappeared and the two expected neighboring
rows remained in all three trials. Every immediate post-revert log backup failed
with error 4214 (no current database backup) and 3013. The next trial's full backup
and successful log backup show reseeding, not chain continuity. Both successful
run resources were removed; first-attempt cleanup was also verified.

## Benefit screen and stop decision

Microsoft's [2022 Linux edition matrix](https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-editions-and-components-2022?view=sql-server-ver16)
supports database snapshots on Enterprise/Standard (Developer exposes Enterprise
features), but not Web/Express. Only Developer was exercised here. Native
snapshots need source-instance storage and copy-on-write capacity and do not
provide independent corruption/media recovery. [Revert restrictions](https://learn.microsoft.com/en-us/sql/relational-databases/databases/revert-a-database-to-a-database-snapshot?view=sql-server-ver16)
include a single remaining snapshot and eligible online files/filegroups; revert
breaks the log backup chain, requiring a new full/file backup before log backups.
These are additional preflight, recovery and test obligations. The probe confirms
the chain cost, while leaving all object/config/key coordination work in place.
Fast SQL checkpoint creation alone establishes no net end-to-end benefit.

[COPY_ONLY](https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/copy-only-backups-sql-server?view=sql-server-ver16)
preparation leaves the existing differential base intact and reuses delivered
restore checks. It copies the database and consumes backup space/time; this is a
cost to measure on the actual release dataset, not an assertion it is faster.
Any actual recovery still needs DBA-owned recovery-fork/log-backup validation;
keeping COPY_ONLY does not promise arbitrary point-in-time continuity.

[T-SQL snapshot backups](https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/create-a-transact-sql-snapshot-backup?view=sql-server-ver16)
require actual underlying data/log storage snapshots in addition to metadata.
No storage-provider access or isolated snapshot-capable backing topology was
provisioned. Adding provider orchestration, suspension cleanup and restore mapping
has no demonstrated benefit here. No suspension was requested and no metadata-only
backup was mistaken for contents. That alternative is **unqualified and unrun**.

Stop after this first candidate: retain the existing mechanisms. Native revert
is feasible on the tested SQL fixture, but the study does not establish sufficient
overall savings to adopt it. No broader backend matrix, coherent candidate
campaign or speculative framework is warranted merely to close the decision.

## Integration handoff for #1244/#1245

Use one admission fence and cutoff for exactly this instance's application
database, complete verified offline object backup, private config/secrets,
certificates, Identity/OpenIddict state and Data Protection key ring. Bind SQL
backup identity/LSNs and hashes to object bucket/key/generation/type/time/length/
SHA-256, instance UUID, exact verified predecessor image/Compose/config selection
and pinned current/historical immutable catalogs. Reuse COPY_ONLY and existing
object backup/restore verification; a short-lived local upgrade point need not be
an encrypted off-host disaster-recovery archive. Explicit disaster recovery in
#1244 retains its separate encryption/off-host retention contract.

The current object tool makes a completed copy. Do not substitute references to
live generations unless a qualified persistent pin prevents normal retention
from deleting them through recovery; that optimization is not selected here.
Neither a DB/file restructure nor a new filesystem removes the external SQL to
object/config cutoff, so retain the current layout.

Keep an ownership-bound durable recovery journal outside all reverted roots.
Record intent, exact component identities, verified completion, restore progress
and decision before advancing; resume only the identical bound request while
all writers stay fenced. Preserve minimal non-rewindable external facts/retry
floors separately, or prove the fence prevents them changing. Candidate success
expires the point only after durable upgrade commit, then admits work and allows
owned checkpoint cleanup. Before commit, failure restores only the paired
predecessor; uncertainty retains journal/checkpoint material and remains fenced.
There is no post-commit rollback window or daily snapshot policy.

The existing tested SQL operator boundary is audited, time-limited sysadmin,
with service-account access to the private backup files; runtime receives no
such grant. Operate on the application database only, never suspend/restore a
shared server or change unrelated databases. Snapshot creation would additionally
need CREATE DATABASE authority and eligible service-owned sparse-file storage;
revert needs RESTORE authority. This probe used only its disposable service's sa.

An unavailable, partial, corrupt or space-exhausted checkpoint refuses protected
mutation. Do not infer CameraAgent's unprotected allowance for LogicHost. If
recovery fails, hold admission closed, retain all markers and give the operator
the exact checkpoint/component failure and bounded recovery-only retry/repair
instruction; an old image must never start on uncertain candidate state.

The implementation stories must still prove coherent SQL/object/config/key/auth
and lineage recovery, mixed-cutoff/missing-key rejection, sibling instance and
object isolation, interruption/resume and exact-candidate health before admitting
work. Those tests, full restore latency/capacity, backup encryption deployment,
catalog/image retention and minimum privilege refinement were **not run or
qualified here**. Reuse the credited evidence where applicable and qualify only
the remaining changed boundary; no new every-PR storage campaign is proposed.
