using System.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class DatabaseRuntimeValidator(
    ApplicationDbContext dbContext,
    IHostEnvironment environment)
{
    internal async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var known = dbContext.Database.GetMigrations().ToArray();
        var applied = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        if (known.Length == 0 || !known.SequenceEqual(applied, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The SkyMonitor database migration set is not current.");
        }
        var state = await dbContext.DatabaseInitializationState.AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || state.Status != DatabaseInitializationStatus.Completed ||
            state.InitializationVersion != DatabaseInitializer.CurrentInitializationVersion ||
            !string.Equals(state.TargetMigrationId, known[^1], StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The SkyMonitor database initialization state is incomplete.");
        }
        if (environment.IsProduction() && await HasProhibitedRuntimeAuthorityAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The production LogicHost runtime SQL principal has prohibited schema, security, or initialization-control authority.");
        }
    }

    private async Task<bool> HasProhibitedRuntimeAuthorityAsync(CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = """
                SELECT CASE WHEN
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CONTROL') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY SCHEMA') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY ROLE') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY USER') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'IMPERSONATE ANY USER') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'TAKE OWNERSHIP') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE SCHEMA') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE VIEW') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE PROCEDURE') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE FUNCTION') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TYPE') = 1 OR
                    HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE SYNONYM') = 1 OR
                    HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'CONTROL') = 1 OR
                    HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'ALTER') = 1 OR
                    HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'TAKE OWNERSHIP') = 1 OR
                    HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'CREATE SEQUENCE') = 1 OR
                    IS_MEMBER(N'db_owner') = 1 OR
                    IS_MEMBER(N'db_ddladmin') = 1 OR
                    IS_MEMBER(N'db_securityadmin') = 1 OR
                    IS_SRVROLEMEMBER(N'securityadmin') = 1 OR
                    IS_SRVROLEMEMBER(N'sysadmin') = 1 OR
                    EXISTS (
                        SELECT 1 FROM [sys].[objects]
                        WHERE [is_ms_shipped] = 0
                          AND HAS_PERMS_BY_NAME(
                              QUOTENAME(OBJECT_SCHEMA_NAME([object_id])) + N'.' + QUOTENAME([name]),
                              N'OBJECT', N'ALTER') = 1) OR
                    HAS_PERMS_BY_NAME(N'dbo.__EFMigrationsHistory', N'OBJECT', N'INSERT') = 1 OR
                    HAS_PERMS_BY_NAME(N'dbo.__EFMigrationsHistory', N'OBJECT', N'UPDATE') = 1 OR
                    HAS_PERMS_BY_NAME(N'dbo.__EFMigrationsHistory', N'OBJECT', N'DELETE') = 1 OR
                    HAS_PERMS_BY_NAME(N'dbo.DatabaseInitializationState', N'OBJECT', N'INSERT') = 1 OR
                    HAS_PERMS_BY_NAME(N'dbo.DatabaseInitializationState', N'OBJECT', N'UPDATE') = 1 OR
                    HAS_PERMS_BY_NAME(N'dbo.DatabaseInitializationState', N'OBJECT', N'DELETE') = 1
                THEN 1 ELSE 0 END
                """;
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
