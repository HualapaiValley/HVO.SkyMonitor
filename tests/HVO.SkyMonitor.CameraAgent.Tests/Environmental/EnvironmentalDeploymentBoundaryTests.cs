using System.Collections.Concurrent;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalDeploymentBoundaryTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HarnessDeadline = TimeSpan.FromSeconds(5);

    [TestMethod]
    [DataRow(-1, 0, null)]
    [DataRow(0, 30, EnvironmentalAcquisitionDisposition.Produced)]
    [DataRow(1, 30, EnvironmentalAcquisitionDisposition.Missing)]
    [DataRow(29_999, 30, EnvironmentalAcquisitionDisposition.Missing)]
    [DataRow(30_000, 60, EnvironmentalAcquisitionDisposition.Produced)]
    public async Task NewDeploymentStartup_RecordsTheFloorSlotAndPreservesTheNextBoundary(
        int startupMilliseconds,
        int nextSeconds,
        EnvironmentalAcquisitionDisposition? expectedInitialDisposition)
    {
        var startup = Epoch.AddMilliseconds(startupMilliseconds);
        var fixture = new Fixture(startup, Location(startup));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var next = Epoch.AddSeconds(nextSeconds);

        await fixture.StartAndWaitForPollAsync(next).ConfigureAwait(false);
        var states = await fixture.ReadStatesAsync().ConfigureAwait(false);
        var attempts = await fixture.ReadAttemptsAsync().ConfigureAwait(false);
        var state = states.Single();

        Assert.AreEqual(expectedInitialDisposition, state.LastDisposition);
        Assert.AreEqual(next, state.NextPollUtc);
        Assert.HasCount(expectedInitialDisposition.HasValue ? 1 : 0, attempts);
        if (expectedInitialDisposition == EnvironmentalAcquisitionDisposition.Missing)
        {
            Assert.AreEqual("deployment-location-not-effective", state.LastReason);
            Assert.IsNull(state.LastObservationId);
            Assert.IsNull(state.LastObservedUtc);
            Assert.IsEmpty(fixture.Publisher.Facts);
            Assert.AreEqual(0L, (await fixture.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
        }

        fixture.Clock.AdvanceTo(next);
        await fixture.Clock.WaitForTimerAsync(next.AddSeconds(30)).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        state = (await fixture.ReadStatesAsync().ConfigureAwait(false)).Single();

        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, state.LastDisposition);
        Assert.AreEqual(next, state.LastObservedUtc);
        Assert.AreEqual(next.AddSeconds(30), state.NextPollUtc);
        Assert.AreEqual(0, state.ConsecutiveFailures);
        Assert.IsTrue(fixture.Publisher.Facts.All(fact => fixture.Location.IsEffectiveAt(fact.ObservedAtUtc)));
        Assert.IsFalse(fixture.Logger.Events.Any(static entry => entry.Id == 2522));
    }

    [TestMethod]
    public async Task FutureEffectiveDeployment_RecordsMissingSlotsUntilTheFirstValidBoundary()
    {
        var fixture = new Fixture(Epoch.AddMilliseconds(1), Location(Epoch.AddSeconds(31)));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.StartAndWaitForPollAsync(Epoch.AddSeconds(30)).ConfigureAwait(false);

        fixture.Clock.AdvanceTo(Epoch.AddSeconds(30));
        await fixture.Clock.WaitForTimerAsync(Epoch.AddSeconds(60)).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        var attempts = await fixture.ReadAttemptsAsync().ConfigureAwait(false);
        Assert.HasCount(2, attempts);
        Assert.IsTrue(attempts.All(static attempt =>
            attempt.Disposition == EnvironmentalAcquisitionDisposition.Missing &&
            attempt.Reason == "deployment-location-not-effective" && attempt.ObservationId is null));
        Assert.IsEmpty(fixture.Publisher.Facts);

        fixture.Clock.AdvanceTo(Epoch.AddSeconds(60));
        await fixture.Clock.WaitForTimerAsync(Epoch.AddSeconds(90)).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        var state = (await fixture.ReadStatesAsync().ConfigureAwait(false)).Single();
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, state.LastDisposition);
        Assert.AreEqual(Epoch.AddSeconds(60), state.LastObservedUtc);
        Assert.HasCount(1, fixture.Publisher.Facts);
    }

    [TestMethod]
    public async Task ExpiredDeployment_RecordsMissingAtTheExclusiveEndWithoutPublishing()
    {
        var fixture = new Fixture(Epoch, Location(Epoch, Epoch.AddSeconds(30)));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.StartAndWaitForPollAsync(Epoch.AddSeconds(30)).ConfigureAwait(false);

        fixture.Clock.AdvanceTo(Epoch.AddSeconds(30));
        await fixture.Clock.WaitForTimerAsync(Epoch.AddSeconds(60)).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        var state = (await fixture.ReadStatesAsync().ConfigureAwait(false)).Single();

        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Missing, state.LastDisposition);
        Assert.AreEqual("deployment-location-not-effective", state.LastReason);
        Assert.AreEqual(Epoch, state.LastObservedUtc, "The prior valid observation remains historical evidence.");
        Assert.HasCount(1, fixture.Publisher.Facts);
        Assert.AreEqual(Epoch.AddSeconds(60), state.NextPollUtc);
    }

    [TestMethod]
    public async Task MissingStartupAndSuccessfulIndependentSources_RemainDurableAcrossRestart()
    {
        var location = Location(Epoch.AddMilliseconds(1));
        var fixture = new Fixture(Epoch.AddMilliseconds(1), location, sourceCount: 2);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.StartAndWaitForPollAsync(Epoch.AddSeconds(30)).ConfigureAwait(false);
        await fixture.StopAsync().ConfigureAwait(false);

        using (var reopened = new SqliteEnvironmentalObservationOutbox())
        {
            var states = await reopened.ReadSourceStatesAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
            var attempts = await reopened.ReadAttemptsAsync(fixture.Root, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(2, states);
            Assert.HasCount(2, attempts);
            Assert.IsTrue(states.All(static state =>
                state.LastDisposition == EnvironmentalAcquisitionDisposition.Missing &&
                state.NextPollUtc == Epoch.AddSeconds(30)));
            Assert.AreEqual(0L, (await reopened.GetLocalSnapshotAsync(fixture.Root, CancellationToken.None)
                .ConfigureAwait(false)).StoredCount);
        }

        var restart = new Fixture(Epoch.AddSeconds(30), location, sourceCount: 2, root: fixture.Root);
        await using var restartLifetime = restart.ConfigureAwait(false);
        await restart.StartAndWaitForPollAsync(Epoch.AddSeconds(60)).ConfigureAwait(false);
        var recovered = await restart.ReadStatesAsync().ConfigureAwait(false);
        var history = await restart.ReadAttemptsAsync().ConfigureAwait(false);
        Assert.HasCount(2, recovered);
        Assert.HasCount(4, history);
        Assert.HasCount(2, restart.Publisher.Facts);
        Assert.AreEqual(2L, (await restart.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
        Assert.IsTrue(recovered.All(static state =>
            state.LastDisposition == EnvironmentalAcquisitionDisposition.Produced &&
            state.LastObservedUtc == Epoch.AddSeconds(30) && state.ConsecutiveFailures == 0));
    }

    [TestMethod]
    public async Task CaptureTriggeredAcquisition_PreservesLocationValidationAndValidPublication()
    {
        var fixture = new Fixture(Epoch.AddMilliseconds(1), Location(Epoch.AddMilliseconds(1)));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var captureId = Guid.NewGuid();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await fixture.Coordinator.AcquireSourceAsync(
                "source-0", EnvironmentalAcquisitionTrigger.BeforeCapture, Epoch, 1, captureId,
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var receipt = await fixture.Coordinator.AcquireSourceAsync(
            "source-0", EnvironmentalAcquisitionTrigger.BeforeCapture, Epoch.AddMilliseconds(1), 1, captureId,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, receipt.Disposition);
        Assert.HasCount(1, fixture.Publisher.Facts);
        Assert.HasCount(1, await fixture.ReadAttemptsAsync().ConfigureAwait(false));
    }

    private static DeploymentLocationSnapshot Location(DateTimeOffset from, DateTimeOffset? until = null)
        => DeploymentLocationSnapshot.Create(
            "location", 1, "test", null, from, until,
            35.5599378, -113.9119818, 520, "America/Phoenix");

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly SqliteEnvironmentalObservationOutbox _store;
        private readonly EnvironmentalAcquisitionTelemetry _telemetry;
        private readonly EnvironmentalAcquisitionService _service;
        private readonly bool _ownsRoot;
        private bool _started;
        private readonly int _sourceCount;

        public Fixture(DateTimeOffset startup, DeploymentLocationSnapshot location, int sourceCount = 1, string? root = null)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), $"hvo-environmental-boundary-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            _ownsRoot = root is null;
            _sourceCount = sourceCount;
            Clock = new ManualClock(startup);
            Location = location;
            _store = new SqliteEnvironmentalObservationOutbox(timeProvider: Clock);
            Publisher = new RecordingPublisher(_store, Root);
            _telemetry = new EnvironmentalAcquisitionTelemetry(Clock);
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = Root,
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = true,
                    MaximumConcurrency = 4,
                    QueueCapacity = 4,
                    Sources = Enumerable.Range(0, sourceCount).Select(index => new EnvironmentalSourceConfiguration
                    {
                        Id = $"source-{index}",
                        Type = "VirtualEnvironment",
                        Kind = EnvironmentalObservationKind.AirTemperature,
                        Required = true,
                        Triggers = [EnvironmentalAcquisitionTrigger.Periodic, EnvironmentalAcquisitionTrigger.BeforeCapture],
                        ScheduleEpochUtc = Epoch,
                        PeriodSeconds = 30,
                        ValidForSeconds = 120,
                        StaleAfterSeconds = 45,
                        Options = CaptureContractJson.SerializeToElement(new VirtualEnvironmentalSourceOptions(
                            1118 + index, Epoch, 12.5, null))
                    }).ToArray()
                }
            });
            var factory = new EnvironmentalSourceFactory(_services,
                [new EnvironmentalSourceRegistration(
                    "VirtualEnvironment", typeof(VirtualEnvironmentalSource), typeof(VirtualEnvironmentalSourceOptions))]);
            Coordinator = new EnvironmentalAcquisitionCoordinator(
                factory, Publisher, new FixedLocationStore(location), options, Clock, _store, _telemetry);
            _service = new EnvironmentalAcquisitionService(
                Coordinator, _store, new NoopRetentionStore(), _telemetry, Clock, options, Logger);
        }

        public string Root { get; }
        public ManualClock Clock { get; }
        public DeploymentLocationSnapshot Location { get; }
        public RecordingPublisher Publisher { get; }
        public RecordingLogger Logger { get; } = new();
        public EnvironmentalAcquisitionCoordinator Coordinator { get; }

        public async Task StartAndWaitForPollAsync(DateTimeOffset nextPoll)
        {
            await _service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            _started = true;
            await Clock.WaitForTimerAsync(nextPoll, _sourceCount).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<EnvironmentalSourceRuntimeState>> ReadStatesAsync()
            => _store.ReadSourceStatesAsync(Root, CancellationToken.None);

        public ValueTask<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>> ReadAttemptsAsync()
            => _store.ReadAttemptsAsync(Root, 100, CancellationToken.None);

        public ValueTask<LocalEnvironmentalObservationSnapshot> ReadSnapshotAsync()
            => _store.GetLocalSnapshotAsync(Root, CancellationToken.None);

        public async Task StopAsync()
        {
            if (_started)
            {
                await _service.StopAsync(CancellationToken.None).ConfigureAwait(false);
                _started = false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _service.Dispose();
            Coordinator.Dispose();
            _telemetry.Dispose();
            _store.Dispose();
            await _services.DisposeAsync().ConfigureAwait(false);
            if (_ownsRoot)
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class RecordingPublisher(SqliteEnvironmentalObservationOutbox store, string root)
        : IEnvironmentalObservationPublisher
    {
        public ConcurrentQueue<EnvironmentalObservationFactV1> Facts { get; } = new();

        public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact, CancellationToken cancellationToken = default)
        {
            var result = await store.CommitLocalAsync(root, fact, cancellationToken).ConfigureAwait(false);
            Facts.Enqueue(fact);
            return new EnvironmentalObservationPublishResult(
                result.Disposition == LocalEnvironmentalObservationCommitDisposition.Committed
                    ? EnvironmentalObservationPublishDisposition.Enqueued
                    : EnvironmentalObservationPublishDisposition.Duplicate,
                null);
        }
    }

    private sealed class FixedLocationStore(DeploymentLocationSnapshot active) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => active;
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
            => ValueTask.FromResult(active);
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null) => active;
    }

    private sealed class NoopRetentionStore : ILocalEnvironmentalRetentionStore
    {
        public ValueTask<LocalEnvironmentalRetentionResult> RetainLocalAsync(
            string root, DateTimeOffset recordedBeforeUtc, int maximumResults, CancellationToken cancellationToken)
            => ValueTask.FromResult(new LocalEnvironmentalRetentionResult(0, 0, 0, 0));
    }

    private sealed class RecordingLogger : ILogger<EnvironmentalAcquisitionService>
    {
        public ConcurrentQueue<(int Id, Exception? Exception)> Events { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Enqueue((eventId.Id, exception));
    }

    private sealed class ManualClock(DateTimeOffset startup) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<(DateTimeOffset Due, int Count, TaskCompletionSource Completion)> _waiters = [];
        private DateTimeOffset _now = startup;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => GetUtcNow().UtcTicks;
        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) { return _now; }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                timer.Change(dueTime, period);
                _timers.Add(timer);
                foreach (var waiter in _waiters.Where(waiter =>
                    _timers.Count(timer => timer.Due == waiter.Due) >= waiter.Count).ToArray())
                {
                    _waiters.Remove(waiter);
                    waiter.Completion.TrySetResult();
                }
                return timer;
            }
        }

        public Task WaitForTimerAsync(DateTimeOffset due, int count = 1)
        {
            lock (_gate)
            {
                if (_timers.Count(timer => timer.Due == due) >= count) { return Task.CompletedTask; }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((due, count, completion));
                return completion.Task;
            }
        }

        public void AdvanceTo(DateTimeOffset instant)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(instant, _now);
                _now = instant;
                due = _timers.Where(timer => timer.Due <= instant).ToArray();
                foreach (var timer in due) { timer.Due = null; }
            }
            foreach (var timer in due) { timer.Fire(); }
        }

        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (period != Timeout.InfiniteTimeSpan) { throw new NotSupportedException("Only one-shot delays are used by this fixture."); }
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                    return true;
                }
            }
            public void Fire() => callback(state);
            public void Dispose()
            {
                lock (owner._gate) { owner._timers.Remove(this); Due = null; }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
