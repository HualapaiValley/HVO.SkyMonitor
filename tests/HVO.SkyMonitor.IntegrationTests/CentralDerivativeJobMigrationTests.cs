using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
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
    public async Task AddCentralDerivativeJobs_BackfillsCompleteRawAndExistingTarget()
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

            await migrator.MigrateAsync("20260715102514_EnforceDeviceArtifactIdentity").ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralArtifacts]
                SET [ObjectState] = N'Available', [ReconstructionState] = N'Complete',
                    [ManifestSchemaVersion] = N'v2'
                WHERE [Id] = {raw.Id};

                UPDATE [CentralDerivativeJobs]
                SET [Status] = N'Leased', [AttemptCount] = 1,
                    [LeaseOwner] = N'migration-active-worker', [LeaseToken] = {Guid.NewGuid()},
                    [LeaseAcquiredAtUtc] = {now}, [LeaseExpiresAtUtc] = {now.AddMinutes(1)},
                    [AvailableAtUtc] = NULL
                WHERE [SourceCentralArtifactId] = {raw.Id} AND [TargetRole] = N'AnnotatedPreview';
                """).ConfigureAwait(false);
            await migrator.MigrateAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();

            var jobs = await db.CentralDerivativeJobs.Include(job => job.ResultArtifact)
                .Include(job => job.InputRequirements)
                .Include(job => job.Inputs)
                .AsSplitQuery()
                .OrderBy(job => job.TargetRole).ToListAsync().ConfigureAwait(false);
            jobs.Should().HaveCount(3);
            var previewJob = jobs.Single(job => job.TargetRole == FrameArtifactRole.Preview);
            previewJob.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            previewJob.ResultArtifact!.ArtifactId.Should().Be(preview.ArtifactId);
            previewJob.CompletedAtUtc.Should().Be(raw.ReceivedAtUtc);
            previewJob.UpdatedAtUtc.Should().Be(raw.ReceivedAtUtc);
            previewJob.TargetVariant.Should().Be(CentralDerivativeRecipeCatalog.PreviewVariant);
            previewJob.RequestedRecipeIdentitySha256.Should().Be(CentralDerivativeRecipeCatalog.PreviewRequestedRecipeIdentity);
            previewJob.RequestIdentitySha256.Should().Be(CentralDerivativeJobIdentity.CreateRequestIdentity(
                frame.DevicePublicId,
                raw.ArtifactId,
                new CentralDerivativeRecipeCatalog().GetRequiredRecipes(FrameArtifactRole.Raw)
                    .Single(recipe => recipe.TargetRole == FrameArtifactRole.Preview)));
            var annotated = jobs.Single(job => job.TargetRole == FrameArtifactRole.AnnotatedPreview);
            annotated.Status.Should().Be(CentralDerivativeJobStatus.RetryableFailure);
            annotated.LastError.Should().Be(
                "The active lease was reset during the central derivative execution migration.");
            annotated.AvailableAtUtc.Should().NotBeNull();
            annotated.LeaseToken.Should().BeNull();
            annotated.RequestedRecipeIdentitySha256.Should()
                .Be(CentralDerivativeRecipeCatalog.AnnotatedPreviewRequestedRecipeIdentity);
            var quality = jobs.Single(job => job.TargetRole == FrameArtifactRole.Metadata);
            quality.Status.Should().Be(CentralDerivativeJobStatus.Pending);
            quality.TargetRecipeVersion.Should().Be(CentralDerivativeRecipeCatalog.ImageQualityRecipeVersion);
            quality.TargetVariant.Should().Be(CentralDerivativeRecipeCatalog.ImageQualityVariant);
            quality.RecipeName.Should().Be(BuiltInProcessingRecipes.ImageQuality);
            quality.RequestedRecipeIdentitySha256.Should()
                .Be(CentralDerivativeRecipeCatalog.ImageQualityRequestedRecipeIdentity);
            quality.RequestIdentitySha256.Should().Be(CentralDerivativeJobIdentity.CreateRequestIdentity(
                frame.DevicePublicId,
                raw.ArtifactId,
                new CentralDerivativeRecipeCatalog().GetRequiredRecipes(FrameArtifactRole.Raw)
                    .Single(recipe => recipe.TargetRole == FrameArtifactRole.Metadata)));
            jobs.Should().OnlyHaveUniqueItems(job => job.RequestIdentitySha256);
            jobs.Should().OnlyContain(job => job.InputRequirements.Count == 1
                && job.Inputs.Count == 1
                && job.Inputs.Single().CentralArtifactId == job.SourceCentralArtifactId
                && job.Inputs.Single().Ordinal == 0
                && job.InputSetIdentitySha256 != null
                && job.ResolutionCompletedAtUtc != null);
            (await db.CentralDerivativeJobAttempts.CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await db.CentralArtifactProcessingEvidence.CountAsync().ConfigureAwait(false)).Should().Be(0);

            db.CentralDerivativeJobs.Add(new CentralDerivativeJob
            {
                SourceCentralArtifactId = quality.SourceCentralArtifactId,
                TargetRole = quality.TargetRole,
                TargetRecipeVersion = quality.TargetRecipeVersion,
                TargetVariant = "alternate-quality",
                RecipeName = quality.RecipeName,
                RecipeOptionsJson = quality.RecipeOptionsJson,
                InputSelectorJson = quality.InputSelectorJson,
                RequestedRecipeIdentitySha256 = quality.RequestedRecipeIdentitySha256,
                RequestIdentitySha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())),
                Status = CentralDerivativeJobStatus.Pending,
                MaxAttempts = quality.MaxAttempts,
                AvailableAtUtc = now,
                CreatedAtUtc = now.AddSeconds(1),
                UpdatedAtUtc = now.AddSeconds(1)
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
            await migrator.MigrateAsync("20260715102514_EnforceDeviceArtifactIdentity").ConfigureAwait(false);
            var legacyJobCount = await db.Database.SqlQuery<int>(
                    $"SELECT COUNT(*) AS [Value] FROM [CentralDerivativeJobs]")
                .SingleAsync().ConfigureAwait(false);
            legacyJobCount.Should().Be(3);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task AddCentralDerivativeJobs_PreservesLegacyDuplicateArtifactIdentities()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorDerivativeDuplicateMigration_{Guid.NewGuid():N}"
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
            var devicePublicId = Guid.NewGuid();
            var duplicateArtifactId = Guid.NewGuid();
            var firstFrame = CreateFrame(devicePublicId, "duplicate-agent-1", now);
            var secondFrame = CreateFrame(devicePublicId, "duplicate-agent-2", now.AddSeconds(1));
            var firstRaw = CreateArtifact(firstFrame, FrameArtifactRole.Raw, "raw-v1", now);
            firstRaw.ArtifactId = duplicateArtifactId;
            var secondRaw = CreateArtifact(secondFrame, FrameArtifactRole.Raw, "raw-v1", now.AddSeconds(1));
            secondRaw.ArtifactId = duplicateArtifactId;
            await InsertFrameAsync(db, firstFrame).ConfigureAwait(false);
            await InsertFrameAsync(db, secondFrame).ConfigureAwait(false);
            await InsertArtifactAsync(db, firstRaw).ConfigureAwait(false);
            await InsertArtifactAsync(db, secondRaw).ConfigureAwait(false);

            await migrator.MigrateAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();

            var sources = await db.CentralArtifacts.Where(item => item.ArtifactId == duplicateArtifactId)
                .ToListAsync().ConfigureAwait(false);
            sources.Should().HaveCount(2);
            sources.Should().OnlyContain(source => source.DevicePublicId == null);
            var jobs = await db.CentralDerivativeJobs.Where(job => sources.Select(source => source.Id)
                    .Contains(job.SourceCentralArtifactId))
                .ToListAsync().ConfigureAwait(false);
            jobs.Should().HaveCount(4);
            jobs.Should().OnlyHaveUniqueItems(job => job.RequestIdentitySha256);
            jobs.Should().OnlyContain(job => job.Status == CentralDerivativeJobStatus.Skipped);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static CentralFrame CreateFrame(Guid devicePublicId, string agentId, DateTimeOffset now)
        => new()
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = devicePublicId,
            ObservatoryId = Guid.NewGuid(),
            AgentId = agentId,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = now,
            FirstReceivedAtUtc = now
        };

    private static Task<int> InsertFrameAsync(ApplicationDbContext db, CentralFrame frame)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [CentralFrames]
                ([Id], [RegistrationId], [DevicePublicId], [ObservatoryId], [AgentId], [FrameId],
                 [CapturedAtUtc], [FirstReceivedAtUtc], [RigProfileVersion], [SceneProvenanceJson])
            VALUES
                ({frame.Id}, {frame.RegistrationId}, {frame.DevicePublicId}, {frame.ObservatoryId}, {frame.AgentId},
                 {frame.FrameId}, {frame.CapturedAtUtc}, {frame.FirstReceivedAtUtc}, NULL, NULL);
            """);

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
