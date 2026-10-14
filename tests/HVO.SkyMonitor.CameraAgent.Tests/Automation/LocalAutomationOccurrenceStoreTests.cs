using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class LocalAutomationOccurrenceStoreTests : IDisposable
{
    private string _root = null!;
    private MutableTimeProvider _time = null!;
    private WindowRegistry _registry = null!;
    private MutableCalendar _calendar = null!;
    private LocalAutomationWindowPlanner _planner = null!;
    private SqliteLocalAutomationStore _store = null!;
    private readonly StubCaptureSequenceSource _captures = new();

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "hvo-occurrence-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _time = new(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        _registry = new();
        _calendar = new();
        _planner = new(_calendar);
        _store = CreateStore();
        await _store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [TestMethod]
    public async Task HourlyAndDailySameKind_RunIndependentlyWithExactPreviewsAndNoCatchupBurst()
    {
        await SaveAsync("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour).ConfigureAwait(false);
        await SaveAsync("daily", LocalAutomationSourceWindowKind.SunriseDay).ConfigureAwait(false);
        var before = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, before.Definitions.Count);
        Assert.IsTrue(before.Definitions.All(static item => item.NextOccurrence is not null));
        Assert.IsTrue(before.Calendar.All(static item => item.Occurrence?.ScheduledForUtc == item.DueUtc));
        var daily = _planner.ResolveWindows(new DateOnly(2026, 10, 12),
            LocalAutomationWindowTests.Policy(LocalAutomationSourceWindowKind.SunriseDay)).Single();
        MoveTo(daily.EarliestFinalUtc);
        using var runner = CreateRunner();
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, _registry.Executed.Count);
        var full = _registry.Executed.Single(static item => item.Definition.DefinitionId == "daily");
        var hour = _registry.Executed.Single(static item => item.Definition.DefinitionId == "hourly");
        Assert.AreEqual(daily.StartUtc, full.SourceWindow!.StartUtc);
        Assert.AreEqual(daily.EndUtc, full.SourceWindow.EndUtc);
        Assert.AreEqual(daily.EndUtc, hour.SourceWindow!.EndUtc);
        Assert.IsTrue(hour.SourceWindow.StartUtc > full.SourceWindow.StartUtc);
        Assert.AreNotEqual(hour.RunKey, full.RunKey);
        var after = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, after.Runs.Count(static run => run.Outcome == LocalAutomationRunOutcome.Succeeded));
        Assert.IsTrue(after.Runs.Any(static run => run.Outcome == LocalAutomationRunOutcome.Missed && run.StartedAtUtc is null));
        Assert.IsTrue(after.Definitions.All(item => item.NextRunUtc > _time.GetUtcNow()));
    }

    [TestMethod]
    public async Task RetryAcrossRestartAndChangedSiteAndDefinition_UsesOriginalOccurrenceAndAdapterKey()
    {
        await SaveAsync("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour).ConfigureAwait(false);
        var entry = (await _store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        var window = _planner.ResolveWindows(new DateOnly(2026, 10, 12), entry.Definition.SourceWindow!)[0];
        var original = LocalAutomationWindowPlanner.CreateOccurrence(entry, window);
        MoveTo(original.ScheduledForUtc);
        Assert.IsTrue(await _store.TryBeginOccurrenceAsync(original, CancellationToken.None).ConfigureAwait(false));
        await _store.CompleteRunAsync(original.RunKey, LocalAutomationRunOutcome.Failed, "Fixture failure", CancellationToken.None)
            .ConfigureAwait(false);
        await SaveAsync("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour, 1, TimeSpan.FromMinutes(20))
            .ConfigureAwait(false);
        _calendar.Current = ObservingDayCalendar.ForDeployment(LocalAutomationWindowTests.Site(
            "America/Denver", 40, -105, 2));
        var request = new LocalAutomationRetryRequest("hourly", original.RunKey, 2, "retry-key", "owner", "retry retained source");
        var queued = await _store.RetryAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, queued.Status);
        var attempt = queued.State.Runs.Single(static run => run.Outcome == LocalAutomationRunOutcome.Queued);
        Assert.IsNull(attempt.StartedAtUtc);
        Assert.AreEqual(original, attempt.Occurrence);
        Assert.AreEqual(2, attempt.Attempt);
        Assert.AreEqual("retry retained source", attempt.Preparation!.Reason);
        _store.Dispose();
        _store = CreateStore();
        await _store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Replayed,
            (await _store.RetryAsync(request, CancellationToken.None).ConfigureAwait(false)).Status);
        using var runner = CreateRunner();
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(original, _registry.Executed.Single());
        var settled = (await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs
            .Single(static run => run.Attempt == 2);
        Assert.AreEqual(original.RunKey + ":retry2", settled.RunKey);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, settled.Outcome);
        Assert.AreEqual(window.ReportingPeriod.Site.CanonicalSha256, settled.Occurrence!.SourceWindow!.ReportingPeriod.Site.CanonicalSha256);
    }

    [TestMethod]
    public async Task Backfill_IsFinalityVersionAndIdentityBoundedAndDoesNotAdvanceAutomaticCursor()
    {
        await SaveAsync("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour).ConfigureAwait(false);
        var policy = LocalAutomationWindowTests.Policy(LocalAutomationSourceWindowKind.CompletedCivilHour);
        var window = _planner.ResolveWindows(new DateOnly(2026, 10, 12), policy)[0];
        var request = new LocalAutomationBackfillRequest("hourly", window.ReportingPeriod.ReportDate,
            window.StartUtc, 1, "backfill-key", "owner", "explicit missing coverage");
        Assert.AreEqual("automation.finality-not-reached",
            (await _store.BackfillAsync(request, CancellationToken.None).ConfigureAwait(false)).ReasonCode);
        MoveTo(window.EarliestFinalUtc);
        Assert.AreEqual(LocalAutomationCommandStatus.Conflict,
            (await _store.BackfillAsync(request with { ExpectedVersion = 2 }, CancellationToken.None).ConfigureAwait(false)).Status);
        var before = (await _store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single().LastOccurrenceUtc;
        Assert.AreEqual(LocalAutomationCommandStatus.Applied,
            (await _store.BackfillAsync(request, CancellationToken.None).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Replayed,
            (await _store.BackfillAsync(request, CancellationToken.None).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Conflict,
            (await _store.BackfillAsync(request with { Reason = "changed payload" }, CancellationToken.None).ConfigureAwait(false)).Status);
        using var runner = CreateRunner();
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(before, (await _store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single().LastOccurrenceUtc);
        Assert.AreEqual(window.IdentitySha256, _registry.Executed.Single().SourceWindow!.IdentitySha256);
    }

    [TestMethod]
    public async Task CancellationIgnoringExecution_KeepsSoleAuthorityUntilActualDrainAndRestartMarksInterrupted()
    {
        await SaveAsync("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour).ConfigureAwait(false);
        var window = _planner.ResolveWindows(new DateOnly(2026, 10, 12),
            LocalAutomationWindowTests.Policy(LocalAutomationSourceWindowKind.CompletedCivilHour))[0];
        MoveTo(window.EarliestFinalUtc);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource<LocalAutomationExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registry.Executor = (_, _) => { entered.SetResult(); return new(drained.Task); };
        using var cancellation = new CancellationTokenSource();
        using var runner = CreateRunner();
        var sweep = runner.SweepAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        _store.Dispose();
        using var replacement = CreateStore();
        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => replacement.InitializeAsync(CancellationToken.None).AsTask())
                .ConfigureAwait(false);
        }
        finally
        {
            drained.TrySetResult(new(LocalAutomationRunOutcome.Succeeded, "drained"));
        }
        await sweep.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await replacement.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var state = await replacement.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        var actualRun = state.Runs.Single(static run => run.Outcome != LocalAutomationRunOutcome.Missed);
        Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, actualRun.Outcome);
        Assert.AreEqual(_registry.Executed.Single().RunKey, actualRun.RunKey);
        Assert.AreEqual(1, _registry.Executed.Count);
    }

    [TestMethod]
    public async Task DisposalDuringACommittingCommand_KeepsAuthorityUntilTheWriteDrains()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        _registry.ValidationHook = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test save barrier did not drain.");
                }
            }
        };
        var save = Task.Run(() => SaveAsync("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _store.Dispose();
        using var replacement = CreateStore();
        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => replacement.InitializeAsync(CancellationToken.None).AsTask())
                .ConfigureAwait(false);
        }
        finally
        {
            release.Set();
        }
        await save.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await replacement.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("hourly", (await replacement.GetStateAsync(CancellationToken.None).ConfigureAwait(false))
            .Definitions.Single().Definition.DefinitionId);
    }

    private SqliteLocalAutomationStore CreateStore() => new(_registry, _captures,
        Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root }), _time,
        NullLogger<SqliteLocalAutomationStore>.Instance, windowPlanner: _planner);

    [TestMethod]
    public async Task Retry_StopsAfterThreeAttemptsAndNeverChangesTheAdapterOccurrenceKey()
    {
        await SaveAsync("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour).ConfigureAwait(false);
        var entry = (await _store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        var window = _planner.ResolveWindows(new DateOnly(2026, 10, 12), entry.Definition.SourceWindow!)[0];
        var original = LocalAutomationWindowPlanner.CreateOccurrence(entry, window);
        MoveTo(original.ScheduledForUtc);
        Assert.IsTrue(await _store.TryBeginOccurrenceAsync(original, CancellationToken.None).ConfigureAwait(false));
        await _store.CompleteRunAsync(original.RunKey, LocalAutomationRunOutcome.Failed, "initial failure", CancellationToken.None)
            .ConfigureAwait(false);
        _registry.Executor = (_, _) => ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Failed, "fixture failure"));
        using var runner = CreateRunner();
        var previous = original.RunKey;
        for (var attempt = 2; attempt <= SqliteLocalAutomationStore.MaximumOccurrenceAttempts; attempt++)
        {
            var command = new LocalAutomationRetryRequest("hourly", previous, 1, "retry-attempt-" + attempt,
                "owner", "retry same input");
            Assert.AreEqual(LocalAutomationCommandStatus.Applied,
                (await _store.RetryAsync(command, CancellationToken.None).ConfigureAwait(false)).Status);
            await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
            previous = original.RunKey + ":retry" + attempt;
        }
        Assert.AreEqual("automation.retry-limit-or-stale-attempt",
            (await _store.RetryAsync(new("hourly", previous, 1, "retry-too-many", "owner", "bounded retry"),
                CancellationToken.None).ConfigureAwait(false)).ReasonCode);
        Assert.AreEqual(2, _registry.Executed.Count);
        Assert.IsTrue(_registry.Executed.All(item => item == original));
    }

    [TestMethod]
    public async Task InvalidAdapterOutcome_SettlesAsFailedInsteadOfLeavingAnActiveClaim()
    {
        await SaveAsync("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour).ConfigureAwait(false);
        var window = _planner.ResolveWindows(new DateOnly(2026, 10, 12),
            LocalAutomationWindowTests.Policy(LocalAutomationSourceWindowKind.CompletedCivilHour))[0];
        MoveTo(window.EarliestFinalUtc);
        _registry.Executor = (_, _) => ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Queued, "invalid terminal result"));
        using var runner = CreateRunner();
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, state.RunningRunCount);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed,
            state.Runs.Single(static run => run.Outcome != LocalAutomationRunOutcome.Missed).Outcome);
    }

    [TestMethod]
    public async Task QueueCapAndTerminalRetention_PreservePreparedWorkAndExactTotalsOutsideTheRecentPage()
    {
        await SaveAsync("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour).ConfigureAwait(false);
        var entry = (await _store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        var windows = _planner.ResolveWindows(new DateOnly(2026, 10, 12), entry.Definition.SourceWindow!)
            .Concat(_planner.ResolveWindows(new DateOnly(2026, 10, 13), entry.Definition.SourceWindow!))
            .Take(SqliteLocalAutomationStore.MaximumQueuedOccurrencesPerDefinition + 1).ToArray();
        MoveTo(windows[^1].EarliestFinalUtc);
        for (var index = 0; index < windows.Length; index++)
        {
            var window = windows[index];
            var result = await _store.BackfillAsync(new("hourly", window.ReportingPeriod.ReportDate,
                window.StartUtc, 1, "queue-" + index, "owner", "bounded backfill"), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(index < SqliteLocalAutomationStore.MaximumQueuedOccurrencesPerDefinition
                ? LocalAutomationCommandStatus.Applied : LocalAutomationCommandStatus.Conflict, result.Status);
        }
        var first = windows[0];
        Assert.AreEqual(LocalAutomationCommandStatus.Unchanged,
            (await _store.BackfillAsync(new("hourly", first.ReportingPeriod.ReportDate, first.StartUtc,
                1, "queue-alias-at-cap", "owner", "same already queued window"), CancellationToken.None).ConfigureAwait(false)).Status);
        for (var index = 0; index <= LocalAutomationContract.MaximumRetainedRuns; index++)
        {
            await _store.RecordTerminalRunAsync(entry, "retention-" + index, _time.GetUtcNow(),
                LocalAutomationRunOutcome.Missed, "administrative missed summary", null, CancellationToken.None).ConfigureAwait(false);
        }
        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SqliteLocalAutomationStore.MaximumQueuedOccurrencesPerDefinition, state.QueuedRunCount);
        Assert.AreEqual(LocalAutomationContract.MaximumProjectedRuns, state.Runs.Count);
        Assert.IsFalse(state.Runs.Any(static run => run.Outcome == LocalAutomationRunOutcome.Queued));
        var recorded = await _store.GetRecordedOccurrenceIdentitiesAsync(entry, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SqliteLocalAutomationStore.MaximumQueuedOccurrencesPerDefinition, recorded.Count);
        Assert.IsTrue(recorded.Contains(LocalAutomationWindowPlanner.CreateOccurrence(entry, first).IdentitySha256),
            "Accounting reads retained identities outside the recent operator page.");
        var claimed = await _store.TryClaimQueuedAsync(entry, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.IdentitySha256, claimed!.Occurrence!.SourceWindow!.IdentitySha256);
        var running = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, running.RunningRunCount);
        Assert.AreEqual(SqliteLocalAutomationStore.MaximumQueuedOccurrencesPerDefinition - 1, running.QueuedRunCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SuccessfulBackfill_IsNotCountedAsMissedAndDoesNotReplay(bool retry)
    {
        var windows = _planner.ResolveWindows(new DateOnly(2026, 10, 12),
            LocalAutomationWindowTests.Policy(LocalAutomationSourceWindowKind.CompletedCivilHour));
        MoveTo(windows[0].EarliestFinalUtc.AddTicks(-1));
        await SaveAsync("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour).ConfigureAwait(false);
        MoveTo(windows[2].EarliestFinalUtc);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied,
            (await _store.BackfillAsync(new("hourly", windows[0].ReportingPeriod.ReportDate, windows[0].StartUtc,
                1, "missed-accounting-backfill", "owner", "fill W1"), CancellationToken.None).ConfigureAwait(false)).Status);
        if (retry)
        {
            _registry.Executor = (_, _) => ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Failed, "retry fixture"));
        }
        using var runner = CreateRunner();
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        if (retry)
        {
            var failed = (await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single();
            Assert.AreEqual(LocalAutomationCommandStatus.Applied,
                (await _store.RetryAsync(new("hourly", failed.RunKey, 1, "missed-accounting-retry", "owner", "retry W1"),
                    CancellationToken.None).ConfigureAwait(false)).Status);
            _registry.Executor = null;
            await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        var missed = state.Runs.Single(static run => run.Outcome == LocalAutomationRunOutcome.Missed);
        StringAssert.StartsWith(missed.Detail, "1 closed window(s)", StringComparison.Ordinal);
        Assert.AreEqual(retry ? 3 : 2, _registry.Executed.Count);
        Assert.AreEqual(windows[0].IdentitySha256, _registry.Executed[0].SourceWindow!.IdentitySha256);
        Assert.AreEqual(windows[2].IdentitySha256, _registry.Executed[^1].SourceWindow!.IdentitySha256);
    }

    private LocalAutomationRunnerService CreateRunner() => new(_store, _registry, _captures,
        Options.Create(new CameraAgentHostOptions()), _time,
        NullLogger<LocalAutomationRunnerService>.Instance, windowPlanner: _planner);

    private void MoveTo(DateTimeOffset utc) => _time.Advance(utc - _time.GetUtcNow());

    private async Task SaveAsync(string id, LocalAutomationSourceWindowKind kind, long version = 0, TimeSpan? settle = null)
    {
        var policy = LocalAutomationWindowTests.Policy(kind) with { ProcessingSettleAllowance = settle ?? TimeSpan.Zero };
        var result = await _store.SaveAsync(new(id, id, true, LocalAutomationTaskKind.StillImageGeneration,
            "test-rig", LocalAutomationTriggerKind.SourceWindowClosed, 1, version, Guid.NewGuid().ToString("N"),
            "owner", "test schedule", policy), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, result.Status);
    }

    private sealed class MutableCalendar : IObservingDayCalendarProvider
    {
        public ObservingDayCalendar Current { get; set; } = ObservingDayCalendar.ForDeployment(LocalAutomationWindowTests.Site());
    }

    private sealed class WindowRegistry : ILocalAutomationTaskRegistry
    {
        public List<LocalAutomationOccurrence> Executed { get; } = [];
        public Action? ValidationHook { get; set; }
        public Func<LocalAutomationOccurrence, CancellationToken, ValueTask<LocalAutomationExecution>>? Executor { get; set; }

        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() =>
        [new(LocalAutomationTaskKind.StillImageGeneration, "Installed fixture", [LocalAutomationTriggerKind.SourceWindowClosed],
            ["test-rig"], true, null) { SupportedSourceWindows = [LocalAutomationSourceWindowKind.CompletedCivilHour, LocalAutomationSourceWindowKind.SunriseDay] }];

        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
        {
            ValidationHook?.Invoke();
            return LocalAutomationDefinitionValidator.Validate(definition);
        }

        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string runKey,
            CancellationToken cancellationToken) => throw new InvalidOperationException("A window task requires an occurrence.");

        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
        {
            Executed.Add(occurrence);
            return Executor?.Invoke(occurrence, cancellationToken) ?? ValueTask.FromResult(new LocalAutomationExecution(
                LocalAutomationRunOutcome.Succeeded, "actual fixture execution"));
        }
    }
}
