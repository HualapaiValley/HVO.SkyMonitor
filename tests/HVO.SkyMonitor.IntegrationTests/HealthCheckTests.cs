using System;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Basic health check integration tests to verify the test infrastructure works.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class HealthCheckTests
{
    private static readonly string[] WorkerHealthDataKeys =
        ["Status", "ActiveSlots", "PendingCount", "OldestAgeSeconds", "LastSuccessAgeSeconds"];
    private HttpClient? _client;

    [TestInitialize]
    public void TestInitialize()
    {
        _client = AssemblyHooks.Fixture.Factory.CreateClient();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client?.Dispose();
    }

    [TestMethod]
    public async Task HealthCheckReportsExplicitFixtureCatalogAsync()
    {
        // Arrange
        var request = new Uri("/health", UriKind.Relative);

        // Act
        var response = await _client!.GetAsync(request).ConfigureAwait(false);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual("Degraded", payload.RootElement.GetProperty("status").GetString());
        var catalog = payload.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "catalog");
        Assert.AreEqual("Degraded", catalog.GetProperty("status").GetString());
        StringAssert.Contains(
            catalog.GetProperty("description").GetString(),
            "Fixture celestial catalog snapshot",
            StringComparison.Ordinal);
        var identity = catalog.GetProperty("data");
        Assert.AreEqual("Fixture", identity.GetProperty("Kind").GetString());
        Assert.AreEqual("4.2-fixture.1", identity.GetProperty("CatalogVersion").GetString());
        Assert.AreEqual(9, identity.GetProperty("RowCount").GetInt64());
        var worker = payload.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "central-derivative-worker");
        Assert.AreEqual("Healthy", worker.GetProperty("status").GetString());
        var workerData = worker.GetProperty("data");
        Assert.AreEqual("disabled", workerData.GetProperty("Status").GetString());
        CollectionAssert.AreEquivalent(
            WorkerHealthDataKeys,
            workerData.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [TestMethod]
    public async Task AliveCheckReturnsHealthyAsync()
    {
        // Arrange
        var request = new Uri("/alive", UriKind.Relative);

        // Act
        var response = await _client!.GetAsync(request).ConfigureAwait(false);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task HealthCheckReportsStaleArtifactConsistencyStateAsDegradedAsync()
    {
        var devicePublicId = Guid.NewGuid();
        var frame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = devicePublicId,
            ObservatoryId = Guid.NewGuid(),
            AgentId = $"health-device-{Guid.NewGuid():N}",
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            FirstReceivedAtUtc = DateTimeOffset.UnixEpoch
        };
        var artifactId = Guid.NewGuid();
        var idempotencyKey = Convert.ToHexString(SHA256.HashData(artifactId.ToByteArray()));
        frame.Artifacts.Add(new CentralArtifact
        {
            Frame = frame,
            ArtifactId = artifactId,
            DevicePublicId = devicePublicId,
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "health-test-v1",
            ManifestSchemaVersion = ArtifactUploadManifest.CurrentSchemaVersion,
            MediaType = "application/octet-stream",
            ByteLength = 4,
            ChecksumSha256 = new string('A', 64),
            StorageReference = "minio://skymonitor-artifacts/health/missing.bin",
            ReceivedAtUtc = DateTimeOffset.UtcNow - CentralArtifactConsistencyHealthCheck.StaleAfter - TimeSpan.FromMinutes(1),
            IdempotencyKey = idempotencyKey,
            ObjectState = CentralArtifactObjectState.Pending,
            ReconstructionState = CentralReconstructionState.LegacyIncomplete,
            StateReasonCode = "object.missing"
        });
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CentralFrames.Add(frame);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using var response = await _client!.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var consistency = payload.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "artifact-consistency");
        Assert.AreEqual("Degraded", consistency.GetProperty("status").GetString());
        var data = consistency.GetProperty("data");
        Assert.AreEqual("stale-consistency-backlog", data.GetProperty("Condition").GetString());
        Assert.AreEqual(
            (long)CentralArtifactConsistencyHealthCheck.StaleAfter.TotalSeconds,
            data.GetProperty("ReconciliationWindowSeconds").GetInt64());
        var publicPayload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsFalse(publicPayload.Contains(artifactId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(data.TryGetProperty("ArtifactId", out _));
        Assert.IsFalse(data.TryGetProperty("ObjectState", out _));
        Assert.IsFalse(data.TryGetProperty("ReconstructionState", out _));
        Assert.IsFalse(data.TryGetProperty("ReasonCode", out _));
    }
}
