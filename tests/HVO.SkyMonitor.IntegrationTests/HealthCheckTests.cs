using System;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await ObservatoryLocationBackfill.RunAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                TimeProvider.System).ConfigureAwait(false);
        }
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
        var connection = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorStaleHealth_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.MigrateAsync().ConfigureAwait(false);
        try
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
                StateReasonCode = "object.pending-test"
            });
            db.CentralFrames.Add(frame);
            await db.SaveChangesAsync().ConfigureAwait(false);
            await db.CentralRecoveryCheckpoints.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Phase, CentralRecoveryPhases.Idle)
                .SetProperty(item => item.NextInventoryAtUtc, DateTimeOffset.UtcNow.AddDays(1)))
                .ConfigureAwait(false);

            var consistency = await new CentralArtifactConsistencyHealthCheck(
                db, TimeProvider.System, new CentralRecoveryStartupState(TimeProvider.System))
                .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

            Assert.AreEqual(HealthStatus.Degraded, consistency.Status);
            Assert.AreEqual("stale-consistency-backlog", consistency.Data["Condition"]);
            Assert.AreEqual(
                (long)CentralArtifactConsistencyHealthCheck.StaleAfter.TotalSeconds,
                consistency.Data["ReconciliationWindowSeconds"]);
            Assert.IsFalse(consistency.Data.ContainsKey("ArtifactId"));
            Assert.IsFalse(consistency.Data.ContainsKey("ObjectState"));
            Assert.IsFalse(consistency.Data.ContainsKey("ReconstructionState"));
            Assert.IsFalse(consistency.Data.ContainsKey("ReasonCode"));
            Assert.IsFalse(consistency.Description?.Contains(artifactId.ToString(), StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }
}
