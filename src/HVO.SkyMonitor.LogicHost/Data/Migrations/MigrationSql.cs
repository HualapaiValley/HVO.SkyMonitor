using Microsoft.EntityFrameworkCore.Migrations;

namespace HVO.SkyMonitor.LogicHost.Data.Migrations;

internal static class MigrationSql
{
    internal static void ExecuteBatch(MigrationBuilder migrationBuilder, string sql)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentNullException.ThrowIfNull(sql);
        migrationBuilder.Sql($"EXEC(N'{sql.Replace("'", "''", StringComparison.Ordinal)}');");
    }
}
