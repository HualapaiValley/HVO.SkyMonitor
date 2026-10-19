using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

/// <summary>
/// Covers the periodic task seam in the durable store: a registered task chooses the phase origin of a first
/// save only, and an explicit run-now queues one occurrence through the existing claim, retry, and restart path
/// without moving the periodic schedule or its missed-occurrence accounting.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class LocalAutomationRunNowTests : IDisposable
{
    private const string DefinitionId = "orbital-elements-refresh";
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private string _root = null!;
    private MutableTimeProvider _time = null!;
    private FakePeriodicTaskAdapter _adapter = null!;
    private PeriodicRegistryHarness _harness = null!;
    private SqliteLocalAutomationStore _store = null!;
    private readonly StubCaptureSequenceSource _captures = new();

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "hvo-run-now-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _time = new(Start);
        _adapter = new();
        _harness = new(_time, [_adapter]);
        _store = CreateStore();
        await _store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _store.Dispose();
        _harness.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [TestMethod]
    public async Task FirstSave_UsesTheAdapterPhaseOriginWhileReplaysAndLaterSavesKeepIt()
    {
        var origin = Start.AddMinutes(17).AddMilliseconds(250);
        _adapter.Epoch = now => now.AddMinutes(17).AddMilliseconds(250);
        var saved = await SaveAsync(key: "save-first").ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
        var state = saved.State.Definitions.Single();
        Assert.AreEqual(origin, state.Definition.TriggerEpochUtc);
        Assert.AreEqual(TimeSpan.Zero, state.Definition.TriggerEpochUtc.Offset);
        Assert.AreEqual(origin.AddSeconds(7_200), state.NextRunUtc);
        Assert.AreEqual(1, _adapter.EpochCalls);

        _time.Advance(TimeSpan.FromMinutes(5));
        _adapter.Epoch = now => now.AddMinutes(1);
        var replay = await SaveAsync(key: "save-first").ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Replayed, replay.Status);
        Assert.AreEqual(origin, replay.State.Definitions.Single().Definition.TriggerEpochUtc);
        var renamed = await SaveAsync(1, name: "Orbital refresh", key: "save-second").ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, renamed.Status);
        Assert.AreEqual(2, renamed.State.Definitions.Single().Version);
        Assert.AreEqual(origin, renamed.State.Definitions.Single().Definition.TriggerEpochUtc);
        Assert.AreEqual(1, _adapter.EpochCalls, "Only a definition's first save consults its registered task.");
    }

    [TestMethod]
    [DataRow("past")]
    [DataRow("sub-millisecond")]
    [DataRow("non-utc-offset")]
    [DataRow("one-interval-later")]
    public async Task FirstSave_RejectsAPhaseOriginOutsideTheBoundedFutureWithoutStoringIt(string scenario)
    {
        _adapter.Epoch = scenario switch
        {
            "past" => static now => now.AddMilliseconds(-1),
            "sub-millisecond" => static now => now.AddTicks(5_000),
            "non-utc-offset" => static now => now.AddMinutes(1).ToOffset(TimeSpan.FromHours(-7)),
            _ => static now => now.AddSeconds(7_200)
        };

        var rejected = await SaveAsync(key: "save-rejected").ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Invalid, rejected.Status);
        Assert.AreEqual(LocalAutomationContract.InvalidTriggerEpochReasonCode, rejected.ReasonCode);
        Assert.AreEqual("definition.triggerEpochUtc", rejected.FieldPath);
        Assert.IsEmpty(rejected.State.Definitions);
        _adapter.Epoch = null;
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await SaveAsync(key: "save-accepted").ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task RunNow_QueuesClaimsAndCompletesOneOccurrenceWithoutMovingThePeriodicSchedule()
    {
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await SaveAsync().ConfigureAwait(false)).Status);
        _time.Advance(TimeSpan.FromMinutes(10));
        var requestedAt = _time.GetUtcNow();
        var queued = await _store.RunNowAsync(RunNow("run-now-1"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, queued.Status);
        var prepared = queued.State.Runs.Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Queued, prepared.Outcome);
        Assert.IsTrue(LocalAutomationRunKeys.IsRunNow(prepared.RunKey));
        Assert.AreEqual(LocalAutomationTriggerKind.Periodic, prepared.TriggerKind);
        Assert.AreEqual(requestedAt, prepared.ScheduledForUtc);
        Assert.IsNull(prepared.StartedAtUtc);
        Assert.AreEqual(1, prepared.Attempt);
        Assert.AreEqual("run-now", prepared.Preparation!.Operation);
        Assert.AreEqual("operator refresh", prepared.Preparation.Reason);
        Assert.AreEqual(1, queued.State.QueuedRunCount);

        using var runner = CreateRunner();
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        var executed = _adapter.Executed.Single();
        Assert.AreEqual(prepared.Occurrence, executed);
        Assert.AreEqual(prepared.RunKey, executed.RunKey);
        var settled = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, settled.Runs.Single().Outcome);
        Assert.AreEqual(prepared.RunKey, settled.Runs.Single().RunKey);
        Assert.AreEqual(0, settled.QueuedRunCount);
        Assert.AreEqual(0, settled.RunningRunCount);
        Assert.IsNull((await Entry().ConfigureAwait(false)).LastOccurrenceUtc, "A run-now never writes periodic progress.");
        Assert.AreEqual(Start.AddSeconds(7_200), settled.Definitions.Single().NextRunUtc);

        var second = await _store.RunNowAsync(RunNow("run-now-2"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, second.Status);
        var secondKey = second.State.Runs.Single(static run => run.Outcome == LocalAutomationRunOutcome.Queued).RunKey;
        Assert.AreNotEqual(prepared.RunKey, secondKey);
        Assert.IsTrue(LocalAutomationRunKeys.IsRunNow(secondKey));
        _time.Advance(Start.AddSeconds(7_200) - _time.GetUtcNow());
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(3, _adapter.Executed);
        Assert.AreEqual(secondKey, _adapter.Executed[1].RunKey);
        var scheduled = _adapter.Executed[2];
        Assert.IsFalse(LocalAutomationRunKeys.IsRunNow(scheduled.RunKey));
        Assert.AreEqual(Start.AddSeconds(7_200), scheduled.ScheduledForUtc);
        var final = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(final.Runs.Any(static run => run.Outcome == LocalAutomationRunOutcome.Missed));
        Assert.HasCount(3, final.Runs.Where(static run => run.Outcome == LocalAutomationRunOutcome.Succeeded).ToArray());
        Assert.AreEqual(Start.AddSeconds(7_200), (await Entry().ConfigureAwait(false)).LastOccurrenceUtc);
    }

    [TestMethod]
    public async Task RunNow_AfterElapsedOccurrences_LeavesMissedAccountingToThePeriodicSchedule()
    {
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await SaveAsync().ConfigureAwait(false)).Status);
        _time.Advance(TimeSpan.FromHours(5));
        Assert.AreEqual(LocalAutomationCommandStatus.Applied,
            (await _store.RunNowAsync(RunNow("run-now-late"), CancellationToken.None).ConfigureAwait(false)).Status);
        using var runner = CreateRunner();

        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        var afterRunNow = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(LocalAutomationRunKeys.IsRunNow(_adapter.Executed.Single().RunKey));
        Assert.IsFalse(afterRunNow.Runs.Any(static run => run.Outcome == LocalAutomationRunOutcome.Missed));
        Assert.IsNull((await Entry().ConfigureAwait(false)).LastOccurrenceUtc);
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        var afterSchedule = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        var missed = afterSchedule.Runs.Single(static run => run.Outcome == LocalAutomationRunOutcome.Missed);
        Assert.IsFalse(LocalAutomationRunKeys.IsRunNow(missed.RunKey));
        StringAssert.StartsWith(missed.Detail, "No run was recorded for 1 elapsed occurrence(s)", StringComparison.Ordinal);
        Assert.AreEqual(Start.AddHours(4), _adapter.Executed[^1].ScheduledForUtc);
        Assert.AreEqual(Start.AddHours(4), (await Entry().ConfigureAwait(false)).LastOccurrenceUtc);
    }

    [TestMethod]
    public async Task RunNow_RejectsAnInvalidUnknownStalePendingDisabledOrUnsupportedRequest()
    {
        Assert.AreEqual(LocalAutomationContract.UnknownDefinitionReasonCode,
            (await _store.RunNowAsync(RunNow("unknown"), CancellationToken.None).ConfigureAwait(false)).ReasonCode);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await SaveAsync().ConfigureAwait(false)).Status);
        await AssertRejectedAsync(RunNow("no-actor") with { Actor = string.Empty },
            LocalAutomationCommandStatus.Invalid, LocalAutomationContract.InvalidCommandReasonCode).ConfigureAwait(false);
        await AssertRejectedAsync(RunNow("no-version", 0),
            LocalAutomationCommandStatus.Invalid, LocalAutomationContract.InvalidCommandReasonCode).ConfigureAwait(false);
        await AssertRejectedAsync(RunNow("stale", 2),
            LocalAutomationCommandStatus.Conflict, LocalAutomationContract.ExpectedVersionConflictReasonCode).ConfigureAwait(false);
        _adapter.SupportsRunNow = false;
        await AssertRejectedAsync(RunNow("unsupported"),
            LocalAutomationCommandStatus.Conflict, LocalAutomationContract.RunNowUnavailableReasonCode).ConfigureAwait(false);
        _adapter.SupportsRunNow = true;
        _adapter.MinimumIntervalSeconds = 10_800;
        await AssertRejectedAsync(RunNow("no-longer-accepted"),
            LocalAutomationCommandStatus.Conflict, LocalAutomationContract.RunNowUnavailableReasonCode).ConfigureAwait(false);
        _adapter.MinimumIntervalSeconds = 7_200;

        Assert.AreEqual(LocalAutomationCommandStatus.Applied,
            (await _store.RunNowAsync(RunNow("first"), CancellationToken.None).ConfigureAwait(false)).Status);
        await AssertRejectedAsync(RunNow("while-queued"),
            LocalAutomationCommandStatus.Conflict, LocalAutomationContract.RunNowPendingReasonCode).ConfigureAwait(false);
        var claimed = await _store.TryClaimQueuedAsync(await Entry().ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Running, claimed!.Outcome);
        await AssertRejectedAsync(RunNow("while-running"),
            LocalAutomationCommandStatus.Conflict, LocalAutomationContract.RunNowPendingReasonCode).ConfigureAwait(false);
        await _store.CompleteRunAsync(claimed.RunKey, LocalAutomationRunOutcome.Succeeded, "fixture", CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied,
            (await _store.RunNowAsync(RunNow("after-settled"), CancellationToken.None).ConfigureAwait(false)).Status);

        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await SaveAsync(1, enabled: false).ConfigureAwait(false)).Status);
        await AssertRejectedAsync(RunNow("disabled", 2),
            LocalAutomationCommandStatus.Conflict, LocalAutomationContract.RunNowUnavailableReasonCode).ConfigureAwait(false);
        Assert.IsEmpty(_adapter.Executed);
    }

    [TestMethod]
    public async Task RunNow_IsUnavailableForANonPeriodicDefinitionOrARegistryWithoutRunNow()
    {
        var root = Path.Combine(_root, "stub");
        Directory.CreateDirectory(root);
        var registry = new StubAutomationTaskRegistry();
        using var store = new SqliteLocalAutomationStore(registry, _captures,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }), _time,
            NullLogger<SqliteLocalAutomationStore>.Instance);
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var (id, trigger, interval) in new[]
        {
            ("capture", LocalAutomationTriggerKind.CaptureRelative, 10),
            ("periodic", LocalAutomationTriggerKind.Periodic, 3_600)
        })
        {
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await store.SaveAsync(new(id, id, true,
                LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, "virtual-sky-temperature", trigger, interval, 0,
                "save-" + id, "owner", null), CancellationToken.None).ConfigureAwait(false)).Status);
            var result = await store.RunNowAsync(new(id, 1, "run-now-" + id, "owner", "operator refresh"),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationCommandStatus.Conflict, result.Status);
            Assert.AreEqual(LocalAutomationContract.RunNowUnavailableReasonCode, result.ReasonCode);
            Assert.AreEqual(0, result.State.QueuedRunCount);
        }
        Assert.IsEmpty(registry.ExecutedRunKeys);
    }

    [TestMethod]
    public async Task RunNow_RespectsTheDefinitionQueueCapacity()
    {
        _adapter.MinimumIntervalSeconds = 60;
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await SaveAsync(interval: 60).ConfigureAwait(false)).Status);
        _adapter.Executor = static (_, _) => ValueTask.FromResult(new LocalAutomationExecution(
            LocalAutomationRunOutcome.Failed, "fixture failure"));
        using var runner = CreateRunner();
        for (var index = 0; index < SqliteLocalAutomationStore.MaximumQueuedOccurrencesPerDefinition; index++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var failed = (await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs;
        Assert.HasCount(SqliteLocalAutomationStore.MaximumQueuedOccurrencesPerDefinition,
            failed.Where(static run => run.Outcome == LocalAutomationRunOutcome.Failed).ToArray());
        foreach (var run in failed)
        {
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await _store.RetryAsync(new(DefinitionId, run.RunKey, 1,
                "retry-" + run.Sequence, "owner", "retry failed refresh"), CancellationToken.None).ConfigureAwait(false)).Status);
        }

        var full = await _store.RunNowAsync(RunNow("at-capacity"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Conflict, full.Status);
        Assert.AreEqual("automation.queue-capacity", full.ReasonCode);
        Assert.AreEqual(SqliteLocalAutomationStore.MaximumQueuedOccurrencesPerDefinition, full.State.QueuedRunCount);
        Assert.IsFalse(full.State.Runs.Any(static run => LocalAutomationRunKeys.IsRunNow(run.RunKey)));
    }

    [TestMethod]
    public async Task RunNow_ReplaysTheSameCommandAcrossRestartAndRejectsAChangedPayload()
    {
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await SaveAsync().ConfigureAwait(false)).Status);
        var request = RunNow("run-now-replay");
        var applied = await _store.RunNowAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, applied.Status);
        var runKey = applied.State.Runs.Single().RunKey;
        _time.Advance(TimeSpan.FromSeconds(30));

        var replayed = await _store.RunNowAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Replayed, replayed.Status);
        Assert.AreEqual(runKey, replayed.State.Runs.Single().RunKey);
        Assert.AreEqual(1, replayed.State.QueuedRunCount);
        await AssertRejectedAsync(request with { Reason = "a different reason" },
            LocalAutomationCommandStatus.Conflict, LocalAutomationContract.IdempotencyKeyConflictReasonCode).ConfigureAwait(false);
        await AssertRejectedAsync(request with { Actor = "another-owner" },
            LocalAutomationCommandStatus.Conflict, LocalAutomationContract.IdempotencyKeyConflictReasonCode).ConfigureAwait(false);

        _store.Dispose();
        _store = CreateStore();
        await _store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Replayed,
            (await _store.RunNowAsync(request, CancellationToken.None).ConfigureAwait(false)).Status);
        using var runner = CreateRunner();
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        var afterRun = await _store.RunNowAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Replayed, afterRun.Status);
        Assert.AreEqual(0, afterRun.State.QueuedRunCount);
        Assert.AreEqual(runKey, _adapter.Executed.Single().RunKey);
    }

    [TestMethod]
    public async Task RunNow_InterruptedByRestartIsSettledAndOnlyOneRunNowIsPendingAcrossItsRetry()
    {
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await SaveAsync().ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied,
            (await _store.RunNowAsync(RunNow("first"), CancellationToken.None).ConfigureAwait(false)).Status);
        var claimed = await _store.TryClaimQueuedAsync(await Entry().ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(LocalAutomationRunKeys.IsRunNow(claimed!.RunKey));

        _store.Dispose();
        _store = CreateStore();
        await _store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var restarted = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, restarted.Runs.Single().Outcome);
        Assert.AreEqual(0, restarted.RunningRunCount);
        var retried = await _store.RetryAsync(new(DefinitionId, claimed.RunKey, 1, "retry-first", "owner",
            "retry interrupted refresh"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, retried.Status);
        await AssertRejectedAsync(RunNow("while-retry-queued"),
            LocalAutomationCommandStatus.Conflict, LocalAutomationContract.RunNowPendingReasonCode).ConfigureAwait(false);

        using var runner = CreateRunner();
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(claimed.Occurrence, _adapter.Executed.Single());
        var settled = (await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs
            .Single(static run => run.Attempt == 2);
        Assert.AreEqual(claimed.RunKey + ":retry2", settled.RunKey);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, settled.Outcome);
        var next = await _store.RunNowAsync(RunNow("after-retry"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, next.Status);
        var nextKey = next.State.Runs.Single(static run => run.Outcome == LocalAutomationRunOutcome.Queued).RunKey;
        Assert.AreNotEqual(claimed.RunKey, nextKey);
        Assert.IsTrue(LocalAutomationRunKeys.IsRunNow(nextKey));
        Assert.IsNull((await Entry().ConfigureAwait(false)).LastOccurrenceUtc);
    }

    [TestMethod]
    public async Task RunNow_QueuedBeforeAnEdit_RunsItsRetainedInputsAndReleasesThePendingBound()
    {
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await SaveAsync().ConfigureAwait(false)).Status);
        var queued = await _store.RunNowAsync(RunNow("before-edit"), CancellationToken.None).ConfigureAwait(false);
        var prepared = queued.State.Runs.Single();
        Assert.AreEqual(LocalAutomationCommandStatus.Applied,
            (await SaveAsync(1, name: "Orbital refresh", key: "edit").ConfigureAwait(false)).Status);
        await AssertRejectedAsync(RunNow("after-edit", 2),
            LocalAutomationCommandStatus.Conflict, LocalAutomationContract.RunNowPendingReasonCode).ConfigureAwait(false);

        using var runner = CreateRunner();
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        var executed = _adapter.Executed.Single();
        Assert.AreEqual(prepared.RunKey, executed.RunKey);
        Assert.AreEqual("Orbital elements refresh", executed.Definition.Name);
        Assert.AreEqual(1, executed.DefinitionVersion);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied,
            (await _store.RunNowAsync(RunNow("after-edit-settled", 2), CancellationToken.None).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task RunNowRetry_IsRejectedWhileAnotherRunNowIsPending()
    {
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await SaveAsync().ConfigureAwait(false)).Status);
        _adapter.Executor = static (_, _) => ValueTask.FromResult(new LocalAutomationExecution(
            LocalAutomationRunOutcome.Failed, "fixture failure"));
        Assert.AreEqual(LocalAutomationCommandStatus.Applied,
            (await _store.RunNowAsync(RunNow("first"), CancellationToken.None).ConfigureAwait(false)).Status);
        using var runner = CreateRunner();
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        var failed = (await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, failed.Outcome);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied,
            (await _store.RunNowAsync(RunNow("second"), CancellationToken.None).ConfigureAwait(false)).Status);

        var retry = await _store.RetryAsync(new(DefinitionId, failed.RunKey, 1, "retry-first", "owner",
            "retry failed refresh"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Conflict, retry.Status);
        Assert.AreEqual(LocalAutomationContract.RunNowPendingReasonCode, retry.ReasonCode);
        Assert.AreEqual(1, retry.State.QueuedRunCount);
    }

    private SqliteLocalAutomationStore CreateStore() => new(_harness.Registry, _captures,
        Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root }), _time,
        NullLogger<SqliteLocalAutomationStore>.Instance);

    private LocalAutomationRunnerService CreateRunner() => new(_store, _harness.Registry, _captures,
        Options.Create(new CameraAgentHostOptions()), _time, NullLogger<LocalAutomationRunnerService>.Instance);

    private async Task<LocalAutomationRunnerEntry> Entry()
        => (await _store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();

    private async Task<LocalAutomationCommandResult> SaveAsync(long version = 0, int interval = 7_200, bool enabled = true,
        string name = "Orbital elements refresh", string? key = null)
        => await _store.SaveAsync(new(DefinitionId, name, enabled, LocalAutomationTaskKind.OrbitalElementsRefresh,
            FakePeriodicTaskAdapter.Target, LocalAutomationTriggerKind.Periodic, interval, version,
            key ?? Guid.NewGuid().ToString("N"), "owner", "orbital schedule"), CancellationToken.None).ConfigureAwait(false);

    private async Task AssertRejectedAsync(LocalAutomationRunNowRequest request, LocalAutomationCommandStatus status, string reasonCode)
    {
        var before = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        var result = await _store.RunNowAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(status, result.Status, request.IdempotencyKey);
        Assert.AreEqual(reasonCode, result.ReasonCode, request.IdempotencyKey);
        Assert.AreEqual(before.QueuedRunCount, result.State.QueuedRunCount, request.IdempotencyKey);
    }

    private static LocalAutomationRunNowRequest RunNow(string key, long version = 1)
        => new(DefinitionId, version, key, "owner", "operator refresh");
}
