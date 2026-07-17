using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class EnvironmentalObservationMigrationTests
{
    private const string PreviousMigration = "20260717045806_AddCentralRecoveryRetrySchedule";

    [TestMethod]
    public async Task CleanAndRepeatedMigrationProduceCurrentPhysicalSchema()
    {
        await using var database = CreateDatabase("Clean");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);

            (await database.Context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
            await AssertSchemaAsync(database.Context).ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PredecessorUpgradePreservesExistingSiteAndAddsEmptyObservationTables()
    {
        await using var database = CreateDatabase("Upgrade");
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
            var siteId = Guid.NewGuid();
            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [Observatories]
                    ([Id], [OwnerUserId], [Name], [LatitudeDegrees], [LongitudeDegrees], [ElevationMeters],
                     [TimeZoneId], [CreatedAtUtc], [UpdatedAtUtc], [IsActive])
                VALUES
                    ({siteId}, {"environment-migration-owner"}, {"Environment migration site"}, {0d}, {0d}, {0d},
                     {"UTC"}, {DateTimeOffset.UnixEpoch}, NULL, {true});
                """).ConfigureAwait(false);

            await migrator.MigrateAsync().ConfigureAwait(false);

            (await database.Context.Observatories.AsNoTracking().CountAsync(site => site.Id == siteId).ConfigureAwait(false))
                .Should().Be(1);
            (await database.Context.EnvironmentalObservations.CountAsync().ConfigureAwait(false)).Should().Be(0);
            await AssertSchemaAsync(database.Context).ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static async Task AssertSchemaAsync(ApplicationDbContext db)
    {
        var indexes = await db.Database.SqlQuery<string>($"""
            SELECT [name] AS [Value]
            FROM [sys].[indexes]
            WHERE [object_id] IN
                (OBJECT_ID(N'[EnvironmentalObservationSources]'), OBJECT_ID(N'[EnvironmentalObservations]'),
                 OBJECT_ID(N'[EnvironmentalObservationLineage]'))
              AND [name] IS NOT NULL
            """).ToListAsync().ConfigureAwait(false);
        indexes.Should().Contain([
            "IX_EnvironmentalObservationSources_IdentitySha256",
            "IX_EnvironmentalObservationSources_SiteId_AgentId_RigId_Kind",
            "IX_EnvironmentalObservations_SourceRecordId_ObservationId",
            "IX_EnvironmentalObservations_SourceKindValidity",
            "IX_EnvironmentalObservations_SourceKindValidityEnd",
            "IX_EnvironmentalObservations_TargetKindValidityEnd",
            "IX_EnvironmentalObservations_TargetScopeKindObserved",
            "IX_EnvironmentalObservations_TargetKindObserved",
            "IX_EnvironmentalObservations_SourceKindObserved",
            "IX_EnvironmentalObservations_Retention",
            "IX_EnvironmentalObservationLineage_DerivedObservationRecordId_SourceObservationRecordId",
            "IX_EnvironmentalObservationLineage_SourceObservationRecordId"
        ]);
        var constraints = await db.Database.SqlQuery<string>($"""
            SELECT [name] AS [Value]
            FROM [sys].[check_constraints]
            WHERE [parent_object_id] IN
                (OBJECT_ID(N'[EnvironmentalObservations]'), OBJECT_ID(N'[EnvironmentalObservationLineage]'))
            """).ToListAsync().ConfigureAwait(false);
        constraints.Should().Contain([
            "CK_EnvironmentalObservations_Value",
            "CK_EnvironmentalObservations_Validity",
            "CK_EnvironmentalObservations_ObservedInterval",
            "CK_EnvironmentalObservations_SubmittedValue",
            "CK_EnvironmentalObservations_Uncertainty",
            "CK_EnvironmentalObservationLineage_Ordinal"
        ]);
    }

    private static MigrationDatabase CreateDatabase(string scenario)
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorEnvironment{scenario}_{Guid.NewGuid():N}"
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
}
