using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Tests.Orbital;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
public sealed class LocalAutomationOffSweepTests : OrbitalStoreTestBase
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FollowingDefinitionStartsWhileOrbitalProviderAndPrecisionWaitAreHeld(bool precision)
    {
        using var orbital = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var harness = await Harness.CreateAsync(CreateRoot(), orbital.Clock).ConfigureAwait(false);
        harness.Registry.Execute = occurrence => orbital.Job.ExecuteAsync(occurrence.RunKey,
            OrbitalAttemptTrigger.Scheduled, occurrence.ScheduledForUtc, harness.Token);
        if (precision)
        {
            var admitted = await orbital.Store.TryBeginAttemptAsync("prior", OrbitalAttemptTrigger.Scheduled, default).ConfigureAwait(false);
            await orbital.Store.RecordTransportFailureAsync(admitted.AttemptId!, "orbital.synthetic-prior", "prior", default).ConfigureAwait(false);
            // The due definition is two minutes short of the actual provider floor.
            harness.Clock.Advance(TimeSpan.FromSeconds(-120));
        }
        await harness.SaveAsync().ConfigureAwait(false);
        harness.Clock.Advance(TimeSpan.FromHours(2));
        orbital.Client.EnqueueHang();
        var sweep = harness.Runner.SweepAsync(default);
        if (precision)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(120), await orbital.Clock.WaitForTimerAsync().ConfigureAwait(false));
            Assert.AreEqual(0, orbital.Client.Calls);
        }
        else
        {
            await orbital.Client.RequestStarted.Task.WaitAsync(Deadline).ConfigureAwait(false);
        }
        await sweep.WaitAsync(Deadline).ConfigureAwait(false);
        Assert.AreEqual(1, harness.Registry.FollowingCalls);
        Assert.AreEqual(LocalAutomationRunOutcome.Running, (await harness.StateAsync().ConfigureAwait(false)).Runs
            .Single(static run => run.DefinitionId == "a-orbital").Outcome);
        Assert.IsFalse(harness.Worker.WhenIdleAsync().IsCompleted);
        await harness.Runner.StopAsync(default).ConfigureAwait(false);
        Assert.AreEqual(precision ? 0 : 1, orbital.Client.Calls);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "Deliberately blocks a synchronous adapter to prove dispatch never executes it on the sweep.")]
    public async Task SynchronousAdapterCannotExecuteInlineInTheSweep()
    {
        using var harness = await Harness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var entered = Signal();
        var release = Signal();
        harness.Registry.Execute = _ =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            return ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Succeeded, "real completion"));
        };
        await harness.MakeDueAsync().ConfigureAwait(false);
        var sweep = Task.Run(() => harness.Runner.SweepAsync(default));
        try
        {
            await entered.Task.WaitAsync(Deadline).ConfigureAwait(false);
            await sweep.WaitAsync(Deadline).ConfigureAwait(false);
            Assert.AreEqual(1, harness.Registry.FollowingCalls);
            Assert.IsFalse(harness.Worker.WhenIdleAsync().IsCompleted);
        }
        finally { release.TrySetResult(); }
        await harness.Worker.WhenIdleAsync().WaitAsync(Deadline).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await harness.StateAsync().ConfigureAwait(false)).Runs
            .Single(static run => run.DefinitionId == "a-orbital").Outcome);
    }

    [TestMethod]
    public async Task CapacityIsReservedBeforeOccurrenceIsClaimed()
    {
        using var harness = await Harness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await harness.MakeDueAsync().ConfigureAwait(false);
        using (var occupied = harness.Worker.TryReserve())
        {
            Assert.IsNotNull(occupied);
            await harness.Runner.SweepAsync(default).ConfigureAwait(false);
            var state = await harness.StateAsync().ConfigureAwait(false);
            Assert.IsFalse(state.Runs.Any(static run => run.DefinitionId == "a-orbital"));
            Assert.IsNull((await harness.Store.GetRunnerViewAsync(default).ConfigureAwait(false))
                .Single(static entry => entry.Definition.DefinitionId == "a-orbital").LastOccurrenceUtc);
            Assert.AreEqual(1, harness.Registry.FollowingCalls);
        }
        await harness.Runner.SweepAsync(default).ConfigureAwait(false);
        await harness.Worker.WhenIdleAsync().WaitAsync(Deadline).ConfigureAwait(false);
        Assert.AreEqual(1, harness.Registry.Occurrences.Count);
    }

    [TestMethod]
    public async Task RunNowRetainsIdentityPriorityAndTerminalOutcome()
    {
        using var harness = await Harness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await harness.MakeDueAsync().ConfigureAwait(false);
        var queued = await harness.Store.RunNowAsync(new("a-orbital", 1, "run-now", "owner", "test"), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, queued.Status);
        var request = queued.State.Runs.Single();
        var release = Signal();
        harness.Registry.Execute = async _ =>
        {
            await release.Task.ConfigureAwait(false);
            return new(LocalAutomationRunOutcome.Failed, "real terminal failure");
        };
        using (var occupied = harness.Worker.TryReserve())
        {
            await harness.Runner.SweepAsync(default).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationRunOutcome.Queued, (await harness.StateAsync().ConfigureAwait(false)).Runs
                .Single(run => run.RunKey == request.RunKey).Outcome);
        }
        await harness.Runner.SweepAsync(default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Running, (await harness.StateAsync().ConfigureAwait(false)).Runs
            .Single(run => run.RunKey == request.RunKey).Outcome);
        release.SetResult();
        await harness.Worker.WhenIdleAsync().WaitAsync(Deadline).ConfigureAwait(false);
        Assert.AreEqual(request.Occurrence, harness.Registry.Occurrences.Single());
        Assert.IsTrue(LocalAutomationRunKeys.IsRunNow(harness.Registry.Occurrences.Single().RunKey));
        var terminal = (await harness.StateAsync().ConfigureAwait(false)).Runs.Single(run => run.RunKey == request.RunKey);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, terminal.Outcome);
        Assert.AreEqual("real terminal failure", terminal.Detail);
        await harness.Runner.SweepAsync(default).ConfigureAwait(false);
        await harness.Worker.WhenIdleAsync().WaitAsync(Deadline).ConfigureAwait(false);
        Assert.AreEqual(2, harness.Registry.Occurrences.Count);
        Assert.IsFalse(LocalAutomationRunKeys.IsRunNow(harness.Registry.Occurrences[1].RunKey), "Due periodic work follows run-now.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ShutdownDrainsAuthorityAndRestartDoesNotReplayTheClaim(bool disposeFirst)
    {
        var root = CreateRoot();
        using var orbital = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var harness = await Harness.CreateAsync(root, orbital.Clock).ConfigureAwait(false);
        var draining = Signal();
        var release = Signal();
        harness.Registry.Execute = async occurrence =>
        {
            try
            {
                return await orbital.Job.ExecuteAsync(occurrence.RunKey, OrbitalAttemptTrigger.Scheduled,
                    occurrence.ScheduledForUtc, harness.Token).ConfigureAwait(false);
            }
            finally
            {
                draining.SetResult();
                await release.Task.ConfigureAwait(false);
            }
        };
        await harness.MakeDueAsync().ConfigureAwait(false);
        orbital.Client.EnqueueHang();
        await harness.Runner.SweepAsync(default).ConfigureAwait(false);
        using var contender = harness.NewStore();
        try
        {
            await orbital.Client.RequestStarted.Task.WaitAsync(Deadline).ConfigureAwait(false);
            var expiredDeadline = new CancellationToken(canceled: true);
            var first = disposeFirst
                ? Task.Run(harness.Worker.Dispose)
                : harness.Runner.StopAsync(expiredDeadline);
            await draining.Task.WaitAsync(Deadline).ConfigureAwait(false);
            var runnerStop = harness.Runner.StopAsync(expiredDeadline);
            var workerStop = harness.Worker.StopAsync(expiredDeadline);
            var repeatedStop = harness.Runner.StopAsync(default);
            var disposeThread = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
            var competingDispose = Task.Factory.StartNew(() =>
            {
                disposeThread.SetResult(Thread.CurrentThread);
                harness.Worker.Dispose();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var disposer = await disposeThread.Task.WaitAsync(Deadline).ConfigureAwait(false);
            // Observe the synchronous disposer reach its wait or return while release is still gated.
            Assert.IsTrue(SpinWait.SpinUntil(() => competingDispose.IsCompleted ||
                (disposer.ThreadState & ThreadState.WaitSleepJoin) != 0, Deadline));
            Assert.IsTrue(harness.Token.IsCancellationRequested);
            Assert.IsFalse(first.IsCompleted, "The first shutdown caller must wait for the cancelled adapter.");
            Assert.IsFalse(competingDispose.IsCompleted, "Dispose must join an existing drain.");
            Assert.IsFalse(runnerStop.IsCompleted, "The runner must join the drain even with an expired host deadline.");
            Assert.IsFalse(workerStop.IsCompleted, "The hosted stop must join the drain even with an expired host deadline.");
            Assert.IsFalse(repeatedStop.IsCompleted, "A repeated stop must join the same drain.");
            harness.Store.Dispose();
            await Assert.ThrowsAsync<Exception>(() => contender.InitializeAsync(default).AsTask()).ConfigureAwait(false);
            release.SetResult();
            await Task.WhenAll(first, competingDispose, runnerStop, workerStop, repeatedStop).WaitAsync(Deadline).ConfigureAwait(false);
            await harness.Runner.StopAsync(default).WaitAsync(Deadline).ConfigureAwait(false);
            await harness.Worker.StopAsync(default).WaitAsync(Deadline).ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.IsNull(harness.Worker.TryReserve(), "An idle, stopped worker must still refuse new reservations.");
        await contender.InitializeAsync(default).ConfigureAwait(false);
        var settled = await contender.GetStateAsync(default).ConfigureAwait(false);
        var original = harness.Registry.Occurrences.Single();
        var interrupted = settled.Runs.Single(static run => run.DefinitionId == "a-orbital");
        Assert.AreEqual(original.RunKey, interrupted.RunKey);
        Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, interrupted.Outcome);
        Assert.AreEqual(1, orbital.Client.Calls);
        Assert.IsNotNull((await orbital.StateAsync().ConfigureAwait(false)).Halt, "Cancellation before provider outcome keeps the fail-safe halt.");
        using var restartedWorker = new LocalAutomationOffSweepWorker();
        using var restarted = harness.NewRunner(contender, restartedWorker);
        await restarted.SweepAsync(default).ConfigureAwait(false);
        await restartedWorker.WhenIdleAsync().ConfigureAwait(false);
        Assert.AreEqual(original, harness.Registry.Occurrences.Single(), "Restart must not invoke the adapter for the recovered claim.");
        var afterRestart = await contender.GetStateAsync(default).ConfigureAwait(false);
        Assert.AreEqual(interrupted, afterRestart.Runs.Single(static run => run.DefinitionId == "a-orbital"));
        Assert.AreEqual(1, orbital.Client.Calls, "Restart settles the claim without replaying provider work.");
    }

    [TestMethod]
    public async Task StartedRunnerDrainsBeforeReportingCancellationCallbackFailure()
    {
        using var harness = await Harness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var registered = Signal();
        var release = Signal();
        var failure = new ObjectDisposedException("test adapter callback");
        harness.Registry.Execute = async _ =>
        {
            using var registration = harness.Token.Register(() => throw failure);
            registered.SetResult();
            await release.Task.ConfigureAwait(false);
            harness.Token.ThrowIfCancellationRequested();
            return new(LocalAutomationRunOutcome.Succeeded, "done");
        };
        await harness.MakeDueAsync().ConfigureAwait(false);
        harness.Clock.DrainCreatedTimers();
        await harness.Runner.StartAsync(default).ConfigureAwait(false);
        try
        {
            harness.Clock.Advance(await harness.Clock.WaitForTimerAsync().ConfigureAwait(false));
            await registered.Task.WaitAsync(Deadline).ConfigureAwait(false);
            var stop = harness.Runner.StopAsync(default);
            Assert.IsTrue(harness.Token.IsCancellationRequested);
            Assert.IsFalse(stop.IsCompleted, "The runner must wait for the adapter before reporting cancellation failure.");
            release.SetResult();
            var error = await Assert.ThrowsExactlyAsync<AggregateException>(() => stop.WaitAsync(Deadline)).ConfigureAwait(false);
            Assert.AreSame(failure, error.Flatten().InnerExceptions.Single());
            await harness.Worker.WhenIdleAsync().WaitAsync(Deadline).ConfigureAwait(false);
            using var unexpectedReservation = harness.Worker.TryReserve();
            Assert.IsNull(unexpectedReservation, "The runner must stop admission even when its cancellation callback throws.");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task ShutdownDrainsBeforeReportingCancellationCallbackFailure()
    {
        var worker = new LocalAutomationOffSweepWorker();
        var registered = Signal();
        var cancellationObserved = Signal();
        var release = Signal();
        var failure = new ObjectDisposedException("test callback");
        var occurrence = CustomScriptTaskAdapterTests.Occurrence(CustomScriptTaskAdapterTests.Script());
        using var reservation = worker.TryReserve();
        Assert.IsNotNull(reservation);
        reservation.Dispatch(async token =>
        {
            using var registration = token.Register(() =>
            {
                cancellationObserved.SetResult();
                throw failure;
            });
            registered.SetResult();
            await release.Task.ConfigureAwait(false);
        }, occurrence, occurrence.RunKey, default);
        try
        {
            await registered.Task.WaitAsync(Deadline).ConfigureAwait(false);
            var stop = worker.StopAsync(default);
            await cancellationObserved.Task.WaitAsync(Deadline).ConfigureAwait(false);
            var repeatedStop = worker.StopAsync(default);
            Assert.AreSame(stop, repeatedStop);
            Assert.IsFalse(stop.IsCompleted, "A cancellation callback fault must not bypass the drain.");
            release.SetResult();
            var error = await Assert.ThrowsExactlyAsync<AggregateException>(() => stop.WaitAsync(Deadline)).ConfigureAwait(false);
            Assert.AreSame(failure, error.Flatten().InnerExceptions.Single());
            Assert.IsTrue(worker.WhenIdleAsync().IsCompletedSuccessfully);
        }
        finally
        {
            release.TrySetResult();
            await worker.WhenIdleAsync().WaitAsync(Deadline).ConfigureAwait(false);
            var error = Assert.ThrowsExactly<AggregateException>(worker.Dispose);
            Assert.AreSame(failure, error.Flatten().InnerExceptions.Single());
        }
    }

    [TestMethod]
    public async Task FloorHaltAndSingleFlightRemainEffectiveAcrossWorkerExecution()
    {
        using var orbital = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var harness = await Harness.CreateAsync(CreateRoot(), orbital.Clock).ConfigureAwait(false);
        harness.Registry.Execute = occurrence => orbital.Job.ExecuteAsync(occurrence.RunKey,
            LocalAutomationRunKeys.IsRunNow(occurrence.RunKey) ? OrbitalAttemptTrigger.Manual : OrbitalAttemptTrigger.Scheduled,
            occurrence.ScheduledForUtc, harness.Token);
        await harness.MakeDueAsync().ConfigureAwait(false);
        orbital.Client.EnqueueHang();
        await harness.Runner.SweepAsync(default).ConfigureAwait(false);
        await orbital.Client.RequestStarted.Task.WaitAsync(Deadline).ConfigureAwait(false);
        var next = await harness.Store.RunNowAsync(new("a-orbital", 1, "second", "owner", "test"), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, next.Status);
        await harness.Runner.SweepAsync(default).ConfigureAwait(false);
        Assert.AreEqual(1, orbital.Client.Calls);
        Assert.AreEqual(1, harness.Registry.Occurrences.Count);
        await harness.Runner.StopAsync(default).ConfigureAwait(false);
        StringAssert.StartsWith((await orbital.Job.ExecuteAsync("halted", OrbitalAttemptTrigger.Manual,
            orbital.Clock.GetUtcNow(), default).ConfigureAwait(false)).Detail, "orbital.provider-halted:", StringComparison.Ordinal);
        var state = await orbital.StateAsync().ConfigureAwait(false);
        await orbital.Store.ResumeProviderAsync(new(state.Halt!.SinceUtc, "resume", "owner", "confirmed"), default).ConfigureAwait(false);
        var floor = await orbital.Job.ExecuteAsync("floor", OrbitalAttemptTrigger.Manual, orbital.Clock.GetUtcNow(), default).ConfigureAwait(false);
        StringAssert.StartsWith(floor.Detail, "orbital.floor-not-elapsed:", StringComparison.Ordinal);
        Assert.AreEqual(1, orbital.Client.Calls);
    }

    [TestMethod]
    public async Task NonOptedInDefinitionsKeepInlineSweepSemantics()
    {
        using var harness = await Harness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Registry.OffSweep = false;
        var entered = Signal();
        var release = Signal();
        harness.Registry.Execute = async _ =>
        {
            entered.SetResult();
            await release.Task.ConfigureAwait(false);
            return new(LocalAutomationRunOutcome.Succeeded, "done");
        };
        await harness.MakeDueAsync().ConfigureAwait(false);
        var sweep = harness.Runner.SweepAsync(default);
        await entered.Task.WaitAsync(Deadline).ConfigureAwait(false);
        Assert.IsFalse(sweep.IsCompleted);
        Assert.AreEqual(0, harness.Registry.FollowingCalls);
        release.SetResult();
        await sweep.WaitAsync(Deadline).ConfigureAwait(false);
        Assert.AreEqual(1, harness.Registry.FollowingCalls);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Harness : IDisposable
    {
        private readonly Microsoft.Extensions.Options.IOptions<CameraAgentHostOptions> _options;
        private readonly StubCaptureSequenceSource _captures = new();

        private Harness(string root, SignalingTimeProvider? clock)
        {
            Clock = clock ?? new(OrbitalElementsStoreTestData.Start);
            _options = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            Store = NewStore();
            Runner = NewRunner(Store, Worker);
        }

        public SignalingTimeProvider Clock { get; }
        public Registry Registry { get; } = new();
        public SqliteLocalAutomationStore Store { get; }
        public LocalAutomationOffSweepWorker Worker { get; } = new();
        public LocalAutomationRunnerService Runner { get; }
        public CancellationToken Token => Registry.Token;
        public SqliteLocalAutomationStore NewStore() => new(Registry, _captures, _options, Clock, NullLogger<SqliteLocalAutomationStore>.Instance);
        public LocalAutomationRunnerService NewRunner(SqliteLocalAutomationStore store, LocalAutomationOffSweepWorker worker)
            => new(store, Registry, _captures, _options, Clock, NullLogger<LocalAutomationRunnerService>.Instance, offSweepWorker: worker);
        public static async Task<Harness> CreateAsync(string root, SignalingTimeProvider? clock = null)
        {
            var harness = new Harness(root, clock);
            await harness.Store.InitializeAsync(default).ConfigureAwait(false);
            return harness;
        }
        public async Task SaveAsync()
        {
            foreach (var id in new[] { "a-orbital", "z-other" })
            {
                var saved = await Store.SaveAsync(new(id, id, true, LocalAutomationTaskKind.OrbitalElementsRefresh,
                    "synthetic", LocalAutomationTriggerKind.Periodic, 7200, 0, id, "owner", "test"), default).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status, saved.ReasonCode);
            }
        }
        public async Task MakeDueAsync()
        {
            await SaveAsync().ConfigureAwait(false);
            Clock.Advance(TimeSpan.FromHours(2));
        }
        public Task<LocalAutomationOperatorState> StateAsync() => Store.GetStateAsync(default).AsTask();
        public void Dispose()
        {
            Worker.Dispose();
            Runner.Dispose();
            Store.Dispose();
        }
    }

    private sealed class Registry : ILocalAutomationTaskRegistry
    {
        public bool OffSweep { get; set; } = true;
        public int FollowingCalls { get; private set; }
        public List<LocalAutomationOccurrence> Occurrences { get; } = [];
        public CancellationToken Token { get; private set; }
        public Func<LocalAutomationOccurrence, ValueTask<LocalAutomationExecution>> Execute { get; set; }
            = _ => ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Succeeded, "done"));
        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() =>
            [new(LocalAutomationTaskKind.OrbitalElementsRefresh, "Synthetic", [LocalAutomationTriggerKind.Periodic], ["synthetic"], true, null)];
        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition) => null;
        public bool RunsOffSweep(LocalAutomationDefinition definition) => OffSweep && definition.DefinitionId == "a-orbital";
        public bool SupportsRunNow(LocalAutomationDefinition definition) => true;
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string runKey, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The durable occurrence must be retained.");
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
        {
            if (occurrence.Definition.DefinitionId == "z-other")
            {
                FollowingCalls++;
                return ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Succeeded, "following"));
            }
            Occurrences.Add(occurrence);
            Token = cancellationToken;
            return Execute(occurrence);
        }
    }
}
