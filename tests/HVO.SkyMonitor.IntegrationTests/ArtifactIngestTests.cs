using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Identity;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class ArtifactIngestTests
{
    [TestMethod]
    public async Task MultipartIngest_WithoutBearerToken_IsUnauthorized()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, _) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        var manifest = new ArtifactUploadManifest(
            "v1", deviceId, Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch,
            "raw-v1", "frames/raw.bin");

        using var response = await PostAsync(client, manifest).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task MultipartIngest_IsIdempotentForManifestKey()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var scene = new SceneProvenance(
            "scene-id", "rig-v1", "HYG", "4.2", new string('A', 64),
            "EquidistantFisheye", "projection-v1", "scene-v1", "sensor-v1");
        var manifest = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin", scene);

        using var first = await PostAsync(client, manifest).ConfigureAwait(false);
        using var second = await PostAsync(client, manifest).ConfigureAwait(false);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var firstAcknowledgement = await first.Content.ReadFromJsonAsync<ArtifactUploadAcknowledgement>().ConfigureAwait(false);
        var duplicateAcknowledgement = await second.Content.ReadFromJsonAsync<ArtifactUploadAcknowledgement>().ConfigureAwait(false);
        firstAcknowledgement.Should().NotBeNull();
        duplicateAcknowledgement.Should().BeEquivalentTo(firstAcknowledgement);
        firstAcknowledgement!.SchemaVersion.Should().Be(ArtifactUploadAcknowledgement.CurrentSchemaVersion);
        firstAcknowledgement.IdempotencyKey.Should().Be(manifest.IdempotencyKey);
        firstAcknowledgement.ArtifactId.Should().Be(manifest.ArtifactId);
        firstAcknowledgement.ChecksumSha256.Should().Be(manifest.ChecksumSha256);
        firstAcknowledgement.ByteLength.Should().Be(manifest.ByteLength);
        firstAcknowledgement.AcceptedManifestSchemaVersion.Should().Be(ArtifactUploadManifest.CurrentSchemaVersion);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var frame = await db.CentralFrames.Include(item => item.Artifacts).SingleAsync(
            item => item.RegistrationId == registrationId).ConfigureAwait(false);
        frame.SceneProvenanceJson.Should().Contain("scene-id");
        frame.FrameId.Should().Be(manifest.FrameId);
        frame.Artifacts.Should().ContainSingle();
        frame.Artifacts.Single().RecipeVersion.Should().Be(manifest.RecipeVersion);
        frame.Artifacts.Single().ManifestSchemaVersion.Should().Be(manifest.SchemaVersion);
        frame.Artifacts.Single().ReconstructionState.Should().Be(CentralReconstructionState.LegacyIncomplete);
        var jobs = await db.CentralDerivativeJobs.Where(job => job.SourceArtifact!.CentralFrameId == frame.Id)
            .ToListAsync().ConfigureAwait(false);
        jobs.Should().BeEmpty();
    }

    [TestMethod]
    public async Task MultipartIngest_BindsInstallationActiveAtCaptureTime()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var capturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        Guid installationId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId)
                .ConfigureAwait(false);
            var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email)
                .ConfigureAwait(false);
            var camera = new LogicalCamera
            {
                ObservatoryId = registration.ObservatoryId,
                Slug = "capture-time-camera",
                Name = "Capture-time camera",
                Description = "Installation binding fixture",
                CreatedAtUtc = capturedAtUtc.AddDays(-1),
                CreatedByUserId = owner.Id
            };
            var installation = new LogicalCameraInstallation
            {
                LogicalCamera = camera,
                LogicalCameraId = camera.Id,
                RegistrationId = registration.Id,
                InstallationPublicId = Guid.NewGuid(),
                AssignedAtUtc = capturedAtUtc.AddHours(-1),
                AssignedByUserId = owner.Id,
                AssignmentReasonCode = "integration-binding"
            };
            installationId = installation.Id;
            db.AddRange(camera, installation);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var manifest = new ArtifactUploadManifest(
            "v1",
            deviceId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            FrameArtifactRole.Raw,
            "application/octet-stream",
            4,
            PayloadChecksum,
            capturedAtUtc,
            "raw-v1",
            "frames/raw.bin");

        using var response = await PostAsync(client, manifest).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var verifyScope = fixture.Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await verifyDb.CentralFrames.Where(item => item.FrameId == manifest.FrameId)
            .Select(item => item.LogicalCameraInstallationId)
            .SingleAsync().ConfigureAwait(false)).Should().Be(installationId);
    }

    [TestMethod]
    public async Task LegacyDuplicatePublisher_WaitsForCanonicalObjectApplicationLock()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, _) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var manifest = new ArtifactUploadManifest(
            "v1", deviceId, Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch,
            "raw-v1", "frames/raw.bin");
        using var first = await PostAsync(client, manifest).ConfigureAwait(false);
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await using var lockScope = fixture.Factory.Services.CreateAsyncScope();
        var db = lockScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var storageReference = await db.CentralArtifacts.Where(item => item.IdempotencyKey == manifest.IdempotencyKey)
            .Select(item => item.StorageReference).SingleAsync().ConfigureAwait(false);
        var objectLock = await CentralObjectApplicationLock.AcquireAsync(
            db, storageReference, CancellationToken.None).ConfigureAwait(false);
        try
        {
            var duplicate = PostAsync(client, manifest);
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            duplicate.IsCompleted.Should().BeFalse("legacy duplicate reconciliation publishes ownership under the shared lock");

            await objectLock.DisposeAsync().ConfigureAwait(false);
            using var response = await duplicate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        finally
        {
            await objectLock.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task MultipartIngestV2_PersistsReconstructableFactsAndBindsHistoricalRig()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("rig-profile-a");
        Guid historicalProfileId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
            var profile = new DeviceRigProfile
            {
                RegistrationId = registration.Id,
                DevicePublicId = registration.DevicePublicId!.Value,
                ObservatoryId = registration.ObservatoryId,
                Version = 1,
                ConfigHash = new string('1', 64),
                ConfigJson = JsonSerializer.Serialize(rig),
                ProfileName = "rig",
                ProfileVersion = rig.ProfileVersion,
                ProfileSha256 = CameraRigProfileIdentity.ComputeSha256(rig),
                CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
                EffectiveFromUtc = DateTimeOffset.UnixEpoch.AddDays(-1)
            };
            historicalProfileId = profile.Id;
            var currentRig = CreateRig("rig-profile-b");
            db.DeviceRigProfiles.AddRange(profile, new DeviceRigProfile
            {
                RegistrationId = registration.Id,
                DevicePublicId = registration.DevicePublicId.Value,
                ObservatoryId = registration.ObservatoryId,
                Version = 2,
                ConfigHash = new string('2', 64),
                ConfigJson = JsonSerializer.Serialize(currentRig),
                ProfileName = "rig",
                ProfileVersion = currentRig.ProfileVersion,
                ProfileSha256 = CameraRigProfileIdentity.ComputeSha256(currentRig),
                CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(1),
                EffectiveFromUtc = DateTimeOffset.UnixEpoch.AddDays(1)
            });
            registration.CurrentRigProfileVersion = 2;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, captureSequence: 7);
        manifest = manifest with
        {
            Descriptor = manifest.Descriptor with
            {
                Layout = manifest.Descriptor.Layout with
                {
                    Readout = new FrameReadoutDescriptor(
                        4, 4, 0, 0, 4, 4, 2, 2,
                        FrameBinningAlgorithm.DigitalAverageV1, null, null)
                }
            }
        };
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var acknowledgement = await response.Content.ReadFromJsonAsync<ArtifactUploadAcknowledgement>().ConfigureAwait(false);
        acknowledgement!.AcceptedManifestSchemaVersion.Should().Be(ArtifactManifestV2.CurrentSchemaVersion);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await assertionDb.CentralArtifacts
            .Include(item => item.Layout)
            .Include(item => item.Recipe)
            .Include(item => item.Sources)
            .Include(item => item.IngestIdentities)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Control)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Profiles)
            .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.Frame!.CaptureSequence.Should().Be(7);
        artifact.Frame.DeviceRigProfileId.Should().Be(historicalProfileId);
        artifact.Frame.RigProfileVersion.Should().Be(1);
        artifact.Layout!.StrideBytes.Should().Be(2);
        artifact.Layout.StoredCodeTransform.Should().BeNull();
        artifact.Layout.LevelCodeSpace.Should().BeNull();
        artifact.Recipe!.OptionsSha256.Should().Be(manifest.Descriptor.Artifact.Recipe.OptionsSha256);
        artifact.IngestIdentities.Should().ContainSingle(item => item.IdempotencyKey == manifest.IdempotencyKey);
        var persistedDescriptor = CentralReconstructionDescriptorFactory.Create(artifact.Frame, artifact);
        persistedDescriptor.Layout.Should().Be(manifest.Descriptor.Layout);
        persistedDescriptor.Layout.StoredCodeTransform.Should().BeNull();
        persistedDescriptor.Layout.LevelCodeSpace.Should().BeNull();
        CaptureContractJson.ComputeDescriptorSha256(persistedDescriptor).Should().Be(manifest.IdempotencyKey);
        FrameReconstructor.TryReconstruct(persistedDescriptor, payload, out var frame).IsValid.Should().BeTrue();
        frame!.PixelData.ToArray().Should().Equal(payload);
        var descriptor = manifest.Descriptor;
        var legacyAlias = new ArtifactUploadManifest(
            "v1", deviceId, descriptor.Artifact.ArtifactId, descriptor.Capture.CaptureId,
            descriptor.Artifact.Role, descriptor.Artifact.MediaType, descriptor.Layout.ByteLength,
            descriptor.Artifact.ChecksumSha256, descriptor.Timing.ExposureStartedUtc,
            $"v2-{manifest.IdempotencyKey}", manifest.RelativeArtifactPath);

        using var legacyAliasResponse = await PostAsync(client, legacyAlias).ConfigureAwait(false);

        legacyAliasResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        assertionDb.ChangeTracker.Clear();
        (await assertionDb.CentralArtifactIngestIdentities.CountAsync(
            item => item.CentralArtifactId == artifact.Id).ConfigureAwait(false)).Should().Be(2);

        await assertionDb.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId == artifact.Id
                && job.TargetRole != FrameArtifactRole.Metadata)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
        Guid resultArtifactId;
        await using (var workerScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var jobService = workerScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            var lease = await jobService.ClaimNextAsync(
                "integration-worker", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.RecipeName.Should().Be(BuiltInProcessingRecipes.ImageQuality);
            var executor = workerScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>();
            var execution = await executor.ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            execution.Status.Should().Be(ProcessingOutcomeStatus.Produced);
            resultArtifactId = execution.ArtifactId!.Value;
        }
        using var ownerClient = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        using var derivativeResponse = await ownerClient.GetAsync(new Uri(
            $"/api/v1.0/devices/{artifact.Frame!.DevicePublicId:D}/artifacts/{resultArtifactId:D}/content",
            UriKind.Relative)).ConfigureAwait(false);
        derivativeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var quality = JsonDocument.Parse(
            await derivativeResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        quality.RootElement.GetProperty("minimum").GetInt32().Should().Be(1);
        quality.RootElement.GetProperty("maximum").GetInt32().Should().Be(4);

        await using var evidenceScope = fixture.Factory.Services.CreateAsyncScope();
        var evidenceDb = evidenceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var result = await evidenceDb.CentralArtifacts.Include(item => item.Sources).Include(item => item.Recipe)
            .SingleAsync(item => item.ArtifactId == resultArtifactId).ConfigureAwait(false);
        result.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        result.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        result.RecipeVersion.Should().Be("central-image-quality-v1");
        result.Recipe!.ImplementationVersion.Should().Be("integer-image-statistics-v1");
        result.Sources.Should().ContainSingle(item => item.ResolvedCentralArtifactId == artifact.Id);
        var evidence = await evidenceDb.CentralArtifactProcessingEvidence
            .SingleAsync(item => item.CentralArtifactId == result.Id).ConfigureAwait(false);
        result.IdempotencyKey.Should().Be(CentralDerivativeOutputWriter.CreateArtifactIdempotencyKey(
            result.DevicePublicId!.Value, evidence.OutputIdentitySha256));
        evidence.AlgorithmsJson.Should().Contain("image-statistics");
        var attempt = await evidenceDb.CentralDerivativeJobAttempts
            .SingleAsync(item => item.CentralDerivativeJobId == evidence.CentralDerivativeJobId).ConfigureAwait(false);
        attempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.Completed);
        attempt.InputBytes.Should().Be(payload.LongLength);
        attempt.OutputBytes.Should().Be(result.ByteLength);

        var completedJob = await evidenceDb.CentralDerivativeJobs
            .SingleAsync(item => item.Id == evidence.CentralDerivativeJobId).ConfigureAwait(false);
        completedJob.Status = CentralDerivativeJobStatus.RetryableFailure;
        completedJob.AvailableAtUtc = DateTimeOffset.UtcNow;
        completedJob.CompletedAtUtc = null;
        completedJob.ResultCentralArtifactId = null;
        result.ObjectState = CentralArtifactObjectState.Pending;
        result.StateReasonCode = "derivative.output-pending";
        attempt.Outcome = CentralDerivativeAttemptOutcome.LeaseExpired;
        attempt.ReasonCode = "lease.expired";
        await evidenceDb.SaveChangesAsync().ConfigureAwait(false);

        await using (var recoveryScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var recoveryJobs = recoveryScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            var recoveryLease = await recoveryJobs.ClaimNextAsync(
                "recovery-worker", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            recoveryLease.Should().NotBeNull();
            var recoveryExecutor = recoveryScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>();
            var recovered = await recoveryExecutor.ExecuteAsync(recoveryLease!, CancellationToken.None).ConfigureAwait(false);
            recovered.ArtifactId.Should().Be(resultArtifactId);
            recovered.ReasonCode.Should().Be("derivative.output-recovered");
        }
        evidenceDb.ChangeTracker.Clear();
        (await evidenceDb.CentralArtifacts.CountAsync(item => item.ArtifactId == resultArtifactId).ConfigureAwait(false))
            .Should().Be(1);
        var recoveredAttempts = await evidenceDb.CentralDerivativeJobAttempts
            .Where(item => item.CentralDerivativeJobId == evidence.CentralDerivativeJobId)
            .OrderBy(item => item.AttemptNumber)
            .ToListAsync().ConfigureAwait(false);
        recoveredAttempts.Select(item => item.Outcome).Should().Equal(
            CentralDerivativeAttemptOutcome.LeaseExpired,
            CentralDerivativeAttemptOutcome.Completed);
        recoveredAttempts[1].RecipeDurationTicks.Should().Be(0);

        var corruptSource = await evidenceDb.CentralArtifacts.SingleAsync(item => item.Id == artifact.Id)
            .ConfigureAwait(false);
        var originalChecksum = corruptSource.ChecksumSha256;
        var sourceObjectKey = corruptSource.StorageReference["minio://skymonitor-artifacts/".Length..];
        var minio = evidenceScope.ServiceProvider.GetRequiredService<IMinioClient>();
        var corruptPayload = payload.Reverse().ToArray();
        await using (var corruptStream = new MemoryStream(corruptPayload))
        {
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket("skymonitor-artifacts")
                .WithObject(sourceObjectKey)
                .WithStreamData(corruptStream)
                .WithObjectSize(corruptStream.Length)
                .WithContentType(corruptSource.MediaType)).ConfigureAwait(false);
        }
        var corruptJob = await evidenceDb.CentralDerivativeJobs.SingleAsync(item =>
            item.SourceCentralArtifactId == artifact.Id && item.TargetRole == FrameArtifactRole.Preview)
            .ConfigureAwait(false);
        corruptJob.Status = CentralDerivativeJobStatus.Pending;
        corruptJob.AvailableAtUtc = DateTimeOffset.UtcNow;
        corruptJob.LastError = null;
        await evidenceDb.SaveChangesAsync().ConfigureAwait(false);
        Guid corruptJobId = corruptJob.Id;
        await using (var corruptScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var corruptJobs = corruptScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            var corruptLease = await corruptJobs.ClaimNextAsync(
                "integrity-worker", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            corruptLease!.JobId.Should().Be(corruptJobId);
            var corruptExecutor = corruptScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>();
            await FluentActions.Awaiting(() => corruptExecutor.ExecuteAsync(corruptLease, CancellationToken.None))
                .Should().ThrowAsync<CentralArtifactIntegrityException>().ConfigureAwait(false);
        }
        evidenceDb.ChangeTracker.Clear();
        var quarantinedSource = await evidenceDb.CentralArtifacts.SingleAsync(item => item.Id == artifact.Id)
            .ConfigureAwait(false);
        quarantinedSource.ObjectState.Should().Be(CentralArtifactObjectState.Quarantined);
        quarantinedSource.ReconstructionState.Should().Be(CentralReconstructionState.Quarantined);
        quarantinedSource.ChecksumSha256.Should().Be(originalChecksum);
        var invalidatedResult = await evidenceDb.CentralArtifacts.Include(item => item.Sources)
            .SingleAsync(item => item.ArtifactId == resultArtifactId).ConfigureAwait(false);
        invalidatedResult.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        invalidatedResult.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
        invalidatedResult.StateReasonCode.Should().Be("lineage.source-unavailable");
        invalidatedResult.Sources.Should().ContainSingle(item => item.ResolvedCentralArtifactId == null);
        using var invalidatedResponse = await ownerClient.GetAsync(new Uri(
            $"/api/v1.0/devices/{artifact.Frame!.DevicePublicId:D}/artifacts/{resultArtifactId:D}/content",
            UriKind.Relative)).ConfigureAwait(false);
        invalidatedResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var sourceJobs = await evidenceDb.CentralDerivativeJobs.Where(item => item.SourceCentralArtifactId == artifact.Id)
            .ToListAsync().ConfigureAwait(false);
        sourceJobs.Single(item => item.TargetRole == FrameArtifactRole.Preview).Status
            .Should().Be(CentralDerivativeJobStatus.Quarantined);
        sourceJobs.Single(item => item.TargetRole == FrameArtifactRole.Metadata).Status
            .Should().Be(CentralDerivativeJobStatus.Quarantined);
        sourceJobs.Single(item => item.TargetRole == FrameArtifactRole.AnnotatedPreview).Status
            .Should().Be(CentralDerivativeJobStatus.TerminalFailure);
        var corruptAttempt = await evidenceDb.CentralDerivativeJobAttempts
            .SingleAsync(item => item.CentralDerivativeJobId == corruptJobId).ConfigureAwait(false);
        corruptAttempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.Quarantined);
        corruptAttempt.ReasonCode.Should().Be("object.checksum-mismatch");
    }

    [TestMethod]
    public async Task StructuredProductIngest_PersistsExactTypedFactsAndResolvedLineage()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("structured-product-rig");
        Guid devicePublicId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId)
                .ConfigureAwait(false);
            devicePublicId = registration.DevicePublicId!.Value;
            db.DeviceRigProfiles.Add(new DeviceRigProfile
            {
                RegistrationId = registration.Id,
                DevicePublicId = registration.DevicePublicId!.Value,
                ObservatoryId = registration.ObservatoryId,
                Version = 1,
                ConfigHash = new string('1', 64),
                ConfigJson = JsonSerializer.Serialize(rig),
                ProfileName = "rig",
                ProfileVersion = rig.ProfileVersion,
                ProfileSha256 = CameraRigProfileIdentity.ComputeSha256(rig),
                CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
                EffectiveFromUtc = DateTimeOffset.UnixEpoch.AddDays(-1)
            });
            registration.CurrentRigProfileVersion = 1;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var rawPayload = new byte[] { 1, 2, 3, 4 };
        var rawManifest = CreateManifestV2(deviceId, rig, rawPayload, captureSequence: 8);
        var layerPayload = PresentationLayerPayloadJson.Create(
            Convert.ToHexString(SHA256.HashData(rawPayload)), 2, 2);
        var layerBytes = PresentationLayerPayloadJson.Serialize(layerPayload);
        var productManifest = CreateStructuredManifest(rawManifest, layerPayload, layerBytes);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var rawResponse = await PostAsync(client, rawManifest, rawPayload).ConfigureAwait(false);
        using var productResponse = await PostAsync(client, productManifest, layerBytes).ConfigureAwait(false);
        using var duplicateResponse = await PostAsync(client, productManifest, layerBytes).ConfigureAwait(false);
        var conflictingManifest = productManifest with
        {
            Descriptor = productManifest.Descriptor with
            {
                Algorithms = [new("presentation-layer", "integration-v2")]
            }
        };
        using var conflictingResponse = await PostAsync(client, conflictingManifest, layerBytes).ConfigureAwait(false);

        rawResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        productResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var duplicateBody = await duplicateResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        duplicateResponse.StatusCode.Should().Be(HttpStatusCode.Accepted, duplicateBody);
        conflictingResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var acknowledgement = await productResponse.Content.ReadFromJsonAsync<ArtifactUploadAcknowledgement>()
            .ConfigureAwait(false);
        acknowledgement!.AcceptedManifestSchemaVersion.Should()
            .Be(StructuredProcessingProductManifestV1.CurrentSchemaVersion);
        acknowledgement.ChecksumSha256.Should().Be(Convert.ToHexString(SHA256.HashData(layerBytes)));

        var contentUri = new Uri(
            $"/api/v1.0/devices/{devicePublicId:D}/artifacts/{productManifest.Descriptor.Artifact.ArtifactId:D}/content",
            UriKind.Relative);
        using var ownerClient = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        using var retrievedResponse = await ownerClient.GetAsync(contentUri).ConfigureAwait(false);
        retrievedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        retrievedResponse.Content.Headers.ContentType!.MediaType.Should().Be(PresentationLayerPayloadJson.MediaType);
        (await retrievedResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(layerBytes);
        retrievedResponse.Headers.GetValues("X-Artifact-SHA256")
            .Should().ContainSingle(Convert.ToHexString(SHA256.HashData(layerBytes)));
        using var foreignClient = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Viewer.Username, TestUsers.Viewer.Password).ConfigureAwait(false);
        using var foreignResponse = await foreignClient.GetAsync(contentUri).ConfigureAwait(false);
        foreignResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await assertionDb.CentralArtifacts
            .Include(item => item.StructuredProduct)
            .Include(item => item.Layout)
            .Include(item => item.Recipe)
            .Include(item => item.Sources)
            .SingleAsync(item => item.ArtifactId == productManifest.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        artifact.Layout.Should().BeNull();
        artifact.Recipe!.Name.Should().Be(productManifest.Descriptor.Artifact.Recipe.Name);
        artifact.Sources.Should().ContainSingle(source =>
            source.SourceArtifactId == rawManifest.Descriptor.Artifact.ArtifactId &&
            source.ResolvedCentralArtifactId != null);
        artifact.StructuredProduct.Should().NotBeNull();
        artifact.StructuredProduct!.OutputIdentitySha256.Should().Be(productManifest.Descriptor.OutputIdentitySha256);
        artifact.StructuredProduct.ProductKind.Should().Be(ProcessingProductKind.Metadata.ToString());
        artifact.StructuredProduct.ProductSchemaVersion.Should().Be(productManifest.Descriptor.ProductSchemaVersion);
        artifact.StructuredProduct.ContentIdentitySha256.Should().Be(layerPayload.ContentIdentitySha256);
        artifact.StructuredProduct.AlgorithmsJson.Should().Be(JsonSerializer.Serialize(productManifest.Descriptor.Algorithms));
        artifact.StructuredProduct.CompatibilityJson.Should().Be(JsonSerializer.Serialize(productManifest.Descriptor.Compatibility));
        artifact.StructuredProduct.TotalIntegrationTicks.Should().Be(productManifest.Descriptor.TotalIntegrationTicks);
        var reconstructedDescriptor = CentralReconstructionDescriptorFactory.CreateStructured(artifact);
        var reconstructedManifest = productManifest with { Descriptor = reconstructedDescriptor };
        StructuredProcessingProductManifestJson.Serialize(reconstructedManifest)
            .Should().Equal(StructuredProcessingProductManifestJson.Serialize(productManifest));
        reconstructedManifest.IdempotencyKey.Should().Be(productManifest.IdempotencyKey);
        var descriptorJson = artifact.StructuredProduct.DescriptorJson;
        artifact.StructuredProduct.DescriptorJson = JsonSerializer.Serialize(
            reconstructedDescriptor with { Algorithms = [new("tampered", "1.0.0")] });
        Action reconstructTamperedDescriptor = () => CentralReconstructionDescriptorFactory.CreateStructured(artifact);
        reconstructTamperedDescriptor.Should().Throw<InvalidDataException>();
        artifact.StructuredProduct.DescriptorJson = descriptorJson;
        (await assertionDb.CentralArtifacts.CountAsync(item =>
            item.ArtifactId == productManifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false)).Should().Be(1);

        var corruptBytes = layerBytes.ToArray();
        corruptBytes[^1] ^= 1;
        var objectKey = artifact.StorageReference["minio://skymonitor-artifacts/".Length..];
        await using (var corruptStream = new MemoryStream(corruptBytes, writable: false))
        {
            await assertionScope.ServiceProvider.GetRequiredService<IMinioClient>().PutObjectAsync(new PutObjectArgs()
                .WithBucket("skymonitor-artifacts")
                .WithObject(objectKey)
                .WithStreamData(corruptStream)
                .WithObjectSize(corruptBytes.LongLength)
                .WithContentType(PresentationLayerPayloadJson.MediaType)).ConfigureAwait(false);
        }
        using var corruptResponse = await ownerClient.GetAsync(contentUri).ConfigureAwait(false);
        corruptResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        assertionDb.ChangeTracker.Clear();
        (await assertionDb.CentralArtifacts.SingleAsync(item =>
            item.ArtifactId == productManifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false))
            .ObjectState.Should().Be(CentralArtifactObjectState.Quarantined);
    }

    [TestMethod]
    public async Task CentralPresentation_RendersCachesAndMaterializesSelectedStack()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("central-presentation-rig");
        Guid devicePublicId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId)
                .ConfigureAwait(false);
            devicePublicId = registration.DevicePublicId!.Value;
            db.DeviceRigProfiles.Add(new DeviceRigProfile
            {
                RegistrationId = registration.Id,
                DevicePublicId = devicePublicId,
                ObservatoryId = registration.ObservatoryId,
                Version = 1,
                ConfigHash = new string('2', 64),
                ConfigJson = JsonSerializer.Serialize(rig),
                ProfileName = "rig",
                ProfileVersion = rig.ProfileVersion,
                ProfileSha256 = CameraRigProfileIdentity.ComputeSha256(rig),
                CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
                EffectiveFromUtc = DateTimeOffset.UnixEpoch.AddDays(-1)
            });
            registration.CurrentRigProfileVersion = 1;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var captureId = Guid.NewGuid();
        var rawBytes = new byte[] { 10, 20, 30, 40 };
        var rawManifest = CreateManifestV2(
            deviceId, rig, rawBytes, 81, captureId: captureId);
        using var ingestClient = fixture.Factory.CreateClient();
        ingestClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(ingestClient).ConfigureAwait(false));
        using var rawResponse = await PostAsync(ingestClient, rawManifest, rawBytes).ConfigureAwait(false);
        rawResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var baseBytes = rawBytes.ToArray();
        var baseChecksum = Convert.ToHexString(SHA256.HashData(baseBytes));
        var baseRecipe = RecipeIdentityDescriptor.Create(
            BuiltInProcessingRecipes.EncodedPreview,
            "1.0.0",
            CentralPresentationBaseDecoder.PackedDecoderVersion,
            JsonSerializer.SerializeToElement(new { }));
        var baseOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Preview,
            "presentation-base",
            ProcessingIdentity.CreateRecipeIdentity(baseRecipe).IdentitySha256,
            [rawManifest.Descriptor.Artifact.ArtifactId]);
        var baseArtifactId = ProcessingIdentity.CreateArtifactId(baseOutputIdentity);
        baseOutputIdentity.Should().NotBe(baseChecksum);
        Guid centralCaptureId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var frame = await db.CentralFrames.Include(item => item.Artifacts)
                .SingleAsync(item => item.FrameId == captureId).ConfigureAwait(false);
            centralCaptureId = frame.Id;
            var raw = frame.Artifacts.Single(item => item.ArtifactId == rawManifest.Descriptor.Artifact.ArtifactId);
            var baseRow = new CentralArtifact
            {
                CentralFrameId = frame.Id,
                DevicePublicId = devicePublicId,
                ArtifactId = baseArtifactId,
                Role = FrameArtifactRole.Preview,
                RecipeVersion = "encoded-preview-v1",
                ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
                MediaType = CentralPresentationBaseDecoder.PackedMediaType,
                ByteLength = baseBytes.LongLength,
                ChecksumSha256 = baseChecksum,
                StorageReference = $"minio://skymonitor-artifacts/derivatives/presentation/{baseArtifactId:D}.bin",
                ReceivedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
                IdempotencyKey = new string('3', 64),
                SourceId = "integration-presentation",
                Variant = "presentation-base",
                CreatedUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete,
                Layout = new CentralArtifactLayout
                {
                    Width = 2,
                    Height = 2,
                    StrideBytes = 2,
                    PixelFormat = CameraPixelFormat.Mono8.ToString(),
                    ByteOrder = FrameByteOrder.NotApplicable.ToString(),
                    SampleDepthBits = 8,
                    ContainerDepthBits = 8,
                    Packing = FrameSamplePacking.ByteAligned.ToString(),
                    CfaPattern = ColorFilterArrayPattern.None.ToString(),
                    BlackLevel = 0,
                    WhiteLevel = 255,
                    ByteLength = rawBytes.LongLength
                },
                Recipe = new CentralArtifactRecipe
                {
                    Name = baseRecipe.Name,
                    SemanticVersion = baseRecipe.SemanticVersion,
                    ImplementationVersion = baseRecipe.ImplementationVersion,
                    OptionsJson = baseRecipe.Options.GetRawText(),
                    OptionsSha256 = baseRecipe.OptionsSha256
                }
            };
            baseRow.Sources.Add(new CentralArtifactSource
            {
                Ordinal = 0,
                SourceArtifactId = raw.ArtifactId,
                ResolvedCentralArtifactId = raw.Id
            });
            db.CentralArtifacts.Add(baseRow);
            await db.SaveChangesAsync().ConfigureAwait(false);
            await using var stream = new MemoryStream(baseBytes, writable: false);
            await scope.ServiceProvider.GetRequiredService<IMinioClient>().PutObjectAsync(new PutObjectArgs()
                .WithBucket("skymonitor-artifacts")
                .WithObject($"derivatives/presentation/{baseArtifactId:D}.bin")
                .WithStreamData(stream)
                .WithObjectSize(baseBytes.LongLength)
                .WithContentType(CentralPresentationBaseDecoder.PackedMediaType)).ConfigureAwait(false);
        }

        var sourceIdentity = Convert.ToHexString(SHA256.HashData(rawBytes));
        var layer = PresentationLayerPayloadJson.Create(
            sourceIdentity,
            2,
            2,
            markers: [new(new(0, 0), 0, new(255, 255, 255))]);
        var layerBytes = PresentationLayerPayloadJson.Serialize(layer);
        var layerUpload = CreateStructuredManifest(rawManifest, layer, layerBytes);
        using var layerResponse = await PostAsync(ingestClient, layerUpload, layerBytes).ConfigureAwait(false);
        layerResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var reorderedOptions = JsonDocument.Parse("{\"beta\":2,\"alpha\":1}"))
        {
            var reorderedRecipe = layerUpload.Descriptor.Artifact.Recipe with
            {
                Options = reorderedOptions.RootElement.Clone(),
                OptionsSha256 = ToLowerHex(layerUpload.Descriptor.Artifact.Recipe.OptionsSha256)
            };
            var reorderedUpload = layerUpload with
            {
                Descriptor = layerUpload.Descriptor with
                {
                    SourceCapture = layerUpload.Descriptor.SourceCapture with
                    {
                        Artifact = layerUpload.Descriptor.SourceCapture.Artifact with
                        {
                            ChecksumSha256 = ToLowerHex(
                                layerUpload.Descriptor.SourceCapture.Artifact.ChecksumSha256)
                        }
                    },
                    Artifact = layerUpload.Descriptor.Artifact with
                    {
                        Recipe = reorderedRecipe,
                        ChecksumSha256 = ToLowerHex(layerUpload.Descriptor.Artifact.ChecksumSha256)
                    }
                }
            };
            reorderedUpload.IdempotencyKey.Should().Be(layerUpload.IdempotencyKey);
            using var reorderedResponse = await PostAsync(
                ingestClient, reorderedUpload, layerBytes).ConfigureAwait(false);
            reorderedResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        var compatibility = new PresentationCompatibilityDescriptor(
            2, 2, PresentationProcessingProducts.ComputeLayoutIdentity(rawManifest.Descriptor.Layout), sourceIdentity);
        var layerContract = LayeredPresentationJson.CreateLayer(
            "scene-annotation",
            new(layerUpload.Descriptor.Artifact.ArtifactId, layer.ContentIdentitySha256,
                PresentationLayerPayloadJson.MediaType, compatibility),
            sourceIdentity,
            PresentationCoordinateSpace.ScenePixels,
            GroupedSvgPresentationRenderer.RendererVersion,
            "integration-style-v1",
            10,
            PresentationBlendMode.Normal,
            1_000_000,
            true,
            JsonSerializer.SerializeToElement(new { }));
        var overlay = LayeredPresentationJson.CreateManifest(
            new(baseArtifactId, baseOutputIdentity, CentralPresentationBaseDecoder.PackedMediaType, compatibility),
            sourceIdentity,
            [layerContract]);
        var overlayBytes = LayeredPresentationJson.Serialize(overlay);
        var overlaySources = new[] { baseArtifactId, layerUpload.Descriptor.Artifact.ArtifactId };
        var overlayRecipe = RecipeIdentityDescriptor.Create(
            PresentationProcessingProducts.ManifestRecipeName,
            "1.0.0",
            "integration-v1",
            JsonSerializer.SerializeToElement(new { }));
        var overlayOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Metadata,
            "overlay-manifest",
            ProcessingIdentity.CreateRecipeIdentity(overlayRecipe).IdentitySha256,
            overlaySources);
        var overlayArtifact = new ArtifactDescriptor(
            ProcessingIdentity.CreateArtifactId(overlayOutputIdentity),
            FrameArtifactRole.Metadata,
            "overlay-manifest-step",
            "overlay-manifest",
            DateTimeOffset.UnixEpoch.AddMinutes(2),
            overlaySources,
            overlayRecipe,
            PresentationProcessingProducts.ManifestMediaType,
            Convert.ToHexString(SHA256.HashData(overlayBytes)));
        var overlayUpload = new StructuredProcessingProductManifestV1(
            StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new StructuredProcessingProductDescriptorV1(
                rawManifest.Descriptor,
                overlayArtifact,
                overlayOutputIdentity,
                [new("grouped-svg", GroupedSvgPresentationRenderer.RendererVersion)],
                new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
                TimeSpan.FromSeconds(1).Ticks,
                overlayBytes.LongLength,
                ProcessingProductKind.Metadata,
                OverlayManifestV1.CurrentSchemaVersion,
                overlay.ManifestIdentitySha256),
            "derived/overlay-manifest.json",
            ProducerStepId: "overlay-manifest-step");
        using var overlayResponse = await PostAsync(ingestClient, overlayUpload, overlayBytes).ConfigureAwait(false);
        overlayResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var presentationCacheKey = CentralLayeredPresentationService.CreateCacheKey(
            centralCaptureId, overlayArtifact.ArtifactId, overlayArtifact.ChecksumSha256);
        presentationCacheKey
            .Should().NotBe(CentralLayeredPresentationService.CreateCacheKey(
                Guid.NewGuid(), overlayArtifact.ArtifactId, overlayArtifact.ChecksumSha256));
        await using (var cacheScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var invalidCacheEntry = JsonSerializer.SerializeToUtf8Bytes(new
            {
                captureId = centralCaptureId,
                devicePublicId,
                baseArtifactId,
                baseMediaType = CentralPresentationBaseDecoder.PackedMediaType,
                manifestIdentitySha256 = overlay.ManifestIdentitySha256,
                presentationIdentitySha256 = new string('A', 64),
                svgChecksumSha256 = new string('B', 64),
                widthPixels = 2,
                heightPixels = 2,
                layers = (object?)null,
                svg = (byte[]?)null
            });
            await cacheScope.ServiceProvider.GetRequiredService<IDistributedCache>()
                .SetAsync(presentationCacheKey, invalidCacheEntry).ConfigureAwait(false);
        }

        using var ownerClient = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        var concurrentResponses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ownerClient.GetAsync(new Uri(
            $"/api/v1.0/captures/{centralCaptureId:D}/presentation.svg", UriKind.Relative)))).ConfigureAwait(false);
        concurrentResponses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
        var concurrentBodies = await Task.WhenAll(concurrentResponses.Select(response =>
            response.Content.ReadAsByteArrayAsync())).ConfigureAwait(false);
        concurrentBodies.Skip(1).Should().OnlyContain(bytes => bytes.SequenceEqual(concurrentBodies[0]));
        using var svgResponse = concurrentResponses[0];
        foreach (var response in concurrentResponses.Skip(1))
        {
            response.Dispose();
        }
        svgResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        svgResponse.Headers.ETag.Should().NotBeNull();
        Encoding.UTF8.GetString(concurrentBodies[0]).Should()
            .Contain($"data-layer-identity=\"{layerContract.LayerIdentitySha256}\"");
        await using (var cacheScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var cache = cacheScope.ServiceProvider.GetRequiredService<IDistributedCache>();
            var validCacheBytes = await cache.GetAsync(presentationCacheKey).ConfigureAwait(false);
            var cacheJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var validCache = JsonSerializer.Deserialize<CentralLayeredPresentation>(
                validCacheBytes!, cacheJsonOptions);
            validCache.Should().NotBeNull();
            var invalidLayerCache = validCache! with
            {
                Layers = [null!]
            };
            fixture.Factory.Services.GetRequiredService<CentralLayeredPresentationCache>()
                .Remove(presentationCacheKey);
            await cache.SetAsync(
                presentationCacheKey,
                JsonSerializer.SerializeToUtf8Bytes(
                    invalidLayerCache, cacheJsonOptions)).ConfigureAwait(false);
        }
        using (var invalidLayerResponse = await ownerClient.GetAsync(new Uri(
                   $"/api/v1.0/captures/{centralCaptureId:D}/presentation.svg", UriKind.Relative)).ConfigureAwait(false))
        {
            invalidLayerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using var descriptorResponse = await ownerClient.GetAsync(new Uri(
            $"/api/v1.0/captures/{centralCaptureId:D}/presentation", UriKind.Relative)).ConfigureAwait(false);
        descriptorResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var baseResponse = await ownerClient.GetAsync(new Uri(
            $"/api/v1.0/captures/{centralCaptureId:D}/presentation/base", UriKind.Relative)).ConfigureAwait(false);
        baseResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        baseResponse.Content.Headers.ContentType!.MediaType.Should().Be(JpegImageCodec.MediaType);
        var displayBase = JpegImageCodec.DecodeJpeg(
            await baseResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        displayBase.Width.Should().Be(2);
        displayBase.Height.Should().Be(2);
        var packedReads = new PackedBaseReadCounter(baseArtifactId);
        using (var conditionalFactory = fixture.Factory.WithWebHostBuilder(builder =>
                   builder.ConfigureTestServices(services =>
                   {
                       services.AddScoped<CentralArtifactObjectReader>();
                       services.RemoveAll<ICentralArtifactObjectReader>();
                       services.AddScoped<ICentralArtifactObjectReader>(provider =>
                           new CountingPackedBaseObjectReader(
                               provider.GetRequiredService<CentralArtifactObjectReader>(), packedReads));
                   })))
        using (var conditionalClient = conditionalFactory.CreateClient())
        {
            conditionalClient.DefaultRequestHeaders.Authorization = ownerClient.DefaultRequestHeaders.Authorization;
            using var baseConditional = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1.0/captures/{centralCaptureId:D}/presentation/base");
            baseConditional.Headers.TryAddWithoutValidation(
                "If-None-Match", $"W/\"ignored\", {baseResponse.Headers.ETag}");
            using var baseNotModified = await conditionalClient.SendAsync(baseConditional).ConfigureAwait(false);
            baseNotModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
            using var baseHead = await conditionalClient.SendAsync(new HttpRequestMessage(
                HttpMethod.Head, $"/api/v1.0/captures/{centralCaptureId:D}/presentation/base")).ConfigureAwait(false);
            baseHead.StatusCode.Should().Be(HttpStatusCode.OK);
            baseHead.Content.Headers.ContentLength.Should().BeNull();
        }
        packedReads.CopyCount.Should().Be(0);
        var latestLayer = LayeredPresentationJson.CreateLayer(
            "scene-annotation",
            new(layerUpload.Descriptor.Artifact.ArtifactId, layer.ContentIdentitySha256,
                PresentationLayerPayloadJson.MediaType, compatibility),
            sourceIdentity,
            PresentationCoordinateSpace.ScenePixels,
            GroupedSvgPresentationRenderer.RendererVersion,
            "integration-style-v2",
            10,
            PresentationBlendMode.Normal,
            1_000_000,
            true,
            JsonSerializer.SerializeToElement(new { }));
        var latestOverlay = LayeredPresentationJson.CreateManifest(
            new(baseArtifactId, baseOutputIdentity, CentralPresentationBaseDecoder.PackedMediaType, compatibility),
            sourceIdentity,
            [latestLayer]);
        var latestOverlayBytes = LayeredPresentationJson.Serialize(latestOverlay);
        var latestRecipe = RecipeIdentityDescriptor.Create(
            PresentationProcessingProducts.ManifestRecipeName,
            "1.0.0",
            "integration-v1",
            JsonSerializer.SerializeToElement(new { revision = 2 }));
        var latestOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Metadata,
            "overlay-manifest-v2",
            ProcessingIdentity.CreateRecipeIdentity(latestRecipe).IdentitySha256,
            overlaySources);
        var latestArtifact = new ArtifactDescriptor(
            ProcessingIdentity.CreateArtifactId(latestOutputIdentity),
            FrameArtifactRole.Metadata,
            "overlay-manifest-step",
            "overlay-manifest-v2",
            DateTimeOffset.UnixEpoch,
            overlaySources,
            latestRecipe,
            PresentationProcessingProducts.ManifestMediaType,
            Convert.ToHexString(SHA256.HashData(latestOverlayBytes)));
        var latestUpload = new StructuredProcessingProductManifestV1(
            StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new StructuredProcessingProductDescriptorV1(
                rawManifest.Descriptor,
                latestArtifact,
                latestOutputIdentity,
                [new("grouped-svg", GroupedSvgPresentationRenderer.RendererVersion)],
                new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
                TimeSpan.FromSeconds(1).Ticks,
                latestOverlayBytes.LongLength,
                ProcessingProductKind.Metadata,
                OverlayManifestV1.CurrentSchemaVersion,
                latestOverlay.ManifestIdentitySha256),
            "derived/overlay-manifest-v2.json",
            ProducerStepId: "overlay-manifest-step");
        using var latestUploadResponse = await PostAsync(
            ingestClient, latestUpload, latestOverlayBytes).ConfigureAwait(false);
        latestUploadResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var latestDescriptorResponse = await ownerClient.GetAsync(new Uri(
            $"/api/v1.0/captures/{centralCaptureId:D}/presentation", UriKind.Relative)).ConfigureAwait(false);
        latestDescriptorResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var latestDescriptor = JsonDocument.Parse(
                   await latestDescriptorResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false)))
        {
            latestDescriptor.RootElement.GetProperty("ManifestIdentitySha256").GetString()
                .Should().Be(latestOverlay.ManifestIdentitySha256);
        }
        using var conditional = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1.0/captures/{centralCaptureId:D}/presentation.svg");
        conditional.Headers.IfNoneMatch.Add(svgResponse.Headers.ETag!);
        using var notModified = await ownerClient.SendAsync(conditional).ConfigureAwait(false);
        notModified.StatusCode.Should().Be(HttpStatusCode.OK);
        notModified.Headers.ETag.Should().NotBe(svgResponse.Headers.ETag);
        using var latestConditional = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1.0/captures/{centralCaptureId:D}/presentation.svg");
        latestConditional.Headers.IfNoneMatch.Add(notModified.Headers.ETag!);
        using var latestNotModified = await ownerClient.SendAsync(latestConditional).ConfigureAwait(false);
        latestNotModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        using var foreignClient = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Viewer.Username, TestUsers.Viewer.Password).ConfigureAwait(false);
        using var foreignResponse = await foreignClient.GetAsync(new Uri(
            $"/api/v1.0/captures/{centralCaptureId:D}/presentation", UriKind.Relative)).ConfigureAwait(false);
        foreignResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var materializationScope = fixture.Factory.Services.CreateAsyncScope();
        var materializationDb = materializationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await materializationDb.Users.SingleAsync(item => item.UserName == TestUsers.Operator.Username)
            .ConfigureAwait(false);
        var viewer = await materializationDb.Users.SingleAsync(item => item.UserName == TestUsers.Viewer.Username)
            .ConfigureAwait(false);
        var observatoryId = await materializationDb.CentralFrames.Where(item => item.Id == centralCaptureId)
            .Select(item => item.ObservatoryId).SingleAsync().ConfigureAwait(false);
        materializationDb.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            ObservatoryId = observatoryId,
            UserId = viewer.Id,
            Role = ObservatoryMembershipRole.Viewer,
            AddedAtUtc = DateTimeOffset.UnixEpoch
        });
        var materializationRegistration = await materializationDb.DeviceRegistrations
            .SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
        materializationRegistration.Status = DeviceRegistrationStatus.Revoked;
        materializationDb.DeviceRegistrations.Add(new DeviceRegistration
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            ObservatoryId = materializationRegistration.ObservatoryId,
            ObservatoryName = materializationRegistration.ObservatoryName,
            ObservatoryTimeZoneId = materializationRegistration.ObservatoryTimeZoneId,
            FriendlyName = "Re-paired Artifact Device",
            OwnerUserId = materializationRegistration.OwnerUserId,
            OwnerDisplayName = materializationRegistration.OwnerDisplayName,
            OwnerConfirmationMethod = materializationRegistration.OwnerConfirmationMethod,
            Status = DeviceRegistrationStatus.Pending,
            VerificationCodeHash = DeviceRegistrationService.ComputeSha256("FGHIJ"),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        });
        await materializationDb.SaveChangesAsync().ConfigureAwait(false);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id)], IdentityConstants.ApplicationScheme));
        var viewerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, viewer.Id)], IdentityConstants.ApplicationScheme));
        var materializer = materializationScope.ServiceProvider.GetRequiredService<ICentralPresentationMaterializer>();
        using (var redisOutageFactory = fixture.Factory.WithWebHostBuilder(builder =>
                   builder.ConfigureTestServices(services =>
                   {
                       services.RemoveAll<IDistributedCache>();
                       services.AddSingleton<IDistributedCache, ThrowingDistributedCache>();
                   })))
        {
            await using var redisOutageScope = redisOutageFactory.Services.CreateAsyncScope();
            var redisFallback = await redisOutageScope.ServiceProvider
                .GetRequiredService<ICentralLayeredPresentationService>()
                .GetAsync(centralCaptureId, principal).ConfigureAwait(false);
            redisFallback.Status.Should().Be(CentralLayeredPresentationStatus.Found);
        }
        using (var objectOutageFactory = fixture.Factory.WithWebHostBuilder(builder =>
                   builder.ConfigureTestServices(services =>
                   {
                       services.RemoveAll<IDistributedCache>();
                       services.AddSingleton<IDistributedCache, EmptyDistributedCache>();
                       services.RemoveAll<ICentralArtifactObjectReader>();
                       services.AddScoped<ICentralArtifactObjectReader, UnavailableObjectReader>();
                   })))
        {
            await using var objectOutageScope = objectOutageFactory.Services.CreateAsyncScope();
            var unavailable = await objectOutageScope.ServiceProvider
                .GetRequiredService<ICentralLayeredPresentationService>()
                .GetAsync(centralCaptureId, principal).ConfigureAwait(false);
            unavailable.Status.Should().Be(CentralLayeredPresentationStatus.DependencyUnavailable);
        }
        var denied = await materializer.SaveAsync(
            centralCaptureId, overlay.ManifestIdentitySha256,
            [layerContract.LayerIdentitySha256], viewerPrincipal).ConfigureAwait(false);
        var stale = await materializer.SaveAsync(
            centralCaptureId, new string('F', 64),
            [layerContract.LayerIdentitySha256], principal).ConfigureAwait(false);
        var first = await materializer.SaveAsync(
            centralCaptureId, overlay.ManifestIdentitySha256,
            [layerContract.LayerIdentitySha256], principal).ConfigureAwait(false);
        var replay = await materializer.SaveAsync(
            centralCaptureId, overlay.ManifestIdentitySha256,
            [layerContract.LayerIdentitySha256], principal).ConfigureAwait(false);
        denied.Status.Should().Be(CentralPresentationMaterializationStatus.Unavailable);
        stale.Status.Should().Be(CentralPresentationMaterializationStatus.Unavailable);
        first.Status.Should().Be(CentralPresentationMaterializationStatus.Saved);
        first.Receipt.Should().NotBeNull();
        replay.Status.Should().Be(CentralPresentationMaterializationStatus.Saved);
        replay.Receipt!.ArtifactId.Should().Be(first.Receipt!.ArtifactId);
        replay.Receipt.Replayed.Should().BeTrue();
        var stored = await materializationDb.CentralArtifacts.Include(item => item.Sources)
            .SingleAsync(item => item.ArtifactId == first.Receipt.ArtifactId).ConfigureAwait(false);
        stored.Role.Should().Be(FrameArtifactRole.AnnotatedPreview);
        stored.MediaType.Should().Be("application/x-hvo-packed-image");
        stored.Sources.OrderBy(item => item.Ordinal).Select(item => item.SourceArtifactId)
            .Should().Equal(baseArtifactId, overlayArtifact.ArtifactId, layerUpload.Descriptor.Artifact.ArtifactId);
    }

    [TestMethod]
    public void StructuredSourceIdentity_AcceptsExactlyOneMatchingCanonicalSource()
    {
        var expected = new string('A', 64);
        var artifact = new CentralArtifact
        {
            StructuredProduct = new CentralStructuredProcessingProduct { SourceIdentitySha256 = expected }
        };
        var matching = new CentralArtifact
        {
            ChecksumSha256 = new string('B', 64),
            StructuredProduct = new CentralStructuredProcessingProduct { ContentIdentitySha256 = expected }
        };
        var other = new CentralArtifact { ChecksumSha256 = new string('C', 64) };
        artifact.Sources.Add(new CentralArtifactSource
        {
            Ordinal = 0,
            SourceArtifactId = Guid.NewGuid(),
            ResolvedCentralArtifactId = matching.Id,
            ResolvedArtifact = matching
        });
        artifact.Sources.Add(new CentralArtifactSource
        {
            Ordinal = 1,
            SourceArtifactId = Guid.NewGuid(),
            ResolvedCentralArtifactId = other.Id,
            ResolvedArtifact = other
        });

        ArtifactIngestService.HasStructuredSourceIdentityMismatch(artifact).Should().BeFalse();
        other.ChecksumSha256 = expected;
        ArtifactIngestService.HasStructuredSourceIdentityMismatch(artifact).Should().BeTrue();
        artifact.Sources.Last().ResolvedCentralArtifactId = null;
        artifact.Sources.Last().ResolvedArtifact = null;
        ArtifactIngestService.HasStructuredSourceIdentityMismatch(artifact).Should().BeFalse();
    }

    [TestMethod]
    public void StructuredCloudSourceFacts_RequireExactResolvedArtifactFacts()
    {
        var recipeDescriptor = RecipeIdentityDescriptor.Create(
            "source-recipe", "1.0.0", "integration-v1", JsonSerializer.SerializeToElement(new { }));
        var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipeDescriptor).IdentitySha256;
        var resolved = new CentralArtifact
        {
            Role = FrameArtifactRole.Raw,
            Variant = "source",
            Recipe = new CentralArtifactRecipe
            {
                Name = recipeDescriptor.Name,
                SemanticVersion = recipeDescriptor.SemanticVersion,
                ImplementationVersion = recipeDescriptor.ImplementationVersion,
                OptionsJson = JsonSerializer.Serialize(CaptureContractJson.Canonicalize(recipeDescriptor.Options)),
                OptionsSha256 = recipeDescriptor.OptionsSha256
            }
        };
        var source = new CentralArtifactSource
        {
            SourceArtifactId = Guid.NewGuid(),
            ExpectedRole = resolved.Role,
            ExpectedVariant = resolved.Variant,
            ExpectedRecipeIdentitySha256 = recipeIdentity,
            ResolvedCentralArtifactId = resolved.Id,
            ResolvedArtifact = resolved
        };
        var artifact = new CentralArtifact();
        artifact.Sources.Add(source);

        ArtifactIngestService.HasStructuredSourceFactsMismatch(artifact).Should().BeFalse();
        source.ExpectedRole = FrameArtifactRole.Preview;
        ArtifactIngestService.HasStructuredSourceFactsMismatch(artifact).Should().BeTrue();
        source.ExpectedRole = resolved.Role;
        source.ExpectedVariant = "other";
        ArtifactIngestService.HasStructuredSourceFactsMismatch(artifact).Should().BeTrue();
        source.ExpectedVariant = resolved.Variant;
        source.ExpectedRecipeIdentitySha256 = new string('F', 64);
        ArtifactIngestService.HasStructuredSourceFactsMismatch(artifact).Should().BeTrue();
        source.ExpectedRecipeIdentitySha256 = recipeIdentity;
        source.ResolvedCentralArtifactId = null;
        source.ResolvedArtifact = null;
        ArtifactIngestService.HasStructuredSourceFactsMismatch(artifact).Should().BeFalse();
        artifact.MediaType = StructuredProcessingProductContracts.CloudAssessmentMediaType;
        source.ExpectedRole = null;
        source.ExpectedVariant = null;
        source.ExpectedRecipeIdentitySha256 = null;
        ArtifactIngestService.HasStructuredSourceFactsMismatch(artifact).Should().BeTrue();
    }

    [TestMethod]
    public async Task StructuredCloudAssessment_ResolvesHistoricalClearReferenceAcrossFrames()
    {
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("structured-cloud-history-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var clearBytes = new byte[] { 1, 2, 3, 4 };
        var currentBytes = new byte[] { 5, 6, 7, 8 };
        var clear = CreateManifestV2(
            deviceId, rig, clearBytes, 91, capturedAtUtc: DateTimeOffset.UnixEpoch);
        var current = CreateManifestV2(
            deviceId, rig, currentBytes, 92, capturedAtUtc: DateTimeOffset.UnixEpoch.AddMinutes(1));
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var currentResponse = await PostAsync(client, current, currentBytes).ConfigureAwait(false);
        currentResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var cloudRecipe = RecipeIdentityDescriptor.Create(
            BuiltInProcessingRecipes.CloudAssessment,
            "1.0.0",
            "integration-v1",
            JsonSerializer.SerializeToElement(new { }));
        var assessment = new CloudAssessmentV1(
            CloudAssessmentV1.CurrentSchemaVersion,
            CloudAssessmentStatus.Quantified,
            CloudAssessmentQuality.Degraded,
            [CloudAssessmentReasonCodes.EnvironmentMissing],
            0,
            1_000_000,
            new CloudAssessmentGridV1(1, 1, 750_000, 1, 0, 1, 0),
            [new CloudAssessmentRegionV1(0, 0, 0, 0, 2, 2, 1, 1, 0, 1_000_000, false)],
            null,
            new CloudAssessmentSourceV1(
                current.Descriptor.Artifact.ArtifactId,
                current.Descriptor.Artifact.Role,
                current.Descriptor.Artifact.Variant,
                ProcessingIdentity.CreateRecipeIdentity(current.Descriptor.Artifact.Recipe).IdentitySha256),
            new CloudAssessmentSourceV1(
                clear.Descriptor.Artifact.ArtifactId,
                clear.Descriptor.Artifact.Role,
                clear.Descriptor.Artifact.Variant,
                ProcessingIdentity.CreateRecipeIdentity(clear.Descriptor.Artifact.Recipe).IdentitySha256),
            new CloudAssessmentCalibrationV1(
                0, 255, 255, "calibration", "mask", "sensor", "processing"),
            new CloudAssessmentEnvironmentV1(
                CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
                CaptureSolarRegime.Night,
                EnvironmentalObservationMatchStatus.Missing,
                null,
                null,
                false),
            ProcessingIdentity.CreateRecipeIdentity(cloudRecipe).IdentitySha256,
            [new ProcessingAlgorithmIdentity("cloud-transmission", "integration-v1")]);
        var cloudBytes = CloudAssessmentJson.Serialize(assessment);
        var sourceIds = new[]
        {
            current.Descriptor.Artifact.ArtifactId,
            clear.Descriptor.Artifact.ArtifactId
        };
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Metadata,
            "cloud-assessment",
            ProcessingIdentity.CreateRecipeIdentity(cloudRecipe).IdentitySha256,
            sourceIds);
        var artifact = new ArtifactDescriptor(
            ProcessingIdentity.CreateArtifactId(outputIdentity),
            FrameArtifactRole.Metadata,
            "cloud-assessment-step",
            "cloud-assessment",
            current.Descriptor.Timing.ReadoutCompletedUtc,
            sourceIds,
            cloudRecipe,
            StructuredProcessingProductContracts.CloudAssessmentMediaType,
            Convert.ToHexString(SHA256.HashData(cloudBytes)));
        var upload = new StructuredProcessingProductManifestV1(
            StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new StructuredProcessingProductDescriptorV1(
                current.Descriptor,
                artifact,
                outputIdentity,
                assessment.Algorithms,
                new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
                TimeSpan.FromSeconds(1).Ticks,
                cloudBytes.LongLength,
                ProcessingProductKind.Metadata,
                CloudAssessmentV1.CurrentSchemaVersion,
                assessment.AssessmentIdentitySha256),
            "derived/cloud-assessment.json",
            ProducerStepId: "cloud-assessment-step");
        var staleAssessment = assessment with
        {
            Current = assessment.Current with { Variant = "stale-source-variant" }
        };
        var staleBytes = CloudAssessmentJson.Serialize(staleAssessment);
        var staleUpload = upload with
        {
            Descriptor = upload.Descriptor with
            {
                ByteLength = staleBytes.LongLength,
                ContentIdentitySha256 = staleAssessment.AssessmentIdentitySha256,
                Artifact = upload.Descriptor.Artifact with
                {
                    ChecksumSha256 = Convert.ToHexString(SHA256.HashData(staleBytes))
                }
            }
        };
        using var staleResponse = await PostAsync(client, staleUpload, staleBytes).ConfigureAwait(false);
        staleResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var response = await PostAsync(client, upload, cloudBytes).ConfigureAwait(false);
        response.StatusCode.Should().Be((HttpStatusCode)425);
        using var clearResponse = await PostAsync(client, clear, clearBytes).ConfigureAwait(false);
        clearResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.CentralArtifacts.Include(item => item.Sources)
            .SingleAsync(item => item.ArtifactId == artifact.ArtifactId).ConfigureAwait(false);
        stored.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        var currentSource = stored.Sources.Single(item => item.Ordinal == 0);
        currentSource.ExpectedRole.Should().Be(assessment.Current.Role);
        currentSource.ExpectedVariant.Should().Be(assessment.Current.Variant);
        currentSource.ExpectedRecipeIdentitySha256.Should().Be(assessment.Current.RecipeIdentitySha256);
        var clearSource = stored.Sources.Single(item => item.Ordinal == 1);
        clearSource.ExpectedRole.Should().Be(assessment.ClearReference!.Role);
        clearSource.ExpectedVariant.Should().Be(assessment.ClearReference.Variant);
        clearSource.ExpectedRecipeIdentitySha256.Should().Be(assessment.ClearReference.RecipeIdentitySha256);
        var clearSourceId = stored.Sources.Single(item => item.Ordinal == 1).ResolvedCentralArtifactId;
        clearSourceId.Should().NotBeNull();
        var clearFrameId = await db.CentralArtifacts.Where(item => item.Id == clearSourceId)
            .Select(item => item.CentralFrameId).SingleAsync().ConfigureAwait(false);
        clearFrameId.Should().NotBe(stored.CentralFrameId);
        clearSource.ExpectedVariant = "persisted-corrupt-variant";
        await db.SaveChangesAsync().ConfigureAwait(false);
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(
            AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new MutableTimeProvider(DateTimeOffset.UtcNow.AddHours(1)),
            telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);
        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var reconciled = await db.CentralArtifacts.SingleAsync(item => item.ArtifactId == artifact.ArtifactId)
            .ConfigureAwait(false);
        reconciled.ReconstructionState.Should().Be(CentralReconstructionState.Quarantined);
        reconciled.StateReasonCode.Should().Be("lineage.source-identity-mismatch");
    }

    [TestMethod]
    public async Task StructuredProjectedScene_RejectsMismatchedSourceDescriptorIdentity()
    {
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("structured-projected-scene-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var rawBytes = new byte[] { 9, 8, 7, 6 };
        var raw = CreateManifestV2(deviceId, rig, rawBytes, 93);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var rawResponse = await PostAsync(client, raw, rawBytes).ConfigureAwait(false);
        rawResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var utc = DateTimeOffset.Parse("2025-01-15T08:00:00Z", CultureInfo.InvariantCulture);
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15;
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("zenith", "Zenith", siderealHours, 0, 1)
        ])).BuildAsync(new VisibleSceneRequest(
            utc,
            new ObserverLocation(0, 0, 0),
            new ProjectionContext(
                ProjectionModel.Perspective, 1, 1, 1, 1, 2, 2, ProjectionAperture.Rectangular,
                BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10),
            new CatalogMetadata(
                "fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "perspective-v1")).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted,
            visible,
            ProjectedSceneImageTransformV1.Identity(2, 2),
            new ProjectedSceneSource(
                raw.Descriptor.Capture.CaptureId,
                raw.Descriptor.Artifact.ArtifactId,
                new string('D', 64)),
            "calibration-v1",
            visible.Request.ProjectionVersion);
        var payload = ProjectedSceneJson.Serialize(scene);
        var recipe = RecipeIdentityDescriptor.Create(
            BuiltInProcessingRecipes.ProjectedScene,
            "1.0.0",
            "integration-v1",
            JsonSerializer.SerializeToElement(new { }));
        var sourceIds = new[] { raw.Descriptor.Artifact.ArtifactId };
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Metadata,
            "projected-scene",
            ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256,
            sourceIds);
        var artifact = new ArtifactDescriptor(
            ProcessingIdentity.CreateArtifactId(outputIdentity),
            FrameArtifactRole.Metadata,
            "projected-scene-step",
            "projected-scene",
            raw.Descriptor.Timing.ReadoutCompletedUtc,
            sourceIds,
            recipe,
            StructuredProcessingProductContracts.ProjectedSceneMediaType,
            Convert.ToHexString(SHA256.HashData(payload)));
        var upload = new StructuredProcessingProductManifestV1(
            StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new StructuredProcessingProductDescriptorV1(
                raw.Descriptor,
                artifact,
                outputIdentity,
                [new ProcessingAlgorithmIdentity("projected-scene", "integration-v1")],
                new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
                TimeSpan.FromSeconds(1).Ticks,
                payload.LongLength,
                ProcessingProductKind.Metadata,
                ProjectedSceneV1.CurrentSchemaVersion,
                scene.SceneIdentitySha256),
            "derived/projected-scene.json",
            ProducerStepId: "projected-scene-step");

        using var response = await PostAsync(client, upload, payload).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task StructuredProductIngest_OutOfOrderSourceConvergesWithoutPayloadRetry()
    {
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("structured-out-of-order-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var rawPayload = new byte[] { 5, 6, 7, 8 };
        var rawManifest = CreateManifestV2(deviceId, rig, rawPayload, captureSequence: 9);
        var layerPayload = PresentationLayerPayloadJson.Create(
            Convert.ToHexString(SHA256.HashData(rawPayload)), 2, 2);
        var layerBytes = PresentationLayerPayloadJson.Serialize(layerPayload);
        var productManifest = CreateStructuredManifest(rawManifest, layerPayload, layerBytes);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var pendingProduct = await PostAsync(client, productManifest, layerBytes).ConfigureAwait(false);
        using var rawResponse = await PostAsync(client, rawManifest, rawPayload).ConfigureAwait(false);
        using var productStatus = await PostStatusAsync(client, productManifest).ConfigureAwait(false);

        ((int)pendingProduct.StatusCode).Should().Be(425);
        rawResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        productStatus.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var artifact = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
            .Include(item => item.Sources)
            .SingleAsync(item => item.ArtifactId == productManifest.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        artifact.Sources.Should().ContainSingle(source => source.ResolvedCentralArtifactId != null);
    }

    [TestMethod]
    public async Task StructuredProductIngest_MismatchedSemanticSourceIsQuarantined()
    {
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("structured-source-mismatch-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var rawPayload = new byte[] { 21, 22, 23, 24 };
        var rawManifest = CreateManifestV2(deviceId, rig, rawPayload, captureSequence: 11);
        var layerPayload = PresentationLayerPayloadJson.Create(new string('B', 64), 2, 2);
        var layerBytes = PresentationLayerPayloadJson.Serialize(layerPayload);
        var productManifest = CreateStructuredManifest(rawManifest, layerPayload, layerBytes);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var productResponse = await PostAsync(client, productManifest, layerBytes).ConfigureAwait(false);
        using var rawResponse = await PostAsync(client, rawManifest, rawPayload).ConfigureAwait(false);

        rawResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        ((int)productResponse.StatusCode).Should().Be(425);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var artifact = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
            .SingleAsync(item => item.ArtifactId == productManifest.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Quarantined);
        artifact.StateReasonCode.Should().Be("lineage.source-identity-mismatch");
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(
            AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new MutableTimeProvider(DateTimeOffset.UtcNow.AddHours(1)),
            telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);
        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Clear();
        var reconciled = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
            .SingleAsync(item => item.ArtifactId == productManifest.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        reconciled.ReconstructionState.Should().Be(CentralReconstructionState.Quarantined);
        reconciled.StateReasonCode.Should().Be("lineage.source-identity-mismatch");
    }

    [TestMethod]
    public async Task StructuredProductIngest_CrossCaptureSourceRemainsPending()
    {
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("structured-cross-capture-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var sourceBytes = new byte[] { 31, 32, 33, 34 };
        var sourceManifest = CreateManifestV2(deviceId, rig, sourceBytes, captureSequence: 12);
        var otherCapture = CreateManifestV2(deviceId, rig, new byte[] { 35, 36, 37, 38 }, captureSequence: 13);
        var layer = PresentationLayerPayloadJson.Create(
            Convert.ToHexString(SHA256.HashData(sourceBytes)), 2, 2);
        var layerBytes = PresentationLayerPayloadJson.Serialize(layer);
        var productManifest = CreateStructuredManifest(otherCapture, layer, layerBytes);
        var sourceIds = new[] { sourceManifest.Descriptor.Artifact.ArtifactId };
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Metadata,
            productManifest.Descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(productManifest.Descriptor.Artifact.Recipe).IdentitySha256,
            sourceIds);
        productManifest = productManifest with
        {
            Descriptor = productManifest.Descriptor with
            {
                Artifact = productManifest.Descriptor.Artifact with
                {
                    ArtifactId = ProcessingIdentity.CreateArtifactId(outputIdentity),
                    SourceArtifactIds = sourceIds
                },
                OutputIdentitySha256 = outputIdentity
            }
        };
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var sourceResponse = await PostAsync(client, sourceManifest, sourceBytes).ConfigureAwait(false);
        using var productResponse = await PostAsync(client, productManifest, layerBytes).ConfigureAwait(false);

        sourceResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        ((int)productResponse.StatusCode).Should().Be(425);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var artifact = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
            .Include(item => item.Sources)
            .SingleAsync(item => item.ArtifactId == productManifest.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
        artifact.Sources.Should().ContainSingle(source => source.ResolvedCentralArtifactId == null);
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(
            AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new MutableTimeProvider(DateTimeOffset.UtcNow.AddHours(1)),
            telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);
        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Clear();
        var reconciled = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
            .Include(item => item.Sources)
            .SingleAsync(item => item.ArtifactId == productManifest.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        reconciled.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
        reconciled.Sources.Should().ContainSingle(source => source.ResolvedCentralArtifactId == null);
    }

    [TestMethod]
    public async Task StructuredProductIngest_CopyFailureRecoversAcrossHostInstance()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("structured-copy-recovery-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var rawPayload = new byte[] { 9, 10, 11, 12 };
        var rawManifest = CreateManifestV2(deviceId, rig, rawPayload, captureSequence: 10);
        var layerPayload = PresentationLayerPayloadJson.Create(
            Convert.ToHexString(SHA256.HashData(rawPayload)), 2, 2);
        var layerBytes = PresentationLayerPayloadJson.Serialize(layerPayload);
        var productManifest = CreateStructuredManifest(rawManifest, layerPayload, layerBytes);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var rawResponse = await PostAsync(client, rawManifest, rawPayload).ConfigureAwait(false);
        rawResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var copyFault = new CopyObjectFaultHandler { InnerHandler = new SocketsHttpHandler() };
        using var faultFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(copyFault, disposeHandler: false), disposeHttpClient: true)
                .Build());
        }));
        using (var faultClient = faultFactory.CreateClient())
        {
            faultClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetSystemTokenAsync(faultClient).ConfigureAwait(false));
            using var failed = await PostAsync(faultClient, productManifest, layerBytes).ConfigureAwait(false);
            failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
        await using (var pendingScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var pending = await pendingScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
                .Include(item => item.StructuredProduct)
                .SingleAsync(item => item.ArtifactId == productManifest.Descriptor.Artifact.ArtifactId)
                .ConfigureAwait(false);
            pending.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
            pending.StructuredProduct.Should().NotBeNull();
        }

        using var recovery = await PostAsync(client, productManifest, layerBytes).ConfigureAwait(false);
        var recoveryBody = await recovery.Content.ReadAsStringAsync().ConfigureAwait(false);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var structuredRows = await assertionDb.CentralStructuredProcessingProducts.CountAsync(item =>
            item.Artifact!.ArtifactId == productManifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
        recovery.StatusCode.Should().Be(HttpStatusCode.Accepted,
            $"{recoveryBody}; structuredRows={structuredRows}");
        var artifact = await assertionDb.CentralArtifacts
            .Include(item => item.StructuredProduct)
            .Include(item => item.Recipe)
            .Include(item => item.Sources)
            .SingleAsync(item => item.ArtifactId == productManifest.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        artifact.StructuredProduct.Should().NotBeNull();
        artifact.Recipe.Should().NotBeNull();
        artifact.Sources.Should().ContainSingle(source => source.ResolvedCentralArtifactId != null);
        (await assertionDb.CentralStructuredProcessingProducts.CountAsync(item =>
            item.CentralArtifactId == artifact.Id).ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task MultipartIngestV2_WithAcknowledgedLocation_PersistsAndReconstructsExactProvenance()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("location-resolved-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var location = await SeedAcknowledgedDeploymentLocationAsync(registrationId).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(
            deviceId, rig, payload, 170, location: location, capturedAtUtc: DateTimeOffset.UtcNow);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts
            .Include(item => item.Layout)
            .Include(item => item.Recipe)
            .Include(item => item.Sources)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Control)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Profiles)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Location)
            .AsSplitQuery()
            .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        artifact.Frame!.LocationEvidenceState.Should().Be(CentralCaptureLocationEvidenceState.ReportedResolved);
        artifact.Frame.Location!.DeviceDeploymentLocationVersionId.Should().NotBeNull();
        var persistedDescriptor = CentralReconstructionDescriptorFactory.Create(artifact.Frame, artifact);
        persistedDescriptor.Location.Should().Be(location);
        var compatibility = CentralDerivativeWindowCompatibility.CreateSnapshot(persistedDescriptor);
        CentralDerivativeWindowCompatibility.CreateSnapshot(persistedDescriptor).Sha256.Should().Be(compatibility.Sha256);
        CentralDerivativeWindowCompatibility.CreateSnapshot(persistedDescriptor with
        {
            Location = location with { Version = location.Version + 1 }
        }).Sha256.Should().NotBe(compatibility.Sha256);
        CentralDerivativeWindowCompatibility.CreateSnapshot(persistedDescriptor with { Location = null }).Sha256
            .Should().NotBe(compatibility.Sha256);
        var annotation = await db.CentralDerivativeJobs.SingleAsync(job =>
            job.SourceCentralArtifactId == artifact.Id && job.RecipeName == BuiltInProcessingRecipes.Annotation)
            .ConfigureAwait(false);
        annotation.Status.Should().NotBe(CentralDerivativeJobStatus.Quarantined);
    }

    [TestMethod]
    [DataRow(-1, false)]
    [DataRow(0, true)]
    [DataRow(119_999, true)]
    [DataRow(120_000, false)]
    public async Task MultipartIngestV2_ManifestLocationInterval_UsesHalfOpenBoundaries(
        int offsetMilliseconds,
        bool expectedAccepted)
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig($"location-interval-{offsetMilliseconds}");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var effectiveFromUtc = DateTimeOffset.UtcNow.AddMinutes(1);
        var effectiveUntilUtc = effectiveFromUtc.AddMinutes(2);
        var location = await SeedAcknowledgedDeploymentLocationAsync(
            registrationId, effectiveFromUtc, effectiveUntilUtc).ConfigureAwait(false);
        var manifest = CreateManifestV2(
            deviceId,
            rig,
            [1, 2, 3, 4],
            176 + offsetMilliseconds,
            location: location,
            capturedAtUtc: effectiveFromUtc.AddMilliseconds(offsetMilliseconds));
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var response = await PostAsync(client, manifest, [1, 2, 3, 4]).ConfigureAwait(false);

        response.StatusCode.Should().Be(expectedAccepted ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest);
        if (!expectedAccepted)
        {
            return;
        }
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var frame = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralFrames.Include(item => item.Location)
            .SingleAsync(item => item.FrameId == manifest.Descriptor.Capture.CaptureId)
            .ConfigureAwait(false);
        frame.LocationEvidenceState.Should().Be(CentralCaptureLocationEvidenceState.ReportedResolved);
        frame.Location!.DeviceDeploymentLocationVersionId.Should().NotBeNull();
    }

    [TestMethod]
    public async Task MultipartIngestV2_WithUnknownLocation_QuarantinesOnlyLocationDependentWork()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("location-unresolved-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        await EnsureObservatoryLocationVersionAsync(registrationId).ConfigureAwait(false);
        var location = new CaptureLocationProvenance(
            "unreported-deployment", 1, "gps-receiver", 4, DateTimeOffset.UnixEpoch.AddDays(-1), null);
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(
            deviceId, rig, payload, 171, location: location, capturedAtUtc: DateTimeOffset.UtcNow);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Location)
            .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        artifact.Frame!.LocationEvidenceState.Should().Be(CentralCaptureLocationEvidenceState.ReportedUnresolved);
        artifact.Frame.Location!.DeviceDeploymentLocationVersionId.Should().BeNull();
        var jobs = await db.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId == artifact.Id)
            .ToListAsync().ConfigureAwait(false);
        var quarantinedAnnotation = jobs.Single(job => job.RecipeName == BuiltInProcessingRecipes.Annotation);
        quarantinedAnnotation.Should().Match<CentralDerivativeJob>(job =>
            job.Status == CentralDerivativeJobStatus.Quarantined
            && job.StateReasonCode == CentralDerivativeJobScheduler.LocationUnresolvedReason);
        quarantinedAnnotation.CompletedAtUtc.Should().BeNull();
        quarantinedAnnotation.ResolutionCompletedAtUtc.Should().NotBeNull();
        jobs.Where(job => job.RecipeName != BuiltInProcessingRecipes.Annotation)
            .Should().OnlyContain(job => job.Status != CentralDerivativeJobStatus.Quarantined);

        var registration = await db.DeviceRegistrations.Include(item => item.Observatory)
            .SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
        var observatory = registration.Observatory!;
        var deployment = DeploymentLocationSnapshot.Create(
            location.LocationId,
            location.Version,
            location.Source,
            location.HorizontalAccuracyMeters,
            location.EffectiveFromUtc,
            location.EffectiveUntilUtc,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId);
        var authority = assertionScope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
        var acknowledgment = await authority.ProposeAsync(
            registration,
            deployment,
            DeploymentLocationSourceKind.Inherited,
            "integration-test").ConfigureAwait(false);
        acknowledgment.Status.Should().Be(DeploymentLocationResolutionStatus.Acknowledged);
        await db.SaveChangesAsync().ConfigureAwait(false);
        for (var step = 0; step < 20; step++)
        {
            await using var processScope = fixture.Factory.Services.CreateAsyncScope();
            var processed = await processScope.ServiceProvider
                .GetRequiredService<IDeploymentLocationReconciliationProcessor>()
                .ProcessNextAsync().ConfigureAwait(false);
            if (!processed)
            {
                break;
            }
        }
        db.ChangeTracker.Clear();
        var reconciledFrame = await db.CentralFrames.Include(item => item.Location)
            .SingleAsync(item => item.Id == artifact.CentralFrameId).ConfigureAwait(false);
        var restoredAnnotation = await db.CentralDerivativeJobs.SingleAsync(job =>
            job.SourceCentralArtifactId == artifact.Id && job.RecipeName == BuiltInProcessingRecipes.Annotation)
            .ConfigureAwait(false);
        reconciledFrame.LocationEvidenceState.Should().Be(CentralCaptureLocationEvidenceState.ReportedResolved);
        reconciledFrame.Location!.DeviceDeploymentLocationVersionId.Should().NotBeNull();
        restoredAnnotation.Status.Should().Be(CentralDerivativeJobStatus.Pending);
        restoredAnnotation.AvailableAtUtc.Should().NotBeNull();
        restoredAnnotation.StateReasonCode.Should().BeNull();
        restoredAnnotation.CompletedAtUtc.Should().BeNull();
    }

    [TestMethod]
    public async Task LocationMismatch_QuarantinesLeasedAttemptAndRestorationCreatesNewAttempt()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("location-leased-annotation-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var location = await SeedAcknowledgedDeploymentLocationAsync(registrationId).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(
            deviceId, rig, payload, 174, location: location, capturedAtUtc: DateTimeOffset.UtcNow);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        Guid annotationJobId;
        Guid devicePublicId;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var source = await db.CentralArtifacts.SingleAsync(item =>
                item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
            devicePublicId = source.DevicePublicId!.Value;
            var annotation = await db.CentralDerivativeJobs.SingleAsync(item =>
                item.SourceCentralArtifactId == source.Id
                && item.RecipeName == BuiltInProcessingRecipes.Annotation).ConfigureAwait(false);
            annotationJobId = annotation.Id;
            await db.CentralDerivativeJobs.Where(item => item.Id != annotationJobId
                    && (item.Status == CentralDerivativeJobStatus.Pending
                        || item.Status == CentralDerivativeJobStatus.RetryableFailure))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
        }

        CentralDerivativeJobLease lease;
        await using (var claimScope = fixture.Factory.Services.CreateAsyncScope())
        {
            lease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("location-lease-worker", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false))!;
            lease.JobId.Should().Be(annotationJobId);
            lease.AttemptCount.Should().Be(1);
        }

        await using (var quarantineScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = quarantineScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts
                .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
                .Include(item => item.Frame)!.ThenInclude(frame => frame!.Location)
                .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
                .ConfigureAwait(false);
            artifact.Frame!.LocationEvidenceState = CentralCaptureLocationEvidenceState.Mismatch;
            await quarantineScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
                .EnsureRequiredJobsAsync(artifact, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);

            var job = await db.CentralDerivativeJobs.SingleAsync(item => item.Id == annotationJobId)
                .ConfigureAwait(false);
            var attempt = await db.CentralDerivativeJobAttempts.SingleAsync(item =>
                item.CentralDerivativeJobId == annotationJobId && item.AttemptNumber == 1).ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.Quarantined);
            job.LeaseToken.Should().BeNull();
            job.CompletedAtUtc.Should().BeNull();
            attempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.Quarantined);
            attempt.ReasonCode.Should().Be(CentralDerivativeJobScheduler.LocationMismatchReason);
            attempt.EndedAtUtc.Should().NotBeNull();
        }

        await using (var staleScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var jobs = staleScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            Func<Task> staleCompletion = () => jobs.CompleteWithoutArtifactAsync(
                lease.JobId, lease.LeaseToken, "stale-worker", CancellationToken.None);
            await staleCompletion.Should().ThrowAsync<CentralDerivativeJobStateException>();
        }

        await using (var restoreScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = restoreScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts
                .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
                .Include(item => item.Frame)!.ThenInclude(frame => frame!.Location)
                .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
                .ConfigureAwait(false);
            artifact.Frame!.LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedResolved;
            await restoreScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
                .EnsureRequiredJobsAsync(artifact, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
        }
        await using (var reclaimScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var replacement = await reclaimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("location-retry-worker", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false);
            replacement.Should().NotBeNull();
            replacement!.JobId.Should().Be(annotationJobId);
            replacement.AttemptCount.Should().Be(2);
            replacement.SourceDevicePublicId.Should().Be(devicePublicId);
            await reclaimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .CompleteWithoutArtifactAsync(
                    replacement.JobId,
                    replacement.LeaseToken,
                    "location-reconciliation-test-complete",
                    CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ObservatoryChange_PreservesHistoricalCaptureAndCompletedAnnotation()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("location-completed-annotation-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var location = await SeedAcknowledgedDeploymentLocationAsync(registrationId).ConfigureAwait(false);
        var payload = new byte[] { 1, 32, 128, 255 };
        var historicalCapturedAtUtc = DateTimeOffset.UtcNow;
        var manifest = CreateManifestV2(
            deviceId, rig, payload, 172, location: location, capturedAtUtc: historicalCapturedAtUtc) with
        {
            Scene = CreateSceneProvenance()
        };
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var ingestResponse = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        ingestResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        Guid sourceId;
        Guid devicePublicId;
        await using (var schedulingScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var schedulingDb = schedulingScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sourceIdentity = await schedulingDb.CentralArtifacts
                .Where(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
                .Select(item => new { item.Id, item.DevicePublicId })
                .SingleAsync().ConfigureAwait(false);
            sourceId = sourceIdentity.Id;
            devicePublicId = sourceIdentity.DevicePublicId!.Value;
            await schedulingDb.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId != sourceId
                    && (job.Status == CentralDerivativeJobStatus.Pending
                        || job.Status == CentralDerivativeJobStatus.RetryableFailure))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
        }

        CentralDerivativeExecutionResult? annotationExecution = null;
        for (var index = 0; index < 3 && annotationExecution is null; index++)
        {
            await using var workerScope = fixture.Factory.Services.CreateAsyncScope();
            var jobs = workerScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            var lease = await jobs.ClaimNextAsync(
                "location-quarantine-worker", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.SourceArtifactId.Should().Be(manifest.Descriptor.Artifact.ArtifactId);
            var execution = await workerScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            execution.Status.Should().Be(ProcessingOutcomeStatus.Produced);
            if (lease.RecipeName == BuiltInProcessingRecipes.Annotation)
            {
                annotationExecution = execution;
            }
        }
        annotationExecution.Should().NotBeNull();

        await using (var quarantineScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = quarantineScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var source = await db.CentralArtifacts
                .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
                .SingleAsync(item => item.Id == sourceId).ConfigureAwait(false);
            var annotation = await db.CentralDerivativeJobs.SingleAsync(job =>
                job.SourceCentralArtifactId == sourceId && job.RecipeName == BuiltInProcessingRecipes.Annotation)
                .ConfigureAwait(false);
            var result = source.Frame!.Artifacts.Single(item => item.Id == annotation.ResultCentralArtifactId);
            var retainedCompletedAtUtc = annotation.CompletedAtUtc;
            source.Frame.LocationEvidenceState = CentralCaptureLocationEvidenceState.Mismatch;
            var scheduler = quarantineScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>();

            await scheduler.EnsureRequiredJobsAsync(source, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);

            annotation.Status.Should().Be(CentralDerivativeJobStatus.Quarantined);
            result.StateReasonCode.Should().Be(CentralDerivativeJobScheduler.LocationMismatchReason);
            result.ReconstructionState.Should().Be(CentralReconstructionState.Quarantined);

            using (var telemetry = new CentralIngestTelemetry())
            {
                var reconciler = new CentralArtifactReconciliationService(
                    fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                    TimeProvider.System,
                    telemetry,
                    NullLogger<CentralArtifactReconciliationService>.Instance);
                await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            db.ChangeTracker.Clear();
            var retainedResult = await db.CentralArtifacts.AsNoTracking()
                .SingleAsync(item => item.Id == result.Id).ConfigureAwait(false);
            retainedResult.StateReasonCode.Should().Be(CentralDerivativeJobScheduler.LocationMismatchReason);
            retainedResult.ReconstructionState.Should().Be(CentralReconstructionState.Quarantined);

            annotation.LastError.Should().Be(CentralDerivativeJobScheduler.LocationMismatchReason);
            annotation.ResultCentralArtifactId.Should().Be(result.Id);
            await db.CentralFrames.Where(frame => frame.Id == source.CentralFrameId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    frame => frame.LocationEvidenceState,
                    CentralCaptureLocationEvidenceState.ReportedResolved))
                .ConfigureAwait(false);
            db.ChangeTracker.Clear();
            var resolvedSource = await db.CentralArtifacts
                .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
                .SingleAsync(item => item.Id == sourceId).ConfigureAwait(false);

            await scheduler.EnsureRequiredJobsAsync(resolvedSource, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);

            var restoredAnnotation = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(job =>
                job.SourceCentralArtifactId == sourceId && job.RecipeName == BuiltInProcessingRecipes.Annotation)
                .ConfigureAwait(false);
            var restoredResult = await db.CentralArtifacts.AsNoTracking()
                .SingleAsync(item => item.Id == result.Id).ConfigureAwait(false);
            restoredResult.StateReasonCode.Should().BeNull();
            restoredResult.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
            restoredAnnotation.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            restoredAnnotation.CompletedAtUtc.Should().Be(retainedCompletedAtUtc);
        }

        DateTimeOffset completedAtUtc;
        Guid pendingEvaluationId;
        Guid pendingToken;
        await using (var mismatchScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = mismatchScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.Include(item => item.Observatory)
                .SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
            var observatory = registration.Observatory!;
            var service = mismatchScope.ServiceProvider.GetRequiredService<IObservatoryService>();
            await service.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
                observatory.Id,
                registration.OwnerUserId,
                observatory.Name,
                observatory.LatitudeDegrees + 0.001,
                observatory.LongitudeDegrees,
                observatory.ElevationMeters,
                observatory.TimeZoneId,
                observatory.IsActive,
                observatory.AllowedDeploymentRadiusMeters)).ConfigureAwait(false);

            var annotation = await db.CentralDerivativeJobs.SingleAsync(job =>
                job.SourceCentralArtifactId == sourceId && job.RecipeName == BuiltInProcessingRecipes.Annotation)
                .ConfigureAwait(false);
            var result = await db.CentralArtifacts.SingleAsync(item => item.Id == annotation.ResultCentralArtifactId)
                .ConfigureAwait(false);
            annotation.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            annotation.StateReasonCode.Should().BeNull();
            result.StateReasonCode.Should().BeNull();
            result.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
            completedAtUtc = annotation.CompletedAtUtc!.Value;
            var pending = await db.DeviceDeploymentLocationVersions.SingleAsync(item =>
                item.RegistrationId == registrationId
                && item.Status == DeploymentLocationResolutionStatus.Pending).ConfigureAwait(false);
            pendingEvaluationId = pending.Id;
            pendingToken = pending.ConcurrencyToken;
        }

        var delayedManifest = CreateManifestV2(
            deviceId, rig, payload, 173, location: location, capturedAtUtc: historicalCapturedAtUtc);
        using (var delayedResponse = await PostAsync(client, delayedManifest, payload).ConfigureAwait(false))
        {
            delayedResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        await using (var delayedScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var delayedFrame = await delayedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralFrames.Include(item => item.Location)!.ThenInclude(item => item!.DeploymentLocation)
                .SingleAsync(item => item.FrameId == delayedManifest.Descriptor.Capture.CaptureId)
                .ConfigureAwait(false);
            delayedFrame.LocationEvidenceState.Should().Be(CentralCaptureLocationEvidenceState.ReportedResolved);
            delayedFrame.Location!.DeploymentLocation!.ObservatoryLocationVersionNumber.Should().Be(1);
        }
        var currentManifest = CreateManifestV2(
            deviceId, rig, payload, 175, location: location, capturedAtUtc: DateTimeOffset.UtcNow);
        using (var currentResponse = await PostAsync(client, currentManifest, payload).ConfigureAwait(false))
        {
            currentResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        await using (var currentScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var currentFrame = await currentScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralFrames.Include(item => item.Location)!.ThenInclude(item => item!.DeploymentLocation)
                .SingleAsync(item => item.FrameId == currentManifest.Descriptor.Capture.CaptureId)
                .ConfigureAwait(false);
            currentFrame.LocationEvidenceState.Should().Be(CentralCaptureLocationEvidenceState.Mismatch);
            currentFrame.Location!.DeploymentLocation!.ObservatoryLocationVersionNumber.Should().Be(2);
        }

        using var ownerClient = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        var annotationArtifactId = annotationExecution!.ArtifactId!.Value;
        using (var available = await ownerClient.GetAsync(new Uri(
                   $"/api/v1.0/devices/{devicePublicId:D}/artifacts/{annotationArtifactId:D}/content",
                   UriKind.Relative)).ConfigureAwait(false))
        {
            available.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await using (var resolutionScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var authority = resolutionScope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
            var resolved = await authority.ResolveAsync(
                pendingEvaluationId,
                (await resolutionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().DeviceRegistrations
                    .Where(item => item.Id == registrationId)
                    .Select(item => item.OwnerUserId)
                    .SingleAsync().ConfigureAwait(false)),
                DeploymentLocationResolutionStatus.Acknowledged,
                "owner-approved-current-observatory",
                pendingToken).ConfigureAwait(false);
            resolved.Status.Should().Be(DeploymentLocationMutationStatus.Applied);
        }
        for (var step = 0; step < 30; step++)
        {
            await using var processScope = fixture.Factory.Services.CreateAsyncScope();
            if (!await processScope.ServiceProvider
                .GetRequiredService<IDeploymentLocationReconciliationProcessor>()
                .ProcessNextAsync().ConfigureAwait(false))
            {
                break;
            }
        }

        await using (var restoredScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = restoredScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var annotation = await db.CentralDerivativeJobs.SingleAsync(job =>
                job.SourceCentralArtifactId == sourceId && job.RecipeName == BuiltInProcessingRecipes.Annotation)
                .ConfigureAwait(false);
            var result = await db.CentralArtifacts.SingleAsync(item => item.Id == annotation.ResultCentralArtifactId)
                .ConfigureAwait(false);
            annotation.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            annotation.CompletedAtUtc.Should().Be(completedAtUtc);
            result.StateReasonCode.Should().BeNull();
            result.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
            var centralFrameId = await db.CentralArtifacts.Where(item => item.Id == sourceId)
                .Select(item => item.CentralFrameId).SingleAsync().ConfigureAwait(false);
            var frame = await db.CentralFrames.Include(item => item.Location)!.ThenInclude(item => item!.DeploymentLocation)
                .SingleAsync(item => item.Id == centralFrameId).ConfigureAwait(false);
            frame.LocationEvidenceState.Should().Be(CentralCaptureLocationEvidenceState.ReportedResolved);
            frame.Location!.DeploymentLocation!.ObservatoryLocationVersionNumber.Should().Be(1);
            var currentFrame = await db.CentralFrames.Include(item => item.Location)
                .SingleAsync(item => item.FrameId == currentManifest.Descriptor.Capture.CaptureId)
                .ConfigureAwait(false);
            currentFrame.LocationEvidenceState.Should().Be(CentralCaptureLocationEvidenceState.ReportedResolved);
        }
        _ = await ReadDerivativeAsync(
            ownerClient, devicePublicId, annotationArtifactId).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MultipartIngestV2_WithDifferentLocationForSameFrame_RejectsCaptureFactConflict()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("location-conflict-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var captureId = Guid.NewGuid();
        var location = new CaptureLocationProvenance(
            "deployment-conflict", 1, "gps-receiver", 4, DateTimeOffset.UnixEpoch.AddDays(-1), null);
        var first = CreateManifestV2(deviceId, rig, payload, 172, captureId: captureId, location: location);
        var conflict = CreateManifestV2(
            deviceId,
            rig,
            payload,
            172,
            role: FrameArtifactRole.Preview,
            sourceArtifactIds: [first.Descriptor.Artifact.ArtifactId],
            captureId: captureId,
            location: location with { Source = "operator-edited" });
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var firstResponse = await PostAsync(client, first, payload).ConfigureAwait(false);
        using var conflictResponse = await PostAsync(client, conflict, payload).ConfigureAwait(false);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        conflictResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [TestMethod]
    public async Task MultipartIngestV2_ConcurrentEnrichmentOfOneFrameSerializesCaptureFacts()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("frame-enrichment-concurrency-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var captureId = Guid.NewGuid();
        var source = CreateManifestV2(deviceId, rig, payload, 174);
        var first = CreateManifestV2(deviceId, rig, payload, 175, captureId: captureId);
        var conflict = CreateManifestV2(
            deviceId,
            rig,
            payload,
            176,
            role: FrameArtifactRole.Preview,
            sourceArtifactIds: [source.Descriptor.Artifact.ArtifactId],
            captureId: captureId);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var sourceResponse = await PostAsync(client, source, payload).ConfigureAwait(false);
        sourceResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var firstCompatibility = await PostAsync(
            client, CreateCompatibilityManifest(first), payloadBytes: payload)
            .ConfigureAwait(false);
        using var conflictCompatibility = await PostAsync(
            client, CreateCompatibilityManifest(conflict), payloadBytes: payload)
            .ConfigureAwait(false);
        firstCompatibility.StatusCode.Should().Be(HttpStatusCode.Accepted);
        conflictCompatibility.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await using var lockScope = fixture.Factory.Services.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var devicePublicId = await lockDb.DeviceRegistrations.Where(item => item.Id == registrationId)
            .Select(item => item.DevicePublicId!.Value)
            .SingleAsync().ConfigureAwait(false);
        await using var transaction = await lockDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        var holderSessionId = await lockDb.Database.SqlQueryRaw<int>("SELECT CAST(@@SPID AS int) AS [Value]")
            .SingleAsync().ConfigureAwait(false);
        var resource = ArtifactIngestService.CreateFrameIdentityLockResource(devicePublicId, captureId);
        await lockDb.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 10000;
            IF @result < 0 THROW 51009, 'Could not acquire the test frame identity lock.', 1;
            """).ConfigureAwait(false);

        var requests = new[]
        {
            PostAsync(client, first, payload),
            PostAsync(client, conflict, payload)
        };
        await using var observerScope = fixture.Factory.Services.CreateAsyncScope();
        var observerDb = observerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var blockedRequests = 0;
        var lockReleased = false;
        try
        {
            for (var attempt = 0; attempt < 50 && blockedRequests < requests.Length; attempt++)
            {
                var blockedRequestCounts = await observerDb.Database.SqlQuery<int>($"""
                    WITH [Blocked] AS
                    (
                        SELECT [session_id]
                        FROM sys.dm_exec_requests
                        WHERE [blocking_session_id] = {holderSessionId}
                        UNION ALL
                        SELECT request.[session_id]
                        FROM sys.dm_exec_requests AS request
                        INNER JOIN [Blocked] AS blocked
                            ON request.[blocking_session_id] = blocked.[session_id]
                    )
                    SELECT COUNT(*) AS [Value]
                    FROM [Blocked]
                    OPTION (MAXRECURSION 100)
                    """).ToListAsync().ConfigureAwait(false);
                blockedRequests = blockedRequestCounts.Single();
                if (blockedRequests < requests.Length)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                }
            }
            if (blockedRequests != requests.Length || requests.Any(request => request.IsCompleted))
            {
                Assert.Fail($"Expected both frame enrichments to wait in the held identity-lock chain; observed {blockedRequests} waiters.");
            }
            await transaction.CommitAsync().ConfigureAwait(false);
            lockReleased = true;
        }
        finally
        {
            if (!lockReleased)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                var abandoned = await Task.WhenAll(requests).ConfigureAwait(false);
                foreach (var response in abandoned)
                {
                    response.Dispose();
                }
            }
        }
        var responses = await Task.WhenAll(requests).ConfigureAwait(false);
        using var firstResponse = responses[0];
        using var conflictResponse = responses[1];

        responses.Select(response => response.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.Accepted, HttpStatusCode.Conflict]);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var frame = await assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralFrames.Include(item => item.Artifacts)
            .SingleAsync(item => item.FrameId == captureId).ConfigureAwait(false);
        frame.CaptureSequence.Should().Be(
            firstResponse.StatusCode == HttpStatusCode.Accepted ? 175 : 176,
            "the accepted request must own the immutable capture facts");
        frame.Artifacts.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task MultipartIngestV2_AfterReassignmentFreezesDeploymentObservatoryForLateFrameArtifacts()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("location-reassignment-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var location = await SeedAcknowledgedDeploymentLocationAsync(registrationId).ConfigureAwait(false);
        var historicalCapturedAtUtc = DateTimeOffset.UtcNow;
        Guid historicalObservatoryId;
        Guid reassignedObservatoryId;
        await using (var reassignmentScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = reassignmentScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId)
                .ConfigureAwait(false);
            historicalObservatoryId = registration.ObservatoryId;
            var current = new Observatory
            {
                OwnerUserId = registration.OwnerUserId,
                Name = "Reassigned Observatory",
                TimeZoneId = "UTC",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                IsActive = true
            };
            reassignedObservatoryId = current.Id;
            db.Observatories.Add(current);
            registration.ObservatoryId = current.Id;
            registration.ObservatoryName = current.Name;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        var payload = new byte[] { 1, 2, 3, 4 };
        var captureId = Guid.NewGuid();
        var raw = CreateManifestV2(
            deviceId,
            rig,
            payload,
            173,
            captureId: captureId,
            location: location,
            capturedAtUtc: historicalCapturedAtUtc);
        var preview = CreateManifestV2(
            deviceId,
            rig,
            payload,
            173,
            role: FrameArtifactRole.Preview,
            sourceArtifactIds: [raw.Descriptor.Artifact.ArtifactId],
            captureId: captureId,
            location: location,
            capturedAtUtc: historicalCapturedAtUtc);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var rawResponse = await PostAsync(client, raw, payload).ConfigureAwait(false);
        using var previewResponse = await PostAsync(client, preview, payload).ConfigureAwait(false);

        rawResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var frame = await assertionDb.CentralFrames.Include(item => item.Artifacts)
            .SingleAsync(item => item.FrameId == captureId).ConfigureAwait(false);
        frame.ObservatoryId.Should().Be(historicalObservatoryId);
        frame.Artifacts.Should().HaveCount(2);
        var restoredRegistration = await assertionDb.DeviceRegistrations.SingleAsync(item => item.Id == registrationId)
            .ConfigureAwait(false);
        restoredRegistration.ObservatoryId = historicalObservatoryId;
        assertionDb.Observatories.Remove(await assertionDb.Observatories.SingleAsync(item =>
            item.Id == reassignedObservatoryId).ConfigureAwait(false));
        await assertionDb.SaveChangesAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MultipartIngestV2_SameArtifactWithDifferentLocationRejectsWithoutDuplicate()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("location-artifact-replay-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var artifactId = Guid.NewGuid();
        var captureId = Guid.NewGuid();
        var location = new CaptureLocationProvenance(
            "deployment-replay", 1, "gps-receiver", 4, DateTimeOffset.UnixEpoch.AddDays(-1), null);
        var first = CreateManifestV2(
            deviceId, rig, payload, 174, artifactId: artifactId, captureId: captureId, location: location);
        var conflict = CreateManifestV2(
            deviceId,
            rig,
            payload,
            174,
            artifactId: artifactId,
            captureId: captureId,
            location: location with { HorizontalAccuracyMeters = 5 });
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var firstResponse = await PostAsync(client, first, payload).ConfigureAwait(false);
        using var conflictResponse = await PostAsync(client, conflict, payload).ConfigureAwait(false);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        conflictResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.CentralArtifacts.CountAsync(item => item.DevicePublicId != null && item.ArtifactId == artifactId)
            .ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task MultipartIngestV2_AllCentralRecipesConformToSharedExecution()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("central-conformance-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 32, 128, 255 };
        var scene = CreateSceneProvenance();
        var manifest = CreateManifestV2(deviceId, rig, payload, captureSequence: 44) with { Scene = scene };
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await using (var schedulingScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var schedulingDb = schedulingScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sourceId = await schedulingDb.CentralArtifacts.Where(item =>
                    item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
                .Select(item => item.Id)
                .SingleAsync().ConfigureAwait(false);
            await schedulingDb.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId != sourceId
                    && (job.Status == CentralDerivativeJobStatus.Pending
                        || job.Status == CentralDerivativeJobStatus.RetryableFailure))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
        }

        var executions = new Dictionary<string, CentralDerivativeExecutionResult>(StringComparer.Ordinal);
        for (var index = 0; index < 3; index++)
        {
            await using var workerScope = fixture.Factory.Services.CreateAsyncScope();
            var jobs = workerScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            var lease = await jobs.ClaimNextAsync(
                "conformance-worker", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.SourceArtifactId.Should().Be(manifest.Descriptor.Artifact.ArtifactId);
            executions[lease.RecipeName] = await workerScope.ServiceProvider
                .GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        executions.Keys.Should().BeEquivalentTo(
            BuiltInProcessingRecipes.EncodedPreview,
            BuiltInProcessingRecipes.Annotation,
            BuiltInProcessingRecipes.ImageQuality);
        executions.Values.Should().OnlyContain(result => result.Status == ProcessingOutcomeStatus.Produced);

        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var source = await db.CentralArtifacts.SingleAsync(item =>
            item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
        var jobsByRecipe = await db.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.SourceCentralArtifactId == source.Id)
            .ToDictionaryAsync(job => job.RecipeName, StringComparer.Ordinal).ConfigureAwait(false);
        jobsByRecipe.Values.Should().OnlyContain(job => job.TraceParent != null
            && job.TraceParent.StartsWith("00-", StringComparison.Ordinal));
        var resultIds = executions.Values.Select(result => result.ArtifactId!.Value).ToArray();
        var outputs = await db.CentralArtifacts.AsNoTracking()
            .Include(item => item.Recipe)
            .Include(item => item.Sources)
            .Where(item => resultIds.Contains(item.ArtifactId))
            .ToDictionaryAsync(item => item.ArtifactId).ConfigureAwait(false);
        outputs.Should().HaveCount(3);
        foreach (var pair in executions)
        {
            var job = jobsByRecipe[pair.Key];
            var output = outputs[pair.Value.ArtifactId!.Value];
            output.RecipeVersion.Should().Be(job.TargetRecipeVersion);
            output.Sources.Should().ContainSingle(item => item.ResolvedCentralArtifactId == source.Id);
            output.ObjectState.Should().Be(CentralArtifactObjectState.Available);
            output.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        }

        using var ownerClient = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        var centralPreview = await ReadDerivativeAsync(
            ownerClient, source.DevicePublicId!.Value, executions[BuiltInProcessingRecipes.EncodedPreview].ArtifactId!.Value)
            .ConfigureAwait(false);
        var centralAnnotation = await ReadDerivativeAsync(
            ownerClient, source.DevicePublicId!.Value, executions[BuiltInProcessingRecipes.Annotation].ArtifactId!.Value)
            .ConfigureAwait(false);
        var decodedPreview = JpegImageCodec.DecodeJpeg(centralPreview);
        var decodedAnnotation = JpegImageCodec.DecodeJpeg(centralAnnotation);
        decodedPreview.Width.Should().Be(2);
        decodedPreview.Height.Should().Be(2);
        decodedAnnotation.Width.Should().Be(2);
        decodedAnnotation.Height.Should().Be(2);

        var adapter = assertionScope.ServiceProvider.GetRequiredService<LogicHostRecipeExecutionAdapter>();
        var previewJob = jobsByRecipe[BuiltInProcessingRecipes.EncodedPreview];
        using var previewOptions = JsonDocument.Parse(previewJob.RecipeOptionsJson);
        var expectedPreview = await adapter.ExecuteAsync(
            manifest.Descriptor,
            payload,
            previewJob.RecipeName,
            previewOptions.RootElement.Clone(),
            ProcessingInputSelector.Raw(),
            previewJob.TargetVariant).ConfigureAwait(false);
        centralPreview.Should().Equal(expectedPreview.Products.Single().Payload.ToArray());

        var annotationJob = jobsByRecipe[BuiltInProcessingRecipes.Annotation];
        using var annotationOptions = JsonDocument.Parse(annotationJob.RecipeOptionsJson);
        var annotationInput = CreateExpectedAnnotation(scene);
        var expectedAnnotation = await adapter.ExecuteAsync(
            manifest.Descriptor,
            payload,
            annotationJob.RecipeName,
            annotationOptions.RootElement.Clone(),
            ProcessingInputSelector.Raw(),
            annotationJob.TargetVariant,
            annotationInput).ConfigureAwait(false);
        centralAnnotation.Should().Equal(expectedAnnotation.Products.Single().Payload.ToArray());
        outputs[executions[BuiltInProcessingRecipes.EncodedPreview].ArtifactId!.Value].Recipe!.ImplementationVersion
            .Should().Be("encoded-preview-v1");
        outputs[executions[BuiltInProcessingRecipes.Annotation].ArtifactId!.Value].Recipe!.ImplementationVersion
            .Should().Be("projected-annotation-v3");
    }

    [TestMethod]
    public async Task CentralDerivativeMissingInputSuspendsWorkUntilObjectIsRestored()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("central-missing-input-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 3, 6, 9, 12 };
        var manifest = CreateManifestV2(deviceId, rig, payload, captureSequence: 45);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        Guid sourceId;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            sourceId = await setupDb.CentralArtifacts.Where(item =>
                    item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
                .Select(item => item.Id)
                .SingleAsync().ConfigureAwait(false);
            await setupDb.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId != sourceId
                    && (job.Status == CentralDerivativeJobStatus.Pending
                        || job.Status == CentralDerivativeJobStatus.RetryableFailure))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
            await setupDb.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId == sourceId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.MaxAttempts, 1))
                .ConfigureAwait(false);
        }
        await RemoveArtifactObjectAsync(registrationId, manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);

        Guid claimedJobId;
        await using (var workerScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var jobs = workerScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            var lease = await jobs.ClaimNextAsync(
                "missing-input-worker", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.SourceArtifactId.Should().Be(manifest.Descriptor.Artifact.ArtifactId);
            claimedJobId = lease.JobId;
            var executor = workerScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>();
            await FluentActions.Awaiting(() => executor.ExecuteAsync(lease, CancellationToken.None))
                .Should().ThrowAsync<CentralArtifactMissingException>().ConfigureAwait(false);
        }

        await using (var unavailableScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var unavailableDb = unavailableScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var source = await unavailableDb.CentralArtifacts.SingleAsync(item => item.Id == sourceId)
                .ConfigureAwait(false);
            source.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
            source.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
            source.StateReasonCode.Should().Be("object.missing");
            var jobs = await unavailableDb.CentralDerivativeJobs
                .Where(job => job.SourceCentralArtifactId == sourceId)
                .ToListAsync().ConfigureAwait(false);
            jobs.Should().HaveCount(4);
            jobs.Where(job => job.RecipeName != BuiltInProcessingRecipes.RollingMean)
                .Should().OnlyContain(job => job.Status == CentralDerivativeJobStatus.RetryableFailure
                    && job.AvailableAtUtc == null);
            jobs.Single(job => job.RecipeName == BuiltInProcessingRecipes.RollingMean).Status
                .Should().Be(CentralDerivativeJobStatus.Waiting);
            var attempt = await unavailableDb.CentralDerivativeJobAttempts.SingleAsync(item =>
                item.CentralDerivativeJobId == claimedJobId).ConfigureAwait(false);
            attempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.RetryableFailure);
            attempt.ReasonCode.Should().Be("object.missing");
            (await unavailableDb.CentralArtifactProcessingEvidence.CountAsync(item =>
                jobs.Select(job => job.Id).Contains(item.CentralDerivativeJobId)).ConfigureAwait(false)).Should().Be(0);
        }

        using var retry = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var restoredScope = fixture.Factory.Services.CreateAsyncScope();
        var restoredDb = restoredScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var restored = await restoredDb.CentralArtifacts.SingleAsync(item => item.Id == sourceId).ConfigureAwait(false);
        restored.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        restored.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        (await restoredDb.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId == sourceId)
            .CountAsync(job => job.Status == CentralDerivativeJobStatus.Pending
                && job.AvailableAtUtc != null).ConfigureAwait(false)).Should().Be(3);
        (await restoredDb.CentralDerivativeJobs.SingleAsync(job => job.SourceCentralArtifactId == sourceId
            && job.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false)).Status
            .Should().Be(CentralDerivativeJobStatus.Waiting);
        await restoredDb.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId == sourceId
                && job.Id != claimedJobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
        await using var recoveryScope = fixture.Factory.Services.CreateAsyncScope();
        var recoveryJobs = recoveryScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
        var recoveryLease = await recoveryJobs.ClaimNextAsync(
            "restored-input-worker", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        recoveryLease.Should().NotBeNull();
        recoveryLease!.JobId.Should().Be(claimedJobId);
        recoveryLease.AttemptCount.Should().Be(2);
        recoveryLease.MaxAttempts.Should().Be(6);
        var recovered = await recoveryScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
            .ExecuteAsync(recoveryLease, CancellationToken.None).ConfigureAwait(false);
        recovered.Status.Should().BeOneOf(ProcessingOutcomeStatus.Produced, ProcessingOutcomeStatus.Skipped);
        restoredDb.ChangeTracker.Clear();
        var attempts = await restoredDb.CentralDerivativeJobAttempts.Where(attempt =>
                attempt.CentralDerivativeJobId == claimedJobId)
            .OrderBy(attempt => attempt.AttemptNumber)
            .ToListAsync().ConfigureAwait(false);
        attempts.Select(attempt => attempt.Outcome).Should().Equal(
            CentralDerivativeAttemptOutcome.RetryableFailure,
            recovered.Status == ProcessingOutcomeStatus.Produced
                ? CentralDerivativeAttemptOutcome.Completed
                : CentralDerivativeAttemptOutcome.Skipped);
    }

    [TestMethod]
    public async Task CentralDerivativeCopyFailureRecoversOnePendingOutputAfterLeaseExpiry()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("central-copy-fault-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 2, 4, 6, 8 };
        var manifest = CreateManifestV2(deviceId, rig, payload, captureSequence: 46);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        Guid sourceId;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            sourceId = await setupDb.CentralArtifacts.Where(item =>
                    item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
                .Select(item => item.Id)
                .SingleAsync().ConfigureAwait(false);
            await setupDb.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId != sourceId
                    || job.TargetRole != FrameArtifactRole.Metadata)
                .Where(job => job.Status == CentralDerivativeJobStatus.Pending
                    || job.Status == CentralDerivativeJobStatus.RetryableFailure)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
        }

        var copyFault = new CopyObjectFaultHandler { InnerHandler = new SocketsHttpHandler() };
        using var faultFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(copyFault, disposeHandler: false), disposeHttpClient: true)
                .Build());
        }));
        Guid jobId;
        await using (var faultScope = faultFactory.Services.CreateAsyncScope())
        {
            var jobs = faultScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            var lease = await jobs.ClaimNextAsync(
                "copy-fault-worker", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.RecipeName.Should().Be(BuiltInProcessingRecipes.ImageQuality);
            jobId = lease.JobId;
            var executor = faultScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>();
            await FluentActions.Awaiting(() => executor.ExecuteAsync(lease, CancellationToken.None))
                .Should().ThrowAsync<MinioException>().ConfigureAwait(false);
        }

        Guid outputArtifactId;
        await using (var pendingScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var pendingDb = pendingScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var evidence = await pendingDb.CentralArtifactProcessingEvidence
                .SingleAsync(item => item.CentralDerivativeJobId == jobId).ConfigureAwait(false);
            outputArtifactId = evidence.CentralArtifactId;
            var pending = await pendingDb.CentralArtifacts.SingleAsync(item => item.Id == outputArtifactId)
                .ConfigureAwait(false);
            pending.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
            (await pendingDb.CentralArtifacts.CountAsync(item => item.Id == outputArtifactId).ConfigureAwait(false))
                .Should().Be(1);
            var retention = pendingScope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionService>();
            (await retention.ReleaseAsync(outputArtifactId, CancellationToken.None).ConfigureAwait(false))
                .Should().Be(CentralArtifactRetentionResult.Held);
            var operations = pendingScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>();
            await FluentActions.Awaiting(() => operations.CancelAsync(
                    jobId, "integration-test", CancellationToken.None))
                .Should().ThrowAsync<CentralDerivativeJobStateException>().ConfigureAwait(false);
            pendingDb.ChangeTracker.Clear();
            var job = await pendingDb.CentralDerivativeJobs.SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            job.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            var attempt = await pendingDb.CentralDerivativeJobAttempts.SingleAsync(item =>
                item.CentralDerivativeJobId == jobId).ConfigureAwait(false);
            attempt.LeaseExpiresAtUtc = job.LeaseExpiresAtUtc.Value;
            await pendingDb.SaveChangesAsync().ConfigureAwait(false);
        }

        await using (var recoveryScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var jobs = recoveryScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            var lease = await jobs.ClaimNextAsync(
                "copy-recovery-worker", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.JobId.Should().Be(jobId);
            var execution = await recoveryScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            execution.Status.Should().Be(ProcessingOutcomeStatus.Produced);
        }

        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var completed = await assertionDb.CentralArtifacts.SingleAsync(item => item.Id == outputArtifactId)
            .ConfigureAwait(false);
        completed.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        completed.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        (await assertionDb.CentralArtifactProcessingEvidence.CountAsync(item =>
            item.CentralDerivativeJobId == jobId).ConfigureAwait(false)).Should().Be(1);
        var attempts = await assertionDb.CentralDerivativeJobAttempts.Where(item =>
                item.CentralDerivativeJobId == jobId)
            .OrderBy(item => item.AttemptNumber)
            .ToListAsync().ConfigureAwait(false);
        attempts.Select(item => item.Outcome).Should().Equal(
            CentralDerivativeAttemptOutcome.LeaseExpired,
            CentralDerivativeAttemptOutcome.Completed);
    }

    [TestMethod]
    public async Task ConcurrentDerivativeCompletions_ConvergeWithoutSqlDeadlocks()
    {
        const int jobCount = 8;
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("concurrent-derivative-completion-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var artifactIds = new Guid[jobCount];
        for (var index = 0; index < jobCount; index++)
        {
            var payload = new byte[] { 2, 4, 6, (byte)(8 + index) };
            var manifest = CreateManifestV2(deviceId, rig, payload, captureSequence: 200 + index);
            artifactIds[index] = manifest.Descriptor.Artifact.ArtifactId;
            using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        Guid[] jobIds;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sourceIds = await db.CentralArtifacts
                .Where(artifact => artifactIds.Contains(artifact.ArtifactId))
                .Select(artifact => artifact.Id)
                .ToArrayAsync().ConfigureAwait(false);
            sourceIds.Should().HaveCount(jobCount);
            await db.CentralDerivativeJobs.Where(job => !sourceIds.Contains(job.SourceCentralArtifactId)
                    || job.RecipeName != BuiltInProcessingRecipes.ImageQuality)
                .Where(job => job.Status == CentralDerivativeJobStatus.Pending
                    || job.Status == CentralDerivativeJobStatus.RetryableFailure)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
            jobIds = await db.CentralDerivativeJobs
                .Where(job => sourceIds.Contains(job.SourceCentralArtifactId)
                    && job.RecipeName == BuiltInProcessingRecipes.ImageQuality)
                .Select(job => job.Id)
                .ToArrayAsync().ConfigureAwait(false);
            jobIds.Should().HaveCount(jobCount);
        }

        var leases = new List<CentralDerivativeJobLease>();
        for (var index = 0; index < jobCount; index++)
        {
            await using var claimScope = fixture.Factory.Services.CreateAsyncScope();
            var lease = await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("concurrent-completion-worker", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false);
            lease.Should().NotBeNull();
            jobIds.Should().Contain(lease!.JobId);
            leases.Add(lease);
        }
        leases.Select(lease => lease.JobId).Should().BeEquivalentTo(jobIds);

        var completionBarrier = new CompletionUpdateBarrier(jobCount);
        using var completionFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlServer(fixture.SqlServerConnectionString);
                options.AddInterceptors(new CompletionUpdateBarrierInterceptor(completionBarrier));
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            });
        }));
        _ = completionFactory.Services;
        var completedJobIds = new ConcurrentBag<Guid>();
        await Parallel.ForEachAsync(
            leases,
            new ParallelOptions { MaxDegreeOfParallelism = jobCount },
            async (lease, cancellationToken) =>
            {
                await using var scope = completionFactory.Services.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                    .ExecuteAsync(lease, cancellationToken).ConfigureAwait(false);
                result.Status.Should().Be(ProcessingOutcomeStatus.Produced);
                completedJobIds.Add(lease.JobId);
            }).ConfigureAwait(false);

        completedJobIds.Should().BeEquivalentTo(jobIds);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await assertionDb.CentralDerivativeJobs.CountAsync(job =>
            jobIds.Contains(job.Id) && job.Status == CentralDerivativeJobStatus.Completed).ConfigureAwait(false))
            .Should().Be(jobCount);
        (await assertionDb.CentralDerivativeJobAttempts.CountAsync(attempt =>
            jobIds.Contains(attempt.CentralDerivativeJobId)
            && attempt.Outcome == CentralDerivativeAttemptOutcome.Completed).ConfigureAwait(false))
            .Should().Be(jobCount);
    }

    [TestMethod]
    public async Task MultipartIngestV2_WithMissingHistoricalRig_IsDurableAndRetryable()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, CreateRig("missing-rig"), payload, captureSequence: 11);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);

        ((int)response.StatusCode).Should().Be(425);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.SingleAsync(item => item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
        artifact.StateReasonCode.Should().Be("profile.rig-not-found");
        var storageReference = artifact.StorageReference;
        var objectKey = storageReference["minio://skymonitor-artifacts/".Length..];
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        await minio.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket("skymonitor-artifacts").WithObject(objectKey)).ConfigureAwait(false);
        artifact.ObjectState = CentralArtifactObjectState.Pending;
        artifact.ReconciledAtUtc = null;
        await db.SaveChangesAsync().ConfigureAwait(false);
        using (var telemetry = new CentralIngestTelemetry())
        {
            var reconciler = new CentralArtifactReconciliationService(
                fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                telemetry,
                NullLogger<CentralArtifactReconciliationService>.Instance);
            await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        }
        db.ChangeTracker.Clear();
        var missingArtifact = await db.CentralArtifacts.SingleAsync(item => item.IdempotencyKey == manifest.IdempotencyKey).ConfigureAwait(false);
        missingArtifact.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
        missingArtifact.StateReasonCode.Should().Be("object.missing");
        using var retry = await PostAsync(client, manifest, payload).ConfigureAwait(false);

        ((int)retry.StatusCode).Should().Be(425);
        await using var retryScope = fixture.Factory.Services.CreateAsyncScope();
        var retryDb = retryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await retryDb.CentralArtifacts.SingleAsync(item => item.IdempotencyKey == manifest.IdempotencyKey).ConfigureAwait(false))
            .ObjectState.Should().Be(CentralArtifactObjectState.Available);
        var retryMinio = retryScope.ServiceProvider.GetRequiredService<IMinioClient>();
        (await retryMinio.StatObjectAsync(new StatObjectArgs()
            .WithBucket("skymonitor-artifacts").WithObject(objectKey)).ConfigureAwait(false)).Size.Should().Be(payload.LongLength);
    }

    [TestMethod]
    public async Task IngestStatus_PendingReferenceAvoidsPayloadRetryAndEventuallyAcknowledges()
    {
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("status-preflight-rig");
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, 34);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        var token = await GetSystemTokenAsync(client).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var upload = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        ((int)upload.StatusCode).Should().Be(425);
        var minioCounter = new MinioGetCountingHandler { InnerHandler = new SocketsHttpHandler() };
        using var statusFactory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(AssemblyHooks.Fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(minioCounter, disposeHandler: false), disposeHttpClient: true)
                .Build());
        }));
        using var statusClient = statusFactory.CreateClient();
        statusClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var pendingStatus = await PostStatusAsync(statusClient, manifest).ConfigureAwait(false);
        using var repeatedPendingStatus = await PostStatusAsync(statusClient, manifest).ConfigureAwait(false);

        ((int)pendingStatus.StatusCode).Should().Be(425);
        ((int)repeatedPendingStatus.StatusCode).Should().Be(425);
        minioCounter.GetRequests.Should().Be(0, "unresolved status checks must remain metadata-only");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        using var acceptedStatus = await PostStatusAsync(statusClient, manifest).ConfigureAwait(false);
        acceptedStatus.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var acknowledgement = await acceptedStatus.Content.ReadFromJsonAsync<ArtifactUploadAcknowledgement>().ConfigureAwait(false);
        acknowledgement!.ArtifactId.Should().Be(manifest.Descriptor.Artifact.ArtifactId);
        acknowledgement.ChecksumSha256.Should().Be(manifest.Descriptor.Artifact.ChecksumSha256);
        minioCounter.GetRequests.Should().Be(1, "the transition to acknowledgement must verify the full object exactly once");
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("checksum")]
    [DataRow("truncated")]
    public async Task IngestStatus_BackgroundResolvedReferenceDoesNotAcknowledgeMissingOrCorruptObject(string failure)
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig($"status-{failure}-rig");
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, failure switch
        {
            "missing" => 41,
            "checksum" => 42,
            "truncated" => 44,
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        });
        using var client = fixture.Factory.CreateClient();
        var token = await GetSystemTokenAsync(client).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var upload = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        ((int)upload.StatusCode).Should().Be(425);
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        using (var telemetry = new CentralIngestTelemetry())
        {
            var reconciler = new CentralArtifactReconciliationService(
                fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                telemetry,
                NullLogger<CentralArtifactReconciliationService>.Instance);
            await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.SingleAsync(item =>
                item.Frame!.RegistrationId == registrationId
                && item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
            artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
            var objectKey = artifact.StorageReference["minio://skymonitor-artifacts/".Length..];
            var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
            if (failure != "missing")
            {
                var corruptPayload = failure == "checksum"
                    ? new byte[] { 4, 3, 2, 1 }
                    : new byte[] { 1, 2, 3 };
                await minio.PutObjectAsync(new PutObjectArgs()
                    .WithBucket("skymonitor-artifacts")
                    .WithObject(objectKey)
                    .WithStreamData(new MemoryStream(corruptPayload))
                    .WithObjectSize(corruptPayload.LongLength)).ConfigureAwait(false);
            }
            else
            {
                await minio.RemoveObjectAsync(new RemoveObjectArgs()
                    .WithBucket("skymonitor-artifacts")
                    .WithObject(objectKey)).ConfigureAwait(false);
            }
        }
        var minioCounter = new MinioGetCountingHandler { InnerHandler = new SocketsHttpHandler() };
        using var statusFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(minioCounter, disposeHandler: false), disposeHttpClient: true)
                .Build());
        }));
        using var statusClient = statusFactory.CreateClient();
        statusClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var status = await PostStatusAsync(statusClient, manifest).ConfigureAwait(false);

        status.StatusCode.Should().Be(HttpStatusCode.NotFound);
        minioCounter.GetRequests.Should().Be(failure == "checksum" ? 1 : 0,
            "missing and truncated objects fail prerequisite stat checks, while same-length corruption is streamed once");
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var quarantined = await assertionDb.CentralArtifacts.SingleAsync(item =>
            item.Frame!.RegistrationId == registrationId
            && item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
        quarantined.ObjectState.Should().Be(CentralArtifactObjectState.Quarantined);
        quarantined.ReconstructionState.Should().Be(CentralReconstructionState.Quarantined);
        quarantined.StateReasonCode.Should().Be(failure switch
        {
            "missing" => "object.missing",
            "checksum" => "object.checksum-mismatch",
            "truncated" => "object.length-mismatch",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        });
    }

    [TestMethod]
    public async Task Reconciliation_AdoptsStrandedObjectVerificationAndPreservesExactJobs()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("stranded-verification-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, 43);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var accepted = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);

        Guid centralArtifactId;
        Guid[] initialJobIds;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.SingleAsync(item =>
                item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
            centralArtifactId = artifact.Id;
            artifact.ObjectState = CentralArtifactObjectState.Pending;
            artifact.ObjectVerificationToken = Guid.NewGuid();
            artifact.ObjectVerificationRequestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync().ConfigureAwait(false);
            initialJobIds = await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.SourceCentralArtifactId == centralArtifactId)
                .OrderBy(job => job.Id)
                .Select(job => job.Id)
                .ToArrayAsync().ConfigureAwait(false);
            initialJobIds.Should().NotBeEmpty();
        }

        using (var telemetry = new CentralIngestTelemetry())
        {
            var reconciler = new CentralArtifactReconciliationService(
                fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                telemetry,
                NullLogger<CentralArtifactReconciliationService>.Instance);
            _ = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reconciled = await assertionDb.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == centralArtifactId).ConfigureAwait(false);
        reconciled.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        reconciled.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        reconciled.ObjectVerificationToken.Should().BeNull();
        reconciled.ObjectVerificationRequestedAtUtc.Should().BeNull();
        reconciled.ObjectVerifiedAtUtc.Should().NotBeNull();
        var finalJobIds = await assertionDb.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.SourceCentralArtifactId == centralArtifactId)
            .OrderBy(job => job.Id)
            .Select(job => job.Id)
            .ToArrayAsync().ConfigureAwait(false);
        finalJobIds.Should().Equal(initialJobIds);
    }

    [TestMethod]
    public async Task IngestStatus_StreamFailureLeavesDurableVerificationForTruthfulRetry()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("verification-stream-failure-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, 45);
        using var client = fixture.Factory.CreateClient();
        var token = await GetSystemTokenAsync(client).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var accepted = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var failureHandler = new ExistingObjectGetFailureHandler { InnerHandler = new SocketsHttpHandler() };
        using var failureFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(failureHandler, disposeHandler: false), disposeHttpClient: true)
                .Build());
        }));
        using var failureClient = failureFactory.CreateClient();
        failureClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var failedStatus = await PostStatusAsync(failureClient, manifest).ConfigureAwait(false);
        failedStatus.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        failureHandler.Failures.Should().Be(1);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var artifact = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralArtifacts.AsNoTracking()
                .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
                .ConfigureAwait(false);
            artifact.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
            artifact.ObjectVerificationToken.Should().NotBeNull();
            artifact.ObjectVerificationRequestedAtUtc.Should().NotBeNull();
        }

        using var retry = await PostStatusAsync(client, manifest).ConfigureAwait(false);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var reconciled = await assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        reconciled.ObjectVerificationToken.Should().BeNull();
        reconciled.ObjectVerificationRequestedAtUtc.Should().BeNull();
        reconciled.ObjectVerifiedAtUtc.Should().NotBeNull();
    }

    [TestMethod]
    public async Task MultipartCrossSchema_StreamFailureRetriesReconstructionWithoutDuplicateLineage()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("cross-schema-stream-retry-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var source = CreateManifestV2(deviceId, rig, payload, 46);
        var current = CreateManifestV2(
            deviceId,
            rig,
            payload,
            47,
            role: FrameArtifactRole.Preview,
            sourceArtifactIds: [source.Descriptor.Artifact.ArtifactId]);
        var descriptor = current.Descriptor;
        var compatibility = new ArtifactUploadManifest(
            "v1", deviceId, descriptor.Artifact.ArtifactId, descriptor.Capture.CaptureId,
            descriptor.Artifact.Role, descriptor.Artifact.MediaType, descriptor.Layout.ByteLength,
            descriptor.Artifact.ChecksumSha256, descriptor.Timing.ExposureStartedUtc,
            $"v1-{current.IdempotencyKey}", current.RelativeArtifactPath);
        using var client = fixture.Factory.CreateClient();
        var token = await GetSystemTokenAsync(client).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var sourceResponse = await PostAsync(client, source, payload).ConfigureAwait(false);
        using var compatibilityResponse = await PostAsync(client, compatibility).ConfigureAwait(false);
        sourceResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        compatibilityResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var failureHandler = new ExistingObjectGetFailureHandler { InnerHandler = new SocketsHttpHandler() };
        using var failureFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(failureHandler, disposeHandler: false), disposeHttpClient: true)
                .Build());
        }));
        using var failureClient = failureFactory.CreateClient();
        failureClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var failed = await PostAsync(failureClient, current, payload).ConfigureAwait(false);
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        using var retry = await PostAsync(client, current, payload).ConfigureAwait(false);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted, await retry.Content.ReadAsStringAsync().ConfigureAwait(false));
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var artifact = await assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralArtifacts.Include(item => item.Sources)
            .SingleAsync(item => item.ArtifactId == descriptor.Artifact.ArtifactId
                && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ObjectVerificationToken.Should().BeNull();
        artifact.Sources.Should().ContainSingle(sourceItem =>
            sourceItem.SourceArtifactId == source.Descriptor.Artifact.ArtifactId
            && sourceItem.ResolvedCentralArtifactId != null);
    }

    [TestMethod]
    public async Task IngestStatus_StaleObjectGenerationRetriesWithoutFalseAcknowledgement()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("stale-object-generation-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, 48);
        using var client = fixture.Factory.CreateClient();
        var token = await GetSystemTokenAsync(client).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var accepted = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var generation = new OneStaleGenerationState();
        using var statusFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICentralArtifactObjectReader>();
            services.AddScoped<CentralArtifactObjectReader>();
            services.AddScoped<ICentralArtifactObjectReader>(provider => new OneStaleGenerationObjectReader(
                provider.GetRequiredService<CentralArtifactObjectReader>(), generation));
        }));
        using var statusClient = statusFactory.CreateClient();
        statusClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var status = await PostStatusAsync(statusClient, manifest).ConfigureAwait(false);

        status.StatusCode.Should().Be(HttpStatusCode.Accepted);
        generation.Rejections.Should().Be(1);
        generation.Checks.Should().BeGreaterThanOrEqualTo(2);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var artifact = await assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId
                && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ObjectVerificationToken.Should().BeNull();
    }

    [TestMethod]
    public async Task MultipartIngestV2_AfterCompatibilityV1_EnrichesOneArtifact()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("compatibility-rig");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
            db.DeviceRigProfiles.Add(new DeviceRigProfile
            {
                RegistrationId = registration.Id,
                DevicePublicId = registration.DevicePublicId!.Value,
                ObservatoryId = registration.ObservatoryId,
                Version = 1,
                ConfigHash = new string('3', 64),
                ConfigJson = JsonSerializer.Serialize(rig),
                ProfileName = "rig",
                ProfileVersion = rig.ProfileVersion,
                ProfileSha256 = CameraRigProfileIdentity.ComputeSha256(rig),
                CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
                EffectiveFromUtc = DateTimeOffset.UnixEpoch.AddDays(-1)
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        var payload = new byte[] { 1, 2, 3, 4 };
        var current = CreateManifestV2(deviceId, rig, payload, captureSequence: 12);
        var descriptor = current.Descriptor;
        var compatibility = new ArtifactUploadManifest(
            "v1", deviceId, descriptor.Artifact.ArtifactId, descriptor.Capture.CaptureId,
            descriptor.Artifact.Role, descriptor.Artifact.MediaType, descriptor.Layout.ByteLength,
            descriptor.Artifact.ChecksumSha256, descriptor.Timing.ExposureStartedUtc,
            $"v2-{current.IdempotencyKey}", current.RelativeArtifactPath);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        var initialResponses = await Task.WhenAll(
            PostAsync(client, compatibility),
            PostAsync(client, current, payload)).ConfigureAwait(false);
        using var legacyResponse = initialResponses[0];
        using var currentResponse = initialResponses[1];
        using var currentRetry = await PostAsync(client, current, payload).ConfigureAwait(false);

        legacyResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        currentResponse.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            await currentResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        currentRetry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var dbAssertion = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await dbAssertion.CentralArtifacts.Include(item => item.IngestIdentities)
            .SingleAsync(item => item.ArtifactId == descriptor.Artifact.ArtifactId).ConfigureAwait(false);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        artifact.IngestIdentities.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task MultipartIngestV2_RetryAfterCompatibilityV1ObjectLoss_RecoversCanonicalArtifact()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("v1-v2-recovery-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var current = CreateManifestV2(deviceId, rig, payload, 14);
        var compatibility = CreateCompatibilityManifest(current);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var legacyAccepted = await PostAsync(client, compatibility, payloadBytes: payload).ConfigureAwait(false);
        using var aliasAccepted = await PostAsync(client, current, payload).ConfigureAwait(false);
        legacyAccepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        aliasAccepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await RemoveCanonicalObjectAsync(registrationId).ConfigureAwait(false);

        using var detection = await PostAsync(client, current, payload).ConfigureAwait(false);
        using var recovery = await PostAsync(client, current, payload).ConfigureAwait(false);

        detection.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        recovery.StatusCode.Should().Be(HttpStatusCode.Accepted, await recovery.Content.ReadAsStringAsync().ConfigureAwait(false));
        await AssertCanonicalRecoveryAsync(registrationId, payload).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MultipartIngestV1_RetryAfterAuthoritativeV2ObjectLoss_RecoversCanonicalArtifact()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("v2-v1-recovery-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var current = CreateManifestV2(deviceId, rig, payload, 15);
        var compatibility = CreateCompatibilityManifest(current);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var currentAccepted = await PostAsync(client, current, payload).ConfigureAwait(false);
        using var aliasAccepted = await PostAsync(client, compatibility, payloadBytes: payload).ConfigureAwait(false);
        currentAccepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        aliasAccepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await RemoveCanonicalObjectAsync(registrationId).ConfigureAwait(false);

        using var detection = await PostAsync(client, compatibility, payloadBytes: payload).ConfigureAwait(false);
        using var recovery = await PostAsync(client, compatibility, payloadBytes: payload).ConfigureAwait(false);

        detection.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        recovery.StatusCode.Should().Be(HttpStatusCode.Accepted, await recovery.Content.ReadAsStringAsync().ConfigureAwait(false));
        await AssertCanonicalRecoveryAsync(registrationId, payload).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MultipartIngestV1_RecoversCommittedPendingV2IntentFromAnotherHost()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("pending-v2-v1-recovery-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var current = CreateManifestV2(deviceId, rig, payload, 35);
        var compatibility = CreateCompatibilityManifest(current);
        var copyFault = new CopyObjectFaultHandler { InnerHandler = new SocketsHttpHandler() };
        using var faultFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(copyFault, disposeHandler: false), disposeHttpClient: true)
                .Build());
        }));
        using (var faultClient = faultFactory.CreateClient())
        {
            faultClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetSystemTokenAsync(faultClient).ConfigureAwait(false));
            using var failed = await PostAsync(faultClient, current, payload).ConfigureAwait(false);
            failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
        await using (var pendingScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var pendingDb = pendingScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var pending = await pendingDb.CentralArtifacts.Include(item => item.IngestIdentities)
                .SingleAsync(item => item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
            pending.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
            pending.ManifestSchemaVersion.Should().Be(ArtifactManifestV2.CurrentSchemaVersion);
            pending.IngestIdentities.Should().ContainSingle(item => item.ManifestSchemaVersion == ArtifactManifestV2.CurrentSchemaVersion);
        }

        using var recoveryClient = fixture.Factory.CreateClient();
        recoveryClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(recoveryClient).ConfigureAwait(false));
        using var recovery = await PostAsync(recoveryClient, compatibility, payloadBytes: payload).ConfigureAwait(false);

        recovery.StatusCode.Should().Be(HttpStatusCode.Accepted, await recovery.Content.ReadAsStringAsync().ConfigureAwait(false));
        await AssertCanonicalRecoveryAsync(registrationId, payload).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MultipartIngestV2_ReservationBeforeIntentRejectsWithoutPendingOwner()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("retention-reservation-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        byte[] payload = [7, 8, 9, 10];
        var manifest = CreateManifestV2(deviceId, rig, payload, 36);
        var ingestManifest = ArtifactIngestManifest.Create(ArtifactManifestDocument.FromCurrent(manifest));
        Guid devicePublicId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            devicePublicId = (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .DeviceRegistrations.AsNoTracking().SingleAsync(item => item.Id == registrationId)
                .ConfigureAwait(false)).DevicePublicId!.Value;
        }
        var storageReference = ArtifactIngestService.CreateCanonicalStorageReference(devicePublicId, ingestManifest);
        await using var blockerScope = fixture.Factory.Services.CreateAsyncScope();
        var blocker = await CentralObjectApplicationLock.AcquireAsync(
            blockerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            storageReference,
            CancellationToken.None).ConfigureAwait(false);
        Guid tombstoneArtifactId = default;
        Guid tombstoneDispositionId = default;
        Guid tombstoneFrameId = default;
        try
        {
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
            var ingest = PostAsync(client, manifest, payload);
            await Task.Delay(250).ConfigureAwait(false);
            ingest.IsCompleted.Should().BeFalse();

            await using (var tombstoneScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = tombstoneScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var registration = await db.DeviceRegistrations.AsNoTracking()
                    .SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var frame = new CentralFrame
                {
                    RegistrationId = registration.Id,
                    DevicePublicId = registration.DevicePublicId!.Value,
                    ObservatoryId = registration.ObservatoryId,
                    AgentId = $"retention-tombstone-{Guid.NewGuid():N}",
                    FrameId = Guid.NewGuid(),
                    CapturedAtUtc = now,
                    FirstReceivedAtUtc = now
                };
                var operationToken = Guid.NewGuid();
                var tombstone = new CentralArtifact
                {
                    Frame = frame,
                    CentralFrameId = frame.Id,
                    DevicePublicId = frame.DevicePublicId,
                    ArtifactId = Guid.NewGuid(),
                    Role = FrameArtifactRole.Metadata,
                    RecipeVersion = "retention-race-v1",
                    ManifestSchemaVersion = ArtifactUploadManifest.CurrentSchemaVersion,
                    MediaType = "application/octet-stream",
                    ByteLength = 1,
                    ChecksumSha256 = new string('A', 64),
                    StorageReference = storageReference,
                    ReceivedAtUtc = now,
                    IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                    ObjectState = CentralArtifactObjectState.Expired,
                    ReconstructionState = CentralReconstructionState.Complete,
                    RetentionDeletionToken = operationToken,
                    RetentionDeletionRequestedAtUtc = now
                };
                var objectKey = storageReference["minio://skymonitor-artifacts/".Length..];
                var disposition = new CentralObjectRecoveryDisposition
                {
                    SourceObjectIdentitySha256 = CentralObjectOwnershipFence.CreateObjectKeyIdentity(objectKey),
                    SourceObjectKey = objectKey,
                    Kind = CentralObjectRecoveryKinds.ExpiredDelete,
                    State = CentralObjectRecoveryStates.PendingDelete,
                    CentralArtifactId = tombstone.Id,
                    OperationToken = operationToken,
                    ByteLength = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    NextAttemptAtUtc = now
                };
                db.AddRange(frame, tombstone, disposition);
                await db.SaveChangesAsync().ConfigureAwait(false);
                tombstoneFrameId = frame.Id;
                tombstoneArtifactId = tombstone.Id;
                tombstoneDispositionId = disposition.Id;
                (await db.CentralArtifacts.CountAsync(item => item.ArtifactId == ingestManifest.ArtifactId)
                    .ConfigureAwait(false)).Should().Be(0);
            }
            await blocker.DisposeAsync().ConfigureAwait(false);
            using var response = await ingest.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);

            await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
            var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await assertionDb.CentralArtifacts.CountAsync(item =>
                item.ArtifactId == ingestManifest.ArtifactId
                && item.ObjectState == CentralArtifactObjectState.Pending).ConfigureAwait(false)).Should().Be(0);
        }
        finally
        {
            await blocker.DisposeAsync().ConfigureAwait(false);
            if (tombstoneDispositionId != Guid.Empty)
            {
                await using var cleanupScope = fixture.Factory.Services.CreateAsyncScope();
                var db = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.CentralObjectRecoveryDispositions.Where(item => item.Id == tombstoneDispositionId)
                    .ExecuteDeleteAsync().ConfigureAwait(false);
                await db.CentralArtifacts.Where(item => item.Id == tombstoneArtifactId)
                    .ExecuteDeleteAsync().ConfigureAwait(false);
                await db.CentralFrames.Where(item => item.Id == tombstoneFrameId)
                    .ExecuteDeleteAsync().ConfigureAwait(false);
            }
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task MultipartIngestV2_CompletedTombstoneSurvivesArtifactOwnerRemovalAndRejectsReplay(
        bool tokenized)
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("retention-permanent-tombstone-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        byte[] payload = [11, 12, 13, 14];
        var manifest = CreateManifestV2(deviceId, rig, payload, 37);
        var ingestManifest = ArtifactIngestManifest.Create(ArtifactManifestDocument.FromCurrent(manifest));
        Guid dispositionId;
        string storageReference;
        string objectKey;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.AsNoTracking()
                .SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
            storageReference = ArtifactIngestService.CreateCanonicalStorageReference(
                registration.DevicePublicId!.Value, ingestManifest);
            objectKey = storageReference["minio://skymonitor-artifacts/".Length..];
            var now = DateTimeOffset.UtcNow;
            var token = Guid.NewGuid();
            var frame = new CentralFrame
            {
                RegistrationId = registration.Id,
                DevicePublicId = registration.DevicePublicId.Value,
                ObservatoryId = registration.ObservatoryId,
                AgentId = $"retention-deleted-owner-{Guid.NewGuid():N}",
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
                Role = FrameArtifactRole.Metadata,
                RecipeVersion = "retention-tombstone-v1",
                ManifestSchemaVersion = ArtifactUploadManifest.CurrentSchemaVersion,
                MediaType = "application/octet-stream",
                ByteLength = 1,
                ChecksumSha256 = new string('C', 64),
                StorageReference = storageReference,
                ReceivedAtUtc = now,
                IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                ObjectState = CentralArtifactObjectState.Expired,
                ReconstructionState = CentralReconstructionState.Complete,
                RetentionDeletionToken = tokenized ? token : null,
                RetentionDeletionRequestedAtUtc = tokenized ? now : null,
                RetentionDeletionCompletedAtUtc = tokenized ? now : null
            };
            var disposition = new CentralObjectRecoveryDisposition
            {
                SourceObjectIdentitySha256 = CentralObjectOwnershipFence.CreateObjectKeyIdentity(objectKey),
                SourceObjectKey = objectKey,
                Kind = CentralObjectRecoveryKinds.ExpiredDelete,
                State = CentralObjectRecoveryStates.Completed,
                CentralArtifactId = tokenized ? artifact.Id : null,
                OperationToken = tokenized ? token : null,
                ByteLength = 1,
                AttemptCount = 1,
                LastAttemptAtUtc = now,
                CompletedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.AddRange(frame, artifact, disposition);
            await db.SaveChangesAsync().ConfigureAwait(false);
            dispositionId = disposition.Id;
            await db.CentralArtifacts.Where(item => item.Id == artifact.Id).ExecuteDeleteAsync().ConfigureAwait(false);
            await db.CentralFrames.Where(item => item.Id == frame.Id).ExecuteDeleteAsync().ConfigureAwait(false);
            if (!tokenized)
            {
                disposition.State = CentralObjectRecoveryStates.PendingDelete;
                await db.SaveChangesAsync().ConfigureAwait(false);
                (await CentralObjectOwnershipFence.IsRetiredAsync(
                    db, storageReference, CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
                disposition.State = CentralObjectRecoveryStates.Cancelled;
                await db.SaveChangesAsync().ConfigureAwait(false);
                (await CentralObjectOwnershipFence.IsRetiredAsync(
                    db, storageReference, CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
                disposition.State = CentralObjectRecoveryStates.Completed;
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
            (await CentralObjectOwnershipFence.IsRetiredAsync(
                db, storageReference, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
        }
        try
        {
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
            using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);

            var stat = () => fixture.Factory.Services.GetRequiredService<IMinioClient>()
                .StatObjectAsync(new StatObjectArgs().WithBucket("skymonitor-artifacts").WithObject(objectKey));
            await stat.Should().ThrowAsync<MinioException>().ConfigureAwait(false);
            await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
            var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await assertionDb.CentralObjectRecoveryDispositions.AsNoTracking()
                .AnyAsync(item => item.Id == dispositionId
                    && item.State == CentralObjectRecoveryStates.Completed).ConfigureAwait(false)).Should().BeTrue();
            (await assertionDb.CentralArtifacts.AnyAsync(item =>
                EF.Functions.Collate(item.StorageReference, CentralObjectOwnershipFence.BinaryCollation)
                    == storageReference).ConfigureAwait(false)).Should().BeFalse();
        }
        finally
        {
            await using var cleanupScope = fixture.Factory.Services.CreateAsyncScope();
            await cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralObjectRecoveryDispositions.Where(item => item.Id == dispositionId)
                .ExecuteDeleteAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task MultipartIngestV2_ConcurrentCorruptPayloadCannotPoisonValidIntent()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var corruptPayload = new byte[] { 4, 3, 2, 1 };
        var manifest = CreateManifestV2(deviceId, CreateRig("concurrent-rig"), payload, captureSequence: 13);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        var responses = await Task.WhenAll(
            PostAsync(client, manifest, payload),
            PostAsync(client, manifest, corruptPayload)).ConfigureAwait(false);
        using var validResponse = responses[0];
        using var corruptResponse = responses[1];

        ((int)validResponse.StatusCode).Should().Be(
            425,
            await validResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        corruptResponse.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            await corruptResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.SingleAsync(item => item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
    }

    [TestMethod]
    public async Task HistoryQuery_ReturnsIngestedArtifactByRole()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, _) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var manifest = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Preview,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "preview-v1", "frames/preview.bin");
        using var ingest = await PostAsync(client, manifest).ConfigureAwait(false);
        ingest.StatusCode.Should().Be(HttpStatusCode.Accepted);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetUserTokenAsync(client).ConfigureAwait(false));

        using var history = await client.GetAsync(new Uri($"/api/v1.0/artifacts?agentId={deviceId}&role=preview", UriKind.Relative)).ConfigureAwait(false);

        history.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await history.Content.ReadAsStringAsync().ConfigureAwait(false);
        body.Should().Contain(manifest.ArtifactId.ToString());
    }

    [TestMethod]
    public async Task MultipartIngest_WithWrongChecksum_IsRejectedWithoutMetadata()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var manifest = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 4, new string('A', 64), DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");

        using var response = await PostAsync(client, manifest).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.CentralFrames.AnyAsync(item => item.RegistrationId == registrationId).ConfigureAwait(false)).Should().BeFalse();
        (await db.CentralArtifacts.AnyAsync(item => item.Frame!.RegistrationId == registrationId).ConfigureAwait(false)).Should().BeFalse();
    }

    [TestMethod]
    public async Task MultipartIngest_WhenIdempotencyKeyHasDifferentArtifact_IsConflict()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, _) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var frameId = Guid.NewGuid();
        var first = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");
        var conflict = first with { ArtifactId = Guid.NewGuid() };
        using var accepted = await PostAsync(client, first).ConfigureAwait(false);

        using var response = await PostAsync(client, conflict).ConfigureAwait(false);

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [TestMethod]
    public async Task MultipartIngest_WhenArtifactIdIsReusedAcrossFramesForDevice_IsConflict()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var artifactId = Guid.NewGuid();
        var first = new ArtifactUploadManifest("v1", deviceId, artifactId, Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw-a.bin");
        var duplicate = first with
        {
            FrameId = Guid.NewGuid(),
            RelativeArtifactPath = "frames/raw-b.bin"
        };

        using var accepted = await PostAsync(client, first).ConfigureAwait(false);
        using var response = await PostAsync(client, duplicate).ConfigureAwait(false);

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.CentralArtifacts.CountAsync(artifact => artifact.Frame!.RegistrationId == registrationId)
            .ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task MultipartIngestV2_WhenCaptureSequenceIsReusedAcrossFramesForDevice_IsConflict()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("capture-sequence-conflict-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var first = CreateManifestV2(deviceId, rig, payload, 43);
        var conflict = CreateManifestV2(deviceId, rig, payload, 43);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var accepted = await PostAsync(client, first, payload).ConfigureAwait(false);
        using var response = await PostAsync(client, conflict, payload).ConfigureAwait(false);

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.CentralFrames.CountAsync(frame => frame.RegistrationId == registrationId).ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task MultipartIngest_SameArtifactIdAcrossDevices_IsAcceptedAndIsolated()
    {
        var fixture = AssemblyHooks.Fixture;
        var (firstDeviceId, firstRegistrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var (secondDeviceId, secondRegistrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var artifactId = Guid.NewGuid();
        var first = new ArtifactUploadManifest("v1", firstDeviceId, artifactId, Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");
        var second = new ArtifactUploadManifest("v1", secondDeviceId, artifactId, Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var firstResponse = await PostAsync(client, first).ConfigureAwait(false);
        using var secondResponse = await PostAsync(client, second).ConfigureAwait(false);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifacts = await db.CentralArtifacts.Include(item => item.Frame)
            .Where(item => item.ArtifactId == artifactId).ToListAsync().ConfigureAwait(false);
        artifacts.Should().HaveCount(2);
        artifacts.Select(item => item.Frame!.RegistrationId).Should().BeEquivalentTo([firstRegistrationId, secondRegistrationId]);
        artifacts.Select(item => item.StorageReference).Should().OnlyHaveUniqueItems();
        artifacts.Should().OnlyContain(item => item.StorageReference.StartsWith(
            $"minio://skymonitor-artifacts/artifacts/{item.Frame!.DevicePublicId:N}/", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MultipartIngest_AgentNamedStaging_UsesCleanupIsolatedFinalNamespace()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync("staging").ConfigureAwait(false);
        var manifest = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var response = await PostAsync(client, manifest).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.Include(item => item.Frame)
            .SingleAsync(item => item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        artifact.StorageReference.Should().StartWith(
            $"minio://skymonitor-artifacts/artifacts/{artifact.Frame!.DevicePublicId:N}/");
        var objectKey = artifact.StorageReference["minio://skymonitor-artifacts/".Length..];
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddHours(1));
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            clock,
            telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);

        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        (await minio.StatObjectAsync(new StatObjectArgs()
            .WithBucket("skymonitor-artifacts").WithObject(objectKey)).ConfigureAwait(false)).Size.Should().Be(4);
    }

    [TestMethod]
    public async Task MultipartIngestV2_WithMissingLineageSource_IsPendingReference()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("missing-lineage-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var missingSourceId = Guid.NewGuid();
        var manifest = CreateManifestV2(
            deviceId, rig, payload, 20, role: FrameArtifactRole.Preview, sourceArtifactIds: [missingSourceId]);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);

        ((int)response.StatusCode).Should().Be(425);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.Include(item => item.Sources)
            .SingleAsync(item => item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
        artifact.StateReasonCode.Should().Be("lineage.source-not-found");
        artifact.Sources.Should().ContainSingle(source =>
            source.SourceArtifactId == missingSourceId && source.ResolvedCentralArtifactId == null);
    }

    [TestMethod]
    public async Task MultipartIngestV2_DelayedLineageSource_ConvergesAfterSourceArrives()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("delayed-lineage-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var source = CreateManifestV2(deviceId, rig, payload, 21);
        var derivative = CreateManifestV2(
            deviceId,
            rig,
            payload,
            22,
            role: FrameArtifactRole.Preview,
            sourceArtifactIds: [source.Descriptor.Artifact.ArtifactId]);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var pending = await PostAsync(client, derivative, payload).ConfigureAwait(false);
        using var sourceResponse = await PostAsync(client, source, payload).ConfigureAwait(false);
        using var retry = await PostAsync(client, derivative, payload).ConfigureAwait(false);

        ((int)pending.StatusCode).Should().Be(425);
        sourceResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var derivativeArtifact = await db.CentralArtifacts.Include(item => item.Sources)
            .SingleAsync(item => item.ArtifactId == derivative.Descriptor.Artifact.ArtifactId
                && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        var sourceArtifact = await db.CentralArtifacts.SingleAsync(item =>
            item.ArtifactId == source.Descriptor.Artifact.ArtifactId
            && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        derivativeArtifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        derivativeArtifact.StateReasonCode.Should().BeNull();
        derivativeArtifact.Sources.Should().ContainSingle(item => item.ResolvedCentralArtifactId == sourceArtifact.Id);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MultipartIngestV2_UnavailableLineageSource_DoesNotBindUntilSourceCompletes(bool quarantined)
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig(quarantined ? "quarantined-source-rig" : "pending-source-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var source = CreateManifestV2(deviceId, rig, payload, quarantined ? 31 : 29);
        var derivative = CreateManifestV2(
            deviceId,
            rig,
            payload,
            quarantined ? 32 : 30,
            role: FrameArtifactRole.Preview,
            sourceArtifactIds: [source.Descriptor.Artifact.ArtifactId]);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var sourceResponse = await PostAsync(client, source, payload).ConfigureAwait(false);
        sourceResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sourceArtifact = await db.CentralArtifacts.SingleAsync(item =>
                item.ArtifactId == source.Descriptor.Artifact.ArtifactId
                && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
            sourceArtifact.ObjectState = quarantined
                ? CentralArtifactObjectState.Quarantined
                : CentralArtifactObjectState.Pending;
            sourceArtifact.ReconstructionState = quarantined
                ? CentralReconstructionState.Quarantined
                : CentralReconstructionState.Complete;
            sourceArtifact.StateReasonCode = quarantined ? "object.test-fault" : "object.test-pending";
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using var derivativeResponse = await PostAsync(client, derivative, payload).ConfigureAwait(false);

        ((int)derivativeResponse.StatusCode).Should().Be(425);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var derivativeArtifact = await db.CentralArtifacts.Include(item => item.Sources).SingleAsync(item =>
                item.ArtifactId == derivative.Descriptor.Artifact.ArtifactId
                && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
            derivativeArtifact.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
            derivativeArtifact.Sources.Should().ContainSingle(item => item.ResolvedCentralArtifactId == null);
            var sourceArtifact = await db.CentralArtifacts.SingleAsync(item =>
                item.ArtifactId == source.Descriptor.Artifact.ArtifactId
                && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
            sourceArtifact.ObjectState = CentralArtifactObjectState.Available;
            sourceArtifact.ReconstructionState = CentralReconstructionState.Complete;
            sourceArtifact.StateReasonCode = null;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);

        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var converged = await assertionDb.CentralArtifacts.Include(item => item.Sources).SingleAsync(item =>
            item.ArtifactId == derivative.Descriptor.Artifact.ArtifactId
            && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        converged.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        converged.Sources.Should().ContainSingle(item => item.ResolvedCentralArtifactId != null);
    }

    [TestMethod]
    public async Task MultipartIngestV2_QuarantinedSourceInvalidatesAndRecoveryReconvergesDependent()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("reverse-quarantine-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var source = CreateManifestV2(deviceId, rig, payload, 36);
        var derivative = CreateManifestV2(
            deviceId, rig, payload, 37, role: FrameArtifactRole.Preview,
            sourceArtifactIds: [source.Descriptor.Artifact.ArtifactId]);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var sourceAccepted = await PostAsync(client, source, payload).ConfigureAwait(false);
        using var derivativeAccepted = await PostAsync(client, derivative, payload).ConfigureAwait(false);
        sourceAccepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        derivativeAccepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await RemoveArtifactObjectAsync(registrationId, source.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);

        using var quarantine = await PostAsync(client, source, payload).ConfigureAwait(false);

        quarantine.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using (var quarantineScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = quarantineScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dependent = await db.CentralArtifacts.Include(item => item.Sources).SingleAsync(item =>
                item.ArtifactId == derivative.Descriptor.Artifact.ArtifactId
                && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
            dependent.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
            dependent.StateReasonCode.Should().Be("lineage.source-unavailable");
            dependent.Sources.Should().ContainSingle(item => item.ResolvedCentralArtifactId == null);
            var sourceArtifact = await db.CentralArtifacts.SingleAsync(item =>
                item.ArtifactId == source.Descriptor.Artifact.ArtifactId
                && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
            var suspendedJobs = await db.CentralDerivativeJobs
                .Where(job => job.SourceCentralArtifactId == sourceArtifact.Id)
                .ToListAsync().ConfigureAwait(false);
            suspendedJobs.Should().HaveCount(4);
            suspendedJobs.Where(job => job.RecipeName != BuiltInProcessingRecipes.RollingMean)
                .Should().OnlyContain(job => job.Status == CentralDerivativeJobStatus.RetryableFailure
                    && job.AvailableAtUtc == null
                    && job.LeaseToken == null
                    && job.LastError == CentralDerivativeJobScheduler.SourceInvalidatedReason);
            suspendedJobs.Single(job => job.RecipeName == BuiltInProcessingRecipes.RollingMean).Status
                .Should().Be(CentralDerivativeJobStatus.Waiting);
        }

        using var recovery = await PostAsync(client, source, payload).ConfigureAwait(false);

        recovery.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var recoveryScope = fixture.Factory.Services.CreateAsyncScope();
        var recoveryDb = recoveryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var recoveredDependent = await recoveryDb.CentralArtifacts.Include(item => item.Sources).SingleAsync(item =>
            item.ArtifactId == derivative.Descriptor.Artifact.ArtifactId
            && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        recoveredDependent.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        recoveredDependent.StateReasonCode.Should().BeNull();
        recoveredDependent.Sources.Should().ContainSingle(item => item.ResolvedCentralArtifactId != null);
        var recoveredSource = await recoveryDb.CentralArtifacts.SingleAsync(item =>
            item.ArtifactId == source.Descriptor.Artifact.ArtifactId
            && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        var recoveredJobs = await recoveryDb.CentralDerivativeJobs.Where(job =>
            job.SourceCentralArtifactId == recoveredSource.Id).ToListAsync().ConfigureAwait(false);
        recoveredJobs.Where(job => job.RecipeName != BuiltInProcessingRecipes.RollingMean)
            .Should().OnlyContain(job => job.Status == CentralDerivativeJobStatus.Pending && job.AvailableAtUtc != null);
        recoveredJobs.Single(job => job.RecipeName == BuiltInProcessingRecipes.RollingMean).Status
            .Should().Be(CentralDerivativeJobStatus.Waiting);
    }

    [TestMethod]
    public async Task Reconciliation_PendingSourceInvalidatesPreviouslyCompleteDependent()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("reverse-pending-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var source = CreateManifestV2(deviceId, rig, payload, 38);
        var derivative = CreateManifestV2(
            deviceId, rig, payload, 39, role: FrameArtifactRole.Preview,
            sourceArtifactIds: [source.Descriptor.Artifact.ArtifactId]);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var sourceAccepted = await PostAsync(client, source, payload).ConfigureAwait(false);
        using var derivativeAccepted = await PostAsync(client, derivative, payload).ConfigureAwait(false);
        sourceAccepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        derivativeAccepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await RemoveArtifactObjectAsync(registrationId, source.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
        await using (var transitionScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = transitionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sourceArtifact = await db.CentralArtifacts.SingleAsync(item =>
                item.ArtifactId == source.Descriptor.Artifact.ArtifactId
                && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
            sourceArtifact.ObjectState = CentralArtifactObjectState.Pending;
            sourceArtifact.StateReasonCode = "object.test-pending";
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);

        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dependent = await assertionDb.CentralArtifacts.Include(item => item.Sources).SingleAsync(item =>
            item.ArtifactId == derivative.Descriptor.Artifact.ArtifactId
            && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        dependent.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
        dependent.StateReasonCode.Should().Be("lineage.source-unavailable");
        dependent.Sources.Should().ContainSingle(item => item.ResolvedCentralArtifactId == null);
        (await assertionDb.CentralDerivativeJobs.AnyAsync(job => job.SourceCentralArtifactId == dependent.Id)
            .ConfigureAwait(false)).Should().BeFalse();
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Reconciliation_ConcurrentIngestScheduling_ConvergesWithoutErrorDuplicateOrStrandedWork()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("reconciliation-concurrency-rig");
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, 43);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var pending = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        ((int)pending.StatusCode).Should().Be(425);
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var injection = new SchedulerConcurrencyInjection(manifest.Descriptor.Artifact.ArtifactId, 1);
        using var concurrencyFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICentralDerivativeJobScheduler>();
            services.AddScoped<CentralDerivativeJobScheduler>();
            services.AddScoped<ICentralDerivativeJobScheduler>(provider => new ConcurrencyInjectingScheduler(
                provider.GetRequiredService<CentralDerivativeJobScheduler>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ApplicationDbContext>(),
                injection));
        }));
        using var metrics = new ReconciliationConcurrencyMetricCollector();
        using var telemetry = new CentralIngestTelemetry();
        var logger = new RecordingLogger<CentralArtifactReconciliationService>();
        var reconciler = new CentralArtifactReconciliationService(
            concurrencyFactory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            telemetry,
            logger);

        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        injection.InjectionCount.Should().Be(1);
        injection.ScopeContextIds.Should().HaveCount(2);
        metrics.Outcomes.Should().BeEquivalentTo(["retry", "converged"]);
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("will retry with fresh state", StringComparison.Ordinal));
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("converged", StringComparison.Ordinal));
        await using var assertionScope = concurrencyFactory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await assertionDb.CentralArtifacts
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Control)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Profiles)
            .Include(item => item.Sources)
            .Include(item => item.Layout)
            .Include(item => item.Recipe)
            .AsSplitQuery()
            .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId
                && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        artifact.StateReasonCode.Should().BeNull();
        artifact.Sources.Should().BeEmpty();
        var jobs = await assertionDb.CentralDerivativeJobs
            .Include(job => job.InputRequirements)
            .Include(job => job.Inputs)
            .Where(job => job.SourceCentralArtifactId == artifact.Id)
            .ToListAsync().ConfigureAwait(false);
        var recipes = assertionScope.ServiceProvider.GetRequiredService<ICentralDerivativeRecipeCatalog>()
            .GetRequiredRecipes(FrameArtifactRole.Raw)
            .Where(recipe => recipe.RecipeName != BuiltInProcessingRecipes.CloudAssessment)
            .ToArray();
        jobs.Should().HaveCount(recipes.Length);
        jobs.Select(job => job.RequestIdentitySha256).Should().OnlyHaveUniqueItems();
        foreach (var recipe in recipes)
        {
            var expectedRequestIdentity = CentralDerivativeJobIdentity.CreateRequestIdentity(
                artifact.Frame!.DevicePublicId, artifact.ArtifactId, recipe);
            var job = jobs.Single(candidate => candidate.RequestIdentitySha256 == expectedRequestIdentity);
            job.SourceCentralArtifactId.Should().Be(artifact.Id);
            job.TargetRole.Should().Be(recipe.TargetRole);
            job.TargetRecipeVersion.Should().Be(recipe.RecipeVersion);
            job.TargetVariant.Should().Be(recipe.TargetVariant);
            job.RecipeName.Should().Be(recipe.RecipeName);
            job.RecipeOptionsJson.Should().Be(CaptureContractJson.Canonicalize(recipe.Options).GetRawText());
            job.InputSelectorJson.Should().Be(CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(recipe.InputSelector)).GetRawText());
            job.RequestedRecipeIdentitySha256.Should().Be(recipe.RequestedRecipeIdentitySha256);
            var expectedPositions = recipe.Window?.Positions
                ?? [new CentralDerivativeWindowPosition(0, IsRequired: true, recipe.InputSelector)];
            var requirements = job.InputRequirements.OrderBy(requirement => requirement.Ordinal).ToArray();
            requirements.Should().HaveCount(expectedPositions.Count);
            for (var index = 0; index < expectedPositions.Count; index++)
            {
                var position = expectedPositions[index];
                var requirement = requirements[index];
                requirement.Ordinal.Should().Be(index);
                requirement.BindingName.Should().Be("input");
                requirement.SourceKind.Should().Be(CentralDerivativeInputSourceKind.Artifact);
                requirement.SequenceOffset.Should().Be(position.SequenceOffset);
                requirement.IsRequired.Should().Be(position.IsRequired);
                requirement.CompatibilityMode.Should().Be(position.CompatibilityMode);
                requirement.SelectorJson.Should().Be(CaptureContractJson.Canonicalize(
                    CaptureContractJson.SerializeToElement(position.Selector)).GetRawText());
                requirement.ExpectedAgentId.Should().Be(artifact.Frame.AgentId);
                requirement.ExpectedRigId.Should().Be(artifact.Frame.RigId);
                requirement.ExpectedCaptureSequence.Should().Be(artifact.Frame.CaptureSequence + position.SequenceOffset);
                requirement.ResolutionState.Should().Be(recipe.Window is null || position.SequenceOffset == 0
                    ? CentralDerivativeInputResolutionState.Resolved
                    : CentralDerivativeInputResolutionState.Waiting);
            }
            var expectedInputOrdinals = expectedPositions
                .Select((position, index) => (position, index))
                .Where(item => recipe.Window is null || item.position.SequenceOffset == 0)
                .Select(item => item.index);
            job.Inputs.Select(input => input.Ordinal).Should().Equal(expectedInputOrdinals);
            var expectedCompatibility = recipe.Window is null
                ? CentralDerivativeWindowCompatibility.EmptySnapshot
                : CentralDerivativeWindowCompatibility.CreateSnapshot(artifact);
            foreach (var input in job.Inputs)
            {
                input.CentralArtifactId.Should().Be(artifact.Id);
                input.CaptureSequence.Should().Be(artifact.Frame.CaptureSequence);
                input.ByteLength.Should().Be(artifact.ByteLength);
                input.CentralDerivativeJobInputRequirementId.Should().Be(requirements[input.Ordinal].Id);
                input.CompatibilityJson.Should().Be(expectedCompatibility.Json);
                input.CompatibilitySha256.Should().Be(expectedCompatibility.Sha256);
            }
            job.InputSetIdentitySha256.Should().Be(recipe.Window is null
                ? CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs)
                : null);
            if (job.Status == CentralDerivativeJobStatus.Pending)
            {
                job.AvailableAtUtc.Should().NotBeNull();
            }
            else if (job.Status == CentralDerivativeJobStatus.Waiting)
            {
                job.ResolutionDeadlineUtc.Should().NotBeNull();
            }
            else
            {
                job.Status.Should().Be(CentralDerivativeJobStatus.Completed);
                job.ResultCentralArtifactId.Should().NotBeNull();
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Reconciliation_TwoConsecutiveSchedulingConflicts_ConvergesWithFreshState()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("reconciliation-repeated-concurrency-rig");
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, 48);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var pending = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        ((int)pending.StatusCode).Should().Be(425);
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var injection = new SchedulerConcurrencyInjection(
            manifest.Descriptor.Artifact.ArtifactId,
            2,
            commitRequiredJobsOnFinalInjection: true);
        using var concurrencyFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICentralDerivativeJobScheduler>();
            services.AddScoped<CentralDerivativeJobScheduler>();
            services.AddScoped<ICentralDerivativeJobScheduler>(provider => new ConcurrencyInjectingScheduler(
                provider.GetRequiredService<CentralDerivativeJobScheduler>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ApplicationDbContext>(),
                injection));
        }));
        using var metrics = new ReconciliationConcurrencyMetricCollector();
        using var telemetry = new CentralIngestTelemetry();
        var logger = new RecordingLogger<CentralArtifactReconciliationService>();
        var reconciler = new CentralArtifactReconciliationService(
            concurrencyFactory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            telemetry,
            logger);

        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        injection.InjectionCount.Should().Be(2);
        injection.ScopeContextIds.Should().HaveCount(CentralArtifactReconciliationService.MaximumSchedulingAttempts);
        metrics.Outcomes.Count(outcome => outcome == "retry").Should().Be(2);
        metrics.Outcomes.Count(outcome => outcome == "converged").Should().Be(1);
        metrics.Outcomes.Should().NotContain("exhausted");
        metrics.CompletedCount.Should().Be(1);
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
        await using var assertionScope = concurrencyFactory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await assertionDb.CentralArtifacts.SingleAsync(item =>
            item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId
            && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        artifact.ObjectVerificationToken.Should().BeNull();
        artifact.ObjectVerificationRetryAtUtc.Should().BeNull();
        var jobs = await assertionDb.CentralDerivativeJobs
            .Where(job => job.SourceCentralArtifactId == artifact.Id)
            .ToListAsync().ConfigureAwait(false);
        var expectedRecipes = assertionScope.ServiceProvider.GetRequiredService<ICentralDerivativeRecipeCatalog>()
            .GetRequiredRecipes(FrameArtifactRole.Raw)
            .Where(recipe => recipe.RecipeName != BuiltInProcessingRecipes.CloudAssessment)
            .ToArray();
        jobs.Should().HaveCount(expectedRecipes.Length);
        jobs.Select(job => job.RequestIdentitySha256).Should().BeEquivalentTo(expectedRecipes.Select(recipe =>
            CentralDerivativeJobIdentity.CreateRequestIdentity(artifact.DevicePublicId!.Value, artifact.ArtifactId, recipe)));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Reconciliation_RepeatedConcurrency_RemainsAnActionableFailure()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("reconciliation-concurrency-exhausted-rig");
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, 44);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var pending = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        ((int)pending.StatusCode).Should().Be(425);
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var injection = new SchedulerConcurrencyInjection(
            manifest.Descriptor.Artifact.ArtifactId,
            CentralArtifactReconciliationService.MaximumSchedulingAttempts);
        using var concurrencyFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICentralDerivativeJobScheduler>();
            services.AddScoped<CentralDerivativeJobScheduler>();
            services.AddScoped<ICentralDerivativeJobScheduler>(provider => new ConcurrencyInjectingScheduler(
                provider.GetRequiredService<CentralDerivativeJobScheduler>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ApplicationDbContext>(),
                injection));
        }));
        using var metrics = new ReconciliationConcurrencyMetricCollector();
        using var telemetry = new CentralIngestTelemetry();
        var logger = new RecordingLogger<CentralArtifactReconciliationService>();
        var reconciler = new CentralArtifactReconciliationService(
            concurrencyFactory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            telemetry,
            logger);

        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        injection.InjectionCount.Should().Be(CentralArtifactReconciliationService.MaximumSchedulingAttempts);
        injection.ScopeContextIds.Should().HaveCount(CentralArtifactReconciliationService.MaximumSchedulingAttempts);
        metrics.Outcomes.Count(outcome => outcome == "retry").Should().Be(
            CentralArtifactReconciliationService.MaximumSchedulingAttempts - 1);
        metrics.Outcomes.Count(outcome => outcome == "exhausted").Should().Be(1);
        metrics.Outcomes.Should().NotContain("converged");
        metrics.CompletedCount.Should().Be(0);
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error
            && entry.Message == "Central artifact reconciliation failed for one durable record"
            && entry.Exception != null
            && entry.Exception.GetType() == typeof(DbUpdateConcurrencyException));
        await using var assertionScope = concurrencyFactory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await assertionDb.CentralArtifacts.SingleAsync(item =>
            item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId
            && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        artifact.ObjectVerificationToken.Should().NotBeNull();
        artifact.ObjectVerificationRetryAtUtc.Should().NotBeNull();
        (await assertionDb.CentralDerivativeJobs.AnyAsync(job => job.SourceCentralArtifactId == artifact.Id)
            .ConfigureAwait(false)).Should().BeFalse();
        artifact.ReconstructionState = CentralReconstructionState.Quarantined;
        artifact.StateReasonCode = "test.concurrency-exhausted";
        await assertionDb.SaveChangesAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Reconciliation_CancellationPropagatesWithoutConcurrencyRetry()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("reconciliation-cancellation-rig");
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, 45);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var pending = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        ((int)pending.StatusCode).Should().Be(425);
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var injection = new SchedulerFaultInjection(
            manifest.Descriptor.Artifact.ArtifactId,
            token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return new InvalidOperationException("The cancellation token was not forwarded.");
            });
        using var faultFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICentralDerivativeJobScheduler>();
            services.AddScoped<CentralDerivativeJobScheduler>();
            services.AddScoped<ICentralDerivativeJobScheduler>(provider => new FaultInjectingScheduler(
                provider.GetRequiredService<CentralDerivativeJobScheduler>(), injection));
        }));
        using var metrics = new ReconciliationConcurrencyMetricCollector();
        using var telemetry = new CentralIngestTelemetry();
        var logger = new RecordingLogger<CentralArtifactReconciliationService>();
        var reconciler = new CentralArtifactReconciliationService(
            faultFactory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            telemetry,
            logger);

        Func<Task> reconcile = () => reconciler.ReconcileAsync(cancellation.Token);
        var thrown = await reconcile.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);

        injection.InjectionCount.Should().Be(1);
        thrown.Which.CancellationToken.Should().Be(cancellation.Token);
        metrics.Outcomes.Should().BeEmpty();
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
        await using (var interruptedScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var interrupted = await interruptedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralArtifacts.AsNoTracking()
                .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId
                    && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
            interrupted.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
            interrupted.ObjectVerificationToken.Should().NotBeNull();
            interrupted.ObjectVerificationRequestedAtUtc.Should().NotBeNull();
            interrupted.ObjectVerifiedAtUtc.Should().NotBeNull();
        }

        using (var restartTelemetry = new CentralIngestTelemetry())
        {
            var restarted = new CentralArtifactReconciliationService(
                fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                restartTelemetry,
                NullLogger<CentralArtifactReconciliationService>.Instance);
            _ = await restarted.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await using var recoveredScope = fixture.Factory.Services.CreateAsyncScope();
        var recoveredDb = recoveredScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var recovered = await recoveredDb.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId
                && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        recovered.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        recovered.ObjectVerificationToken.Should().BeNull();
        recovered.ObjectVerificationRequestedAtUtc.Should().BeNull();
        (await recoveredDb.CentralDerivativeJobs.AnyAsync(job => job.SourceCentralArtifactId == recovered.Id)
            .ConfigureAwait(false)).Should().BeTrue();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task Reconciliation_NonConcurrencyFailureUsesExistingErrorPathWithoutRetry(bool unmarkedConcurrency)
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig(unmarkedConcurrency
            ? "reconciliation-unmarked-concurrency-rig"
            : "reconciliation-failure-rig");
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, unmarkedConcurrency ? 47 : 46);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var pending = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        ((int)pending.StatusCode).Should().Be(425);
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var injection = new SchedulerFaultInjection(
            manifest.Descriptor.Artifact.ArtifactId,
            unmarkedConcurrency
                ? static _ => new DbUpdateConcurrencyException("Injected unmarked scheduler concurrency failure.")
                : static _ => new InvalidOperationException("Injected scheduler failure."));
        using var faultFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICentralDerivativeJobScheduler>();
            services.AddScoped<CentralDerivativeJobScheduler>();
            services.AddScoped<ICentralDerivativeJobScheduler>(provider => new FaultInjectingScheduler(
                provider.GetRequiredService<CentralDerivativeJobScheduler>(), injection));
        }));
        using var metrics = new ReconciliationConcurrencyMetricCollector();
        using var telemetry = new CentralIngestTelemetry();
        var logger = new RecordingLogger<CentralArtifactReconciliationService>();
        var reconciler = new CentralArtifactReconciliationService(
            faultFactory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            telemetry,
            logger);

        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        injection.InjectionCount.Should().Be(1);
        metrics.Outcomes.Should().BeEmpty();
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error
            && entry.Message == "Central artifact reconciliation failed for one durable record"
            && entry.Exception != null
            && entry.Exception.GetType() == (unmarkedConcurrency
                ? typeof(DbUpdateConcurrencyException)
                : typeof(InvalidOperationException)));
        await QuarantineTestArtifactAsync(registrationId, manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MultipartIngestV2_CrossDeviceLineageSource_DoesNotBind()
    {
        var fixture = AssemblyHooks.Fixture;
        var (sourceDeviceId, sourceRegistrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var (derivativeDeviceId, derivativeRegistrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var sourceRig = CreateRig("cross-device-source-rig");
        var derivativeRig = CreateRig("cross-device-derivative-rig");
        await SeedRigProfileAsync(sourceRegistrationId, sourceRig).ConfigureAwait(false);
        await SeedRigProfileAsync(derivativeRegistrationId, derivativeRig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var source = CreateManifestV2(sourceDeviceId, sourceRig, payload, 23);
        var derivative = CreateManifestV2(
            derivativeDeviceId,
            derivativeRig,
            payload,
            24,
            role: FrameArtifactRole.Preview,
            sourceArtifactIds: [source.Descriptor.Artifact.ArtifactId]);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var sourceResponse = await PostAsync(client, source, payload).ConfigureAwait(false);
        using var derivativeResponse = await PostAsync(client, derivative, payload).ConfigureAwait(false);

        sourceResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        ((int)derivativeResponse.StatusCode).Should().Be(425);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var derivativeArtifact = await db.CentralArtifacts.Include(item => item.Sources)
            .SingleAsync(item => item.Frame!.RegistrationId == derivativeRegistrationId).ConfigureAwait(false);
        derivativeArtifact.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
        derivativeArtifact.StateReasonCode.Should().Be("lineage.source-not-found");
        derivativeArtifact.Sources.Should().ContainSingle(item => item.ResolvedCentralArtifactId == null);
    }

    [TestMethod]
    public async Task MultipartIngestV2_AfterMigrationPreservedDuplicateArtifactIds_EnrichesMatchingFrame()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("migration-enrichment-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, 25);
        const string bucket = "skymonitor-artifacts";
        var objectKey = $"artifacts/migration/{Guid.NewGuid():N}";
        var minio = fixture.Factory.Services.GetRequiredService<IMinioClient>();
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket)).ConfigureAwait(false);
        }
        await minio.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(objectKey)
            .WithStreamData(new MemoryStream(payload)).WithObjectSize(payload.LongLength)
            .WithContentType(manifest.Descriptor.Artifact.MediaType)).ConfigureAwait(false);
        Guid targetArtifactRowId;
        Guid duplicateFrameId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
            var targetFrame = CreateLegacyFrame(registration, manifest.Descriptor.Capture.CaptureId);
            var duplicateFrame = CreateLegacyFrame(registration, Guid.NewGuid());
            duplicateFrameId = duplicateFrame.FrameId;
            var targetArtifact = CreateLegacyArtifact(targetFrame, manifest, $"minio://{bucket}/{objectKey}");
            var duplicateArtifact = CreateLegacyArtifact(duplicateFrame, manifest, $"minio://{bucket}/migration/duplicate");
            targetArtifactRowId = targetArtifact.Id;
            db.CentralFrames.AddRange(targetFrame, duplicateFrame);
            db.CentralArtifacts.AddRange(targetArtifact, duplicateArtifact);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifacts = await assertionDb.CentralArtifacts.Include(item => item.IngestIdentities)
            .Where(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
            .ToListAsync().ConfigureAwait(false);
        artifacts.Should().HaveCount(2);
        artifacts.Single(item => item.Id == targetArtifactRowId).ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        artifacts.Single(item => item.Id == targetArtifactRowId).IngestIdentities.Should().HaveCount(2);
        artifacts.Single(item => item.Id != targetArtifactRowId).ReconstructionState.Should().Be(CentralReconstructionState.LegacyIncomplete);

        var thirdReuse = CreateManifestV2(
            deviceId,
            rig,
            payload,
            33,
            artifactId: manifest.Descriptor.Artifact.ArtifactId,
            captureId: duplicateFrameId);
        using var conflict = await PostAsync(client, thirdReuse, payload).ConfigureAwait(false);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        assertionDb.ChangeTracker.Clear();
        (await assertionDb.CentralArtifacts.CountAsync(item =>
            item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false)).Should().Be(2);
    }

    [TestMethod]
    public async Task Reconciliation_PendingLineage_ConvergesWhenSameDeviceSourceArrives()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("reconciled-lineage-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var sourceArtifactId = Guid.NewGuid();
        var derivative = CreateManifestV2(
            deviceId, rig, payload, 26, role: FrameArtifactRole.Preview, sourceArtifactIds: [sourceArtifactId]);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var pendingResponse = await PostAsync(client, derivative, payload).ConfigureAwait(false);
        ((int)pendingResponse.StatusCode).Should().Be(425);
        Guid sourceRowId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
            var sourceManifest = CreateManifestV2(deviceId, rig, payload, 27, artifactId: sourceArtifactId);
            var sourceFrame = CreateLegacyFrame(registration, sourceManifest.Descriptor.Capture.CaptureId);
            var sourceArtifact = CreateLegacyArtifact(sourceFrame, sourceManifest, "minio://migration/source-arrived");
            sourceArtifact.DevicePublicId = registration.DevicePublicId;
            sourceArtifact.ReconstructionState = CentralReconstructionState.Complete;
            sourceArtifact.StateReasonCode = null;
            sourceRowId = sourceArtifact.Id;
            db.CentralFrames.Add(sourceFrame);
            db.CentralArtifacts.Add(sourceArtifact);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);

        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await assertionDb.CentralArtifacts.Include(item => item.Sources)
            .SingleAsync(item => item.ArtifactId == derivative.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        artifact.StateReasonCode.Should().BeNull();
        artifact.Sources.Should().ContainSingle(item => item.ResolvedCentralArtifactId == sourceRowId);
    }

    [TestMethod]
    public async Task MultipartIngestV2_ValidRetryAfterMissingObject_RestoresReconstructionState()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("quarantine-retry-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, 28);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var accepted = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.SingleAsync(item => item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
            var objectKey = artifact.StorageReference["minio://skymonitor-artifacts/".Length..];
            var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
            await minio.RemoveObjectAsync(new RemoveObjectArgs()
                .WithBucket("skymonitor-artifacts").WithObject(objectKey)).ConfigureAwait(false);
        }

        using var quarantined = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        quarantined.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.SingleAsync(item => item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
            artifact.ObjectState.Should().Be(CentralArtifactObjectState.Quarantined);
            artifact.ReconstructionState.Should().Be(CentralReconstructionState.Quarantined);
            artifact.StateReasonCode.Should().Be("object.missing");
        }

        using var retry = await PostAsync(client, manifest, payload).ConfigureAwait(false);

        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var recovered = await assertionDb.CentralArtifacts.SingleAsync(item => item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        recovered.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        recovered.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        recovered.StateReasonCode.Should().BeNull();
    }

    [TestMethod]
    [DataRow("staging/", 10)]
    [DataRow("staging/derivatives/", 41)]
    public async Task Reconciliation_RemovesOnlyStagingObjectsPastGracePeriod(
        string stagingPrefix,
        int stagingPartition)
    {
        var fixture = AssemblyHooks.Fixture;
        var services = fixture.Factory.Services;
        var minio = services.GetRequiredService<IMinioClient>();
        const string bucket = "skymonitor-artifacts";
        var objectId = Guid.NewGuid().ToString("N");
        var objectKey = $"{stagingPrefix}a{objectId[1..]}";
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket)).ConfigureAwait(false);
        }
        await minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectKey)
            .WithStreamData(new MemoryStream([1, 2, 3, 4]))
            .WithObjectSize(4)).ConfigureAwait(false);
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(5));
        await using (var checkpointScope = services.CreateAsyncScope())
        {
            await checkpointScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralRecoveryCheckpoints.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Phase, CentralRecoveryPhases.Idle)
                    .SetProperty(item => item.StagingPartition, stagingPartition)
                    .SetProperty(item => item.StagingCursor, (string?)null)
                    .SetProperty(item => item.NextInventoryAtUtc, clock.UtcNow.AddDays(1))
                    .SetProperty(item => item.LeaseToken, (Guid?)null)
                    .SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
        }
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(
            services.GetRequiredService<IServiceScopeFactory>(),
            clock,
            telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);

        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        (await minio.StatObjectAsync(new StatObjectArgs().WithBucket(bucket).WithObject(objectKey)).ConfigureAwait(false))
            .Size.Should().Be(4);

        clock.UtcNow = clock.UtcNow.Add(CentralArtifactReconciliationService.StagingObjectGracePeriod);
        await using (var checkpointScope = services.CreateAsyncScope())
        {
            await checkpointScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralRecoveryCheckpoints.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Phase, CentralRecoveryPhases.Idle)
                    .SetProperty(item => item.StagingPartition, 0)
                    .SetProperty(item => item.StagingCursor, (string?)null)
                    .SetProperty(item => item.NextInventoryAtUtc, clock.UtcNow.AddDays(1))
                    .SetProperty(item => item.LeaseToken, (Guid?)null)
                    .SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
        }
        var convergenceCycles = 0;
        var removed = false;
        while (convergenceCycles < CentralArtifactReconciliationService.MaximumStagingConvergenceCycles)
        {
            convergenceCycles++;
            await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _ = await minio.StatObjectAsync(new StatObjectArgs().WithBucket(bucket).WithObject(objectKey))
                    .ConfigureAwait(false);
            }
            catch (Minio.Exceptions.MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
            {
                removed = true;
                break;
            }
        }

        removed.Should().BeTrue();
        convergenceCycles.Should().BeLessThanOrEqualTo(
            CentralArtifactReconciliationService.MaximumStagingConvergenceCycles);
        CentralArtifactReconciliationService.MaximumStagingConvergenceCycles.Should().BeLessThanOrEqualTo(47);
    }

    [TestMethod]
    public async Task MultipartIngest_WithLowercaseIdempotencyKey_IsAccepted()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, _) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var manifest = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");

        using var response = await PostAsync(client, manifest, ToLowerHex(manifest.IdempotencyKey)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [TestMethod]
    public async Task MultipartIngest_PreviewBeforeRawCreatesOneQueryableFrame()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var frameId = Guid.NewGuid();
        var capturedAtUtc = DateTimeOffset.UnixEpoch.AddDays(1);
        var preview = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Preview,
            "application/octet-stream", 4, PayloadChecksum, capturedAtUtc, "preview-v1", "frames/preview.bin");
        var scene = new SceneProvenance(
            "late-scene", "rig-v1", "HYG", "4.2", new string('B', 64),
            "EquidistantFisheye", "projection-v1", "scene-v1", "sensor-v1");
        var raw = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, capturedAtUtc, "raw-v1", "frames/raw.bin", scene);

        using var previewResponse = await PostAsync(client, preview).ConfigureAwait(false);
        using var rawResponse = await PostAsync(client, raw).ConfigureAwait(false);

        previewResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        rawResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var frame = await db.CentralFrames.Include(item => item.Artifacts).SingleAsync(
                item => item.RegistrationId == registrationId).ConfigureAwait(false);
            frame.Artifacts.Should().HaveCount(2);
            frame.SceneProvenanceJson.Should().Contain("late-scene");
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetUserTokenAsync(client).ConfigureAwait(false));
        using var latest = await client.GetAsync(
            new Uri($"/api/v1.0/frames/latest?agentId={deviceId}&role=raw", UriKind.Relative)).ConfigureAwait(false);
        latest.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await latest.Content.ReadAsStringAsync().ConfigureAwait(false));
        document.RootElement.GetProperty("FrameId").GetGuid().Should().Be(frameId);
        document.RootElement.GetProperty("Artifacts").GetArrayLength().Should().Be(2);
    }

    [TestMethod]
    public async Task MultipartIngest_WhenFrameCaptureTimeDiffers_IsConflict()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var frameId = Guid.NewGuid();
        var first = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Preview,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "preview-v1", "frames/preview.bin");
        var conflict = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch.AddSeconds(1), "raw-v1", "frames/raw.bin");
        using var accepted = await PostAsync(client, first).ConfigureAwait(false);

        using var response = await PostAsync(client, conflict).ConfigureAwait(false);

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var frame = await db.CentralFrames.Include(item => item.Artifacts).SingleAsync(
            item => item.RegistrationId == registrationId).ConfigureAwait(false);
        frame.Artifacts.Should().ContainSingle();
    }

    [TestMethod]
    public async Task MultipartIngest_ConcurrentRolesCreateOneFrame()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var frameId = Guid.NewGuid();
        var raw = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");
        var preview = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Preview,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "preview-v1", "frames/preview.bin");

        var responses = await Task.WhenAll(PostAsync(client, raw), PostAsync(client, preview)).ConfigureAwait(false);
        using var rawResponse = responses[0];
        using var previewResponse = responses[1];

        rawResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var frame = await db.CentralFrames.Include(item => item.Artifacts).SingleAsync(
            item => item.RegistrationId == registrationId).ConfigureAwait(false);
        frame.Artifacts.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task MultipartIngest_ConcurrentConflictDoesNotDeleteCommittedObject()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var manifest = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");
        var conflict = manifest with { ArtifactId = Guid.NewGuid(), MediaType = "image/png" };
        var preview = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), manifest.FrameId, FrameArtifactRole.Preview,
            "application/octet-stream", 4, PayloadChecksum, manifest.CapturedAtUtc, "preview-v1", "frames/preview.bin");

        var responses = await Task.WhenAll(
            PostAsync(client, manifest), PostAsync(client, conflict), PostAsync(client, preview)).ConfigureAwait(false);
        using var first = responses[0];
        using var second = responses[1];
        using var third = responses[2];

        new[] { first.StatusCode, second.StatusCode, third.StatusCode }.Should().BeEquivalentTo(
            new[] { HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.Conflict });
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var frame = await db.CentralFrames.Include(item => item.Artifacts).SingleAsync(
            item => item.RegistrationId == registrationId).ConfigureAwait(false);
        frame.Artifacts.Should().HaveCount(2);
        var artifact = frame.Artifacts.Single(item => item.Role == FrameArtifactRole.Raw);
        var objectKey = artifact.StorageReference["minio://skymonitor-artifacts/".Length..];
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        var objectInfo = await minio.StatObjectAsync(new StatObjectArgs()
            .WithBucket("skymonitor-artifacts").WithObject(objectKey)).ConfigureAwait(false);
        objectInfo.Size.Should().Be(4);
        objectInfo.ContentType.Should().Be(artifact.MediaType);
    }

    [TestMethod]
    public async Task HistoryQuery_PreservesIncompleteLegacyUploadVisibility()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var lateArtifactId = Guid.NewGuid();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
            db.DeviceImageUploads.Add(new DeviceImageUpload
            {
                RegistrationId = registration.Id,
                DevicePublicId = registration.DevicePublicId!.Value,
                ObservatoryId = registration.ObservatoryId,
                CapturedAtUtc = DateTimeOffset.UnixEpoch,
                ReceivedAtUtc = DateTimeOffset.UnixEpoch,
                ContentType = "application/octet-stream",
                PayloadBase64Length = 4,
                StorageReference = "stubs://legacy/visible",
                AgentId = deviceId
            });
            db.DeviceImageUploads.Add(new DeviceImageUpload
            {
                RegistrationId = registration.Id,
                DevicePublicId = registration.DevicePublicId.Value,
                ObservatoryId = registration.ObservatoryId,
                CapturedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
                ReceivedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
                ContentType = "application/octet-stream",
                PayloadBase64Length = 0,
                StorageReference = "minio://legacy/late-complete",
                IdempotencyKey = Convert.ToHexString(SHA256.HashData(lateArtifactId.ToByteArray())),
                ArtifactId = lateArtifactId,
                FrameId = Guid.NewGuid(),
                ArtifactRole = FrameArtifactRole.Raw.ToString(),
                RecipeVersion = "raw-v1",
                ManifestSchemaVersion = "v1",
                ChecksumSha256 = PayloadChecksum,
                ByteLength = 4,
                AgentId = deviceId
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetUserTokenAsync(client).ConfigureAwait(false));

        using var history = await client.GetAsync(
            new Uri($"/api/v1.0/artifacts?agentId={deviceId}", UriKind.Relative)).ConfigureAwait(false);

        history.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await history.Content.ReadAsStringAsync().ConfigureAwait(false);
        body.Should().Contain(lateArtifactId.ToString());
        body.Should().NotContain("stubs://");
        body.Should().NotContain("minio://");
        body.Should().NotContain("StorageReference");
    }

    [TestMethod]
    public async Task HistoryQueries_WithUndefinedNumericRole_ReturnBadRequest()
    {
        var fixture = AssemblyHooks.Fixture;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetUserTokenAsync(client).ConfigureAwait(false));

        using var artifacts = await client.GetAsync(new Uri("/api/v1.0/artifacts?role=999", UriKind.Relative)).ConfigureAwait(false);
        using var frames = await client.GetAsync(new Uri("/api/v1.0/frames?role=999", UriKind.Relative)).ConfigureAwait(false);

        artifacts.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        frames.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task MultipartIngest_ConcurrentInvalidPayloadCannotOverwriteAcceptedObject()
    {
        var fixture = AssemblyHooks.Fixture;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var validPayload = new byte[4 * 1024 * 1024];
        RandomNumberGenerator.Fill(validPayload);
        var invalidPayload = validPayload.ToArray();
        invalidPayload[0] ^= 0xff;
        var checksum = Convert.ToHexString(SHA256.HashData(validPayload));
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
            var manifest = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
                "application/octet-stream", validPayload.Length, checksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");

            var responses = await Task.WhenAll(
                PostAsync(client, manifest, payloadBytes: validPayload),
                PostAsync(client, manifest, payloadBytes: invalidPayload)).ConfigureAwait(false);
            using var first = responses[0];
            using var second = responses[1];

            new[] { first.StatusCode, second.StatusCode }.Should().BeEquivalentTo(
                new[] { HttpStatusCode.Accepted, HttpStatusCode.BadRequest }, $"iteration {iteration}");
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts.Include(item => item.Frame).SingleAsync(
                item => item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
            var objectKey = artifact.StorageReference["minio://skymonitor-artifacts/".Length..];
            var storedPayload = new MemoryStream();
            var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket("skymonitor-artifacts")
                .WithObject(objectKey)
                .WithCallbackStream(stream => stream.CopyTo(storedPayload))).ConfigureAwait(false);
            Convert.ToHexString(SHA256.HashData(storedPayload.ToArray())).Should().Be(checksum, $"iteration {iteration}");
        }
    }

    [TestMethod]
    public async Task MultipartIngest_LegacyTargetBeforeRawDoesNotScheduleDerivativeJobs()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var frameId = Guid.NewGuid();
        var preview = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Preview,
            "image/png", 4, PayloadChecksum, DateTimeOffset.UnixEpoch,
            CentralDerivativeRecipeCatalog.PreviewRecipeVersion, "frames/preview.bin");
        var raw = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");

        using var previewResponse = await PostAsync(client, preview).ConfigureAwait(false);
        using var rawResponse = await PostAsync(client, raw).ConfigureAwait(false);

        previewResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        rawResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.CentralDerivativeJobs.CountAsync(job => job.SourceArtifact!.Frame!.RegistrationId == registrationId)
            .ConfigureAwait(false)).Should().Be(0);
    }

    [TestMethod]
    public async Task MultipartIngest_ConcurrentLegacyRawAndTargetDoNotScheduleDerivativeJobs()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var frameId = Guid.NewGuid();
        var seed = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Combined,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "combined-v1", "frames/combined.bin");
        var raw = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Raw,
            "application/octet-stream", 4, PayloadChecksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");
        var preview = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), frameId, FrameArtifactRole.Preview,
            "image/png", 4, PayloadChecksum, DateTimeOffset.UnixEpoch,
            CentralDerivativeRecipeCatalog.PreviewRecipeVersion, "frames/preview.bin");
        using var seedResponse = await PostAsync(client, seed).ConfigureAwait(false);
        seedResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var responses = await Task.WhenAll(PostAsync(client, raw), PostAsync(client, preview)).ConfigureAwait(false);
        using var rawResponse = responses[0];
        using var previewResponse = responses[1];

        rawResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.CentralDerivativeJobs.CountAsync(job => job.SourceArtifact!.Frame!.RegistrationId == registrationId)
            .ConfigureAwait(false)).Should().Be(0);
    }

    [TestMethod]
    public async Task MultipartIngest_AmbiguousLowerDepthV2RemainsLegacyIncompleteAcrossDuplicate()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("ambiguous-lower-depth");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[8];
        var manifest = CreateManifestV2(deviceId, rig, payload, captureSequence: 206);
        manifest = manifest with
        {
            Descriptor = manifest.Descriptor with
            {
                Layout = new FrameLayoutDescriptor(
                    2, 2, 4, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian, 12, 16,
                    FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, ushort.MaxValue, payload.LongLength)
            }
        };
        var scheduler = new RecordingScheduler(manifest.Descriptor.Artifact.ArtifactId);
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICentralDerivativeJobScheduler>();
            services.AddScoped<ICentralDerivativeJobScheduler>(_ => scheduler);
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var first = await PostAsync(client, manifest, payload).ConfigureAwait(false);
        using var duplicate = await PostAsync(client, manifest, payload).ConfigureAwait(false);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts
            .Include(item => item.Layout)
            .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.LegacyIncomplete);
        artifact.StateReasonCode.Should().Be("layout.stored-code-ambiguous");
        artifact.Layout!.StoredCodeTransform.Should().BeNull();
        artifact.Layout.LevelCodeSpace.Should().BeNull();
        (await db.CentralDerivativeJobs.CountAsync(job => job.SourceCentralArtifactId == artifact.Id)
            .ConfigureAwait(false)).Should().Be(0);
        scheduler.InvocationCount.Should().Be(0);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        ArtifactUploadManifest manifest,
        string? idempotencyKey = null,
        byte[]? payloadBytes = null)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(JsonSerializer.Serialize(manifest)), "manifest");
        content.Add(new ByteArrayContent(payloadBytes ?? [1, 2, 3, 4]) { Headers = { ContentType = new MediaTypeHeaderValue(manifest.MediaType) } }, "payload", "artifact.bin");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1.0/artifacts", UriKind.Relative)) { Content = content };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey ?? manifest.IdempotencyKey);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, ArtifactManifestV2 manifest, byte[] payloadBytes)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(CaptureContractJson.Serialize(manifest))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
        }, "manifest");
        content.Add(new ByteArrayContent(payloadBytes)
        {
            Headers = { ContentType = new MediaTypeHeaderValue(manifest.Descriptor.Artifact.MediaType) }
        }, "payload", "artifact.bin");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1.0/artifacts", UriKind.Relative)) { Content = content };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", manifest.IdempotencyKey);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        StructuredProcessingProductManifestV1 manifest,
        byte[] payloadBytes)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(StructuredProcessingProductManifestJson.Serialize(manifest))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
        }, "manifest");
        content.Add(new ByteArrayContent(payloadBytes)
        {
            Headers = { ContentType = new MediaTypeHeaderValue(manifest.Descriptor.Artifact.MediaType) }
        }, "payload", "artifact.json");
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1.0/artifacts", UriKind.Relative))
        { Content = content };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", manifest.IdempotencyKey);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> PostStatusAsync(HttpClient client, ArtifactManifestV2 manifest)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1.0/artifacts/status", UriKind.Relative))
        {
            Content = new ByteArrayContent(CaptureContractJson.Serialize(manifest))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
            }
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", manifest.IdempotencyKey);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> PostStatusAsync(
        HttpClient client,
        StructuredProcessingProductManifestV1 manifest)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1.0/artifacts/status", UriKind.Relative))
        {
            Content = new ByteArrayContent(StructuredProcessingProductManifestJson.Serialize(manifest))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
            }
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", manifest.IdempotencyKey);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static CameraRigConfig CreateRig(string profileVersion)
        => new(
            new SensorProfile("test-sensor", 2, 2, 4.8, SensorColorMode.Mono, CameraPixelFormat.Mono8),
            new OpticsProfile("EquidistantFisheye", 1.5, 180, 0),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), 1, 2),
            ProfileVersion: profileVersion);

    private static SceneProvenance CreateSceneProvenance() => new(
        "central-conformance-scene",
        "central-conformance-rig",
        "test-catalog",
        "1.0.0",
        new string('D', 64),
        "EquidistantFisheye",
        "projection-v1",
        "astronomy-v1",
        "sensor-v1",
        Objects:
        [
            new("star:bright", "Bright Star", 0.5, 0.5, 1),
            new("star:faint", "Faint Star", 1.5, 0.5, 4),
            new("star:unnamed", "star:unnamed", 0.5, 1.5, 0),
            new("solar-system:mars", "Mars", 1.5, 1.5, 10)
        ],
        Segments:
        [
            new("ORI", "star:bright", "star:faint", 0.5, 0.5, 1.5, 0.5)
        ]);

    private static ProcessingAnnotationInput CreateExpectedAnnotation(SceneProvenance scene)
    {
        var objects = scene.Objects!.Select(item =>
        {
            var annotate = !string.IsNullOrWhiteSpace(item.DisplayName)
                && !string.Equals(item.Id, item.DisplayName, StringComparison.Ordinal)
                && (item.Id.StartsWith("solar-system:", StringComparison.Ordinal) || item.Magnitude <= 2.5);
            return new ProjectedAnnotationObject(
                item.Id, item.DisplayName, new PixelPoint(item.PixelX, item.PixelY), annotate, annotate);
        }).ToArray();
        var segments = scene.Segments!.Select(item => new ProjectedAnnotationSegment(
            item.ConstellationId,
            new PixelPoint(item.FromPixelX, item.FromPixelY),
            new PixelPoint(item.ToPixelX, item.ToPixelY))).ToArray();
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(
            CaptureContractJson.SerializeToElement(new { scene.SceneId, objects, segments }));
        return new ProcessingAnnotationInput(objects, segments, new PreviewTransform(1, 1), null, identity);
    }

    private static async Task<byte[]> ReadDerivativeAsync(HttpClient client, Guid deviceId, Guid artifactId)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1.0/devices/{deviceId:D}/artifacts/{artifactId:D}/content", UriKind.Relative)).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    private static ArtifactManifestV2 CreateManifestV2(
        string deviceId,
        CameraRigConfig rig,
        byte[] payload,
        long captureSequence,
        Guid? artifactId = null,
        FrameArtifactRole role = FrameArtifactRole.Raw,
        IReadOnlyList<Guid>? sourceArtifactIds = null,
        Guid? captureId = null,
        CaptureLocationProvenance? location = null,
        DateTimeOffset? capturedAtUtc = null)
    {
        var capturedAt = capturedAtUtc ?? DateTimeOffset.UnixEpoch;
        var profileHash = CameraRigProfileIdentity.ComputeSha256(rig);
        var otherProfileHash = new string('A', 64);
        var descriptor = new ReconstructionDescriptor(
            new CaptureIdentityDescriptor(deviceId, "rig-test", captureSequence, captureId ?? Guid.NewGuid()),
            new CaptureTimingDescriptor(
                capturedAt.AddMilliseconds(-3), capturedAt, capturedAt.AddMilliseconds(1),
                capturedAt.AddMilliseconds(2), capturedAt.AddMilliseconds(3)),
            new CaptureControlDescriptor(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1, 1, null, null, null, null),
            new CaptureProfileSet(
                new ProfileIdentityDescriptor("rig", rig.ProfileVersion, profileHash),
                new ProfileIdentityDescriptor("calibration", "none-v1", otherProfileHash),
                new ProfileIdentityDescriptor("mask", "none-v1", otherProfileHash),
                new ProfileIdentityDescriptor("test-sensor", "sensor-v1", otherProfileHash),
                new ProfileIdentityDescriptor("processing", "configured-v1", otherProfileHash)),
            new FrameLayoutDescriptor(
                2, 2, 2, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable, 8, 8,
                FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 255, payload.LongLength),
            new ArtifactDescriptor(
                artifactId ?? Guid.NewGuid(), role, "virtual-test", "source", capturedAt.AddMilliseconds(2), sourceArtifactIds ?? [],
                RecipeIdentityDescriptor.Create("capture-raw", "1.0.0", "raw-ingress-v1", JsonSerializer.SerializeToElement(new { normalization = "none" })),
                "application/x-skymonitor-mono8", Convert.ToHexString(SHA256.HashData(payload))))
        {
            Location = location
        };
        return new ArtifactManifestV2("v2", descriptor, "frames/raw.bin");
    }

    private static StructuredProcessingProductManifestV1 CreateStructuredManifest(
        ArtifactManifestV2 sourceManifest,
        PresentationLayerPayloadV1 layer,
        byte[] payload)
    {
        var sourceIds = new[] { sourceManifest.Descriptor.Artifact.ArtifactId };
        var recipe = RecipeIdentityDescriptor.Create(
            "presentation-layer", "1.0.0", "integration-v1",
            JsonSerializer.SerializeToElement(new { alpha = 1, beta = 2 }));
        var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe);
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Metadata, "scene-layer", recipeIdentity.IdentitySha256, sourceIds);
        var artifact = new ArtifactDescriptor(
            ProcessingIdentity.CreateArtifactId(outputIdentity),
            FrameArtifactRole.Metadata,
            "presentation-layer-step",
            "scene-layer",
            sourceManifest.Descriptor.Timing.ReadoutCompletedUtc,
            sourceIds,
            recipe,
            PresentationLayerPayloadJson.MediaType,
            Convert.ToHexString(SHA256.HashData(payload)));
        return new StructuredProcessingProductManifestV1(
            StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new StructuredProcessingProductDescriptorV1(
                sourceManifest.Descriptor,
                artifact,
                outputIdentity,
                [new("presentation-layer", "integration-v1")],
                new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
                TimeSpan.FromSeconds(1).Ticks,
                payload.LongLength,
                ProcessingProductKind.Metadata,
                PresentationLayerPayloadV1.CurrentSchemaVersion,
                layer.ContentIdentitySha256),
            "derived/scene-layer.json",
            ProducerStepId: "presentation-layer-step");
    }

    private static ArtifactUploadManifest CreateCompatibilityManifest(ArtifactManifestV2 current)
    {
        var descriptor = current.Descriptor;
        return new ArtifactUploadManifest(
            ArtifactUploadManifest.CurrentSchemaVersion,
            descriptor.Capture.AgentId,
            descriptor.Artifact.ArtifactId,
            descriptor.Capture.CaptureId,
            descriptor.Artifact.Role,
            descriptor.Artifact.MediaType,
            descriptor.Layout.ByteLength,
            descriptor.Artifact.ChecksumSha256,
            descriptor.Timing.ExposureStartedUtc,
            $"v2-{current.IdempotencyKey}",
            current.RelativeArtifactPath);
    }

    private static async Task RemoveCanonicalObjectAsync(Guid registrationId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.SingleAsync(item => item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        var objectKey = artifact.StorageReference["minio://skymonitor-artifacts/".Length..];
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        await minio.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket("skymonitor-artifacts")
            .WithObject(objectKey)).ConfigureAwait(false);
    }

    private static async Task RemoveArtifactObjectAsync(Guid registrationId, Guid artifactId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.SingleAsync(item =>
            item.Frame!.RegistrationId == registrationId && item.ArtifactId == artifactId).ConfigureAwait(false);
        var objectKey = artifact.StorageReference["minio://skymonitor-artifacts/".Length..];
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        await minio.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket("skymonitor-artifacts")
            .WithObject(objectKey)).ConfigureAwait(false);
    }

    private static async Task AssertCanonicalRecoveryAsync(Guid registrationId, byte[] payload)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifacts = await db.CentralArtifacts
            .Include(item => item.IngestIdentities)
            .Where(item => item.Frame!.RegistrationId == registrationId)
            .ToListAsync().ConfigureAwait(false);
        artifacts.Should().ContainSingle();
        var artifact = artifacts.Single();
        artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
        artifact.IngestIdentities.Should().HaveCount(2);
        var objectKey = artifact.StorageReference["minio://skymonitor-artifacts/".Length..];
        var storedPayload = new MemoryStream();
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        await minio.GetObjectAsync(new GetObjectArgs()
            .WithBucket("skymonitor-artifacts")
            .WithObject(objectKey)
            .WithCallbackStream(stream => stream.CopyTo(storedPayload))).ConfigureAwait(false);
        storedPayload.ToArray().Should().Equal(payload);
    }

    private static async Task SeedRigProfileAsync(Guid registrationId, CameraRigConfig rig)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
        db.DeviceRigProfiles.Add(new DeviceRigProfile
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId!.Value,
            ObservatoryId = registration.ObservatoryId,
            Version = 1,
            ConfigHash = CameraRigProfileIdentity.ComputeSha256(rig),
            ConfigJson = JsonSerializer.Serialize(rig),
            ProfileName = "rig",
            ProfileVersion = rig.ProfileVersion,
            ProfileSha256 = CameraRigProfileIdentity.ComputeSha256(rig),
            CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
            EffectiveFromUtc = DateTimeOffset.UnixEpoch.AddDays(-1)
        });
        registration.CurrentRigProfileVersion = 1;
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<CaptureLocationProvenance> SeedAcknowledgedDeploymentLocationAsync(
        Guid registrationId,
        DateTimeOffset? effectiveFromUtc = null,
        DateTimeOffset? effectiveUntilUtc = null)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.Include(item => item.Observatory)
            .SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
        var observatory = registration.Observatory!;
        var deployment = DeploymentLocationSnapshot.Create(
            "inherited-observatory",
            1,
            "observatory-fallback",
            null,
            effectiveFromUtc ?? DateTimeOffset.UnixEpoch.AddDays(-1),
            effectiveUntilUtc,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId);
        var service = scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
        var acknowledgment = await service.ProposeAsync(
            registration,
            deployment,
            DeploymentLocationSourceKind.Inherited,
            "integration-test").ConfigureAwait(false);
        acknowledgment.Status.Should().Be(DeploymentLocationResolutionStatus.Acknowledged);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return deployment.ToProvenance();
    }

    private static async Task EnsureObservatoryLocationVersionAsync(Guid registrationId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.Include(item => item.Observatory)
            .SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
        _ = await ObservatoryLocationAuthority.EnsureCurrentVersionAsync(
            db,
            registration.Observatory!,
            DateTimeOffset.UtcNow,
            "integration-test",
            CancellationToken.None).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task QuarantineTestArtifactAsync(Guid registrationId, Guid artifactId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.SingleAsync(item =>
            item.ArtifactId == artifactId && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        artifact.ReconstructionState = CentralReconstructionState.Quarantined;
        artifact.StateReasonCode = "test.reconciliation-fault";
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static CentralFrame CreateLegacyFrame(DeviceRegistration registration, Guid frameId)
        => new()
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId!.Value,
            ObservatoryId = registration.ObservatoryId,
            AgentId = registration.DeviceId,
            FrameId = frameId,
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            FirstReceivedAtUtc = DateTimeOffset.UnixEpoch
        };

    private static CentralArtifact CreateLegacyArtifact(
        CentralFrame frame,
        ArtifactManifestV2 manifest,
        string storageReference)
    {
        var descriptor = manifest.Descriptor;
        var idempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        var artifact = new CentralArtifact
        {
            CentralFrameId = frame.Id,
            Frame = frame,
            ArtifactId = descriptor.Artifact.ArtifactId,
            DevicePublicId = null,
            Role = descriptor.Artifact.Role,
            RecipeVersion = "legacy-raw-v1",
            ManifestSchemaVersion = ArtifactUploadManifest.CurrentSchemaVersion,
            MediaType = descriptor.Artifact.MediaType,
            ByteLength = descriptor.Layout.ByteLength,
            ChecksumSha256 = descriptor.Artifact.ChecksumSha256,
            StorageReference = storageReference,
            ReceivedAtUtc = DateTimeOffset.UnixEpoch,
            IdempotencyKey = idempotencyKey,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.LegacyIncomplete,
            StateReasonCode = "manifest.legacy-incomplete"
        };
        artifact.IngestIdentities.Add(new CentralArtifactIngestIdentity
        {
            ManifestSchemaVersion = ArtifactUploadManifest.CurrentSchemaVersion,
            IdempotencyKey = idempotencyKey
        });
        return artifact;
    }

    private static async Task<string> GetSystemTokenAsync(HttpClient client)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "system-camera-agent",
            ["client_secret"] = "test-camera-agent-secret-do-not-use-in-production",
            ["scope"] = "api.camera api.frames"
        });
        using var response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<string> GetUserTokenAsync(HttpClient client)
    {
        var token = await HttpHelpers.GetPasswordTokenAsync(
            client,
            "/connect/token",
            TestUsers.Operator.Username,
            TestUsers.Operator.Password,
            TestClients.WebUI.ClientId,
            string.Join(' ', TestClients.WebUI.Scopes)).ConfigureAwait(false);
        return token.AccessToken;
    }

    private static async Task<(string DeviceId, Guid RegistrationId)> SeedActiveDeviceAsync(string? requestedDeviceId = null)
    {
        var deviceId = requestedDeviceId ?? $"artifact-device-{Guid.NewGuid():N}";
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var observatory = new Observatory
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            Name = "Artifact Observatory",
            TimeZoneId = "UTC",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsActive = true
        };
        var registration = new DeviceRegistration
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            ObservatoryId = observatory.Id,
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = "UTC",
            FriendlyName = "Artifact Device",
            OwnerUserId = owner.Id,
            OwnerDisplayName = TestUsers.Operator.FullName,
            OwnerConfirmationMethod = "SelfAttested",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = DeviceRegistrationService.ComputeSha256("ABCDE"),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            ActivatedAtUtc = DateTimeOffset.UtcNow,
            DevicePublicId = Guid.NewGuid(),
            DeviceKeyHash = DeviceRegistrationService.ComputeSha256("artifact-key")
        };
        db.Observatories.Add(observatory);
        db.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            UserId = owner.Id,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = observatory.CreatedAtUtc
        });
        db.DeviceRegistrations.Add(registration);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return (deviceId, registration.Id);
    }

    private static readonly string PayloadChecksum = Convert.ToHexString(SHA256.HashData([1, 2, 3, 4]));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class CopyObjectFaultHandler : DelegatingHandler
    {
        private int _remainingFailures = 1;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put
                && request.Headers.Contains("x-amz-copy-source")
                && Interlocked.Decrement(ref _remainingFailures) >= 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        "<Error><Code>ServiceUnavailable</Code><Message>Injected copy failure</Message></Error>",
                        Encoding.UTF8,
                        "application/xml")
                });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class MinioGetCountingHandler : DelegatingHandler
    {
        private long getRequests;

        public long GetRequests => Interlocked.Read(ref getRequests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref getRequests);
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ExistingObjectGetFailureHandler : DelegatingHandler
    {
        private int failures;

        internal int Failures => Volatile.Read(ref failures);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && Interlocked.CompareExchange(ref failures, 1, 0) == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    Content = new StringContent("injected existing-object stream failure", Encoding.UTF8, "text/plain")
                });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class OneStaleGenerationState
    {
        private int checks;
        private int rejections;

        public int Checks => Volatile.Read(ref checks);
        public int Rejections => Volatile.Read(ref rejections);

        public bool IsCurrent()
        {
            Interlocked.Increment(ref checks);
            if (Interlocked.CompareExchange(ref rejections, 1, 0) == 0)
            {
                return false;
            }
            return true;
        }
    }

    private sealed class OneStaleGenerationObjectReader(
        ICentralArtifactObjectReader inner,
        OneStaleGenerationState state) : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
            => inner.VerifyAsync(artifact, cancellationToken);

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
            => Task.FromResult(state.IsCurrent());

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
            => inner.CopyToAsync(snapshot, destination, range, cancellationToken);
    }

    private sealed class PackedBaseReadCounter(Guid artifactId)
    {
        private int copyCount;

        public Guid ArtifactId { get; } = artifactId;
        public int CopyCount => Volatile.Read(ref copyCount);
        public void RecordCopy() => Interlocked.Increment(ref copyCount);
    }

    private sealed class CountingPackedBaseObjectReader(
        ICentralArtifactObjectReader inner,
        PackedBaseReadCounter counter) : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
            => inner.VerifyAsync(artifact, cancellationToken);

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
            => inner.IsCurrentGenerationAsync(artifact, storageETag, cancellationToken);

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
        {
            if (snapshot.ObjectKey.Contains(counter.ArtifactId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                counter.RecordCopy();
            }
            return inner.CopyToAsync(snapshot, destination, range, cancellationToken);
        }
    }

    private sealed class SchedulerConcurrencyInjection(
        Guid targetArtifactId,
        int remainingInjections,
        bool commitRequiredJobsOnFinalInjection = false)
    {
        private int remaining = remainingInjections;
        private int injectionCount;
        private readonly HashSet<Guid> scopeContextIds = [];

        public int InjectionCount => Volatile.Read(ref injectionCount);

        public IReadOnlyCollection<Guid> ScopeContextIds
        {
            get
            {
                lock (scopeContextIds)
                {
                    return scopeContextIds.ToArray();
                }
            }
        }

        public bool TryInject(Guid artifactId, Guid scopeContextId, out bool commitRequiredJobs)
        {
            commitRequiredJobs = false;
            if (artifactId != targetArtifactId)
            {
                return false;
            }
            lock (scopeContextIds)
            {
                scopeContextIds.Add(scopeContextId);
            }
            var remainingAfterInjection = Interlocked.Decrement(ref remaining);
            if (remainingAfterInjection < 0)
            {
                return false;
            }
            commitRequiredJobs = commitRequiredJobsOnFinalInjection && remainingAfterInjection == 0;
            Interlocked.Increment(ref injectionCount);
            return true;
        }
    }

    private sealed class RecordingScheduler(Guid targetArtifactId) : ICentralDerivativeJobScheduler
    {
        private int invocationCount;

        public int InvocationCount => Volatile.Read(ref invocationCount);

        public Task EnsureRequiredJobsAsync(
            CentralArtifact artifact,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            if (artifact.ArtifactId == targetArtifactId)
            {
                Interlocked.Increment(ref invocationCount);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrencyInjectingScheduler(
        CentralDerivativeJobScheduler inner,
        IServiceScopeFactory scopeFactory,
        ApplicationDbContext currentDbContext,
        SchedulerConcurrencyInjection injection) : ICentralDerivativeJobScheduler
    {
        public async Task EnsureRequiredJobsAsync(
            CentralArtifact artifact,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            if (injection.TryInject(
                    artifact.ArtifactId,
                    currentDbContext.ContextId.InstanceId,
                    out var commitRequiredJobs))
            {
                currentDbContext.Entry(artifact).Property(candidate => candidate.ReconciledAtUtc).IsModified = true;
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var concurrentArtifact = await db.CentralArtifacts
                    .Include(candidate => candidate.Frame)!.ThenInclude(frame => frame!.Artifacts)
                    .Include(candidate => candidate.Frame)!.ThenInclude(frame => frame!.Location)
                    .SingleAsync(candidate => candidate.Id == artifact.Id, cancellationToken).ConfigureAwait(false);
                if (commitRequiredJobs)
                {
                    await scope.ServiceProvider.GetRequiredService<CentralDerivativeJobScheduler>()
                        .EnsureRequiredJobsAsync(concurrentArtifact, now, cancellationToken).ConfigureAwait(false);
                }
                concurrentArtifact.ReconciledAtUtc = (concurrentArtifact.ReconciledAtUtc ?? DateTimeOffset.UnixEpoch).AddTicks(1);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (commitRequiredJobs)
                {
                    var exception = new DbUpdateConcurrencyException(
                        "Injected scheduling conflict after the competing scheduler committed required jobs.");
                    exception.Data[CentralDerivativeJobScheduler.SchedulingConcurrencyMarker] = true;
                    throw exception;
                }
            }
            await inner.EnsureRequiredJobsAsync(artifact, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class SchedulerFaultInjection(Guid targetArtifactId, Func<CancellationToken, Exception> createException)
    {
        private int injectionCount;

        public int InjectionCount => Volatile.Read(ref injectionCount);

        public Exception? Create(Guid artifactId, CancellationToken cancellationToken)
        {
            if (artifactId != targetArtifactId || Interlocked.Increment(ref injectionCount) != 1)
            {
                return null;
            }
            return createException(cancellationToken);
        }
    }

    private sealed class FaultInjectingScheduler(
        CentralDerivativeJobScheduler inner,
        SchedulerFaultInjection injection) : ICentralDerivativeJobScheduler
    {
        public async Task EnsureRequiredJobsAsync(
            CentralArtifact artifact,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var exception = injection.Create(artifact.ArtifactId, cancellationToken);
            if (exception is not null)
            {
                throw exception;
            }
            await inner.EnsureRequiredJobsAsync(artifact, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ReconciliationConcurrencyMetricCollector : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly List<string> outcomes = [];
        private int completedCount;

        public ReconciliationConcurrencyMetricCollector()
        {
            listener.InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == CentralIngestTelemetry.MeterName
                    && instrument.Name is "skymonitor.central.ingest.reconciliation_concurrency"
                        or "skymonitor.central.ingest.reconciled")
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "outcome" && tag.Value is string outcome)
                    {
                        if (instrument.Name == "skymonitor.central.ingest.reconciled" && outcome == "completed")
                        {
                            Interlocked.Increment(ref completedCount);
                        }
                        else if (instrument.Name == "skymonitor.central.ingest.reconciliation_concurrency")
                        {
                            outcomes.Add(outcome);
                        }
                    }
                }
            });
            listener.Start();
        }

        public IReadOnlyList<string> Outcomes => outcomes;

        public int CompletedCount => Volatile.Read(ref completedCount);

        public void Dispose() => listener.Dispose();
    }

    private sealed class CompletionUpdateBarrier(int participants)
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        internal async Task SignalAndWaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref arrivals) == participants)
            {
                released.TrySetResult();
            }
            await released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class CompletionUpdateBarrierInterceptor(CompletionUpdateBarrier barrier) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE [c]", StringComparison.Ordinal)
                && command.CommandText.Contains("[CompletedAtUtc]", StringComparison.Ordinal)
                && command.CommandText.Contains("[ResultCentralArtifactId]", StringComparison.Ordinal))
            {
                await barrier.SignalAndWaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new(logLevel, formatter(state, exception), exception));
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("redis unavailable");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Task.FromException<byte[]?>(new InvalidOperationException("redis unavailable"));
        public void Refresh(string key) => throw new InvalidOperationException("redis unavailable");
        public Task RefreshAsync(string key, CancellationToken token = default) =>
            Task.FromException(new InvalidOperationException("redis unavailable"));
        public void Remove(string key) => throw new InvalidOperationException("redis unavailable");
        public Task RemoveAsync(string key, CancellationToken token = default) =>
            Task.FromException(new InvalidOperationException("redis unavailable"));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            throw new InvalidOperationException("redis unavailable");
        public Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default) =>
            Task.FromException(new InvalidOperationException("redis unavailable"));
    }

    private sealed class EmptyDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { }
        public Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class UnavailableObjectReader : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken) =>
            Task.FromException<CentralArtifactObjectSnapshot>(new CentralArtifactStorageException());

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken) =>
            Task.FromException(new CentralArtifactStorageException());
    }

    private static string ToLowerHex(string value)
        => string.Create(value.Length, value, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                destination[index] = source[index] switch
                {
                    'A' => 'a',
                    'B' => 'b',
                    'C' => 'c',
                    'D' => 'd',
                    'E' => 'e',
                    'F' => 'f',
                    var character => character
                };
            }
        });
}
