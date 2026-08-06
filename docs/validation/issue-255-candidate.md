# Issue 255 SQL Operations Candidate Evidence

## Scope

Issue #255 adds an operator-only SQLCMD toolkit and runbook for SQL Server 2022
Query Store, blocked-process/deadlock Extended Events, shared-instance capacity,
statistics/index observation, and executable backup/restore consistency checks.
Production application code, EF migrations, and runtime/migration grants are
unchanged. RCSI, automatic maintenance, universal thresholds, retries, and
speculative indexes remain rejected or deferred.

## Environment And Method

- Base revision: merged #254 at `49a6cd8f6522c4a4f5696174e624d488a5c37350`.
- SQL image: `mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04@sha256:c1aa8afe9b06eab64c9774a4802dcd032205d1be785b1fd51e1c0151e7586b74`.
- Topology: one digest-pinned, test-owned SQL Server container, separate from
  `AssemblyHooks` and destroyed after the non-parallel test.
- Workload: one unique compatibility-level-160 database; Query Store canary
  executions; one writer blocked beyond the five-second server minimum; one
  deterministic two-row 1205 deadlock; capacity and maintenance snapshots; and
  a two-row explicit canonical SHA-256 invariant through backup, verify-only, actual restore,
  full CHECKDB, comparison, and drop.
- Raw Query Store text and XE XML are read only in test memory. Exported result
  sets and console evidence pass case-insensitive scanning for canaries, run and
  database names, login, password, paths, raw payload element names, and SQL/plan
  field names.

## Available Evidence

- Focused Release project build: passed warning-clean with zero warnings and
  zero errors.
- Corrected-head focused Integration test
  `SqlOperationsIssue255Tests.OperatorToolkit_ProducesSanitizedEvidenceAndExactCleanup`:
  passed 1/1 in 43 seconds on the final corrected scripts, wrapper, and harness.
  It proves
  strict result schemas/content for every shipped script, Query Store baseline
  desired/actual/readonly restoration, nonzero canary metrics, target-database
  blocked capture, a mixed runtime/migration 1205 deadlock, complete participant
  attribution, shared CPU/memory/worker/headroom capacity, maintenance reads,
  exact COPY_ONLY backup metadata, `RESTORE VERIFYONLY`, unique actual restore
  with `RESTORE FILELISTONLY`-derived `MOVE`, full `DBCC CHECKDB`, canonical
  SHA-256 equality, exact run-owned SQL/file cleanup, and retained exact `msdb`
  metadata with an externally declared but unverified retention control. No
  authoritative production control evidence is present, so no production bound
  is claimed. The dedicated test then invokes
  `sp_delete_database_backuphistory` only for its ephemeral source and verifies
  exact backup-set, media-family, and media-set absence.
- Query Store scenarios cover atomic persistent-token acquisition, stale and
  concurrent owner rejection, wrong-token rollback rejection, bounded configure
  and rollback error codes, complete configured-state checks before transition,
  corrected retry after drift, unconditional invalid-value rejection for `OFF`,
  and all-option comparison while enabled and after returning to `OFF`.
  Successful rollback exactly releases the marker. The distinct diagnostic
  principal proves `ALTER` denied for capture/inspect, temporary grant for each
  mutation, and immediate effective denial afterward. Plan variation counts only
  plans observed in the selected runtime window.
- Forced invalid configuration, omitted instance-wide selection, malformed XE
  paths, replaced XE targets, nullable/duplicate keys, disabled/filtered ordering
  indexes, absent retention declaration, and XE startup-permission failures
  produce sanitized exceptions. Startup cleanup
  verifies that no run-owned session remains. Raw query text and XML are never
  passed to assertion messages or serialized.
