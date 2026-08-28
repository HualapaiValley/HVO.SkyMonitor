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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.TestHost;

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
    private static readonly string[] RetentionHealthDataKeys =
        ["Condition", "PendingCount", "PendingBytes", "PendingOldestAgeSeconds"];
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
        var connection = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorCatalogHealth_{Guid.NewGuid():N}"
        }.ConnectionString;
        await using (var setup = new ApplicationDbContext(
                         new DbContextOptionsBuilder<ApplicationDbContext>()
                             .UseSqlServer(connection)
                             .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                             .Options))
        {
            await setup.Database.MigrateAsync().ConfigureAwait(false);
        }
        var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<ApplicationDbContext>();
                services.AddDbContext<ApplicationDbContext>(options => options
                    .UseSqlServer(connection)
                    .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
            }));
        try
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
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
            Assert.AreEqual("hyg-v42-fixture", identity.GetProperty("CatalogId").GetString());
            Assert.AreEqual("explicit-manifest-v2", identity.GetProperty("CatalogIdentitySource").GetString());
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
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(false);
            SqlConnection.ClearAllPools();
            await using var db = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
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
    public async Task HealthEndpointExposesOnlyBoundedArtifactRetentionDataAsync()
    {
        var artifactId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var objectKey = $"health/retention/{Guid.NewGuid():N}.bin";
        Guid frameId;
        Guid dispositionId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTimeOffset.UtcNow;
            var frame = new CentralFrame
            {
                RegistrationId = Guid.NewGuid(),
                DevicePublicId = Guid.NewGuid(),
                ObservatoryId = Guid.NewGuid(),
                AgentId = $"health-retention-{Guid.NewGuid():N}",
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = now,
                FirstReceivedAtUtc = now
            };
            var artifact = new CentralArtifact
            {
                Frame = frame,
                CentralFrameId = frame.Id,
                DevicePublicId = frame.DevicePublicId,
                ArtifactId = artifactId,
                Role = FrameArtifactRole.Raw,
                RecipeVersion = "health-retention-v1",
                ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
                MediaType = "application/octet-stream",
                ByteLength = 73,
                ChecksumSha256 = new string('D', 64),
                StorageReference = $"minio://skymonitor-artifacts/{objectKey}",
                ReceivedAtUtc = now,
                IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                ObjectState = CentralArtifactObjectState.Expired,
                ReconstructionState = CentralReconstructionState.Complete,
                RetentionDeletionToken = token,
                RetentionDeletionRequestedAtUtc = now
            };
            var disposition = new CentralObjectRecoveryDisposition
            {
                SourceObjectIdentitySha256 = CentralObjectOwnershipFence.CreateObjectKeyIdentity(objectKey),
                SourceObjectKey = objectKey,
                Kind = CentralObjectRecoveryKinds.ExpiredDelete,
                State = CentralObjectRecoveryStates.PendingDelete,
                CentralArtifactId = artifact.Id,
                OperationToken = token,
                ByteLength = artifact.ByteLength,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                NextAttemptAtUtc = now
            };
            db.AddRange(frame, artifact, disposition);
            await db.SaveChangesAsync().ConfigureAwait(false);
            frameId = frame.Id;
            dispositionId = disposition.Id;
        }
        try
        {
            using var response = await _client!.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var payload = JsonDocument.Parse(json);
            var retention = payload.RootElement.GetProperty("checks").EnumerateArray()
                .Single(check => check.GetProperty("name").GetString() == "artifact-retention");
            var data = retention.GetProperty("data");
            CollectionAssert.AreEquivalent(
                RetentionHealthDataKeys,
                data.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.AreEqual("pending", data.GetProperty("Condition").GetString());
            Assert.IsTrue(data.GetProperty("PendingCount").GetInt64() >= 1);
            Assert.IsTrue(data.GetProperty("PendingBytes").GetInt64() >= 73);
            Assert.IsFalse(json.Contains(artifactId.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(json.Contains(token.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(json.Contains(objectKey, StringComparison.Ordinal));
        }
        finally
        {
            await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CentralObjectRecoveryDispositions.Where(item => item.Id == dispositionId)
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await db.CentralArtifacts.Where(item => item.ArtifactId == artifactId)
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await db.CentralFrames.Where(item => item.Id == frameId).ExecuteDeleteAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task HealthCheckUsesVerificationRequestAgeForReservedArtifactAsync()
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
                ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
                MediaType = "application/octet-stream",
                ByteLength = 4,
                ChecksumSha256 = new string('A', 64),
                StorageReference = "minio://skymonitor-artifacts/health/missing.bin",
                ReceivedAtUtc = DateTimeOffset.UtcNow - CentralArtifactConsistencyHealthCheck.StaleAfter - TimeSpan.FromMinutes(1),
                IdempotencyKey = idempotencyKey,
                ObjectState = CentralArtifactObjectState.Pending,
                ReconstructionState = CentralReconstructionState.PendingReference,
                StateReasonCode = "object.pending-test",
                ObjectVerificationToken = Guid.NewGuid(),
                ObjectVerificationRequestedAtUtc = DateTimeOffset.UtcNow
            });
            db.CentralFrames.Add(frame);
            await db.SaveChangesAsync().ConfigureAwait(false);
            await db.CentralRecoveryCheckpoints.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Phase, CentralRecoveryPhases.Idle)
                .SetProperty(item => item.NextInventoryAtUtc, DateTimeOffset.UtcNow.AddDays(1)))
                .ConfigureAwait(false);

            var healthCheck = new CentralArtifactConsistencyHealthCheck(
                db, TimeProvider.System, new CentralRecoveryStartupState(TimeProvider.System));
            var fresh = await healthCheck.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
            Assert.AreEqual(HealthStatus.Healthy, fresh.Status);

            await db.CentralArtifacts.Where(item => item.ArtifactId == artifactId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    item => item.ObjectVerificationRequestedAtUtc,
                    DateTimeOffset.UtcNow - CentralArtifactConsistencyHealthCheck.StaleAfter - TimeSpan.FromMinutes(1)))
                .ConfigureAwait(false);
            var consistency = await healthCheck
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
