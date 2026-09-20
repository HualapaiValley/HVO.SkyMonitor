# SQL Server Operations

This runbook is the operator boundary for the shared SQL Server used by
SkyMonitor and the main website. The scripts in `deploy/sql` support SQL Server
2022 only and fail closed unless `ProductMajorVersion=16`; database scripts also
require compatibility level 160. Do not bypass either check for an older engine
or compatibility level. Upgrade and revalidate instead.

The scripts emit aggregate measurements and approved attribution labels only.
Never redirect raw Query Store text/plans or Extended Events XML into tickets,
logs, build artifacts, or object storage. Evidence must not contain SQL text,
plans, raw XML, parameters, login/host names, paths, database/session/query/plan
IDs, lock resources, secrets, or connection strings.

## Owners And Permissions

Use separate credentials and audited elevation. LogicHost runtime and migration
roles receive none of these permissions.

| Owner | SQL Server 2022 permissions | Purpose |
| --- | --- | --- |
| Database diagnostics operator | `VIEW DATABASE PERFORMANCE STATE` and `VIEW PERFORMANCE DEFINITION` in the target database; no standing `ALTER`, `VIEW SECURITY DEFINITION`, or server diagnostic permission | Capture/inspect Query Store and read its cooperative ownership marker. A security owner grants `ALTER` only for configure and rollback as described below. |
| Instance diagnostics operator | Standing `VIEW SERVER PERFORMANCE STATE` at server scope; `CONNECT`, `VIEW DATABASE PERFORMANCE STATE`, `VIEW PERFORMANCE DEFINITION`, and `REFERENCES` on each observed application schema in the target database; no standing `ALTER ANY EVENT SESSION`, `ALTER TRACE`, `VIEW SECURITY DEFINITION`, database `ALTER`, data read, or server `ALTER SETTINGS` | Read sanitized XE, sample shared capacity except default-trace history, and observe statistics/index metadata. A security owner grants mutation/default-trace permissions only for the exact operations below. |
| Instance configuration owner | `ALTER SETTINGS` | Set and restore `blocked process threshold (s)`. This is not granted to the collector. |
| Recovery operator | Dedicated, audited, time-limited membership in `sysadmin` | Fail if the exact backup already exists; read the explicit invariant columns; identify exact backup/media metadata; run backup, `RESTORE VERIFYONLY`, isolated restore, `DBCC CHECKDB`, invariant comparison, and drop. Testing proved that `BACKUP DATABASE` plus `CREATE ANY DATABASE` is insufficient because restore preserves the source owner and the restoring login cannot run CHECKDB as `db_owner`. Remove the login or role membership immediately after the drill. |
| SQL service account owner | Write/read/delete on the approved encrypted SQL backup directory and XE directory | SQL Server performs file I/O. The database login does not receive operating-system access. |
| Object-store backup owner | Read and restore the versioned LogicHost object-store backup set and manifest | Coordinates object consistency with the SQL recovery operator. No SQL permission is implied. |

Permission setup is platform-owned, not an application migration. Do not add
these grants to `hvo_logichost_runtime`, `hvo_logichost_migrator`, startup, or EF
migrations. On SQL Server 2022 use `VIEW SERVER PERFORMANCE STATE` and
`VIEW DATABASE PERFORMANCE STATE`, not a silent fallback to the older broad
`VIEW SERVER STATE`/`VIEW DATABASE STATE` names.

## SQLCMD Safety

Use current `sqlcmd` with encryption and certificate validation configured by
the deployment platform. Keep credentials outside shell history. Generate one
lowercase 32-hex `RUN_ID` per collection. Database names, run IDs, retention
values, and file locations are validated before dynamic SQL. Supply a canonical
absolute approved directory, a safe fixed prefix, and the exact derived path.
Relative, UNC, URI, control-character, dot-segment, repeated-separator,
wildcard, noncanonical, and out-of-root paths are rejected textually. The
platform must supply the allowlisted canonical root and verify its ownership,
permissions, and resolved filesystem target; SQL Server cannot prove symlink
containment. Create a mode-0600 `query-store.vars.sqlcmd` in restricted operator
state containing the complete reviewed capture and target values; never
reconstruct rollback input from memory:

