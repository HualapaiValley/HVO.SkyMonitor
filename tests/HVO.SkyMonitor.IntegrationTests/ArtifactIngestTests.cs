using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.TestHost;
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task IngestStatus_BackgroundResolvedReferenceDoesNotAcknowledgeMissingOrCorruptObject(bool corruptSameLength)
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig(corruptSameLength ? "status-corrupt-rig" : "status-missing-rig");
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, corruptSameLength ? 42 : 41);
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
            if (corruptSameLength)
            {
                var corruptPayload = new byte[] { 4, 3, 2, 1 };
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
        minioCounter.GetRequests.Should().Be(corruptSameLength ? 1 : 0,
            "a missing key is rejected by MinIO's prerequisite stat, while a present object is streamed once");
        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var quarantined = await assertionDb.CentralArtifacts.SingleAsync(item =>
            item.Frame!.RegistrationId == registrationId
            && item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
        quarantined.ObjectState.Should().Be(CentralArtifactObjectState.Quarantined);
        quarantined.ReconstructionState.Should().Be(CentralReconstructionState.Quarantined);
        quarantined.StateReasonCode.Should().Be(corruptSameLength
            ? "object.checksum-mismatch"
            : "object.missing");
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
            && entry.Message.Contains("will retry once", StringComparison.Ordinal));
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
        var injection = new SchedulerConcurrencyInjection(manifest.Descriptor.Artifact.ArtifactId, 2);
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
        injection.ScopeContextIds.Should().HaveCount(2);
        metrics.Outcomes.Should().BeEquivalentTo(["retry", "exhausted"]);
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error
            && entry.Message == "Central artifact reconciliation failed for one durable record"
            && entry.Exception != null
            && entry.Exception.GetType() == typeof(DbUpdateConcurrencyException));
        await using var assertionScope = concurrencyFactory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await assertionDb.CentralArtifacts.SingleAsync(item =>
            item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId
            && item.Frame!.RegistrationId == registrationId).ConfigureAwait(false);
        artifact.ReconstructionState.Should().Be(CentralReconstructionState.PendingReference);
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
        await QuarantineTestArtifactAsync(registrationId, manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
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
    public async Task Reconciliation_RemovesOnlyStagingObjectsPastGracePeriod()
    {
        var fixture = AssemblyHooks.Fixture;
        var services = fixture.Factory.Services;
        var minio = services.GetRequiredService<IMinioClient>();
        const string bucket = "skymonitor-artifacts";
        const int stagingPartition = 10;
        var objectKey = $"staging/a{Guid.NewGuid():N}";
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

    private sealed class SchedulerConcurrencyInjection(Guid targetArtifactId, int remainingInjections)
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

        public bool TryInject(Guid artifactId, Guid scopeContextId)
        {
            if (artifactId != targetArtifactId)
            {
                return false;
            }
            lock (scopeContextIds)
            {
                scopeContextIds.Add(scopeContextId);
            }
            if (Interlocked.Decrement(ref remaining) < 0)
            {
                return false;
            }
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
            if (injection.TryInject(artifact.ArtifactId, currentDbContext.ContextId.InstanceId))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var concurrentArtifact = await db.CentralArtifacts.SingleAsync(
                    candidate => candidate.Id == artifact.Id,
                    cancellationToken).ConfigureAwait(false);
                concurrentArtifact.ReconciledAtUtc = (concurrentArtifact.ReconciledAtUtc ?? DateTimeOffset.UnixEpoch).AddTicks(1);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

        public ReconciliationConcurrencyMetricCollector()
        {
            listener.InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == CentralIngestTelemetry.MeterName
                    && instrument.Name == "skymonitor.central.ingest.reconciliation_concurrency")
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "outcome" && tag.Value is string outcome)
                    {
                        outcomes.Add(outcome);
                    }
                }
            });
            listener.Start();
        }

        public IReadOnlyList<string> Outcomes => outcomes;

        public void Dispose() => listener.Dispose();
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
