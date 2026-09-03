using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class DeploymentLocationAuthorityMigrationTests
{
    [TestMethod]
    public async Task CleanAndRepeatedMigrationProduceCurrentPhysicalSchema()
    {
        await using var database = CreateDatabase("Clean");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);

            (await database.Context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
            if (database.Context.Database.HasPendingModelChanges())
            {
                var snapshot = database.Context.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
                var current = database.Context.GetService<IDesignTimeModel>().Model;
                var differences = database.Context.GetService<IMigrationsModelDiffer>().GetDifferences(
                    snapshot.GetRelationalModel(), current.GetRelationalModel());
                var issueDifferences = differences.Where(operation =>
                    !TableName(operation).StartsWith("AspNet", StringComparison.Ordinal)).ToArray();
                issueDifferences.Should().BeEmpty(
                    $"issue #170 model operations must be migrated; observed {string.Join(", ", issueDifferences.Select(Describe))}");
            }
            var tables = await database.Context.Database.SqlQuery<string>($"""
                SELECT [name] AS [Value]
                FROM [sys].[tables]
                WHERE [name] IN (N'ObservatoryLocationVersions', N'DeviceDeploymentLocationVersions',
                    N'DeploymentLocationResolutionAudits', N'CentralCaptureLocations',
                    N'DeploymentLocationReconciliationWork', N'DeploymentLocationReconciliationCaptures')
                """).ToListAsync().ConfigureAwait(false);
            tables.Should().BeEquivalentTo(
                "ObservatoryLocationVersions",
                "DeviceDeploymentLocationVersions",
                "DeploymentLocationResolutionAudits",
                "CentralCaptureLocations",
                "DeploymentLocationReconciliationWork",
                "DeploymentLocationReconciliationCaptures");
            var indexes = await database.Context.Database.SqlQuery<string>($"""
                SELECT [name] AS [Value]
                FROM [sys].[indexes]
                WHERE [object_id] IN (OBJECT_ID(N'[ObservatoryLocationVersions]'),
                    OBJECT_ID(N'[DeviceDeploymentLocationVersions]'), OBJECT_ID(N'[CentralCaptureLocations]'),
                    OBJECT_ID(N'[DeploymentLocationReconciliationWork]'),
                    OBJECT_ID(N'[DeploymentLocationReconciliationCaptures]'),
                    OBJECT_ID(N'[CentralFrames]'))
                  AND [name] IS NOT NULL
                """).ToListAsync().ConfigureAwait(false);
            indexes.Should().Contain([
                "IX_ObservatoryLocationVersions_ObservatoryId_Version",
                "IX_ObservatoryLocationVersions_ObservatoryId",
                "IX_DeviceDeploymentLocationVersions_RegistrationId_LocationId_Version_ObservatoryLocationVersionId",
                "IX_DeviceDeploymentLocationVersions_RegistrationId_Status_ProposedAtUtc_Id",
                "IX_DeviceDeploymentLocationVersions_RegistrationId_ProposedAtUtc_Id",
                "IX_CentralCaptureLocations_LocationId_Version",
                "IX_DeploymentLocationReconciliationWork_DeviceDeploymentLocationVersionId",
                "IX_DeploymentLocationReconciliationWork_Status_NextAttemptAtUtc_CreatedAtUtc_Id",
                "IX_DeploymentLocationReconciliationWork_Status_LeaseExpiresAtUtc_CreatedAtUtc_Id",
                "IX_DeploymentLocationReconciliationCaptures_Work_Generation_Cursor",
                "IX_CentralFrames_RegistrationId",
                "IX_CentralFrames_RegistrationId_FirstReceivedAtUtc_Id"
            ]);
            var indexShapes = await database.Context.Database.SqlQuery<IndexShape>($"""
                SELECT indexes.[name] AS [Name], indexes.[is_unique] AS [IsUnique],
                       COALESCE(indexes.[filter_definition], N'') AS [FilterDefinition],
                       STRING_AGG(columns.[name], N',') WITHIN GROUP (ORDER BY index_columns.[key_ordinal]) AS [KeyColumns]
                FROM [sys].[indexes] AS indexes
                INNER JOIN [sys].[index_columns] AS index_columns
                    ON index_columns.[object_id] = indexes.[object_id]
                    AND index_columns.[index_id] = indexes.[index_id]
                    AND index_columns.[key_ordinal] > 0
                INNER JOIN [sys].[columns] AS columns
                    ON columns.[object_id] = index_columns.[object_id]
                    AND columns.[column_id] = index_columns.[column_id]
                WHERE indexes.[object_id] IN (OBJECT_ID(N'[ObservatoryLocationVersions]'),
                    OBJECT_ID(N'[DeviceDeploymentLocationVersions]'))
                GROUP BY indexes.[name], indexes.[is_unique], indexes.[filter_definition]
                """).ToArrayAsync().ConfigureAwait(false);
            var currentIndex = indexShapes.Should().ContainSingle(item =>
                item.Name == "IX_ObservatoryLocationVersions_ObservatoryId").Subject;
            currentIndex.IsUnique.Should().BeTrue();
            currentIndex.FilterDefinition.Should().Contain("[SupersededAtUtc] IS NULL");
            currentIndex.KeyColumns.Should().Be("ObservatoryId");
            indexShapes.Should().ContainSingle(item =>
                item.Name == "IX_DeviceDeploymentLocationVersions_RegistrationId_LocationId_Version_ObservatoryLocationVersionId"
                && item.IsUnique
                && item.KeyColumns == "RegistrationId,LocationId,Version,ObservatoryLocationVersionId");
            var foreignKeys = await database.Context.Database.SqlQuery<ForeignKeyShape>($"""
                SELECT OBJECT_NAME([parent_object_id]) AS [DependentTable], [name] AS [Name],
                       [delete_referential_action_desc] AS [DeleteAction]
                FROM [sys].[foreign_keys]
                WHERE [parent_object_id] IN (OBJECT_ID(N'[ObservatoryLocationVersions]'),
                    OBJECT_ID(N'[DeviceDeploymentLocationVersions]'),
                    OBJECT_ID(N'[DeploymentLocationResolutionAudits]'),
                    OBJECT_ID(N'[CentralCaptureLocations]'),
                    OBJECT_ID(N'[DeploymentLocationReconciliationWork]'),
                    OBJECT_ID(N'[DeploymentLocationReconciliationCaptures]'))
                """).ToArrayAsync().ConfigureAwait(false);
            foreignKeys.Should().HaveCount(9);
            foreignKeys.Count(item => item.DeleteAction == "CASCADE").Should().Be(2);
            foreignKeys.Count(item => item.DeleteAction == "NO_ACTION").Should().Be(7);
            var checks = await database.Context.Database.SqlQuery<string>($"""
                SELECT [name] AS [Value]
                FROM [sys].[check_constraints]
                WHERE [parent_object_id] IN (OBJECT_ID(N'[ObservatoryLocationVersions]'),
                    OBJECT_ID(N'[DeviceDeploymentLocationVersions]'), OBJECT_ID(N'[CentralCaptureLocations]'))
                """).ToArrayAsync().ConfigureAwait(false);
            checks.Should().Contain([
                "CK_ObservatoryLocationVersions_Interval",
                "CK_ObservatoryLocationVersions_Latitude",
                "CK_ObservatoryLocationVersions_Longitude",
                "CK_ObservatoryLocationVersions_Radius",
                "CK_ObservatoryLocationVersions_Version",
                "CK_DeviceDeploymentLocationVersions_Resolution",
                "CK_DeviceDeploymentLocationVersions_Interval",
                "CK_CentralCaptureLocations_Interval"
            ]);
            var workChecks = await database.Context.Database.SqlQuery<string>($"""
                SELECT [name] AS [Value]
                FROM [sys].[check_constraints]
                WHERE [parent_object_id] = OBJECT_ID(N'[DeploymentLocationReconciliationWork]')
                """).ToArrayAsync().ConfigureAwait(false);
            workChecks.Should().BeEquivalentTo(
                "CK_DeploymentLocationReconciliationWork_Status",
                "CK_DeploymentLocationReconciliationWork_Counts",
                "CK_DeploymentLocationReconciliationWork_Lease",
                "CK_DeploymentLocationReconciliationWork_ActiveBatch",
                "CK_DeploymentLocationReconciliationWork_Discovery",
                "CK_DeploymentLocationReconciliationWork_DiscoveryCursor",
                "CK_DeploymentLocationReconciliationWork_Cursor",
                "CK_DeploymentLocationReconciliationWork_Timestamps",
                "CK_DeploymentLocationReconciliationWork_State");
            var rowVersion = await database.Context.Database.SqlQuery<string>($"""
                SELECT TYPE_NAME([system_type_id]) AS [Value]
                FROM [sys].[columns]
                WHERE [object_id] = OBJECT_ID(N'[DeploymentLocationReconciliationWork]')
                  AND [name] = N'RowVersion'
                """).SingleAsync().ConfigureAwait(false);
            rowVersion.Should().Be("timestamp");
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static MigrationDatabase CreateDatabase(string scenario)
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorDeploymentLocation{scenario}_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new MigrationDatabase(new ApplicationDbContext(options));
    }

    private sealed class MigrationDatabase(ApplicationDbContext context) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed record IndexShape(
        string Name,
        bool IsUnique,
        string FilterDefinition,
        string KeyColumns);

    private sealed record ForeignKeyShape(string DependentTable, string Name, string DeleteAction);

    private static string Describe(MigrationOperation operation)
        => operation switch
        {
            DropTableOperation drop => $"DropTable({drop.Name})",
            AlterColumnOperation alter =>
                $"AlterColumn({alter.Table}.{alter.Name}:{alter.OldColumn.ClrType.Name}->{alter.ClrType.Name})",
            _ => operation.GetType().Name
        };

    private static string TableName(MigrationOperation operation)
        => operation switch
        {
            DropTableOperation drop => drop.Name,
            TableOperation table => table.Name,
            ColumnOperation column => column.Table,
            _ => string.Empty
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
