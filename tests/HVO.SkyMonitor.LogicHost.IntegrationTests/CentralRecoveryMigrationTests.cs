using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralRecoveryMigrationTests
{
    [TestMethod]
    public async Task CleanDatabase_MigratesToCurrentModelWithIndexesAndCheckpointSeed()
    {
        await using var database = CreateDatabase("Clean");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);

            (await database.Context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
            await AssertRecoverySchemaAsync(database.Context).ConfigureAwait(false);
            var model = database.Context.Model.FindEntityType(typeof(CentralArtifact));
            model.Should().NotBeNull();
            var expectedProperties = new[]
            {
                nameof(CentralArtifact.ObjectState), nameof(CentralArtifact.RecoveryGeneration), nameof(CentralArtifact.Id)
            };
            model!.GetIndexes().Any(index => index.Properties.Select(property => property.Name)
                .SequenceEqual(expectedProperties)).Should().BeTrue();
            database.Context.Model.FindEntityType(typeof(CentralObjectRecoveryDisposition))!.GetIndexes()
                .Any(index => index.IsUnique
                    && index.Properties.Single().Name == nameof(CentralObjectRecoveryDisposition.SourceObjectIdentitySha256))
                .Should().BeTrue();
            var dispositionModel = database.Context.Model.FindEntityType(typeof(CentralObjectRecoveryDisposition))!;
            dispositionModel.FindProperty(nameof(CentralObjectRecoveryDisposition.SourceObjectKey))!.GetMaxLength()
                .Should().Be(1024);
            database.Context.Model.FindEntityType(typeof(CentralRecoveryCheckpoint))!
                .FindProperty(nameof(CentralRecoveryCheckpoint.ObjectCursor))!.GetMaxLength().Should().Be(1024);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CurrentDatabase_MigrationIsIdempotentAndDoesNotDuplicateCheckpoint()
    {
        await using var database = CreateDatabase("Current");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);

            (await database.Context.CentralRecoveryCheckpoints.CountAsync().ConfigureAwait(false)).Should().Be(1);
            await AssertRecoverySchemaAsync(database.Context).ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task EnsureCreatedContext_ReceivesCheckpointModelSeed()
    {
        await using var database = CreateDatabase("EnsureCreated");
        try
        {
            (await database.Context.Database.EnsureCreatedAsync().ConfigureAwait(false)).Should().BeTrue();

            var checkpoint = await database.Context.CentralRecoveryCheckpoints.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            checkpoint.Id.Should().Be(CentralRecoveryCheckpoint.SingletonId);
            checkpoint.Generation.Should().Be(0);
            checkpoint.Phase.Should().Be(CentralRecoveryPhases.Idle);
            checkpoint.NextInventoryAtUtc.Should().Be(DateTimeOffset.UnixEpoch);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static async Task AssertRecoverySchemaAsync(ApplicationDbContext db)
    {
        var checkpoint = await db.CentralRecoveryCheckpoints.AsNoTracking().SingleAsync().ConfigureAwait(false);
        checkpoint.Should().Match<CentralRecoveryCheckpoint>(item => item.Id == CentralRecoveryCheckpoint.SingletonId
            && item.Generation == 0 && item.Phase == CentralRecoveryPhases.Idle
            && item.NextInventoryAtUtc == DateTimeOffset.UnixEpoch && item.ObjectPartition == 0);
        var indexes = await db.Database.SqlQuery<string>($"""
            SELECT [name] AS [Value]
            FROM [sys].[indexes]
            WHERE [object_id] IN (OBJECT_ID(N'[CentralArtifacts]'), OBJECT_ID(N'[CentralObjectRecoveryDispositions]'))
              AND [name] IS NOT NULL
            """).ToListAsync().ConfigureAwait(false);
        indexes.Should().Contain("IX_CentralArtifacts_ObjectState_RecoveryGeneration_Id");
        indexes.Should().Contain("IX_CentralArtifacts_ObjectState_ObjectVerifiedAtUtc_ReceivedAtUtc_Id");
        indexes.Should().Contain(
            "IX_CentralArtifacts_ObjectVerificationRetryAtUtc_ObjectVerificationRequestedAtUtc_Id");
        indexes.Should().Contain(
            "IX_CentralArtifacts_ReconstructionState_ReferenceRetryAtUtc_ReceivedAtUtc_Id");
        indexes.Should().Contain("IX_CentralArtifacts_StorageReference");
        indexes.Should().Contain("IX_CentralObjectRecoveryDispositions_SourceObjectIdentitySha256");
        indexes.Should().Contain("IX_CentralObjectRecoveryDispositions_State_UpdatedAtUtc_Id");
        var collations = await db.Database.SqlQuery<string>($"""
            SELECT [collation_name] AS [Value]
            FROM [sys].[columns]
            WHERE ([object_id] = OBJECT_ID(N'[CentralArtifacts]') AND [name] = N'StorageReference')
               OR ([object_id] = OBJECT_ID(N'[CentralObjectRecoveryDispositions]') AND [name] = N'SourceObjectKey')
            """).ToListAsync().ConfigureAwait(false);
        collations.Should().HaveCount(2).And.OnlyContain(value => value == "Latin1_General_100_BIN2");
    }

    private static MigrationDatabase CreateDatabase(string scenario)
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorRecovery{scenario}_{Guid.NewGuid():N}"
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
