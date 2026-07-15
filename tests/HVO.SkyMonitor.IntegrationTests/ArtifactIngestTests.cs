using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.TestHost;
using Minio;
using Minio.DataModel.Args;

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
        artifact.Recipe!.OptionsSha256.Should().Be(manifest.Descriptor.Artifact.Recipe.OptionsSha256);
        artifact.IngestIdentities.Should().ContainSingle(item => item.IdempotencyKey == manifest.IdempotencyKey);
        var persistedDescriptor = CentralReconstructionDescriptorFactory.Create(artifact.Frame, artifact);
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
            suspendedJobs.Should().HaveCount(2);
            suspendedJobs.Should().OnlyContain(job => job.Status == CentralDerivativeJobStatus.RetryableFailure
                && job.AvailableAtUtc == null
                && job.LeaseToken == null
                && job.LastError == CentralDerivativeJobScheduler.SourceInvalidatedReason);
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
        (await recoveryDb.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId == recoveredSource.Id)
            .ToListAsync().ConfigureAwait(false)).Should().OnlyContain(job =>
                job.Status == CentralDerivativeJobStatus.Pending && job.AvailableAtUtc != null);
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
        var objectKey = $"staging/reconciliation-{Guid.NewGuid():N}";
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
        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        var stat = async () => await minio.StatObjectAsync(
            new StatObjectArgs().WithBucket(bucket).WithObject(objectKey)).ConfigureAwait(false);
        await stat.Should().ThrowAsync<Minio.Exceptions.MinioException>().ConfigureAwait(false);
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

    private static ArtifactManifestV2 CreateManifestV2(
        string deviceId,
        CameraRigConfig rig,
        byte[] payload,
        long captureSequence,
        Guid? artifactId = null,
        FrameArtifactRole role = FrameArtifactRole.Raw,
        IReadOnlyList<Guid>? sourceArtifactIds = null,
        Guid? captureId = null)
    {
        var capturedAt = DateTimeOffset.UnixEpoch;
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
                "application/x-skymonitor-mono8", Convert.ToHexString(SHA256.HashData(payload))));
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
