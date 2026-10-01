using System.Collections.Concurrent;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeSync;

[TestClass]
[TestCategory("Unit")]
public sealed class ClockSyncMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly KernelClockState Kernel = new(KernelClockStatus.Synchronized, null, TimeSpan.FromMicroseconds(40));

    [TestMethod]
    public async Task CheckNowAsync_WhenCheckingIsOff_ContactsNoServerAsync()
    {
        var client = new RecordingClient();
        using var monitor = Create(TimeSyncSettings.Default with { Enabled = false }, client);

        var result = await monitor.CheckNowAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ClockCheckOutcome.Measured, result.Outcome);
        Assert.IsFalse(result.Snapshot!.Enabled);
        Assert.IsEmpty(result.Snapshot.Servers);
        Assert.IsEmpty(client.Queries);
        Assert.AreSame(result.Snapshot, monitor.Latest);
    }

    [TestMethod]
    public async Task CheckNowAsync_ChoosesTheValidReplyWithTheShortestRoundTripAsync()
    {
        var client = new RecordingClient
        {
            ["a.example"] = new("a.example", null, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(90), 2),
            ["b.example"] = new("b.example", null, TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(15), 1),
            ["c.example"] = TimeServerResult.Failed("c.example", SntpFailure.Timeout),
        };
        using var monitor = Create(Servers("a.example", "b.example", "c.example"), client);

        var snapshot = (await monitor.CheckNowAsync(CancellationToken.None).ConfigureAwait(false)).Snapshot!;

        Assert.AreEqual("b.example", snapshot.Selected!.Server);
        Assert.HasCount(3, snapshot.Servers);
        Assert.AreEqual(Now, snapshot.MeasuredUtc);
        Assert.AreEqual(Kernel, snapshot.Kernel);
        Assert.AreEqual(TimeSyncSettings.DefaultQueryTimeout, client.Timeouts.Single().Value);
    }

    [TestMethod]
    public async Task CheckNowAsync_WhenEveryServerFails_SelectsNothingAsync()
    {
        var client = new RecordingClient
        {
            ["a.example"] = TimeServerResult.Failed("a.example", SntpFailure.Refused),
        };
        using var monitor = Create(Servers("a.example"), client);

        var snapshot = (await monitor.CheckNowAsync(CancellationToken.None).ConfigureAwait(false)).Snapshot!;

        Assert.IsNull(snapshot.Selected);
        Assert.AreEqual(SntpFailure.Refused, snapshot.Servers.Single().Failure);
    }

    [TestMethod]
    public async Task CheckNowAsync_QueriesAtMostFourValidServersAndCountsTheRestAsIgnoredAsync()
    {
        var client = new RecordingClient();
        using var monitor = Create(
            Servers("https://bad.example", "a.example", "b.example", "user@bad.example", "c.example", "d.example", "e.example"),
            client);

        var snapshot = (await monitor.CheckNowAsync(CancellationToken.None).ConfigureAwait(false)).Snapshot!;

        string[] expected = ["a.example", "b.example", "c.example", "d.example"];
        CollectionAssert.AreEquivalent(expected, client.Queries.ToArray());
        Assert.AreEqual(3, snapshot.IgnoredEntries);
    }

    [TestMethod]
    public async Task CheckNowAsync_WithNoServerConfigured_QueriesTheDefaultAsync()
    {
        var client = new RecordingClient();
        using var monitor = Create(TimeSyncSettings.Default with { Servers = [] }, client);

        await monitor.CheckNowAsync(CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(TimeSyncSettings.DefaultServers.ToArray(), client.Queries.ToArray());
    }

    [TestMethod]
    public async Task CheckNowAsync_WhenTheClientThrows_RecordsARoundWithNoAnswerAsync()
    {
        var client = new RecordingClient { Throw = new InvalidOperationException("boom") };
        using var monitor = Create(Servers("a.example"), client);

        var snapshot = (await monitor.CheckNowAsync(CancellationToken.None).ConfigureAwait(false)).Snapshot!;

        Assert.IsTrue(snapshot.Enabled);
        Assert.IsNull(snapshot.Selected);
        Assert.IsEmpty(snapshot.Servers);
    }

    [TestMethod]
    public async Task CheckNowAsync_WithinThirtySecondsOfTheLastOnDemandRound_ReturnsItsResultInsteadAsync()
    {
        var client = new RecordingClient();
        var clock = new ManualTimeProvider(Now);
        using var monitor = Create(Servers("a.example"), client, clock);
        var first = await monitor.CheckNowAsync(CancellationToken.None).ConfigureAwait(false);

        clock.Advance(TimeSpan.FromSeconds(20));
        var limited = await monitor.CheckNowAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ClockCheckOutcome.RateLimited, limited.Outcome);
        Assert.AreSame(first.Snapshot, limited.Snapshot);
        Assert.AreEqual(Now.AddSeconds(30), limited.RetryAfterUtc);
        Assert.HasCount(1, client.Queries);

        clock.Advance(TimeSpan.FromSeconds(10));
        var again = await monitor.CheckNowAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ClockCheckOutcome.Measured, again.Outcome);
        Assert.HasCount(2, client.Queries);
    }

    [TestMethod]
    public async Task CheckNowAsync_WhileARoundIsRunning_JoinsItAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingClient { Gate = release.Task };
        using var monitor = Create(Servers("a.example"), client);

        var first = monitor.CheckNowAsync(CancellationToken.None);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var second = monitor.CheckNowAsync(CancellationToken.None);
        release.SetResult();
        var results = await Task.WhenAll(first, second).ConfigureAwait(false);

        Assert.AreEqual(ClockCheckOutcome.Measured, results[0].Outcome);
        Assert.AreEqual(ClockCheckOutcome.Joined, results[1].Outcome);
        Assert.AreSame(results[0].Snapshot, results[1].Snapshot);
        Assert.HasCount(1, client.Queries);
    }

    [TestMethod]
    public async Task CheckNowAsync_ACallerThatStopsWaiting_DoesNotCancelTheRoundAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingClient { Gate = release.Task };
        using var monitor = Create(Servers("a.example"), client);
        using var cancellation = new CancellationTokenSource();

        var waiting = monitor.CheckNowAsync(cancellation.Token);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting).ConfigureAwait(false);
        release.SetResult();
        var joined = await monitor.CheckNowAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreNotEqual(ClockCheckOutcome.Measured, joined.Outcome);
        Assert.IsNotNull(joined.Snapshot);
        Assert.HasCount(1, client.Queries);
    }

    [TestMethod]
    public async Task StartAsync_MeasuresAtStartupAndReadsTheSettingsEachRoundAsync()
    {
        var client = new RecordingClient();
        var settings = Servers("a.example");
        using var monitor = new ClockSyncMonitor(
            () => settings, client, new StaticKernel(), new ManualTimeProvider(Now), NullLogger<ClockSyncMonitor>.Instance);

        await monitor.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        settings = Servers("b.example");
        await monitor.StopAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("a.example", client.Queries.Single());
        Assert.AreEqual("b.example", monitor.Settings.EffectiveServers.Single());
    }

    private static TimeSyncSettings Servers(params string[] servers) => TimeSyncSettings.Default with { Servers = servers };

    private static ClockSyncMonitor Create(TimeSyncSettings settings, RecordingClient client, TimeProvider? clock = null)
        => new(() => settings, client, new StaticKernel(), clock ?? new ManualTimeProvider(Now), NullLogger<ClockSyncMonitor>.Instance);

    private sealed class RecordingClient : ISntpClient
    {
        private readonly Dictionary<string, TimeServerResult> _results = new(StringComparer.Ordinal);

        internal ConcurrentQueue<string> Queries { get; } = new();

        internal ConcurrentDictionary<TimeSpan, TimeSpan> Timeouts { get; } = new();

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Gate { get; init; } = Task.CompletedTask;

        internal Exception? Throw { get; init; }

        internal TimeServerResult this[string server]
        {
            set => _results[server] = value;
        }

        public async Task<TimeServerResult> QueryAsync(string server, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Queries.Enqueue(server);
            Timeouts[timeout] = timeout;
            Started.TrySetResult();
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Throw is not null)
            {
                throw Throw;
            }
            return _results.TryGetValue(server, out var result)
                ? result
                : new TimeServerResult(server, null, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10), 2);
        }
    }

    private sealed class StaticKernel : IKernelClockReader
    {
        public KernelClockState Read() => Kernel;
    }

    /// <summary>A clock that moves only when told to, for both the wall clock and the monotonic timestamp.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _now.UtcTicks;

        internal void Advance(TimeSpan by) => _now += by;
    }
}
