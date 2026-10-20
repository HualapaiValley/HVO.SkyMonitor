using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalWarmHandoffTests : OrbitalStoreTestBase
{
    [TestMethod]
    public async Task PublishedAndReacquiredManifestIdentitiesArePreparedInTheSharedReader()
    {
        var host = await OrbitalCompositionHost.StartAsync(CreateRoot(), new(OrbitalElementsStoreTestData.Start)).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var definition = await host.SaveDefinitionAsync().ConfigureAwait(false);
        host.AdvanceTo(definition.NextRunUtc!.Value);
        Assert.IsTrue(host.Provider.GetRequiredService<ILocalAutomationTaskRegistry>().RunsOffSweep(definition.Definition));
        var reader = host.Provider.GetRequiredService<OrbitalSnapshotReader>();
        Assert.AreSame(reader, host.Reader);
        Assert.AreEqual(0, reader.LoadCount);
        host.Handler.EnqueueBody(OrbitalRefreshTestHarness.ValidPayload());
        await host.SweepAsync().ConfigureAwait(false);
        Assert.AreEqual(1, reader.LoadCount, "Always prepare, even with no consumer since startup.");
        var first = await host.Reader.ReadCurrentAsync(default).ConfigureAwait(false);
        Assert.AreEqual(1, reader.LoadCount);
        host.Clock.Advance(SqliteOrbitalElementsStore.MinimumProviderAttemptInterval);
        host.Handler.EnqueueBody(OrbitalRefreshTestHarness.ValidPayload());
        await host.SweepAsync().ConfigureAwait(false);
        Assert.AreEqual(2, reader.LoadCount);
        var reacquired = await host.Reader.ReadCurrentAsync(default).ConfigureAwait(false);
        Assert.AreNotEqual(first.Snapshot!.Manifest.ManifestSha256, reacquired.Snapshot!.Manifest.ManifestSha256);
        Assert.AreEqual(first.Snapshot.Manifest.NormalizedPayloadSha256, reacquired.Snapshot.Manifest.NormalizedPayloadSha256);
        Assert.AreEqual(2, reader.LoadCount, "Both first consumer reads are cache hits.");
        Assert.HasCount(2, reader.CachedIdentities());
    }

    [TestMethod]
    public async Task PreparationFailurePreservesDurableActivationAndPriorCache()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var prior = OrbitalElementsStoreTestData.Build("prior");
        var admitted = await OrbitalElementsStoreTestData.AdmitAsync(harness.Store).ConfigureAwait(false);
        await harness.Store.PublishAndActivateAsync(admitted, prior.Snapshot, prior.Raw, default).ConfigureAwait(false);
        var logger = new OrbitalTestLogger<OrbitalSnapshotReader>();
        var reader = new OrbitalSnapshotReader(harness.Store, true, logger);
        Assert.IsTrue(await reader.PrepareCurrentAsync(default).ConfigureAwait(false));
        var cached = (await reader.ReadCurrentAsync(default).ConfigureAwait(false)).Snapshot;
        harness.Clock.Advance(SqliteOrbitalElementsStore.MinimumProviderAttemptInterval);
        harness.Intercept.RetentionFault = _ =>
        {
            // Corrupt the new committed bytes after activation, before preparation.
            var payload = (string)OrbitalElementsStoreTestData.Scalar(harness.Root,
                "SELECT payload_sha256 FROM orbital_snapshots WHERE manifest_sha256 = (SELECT active_manifest_sha256 FROM orbital_provider_state WHERE provider = 'celestrak');")!;
            File.WriteAllText(harness.Store.PayloadPath(payload), "damaged");
            return null;
        };
        var job = new OrbitalElementsRefreshJob(harness.Intercept, harness.Client, harness.Options, harness.Clock,
            harness.Telemetry, harness.Logger, reader);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload(60003)));
        var result = await job.ExecuteAsync("prepare-failure", OrbitalAttemptTrigger.Scheduled, harness.Clock.GetUtcNow(), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, result.Outcome);
        StringAssert.Contains(result.Detail, "orbital.preparation-unavailable", StringComparison.Ordinal);
        Assert.AreNotEqual(prior.Manifest, harness.Store.ActiveManifestSha256);
        Assert.AreEqual(OrbitalAttemptOutcome.Activated, (await harness.StateAsync().ConfigureAwait(false)).LastAttemptOutcome);
        Assert.AreSame(cached, (await reader.ReadExactAsync(prior.Manifest, default).ConfigureAwait(false)).Snapshot);
        Assert.IsTrue(logger.EventIds.Contains(7724));
    }

    [TestMethod]
    public async Task StartupPreparesPersistedActiveSnapshot()
    {
        var root = CreateRoot();
        string identity;
        var host = await OrbitalCompositionHost.StartAsync(root, new(OrbitalElementsStoreTestData.Start)).ConfigureAwait(false);
        await using (host.ConfigureAwait(false))
        {
            var definition = await host.SaveDefinitionAsync().ConfigureAwait(false);
            host.AdvanceTo(definition.NextRunUtc!.Value);
            host.Handler.EnqueueBody(OrbitalRefreshTestHarness.ValidPayload());
            await host.SweepAsync().ConfigureAwait(false);
            identity = host.Store.ActiveManifestSha256!;
        }
        var restarted = await OrbitalCompositionHost.StartAsync(root,
            new(OrbitalElementsStoreTestData.Start.AddDays(1)), enabled: false).ConfigureAwait(false);
        await using var lifetime = restarted.ConfigureAwait(false);
        var reader = restarted.Provider.GetRequiredService<OrbitalSnapshotReader>();
        Assert.AreEqual(1, reader.LoadCount, "Initializer prepares persisted state before any consumer, even with refresh disabled.");
        Assert.AreEqual(identity, (await reader.ReadCurrentAsync(default).ConfigureAwait(false)).Snapshot!.Manifest.ManifestSha256);
        Assert.AreEqual(1, reader.LoadCount);
        Assert.AreEqual(0, restarted.Handler.Count);
    }

    [TestMethod]
    public async Task ConcurrentInitializationFailuresBackOffAndResetAfterRecovery()
    {
        var root = CreateRoot();
        var clock = new InitializationClock();
        using var owner = OrbitalElementsStoreTestData.Open(root, clock);
        await owner.InitializeAsync(default).ConfigureAwait(false);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var reader = new OrbitalSnapshotReader(store, true, NullLogger<OrbitalSnapshotReader>.Instance);
        foreach (var delay in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            var first = await Assert.ThrowsExactlyAsync<OrbitalStorageException>(() => store.InitializeAsync(default).AsTask()).ConfigureAwait(false);
            var failures = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
                await Assert.ThrowsExactlyAsync<OrbitalStorageException>(() => store.EnsureReadyAsync(default).AsTask()).ConfigureAwait(false))).ConfigureAwait(false);
            Assert.IsTrue(failures.All(failure => ReferenceEquals(first, failure)), "One failed initialization is shared by concurrent entry points.");
            Assert.AreEqual(OrbitalSnapshotUnavailableReason.StorageUnavailable,
                (await reader.ReadCurrentAsync(default).ConfigureAwait(false)).UnavailableReason);
            clock.Advance(TimeSpan.FromSeconds(delay).Subtract(TimeSpan.FromTicks(1)));
            Assert.AreSame(first, await Assert.ThrowsExactlyAsync<OrbitalStorageException>(() => store.GetStateAsync(default).AsTask()).ConfigureAwait(false));
            clock.Advance(TimeSpan.FromTicks(1));
        }
        owner.Dispose();
        await store.InitializeAsync(default).ConfigureAwait(false);
        Assert.IsTrue(store.IsInitialized);
        // A successful initialization clears the shared failure window entirely.
        Assert.AreEqual(OrbitalSnapshotUnavailableReason.NoActiveSnapshot,
            (await reader.ReadCurrentAsync(default).ConfigureAwait(false)).UnavailableReason);
        await store.EnsureReadyAsync(default).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CancellationAndClockRollbackCannotPoisonInitializationRecovery()
    {
        var root = CreateRoot();
        var clock = new InitializationClock();
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        using var clean = OrbitalElementsStoreTestData.Open(CreateRoot(), clock);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.InitializeAsync(cancelled.Token).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => clean.InitializeAsync(cancelled.Token).AsTask()).ConfigureAwait(false);
        await clean.InitializeAsync(default).ConfigureAwait(false);
        Assert.IsTrue(clean.IsInitialized, "Cancellation does not delay an immediate successful retry.");
        using var owner = OrbitalElementsStoreTestData.Open(root, clock);
        await owner.InitializeAsync(default).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<OrbitalStorageException>(() => store.InitializeAsync(default).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.EnsureReadyAsync(cancelled.Token).AsTask()).ConfigureAwait(false);
        owner.Dispose();
        clock.Advance(TimeSpan.FromDays(-1));
        await store.InitializeAsync(default).ConfigureAwait(false);
        Assert.IsTrue(store.IsInitialized, "A rollback expires the window rather than extending it beyond the cap.");

        var cappedRoot = CreateRoot();
        using var cappedOwner = OrbitalElementsStoreTestData.Open(cappedRoot, clock);
        await cappedOwner.InitializeAsync(default).ConfigureAwait(false);
        using var cappedStore = OrbitalElementsStoreTestData.Open(cappedRoot, clock);
        await Assert.ThrowsExactlyAsync<OrbitalStorageException>(() => cappedStore.InitializeAsync(default).AsTask()).ConfigureAwait(false);
        cappedOwner.Dispose();
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        await cappedStore.InitializeAsync(default).ConfigureAwait(false);
        Assert.IsTrue(cappedStore.IsInitialized, "Monotonic elapsed time expires the delay even when UTC is frozen.");
    }

    private sealed class InitializationClock : TimeProvider
    {
        private DateTimeOffset _utcNow = OrbitalElementsStoreTestData.Start;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan delta)
        {
            _utcNow += delta;
            AdvanceMonotonic(delta > TimeSpan.Zero ? delta : TimeSpan.Zero);
        }

        public void AdvanceMonotonic(TimeSpan delta) => _timestamp += delta.Ticks;
    }
}
