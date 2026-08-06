# LogicHost Database Initialization

Production LogicHost separates database initialization from ordinary runtime.
The initialization command is the only application process that receives the
migration connection string or DDL authority. Runtime validates the exact EF
migration set, durable initialization state, and absence of effective DDL
authority before serving requests.

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

Before each production change:

1. Confirm a tested encrypted full backup and point-in-time restore path.
2. Confirm the target database is precreated and no restore, schema change, or other initializer is active.
3. Record current migration, database data/log size, free space, autogrowth settings, and volume headroom.
4. Generate the canonical idempotent SQL and SHA-256 from the exact candidate revision.
5. Review all destructive operations, long scans, backfills, index builds, and expected lock/log growth against the approved maintenance window.

Generate the review artifact outside tracked source:

```bash
./scripts/logichost:generate-migration-sql TestResults/issue-256/logichost-idempotent.sql
sha256sum --check TestResults/issue-256/logichost-idempotent.sql.sha256
```

Record the Git revision, SQL hash, source and target migration IDs, backup ID,
data/log before values, free volume space, and reviewer approval. The script is
idempotent for inspection and recovery; production execution remains owned by
the controlled command so migration, seed, backfill, locking, and durable state
are one operational phase.

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
- Migration failure: do not start runtime. Preserve logs and the SQL hash, inspect EF migration history and the durable state, correct the cause, and rerun the idempotent command.
- Seed or backfill failure: a `Failed` or stale `Running` state blocks runtime. Correct configuration/data and rerun; seed and backfill operations are convergent.
- Process or host loss: confirm the session lock was released, inspect backup/log/headroom and durable state, then rerun the same revision.
- Expand-compatible application rollback: retain the expanded schema and deploy the prior compatible application revision.
- Destructive or incompatible migration rollback: stop all application traffic and restore the approved database backup. Do not manually edit `__EFMigrationsHistory` or the initialization singleton.

Every retry records a new attempt ID. Retain command logs, final state, migration
history, SQL hash, timings, blocked-session samples, data/log growth, and the
sanitized runtime/initializer application attribution with the deployment
record.
