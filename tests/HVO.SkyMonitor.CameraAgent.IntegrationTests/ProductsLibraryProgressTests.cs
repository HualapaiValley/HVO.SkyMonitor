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
                var runs = await store.ListNightlyProductRunsAsync(query, CancellationToken.None).ConfigureAwait(false);
                var queuedCard = (await catalog.ListLibraryAsync(query, CancellationToken.None, runs).ConfigureAwait(false)).Entries.Single();
                Assert.AreEqual(window, queuedCard.SourceWindow);
                Assert.IsNull(queuedCard.Generation!.StartedAtUtc);
                var entry = (await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
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