```text
:setvar DatabaseName "SkyMonitor"
:setvar QueryStoreToken "0123456789abcdef0123456789abcdef"
:setvar OriginalDesiredState "OFF"
:setvar OriginalActualState "OFF"
:setvar OriginalReadonlyReason "0"
:setvar OriginalMaxStorageMb "100"
:setvar OriginalRetentionDays "30"
:setvar OriginalIntervalMinutes "60"
:setvar OriginalMaxPlansPerQuery "200"
:setvar OriginalFlushSeconds "900"
:setvar OriginalCaptureMode "AUTO"
:setvar OriginalCleanupMode "AUTO"
:setvar OriginalWaitStatsMode "OFF"
:setvar MaxStorageMb "512"
:setvar RetentionDays "7"
:setvar IntervalMinutes "15"
:setvar MaxPlansPerQuery "50"
:setvar ConfiguredMaxStorageMb "512"
:setvar ConfiguredRetentionDays "7"
:setvar ConfiguredIntervalMinutes "15"
:setvar ConfiguredMaxPlansPerQuery "50"
```

Example invocations omit authentication:

```bash
sqlcmd -S "$SQL_SERVER" -d "$DATABASE" -b \
  -v DatabaseName="$DATABASE" -i deploy/sql/query-store-capture.sql
sqlcmd -S "$SQL_SERVER" -d "$DATABASE" -b \
  -i query-store.vars.sqlcmd -i deploy/sql/query-store-configure.sql
sqlcmd -S "$SQL_SERVER" -d "$DATABASE" -b \
  -i query-store.vars.sqlcmd -v WindowMinutes=60 -i deploy/sql/query-store-inspect.sql
sqlcmd -S "$SQL_SERVER" -d "$DATABASE" -b \
  -i query-store.vars.sqlcmd -i deploy/sql/query-store-rollback.sql

scripts/sql-xe:create \
  --root /approved/sql-xe --root-owner mssql --service-user mssql \
  --run-id "$RUN_ID" --server "$SQL_SERVER" --database "$DATABASE" \
  --credential-file /run/skymonitor/sql-xe/sqlcmd.credentials \
  --include-instance-deadlocks

sqlcmd -S "$SQL_SERVER" -d master -b -i deploy/sql/backup-restore-checkdb.sql \
  -v DatabaseName="$DATABASE" RunId="$RUN_ID" \
  ApprovedBackupDirectory="/approved/sql-backup" BackupFilePrefix="hvo-backup-" \
  BackupFilePath="/approved/sql-backup/hvo-backup-$RUN_ID.bak" \
  InvariantSchema="dbo" InvariantTable="$INVARIANT_TABLE" \
  InvariantKeyColumn="$INVARIANT_KEY" InvariantTextColumn="$INVARIANT_TEXT" \
  InvariantNumberColumn="$INVARIANT_NUMBER" \
  MetadataRetentionDeclaredDays="$MSDB_DAYS" \
  MetadataRetentionControlReference="$MSDB_CONTROL_REFERENCE"
```

`-b` is mandatory so validation and permission failures stop the operation.
Never print the expanded command or connection environment. The integration
harness also executes the shipped capture script through the image's real
`mssql-tools18/sqlcmd` with an adversarial `-v` value and proves that escaping
fails closed without creating the injected database.

## Query Store

Before enabling, execute `query-store-capture.sql` and save its single sanitized
row inside the restricted change record. Capture `desired_state_desc`,
`actual_state_desc`, `readonly_reason`, `max_storage_size_mb`, `stale_query_threshold_days`,
`interval_length_minutes`, `max_plans_per_query`,
`flush_interval_seconds`, `query_capture_mode_desc`,
`size_based_cleanup_mode_desc`, and `wait_stats_capture_mode_desc`. These are
configuration values, not exported diagnostics. Stop if the original capture
mode is `CUSTOM`, a complete rollback record is not available, or desired and
actual state differ for any reason. Dynamic desired `READ_WRITE`/actual
`READ_ONLY` state is observation-only and must be resolved separately.

