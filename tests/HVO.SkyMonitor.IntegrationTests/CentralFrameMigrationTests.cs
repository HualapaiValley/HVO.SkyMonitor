using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class CentralFrameMigrationTests
{
    [TestMethod]
    public async Task NormalizeCentralFrames_BackfillsCompleteRowsAndPreservesLegacyRows()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorMigration_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(options);
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260713002059_AddArtifactIngestIdentity").ConfigureAwait(false);
            var observatory = new Observatory
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "migration-tests",
                Name = "Migration Observatory",
                TimeZoneId = "UTC",
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
                IsActive = true
            };
            var registration = new DeviceRegistration
            {
                Id = Guid.NewGuid(),
                DeviceId = $"migration-device-{Guid.NewGuid():N}",
                ObservatoryId = observatory.Id,
                ObservatoryName = observatory.Name,
                ObservatoryTimeZoneId = "UTC",
                FriendlyName = "Migration Device",
                OwnerUserId = "migration-tests",
                OwnerDisplayName = "Migration Tests",
                OwnerConfirmationMethod = "SelfAttested",
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = DeviceRegistrationService.ComputeSha256("ABCDE"),
                IssuedAtUtc = DateTimeOffset.UnixEpoch,
                DevicePublicId = Guid.NewGuid(),
                DeviceKeyHash = DeviceRegistrationService.ComputeSha256("migration-key")
            };
            var frameId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [Observatories]
                    ([Id], [OwnerUserId], [Name], [LatitudeDegrees], [LongitudeDegrees], [ElevationMeters],
                     [TimeZoneId], [CreatedAtUtc], [UpdatedAtUtc], [IsActive])
                VALUES
                    ({observatory.Id}, {observatory.OwnerUserId}, {observatory.Name}, {observatory.LatitudeDegrees},
                     {observatory.LongitudeDegrees}, {observatory.ElevationMeters}, {observatory.TimeZoneId},
                     {observatory.CreatedAtUtc}, NULL, {observatory.IsActive});

                INSERT INTO [DeviceRegistrations]
                    ([Id], [DeviceId], [ObservatoryId], [FriendlyName], [ObservatoryName],
                     [ObservatoryLatitudeDegrees], [ObservatoryLongitudeDegrees], [ObservatoryElevationMeters],
                     [ObservatoryTimeZoneId], [OwnerUserId], [OwnerDisplayName], [OwnerConfirmationMethod],
                     [Status], [VerificationCodeHash], [DevicePublicId], [DeviceKeyHash], [IssuedAtUtc])
                VALUES
                    ({registration.Id}, {registration.DeviceId}, {registration.ObservatoryId},
                     {registration.FriendlyName}, {registration.ObservatoryName},
                     {registration.ObservatoryLatitudeDegrees}, {registration.ObservatoryLongitudeDegrees},
                     {registration.ObservatoryElevationMeters}, {registration.ObservatoryTimeZoneId},
                     {registration.OwnerUserId}, {registration.OwnerDisplayName},
                     {registration.OwnerConfirmationMethod}, {registration.Status.ToString()},
                     {registration.VerificationCodeHash}, {registration.DevicePublicId},
                     {registration.DeviceKeyHash}, {registration.IssuedAtUtc});
                """).ConfigureAwait(false);
            var duplicateArtifactId = Guid.NewGuid();
            var duplicateA = CreateCompleteUpload(
                registration, Guid.NewGuid(), "Raw", "raw-v1", "minio://artifacts/duplicate-a.bin");
            var duplicateB = CreateCompleteUpload(
                registration, Guid.NewGuid(), "Raw", "raw-v1", "minio://artifacts/duplicate-b.bin");
            duplicateA.ArtifactId = duplicateArtifactId;
            duplicateB.ArtifactId = duplicateArtifactId;
            db.DeviceImageUploads.AddRange(
                CreateCompleteUpload(registration, frameId, "Raw", "raw-v1", "minio://artifacts/raw.bin"),
                CreateCompleteUpload(registration, frameId, "Preview", "preview-v1", "minio://artifacts/preview.bin"),
                duplicateA,
                duplicateB,
                CreateCompleteUpload(new DeviceRegistration
                {
                    Id = Guid.NewGuid(),
                    DeviceId = $"orphan-device-{Guid.NewGuid():N}",
                    DevicePublicId = Guid.NewGuid(),
                    ObservatoryId = Guid.NewGuid()
                }, Guid.NewGuid(), "Raw", "raw-v1", "minio://artifacts/orphan.bin"),
                new DeviceImageUpload
                {
                    RegistrationId = registration.Id,
                    DevicePublicId = registration.DevicePublicId.Value,
                    ObservatoryId = observatory.Id,
                    CapturedAtUtc = DateTimeOffset.UnixEpoch,
                    ReceivedAtUtc = DateTimeOffset.UnixEpoch,
                    ContentType = "application/octet-stream",
                    PayloadBase64Length = 4,
                    StorageReference = "stubs://legacy/incomplete"
                });
            await db.SaveChangesAsync().ConfigureAwait(false);

            await migrator.MigrateAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();

            var frame = await db.CentralFrames.Include(item => item.Artifacts)
                .SingleAsync(item => item.FrameId == frameId).ConfigureAwait(false);
            frame.FrameId.Should().Be(frameId);
            frame.Artifacts.Should().HaveCount(2);
            frame.Artifacts.Should().OnlyContain(artifact =>
                artifact.ReconstructionState == CentralReconstructionState.LegacyIncomplete
                && artifact.ObjectState == CentralArtifactObjectState.Available
                && artifact.StateReasonCode == "manifest.legacy-incomplete"
                && artifact.SourceId == null && artifact.Variant == null);
            (await db.CentralArtifactIngestIdentities.CountAsync().ConfigureAwait(false)).Should().Be(5);
            (await db.CentralArtifactLayouts.CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await db.CentralArtifactRecipes.CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await db.CentralCaptureProfiles.CountAsync().ConfigureAwait(false)).Should().Be(0);
            frame.Artifacts.Select(artifact => artifact.StorageReference).Should().BeEquivalentTo(
                "minio://artifacts/raw.bin", "minio://artifacts/preview.bin");
            (await db.CentralArtifacts.CountAsync(artifact => artifact.ArtifactId == duplicateArtifactId)
                .ConfigureAwait(false)).Should().Be(2);
            (await db.CentralArtifacts.Where(artifact => artifact.ArtifactId == duplicateArtifactId)
                .AllAsync(artifact => artifact.DevicePublicId == null).ConfigureAwait(false)).Should().BeTrue();
            (await db.CentralArtifacts.CountAsync(artifact => artifact.DevicePublicId != null)
                .ConfigureAwait(false)).Should().Be(3);
            (await db.CentralFrames.CountAsync().ConfigureAwait(false)).Should().Be(4);
            (await db.DeviceImageUploads.CountAsync().ConfigureAwait(false)).Should().Be(6);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }


    [TestMethod]
    public async Task NormalizeCentralFrames_RejectsConflictingRigVersions()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorMigration_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(options);
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260713002059_AddArtifactIngestIdentity").ConfigureAwait(false);
            var registration = new DeviceRegistration
            {
                Id = Guid.NewGuid(),
                DeviceId = $"conflict-device-{Guid.NewGuid():N}",
                DevicePublicId = Guid.NewGuid(),
                ObservatoryId = Guid.NewGuid()
            };
            var frameId = Guid.NewGuid();
            var raw = CreateCompleteUpload(registration, frameId, "Raw", "raw-v1", "minio://artifacts/raw.bin");
            var preview = CreateCompleteUpload(registration, frameId, "Preview", "preview-v1", "minio://artifacts/preview.bin");
            raw.RigProfileVersion = 1;
            preview.RigProfileVersion = 2;
            db.DeviceImageUploads.AddRange(raw, preview);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var migration = async () => await migrator.MigrateAsync().ConfigureAwait(false);

            await migration.Should().ThrowAsync<Exception>().WithMessage("*Conflicting frame metadata*");
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static DeviceImageUpload CreateCompleteUpload(
        DeviceRegistration registration,
        Guid frameId,
        string role,
        string recipeVersion,
        string storageReference)
    {
        var artifactId = Guid.NewGuid();
        return new DeviceImageUpload
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId!.Value,
            ObservatoryId = registration.ObservatoryId,
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            ReceivedAtUtc = DateTimeOffset.UnixEpoch,
            ContentType = "application/octet-stream",
            PayloadBase64Length = 0,
            StorageReference = storageReference,
            IdempotencyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(artifactId.ToByteArray())),
            ArtifactId = artifactId,
            FrameId = frameId,
            ArtifactRole = role,
            RecipeVersion = recipeVersion,
            ManifestSchemaVersion = "v1",
            ChecksumSha256 = new string('A', 64),
            ByteLength = 4,
            AgentId = registration.DeviceId,
            SceneProvenanceJson = "{\"sceneId\":\"migration-scene\"}"
        };
    }
}
