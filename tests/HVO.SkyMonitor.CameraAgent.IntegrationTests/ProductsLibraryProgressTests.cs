using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ProductsLibraryProgressTests
{
    [TestMethod]
    [DataRow(NightlyProductKind.Keogram)]
    [DataRow(NightlyProductKind.StarTrail)]
    public async Task DurableJournalProjectsQueuedRunningInterruptedAndRetryingAfterRestart(NightlyProductKind kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-library-progress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var site = DeploymentLocationSnapshot.Create("virtual-library", 1, "test", null,
                new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, 35.5599378, -113.9119818, 520, "America/Phoenix");
            var calendar = new FixedObservingDayCalendarProvider(ObservingDayCalendar.ForDeployment(site));
            var planner = new LocalAutomationWindowPlanner(calendar);
            var policy = new LocalAutomationSourceWindowPolicy(LocalAutomationSourceWindowPolicy.CurrentVersion,
                LocalAutomationSourceWindowKind.SunriseDay,
                kind == NightlyProductKind.Keogram ? LocalAutomationSourceSelection.AllActualSources : LocalAutomationSourceSelection.DarkNightActualSources,
                TimeSpan.Zero);
            var date = new DateOnly(2026, 10, 1);
            var window = planner.ResolveWindows(date, policy).Single();
            var clock = new FixtureClock(DateTimeOffset.FromUnixTimeMilliseconds(window.EarliestFinalUtc.AddMinutes(10).ToUnixTimeMilliseconds()));
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            SqliteLocalAutomationStore Open() => new(new FixtureRegistry(), new CaptureSequence(), options, clock,
                NullLogger<SqliteLocalAutomationStore>.Instance, windowPlanner: planner);
            var query = new NightlyProductLibraryQuery(1, kind, NightlyProductLibraryState.Running, date);
            using var catalog = new SqliteNightlyProductStore(options, clock);
            DateTimeOffset? started;
            string runKey;
            using (var store = Open())
            {
                await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var saved = await store.SaveAsync(new("daily", "Virtual daily", true, LocalAutomationTaskKind.StillImageGeneration,
                    NightlyProductContract.TargetFor(kind) + ":" + new string('A', 64), LocalAutomationTriggerKind.SourceWindowClosed,
                    1, 0, "save", "fixture", "virtual progress", policy), CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
                var queued = await store.BackfillAsync(new("daily", date, null, 1, "backfill", "fixture", "virtual backfill"), CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, queued.Status);
                var entry = (await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
                for (var index = 0; index <= LocalAutomationContract.MaximumProjectedRuns; index++)
                    await store.RecordTerminalRunAsync(entry, "noise-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        clock.GetUtcNow(), LocalAutomationRunOutcome.Missed, "Unrelated recent history", null, CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse((await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs
                    .Any(static run => run.Outcome == LocalAutomationRunOutcome.Queued), "The recent-history page no longer contains the active job.");
                var runs = await store.ListNightlyProductRunsAsync(query, CancellationToken.None).ConfigureAwait(false);
                var queuedCard = (await catalog.ListLibraryAsync(query, CancellationToken.None, runs).ConfigureAwait(false)).Entries.Single();
                Assert.AreEqual(window, queuedCard.SourceWindow);
                Assert.IsNull(queuedCard.Generation!.StartedAtUtc);
                var claimed = await store.TryClaimQueuedAsync(entry, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(claimed);
                started = claimed.StartedAtUtc;
                runKey = claimed.RunKey;
                Assert.AreEqual(clock.GetUtcNow(), started);
                runs = await store.ListNightlyProductRunsAsync(query, CancellationToken.None).ConfigureAwait(false);
                var runningCard = (await catalog.ListLibraryAsync(query, CancellationToken.None, runs).ConfigureAwait(false)).Entries.Single();
                Assert.AreEqual(NightlyProductLibraryState.Running, runningCard.State);
                Assert.IsNull(runningCard.Product);
                Assert.AreEqual(started, runningCard.Generation!.StartedAtUtc);
                Assert.IsEmpty((await catalog.ListLibraryAsync(query with { State = NightlyProductLibraryState.Produced }, CancellationToken.None, runs).ConfigureAwait(false)).Entries);
            }
            using var restarted = Open();
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var retained = await restarted.ListNightlyProductRunsAsync(query, CancellationToken.None).ConfigureAwait(false);
            Assert.IsEmpty((await catalog.ListLibraryAsync(query, CancellationToken.None, retained).ConfigureAwait(false)).Entries);
            var stalled = (await catalog.ListLibraryAsync(query with { State = NightlyProductLibraryState.Stalled }, CancellationToken.None, retained).ConfigureAwait(false)).Entries.Single();
            Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, stalled.Generation!.Outcome);
            Assert.AreEqual(started, stalled.Generation.StartedAtUtc);
            Assert.AreEqual(window, stalled.SourceWindow);
            var retry = await restarted.RetryAsync(new("daily", runKey, 1, "retry", "fixture", "retry virtual generation"), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, retry.Status);
            var retryRuns = await restarted.ListNightlyProductRunsAsync(query, CancellationToken.None).ConfigureAwait(false);
            var retrying = (await catalog.ListLibraryAsync(query, CancellationToken.None, retryRuns).ConfigureAwait(false)).Entries.Single();
            Assert.AreEqual(2, retrying.Generation!.Attempt);
            Assert.IsNull(retrying.Generation.StartedAtUtc);
            Assert.AreEqual(window, retrying.SourceWindow);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(NightlyProductKind.Keogram, true)]
    [DataRow(NightlyProductKind.StarTrail, true)]
    [DataRow(NightlyProductKind.Keogram, false)]
    [DataRow(NightlyProductKind.StarTrail, false)]
    public async Task DisabledGenerationProjectsQueuedStillWorkAsStalled(NightlyProductKind kind, bool disableDefinition)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-library-disabled", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var site = DeploymentLocationSnapshot.Create("virtual-library-disabled", 1, "test", null,
                new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, 35.5599378, -113.9119818, 520, "America/Phoenix");
            var calendar = new FixedObservingDayCalendarProvider(ObservingDayCalendar.ForDeployment(site));
            var planner = new LocalAutomationWindowPlanner(calendar);
            var policy = new LocalAutomationSourceWindowPolicy(LocalAutomationSourceWindowPolicy.CurrentVersion,
                LocalAutomationSourceWindowKind.SunriseDay,
                kind == NightlyProductKind.Keogram ? LocalAutomationSourceSelection.AllActualSources : LocalAutomationSourceSelection.DarkNightActualSources,
                TimeSpan.Zero);
            var date = new DateOnly(2026, 10, 1);
            var window = planner.ResolveWindows(date, policy).Single();
            var clock = new FixtureClock(DateTimeOffset.FromUnixTimeMilliseconds(window.EarliestFinalUtc.AddMinutes(10).ToUnixTimeMilliseconds()));
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var target = NightlyProductContract.TargetFor(kind) + ":" + new string('A', 64);
            using (var store = new SqliteLocalAutomationStore(new FixtureRegistry(), new CaptureSequence(), options, clock,
                NullLogger<SqliteLocalAutomationStore>.Instance, windowPlanner: planner))
            {
                var saved = await store.SaveAsync(new("daily", "Virtual daily", true, LocalAutomationTaskKind.StillImageGeneration,
                    target, LocalAutomationTriggerKind.SourceWindowClosed, 1, 0, "save", "fixture", "virtual progress", policy), CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
                var queued = await store.BackfillAsync(new("daily", date, null, 1, "backfill", "fixture", "virtual backfill"), CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, queued.Status);
                if (disableDefinition)
                {
                    var disabled = await store.SaveAsync(new("daily", "Virtual daily", false, LocalAutomationTaskKind.StillImageGeneration,
                        target, LocalAutomationTriggerKind.SourceWindowClosed, 1, 1, "disable", "fixture", "disabled fixture", policy), CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(LocalAutomationCommandStatus.Applied, disabled.Status);
                }
            }
            using var catalog = new SqliteNightlyProductStore(options, clock);
            using var reader = new SqliteLocalAutomationStore(new FixtureRegistry(), new CaptureSequence(),
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, Automation = new() { Enabled = disableDefinition } }), clock,
                NullLogger<SqliteLocalAutomationStore>.Instance, windowPlanner: planner);
            var query = new NightlyProductLibraryQuery(1, kind, ObservingDate: date);
            var runs = await reader.ListNightlyProductRunsAsync(query, CancellationToken.None).ConfigureAwait(false);
            var page = await catalog.ListLibraryAsync(query, CancellationToken.None, runs).ConfigureAwait(false);
            var stalled = page.Entries.Single();
            Assert.AreEqual(NightlyProductLibraryState.Stalled, stalled.State);
            Assert.AreEqual(LocalAutomationRunOutcome.Queued, stalled.Generation!.Outcome);
            Assert.IsTrue(stalled.Occurrence.Definition.Enabled, "Current availability must not rewrite the retained definition snapshot.");
            Assert.IsNull(stalled.Generation.StartedAtUtc);
            Assert.AreEqual(window, stalled.SourceWindow);
            Assert.IsEmpty((await catalog.ListLibraryAsync(query with { State = NightlyProductLibraryState.Running }, CancellationToken.None, runs).ConfigureAwait(false)).Entries);
            Assert.HasCount(1, (await catalog.ListLibraryAsync(query with { State = NightlyProductLibraryState.Stalled }, CancellationToken.None, runs).ConfigureAwait(false)).Entries);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(NightlyProductKind.Keogram)]
    [DataRow(NightlyProductKind.StarTrail)]
    public async Task RunningStillTaskRemainsRunningWhenItsDefinitionIsDisabledUntilRunnerCompletes(NightlyProductKind kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-library-running-disabled", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var site = DeploymentLocationSnapshot.Create("virtual-library-running", 1, "test", null,
                new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, 35.5599378, -113.9119818, 520, "America/Phoenix");
            var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(ObservingDayCalendar.ForDeployment(site)));
            var policy = new LocalAutomationSourceWindowPolicy(LocalAutomationSourceWindowPolicy.CurrentVersion,
                LocalAutomationSourceWindowKind.SunriseDay,
                kind == NightlyProductKind.Keogram ? LocalAutomationSourceSelection.AllActualSources : LocalAutomationSourceSelection.DarkNightActualSources,
                TimeSpan.Zero);
            var date = new DateOnly(2026, 10, 1);
            var window = planner.ResolveWindows(date, policy).Single();
            var clock = new FixtureClock(DateTimeOffset.FromUnixTimeMilliseconds(window.EarliestFinalUtc.AddMinutes(10).ToUnixTimeMilliseconds()));
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var registry = new BlockedTaskRegistry();
            var captureSequence = new CaptureSequence();
            using var store = new SqliteLocalAutomationStore(registry, captureSequence, options, clock,
                NullLogger<SqliteLocalAutomationStore>.Instance, windowPlanner: planner);
            using var catalog = new SqliteNightlyProductStore(options, clock);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var target = NightlyProductContract.TargetFor(kind) + ":" + new string('A', 64);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await store.SaveAsync(new("daily", "Virtual daily", true,
                LocalAutomationTaskKind.StillImageGeneration, target, LocalAutomationTriggerKind.SourceWindowClosed,
                1, 0, "save", "fixture", "running fixture", policy), CancellationToken.None).ConfigureAwait(false)).Status);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await store.BackfillAsync(
                new("daily", date, null, 1, "backfill", "fixture", "running fixture"), CancellationToken.None).ConfigureAwait(false)).Status);
            using var runner = new LocalAutomationRunnerService(store, registry, captureSequence, options, clock,
                NullLogger<LocalAutomationRunnerService>.Instance, windowPlanner: planner);
            var sweep = runner.SweepAsync(CancellationToken.None);
            LocalAutomationRun? claimed = null;
            try
            {
                var occurrence = await registry.Started.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var query = new NightlyProductLibraryQuery(1, kind, NightlyProductLibraryState.Running, date);
                var before = await store.ListNightlyProductRunsAsync(query, CancellationToken.None).ConfigureAwait(false);
                claimed = before.Single().Run;
                Assert.AreEqual(LocalAutomationRunOutcome.Running, claimed.Outcome);
                Assert.IsTrue(before.Single().Enabled);
                Assert.HasCount(1, (await catalog.ListLibraryAsync(query, CancellationToken.None, before).ConfigureAwait(false)).Entries);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await store.SaveAsync(new("daily", "Virtual daily", false,
                    LocalAutomationTaskKind.StillImageGeneration, target, LocalAutomationTriggerKind.SourceWindowClosed,
                    1, 1, "disable", "fixture", "disable during execution", policy), CancellationToken.None).ConfigureAwait(false)).Status);
                var after = await store.ListNightlyProductRunsAsync(query, CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(after.Single().Enabled);
                Assert.AreEqual(LocalAutomationRunOutcome.Running, after.Single().Run.Outcome);
                Assert.IsFalse(sweep.IsCompleted, "The real runner is still awaiting the controlled task.");
                Assert.IsFalse(registry.ExecutionToken.IsCancellationRequested);
                var card = (await catalog.ListLibraryAsync(query with { State = null }, CancellationToken.None, after).ConfigureAwait(false)).Entries.Single();
                Assert.AreEqual(NightlyProductLibraryState.Running, card.State);
                Assert.AreEqual(claimed.RunKey, card.Generation!.RunKey);
                Assert.AreEqual(claimed.StartedAtUtc, card.Generation.StartedAtUtc);
                Assert.AreEqual(occurrence, card.Occurrence);
                Assert.AreEqual(window, card.SourceWindow);
                Assert.IsNull(card.Product);
                Assert.HasCount(1, (await catalog.ListLibraryAsync(query, CancellationToken.None, after).ConfigureAwait(false)).Entries);
                Assert.IsEmpty((await catalog.ListLibraryAsync(query with { State = NightlyProductLibraryState.Stalled }, CancellationToken.None, after).ConfigureAwait(false)).Entries);
                Assert.IsEmpty((await catalog.ListLibraryAsync(query with { State = NightlyProductLibraryState.Produced }, CancellationToken.None, after).ConfigureAwait(false)).Entries);
            }
            finally
            {
                registry.Release.TrySetResult();
                await sweep.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            var settled = (await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single(run => run.RunKey == claimed!.RunKey);
            Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, settled.Outcome);
            Assert.IsNotNull(settled.CompletedAtUtc);
            Assert.IsEmpty(await store.ListNightlyProductRunsAsync(new(1, kind, NightlyProductLibraryState.Running, date), CancellationToken.None).ConfigureAwait(false));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class BlockedTaskRegistry : ILocalAutomationTaskRegistry
    {
        internal TaskCompletionSource<LocalAutomationOccurrence> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken ExecutionToken { get; private set; }
        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() => [];
        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition) => LocalAutomationDefinitionValidator.Validate(definition);
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string runKey, CancellationToken cancellationToken)
            => throw new InvalidOperationException("This fixture requires the retained occurrence.");
        public async ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
        {
            ExecutionToken = cancellationToken;
            Started.TrySetResult(occurrence);
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(LocalAutomationRunOutcome.Succeeded, "Controlled task completed after disablement.");
        }
    }

    private sealed class FixtureClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CaptureSequence : ILocalAutomationCaptureSequenceSource
    {
        public ValueTask<long?> GetCaptureSequenceAsync(CancellationToken cancellationToken) => ValueTask.FromResult<long?>(null);
    }

    private sealed class FixtureRegistry : ILocalAutomationTaskRegistry
    {
        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() => [];
        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition) => LocalAutomationDefinitionValidator.Validate(definition);
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string runKey, CancellationToken cancellationToken)
            => throw new InvalidOperationException("This fixture only exercises the durable journal.");
    }
}
