using System.Text;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HVO.SkyMonitor.LogicHost.Data.Migrations;

internal static class BaselineTriggerSql
{
    private const string ResourceName =
        "HVO.SkyMonitor.LogicHost.Data.Migrations.BaselineTriggers.sql";

    internal static void CreateAll(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        using var stream = typeof(BaselineTriggerSql).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded trigger resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var batch = new StringBuilder();
        while (reader.ReadLine() is { } line)
        {
            if (string.Equals(line, "GO", StringComparison.Ordinal))
            {
                ExecuteBatch(migrationBuilder, batch);
                continue;
            }
            if (!line.StartsWith("-- TR_", StringComparison.Ordinal))
            {
                _ = batch.AppendLine(line);
            }
        }
        ExecuteBatch(migrationBuilder, batch);
    }

    private static void ExecuteBatch(MigrationBuilder migrationBuilder, StringBuilder batch)
    {
        var sql = batch.ToString().Trim();
        if (sql.Length > 0)
        {
            MigrationSql.ExecuteBatch(migrationBuilder, sql);
        }
        _ = batch.Clear();
    }
}
