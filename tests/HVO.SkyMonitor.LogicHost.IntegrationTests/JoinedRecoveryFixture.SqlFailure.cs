using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.IntegrationTests;

internal sealed partial class JoinedRecoveryFixture
{
    private async Task VerifySqlFailurePreservationAsync()
    {
        var sourceBefore = Fingerprint(SourceObjects) + Fingerprint(SourceHost);
        var error = 0;
        try { await BackupSqlAsync(FailAfterRestore); }
        catch (SanitizedScriptException exception) { error = exception.SqlErrorNumber; }
        Require(error == 51266, "postrestore-sanitized-error");
        Require(await DatabaseExistsAsync(sourceDatabase), "postrestore-source-preserved");
        Require(await DatabaseExistsAsync(targetDatabase), "postrestore-target-preserved");
        Require(await ReadSqlFactsAsync(sourceDatabase) == sqlFacts, "postrestore-source-facts");
        Require(await ReadSqlFactsAsync(targetDatabase) == sqlFacts, "postrestore-target-facts");
        var backup = await container.ReadFileAsync(BackupPath);
        Require(backup.Length > 0, "postrestore-backup-present");
        phase = "sql-failure-preservation";
        RetainPrivateAttribution();
        using (var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "private-attribution.json"))))
        {
            var value = receipt.RootElement;
            Require(value.GetProperty("Run").GetString() == runId
                && value.GetProperty("ContainerId").GetString() == container.Id
                && value.GetProperty("SourceDatabase").GetString() == sourceDatabase
                && value.GetProperty("TargetDatabase").GetString() == targetDatabase
                && value.GetProperty("Backup").GetString() == BackupPath
                && value.GetProperty("Root").GetString() == root
                && value.GetProperty("Phase").GetString() == phase, "postrestore-private-attribution");
        }
        Require(sourceBefore == Fingerprint(SourceObjects) + Fingerprint(SourceHost), "postrestore-files-preserved");
        Require(Hash(backup) == Hash(await container.ReadFileAsync(BackupPath)), "postrestore-backup-preserved");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Issue = 1630,
            PostRestoreFailure = true,
            SanitizedErrorNumber = error,
            SourcePreserved = true,
            TargetPreserved = true,
            BackupPreserved = true,
            PrivateAttributionVerified = true
        }));
    }

    private async Task<bool> DatabaseExistsAsync(string database)
    {
        await using var master = new SqlConnection(connectionString);
        await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandText = "SELECT CASE WHEN DB_ID(@database) IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.Add(new SqlParameter("@database", SqlDbType.NVarChar, 128) { Value = database });
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 1;
    }

    private static string FailAfterRestore(string script)
    {
        const string anchor = "    SET @restoreCompletedAt = SYSUTCDATETIME();";
        Require(script.Split(anchor, StringSplitOptions.None).Length == 2, "postrestore-failure-anchor");
        return script.Replace(anchor, anchor + "\n    THROW 51999, 'Disposable fixture control.', 1;", StringComparison.Ordinal);
    }
}
