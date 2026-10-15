using System.Net;
using System.Net.Http.Json;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ExecutionEvidenceIntegrationTests
{
    [TestMethod]
    public async Task OwnerBindingAndReadsEnforceMembershipScopeAndKeysetBounds()
    {
        var device = await SeedAsync(createBinding: false).ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        var writeToken = await HttpHelpers.GetPasswordTokenAsync(client, "/connect/token", TestUsers.Operator.Username,
            TestUsers.Operator.Password, TestClients.WebUI.ClientId, "api.owner.write").ConfigureAwait(false);
        using var owner = HttpHelpers.WithBearerToken(client, writeToken.AccessToken);
        using var created = await owner.PostAsJsonAsync("/api/internal/execution-evidence/bindings", new ExecutionEvidenceBindingRequest(
            device.ObservatoryId, device.RegistrationId, device.InstallationId, device.Origin.OriginInstallationId, device.Origin.AgentInstanceId)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync().ConfigureAwait(false));
        var binding = await created.Content.ReadFromJsonAsync<ExecutionEvidenceBindingView>().ConfigureAwait(false);
        Assert.IsNotNull(binding);
        device = device with { BindingId = binding.Id };
        var unit = Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device);
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            using var accepted = await PostAsync(client, device, GraphExecutionEvidenceJson.Seal(unit with { OriginSequence = sequence, EvidenceId = Guid.NewGuid() })).ConfigureAwait(false);
            _ = await FeedbackAsync(accepted).ConfigureAwait(false);
        }
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>();
        var first = await service.ListUnitsAsync(device.BindingId, device.OwnerId, device.ObservatoryId, take: 2).ConfigureAwait(false);
        Assert.AreEqual(2, first!.Count);
        var second = await service.ListUnitsAsync(device.BindingId, device.OwnerId, device.ObservatoryId, afterId: first[^1].Id, take: 2).ConfigureAwait(false);
        Assert.AreEqual(1, second!.Count);
        Assert.IsTrue(second[0].Id > first[^1].Id);
        var bytes = await service.GetUnitAsync(device.BindingId, first[0].Id, device.OwnerId, null).ConfigureAwait(false);
        Assert.IsTrue(GraphExecutionEvidenceJson.ParseEnvelope(bytes!).Validation.IsValid);
        var receipts = await service.GetFeedbackAsync(device.BindingId, unit.Origin.IdentitySha256, device.OwnerId, null, take: 2).ConfigureAwait(false);
        Assert.AreEqual(8, receipts!.Facts.Length);
        var viewerId = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.Where(u => u.Email == TestUsers.Viewer.Email)
            .Select(u => u.Id).SingleAsync().ConfigureAwait(false);
        Assert.IsNull(await service.GetBindingAsync(device.BindingId, viewerId, null).ConfigureAwait(false));
        Assert.IsNull(await service.GetUnitAsync(device.BindingId, first[0].Id, device.OwnerId, Guid.NewGuid()).ConfigureAwait(false));
        Assert.IsNull(await service.CreateBindingAsync(new(device.ObservatoryId, device.RegistrationId, device.InstallationId,
            Guid.NewGuid(), Guid.NewGuid()), viewerId, null).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<ExecutionEvidenceInvalidException>(() => service.ListUnitsAsync(device.BindingId, device.OwnerId, null, take: 101)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ExecutionEvidenceInvalidException>(() => service.GetFeedbackAsync(device.BindingId, unit.Origin.IdentitySha256,
            device.OwnerId, null, take: 65)).ConfigureAwait(false);
        var readToken = await HttpHelpers.GetPasswordTokenAsync(client, "/connect/token", TestUsers.Viewer.Username,
            TestUsers.Viewer.Password, TestClients.WebUI.ClientId, "api.viewer").ConfigureAwait(false);
        using var other = HttpHelpers.WithBearerToken(client, readToken.AccessToken);
        using var denied = await other.GetAsync(new Uri($"/api/internal/execution-evidence/bindings/{device.BindingId}", UriKind.Relative)).ConfigureAwait(false);
        using var absent = await other.GetAsync(new Uri($"/api/internal/execution-evidence/bindings/{Guid.NewGuid()}", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.AreEqual(absent.StatusCode, denied.StatusCode);
    }

    [TestMethod]
    public async Task UnitOriginByteAndConflictPressurePreserveAllPreviouslyAcknowledgedEvidence()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var envelope = Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device);
        var limits = new ExecutionEvidenceOptions { MaximumUnitsPerBinding = 1, MaximumOriginsPerBinding = 1, MaximumConflictsPerBinding = 1 };
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var appDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var database = new ExecutionEvidenceDatabase(appDb, Options.Create(limits));
        var service = new ExecutionEvidenceService(database, Options.Create(limits), TimeProvider.System,
            scope.ServiceProvider.GetRequiredService<ExecutionEvidenceTelemetry>());
        async Task<ExecutionEvidenceFeedbackV1> Send(ExecutionEvidenceEnvelopeV1 unit)
            => await service.ReceiveAsync(device.DeviceId, device.DeviceKey, unit.Origin.IdentitySha256,
                new ReadOnlyMemory<byte>[] { GraphExecutionEvidenceJson.Serialize(unit) }).ConfigureAwait(false);
        var accepted = await Send(envelope).ConfigureAwait(false);
        Assert.AreEqual(4, accepted.Facts.Length);
        await Assert.ThrowsExactlyAsync<ExecutionEvidencePressureException>(() => Send(GraphExecutionEvidenceJson.Seal(envelope with { OriginSequence = 2, EvidenceId = Guid.NewGuid() }))).ConfigureAwait(false);
        var origin = GraphExecutionEvidenceJson.BindIdentity(device.Origin with { BootSessionId = Guid.NewGuid() });
        await Assert.ThrowsExactlyAsync<ExecutionEvidencePressureException>(() => Send(GraphExecutionEvidenceJson.Seal(envelope with { Origin = origin }))).ConfigureAwait(false);
        var conflict = GraphExecutionEvidenceJson.Seal(envelope with { EvidenceId = Guid.NewGuid() });
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.SequenceConflict, (await Send(conflict).ConfigureAwait(false)).Facts.Single().ReasonCode);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.SequenceConflict, (await Send(conflict).ConfigureAwait(false)).Facts.Single().ReasonCode);
        await Assert.ThrowsExactlyAsync<ExecutionEvidencePressureException>(() => Send(GraphExecutionEvidenceJson.Seal(envelope with { EvidenceId = Guid.NewGuid() }))).ConfigureAwait(false);
        Assert.IsTrue((await Send(envelope).ConfigureAwait(false)).Facts.All(f => f.Duplicate));
        Assert.AreEqual(1, await appDb.Set<ImportedExecutionEvidenceUnit>().CountAsync(u => u.BindingId == device.BindingId).ConfigureAwait(false));
        Assert.AreEqual(1, await appDb.Set<ImportedExecutionEvidenceOrigin>().CountAsync(o => o.BindingId == device.BindingId).ConfigureAwait(false));
        var byteDevice = await SeedAsync().ConfigureAwait(false);
        limits.MaximumStoredBytesPerBinding = 1;
        var byteEnvelope = Reorigin(envelope, byteDevice);
        await Assert.ThrowsExactlyAsync<ExecutionEvidencePressureException>(() => service.ReceiveAsync(byteDevice.DeviceId, byteDevice.DeviceKey,
            byteEnvelope.Origin.IdentitySha256, new ReadOnlyMemory<byte>[] { GraphExecutionEvidenceJson.Serialize(byteEnvelope) })).ConfigureAwait(false);
        Assert.IsFalse(await appDb.Set<ImportedExecutionEvidenceOrigin>().AnyAsync(o => o.BindingId == byteDevice.BindingId).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SqlRollbackCannotProduceAcknowledgementsAndRetryCommitsExactlyOnce()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var unit = GraphExecutionEvidenceJson.Seal(Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device) with
        { EvidenceId = Guid.Parse("8a273b7b-b026-42a5-83a0-225da4098855") });
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER [TR_Test_ExecutionEvidenceRollback] ON [ImportedExecutionEvidenceUnits] AFTER INSERT AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted WHERE EvidenceId = '8a273b7b-b026-42a5-83a0-225da4098855')
                    THROW 51938, 'Injected evidence transaction failure.', 1;
            END
            """).ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        try
        {
            using var failed = await PostAsync(client, device, unit).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.IsNotNull(failed.Headers.RetryAfter);
            Assert.IsFalse(await db.Set<ImportedExecutionEvidenceUnit>().AnyAsync(u => u.BindingId == device.BindingId).ConfigureAwait(false));
            Assert.IsFalse(await db.Set<ImportedExecutionEvidenceOrigin>().AnyAsync(o => o.BindingId == device.BindingId).ConfigureAwait(false));
            var usage = await db.Set<ExecutionEvidenceBindingUsage>().AsNoTracking().SingleAsync(u => u.BindingId == device.BindingId).ConfigureAwait(false);
            Assert.AreEqual(0, usage.UnitCount);
            Assert.AreEqual(0, usage.StoredBytes);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [TR_Test_ExecutionEvidenceRollback]").ConfigureAwait(false); }
        using var retry = await PostAsync(client, device, unit).ConfigureAwait(false);
        Assert.AreEqual(4, (await FeedbackAsync(retry).ConfigureAwait(false)).Facts.Length);
    }

    [TestMethod]
    public async Task DatabaseGuardsProtectBindingsCanonicalUnitsOriginProvenanceAndAudit()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var unit = Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        using var accepted = await PostAsync(client, device, unit).ConfigureAwait(false);
        _ = await FeedbackAsync(accepted).ConfigureAwait(false);
        using var conflict = await PostAsync(client, device, GraphExecutionEvidenceJson.Seal(unit with { EvidenceId = Guid.NewGuid() })).ConfigureAwait(false);
        _ = await FeedbackAsync(conflict).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await Assert.ThrowsExactlyAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [ExecutionEvidenceBindings] SET [AgentInstanceId] = NEWID() WHERE [Id] = {device.BindingId}")).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [ImportedExecutionEvidenceUnits] SET [CanonicalBytes] = 0x00 WHERE [BindingId] = {device.BindingId}")).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [ImportedExecutionEvidenceOrigins] SET [SoftwareVersion] = 'changed' WHERE [BindingId] = {device.BindingId}")).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [ImportedExecutionEvidenceConflicts] SET [SubmittedPayloadSha256] = REPLICATE('B', 64) WHERE [BindingId] = {device.BindingId}")).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [ImportedExecutionEvidenceUnits] WHERE [BindingId] = {device.BindingId}")).ConfigureAwait(false);
        var otherBinding = await SeedAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [ImportedExecutionEvidenceUnits]
                ([BindingId], [OriginId], [OriginSequence], [EvidenceId], [PayloadSha256], [Kind], [ProducedAtUtc], [ReceivedAtUtc], [CanonicalBytes], [Accepted])
            SELECT {otherBinding.BindingId}, [OriginId], [OriginSequence] + 1, NEWID(), [PayloadSha256], [Kind], [ProducedAtUtc], [ReceivedAtUtc], [CanonicalBytes], 1
            FROM [ImportedExecutionEvidenceUnits] WHERE [BindingId] = {device.BindingId}
            """)).ConfigureAwait(false);
        Assert.IsFalse(db.Database.HasPendingModelChanges());
    }

    [TestMethod]
    public async Task SqlOutageInIsolatedEvidencePoolCannotAcknowledgeOrAffectTheMainContext()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var unit = Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        using var initial = await PostAsync(client, device, unit).ConfigureAwait(false);
        var accepted = await FeedbackAsync(initial).ConfigureAwait(false);
        await using var unavailableContext = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=EvidenceUnavailable;User Id=unavailable;Password=Unavailable_test1!;TrustServerCertificate=True;Connect Timeout=1").Options);
        var options = Options.Create(new ExecutionEvidenceOptions { SqlTimeoutSeconds = 1 });
        await using var evidenceDatabase = new ExecutionEvidenceDatabase(unavailableContext, options);
        using var telemetry = new ExecutionEvidenceTelemetry();
        var unavailable = new ExecutionEvidenceService(evidenceDatabase, options, TimeProvider.System, telemetry);
        await Assert.ThrowsExactlyAsync<SqlException>(() => unavailable.ReceiveAsync(device.DeviceId, device.DeviceKey,
            unit.Origin.IdentitySha256, new ReadOnlyMemory<byte>[] { GraphExecutionEvidenceJson.Serialize(unit) })).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().DeviceRegistrations
            .AnyAsync(r => r.Id == device.RegistrationId).ConfigureAwait(false));
        using var retried = await PostAsync(client, device, unit).ConfigureAwait(false);
        var duplicate = await FeedbackAsync(retried).ConfigureAwait(false);
        Assert.AreEqual(accepted.Facts[^1].OccurredAtUtc, duplicate.Facts[^1].OccurredAtUtc);
        Assert.IsTrue(duplicate.Facts.All(f => f.Duplicate));
    }

    [TestMethod]
    public async Task ReceiverCapacityRejectsImmediatelyAndDoesNotConsumeAnotherSqlConnection()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var unit = Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device);
        var gate = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<ExecutionEvidenceCapacity>();
        Assert.IsTrue(gate.TryEnter());
        Assert.IsTrue(gate.TryEnter());
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        try
        {
            using var unavailable = await PostAsync(client, device, unit).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.IsNotNull(unavailable.Headers.RetryAfter);
        }
        finally { gate.Exit(); gate.Exit(); }
        using var accepted = await PostAsync(client, device, unit).ConfigureAwait(false);
        Assert.AreEqual(4, (await FeedbackAsync(accepted).ConfigureAwait(false)).Facts.Length);
    }
}
