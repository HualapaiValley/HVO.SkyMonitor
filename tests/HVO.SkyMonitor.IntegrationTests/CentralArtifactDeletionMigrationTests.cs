using System.Security.Cryptography;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralArtifactDeletionMigrationTests
{
    [TestMethod]
    public async Task CurrentSchemaEnforcesDeletionInvariants()
    {
        await using var database = CreateDatabase();
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            (await database.Context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
            var indexes = await database.Context.Database.SqlQuery<string>($"""
                SELECT [name] AS [Value] FROM [sys].[indexes]
                WHERE [object_id] = OBJECT_ID(N'[CentralObjectRecoveryDispositions]')
                """).ToArrayAsync().ConfigureAwait(false);
            indexes.Should().Contain(
                "IX_CentralObjectRecoveryDispositions_Kind_State_NextAttemptAtUtc_UpdatedAtUtc_Id");
            database.Context.Model.FindEntityType(typeof(CentralObjectRecoveryDisposition))!
                .GetForeignKeys().Should().NotContain(key =>
                    key.Properties.Any(property => property.Name == nameof(CentralObjectRecoveryDisposition.CentralArtifactId)));

            Func<Task> completedWithoutTimestamp = () => database.Context.Database.ExecuteSqlRawAsync("""
                INSERT INTO [CentralObjectRecoveryDispositions]
                    ([Id], [SourceObjectIdentitySha256], [SourceObjectKey], [Kind], [State], [CentralArtifactId],
                     [OperationToken], [ByteLength], [CreatedAtUtc], [UpdatedAtUtc], [AttemptCount],
                     [LastAttemptAtUtc], [NextAttemptAtUtc], [CompletedAtUtc], [ReasonCode])
                VALUES
                    (NEWID(), REPLICATE('B', 64), N'invalid/completed.bin', N'ExpiredDelete', N'Completed', NEWID(),
                     NEWID(), 12, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 1,
                     SYSDATETIMEOFFSET(), NULL, NULL, NULL);
                """);
            await completedWithoutTimestamp.Should().ThrowAsync<SqlException>().ConfigureAwait(false);

            Func<Task> failedWithCompletion = () => database.Context.Database.ExecuteSqlRawAsync("""
                INSERT INTO [CentralObjectRecoveryDispositions]
                    ([Id], [SourceObjectIdentitySha256], [SourceObjectKey], [Kind], [State], [CentralArtifactId],
                     [OperationToken], [ByteLength], [CreatedAtUtc], [UpdatedAtUtc], [AttemptCount],
                     [LastAttemptAtUtc], [NextAttemptAtUtc], [CompletedAtUtc], [ReasonCode])
                VALUES
                    (NEWID(), REPLICATE('C', 64), N'invalid/failed.bin', N'ExpiredDelete', N'Failed', NEWID(),
                     NEWID(), 12, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 1,
                     SYSDATETIMEOFFSET(), NULL, SYSDATETIMEOFFSET(), N'retention.authorization');
                """);
            await failedWithCompletion.Should().ThrowAsync<SqlException>().ConfigureAwait(false);

            Func<Task> negativeAttempt = () => database.Context.Database.ExecuteSqlRawAsync("""
                INSERT INTO [CentralObjectRecoveryDispositions]
                    ([Id], [SourceObjectIdentitySha256], [SourceObjectKey], [Kind], [State], [ByteLength],
                     [CreatedAtUtc], [UpdatedAtUtc], [AttemptCount])
                VALUES
                    (NEWID(), REPLICATE('D', 64), N'invalid/attempt.bin', N'ExpiredDelete',
                     N'PendingDelete', 12, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), -1);
                """);
            await negativeAttempt.Should().ThrowAsync<SqlException>().ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var frame = new CentralFrame
            {
                RegistrationId = Guid.NewGuid(),
                DevicePublicId = Guid.NewGuid(),
                ObservatoryId = Guid.NewGuid(),
                AgentId = $"retention-migration-{Guid.NewGuid():N}",
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = now,
                FirstReceivedAtUtc = now
            };
            var artifact = new CentralArtifact
            {
                Frame = frame,
                CentralFrameId = frame.Id,
                DevicePublicId = frame.DevicePublicId,
                ArtifactId = Guid.NewGuid(),
                Role = FrameArtifactRole.Raw,
                RecipeVersion = "retention-migration-v1",
                ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
                MediaType = "application/octet-stream",
                ByteLength = 3,
                ChecksumSha256 = Convert.ToHexString(SHA256.HashData([1, 2, 3])),
                StorageReference = $"minio://skymonitor-artifacts/migration/{Guid.NewGuid():N}.bin",
                ReceivedAtUtc = now,
                IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete
            };
            database.Context.Add(artifact);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);

            var token = Guid.NewGuid();
            Func<Task> tokenizedAvailable = () => database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralArtifacts]
                SET [RetentionDeletionToken] = {token}, [RetentionDeletionRequestedAtUtc] = {now}
                WHERE [Id] = {artifact.Id};
                """);
            await tokenizedAvailable.Should().ThrowAsync<SqlException>().ConfigureAwait(false);

            Func<Task> timestampsWithoutToken = () => database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralArtifacts]
                SET [RetentionDeletionRequestedAtUtc] = {now}
                WHERE [Id] = {artifact.Id};
                """);
            await timestampsWithoutToken.Should().ThrowAsync<SqlException>().ConfigureAwait(false);

            Func<Task> completedBeforeRequested = () => database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralArtifacts]
                SET [ObjectState] = N'Expired',
                    [RetentionDeletionToken] = {token},
                    [RetentionDeletionRequestedAtUtc] = {now},
                    [RetentionDeletionCompletedAtUtc] = {now.AddSeconds(-1)}
                WHERE [Id] = {artifact.Id};
                """);
            await completedBeforeRequested.Should().ThrowAsync<SqlException>().ConfigureAwait(false);

            (await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralArtifacts]
                SET [ObjectState] = N'Expired',
                    [RetentionDeletionToken] = {token},
                    [RetentionDeletionRequestedAtUtc] = {now},
                    [RetentionDeletionCompletedAtUtc] = {now}
                WHERE [Id] = {artifact.Id};
                """).ConfigureAwait(false)).Should().Be(1);
            database.Context.ChangeTracker.Clear();
            var validArtifact = await database.Context.CentralArtifacts.AsNoTracking()
                .SingleAsync(item => item.Id == artifact.Id).ConfigureAwait(false);
            validArtifact.ObjectState.Should().Be(CentralArtifactObjectState.Expired);
            validArtifact.RetentionDeletionToken.Should().Be(token);
            validArtifact.RetentionDeletionRequestedAtUtc.Should().Be(now);
            validArtifact.RetentionDeletionCompletedAtUtc.Should().Be(now);

            Func<Task> tokenWithoutArtifact = () => database.Context.Database.ExecuteSqlRawAsync("""
                INSERT INTO [CentralObjectRecoveryDispositions]
                    ([Id], [SourceObjectIdentitySha256], [SourceObjectKey], [Kind], [State],
                     [OperationToken], [ByteLength], [CreatedAtUtc], [UpdatedAtUtc], [AttemptCount])
                VALUES
                    (NEWID(), REPLICATE('E', 64), N'invalid/token-link.bin', N'ExpiredDelete',
                     N'PendingDelete', NEWID(), 3, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 0);
                """);
            await tokenWithoutArtifact.Should().ThrowAsync<SqlException>().ConfigureAwait(false);

            (await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralObjectRecoveryDispositions]
                    ([Id], [SourceObjectIdentitySha256], [SourceObjectKey], [Kind], [State], [CentralArtifactId],
                     [OperationToken], [ByteLength], [CreatedAtUtc], [UpdatedAtUtc], [AttemptCount],
                     [LastAttemptAtUtc], [NextAttemptAtUtc], [CompletedAtUtc], [ReasonCode])
                VALUES
                    ({Guid.NewGuid()}, {new string('F', 64)}, N'valid/completed.bin', N'ExpiredDelete', N'Completed',
                     {artifact.Id}, {token}, 3, {now.AddSeconds(-1)}, {now}, 1,
                     {now}, NULL, {now}, NULL);
                """).ConfigureAwait(false)).Should().Be(1);
            (await database.Context.CentralObjectRecoveryDispositions.AsNoTracking().CountAsync(item =>
                item.OperationToken == token && item.State == CentralObjectRecoveryStates.Completed)
                .ConfigureAwait(false)).Should().Be(1);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static MigrationDatabase CreateDatabase()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorArtifactDeletionMigration_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new(new ApplicationDbContext(options));
    }

    private sealed class MigrationDatabase(ApplicationDbContext context) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}
