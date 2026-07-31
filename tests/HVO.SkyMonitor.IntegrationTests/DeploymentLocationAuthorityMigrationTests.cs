using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
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
    private const string PreviousMigration = "20260721201807_AddCentralTransientReviewAndDerivatives";

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
                    N'DeploymentLocationResolutionAudits', N'CentralCaptureLocations')
                """).ToListAsync().ConfigureAwait(false);
            tables.Should().BeEquivalentTo(
                "ObservatoryLocationVersions",
                "DeviceDeploymentLocationVersions",
                "DeploymentLocationResolutionAudits",
                "CentralCaptureLocations");
            var indexes = await database.Context.Database.SqlQuery<string>($"""
                SELECT [name] AS [Value]
                FROM [sys].[indexes]
                WHERE [object_id] IN (OBJECT_ID(N'[ObservatoryLocationVersions]'),
                    OBJECT_ID(N'[DeviceDeploymentLocationVersions]'), OBJECT_ID(N'[CentralCaptureLocations]'),
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
                "IX_CentralFrames_RegistrationId"
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
                    OBJECT_ID(N'[CentralCaptureLocations]'))
                """).ToArrayAsync().ConfigureAwait(false);
            foreignKeys.Should().HaveCount(6);
            foreignKeys.Should().ContainSingle(item =>
                item.DependentTable == "CentralCaptureLocations" && item.DeleteAction == "CASCADE");
            foreignKeys.Count(item => item.DeleteAction == "NO_ACTION").Should().Be(5);
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
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PredecessorUpgradePreservesLegacyEvidenceAndBackfillsOnlyCurrentObservatory()
    {
        await using var database = CreateDatabase("Upgrade");
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
            var observatoryId = Guid.NewGuid();
            var registrationId = Guid.NewGuid();
            var devicePublicId = Guid.NewGuid();
            var frameId = Guid.NewGuid();
            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [Observatories]
                    ([Id], [OwnerUserId], [Name], [LatitudeDegrees], [LongitudeDegrees], [ElevationMeters],
                     [TimeZoneId], [CreatedAtUtc], [UpdatedAtUtc], [IsActive])
                VALUES
                    ({observatoryId}, {"migration-owner"}, {"Legacy Observatory"}, {35.347d}, {-113.878d},
                     {520d}, {"America/Phoenix"}, {DateTimeOffset.UnixEpoch}, NULL, {true});

                INSERT INTO [DeviceRegistrations]
                    ([Id], [DeviceId], [ObservatoryId], [FriendlyName], [ObservatoryName],
                     [ObservatoryLatitudeDegrees], [ObservatoryLongitudeDegrees], [ObservatoryElevationMeters],
                     [ObservatoryTimeZoneId], [OwnerUserId], [OwnerDisplayName], [OwnerConfirmationMethod],
                     [Status], [VerificationCodeHash], [DevicePublicId], [IssuedAtUtc])
                VALUES
                    ({registrationId}, {"legacy-camera"}, {observatoryId}, {"Legacy Camera"}, {"Legacy Observatory"},
                     {35.347d}, {-113.878d}, {520d}, {"America/Phoenix"}, {"migration-owner"},
                     {"Migration Owner"}, {"SelfAttested"}, {"Active"}, {new string('A', 64)},
                     {devicePublicId}, {DateTimeOffset.UnixEpoch});

                INSERT INTO [CentralFrames]
                    ([Id], [RegistrationId], [DevicePublicId], [ObservatoryId], [AgentId], [FrameId],
                     [CapturedAtUtc], [FirstReceivedAtUtc], [RigProfileVersion], [SceneProvenanceJson])
                VALUES
                    ({Guid.NewGuid()}, {registrationId}, {devicePublicId}, {observatoryId}, {"legacy-camera"},
                     {frameId}, {DateTimeOffset.UnixEpoch}, {DateTimeOffset.UnixEpoch}, NULL, NULL);
                """).ConfigureAwait(false);

            await migrator.MigrateAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            var registration = await database.Context.DeviceRegistrations.SingleAsync(item => item.Id == registrationId)
                .ConfigureAwait(false);
            var frame = await database.Context.CentralFrames.SingleAsync(item => item.FrameId == frameId)
                .ConfigureAwait(false);
            var observatory = await database.Context.Observatories.SingleAsync(item => item.Id == observatoryId)
                .ConfigureAwait(false);
            registration.LocationEvidenceState.Should().Be(RegistrationLocationEvidenceState.LegacyIncomplete);
            registration.ObservatoryLocationVersion.Should().BeNull();
            frame.LocationEvidenceState.Should().Be(CentralCaptureLocationEvidenceState.LegacyIncomplete);
            (await database.Context.CentralCaptureLocations.CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await database.Context.DeviceDeploymentLocationVersions.CountAsync().ConfigureAwait(false)).Should().Be(0);
            observatory.CurrentLocationVersion.Should().BeNull();

            var backfilled = await ObservatoryLocationBackfill.RunAsync(
                database.Context,
                new FixedTimeProvider(BackfillUtc)).ConfigureAwait(false);

            backfilled.Should().Be(1);
            database.Context.ChangeTracker.Clear();
            observatory = await database.Context.Observatories.SingleAsync(item => item.Id == observatoryId)
                .ConfigureAwait(false);
            observatory.CurrentLocationVersion.Should().Be(1);
            observatory.CurrentLocationCanonicalSha256.Should().HaveLength(64);
            var version = await database.Context.ObservatoryLocationVersions.SingleAsync().ConfigureAwait(false);
            version.EffectiveFromUtc.Should().Be(BackfillUtc);
            version.LatitudeDegrees.Should().Be(35.347d);
            (await database.Context.DeviceDeploymentLocationVersions.CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await database.Context.CentralCaptureLocations.CountAsync().ConfigureAwait(false)).Should().Be(0);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PredecessorUpgrade_AllowsInvalidLegacyLocationToRemainIncompleteUntilOwnerRepair()
    {
        await using var database = CreateDatabase("LegacyRepair");
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
            var observatoryId = Guid.NewGuid();
            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [AspNetUsers]
                    ([Id], [AccountType], [UserName], [EmailConfirmed], [PhoneNumberConfirmed],
                     [TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount])
                VALUES ({"legacy-repair-owner"}, {(int)AccountType.User}, {"legacy-repair-owner"},
                        {false}, {false}, {false}, {false}, {0});
                """).ConfigureAwait(false);
            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [Observatories]
                    ([Id], [OwnerUserId], [Name], [LatitudeDegrees], [LongitudeDegrees], [ElevationMeters],
                     [TimeZoneId], [CreatedAtUtc], [UpdatedAtUtc], [IsActive])
                VALUES
                    ({observatoryId}, {"legacy-repair-owner"}, {"Invalid Legacy Observatory"}, {95d}, {200d},
                     {520d}, {"Not/A-Time-Zone"}, {DateTimeOffset.UnixEpoch}, NULL, {true});
                """).ConfigureAwait(false);

            await migrator.MigrateAsync().ConfigureAwait(false);
            var backfilled = await ObservatoryLocationBackfill.RunAsync(
                database.Context,
                new FixedTimeProvider(BackfillUtc)).ConfigureAwait(false);

            backfilled.Should().Be(0);
            var legacy = await database.Context.Observatories.SingleAsync(item => item.Id == observatoryId)
                .ConfigureAwait(false);
            legacy.CurrentLocationVersion.Should().BeNull();

            var clock = new FixedTimeProvider(BackfillUtc.AddMinutes(1));
            var authority = new DeploymentLocationAuthorityService(database.Context, clock);
            var service = new ObservatoryService(database.Context, clock, authority);
            var repaired = await service.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
                observatoryId,
                "legacy-repair-owner",
                legacy.Name,
                35,
                -113,
                500,
                "UTC",
                true)).ConfigureAwait(false);
            repaired.CurrentLocationVersion.Should().Be(1);
            repaired.CurrentLocationCanonicalSha256.Should().HaveLength(64);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static readonly DateTimeOffset BackfillUtc = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

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
