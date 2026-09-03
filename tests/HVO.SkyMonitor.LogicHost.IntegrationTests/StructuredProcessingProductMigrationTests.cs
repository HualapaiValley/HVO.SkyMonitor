using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class StructuredProcessingProductMigrationTests
{
    [TestMethod]
    public async Task CleanDatabaseProducesBoundedStructuredSchema()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorStructuredMigration_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(options);
        try
        {
            await db.Database.MigrateAsync().ConfigureAwait(false);
            await AssertCurrentSchemaAsync(db).ConfigureAwait(false);
            await db.Database.MigrateAsync().ConfigureAwait(false);
            await AssertCurrentSchemaAsync(db).ConfigureAwait(false);
            (await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static async Task AssertCurrentSchemaAsync(ApplicationDbContext db)
    {
        var columns = await db.Database.SqlQuery<ColumnShape>($"""
                SELECT [name] AS [Name], [max_length] AS [MaxLength]
                FROM [sys].[columns]
                WHERE [object_id] = OBJECT_ID(N'[CentralStructuredProcessingProducts]')
                  AND [name] IN (N'AlgorithmsJson', N'CompatibilityJson')
                """).ToArrayAsync().ConfigureAwait(false);
        columns.Should().HaveCount(2).And.OnlyContain(column => column.MaxLength == 8000);
        var schemaColumns = await db.Database.SqlQuery<ColumnShape>($"""
                SELECT CONCAT(OBJECT_NAME([object_id]), N'.', [name]) AS [Name], [max_length] AS [MaxLength]
                FROM [sys].[columns]
                WHERE ([object_id] = OBJECT_ID(N'[CentralArtifacts]')
                       OR [object_id] = OBJECT_ID(N'[CentralArtifactIngestIdentities]'))
                  AND [name] = N'ManifestSchemaVersion'
                """).ToArrayAsync().ConfigureAwait(false);
        schemaColumns.Should().HaveCount(2).And.OnlyContain(column => column.MaxLength == 128);
    }

    private sealed record ColumnShape(string Name, short MaxLength);
}
