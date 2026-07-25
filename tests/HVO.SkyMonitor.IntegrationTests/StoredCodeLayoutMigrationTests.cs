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
public sealed class StoredCodeLayoutMigrationTests
{
    private const string PreviousMigration = "20260724162853_AddDeploymentLocationAuthority";

    [TestMethod]
    public async Task LegacyAmbiguousLayoutInvalidatesResolvedDescendantLineage()
    {
        await using var database = CreateDatabase();
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
            var frameId = Guid.NewGuid();
            var rootId = Guid.NewGuid();
            var childId = Guid.NewGuid();
            var grandchildId = Guid.NewGuid();
            var rootArtifactId = Guid.NewGuid();
            var childArtifactId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var attemptId = Guid.NewGuid();
            var leaseToken = Guid.NewGuid();
            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralFrames]
                    ([Id], [RegistrationId], [DevicePublicId], [ObservatoryId], [AgentId], [FrameId],
                     [CapturedAtUtc], [FirstReceivedAtUtc], [RigProfileVersion], [SceneProvenanceJson])
                VALUES
                    ({frameId}, {Guid.NewGuid()}, {deviceId}, {Guid.NewGuid()}, {"stored-code-migration-agent"},
                     {Guid.NewGuid()}, {DateTimeOffset.UnixEpoch}, {DateTimeOffset.UnixEpoch}, NULL, NULL);

                INSERT INTO [CentralArtifacts]
                    ([Id], [CentralFrameId], [ArtifactId], [DevicePublicId], [Role], [RecipeVersion],
                     [ManifestSchemaVersion], [MediaType], [ByteLength], [ChecksumSha256], [StorageReference],
                     [ReceivedAtUtc], [IdempotencyKey], [ObjectState], [ReconstructionState])
                VALUES
                    ({rootId}, {frameId}, {rootArtifactId}, {deviceId}, {"Raw"}, {"raw-v1"},
                     {"v2"}, {"application/octet-stream"}, {8L}, {new string('A', 64)}, {"root.bin"},
                     {DateTimeOffset.UnixEpoch}, {new string('1', 64)}, {"Available"}, {"Complete"}),
                    ({childId}, {frameId}, {childArtifactId}, {deviceId}, {"Calibrated"}, {"cal-v1"},
                     {"v2"}, {"application/octet-stream"}, {8L}, {new string('B', 64)}, {"child.bin"},
                     {DateTimeOffset.UnixEpoch}, {new string('2', 64)}, {"Available"}, {"Complete"}),
                    ({grandchildId}, {frameId}, {Guid.NewGuid()}, {deviceId}, {"Combined"}, {"combine-v1"},
                     {"v2"}, {"application/octet-stream"}, {8L}, {new string('C', 64)}, {"grandchild.bin"},
                     {DateTimeOffset.UnixEpoch}, {new string('3', 64)}, {"Available"}, {"Complete"});

                INSERT INTO [CentralArtifactLayouts]
                    ([CentralArtifactId], [Width], [Height], [StrideBytes], [PixelFormat], [ByteOrder],
                     [SampleDepthBits], [ContainerDepthBits], [Packing], [CfaPattern], [BlackLevel],
                     [WhiteLevel], [ByteLength])
                VALUES
                    ({rootId}, 2, 2, 4, {"Mono16"}, {"LittleEndian"}, 12, 16, {"ByteAligned"},
                     {"None"}, 0, 4095, 8);

                INSERT INTO [CentralArtifactSources]
                    ([Id], [CentralArtifactId], [Ordinal], [SourceArtifactId], [ResolvedCentralArtifactId])
                VALUES
                    ({Guid.NewGuid()}, {childId}, 0, {rootArtifactId}, {rootId}),
                    ({Guid.NewGuid()}, {grandchildId}, 0, {childArtifactId}, {childId});

                INSERT INTO [CentralDerivativeJobs]
                    ([Id], [SourceCentralArtifactId], [TargetRole], [TargetRecipeVersion], [TargetVariant],
                     [RecipeName], [RecipeOptionsJson], [InputSelectorJson], [RequestedRecipeIdentitySha256],
                     [ExpectedRecipeIdentitySha256], [RequestIdentitySha256], [Status], [AttemptCount],
                     [MaxAttempts], [LeaseOwner], [LeaseToken], [LeaseAcquiredAtUtc], [LeaseExpiresAtUtc],
                     [CreatedAtUtc], [UpdatedAtUtc])
                VALUES
                    ({jobId}, {rootId}, {"Preview"}, {"preview-v1"}, {"preview"}, {"encoded-preview"},
                     {"{}"}, {"{}"}, {new string('D', 64)}, {new string('D', 64)}, {new string('E', 64)},
                     {"Leased"}, 1, 5, {"migration-worker"}, {leaseToken}, {DateTimeOffset.UnixEpoch},
                     {DateTimeOffset.UnixEpoch.AddMinutes(1)}, {DateTimeOffset.UnixEpoch}, {DateTimeOffset.UnixEpoch});

                INSERT INTO [CentralDerivativeJobAttempts]
                    ([Id], [CentralDerivativeJobId], [AttemptNumber], [WorkerId], [LeaseAcquiredAtUtc],
                     [LeaseExpiresAtUtc], [Outcome], [InputBytes], [OutputBytes], [RecipeDurationTicks])
                VALUES
                    ({attemptId}, {jobId}, 1, {"migration-worker"}, {DateTimeOffset.UnixEpoch},
                     {DateTimeOffset.UnixEpoch.AddMinutes(1)}, {"Leased"}, 8, 0, 0);
                """).ConfigureAwait(false);

            await migrator.MigrateAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            var artifacts = await database.Context.CentralArtifacts.AsNoTracking()
                .Where(item => item.Id == rootId || item.Id == childId || item.Id == grandchildId)
                .ToDictionaryAsync(item => item.Id).ConfigureAwait(false);
            artifacts[rootId].ReconstructionState.Should().Be(CentralReconstructionState.LegacyIncomplete);
            artifacts[rootId].StateReasonCode.Should().Be("layout.stored-code-ambiguous");
            artifacts[childId].ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
            artifacts[grandchildId].ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
            (await database.Context.CentralArtifactSources.AsNoTracking()
                .CountAsync(item => item.ResolvedCentralArtifactId != null).ConfigureAwait(false)).Should().Be(0);
            var job = await database.Context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.TerminalFailure);
            job.LeaseOwner.Should().BeNull();
            job.LeaseToken.Should().BeNull();
            job.StateReasonCode.Should().Be("source.layout-stored-code-ambiguous");
            var attempt = await database.Context.CentralDerivativeJobAttempts.AsNoTracking()
                .SingleAsync(item => item.Id == attemptId).ConfigureAwait(false);
            attempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.TerminalFailure);
            attempt.EndedAtUtc.Should().NotBeNull();
            attempt.ReasonCode.Should().Be("source.layout-stored-code-ambiguous");
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
            InitialCatalog = $"SkyMonitorStoredCodeMigration_{Guid.NewGuid():N}"
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
