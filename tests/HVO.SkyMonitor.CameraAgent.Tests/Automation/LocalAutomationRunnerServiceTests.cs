using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

/// <summary>
/// Covers the trigger bridge: periodic cadence that does not drift, explicit missed-window recording,
/// capture-relative baselining and firing, idempotent retry, and isolation from acquisition timing.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class LocalAutomationRunnerServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-09-05T00:00:00Z", CultureInfo.InvariantCulture);

    private string _root = null!;
    private MutableTimeProvider _timeProvider = null!;
    private StubAutomationTaskRegistry _registry = null!;
    private StubCaptureSequenceSource _captureSequence = null!;
    private SqliteLocalAutomationStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "hvo-automation-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _timeProvider = new MutableTimeProvider(Now);
        _registry = new StubAutomationTaskRegistry();
        _captureSequence = new StubCaptureSequenceSource();
        _store = new SqliteLocalAutomationStore(
            _registry,
            _captureSequence,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root }),
            _timeProvider,
            NullLogger<SqliteLocalAutomationStore>.Instance);
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
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SweepAsync_DoesNothingBeforeTheFirstOccurrenceIsDue()
    {
        await SaveAsync().ConfigureAwait(false);
        _timeProvider.Advance(TimeSpan.FromSeconds(3599));

        await CreateRunner().SweepAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsEmpty(_registry.ExecutedRunKeys);
        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(state.Runs);
    }

    [TestMethod]
    public async Task SweepAsync_RunsOnePeriodicOccurrenceAndKeepsTheCadenceAnchoredToTheEpoch()
    {
        await SaveAsync().ConfigureAwait(false);
        var runner = CreateRunner();
        _timeProvider.Advance(TimeSpan.FromSeconds(3605));

        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, _registry.ExecutedRunKeys.Count, "A due occurrence runs exactly once.");
        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        var run = state.Runs.Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, run.Outcome);
        // The run fired five seconds late, but the next occurrence stays on the epoch boundary.
        Assert.AreEqual(Now.AddSeconds(3600), run.ScheduledForUtc);
        Assert.AreEqual(Now.AddSeconds(7200), state.Definitions.Single().NextRunUtc);
    }

    [TestMethod]
    public async Task SweepAsync_RecordsSkippedOccurrencesOnceAndDoesNotReplayThem()
    {
        await SaveAsync().ConfigureAwait(false);
        _timeProvider.Advance(TimeSpan.FromSeconds(3600 * 5));

        await CreateRunner().SweepAsync(CancellationToken.None).ConfigureAwait(false);

        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, state.Runs.Count);
        var missed = state.Runs.Single(static run => run.Outcome == LocalAutomationRunOutcome.Missed);
        Assert.IsTrue(missed.Detail.Contains("4 occurrence(s) elapsed", StringComparison.Ordinal));
        Assert.AreEqual(1, _registry.ExecutedRunKeys.Count, "Skipped occurrences are never replayed.");
        Assert.AreEqual(Now.AddSeconds(3600 * 5), state.Runs
            .Single(static run => run.Outcome == LocalAutomationRunOutcome.Succeeded).ScheduledForUtc);
    }

    [TestMethod]
    public async Task SweepAsync_RecordsAFailedRunAndRetriesOnlyAtTheNextOccurrence()
    {
        await SaveAsync().ConfigureAwait(false);
        _registry.Result = new LocalAutomationExecution(LocalAutomationRunOutcome.Failed, "The source failed.");
        var runner = CreateRunner();
        _timeProvider.Advance(TimeSpan.FromSeconds(3600));

        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        _timeProvider.Advance(TimeSpan.FromSeconds(3600));
        _registry.Result = new LocalAutomationExecution(LocalAutomationRunOutcome.Succeeded, "Acquired.");
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, state.Runs.Count);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, state.Runs[0].Outcome);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, state.Runs[1].Outcome);
    }

    [TestMethod]
    public async Task SweepAsync_DoesNotRunADisabledDefinition()
    {
        await SaveAsync(enabled: false).ConfigureAwait(false);
        _timeProvider.Advance(TimeSpan.FromSeconds(7200));

        await CreateRunner().SweepAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsEmpty(_registry.ExecutedRunKeys);
        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(state.Runs);
        Assert.IsNull(state.Definitions.Single().NextRunUtc);
    }

    [TestMethod]
    public async Task SweepAsync_BaselinesACaptureRelativeDefinitionBeforeFiringIt()
    {
        await SaveAsync(trigger: LocalAutomationTriggerKind.CaptureRelative, interval: 10).ConfigureAwait(false);
        _captureSequence.Sequence = 100;
        var runner = CreateRunner();

        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        _captureSequence.Sequence = 109;
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        _captureSequence.Sequence = 110;
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, _registry.ExecutedRunKeys.Count);
        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(110L, state.Runs.Single().ObservedCaptureSequence);
        Assert.AreEqual(120L, state.Definitions.Single().NextRunCaptureSequence);
        Assert.IsEmpty(state.Calendar, "A capture-relative definition has no wall-clock calendar entry.");
    }

    [TestMethod]
    public async Task SweepAsync_LeavesACaptureRelativeDefinitionPendingWhenTheSequenceIsUnreadable()
    {
        await SaveAsync(trigger: LocalAutomationTriggerKind.CaptureRelative, interval: 1).ConfigureAwait(false);
        _captureSequence.Throw = new IOException("unavailable");

        await CreateRunner().SweepAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsEmpty(_registry.ExecutedRunKeys);
        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(state.Runs);
    }

    [TestMethod]
    public async Task SweepAsync_DoesNotReadTheCaptureSequenceForPeriodicDefinitionsAlone()
    {
        await SaveAsync().ConfigureAwait(false);
        _timeProvider.Advance(TimeSpan.FromSeconds(3600));
        var before = _captureSequence.Reads;

        await CreateRunner().SweepAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(
            before, _captureSequence.Reads, "The sweep reads durable capture state only when needed.");
    }

    [TestMethod]
    public async Task SweepAsync_UsesTheOccurrenceIdentityAsTheTaskCommandIdentity()
    {
        await SaveAsync().ConfigureAwait(false);
        _timeProvider.Advance(TimeSpan.FromSeconds(3600));

        await CreateRunner().SweepAsync(CancellationToken.None).ConfigureAwait(false);

        var runKey = _registry.ExecutedRunKeys.Single();
        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(state.Runs.Single().RunKey, runKey);
        Assert.IsTrue(runKey.StartsWith("sky-temperature|", StringComparison.Ordinal));
        Assert.IsTrue(runKey.EndsWith("|p1", StringComparison.Ordinal));
        Assert.IsTrue(runKey.Length <= LocalAutomationContract.MaximumIdempotencyKeyLength);
    }

    [TestMethod]
    public async Task SweepAsync_ContinuesAfterOneDefinitionThrows()
    {
        await SaveAsync().ConfigureAwait(false);
        await SaveAsync(definitionId: "second", key: "second-key").ConfigureAwait(false);
        _registry.Throw = new InvalidOperationException("boom");
        _timeProvider.Advance(TimeSpan.FromSeconds(3600));

        await CreateRunner().SweepAsync(CancellationToken.None).ConfigureAwait(false);

        var state = await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        // Both occurrences are claimed and both stay claimed; restart recovery settles them.
        Assert.AreEqual(2, state.Runs.Count);
        Assert.IsTrue(state.Runs.All(static run => run.Outcome == LocalAutomationRunOutcome.Running));
    }

    private LocalAutomationRunnerService CreateRunner()
        => new(
            _store,
            _registry,
            _captureSequence,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root }),
            _timeProvider,
            NullLogger<LocalAutomationRunnerService>.Instance);

    private async Task SaveAsync(
        string definitionId = "sky-temperature",
        bool enabled = true,
        LocalAutomationTriggerKind trigger = LocalAutomationTriggerKind.Periodic,
        int interval = 3600,
        string key = "command-1")
    {
        var result = await _store.SaveAsync(
            new LocalAutomationSaveRequest(
                definitionId,
                "Sky temperature",
                enabled,
                LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
                "virtual-sky-temperature",
                trigger,
                interval,
                0,
                key,
                "owner",
                null),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, result.Status);
    }
}
