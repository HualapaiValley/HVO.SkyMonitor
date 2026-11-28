using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.CameraAgent.Data;

internal static class CameraAgentIdentitySchema
{
    internal static async Task InitializeAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder(context.Database.GetConnectionString());
        if (File.Exists(connectionString.DataSource))
        {
            connectionString.Mode = SqliteOpenMode.ReadOnly;
            connectionString.Pooling = false;
            using var inspection = new SqliteConnection(connectionString.ToString());
            await inspection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // EF treats unknown migration IDs as applied migrations from another lineage and
            // can try to apply its initial migration over them. Inspect before EF opens for write.
            using var command = inspection.CreateCommand();
            command.CommandText = "BEGIN DEFERRED;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = """
                SELECT name, type FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*' AND name <> '__EFMigrationsLock';
                """;
            var hasHistory = false;
            var hasApplicationObjects = false;
            using (var rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (rows.GetString(0) == "__EFMigrationsHistory")
                    {
                        if (rows.GetString(1) != "table")
                            throw FreshInstallRequired();
                        hasHistory = true;
                    }
                    else
                        hasApplicationObjects = true;
                }
            }
            if (hasHistory)
            {
                command.CommandText = """
                    SELECT typeof("MigrationId"), "MigrationId"
                    FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
                    """;
                var knownMigrations = context.Database.GetMigrations().ToArray();
                using var rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var index = 0;
                while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (index == knownMigrations.Length || rows.GetString(0) != "text" || rows.GetString(1) != knownMigrations[index++])
                        throw FreshInstallRequired();
                }
                if (index == 0 && hasApplicationObjects)
                    throw FreshInstallRequired();
            }
            else if (hasApplicationObjects)
            {
                throw FreshInstallRequired();
            }
        }

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    private static InvalidDataException FreshInstallRequired()
        => new("CameraAgent Identity database has an unsupported migration history; fresh install required.");
}
