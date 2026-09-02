# LogicHost Database Initialization

Production LogicHost separates database initialization from ordinary runtime.
The initialization command is the only application process that receives the
migration connection string or DDL authority. Runtime validates the exact EF
migration set, durable initialization state, and absence of effective DDL
authority before serving requests.

## Pre-Release Support Boundary

Until the first LogicHost product release, this procedure supports only a
precreated empty LogicHost database. Prior unreleased schemas and data are
disposable; initialization does not upgrade, downgrade, backfill, or converge
them. The candidate contains one canonical initial EF migration. Its migration
ID and `DatabaseInitializationState` target identify the exact current schema
and initialization provenance; they do not promise compatibility with an
earlier unreleased database. Same-revision retries, idempotent SQL, locking,
seed convergence, role separation, and runtime fail-closed validation remain
required. Recreate a superseded pre-release database instead of editing EF
migration history.

## Principals And Configuration

Provision the database and two externally managed logins or managed identities
before deployment. Map them to distinct database users; do not make either user
`sysadmin`, `securityadmin`, `db_owner`, `db_ddladmin`, `db_securityadmin`, or
the owner of `dbo`.

For SQL logins, a database administrator creates the users after the logins are
created through the approved secret-management process:

```sql
USE [SkyMonitor];
CREATE USER [HVO.LogicHost.Migrator] FOR LOGIN [HVO.LogicHost.Migrator];
CREATE USER [HVO.LogicHost.Runtime] FOR LOGIN [HVO.LogicHost.Runtime];
```

For Microsoft Entra identities, use `CREATE USER [...] FROM EXTERNAL PROVIDER`
instead. Integrated-authentication deployments use the corresponding Windows
login mapping. The role scripts intentionally do not create server logins or
embed authentication secrets.

| Purpose | Configuration | SQL application name |
| --- | --- | --- |
| Runtime | `ConnectionStrings:skymonitordb` | `HVO.SkyMonitor.LogicHost` |
| Initialize | `ConnectionStrings:skymonitordb-migrations` | `HVO.SkyMonitor.LogicHost.DatabaseInitialization` |

Production initialization never falls back to the runtime connection. Keep both
connection strings in the deployment secret store, never in images, source, or
the generated SQL. Do not expose the migration connection to runtime containers.
Development and Testing retain automatic initialization and may use one local
credential.

LogicHost parses each selected connection string before constructing EF or
dedicated object-lock connections. It replaces an absent, blank, or conflicting
`Application Name` with the purpose-owned value above while preserving valid
operator-supplied pool, connect-timeout, and connection-lifetime keywords. A
malformed string fails startup with a purpose-specific sanitized message; the
configured value and parser exception are not logged.

Issue #254 measured the default runtime policy at W4 concurrency 1/4/8 and
0/250/2,000 ms object-store delays. Peak attributable sessions were 3/9/17,
blocked requests remained zero, and no delayed stable lock window held an open
transaction. No tighter bound was justified, so the retained policy is SqlClient
defaults: pooling enabled, minimum/maximum pool size 0/100, connect timeout 15
seconds, EF command timeout 30 seconds, and connection lifetime 0. Dedicated
object-lock commands remain explicitly bounded at 5 seconds for a zero-timeout
attempt and 15 seconds for acquisition or release. Override connection policy
only with workload-equivalent evidence; `Application Name` is never
operator-overridable.

Apply the migration role before the first initialization, substituting reviewed
database-user names through SQLCMD variables:

```bash
SQLCMDPASSWORD="$SQL_ADMIN_PASSWORD" sqlcmd \
  -b -S "$SQLSERVER_HOST,$SQLSERVER_PORT" -d master -U "$SQL_ADMIN_USER" \
  -v DatabaseName=SkyMonitor MigrationUser=HVO.LogicHost.Migrator \
  -i deploy/sql/logichost-migration-role.sql
```

Use `-E` instead of `-U` for integrated authentication or `-G` for Microsoft
Entra authentication. Never place an administrator password on the command
line.

The migration role has DML and schema alteration only in `dbo` plus `CREATE
TABLE`; it is not a database or server administrator. SQL Server permits
`sys.sp_getapplock` and `sys.sp_releaseapplock` to database `public`, which both
roles retain.

