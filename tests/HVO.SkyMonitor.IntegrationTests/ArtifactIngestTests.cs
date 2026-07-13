using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
public sealed class ArtifactIngestTests
{
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
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var frame = await db.CentralFrames.Include(item => item.Artifacts).SingleAsync(
            item => item.RegistrationId == registrationId).ConfigureAwait(false);
        frame.SceneProvenanceJson.Should().Contain("scene-id");
        frame.FrameId.Should().Be(manifest.FrameId);
        frame.Artifacts.Should().ContainSingle();
        frame.Artifacts.Single().RecipeVersion.Should().Be(manifest.RecipeVersion);
        frame.Artifacts.Single().ManifestSchemaVersion.Should().Be(manifest.SchemaVersion);
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
        objectInfo.ContentType.Should().Be("application/octet-stream");
    }

    [TestMethod]
    public async Task HistoryQuery_PreservesIncompleteLegacyUploadVisibility()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
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
            var lateArtifactId = Guid.NewGuid();
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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var history = await client.GetAsync(
            new Uri($"/api/v1.0/artifacts?agentId={deviceId}", UriKind.Relative)).ConfigureAwait(false);

        history.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await history.Content.ReadAsStringAsync().ConfigureAwait(false);
        body.Should().Contain("stubs://legacy/visible");
        body.Should().Contain("minio://legacy/late-complete");
    }

    [TestMethod]
    public async Task HistoryQueries_WithUndefinedNumericRole_ReturnBadRequest()
    {
        var fixture = AssemblyHooks.Fixture;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var artifacts = await client.GetAsync(new Uri("/api/v1.0/artifacts?role=999", UriKind.Relative)).ConfigureAwait(false);
        using var frames = await client.GetAsync(new Uri("/api/v1.0/frames?role=999", UriKind.Relative)).ConfigureAwait(false);

        artifacts.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        frames.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task MultipartIngest_ConcurrentInvalidPayloadCannotOverwriteAcceptedObject()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var validPayload = new byte[4 * 1024 * 1024];
        RandomNumberGenerator.Fill(validPayload);
        var invalidPayload = validPayload.ToArray();
        invalidPayload[0] ^= 0xff;
        var checksum = Convert.ToHexString(SHA256.HashData(validPayload));
        var manifest = new ArtifactUploadManifest("v1", deviceId, Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", validPayload.Length, checksum, DateTimeOffset.UnixEpoch, "raw-v1", "frames/raw.bin");

        var responses = await Task.WhenAll(
            PostAsync(client, manifest, payloadBytes: validPayload),
            PostAsync(client, manifest, payloadBytes: invalidPayload)).ConfigureAwait(false);
        using var first = responses[0];
        using var second = responses[1];

        new[] { first.StatusCode, second.StatusCode }.Should().BeEquivalentTo(
            new[] { HttpStatusCode.Accepted, HttpStatusCode.BadRequest });
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
        Convert.ToHexString(SHA256.HashData(storedPayload.ToArray())).Should().Be(checksum);
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

    private static async Task<(string DeviceId, Guid RegistrationId)> SeedActiveDeviceAsync()
    {
        var deviceId = $"artifact-device-{Guid.NewGuid():N}";
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var observatory = new Observatory
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "integration-tests",
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
            OwnerUserId = "integration-tests",
            OwnerDisplayName = "Integration Tests",
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