Pass every captured value and one new lowercase 32-hex token to
`query-store-configure.sql`. Under a transaction applock it atomically rejects an
existing owner and creates database extended property
`HvoSqlOps_QueryStoreOwner`; this persistent marker, not a session applock,
spans separate SQLCMD sessions and the complete collection window for compliant
toolkit operators. It does not block a principal with arbitrary database
`ALTER`; control that permission separately. Configure
then compares every captured option and fails if any state or option changed.
`query-store-inspect.sql` requires the same token. Configure enables
`READ_WRITE`, `AUTO` capture, wait capture,
size-based automatic cleanup, and operator-supplied bounds. The reviewed initial
production values are 512 MB, seven stale days, 15-minute intervals, and 50
plans per query; change them only through a measured review. Query Store adds
database writes, memory use, and storage pressure. Review current/max storage,
readonly reason, capture success, application latency, CPU, log generation, and
I/O after enablement.

Collect only `query-store-inspect.sql`; it aggregates execution count, duration,
CPU, logical reads/writes, and plan variation without projecting query or plan
content. Disable collection by running `query-store-rollback.sql` with every
captured original value and every expected configured value. Rollback validates
all original values even for `OFF`, then under the applock validates the exact
plain marker and complete expected toolkit state before changing the marker to
`<token>:rollback` immediately before restoration. Wrong expected values or
state drift leave the plain marker unchanged so corrected input can retry.
Rollback restores and compares every option while enabled, applies the original
mode, compares every semantically observable state and option again, and removes
the marker only after full success. `CUSTOM` is rejected before alteration
because its full policy is not captured. Configure and rollback convert engine
failures to bounded stage codes; they never rethrow raw engine text. Do not clear
or purge Query Store as rollback.

The security owner must execute and audit this elevation sequence. Capture the
baseline with effective database `ALTER` denied. Grant `ALTER` immediately before
configure, verify `HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER')=1`, run
configure, then immediately revoke and verify it returns `0`. Keep `ALTER`
effectively denied for the complete inspect window. Immediately before rollback,
grant and verify it again; immediately after rollback, revoke and verify denial.
On any command failure, revoke first, retain the marker according to its state,
and follow abandoned-marker recovery. Do not grant `ALTER` to runtime or
migration identities.

An abandoned marker is an incident, not permission to delete it. Stop new
toolkit runs, retrieve the exact restricted variable record, and have the
database owner inspect the marker and every Query Store option while holding
transaction applock `HvoSqlOps.QueryStore.Owner`. If the marker is the plain
token and the complete toolkit state matches, run normal rollback. If it is
`<token>:rollback`, compare every option with both the recorded baseline and
toolkit state. Remove that exact marker under the applock only after the baseline
is already fully restored and independently verified. If neither state matches,
retain the marker, restore from the change record under DBA control, verify all
values, then remove only the exact marker. Never overwrite a marker or infer a
baseline from current state.

## Blocking And Deadlocks

`blocked process threshold (s)` is server-wide and its minimum useful value is
five seconds. The instance configuration owner must record both
`show advanced options` and the threshold, set a reviewed value of at least 5,
and restore both values in a `finally`/incident-close step. Never change it in a
shared-instance automated test.

Invoke XE creation only through `scripts/sql-xe:create`. It validates a canonical
nonsymlink platform-owned root, configured owner, mode with no group/world write,
and SQL service-account read/traverse access. It takes an exclusive OS lock for
the complete preflight and SQLCMD invocation, rejects unsafe arguments, and
atomically creates mode-0700 `<root>/hvo-xe-<RUN_ID>` for the SQL service
identity. Reuse therefore fails before SQL. Production mode invokes real
`sqlcmd` without placing credentials in arguments or output, discards raw command
output on failure, and verifies the sanitized result plus an owned regular event
file before success. The credential file is a canonical nonsymlink mode-0600
regular file owned by the wrapper identity; it contains exactly the SQLCMD user
on line one and password on line two and is never printed.