## Preflight And Evidence

Before the first pre-release initialization of a target database:

1. Confirm the target database is precreated and empty, with no application schema or EF migration history, and that no restore, schema change, runtime replica, or other initializer is active.
2. Record the candidate's canonical migration ID, database data/log size, free space, autogrowth settings, and volume headroom.
3. Generate the canonical idempotent SQL and SHA-256 from the exact candidate revision.
4. Review the initial DDL, constraints, defaults, indexes, triggers, provider-specific annotations, and expected lock/log growth.

For an approved same-revision rerun or retry, instead confirm that the existing
schema, migration history, and durable initialization state are attributable to
that exact revision and that no restore, schema change, runtime replica, or other
initializer is active. Recreate an empty database when that attribution cannot
be established.

Generate the review artifact outside tracked source:

```bash
./scripts/logichost:generate-migration-sql TestResults/logichost-idempotent.sql
sha256sum --check TestResults/logichost-idempotent.sql.sha256
```

Record the Git revision, SQL hash, canonical target migration ID, data/log before
values, free volume space, reviewer approval, and either empty-database proof for
a first initialization or exact same-revision attribution evidence for a rerun.
The script is idempotent for clean initialization and same-current-layout reruns;
it is not an upgrade artifact. Production execution remains owned by the
controlled command so migration, seed, locking, and durable state are one
operational phase.

## Execute

Stop rollout before starting runtime replicas. Run exactly one command with the
initialization credential and production environment:

The connection-string key contains a hyphen, so use `env` when assigning it in
a POSIX shell:

```bash
env 'ConnectionStrings__skymonitordb-migrations'="$SKYMONITOR_MIGRATION_CONNECTION" \
  ASPNETCORE_ENVIRONMENT=Production \
  dotnet HVO.SkyMonitor.LogicHost.dll --host-mode=database-initialize
```

The command takes the exclusive session application lock
`HVO.SkyMonitor.LogicHost.DatabaseInitialization.v1`. A concurrent command
fails before schema or durable state mutation. Success requires a `Completed`
singleton whose target equals the executable's latest migration. Capture
duration, blocked sessions, peak data/log growth, autogrowth events, final free
space, and sanitized sessions showing the initialization application name.
The `v1` suffix identifies the current lock protocol and resource; it does not
imply support for a prior EF schema.

After success, apply or reapply the runtime grants. This enumerates all current
application tables and sequences while leaving `__EFMigrationsHistory` and
`DatabaseInitializationState` read-only:

```bash
SQLCMDPASSWORD="$SQL_ADMIN_PASSWORD" sqlcmd \
  -b -S "$SQLSERVER_HOST,$SQLSERVER_PORT" -d master -U "$SQL_ADMIN_USER" \
  -v DatabaseName=SkyMonitor RuntimeUser=HVO.LogicHost.Runtime \
  -i deploy/sql/logichost-runtime-role.sql
```

Remove the initialization secret from the workload, configure only the runtime
connection, and start one runtime replica. Production startup fails closed for
pending, missing, unknown, or incomplete migrations and for runtime principals
with effective DDL authority. Validate health and representative Identity,
OpenIddict, ingest, claim, retention-reference, network-read, and application
lock operations before scaling out.

## Failure And Recovery

- Lock acquisition failure: identify the session by application name. Wait for the approved owner or terminate it only through the database incident process; then rerun.
- Migration failure: do not start runtime. Preserve logs and the SQL hash, correct the cause, and rerun the same canonical revision only when migration history and durable state are attributable to that revision; otherwise recreate an empty database.
- Seed or initialization failure: a `Failed` or stale `Running` state blocks runtime. Correct configuration or current-layout data and rerun; current initialization operations remain convergent.
- Process or host loss: confirm the session lock was released, inspect logs, headroom, and durable state, then rerun the same revision.
- Pre-release revision rollback: stop all application traffic, deploy the selected revision, and initialize a new empty database. Do not edit `__EFMigrationsHistory` or the initialization singleton to manufacture compatibility.

Every retry records a new attempt ID. Retain command logs, final state, migration
history, SQL hash, timings, blocked-session samples, data/log growth, and the
sanitized runtime/initializer application attribution with the deployment
record.
