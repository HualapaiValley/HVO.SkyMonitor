using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

public sealed partial class DailySummaryIntegrationTests
{
    [TestMethod]
    [DataRow(NightlyProductKind.Keogram)]
    [DataRow(NightlyProductKind.StarTrail)]
    public async Task ListingOverflowKeepsSuccessfulStillOutcomeAndExplainsSkippedMaintenance(NightlyProductKind kind)
    {
        // Five real hourly publications of 205 single-source parts plus a final exceed the day-list bound.
        // Hourly work does not trigger summary maintenance; no catalog rows or bounds are fabricated.
        var historicalOptions = NightlyProductFixture.Options(maximumSegmentSources: 1, maximumSegmentsPerRun: 256);
        var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar));
        var hourlyPolicy = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram,
            windowKind: LocalAutomationSourceWindowKind.CompletedCivilHour).SourceWindow!.Policy;
        var hour = planner.ResolveWindows(NightlyProductFixture.ObservingDate, hourlyPolicy)
            .Single(window => window.StartUtc <= NightlyProductFixture.DayStartUtc.AddHours(16) &&
                window.EndUtc > NightlyProductFixture.DayStartUtc.AddHours(16));
        _reader.Add([.. Enumerable.Range(1, 205).Select(index => NightlyProductFixture.Frame(index, hour.StartUtc.AddSeconds(index * 10)))]);
        using (var seed = Create(options: historicalOptions))
        {
            for (var index = 0; index < 5; index++)
            {
                var historical = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, historicalOptions,
                    LocalAutomationSourceWindowKind.CompletedCivilHour, hour.StartUtc, "historical-" + index);
                var report = await seed.Generator.RunAsync(historical, CancellationToken.None);
                Assert.AreEqual(206, report.ProductsPublished, report.Describe());
                Assert.AreEqual(0, report.FailedWindows, report.Describe());
            }
            Assert.AreEqual(1030L, await CountProductsAsync());
            var overflow = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await seed.Store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None));
            StringAssert.Contains(overflow.Message, "product listing exceeds its declared bound", StringComparison.Ordinal);
        }

        string runKey;
        Guid finalId;
        byte[] provenance;
        using (var harness = Create())
        using (var automation = CreateAutomation(harness))
        {
            await QueueStillAsync(automation.Store, kind);
            await automation.Runner.SweepAsync(CancellationToken.None);
            var run = (await automation.Store.GetStateAsync(CancellationToken.None)).Runs.Single();
            runKey = run.RunKey;
            Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, run.Outcome, run.Detail);
            Assert.AreEqual(1, run.Attempt);
            StringAssert.Contains(run.Detail, "8 products published", StringComparison.Ordinal);
            StringAssert.Contains(run.Detail, "Daily summary refresh skipped: the report date could not be listed (InvalidDataException).", StringComparison.Ordinal);
            Assert.IsLessThanOrEqualTo(LocalAutomationContract.MaximumDetailLength, run.Detail.Length);
            var final = await harness.Store.ReadWindowAsync(kind, NightlyProductScope.Final,
                run.Occurrence!.SourceWindow!.StartUtc, CancellationToken.None, run.Occurrence.IdentitySha256);
            Assert.IsNotNull(final);
            finalId = final.ProductIds.Single();
            await harness.Store.VerifyPublicationAsync(finalId, CancellationToken.None);
            provenance = (await harness.Store.OpenProvenanceAsync(finalId, CancellationToken.None))!.Content.ToArray();
            Assert.AreEqual(1038L, await CountProductsAsync());
        }
        using var restarted = Create();
        using var history = CreateAutomation(restarted);
        await history.Runner.SweepAsync(CancellationToken.None);
        var retained = (await history.Store.GetStateAsync(CancellationToken.None)).Runs.Single(run => run.RunKey == runKey);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, retained.Outcome, retained.Detail);
        Assert.AreEqual(1, retained.Attempt, "Skipped maintenance must not consume another attempt after restart.");
        Assert.AreNotEqual(LocalAutomationCommandStatus.Applied, (await history.Store.RetryAsync(
            new("daily", runKey, 1, "retry-success", "fixture", "successful work is not retryable"), CancellationToken.None)).Status);
        var unchanged = await restarted.Generator.RunAsync(retained.Occurrence!, CancellationToken.None);
        Assert.AreEqual(0, unchanged.FailedWindows, unchanged.Describe());
        Assert.AreEqual(0, unchanged.ProductsPublished);
        StringAssert.Contains(unchanged.Describe(), "Daily summary refresh skipped:", StringComparison.Ordinal);
        Assert.AreEqual(1038L, await CountProductsAsync());
        CollectionAssert.AreEqual(provenance, (await restarted.Store.OpenProvenanceAsync(finalId, CancellationToken.None))!.Content.ToArray());
    }

    [TestMethod]
    [DataRow("recover")]
    [DataRow("unchanged")]
    [DataRow("reused")]
    public async Task PartialRefreshHistoryRetainsCompletedCountsAcrossFailureAndRestart(string nextAttempt)
    {
        _reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16)));
        NightlyWindowState[] originals;
        Dictionary<Guid, byte[]> originalBytes;
        using (var seed = Create())
        {
            foreach (var id in new[] { "summary-a", "summary-b" })
                await seed.Generator.RunAsync(NightlyProductFixture.Occurrence(NightlyProductKind.DailySummary,
                    definitionId: id), CancellationToken.None);
            var day = await seed.Store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
            Assert.AreEqual(2, day.Products.Count);
            var states = new List<NightlyWindowState>();
            originalBytes = [];
            foreach (var window in day.Windows)
            {
                states.Add((await seed.Store.ReadWindowAsync(NightlyProductKind.DailySummary, NightlyProductScope.Final,
                    window.Status.WindowStartUtc, CancellationToken.None, window.Status.Occurrence.IdentitySha256))!);
                var id = window.FinalProduct!.ProductId;
                originalBytes.Add(id, (await seed.Store.OpenProvenanceAsync(id, CancellationToken.None))!.Content.ToArray());
            }
            originals = [.. states];
        }

        string failedKey;
        using (var first = Create(new FailRestoreReader(_reader, 3)))
        using (var automation = CreateAutomation(first))
        {
            await QueueStillAsync(automation.Store, NightlyProductKind.StarTrail);
            await automation.Runner.SweepAsync(CancellationToken.None);
            var failed = (await automation.Store.GetStateAsync(CancellationToken.None)).Runs.Single();
            failedKey = failed.RunKey;
            Assert.AreEqual(LocalAutomationRunOutcome.Failed, failed.Outcome, failed.Detail);
            StringAssert.Contains(failed.Detail, "2 products published", StringComparison.Ordinal);
            AssertRefreshFailure(failed, "1 published, 0 reused, 0 unchanged");
            var day = await first.Store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
            Assert.AreEqual(3, day.Products.Count(product => product.Kind == NightlyProductKind.DailySummary));
            Assert.AreEqual(1, day.Windows.Count(window => window.Status.Kind == NightlyProductKind.DailySummary &&
                originalBytes.ContainsKey(window.FinalProduct!.ProductId)), "The failed later summary must keep its last good pointer.");
            if (nextAttempt == "reused")
            {
                var completed = day.Windows.Single(window => window.Status.Kind == NightlyProductKind.DailySummary &&
                    !originalBytes.ContainsKey(window.FinalProduct!.ProductId));
                var old = originals.Single(window => window.Status.Occurrence.IdentitySha256 == completed.Status.Occurrence.IdentitySha256);
                // Recreate the publication-before-pointer crash boundary without changing immutable products.
                await first.Store.RecordWindowAsync(old.Status, old.FingerprintSha256, old.ProductIds, CancellationToken.None);
            }
        }
        if (nextAttempt != "recover")
        {
            using var second = Create(new FailRestoreReader(_reader, 1));
            using var automation = CreateAutomation(second);
            await RetryStillAsync(automation.Store, failedKey, "retry-second");
            await automation.Runner.SweepAsync(CancellationToken.None);
            var failed = (await automation.Store.GetStateAsync(CancellationToken.None)).Runs.Single(run => run.Attempt == 2);
            failedKey = failed.RunKey;
            Assert.AreEqual(LocalAutomationRunOutcome.Failed, failed.Outcome, failed.Detail);
            AssertRefreshFailure(failed, nextAttempt == "reused" ? "0 published, 1 reused, 0 unchanged" : "0 published, 0 reused, 1 unchanged");
            Assert.AreEqual(3, (await second.Store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None))
                .Count(product => product.Kind == NightlyProductKind.DailySummary));
        }
        using var recovered = Create();
        using var recoveredAutomation = CreateAutomation(recovered);
        await RetryStillAsync(recoveredAutomation.Store, failedKey, "retry-recover");
        await recoveredAutomation.Runner.SweepAsync(CancellationToken.None);
        var runs = (await recoveredAutomation.Store.GetStateAsync(CancellationToken.None)).Runs;
        var succeeded = runs.Single(run => run.Outcome == LocalAutomationRunOutcome.Succeeded);
        Assert.AreEqual(nextAttempt == "recover" ? 2 : 3, succeeded.Attempt);
        StringAssert.Contains(succeeded.Detail, "0 products published", StringComparison.Ordinal);
        StringAssert.Contains(succeeded.Detail, "Daily summary refresh: 1 published, 0 reused, 1 unchanged.", StringComparison.Ordinal);
        AssertRefreshFailure(runs.Single(run => run.Attempt == 1), "1 published, 0 reused, 0 unchanged");
        var finalDay = await recovered.Store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
        Assert.AreEqual(4, finalDay.Products.Count(product => product.Kind == NightlyProductKind.DailySummary));
        var stillId = finalDay.Products.Single(product => product.Kind == NightlyProductKind.StarTrail && product.Scope == NightlyProductScope.Final).ProductId;
        foreach (var window in finalDay.Windows.Where(window => window.Status.Kind == NightlyProductKind.DailySummary))
        {
            var id = window.FinalProduct!.ProductId;
            await recovered.Store.VerifyPublicationAsync(id, CancellationToken.None);
            Assert.AreEqual(stillId, (await recovered.Store.GetPresentationAsync(id, CancellationToken.None))!.DailySummary!.Products.Single().ProductId);
        }
        foreach (var (id, bytes) in originalBytes)
            CollectionAssert.AreEqual(bytes, (await recovered.Store.OpenProvenanceAsync(id, CancellationToken.None))!.Content.ToArray());
    }

    private static void AssertRefreshFailure(LocalAutomationRun run, string counts)
    {
        StringAssert.Contains(run.Detail, "Daily summary refresh: " + counts + ".", StringComparison.Ordinal);
        StringAssert.Contains(run.Detail, "A retained daily summary could not be refreshed; retry this occurrence.", StringComparison.Ordinal);
        Assert.IsLessThanOrEqualTo(LocalAutomationContract.MaximumDetailLength, run.Detail.Length);
    }

    private async Task<long> CountProductsAsync()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, SqliteNightlyProductStore.DirectoryName, SqliteNightlyProductStore.FileName),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM nightly_products;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private AutomationHarness CreateAutomation(Harness harness)
    {
        var options = NightlyProductFixture.HostOptions(_root, NightlyProductFixture.Options());
        var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
        var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar));
        var registry = new StillRegistry(new NightlyProductAutomationTaskRegistry(harness.Generator, options));
        var captures = new NoCaptureSequence();
        var store = new SqliteLocalAutomationStore(registry, captures, options, clock,
            NullLogger<SqliteLocalAutomationStore>.Instance, windowPlanner: planner);
        return new(store, new LocalAutomationRunnerService(store, registry, captures, options, clock,
            NullLogger<LocalAutomationRunnerService>.Instance, windowPlanner: planner));
    }

    private static async Task QueueStillAsync(SqliteLocalAutomationStore store, NightlyProductKind kind)
    {
        var definition = NightlyProductFixture.Occurrence(kind, definitionId: "daily").Definition;
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await store.SaveAsync(new(definition.DefinitionId,
            definition.Name, true, definition.TaskKind, definition.TaskTarget, definition.TriggerKind, 1, 0,
            "save", "fixture", "summary maintenance", definition.SourceWindow), CancellationToken.None)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await store.BackfillAsync(new("daily",
            NightlyProductFixture.ObservingDate, null, 1, "backfill", "fixture", "summary maintenance"), CancellationToken.None)).Status);
    }

    private static async Task RetryStillAsync(SqliteLocalAutomationStore store, string runKey, string commandId)
    {
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await store.RetryAsync(new("daily", runKey, 1,
            commandId, "fixture", "retry retained summary maintenance"), CancellationToken.None)).Status);
    }

    private sealed record AutomationHarness(SqliteLocalAutomationStore Store, LocalAutomationRunnerService Runner) : IDisposable
    {
        public void Dispose() { Runner.Dispose(); Store.Dispose(); }
    }

    private sealed class StillRegistry(NightlyProductAutomationTaskRegistry adapter) : ILocalAutomationTaskRegistry
    {
        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() => [adapter.Describe()];
        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition) => adapter.Validate(definition);
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
            => adapter.ExecuteAsync(occurrence, cancellationToken);
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string runKey, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A real retained source-window occurrence is required.");
    }

    private sealed class NoCaptureSequence : ILocalAutomationCaptureSequenceSource
    {
        public ValueTask<long?> GetCaptureSequenceAsync(CancellationToken cancellationToken) => ValueTask.FromResult<long?>(null);
    }

    private sealed class FailRestoreReader(INightlyProductSourceReader inner, int failAt) : INightlyProductSourceReader
    {
        private int _restores;
        public ValueTask<IReadOnlyList<NightlyProductCandidate>> ReadCandidatesAsync(string nodeId, DateTimeOffset startUtc,
            DateTimeOffset endUtc, int maximumCount, Action<string> unsupported, CancellationToken cancellationToken)
            => inner.ReadCandidatesAsync(nodeId, startUtc, endUtc, maximumCount, unsupported, cancellationToken);
        public ValueTask<IReadOnlyList<ProcessingArtifact>> RestoreAsync(IReadOnlyList<NightlyProductCandidate> candidates,
            CancellationToken cancellationToken) => ++_restores == failAt
                ? ValueTask.FromException<IReadOnlyList<ProcessingArtifact>>(new IOException("Synthetic later summary refresh failure."))
                : inner.RestoreAsync(candidates, cancellationToken);
    }
}
