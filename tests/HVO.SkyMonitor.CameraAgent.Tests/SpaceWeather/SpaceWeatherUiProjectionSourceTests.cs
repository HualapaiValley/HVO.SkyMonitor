using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using static HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.SpaceWeatherUiTestData;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

/// <summary>
/// The shared projection read against the real store and operations service: its time-to-live, single flight,
/// per-caller cancellation, cached failures, invalidation and the paired-revision retry. Time is the host's manual clock.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherUiProjectionSourceTests
{
    [TestMethod]
    public async Task AReadWithinTheTimeToLiveReusesTheProjectionAndALaterReadRereads()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var source = Source(host);

        var first = await source.GetAsync(default).ConfigureAwait(false);
        Assert.AreEqual(SpaceWeatherUiState.Ready, first.State);
        Assert.AreEqual(2, Reads(host), "One shared read is the notices view and the status, read once each.");

        host.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.AreSame(first, await source.GetAsync(default).ConfigureAwait(false));
        Assert.AreEqual(2, Reads(host));

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await source.GetAsync(default).ConfigureAwait(false);
        Assert.AreNotSame(first, second);
        Assert.AreEqual(4, Reads(host));
    }

    [TestMethod]
    public async Task ConcurrentCallersShareOneRead()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Store.ReadOverride = async (at, ct) =>
        {
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
            return await host.Sqlite.ReadAsync(at, ct).ConfigureAwait(false);
        };
        var source = Source(host);

        var callers = Enumerable.Range(0, 3).Select(_ => source.GetAsync(default)).ToArray();
        Assert.IsFalse(callers.Any(static caller => caller.IsCompleted));
        release.SetResult();
        var results = await Task.WhenAll(callers).ConfigureAwait(false);

        Assert.AreSame(results[0], results[1]);
        Assert.AreSame(results[0], results[2]);
        Assert.AreEqual(SpaceWeatherUiState.Ready, results[0].State);
        Assert.AreEqual(2, Reads(host));
    }

    [TestMethod]
    public async Task ACallerThatStopsWaitingDoesNotCancelTheSharedRead()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Store.ReadOverride = async (at, ct) =>
        {
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
            return await host.Sqlite.ReadAsync(at, ct).ConfigureAwait(false);
        };
        var source = Source(host);
        using var leaving = new CancellationTokenSource();

        var left = source.GetAsync(leaving.Token);
        var staying = source.GetAsync(default);
        await leaving.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => left).ConfigureAwait(false);
        Assert.IsFalse(staying.IsCompleted);

        release.SetResult();
        Assert.AreEqual(SpaceWeatherUiState.Ready, (await staying.ConfigureAwait(false)).State);
        Assert.AreEqual(2, Reads(host));
    }

    [TestMethod]
    public async Task AReadThatOutlastsItsTimeoutBecomesAReadFailure()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Store.ReadOverride = async (at, ct) =>
        {
            await never.Task.WaitAsync(ct).ConfigureAwait(false);
            return await host.Sqlite.ReadAsync(at, ct).ConfigureAwait(false);
        };
        var source = Source(host);

        var pending = source.GetAsync(default);
        host.Clock.Advance(SpaceWeatherUiBounds.ReadTimeout);

        Assert.AreEqual(SpaceWeatherUiState.ReadFailed, (await pending.ConfigureAwait(false)).State);
        Assert.AreEqual(1, LogsOf(host, 7835).Length);
    }

    [TestMethod]
    public async Task AFailedReadIsCachedForTheTimeToLiveAndLogsOnlyItsType()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        host.Store.ReadFailure = new InvalidOperationException("secret-detail at a private location");
        var source = Source(host);

        var failed = await source.GetAsync(default).ConfigureAwait(false);
        Assert.AreEqual(SpaceWeatherUiState.ReadFailed, failed.State);
        Assert.IsFalse(failed.HasData);
        Assert.AreEqual("Space-weather UI read failed (InvalidOperationException).", LogsOf(host, 7835).Single());
        Assert.IsFalse(host.Logs.Any(static entry => entry.Text.Contains("secret-detail", StringComparison.Ordinal)));

        host.Store.ReadFailure = null;
        host.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.AreSame(failed, await source.GetAsync(default).ConfigureAwait(false));
        Assert.AreEqual(1, Reads(host));

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(SpaceWeatherUiState.Ready, (await source.GetAsync(default).ConfigureAwait(false)).State);
    }

    [TestMethod]
    public async Task AStorageFailureIsTheStoreUnavailableState()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        host.Store.ReadFailure = new SwpcStorageException();
        var source = Source(host);

        var projection = await source.GetAsync(default).ConfigureAwait(false);

        Assert.AreEqual(SpaceWeatherUiState.StoreUnavailable, projection.State);
        Assert.AreEqual("Space-weather UI read failed (SwpcStorageException).", LogsOf(host, 7835).Single());
    }

    [TestMethod]
    public async Task AStatusThatCannotReadTheStoreIsTheStoreUnavailableState()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var reads = 0;
        host.Store.ReadOverride = async (at, ct) =>
        {
            if (Interlocked.Increment(ref reads) % 2 == 0)
            {
                throw new SwpcStorageException();
            }
            return await host.Sqlite.ReadAsync(at, ct).ConfigureAwait(false);
        };
        var source = Source(host);

        var projection = await source.GetAsync(default).ConfigureAwait(false);

        Assert.AreEqual(SpaceWeatherUiState.StoreUnavailable, projection.State);
        Assert.IsEmpty(projection.Current);
        Assert.AreEqual(0, projection.ReachCount);
    }

    [TestMethod]
    public async Task InvalidationMakesTheNextCallerReread()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var source = Source(host);
        var first = await source.GetAsync(default).ConfigureAwait(false);

        source.Invalidate();
        var second = await source.GetAsync(default).ConfigureAwait(false);

        Assert.AreNotSame(first, second);
        Assert.AreEqual(4, Reads(host));
    }

    [TestMethod]
    public async Task AReadInFlightDuringInvalidationServesItsWaitersButIsNotCached()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Store.ReadOverride = async (at, ct) =>
        {
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
            return await host.Sqlite.ReadAsync(at, ct).ConfigureAwait(false);
        };
        var source = Source(host);

        var waiter = source.GetAsync(default);
        source.Invalidate();
        release.SetResult();
        Assert.AreEqual(SpaceWeatherUiState.Ready, (await waiter.ConfigureAwait(false)).State);
        Assert.AreEqual(2, Reads(host));

        await source.GetAsync(default).ConfigureAwait(false);
        Assert.AreEqual(4, Reads(host));
    }

    [TestMethod]
    public async Task ARevisionMismatchIsRereadOnceAndThenFlaggedInconsistent()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var reads = 0;
        host.Store.ReadOverride = async (at, ct) =>
        {
            var view = await host.Sqlite.ReadAsync(at, ct).ConfigureAwait(false);
            return Interlocked.Increment(ref reads) % 2 == 1 ? view with { Revision = view.Revision + 1000 } : view;
        };
        var source = Source(host);

        var projection = await source.GetAsync(default).ConfigureAwait(false);

        Assert.AreEqual(SpaceWeatherUiState.Ready, projection.State);
        Assert.IsTrue(projection.Inconsistent);
        Assert.AreEqual(4, Reads(host));
        Assert.AreEqual("Space-weather UI projection inconsistent after retry.", LogsOf(host, 7838).Single());
    }

    [TestMethod]
    public async Task ARevisionMismatchThatClearsOnRetryIsConsistent()
    {
        using var host = new SpaceWeatherTestHost();
        await host.InitializeAsync().ConfigureAwait(false);
        var reads = 0;
        host.Store.ReadOverride = async (at, ct) =>
        {
            var view = await host.Sqlite.ReadAsync(at, ct).ConfigureAwait(false);
            return Interlocked.Increment(ref reads) == 1 ? view with { Revision = view.Revision + 1000 } : view;
        };
        var source = Source(host);

        var projection = await source.GetAsync(default).ConfigureAwait(false);

        Assert.IsFalse(projection.Inconsistent);
        Assert.AreEqual(4, Reads(host));
        Assert.IsEmpty(LogsOf(host, 7838));
    }

    [TestMethod]
    public async Task TheProjectionCarriesTheActiveSiteAndTheHostSwitch()
    {
        using var host = new SpaceWeatherTestHost(enabled: false);
        await host.InitializeAsync().ConfigureAwait(false);

        var projection = await Source(host).GetAsync(default).ConfigureAwait(false);

        Assert.AreEqual(SpaceWeatherUiState.Ready, projection.State);
        Assert.IsFalse(projection.Enabled);
        Assert.IsTrue(projection.Site.Configured);
        Assert.AreEqual("America/Phoenix", projection.Site.ZoneLabel);
        Assert.AreEqual(42.2, projection.Site.GeomagneticLatitude!.Value, 0.05);
    }

    private static SpaceWeatherUiProjectionSource Source(SpaceWeatherTestHost host)
        => new(host.Availability, host.Store, host.Operations, new FixedLocations(Site()), host.Options, host.Clock,
            host.Logger<SpaceWeatherUiProjectionSource>(), new TestSun());

    private static int Reads(SpaceWeatherTestHost host)
        => host.Store.Calls.Count(static call => call == "read");

    private static string[] LogsOf(SpaceWeatherTestHost host, int id)
    {
        lock (host.Logs)
        {
            return host.Logs.Where(entry => entry.Id == id).Select(static entry => entry.Text).ToArray();
        }
    }

    private sealed class FixedLocations(DeploymentLocationSnapshot active) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => active;

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken) => ValueTask.FromResult(active);

        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null)
            => active;
    }
}
