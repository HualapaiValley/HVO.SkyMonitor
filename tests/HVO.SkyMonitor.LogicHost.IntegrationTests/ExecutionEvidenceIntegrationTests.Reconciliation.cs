using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ExecutionEvidenceIntegrationTests
{
    private static readonly string[] ExpectedAvailabilityTransitions = ["Missing", "Missing", "Available", "Missing"];
    [TestMethod]
    public async Task ObjectLateMissingAndMetadataMismatchAppendObservationsWithoutRewritingProducerFacts()
    {
        var setup = await PrepareReconciliationAsync().ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.AddDays(2);
        var clock = new FixedTimeProvider(now);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var store = services.GetRequiredService<IObjectStore>();
        var names = services.GetRequiredService<CentralObjectStorageNames>();
        var metadataOnly = new MetadataOnlyStore((bucket, key, token) => store.StatAsync(bucket, key, token));
        var reconciler = new ExecutionEvidenceReconciler(services.GetRequiredService<ExecutionEvidenceDatabase>(),
            services.GetRequiredService<ExecutionEvidenceService>(), metadataOnly, names, clock,
            services.GetRequiredService<IOptions<ExecutionEvidenceOptions>>(), services.GetRequiredService<ExecutionEvidenceTelemetry>());
        var before = await db.Set<ImportedExecutionEvidenceUnit>().AsNoTracking().SingleAsync(u => u.BindingId == setup.Device.BindingId && u.OriginSequence == 3).ConfigureAwait(false);
        await ScheduleOnlyReferenceAsync(db, setup.ReferenceId, now).ConfigureAwait(false);
        _ = await reconciler.ReconcileAsync(32).ConfigureAwait(false);
        var check = await CurrentCheckAsync(db, setup.ReferenceId).ConfigureAwait(false);
        Assert.AreEqual("Missing", check.State);
        Assert.AreEqual("evidence.artifact-not-received", check.ReasonCode);
        await SeedCentralArtifactAsync(db, setup, names).ConfigureAwait(false);
        await ScheduleOnlyReferenceAsync(db, setup.ReferenceId, now).ConfigureAwait(false);
        _ = await reconciler.ReconcileAsync(32).ConfigureAwait(false);
        Assert.AreEqual("evidence.object-missing", (await CurrentCheckAsync(db, setup.ReferenceId).ConfigureAwait(false)).ReasonCode);
        await using (var bytes = new MemoryStream(setup.Payload))
        {
            await store.PutAsync(names.ArtifactBucket, setup.ObjectKey, bytes, bytes.Length, "application/octet-stream", CancellationToken.None).ConfigureAwait(false);
        }
        await ScheduleOnlyReferenceAsync(db, setup.ReferenceId, now).ConfigureAwait(false);
        _ = await reconciler.ReconcileAsync(32).ConfigureAwait(false);
        Assert.AreEqual("Available", (await CurrentCheckAsync(db, setup.ReferenceId).ConfigureAwait(false)).State);
        await store.DeleteAsync(names.ArtifactBucket, setup.ObjectKey, CancellationToken.None).ConfigureAwait(false);
        await ScheduleOnlyReferenceAsync(db, setup.ReferenceId, now).ConfigureAwait(false);
        _ = await reconciler.ReconcileAsync(32).ConfigureAwait(false);
        Assert.AreEqual("Missing", (await CurrentCheckAsync(db, setup.ReferenceId).ConfigureAwait(false)).State);
        var after = await db.Set<ImportedExecutionEvidenceUnit>().AsNoTracking().SingleAsync(u => u.Id == before.Id).ConfigureAwait(false);
        CollectionAssert.AreEqual(before.CanonicalBytes, after.CanonicalBytes);
        Assert.AreEqual(before.ReceivedAtUtc, after.ReceivedAtUtc);
        var states = await db.Set<ImportedExecutionEvidenceObservation>().Where(o => o.ArtifactReferenceId == setup.ReferenceId)
            .OrderBy(o => o.Id).Select(o => o.State).ToListAsync().ConfigureAwait(false);
        CollectionAssert.AreEqual(ExpectedAvailabilityTransitions, states.TakeLast(4).ToArray());
        Assert.IsTrue(metadataOnly.StatCalls >= 3);
        // The optional supplied metadata is checked before touching object storage.
        await db.CentralArtifacts.Where(a => a.DevicePublicId == setup.Device.DevicePublicId && a.ArtifactId == setup.ArtifactId)
            .ExecuteUpdateAsync(update => update.SetProperty(a => a.ChecksumSha256, new string('B', 64))).ConfigureAwait(false);
        await ScheduleOnlyReferenceAsync(db, setup.ReferenceId, now).ConfigureAwait(false);
        _ = await reconciler.ReconcileAsync(32).ConfigureAwait(false);
        Assert.AreEqual("Quarantined", (await CurrentCheckAsync(db, setup.ReferenceId).ConfigureAwait(false)).State);
    }

    [TestMethod]
    public async Task ConcurrentMetadataPassCannotPublishAnOlderGenerationAfterTheWinningPass()
    {
        var setup = await PrepareReconciliationAsync().ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.AddDays(2);
        await using var setupScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var names = setupScope.ServiceProvider.GetRequiredService<CentralObjectStorageNames>();
        await SeedCentralArtifactAsync(db, setup, names).ConfigureAwait(false);
        await ScheduleOnlyReferenceAsync(db, setup.ReferenceId, now).ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStore = new MetadataOnlyStore(async (_, key, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return new(key, setup.Payload.Length, "application/octet-stream", "older-generation", now);
        });
        var fastStore = new MetadataOnlyStore((_, key, _) => Task.FromResult(new ObjectStoreObjectMetadata(
            key, setup.Payload.Length, "application/octet-stream", "newer-generation", now)));
        await using var olderScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        await using var newerScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        ExecutionEvidenceReconciler Reconciler(IServiceProvider services, IObjectStore store)
            => new(services.GetRequiredService<ExecutionEvidenceDatabase>(), services.GetRequiredService<ExecutionEvidenceService>(), store,
                names, new FixedTimeProvider(now), services.GetRequiredService<IOptions<ExecutionEvidenceOptions>>(), services.GetRequiredService<ExecutionEvidenceTelemetry>());
        var oldPass = Reconciler(olderScope.ServiceProvider, slowStore).ReconcileAsync(1);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.AreEqual(1, await Reconciler(newerScope.ServiceProvider, fastStore).ReconcileAsync(1).ConfigureAwait(false));
        }
        finally { release.TrySetResult(); }
        _ = await oldPass.ConfigureAwait(false);
        var current = await CurrentCheckAsync(db, setup.ReferenceId).ConfigureAwait(false);
        Assert.AreEqual("newer-generation", current.ObjectGeneration);
        Assert.IsFalse(await db.Set<ImportedExecutionEvidenceObservation>().AnyAsync(o => o.ArtifactReferenceId == setup.ReferenceId &&
            o.ObjectGeneration == "older-generation").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ObservationPressureRetainsLastObservationAndCanonicalHistory()
    {
        var setup = await PrepareReconciliationAsync().ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.AddDays(2);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var options = Options.Create(new ExecutionEvidenceOptions());
        var store = new MetadataOnlyStore((_, _, _) => throw new InvalidOperationException("An unresolved artifact must not stat bytes."));
        var reconciler = new ExecutionEvidenceReconciler(services.GetRequiredService<ExecutionEvidenceDatabase>(), services.GetRequiredService<ExecutionEvidenceService>(),
            store, services.GetRequiredService<CentralObjectStorageNames>(), new FixedTimeProvider(now), options, services.GetRequiredService<ExecutionEvidenceTelemetry>());
        await ScheduleOnlyReferenceAsync(db, setup.ReferenceId, now).ConfigureAwait(false);
        _ = await reconciler.ReconcileAsync(1).ConfigureAwait(false);
        var usageBefore = await db.Set<ExecutionEvidenceBindingUsage>().AsNoTracking().SingleAsync(u => u.BindingId == setup.Device.BindingId).ConfigureAwait(false);
        options.Value.MaximumObservationsPerBinding = usageBefore.ObservationCount;
        await SeedCentralArtifactAsync(db, setup, services.GetRequiredService<CentralObjectStorageNames>()).ConfigureAwait(false);
        var observedStore = new MetadataOnlyStore((_, key, _) => Task.FromResult(new ObjectStoreObjectMetadata(key, setup.Payload.Length,
            "application/octet-stream", "late-generation", now)));
        var second = new ExecutionEvidenceReconciler(services.GetRequiredService<ExecutionEvidenceDatabase>(), services.GetRequiredService<ExecutionEvidenceService>(),
            observedStore, services.GetRequiredService<CentralObjectStorageNames>(), new FixedTimeProvider(now), options, services.GetRequiredService<ExecutionEvidenceTelemetry>());
        await ScheduleOnlyReferenceAsync(db, setup.ReferenceId, now).ConfigureAwait(false);
        _ = await second.ReconcileAsync(1).ConfigureAwait(false);
        Assert.AreEqual("Missing", (await CurrentCheckAsync(db, setup.ReferenceId).ConfigureAwait(false)).State);
        Assert.AreEqual(usageBefore.ObservationCount, await db.Set<ImportedExecutionEvidenceObservation>().CountAsync(o => o.BindingId == setup.Device.BindingId).ConfigureAwait(false));
        Assert.AreEqual(3, await db.Set<ImportedExecutionEvidenceUnit>().CountAsync(u => u.BindingId == setup.Device.BindingId && u.Accepted).ConfigureAwait(false));
    }

    private sealed record ReconciliationSetup(Device Device, long ReferenceId, Guid ArtifactId, Guid CaptureId, string ObjectKey, byte[] Payload);
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static async Task<ReconciliationSetup> PrepareReconciliationAsync()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        using var revision = await PostAsync(client, device, Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device)).ConfigureAwait(false);
        _ = await FeedbackAsync(revision).ConfigureAwait(false);
        var execution = GraphExecutionEvidenceJson.Seal(Reorigin(await FixtureAsync("execution-live").ConfigureAwait(false), device) with { OriginSequence = 2 });
        using var executionResponse = await PostAsync(client, device, execution).ConfigureAwait(false);
        _ = await FeedbackAsync(executionResponse).ConfigureAwait(false);
        var payload = new byte[] { 1, 2, 3, 4 };
        var artifactId = Guid.NewGuid();
        var reference = new ExecutionEvidenceArtifactReferenceV1(ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion, artifactId,
            execution.Execution!.CaptureId, Role: FrameArtifactRole.Raw, Variant: "raw", PayloadSha256: Convert.ToHexString(SHA256.HashData(payload)),
            PayloadLength: payload.Length, MediaType: "application/octet-stream");
        var report = GraphExecutionEvidenceJson.Seal(Reorigin(await FixtureAsync("availability").ConfigureAwait(false), device) with
        {
            OriginSequence = 3,
            Availability = new(ArtifactAvailabilityReportV1.CurrentSchemaVersion, execution.Execution.ExecutionId,
                [new(ArtifactAvailabilityObservationV1.CurrentSchemaVersion, reference, ExecutionEvidenceAvailabilityState.Available, Epoch)])
        });
        using var availability = await PostAsync(client, device, report).ConfigureAwait(false);
        _ = await FeedbackAsync(availability).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var id = await db.Set<ImportedExecutionEvidenceArtifact>().Where(r => r.BindingId == device.BindingId && r.ArtifactId == artifactId)
            .Select(r => r.Id).SingleAsync().ConfigureAwait(false);
        return new(device, id, artifactId, reference.CaptureId, $"evidence-tests/{Guid.NewGuid():N}", payload);
    }

    private static async Task SeedCentralArtifactAsync(ApplicationDbContext db, ReconciliationSetup setup, CentralObjectStorageNames names)
    {
        var frame = new CentralFrame
        {
            RegistrationId = setup.Device.RegistrationId,
            LogicalCameraInstallationId = setup.Device.InstallationId,
            DevicePublicId = setup.Device.DevicePublicId,
            ObservatoryId = setup.Device.ObservatoryId,
            AgentId = setup.Device.DeviceId,
            FrameId = setup.CaptureId,
            CapturedAtUtc = Epoch,
            FirstReceivedAtUtc = Epoch
        };
        db.CentralFrames.Add(frame);
        db.CentralArtifacts.Add(new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            ArtifactId = setup.ArtifactId,
            DevicePublicId = setup.Device.DevicePublicId,
            Role = FrameArtifactRole.Raw,
            Variant = "raw",
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "application/octet-stream",
            ByteLength = setup.Payload.Length,
            ChecksumSha256 = Convert.ToHexString(SHA256.HashData(setup.Payload)),
            StorageReference = names.ArtifactPrefix + setup.ObjectKey,
            ReceivedAtUtc = Epoch,
            IdempotencyKey = Guid.NewGuid().ToString("N")
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task ScheduleOnlyReferenceAsync(ApplicationDbContext db, long referenceId, DateTimeOffset now)
    {
        await db.Set<ExecutionEvidenceArtifactCheck>().ExecuteUpdateAsync(update => update.SetProperty(c => c.NextCheckAtUtc, now.AddDays(10))).ConfigureAwait(false);
        var version = Guid.NewGuid();
        await db.Set<ExecutionEvidenceArtifactCheck>().Where(c => c.ArtifactReferenceId == referenceId)
            .ExecuteUpdateAsync(update => update.SetProperty(c => c.NextCheckAtUtc, now).SetProperty(c => c.Version, version)).ConfigureAwait(false);
    }

    private static Task<ExecutionEvidenceArtifactCheck> CurrentCheckAsync(ApplicationDbContext db, long id)
        => db.Set<ExecutionEvidenceArtifactCheck>().AsNoTracking().SingleAsync(c => c.ArtifactReferenceId == id);

    private sealed class MetadataOnlyStore(Func<string, string, CancellationToken, Task<ObjectStoreObjectMetadata>> stat) : IObjectStore
    {
        public int StatCalls { get; private set; }
        public Task<ObjectStoreObjectMetadata> StatAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            StatCalls++;
            return stat(bucket, key, cancellationToken);
        }
        public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task PutAsync(string bucket, string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task ReadAsync(string bucket, string key, string? generation, Func<Stream, CancellationToken, Task> reader, CancellationToken cancellationToken) => throw new InvalidOperationException("Evidence reconciliation must never read image bytes.");
        public Task CopyAsync(string bucket, string sourceKey, string destinationKey, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public IAsyncEnumerable<ObjectStoreItem> ListAsync(string bucket, string prefix, CancellationToken cancellationToken, string? startAfter = null) => throw new InvalidOperationException();
    }
}