The wrapper cannot reliably prove that an arbitrary canonical path is backed by
protected ephemeral storage. The platform must create the credential file under
an owner-only tmpfs/runtime directory, issue a dedicated short-lived credential
whose expiry does not exceed the reviewed collection window, and record that
expiry. The owning platform deletes the file immediately after the final
create/read/drop or cleanup use and then disables or rotates the credential at
collection close. Persistent home, repository, application-data, backup, and
shared temporary directories are prohibited. File-mode validation is not a claim
that this lifecycle was enforced.

Every create, cleanup drop, and absence-verification `sqlcmd` process runs under
`env -i` with only fixed `PATH`, fixed `LC_ALL`, and the two validated credential
values. The wrapper supplies explicit security mode, batch/severity behavior,
login timeout, and statement timeout flags. Inherited `SQLCMDINI`,
`SQLCMDERRORLEVEL`, endpoints, databases, credentials, timeouts, packet size, and
format controls cannot affect startup SQL, routing, or failure handling.

Before invoking `sqlcmd`, production mode arms its failure trap. Any unsuccessful
or uncertain SQL result, sanitized-output parse failure, or file ownership
postcheck failure attempts `xe-drop.sql` for exactly `HvoSqlOps_<RUN_ID>`, then
independently verifies session absence. Only after absence is proven does it
remove the inode-validated `<root>/hvo-xe-<RUN_ID>` directory. SQL, cleanup, and
verification output are discarded. A fixed cleanup-complete message preserves
the original nonzero status; cleanup that cannot prove both absence and directory
removal emits a distinct fixed message and exits `3`. Successful creation leaves
the requested session and files untouched.

`--preflight-only` runs the same root, lock, argument, and atomic-directory logic
without SQLCMD and is intended only for platform validation/tests. The lock is
cooperative: every writer to the trusted root must use this wrapper, and the
platform must protect root ownership and mount/symlink administration. The
wrapper cannot defend against a privileged process that ignores the lock or
replaces the trusted filesystem boundary.

The security owner grants `ALTER ANY EVENT SESSION` immediately before wrapper
production create and retains it only until the wrapper returns, because the
failure trap may need to stop/drop an uncertain session. Revoke immediately and
verify effective denial for the collection window. The tested `xe-read-sanitized.sql`
minimum does not require this permission. Grant it again only around an exact
stop or `xe-drop.sql`, revoke immediately afterward, and verify denial. Grant
`ALTER TRACE` only around `capacity-snapshot.sql` default-trace collection,
revoke immediately, and verify denial before maintenance or other collection.

`xe-create.sql` creates only `HvoSqlOps_<RUN_ID>`, starts it with
`STARTUP_STATE=OFF`, and cleans up that exact session if startup fails.
`blocked_process_report` is filtered by the event's target database ID.
`xml_deadlock_report` has no reliable database predicate, so the wrapper requires
explicit `--include-instance-deadlocks`; the session must not be described as
database-isolated. Its event files are bounded to two 5 MB files. Raw XML
contains statements, parameters, identities, paths, and lock resources. It may
be read only inside the restricted SQL boundary or process memory and must be
reduced immediately to event kind, complete normalized participant set,
single/mixed/other attribution, UTC range, and count. A deadlock involving
runtime and migration is `mixed-approved` with both participant labels; any
approved/unapproved combination is `mixed-approved-other`. Use
`xe-read-sanitized.sql` for the export.

After collection, run `xe-drop.sql`, then have the platform remove only files in
the exact `<root>/hvo-xe-<RUN_ID>` directory and remove that directory. Verify no
server event session, event file, or run directory remains. Never use a broad
root wildcard. Target replacement is checked with binary exact filename
equality. Startup cleanup reports a distinct sanitized cleanup failure and never
reports success while the session remains. Normal collection is 15 minutes or less; stop early if file
rollover, CPU, I/O, or latency changes materially.

