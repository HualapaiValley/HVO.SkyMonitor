using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalElementsAvailabilityTests : OrbitalStoreTestBase
{
    private static readonly int[] UnavailableOnce = [7730];
    private static readonly int[] UnavailableThenRecovered = [7730, 7731];

    [TestMethod]
    public async Task AvailabilityIsUnknownUntilProbedThenAvailable()
    {
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(CreateRoot(), clock);
        var logger = new OrbitalTestLogger<OrbitalElementsAvailability>();
        var availability = new OrbitalElementsAvailability(store, logger);

        Assert.AreEqual(OrbitalElementsAvailabilityState.NotInitialized, availability.State);
        Assert.IsFalse(availability.IsAvailable);
        Assert.AreEqual(OrbitalElementsAvailability.StoreUnavailableReasonCode, availability.ReasonCode);
        Assert.IsFalse(availability.TryGetPhaseOffset(out _));

        var state = await availability.EnsureAvailableAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsAvailabilityState.Available, state);
        Assert.AreEqual(OrbitalElementsAvailabilityState.Available, availability.State);
        Assert.IsTrue(availability.IsAvailable);
        Assert.IsNull(availability.ReasonCode);
        Assert.IsTrue(availability.TryGetPhaseOffset(out var offset));
        Assert.AreEqual(store.PhaseOffset, offset);
        Assert.AreEqual(0, logger.Entries.Count, "A first successful probe is not a recovery.");
    }

    [TestMethod]
    public async Task ALockedRootIsReportedOnceAndRecoversWithoutARestart()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var owner = OrbitalElementsStoreTestData.Open(root, clock);
        await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var logger = new OrbitalTestLogger<OrbitalElementsAvailability>();
        var availability = new OrbitalElementsAvailability(store, logger);

        var first = await availability.EnsureAvailableAsync(CancellationToken.None).ConfigureAwait(false);
        var second = await availability.EnsureAvailableAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsAvailabilityState.StorageUnavailable, first);
        Assert.AreEqual(OrbitalElementsAvailabilityState.StorageUnavailable, second);
        Assert.AreEqual("orbital.storage-locked", availability.ReasonCode);
        CollectionAssert.AreEqual(UnavailableOnce, logger.EventIds.ToArray());
        Assert.IsFalse(logger.Rendered().Contains(root, StringComparison.Ordinal), "Logs never carry the store path.");

        owner.Dispose();
        var recovered = await availability.EnsureAvailableAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsAvailabilityState.Available, recovered);
        Assert.IsNull(availability.ReasonCode);
        CollectionAssert.AreEqual(UnavailableThenRecovered, logger.EventIds.ToArray());
    }

    [TestMethod]
    public async Task ASchemaFromANewerVersionIsAnIntegrityFailure()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using (var original = OrbitalElementsStoreTestData.Open(root, clock))
        {
            await original.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        OrbitalElementsStoreTestData.Execute(root, "PRAGMA user_version = 2;");
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var availability = new OrbitalElementsAvailability(store, new OrbitalTestLogger<OrbitalElementsAvailability>());

        var state = await availability.EnsureAvailableAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsAvailabilityState.IntegrityFailure, state);
        Assert.AreEqual(OrbitalElementsAvailabilityState.IntegrityFailure, availability.State);
        Assert.AreEqual(OrbitalElementsAvailability.IntegrityFailureReasonCode, availability.ReasonCode);
    }

    [TestMethod]
    public async Task CallerCancellationIsRethrownAndNotRecordedAsAFailure()
    {
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(CreateRoot(), clock);
        var logger = new OrbitalTestLogger<OrbitalElementsAvailability>();
        var availability = new OrbitalElementsAvailability(store, logger);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => availability.EnsureAvailableAsync(cancellation.Token).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsAvailabilityState.NotInitialized, availability.State);
        Assert.AreEqual(0, logger.Entries.Count);
    }

    [TestMethod]
    [DataRow("storage", "StorageUnavailable", "orbital.storage-no-space")]
    [DataRow("integrity", "IntegrityFailure", "orbital.storage-integrity")]
    [DataRow("sqlite", "StorageUnavailable", "orbital.storage-database")]
    [DataRow("permission", "StorageUnavailable", "orbital.storage-permission")]
    [DataRow("io", "StorageUnavailable", "orbital.storage-io")]
    [DataRow("other", "StorageUnavailable", "orbital.store-unavailable")]
    public void FailuresAreClassifiedToStableReasonCodes(string kind, string state, string reasonCode)
    {
        Exception exception = kind switch
        {
            "storage" => new OrbitalStorageException("orbital.storage-no-space", "synthetic", null),
            "integrity" => new InvalidDataException("synthetic"),
            "sqlite" => new SqliteException("synthetic", 1),
            "permission" => new UnauthorizedAccessException("synthetic"),
            "io" => new IOException("synthetic"),
            _ => new InvalidOperationException("synthetic")
        };

        var classified = OrbitalElementsAvailability.Classify(exception);

        Assert.AreEqual(Enum.Parse<OrbitalElementsAvailabilityState>(state), classified.State);
        Assert.AreEqual(reasonCode, classified.ReasonCode);
    }

    [TestMethod]
    public async Task TheInitializerNeverFailsStartupAndRetriesUntilTheStoreRecovers()
    {
        var root = CreateRoot();
        var clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        var owner = OrbitalElementsStoreTestData.Open(root, clock);
        await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var availability = new OrbitalElementsAvailability(store, new OrbitalTestLogger<OrbitalElementsAvailability>());
        using var service = new OrbitalElementsInitializationService(availability, clock);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsAvailabilityState.StorageUnavailable, availability.State);
        Assert.AreEqual(OrbitalElementsInitializationService.RetryInterval, await clock.WaitForTimerAsync().ConfigureAwait(false));
        clock.Advance(OrbitalElementsInitializationService.RetryInterval);
        await Task.Delay(50).ConfigureAwait(false);
        Assert.IsFalse(service.ExecuteTask!.IsCompleted, "A failed retry keeps the retry loop alive.");
        Assert.AreEqual(OrbitalElementsAvailabilityState.StorageUnavailable, availability.State);

        owner.Dispose();
        clock.Advance(OrbitalElementsInitializationService.RetryInterval);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        Assert.IsTrue(availability.IsAvailable);
        await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TheInitializerIsIdleOnceTheStoreIsAvailableAndStopsCleanlyWhileRetrying()
    {
        var root = CreateRoot();
        var clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        using (var store = OrbitalElementsStoreTestData.Open(root, clock))
        {
            var availability = new OrbitalElementsAvailability(store, new OrbitalTestLogger<OrbitalElementsAvailability>());
            using var idle = new OrbitalElementsInitializationService(availability, clock);
            await idle.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(availability.IsAvailable);
            await idle.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Assert.IsTrue(idle.ExecuteTask.IsCompletedSuccessfully);
        }

        using var owner = OrbitalElementsStoreTestData.Open(root, clock);
        await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var locked = OrbitalElementsStoreTestData.Open(root, clock);
        var unavailable = new OrbitalElementsAvailability(locked, new OrbitalTestLogger<OrbitalElementsAvailability>());
        using var retrying = new OrbitalElementsInitializationService(unavailable, clock);
        await retrying.StartAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsInitializationService.RetryInterval, await clock.WaitForTimerAsync().ConfigureAwait(false));
        Assert.IsFalse(retrying.ExecuteTask!.IsCompleted);

        await retrying.StopAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(retrying.ExecuteTask.IsCompletedSuccessfully);
        Assert.IsFalse(unavailable.IsAvailable);
    }
}