- The actual `scripts/sql-xe:create` wrapper runs both preflight and production
  modes in the digest-pinned container against a dedicated mode-0750
  `mssql`-owned root and real `mssql-tools18/sqlcmd`. Production success leaves
  the exact session/files. Injected post-SQL parse and ownership-postcheck
  failures each stop/drop the exact session, independently verify absence, remove
  the exact run directory, expose no raw output, and return the fixed
  cleanup-complete failure. The lock remains cooperative against privileged
  nonconforming writers.
- Actual wrapper production success runs with malicious inherited SQLCMD startup,
  severity, endpoint, database, credential, timeout, packet, header, and separator
  controls. The wrapper's minimal allowlisted environment and explicit flags
  ignore them; the malicious startup XE session is absent, requested creation
  succeeds, and no credential content enters output. Ephemeral credential
  placement, expiry, deletion, and disable/rotation remain external platform
  prerequisites and are not claimed as wrapper evidence.
- Separate database and instance diagnostic logins prove broad and cross-scope
  authority denials, including denied database `VIEW SECURITY DEFINITION` while
  the scripts run with `VIEW PERFORMANCE DEFINITION` and schema `REFERENCES`.
  Effective users provisioned through the shipped runtime and
  migration role scripts prove denial of Query Store, XE, backup, and server
  diagnostics without changing production grants.
- The digest-pinned container's real `mssql-tools18/sqlcmd` executes the shipped
  capture script with an adversarial SQLCMD variable; escaping fails closed and
  the injected database remains absent. The in-process expander is used only to
  collect result sets from the other scripts.
- Capacity checks enforce non-null zero-safe sums, shared scope, and
  total/used/free/headroom arithmetic; target volumes are deduplicated internally
  by mount point. Server-local history fields are not mislabeled UTC.
  Maintenance aggregates leaf/in-row rows per partition and index so usage
  counters are counted once.
- The instance operator starts with `ALTER ANY EVENT SESSION` and `ALTER TRACE`
  denied. The harness grants/revokes event-session mutation around each wrapper
  create/failure-cleanup, stop, and drop; sanitized XE read succeeds while
  denied. `ALTER TRACE` is granted only for the capacity snapshot and is denied
  immediately afterward.
- Recovery ran under a dedicated ephemeral `sysadmin` login. A preceding
  granular-permission trial proved `BACKUP DATABASE` plus `CREATE ANY DATABASE`
  insufficient at CHECKDB because restore preserves the source owner; the
  runbook now states the tested sufficient boundary rather than claiming the
  unproven narrower grant set. The harness closes its connection, clears the
  pool, kills residual sessions, drops the recovery login immediately after the
  drill, and verifies absence before later rollback/cleanup work.
- Final cleanup restores and verifies each server configuration value in an
  independent guarded operation, then independently attempts SQL cleanup and
  exact file cleanup, aggregating sanitized failures without skipping later
  operations.
- Warning-clean solution Release build: passed with zero warnings and zero errors.
- `dotnet format --verify-no-changes`: passed.
- Package audit: passed.
- Category audit: passed exactly with Unit 1,731, Integration 528, Manual 75,
  Soak 1, External 0, and Hardware 1. The central Integration project is 355.
- The pre-review candidate had green replacement Unit and Integration shards,
  but those runs predate the independent-review harness/script corrections and
  are not claimed as corrected-head evidence. Replacement full category and
  protected CI runs remain required before merge.

## Candidate Gate

The requested local candidate gates are complete. Architecture/publish,
migrations, canonical merged coverage enforcement, independent
operational/security review, and protected current-head CI remain PR merge gates
and are not claimed by this uncommitted worktree record.

## Performance And Runtime Disposition

This is a bounded operator harness, not a production hot-path change. Production
CPU, memory, throughput, latency, backlog, and I/O comparison are `N/A` because
no production code or runtime configuration changed. The representative drill
records bounded wall time, two 5 MB XE rollover files, one unique backup, exact
run-owned SQL/file cleanup, externally declared but unverified `msdb` retention,
and explicitly shared capacity fields. Production overhead must be measured in the runbook's
controlled enable/collect/disable window before site-specific alerts or capacity
decisions.