Review blocked/deadlock events with the Query Store aggregate and application
telemetry. Escalate repeated events, any migration attribution during normal
runtime, sustained worker/grant pressure, storage exhaustion, or a failed
cleanup to the SQL owner and SkyMonitor owner. Raw XML remains restricted even
during escalation.

## Capacity Snapshot

Run `capacity-snapshot.sql` from `master` with `DatabaseName`. Session, request,
transaction, lock, and grant occupancy is grouped by the stable #254 application
name allowlist. `other` is not attributed to SkyMonitor. Waits, workers, cached
query spills, memory grants, cumulative SQL process CPU, physical/process memory,
configured/committed/target SQL memory, effective/configured worker capacity,
tempdb/version store, target-database file/log I/O, log reuse, autogrowth
history, and used/free/headroom values are explicitly labeled
instance-wide, instance-history, instance-cache, or target-database. Shared
metrics cannot be claimed as SkyMonitor usage. Volumes touched by target files
are deduplicated internally by mount point and summed once into a sanitized
shared aggregate; its arithmetic is `total = used + free`. File-type rows do not
pretend one minimum-volume value represents a multi-volume set.
All empty aggregate sums are emitted as zero. Current sample times are UTC;
default-trace autogrowth times are explicitly labeled server-local because
`StartTime` cannot be safely converted without platform timezone context.

Record the SQL engine/version, image or deployment revision, topology,
application revision, and UTC sample range beside the sanitized rows. Compare
equivalent workload windows. The toolkit defines no alert or capacity threshold;
the SQL owner sets site-specific alerts from measured baseline, service SLOs,
growth rate, and recovery time. Review alerts before changing pool, timeout,
storage, index, or isolation settings.

## Backup And Consistency Fence

SQL rows and stored objects form one application state but are not one atomic
backup. The incident/change owner establishes this consistency fence:

1. Quiesce LogicHost writers and workers, verify no attributed active request or
   open transaction, and record the fence start UTC.
2. The object-store owner produces a verified LogicHost object-store backup and records the
   manifest revision and completion UTC. Never place SQL backups in an
   application bucket.
3. The ephemeral recovery operator runs `backup-restore-checkdb.sql` with the
   canonical approved backup directory, fixed prefix, exact unique `.bak` path,
   and explicit stable `int` key, `nvarchar` text, and `int` numeric invariant
   columns. It performs `BACKUP ... WITH COPY_ONLY, FORMAT, CHECKSUM`, identifies
   exactly one new full-database backup, media name/family, path, and COPY_ONLY
   metadata without comparing local `msdb` timestamps to UTC, runs
   `RESTORE VERIFYONLY`, obtains logical files from that exact backup with
   `RESTORE FILELISTONLY`, builds `MOVE` only from that list, performs an actual
   uniquely named restore, full
   `DBCC CHECKDB`, and row-count/canonical SHA-256 comparison, then drops only
   that restore. Close the recovery connection, kill any residual sessions, and
   drop or disable the recovery `sysadmin` login immediately after this block;
   verify absence before any later collection cleanup. Final cleanup remains
   idempotent if the login is already absent.
4. Record backup completion, bytes, actual restore completion, CHECKDB result,
   invariant result, and fence end UTC. Resume writers only after both owners
   sign the same fence record.
5. Measure RPO as the age of the newest jointly restorable SQL/object-store fence at the
   incident time. Measure RTO from restore authorization through SQL restore,
   CHECKDB, object-store restore, reconciliation, and service-ready verification. Do not
   publish a target as achieved without this measured drill.
