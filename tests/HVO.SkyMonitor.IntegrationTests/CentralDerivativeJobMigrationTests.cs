using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class CentralDerivativeJobMigrationTests
{
    [TestMethod]
    public async Task AddCentralDerivativeJobs_BackfillsRawAndExistingTarget()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorDerivativeMigration_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(options);
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260713051609_NormalizeCentralFrames").ConfigureAwait(false);
            var now = DateTimeOffset.UnixEpoch;
            var frame = new CentralFrame
            {
                RegistrationId = Guid.NewGuid(),
                DevicePublicId = Guid.NewGuid(),
                ObservatoryId = Guid.NewGuid(),
                AgentId = "migration-agent",
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = now,
                FirstReceivedAtUtc = now
            };
            var raw = CreateArtifact(frame, FrameArtifactRole.Raw, "raw-v1", now);
            var preview = CreateArtifact(frame, FrameArtifactRole.Preview,
                CentralDerivativeRecipeCatalog.PreviewRecipeVersion, now.AddSeconds(-1));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralFrames]
                    ([Id], [RegistrationId], [DevicePublicId], [ObservatoryId], [AgentId], [FrameId],
                     [CapturedAtUtc], [FirstReceivedAtUtc], [RigProfileVersion], [SceneProvenanceJson])
                VALUES
                    ({frame.Id}, {frame.RegistrationId}, {frame.DevicePublicId}, {frame.ObservatoryId}, {frame.AgentId},
                     {frame.FrameId}, {frame.CapturedAtUtc}, {frame.FirstReceivedAtUtc}, NULL, NULL);
                """).ConfigureAwait(false);
            await InsertArtifactAsync(db, raw).ConfigureAwait(false);
            await InsertArtifactAsync(db, preview).ConfigureAwait(false);

            await migrator.MigrateAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();

            var jobs = await db.CentralDerivativeJobs.Include(job => job.ResultArtifact)
                .OrderBy(job => job.TargetRole).ToListAsync().ConfigureAwait(false);
            jobs.Should().HaveCount(2);
            var previewJob = jobs.Single(job => job.TargetRole == FrameArtifactRole.Preview);
            previewJob.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            previewJob.ResultArtifact!.ArtifactId.Should().Be(preview.ArtifactId);
            previewJob.CompletedAtUtc.Should().Be(raw.ReceivedAtUtc);
            previewJob.UpdatedAtUtc.Should().Be(raw.ReceivedAtUtc);
            jobs.Single(job => job.TargetRole == FrameArtifactRole.AnnotatedPreview).Status
                .Should().Be(CentralDerivativeJobStatus.Pending);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static CentralArtifact CreateArtifact(
        CentralFrame frame,
        FrameArtifactRole role,
        string recipeVersion,
        DateTimeOffset receivedAtUtc)
        => new()
        {
            CentralFrameId = frame.Id,
            Frame = frame,
            ArtifactId = Guid.NewGuid(),
            Role = role,
            RecipeVersion = recipeVersion,
            ManifestSchemaVersion = "v1",
            MediaType = "application/octet-stream",
            ByteLength = 4,
            ChecksumSha256 = new string('A', 64),
            StorageReference = $"minio://migration/{Guid.NewGuid():N}",
            ReceivedAtUtc = receivedAtUtc,
            IdempotencyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()))
        };

    private static Task<int> InsertArtifactAsync(ApplicationDbContext db, CentralArtifact artifact)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [CentralArtifacts]
                ([Id], [CentralFrameId], [ArtifactId], [Role], [RecipeVersion], [ManifestSchemaVersion],
                 [MediaType], [ByteLength], [ChecksumSha256], [StorageReference], [ReceivedAtUtc], [IdempotencyKey])
            VALUES
                ({artifact.Id}, {artifact.CentralFrameId}, {artifact.ArtifactId}, {artifact.Role.ToString()},
                 {artifact.RecipeVersion}, {artifact.ManifestSchemaVersion}, {artifact.MediaType}, {artifact.ByteLength},
                 {artifact.ChecksumSha256}, {artifact.StorageReference}, {artifact.ReceivedAtUtc}, {artifact.IdempotencyKey});
            """);
}
