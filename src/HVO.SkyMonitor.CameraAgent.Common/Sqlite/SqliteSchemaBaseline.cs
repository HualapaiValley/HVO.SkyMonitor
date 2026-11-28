using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Sqlite;

internal static class SqliteSchemaBaseline
{
    // Used before persistent connection configuration and again under the schema creation transaction.
    internal static bool NeedsCreation(SqliteConnection connection, SqliteTransaction? transaction,
        string store, int version, int applicationId = 0)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT (SELECT user_version FROM pragma_user_version), (SELECT application_id FROM pragma_application_id);
            """;
        long foundVersion;
        long foundApplicationId;
        using (var row = command.ExecuteReader())
        {
            row.Read();
            foundVersion = row.GetInt64(0);
            foundApplicationId = row.GetInt64(1);
        }
        if (foundVersion == version && foundApplicationId == applicationId)
            return false;
        if (foundVersion == 0 && foundApplicationId == 0)
        {
            command.CommandText = "SELECT NOT EXISTS (SELECT 1 FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*');";
            if (command.ExecuteScalar() is 1L)
                return true;
        }
        throw new InvalidDataException(
            $"{store} SQLite schema {foundVersion} (application ID {foundApplicationId}) is unsupported; fresh install required.");
    }

    internal static void CheckBeforeWrite(SqliteConnection connection, string store, int version, int applicationId = 0)
    {
        // An unsupported WAL database must not be checkpointed when its connection is disposed.
        SetCheckpointOnClose(connection, enabled: false);
        _ = NeedsCreation(connection, null, store, version, applicationId);
        SetCheckpointOnClose(connection, enabled: true);
    }

    internal static string Stamp(int version, int applicationId = 0)
        => string.Create(CultureInfo.InvariantCulture,
            $"PRAGMA application_id = {applicationId}; PRAGMA user_version = {version};");

    private static void SetCheckpointOnClose(SqliteConnection connection, bool enabled)
    {
        const int noCheckpointOnClose = 1006;
        var requested = enabled ? 0 : 1;
        var result = SQLitePCL.raw.sqlite3_db_config(connection.Handle, noCheckpointOnClose, requested, out var applied);
        if (result != SQLitePCL.raw.SQLITE_OK || applied != requested)
            throw new InvalidOperationException("SQLite checkpoint-on-close configuration failed.");
    }
}