6. Retain the encrypted backup under the approved backup policy, or have the SQL
   service account owner delete that one exact run-owned `.bak` after an
   evidence-only drill. Verify the restore database, its files, and temporary
   backup are absent when policy requires deletion. Exact `msdb` backup/media
   rows are intentionally retained audit history; no bound is established by the
   SQL declaration itself. Before execution,
   the platform owner must separately verify an enabled authoritative SQL Agent
   job and enabled schedule invoke the reviewed backup-history cleanup procedure
   at the recorded bound, or establish a dated manual control when SQL Agent is
   unavailable. Record retention days and the authoritative job/change reference
   as separate platform evidence. The SQL script accepts those values only as a
   sanitized declaration and emits `external-control-declared`, declared days,
   and a digest of the reference. It does not verify, authenticate, or approve a
   deployed control. Missing external verification is a fail-closed operator
   prerequisite even when the declaration is syntactically valid. Recheck the
   oldest retained `msdb.dbo.backupset.backup_finish_date` after the next policy
   run and fail the control if it exceeds the reviewed server-local bound. This
   toolkit never performs unsafe broad history deletion or claims exact metadata
   cleanup.

For a SQL Agent control, use its `job_id` text as the control ID and perform this
restricted preflight. Require exactly one enabled job, at least one enabled
schedule, a nonzero next run, and a reviewed step whose exact command hash is in
the platform control record and whose bound equals
`MetadataRetentionDeclaredDays`:

```sql
DECLARE @control uniqueidentifier = TRY_CONVERT(uniqueidentifier, @MetadataRetentionControlReference);
SELECT jobs.[enabled] AS [job_enabled], schedules.[enabled] AS [schedule_enabled],
    job_schedules.[next_run_date], job_schedules.[next_run_time], steps.[step_id],
    CONVERT(char(64), HASHBYTES('SHA2_256', CONVERT(varbinary(max), steps.[command])), 2) AS [command_sha256]
FROM msdb.dbo.sysjobs AS jobs
JOIN msdb.dbo.sysjobsteps AS steps ON steps.[job_id] = jobs.[job_id]
JOIN msdb.dbo.sysjobschedules AS job_schedules ON job_schedules.[job_id] = jobs.[job_id]
JOIN msdb.dbo.sysschedules AS schedules ON schedules.[schedule_id] = job_schedules.[schedule_id]
WHERE jobs.[job_id] = @control;
```

If these checks or the reviewed command/bound comparison fail, do not run the
backup drill. A manual control must instead record its owner, expiry, exact
deletion command, execution evidence, and the same post-run oldest-history
verification. The dedicated integration harness does not claim a production job:
after proving its exact retained rows, it safely calls
`sp_delete_database_backuphistory` only for its ephemeral test-owned source
database and verifies exact `backupset`, media-family, and media-set absence.

The invariant serialization is length-delimited, explicitly ordered by the
stable key, and hashed with SHA-256. The script independently rejects null and
duplicate values, then requires an enabled, non-hypothetical, unfiltered unique
index whose only key column is that key. `CHECKSUM_AGG(BINARY_CHECKSUM(*))` is not an
accepted recovery invariant. Safe failure output reports only the script and a
bounded stage number; detailed SQL diagnostics stay in the restricted server
boundary.

Never substitute `RESTORE VERIFYONLY` for an actual restore or skip object-store
ownership. A SQL-only or object-store-only backup is not a consistent SkyMonitor
recovery point.

## Statistics, Indexes, And Isolation

`maintenance-observe.sql` returns aggregate statistics age/modification state,
limited physical index observations, and usage counters. Statistics timestamps
are labeled server-local. Physical rows are limited to leaf-level `IN_ROW_DATA`,
aggregated per partition, then per index before one join to usage counters. It
has no thresholds,
recommendations, `UPDATE STATISTICS`, rebuild, reorganize, or index creation.
Review the exact production query, plan variation, lock behavior, I/O, write
cost, backup impact, and equivalent before/after workload before approving
maintenance. Roll back a separately approved index change with its reviewed
inverse DDL; roll back statistics changes through the approved backup/restore or
captured statistics procedure for that change. Universal fragmentation or stale
statistics thresholds are rejected.

RCSI is explicitly deferred and rejected by this runbook. Do not enable it from
blocking evidence alone. A separate change must prove lock-wait benefit against
shared tempdb/version-store bytes, transaction duration, correctness, recovery,
and equivalent workload evidence, with an explicit rollback. `NOLOCK`, blanket
retries, speculative indexes, and automatic maintenance are not alternatives.
