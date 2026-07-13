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
        var upload = await db.DeviceImageUploads.SingleAsync(
            item => item.RegistrationId == registrationId).ConfigureAwait(false);
        upload.SceneProvenanceJson.Should().Contain("scene-id");
        upload.FrameId.Should().Be(manifest.FrameId);
        upload.RecipeVersion.Should().Be(manifest.RecipeVersion);
        upload.ManifestSchemaVersion.Should().Be(manifest.SchemaVersion);
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
        (await db.DeviceImageUploads.AnyAsync(item => item.RegistrationId == registrationId).ConfigureAwait(false)).Should().BeFalse();
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

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, ArtifactUploadManifest manifest, string? idempotencyKey = null)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(JsonSerializer.Serialize(manifest)), "manifest");
        content.Add(new ByteArrayContent([1, 2, 3, 4]) { Headers = { ContentType = new MediaTypeHeaderValue(manifest.MediaType) } }, "payload", "artifact.bin");
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
