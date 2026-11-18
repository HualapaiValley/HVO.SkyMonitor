using System.Runtime.ExceptionServices;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

public sealed partial class DailySummaryIntegrationTests
{
    [TestMethod]
    [DataRow(NightlyProductKind.Keogram)]
    [DataRow(NightlyProductKind.StarTrail)]
    public async Task ListingIoFailureIsIsolatedAfterStillPublicationAndRetainedAcrossRestart(NightlyProductKind kind)
    {
        var original = await SeedMaintenanceSummaryAsync();
        var reads = 0;
        var logger = new CapturingNightlyProductLogger();
        var listingFailure = new IOException("Synthetic maintenance listing IO failure.");
        ExceptionDispatchInfo? hookFailure = null;
        var reachedSyntheticThrow = false;
        Guid stillId = default;
        byte[] provenance;
        string runKey;
        string occurrenceId;
        using (var harness = Create(logger: logger, readMaintenanceDay: async (store, date, token) =>
        {
            try
            {
                reads++;
                Assert.AreEqual(NightlyProductFixture.ObservingDate, date);
                Assert.IsFalse(token.IsCancellationRequested);
                stillId = await VerifyStillFinalAsync(store, kind);
            }
            catch (Exception exception)
            {
                hookFailure ??= ExceptionDispatchInfo.Capture(exception);
                throw;
            }
            reachedSyntheticThrow = true;
            throw listingFailure;
        }))
        using (var automation = CreateAutomation(harness))
        {
            await QueueStillAsync(automation.Store, kind);
            await automation.Runner.SweepAsync(CancellationToken.None);
            hookFailure?.Throw();
            Assert.IsTrue(reachedSyntheticThrow);
            Assert.AreEqual(1, reads, "Only the initial maintenance listing uses this operation.");
            var warning = logger.Warnings.Single();
            Assert.AreEqual(2, warning.Id.Id);
            Assert.AreSame(listingFailure, warning.Exception);
            Assert.AreEqual(NightlyProductFixture.ObservingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                warning.Fields.Single(field => field.Key == "ReportDate").Value);
            var run = (await automation.Store.GetStateAsync(CancellationToken.None)).Runs.Single();
            runKey = run.RunKey;
            occurrenceId = run.Occurrence!.IdentitySha256;
            await AssertSingleSuccessfulAttemptAsync(automation.Store, occurrenceId, runKey);
            StringAssert.Contains(run.Detail, "Daily summary refresh skipped: the report date could not be listed (IOException).",
                StringComparison.Ordinal);
            Assert.IsLessThanOrEqualTo(512, run.Detail.Length);
            provenance = (await harness.Store.OpenProvenanceAsync(stillId, CancellationToken.None))!.Content.ToArray();
            await AssertMaintenanceSummaryUnchangedAsync(harness.Store, original);
        }

        using var restarted = Create();
        using var history = CreateAutomation(restarted);
        await history.Runner.SweepAsync(CancellationToken.None);
        var retained = await AssertSingleSuccessfulAttemptAsync(history.Store, occurrenceId, runKey);
        StringAssert.Contains(retained.Detail, "could not be listed (IOException)", StringComparison.Ordinal);
        await AssertMaintenanceSummaryUnchangedAsync(restarted.Store, original);
        await restarted.Store.VerifyPublicationAsync(stillId, CancellationToken.None);
        CollectionAssert.AreEqual(provenance, (await restarted.Store.OpenProvenanceAsync(stillId, CancellationToken.None))!.Content.ToArray());
    }

    [TestMethod]
    [DataRow(NightlyProductKind.Keogram, true)]
    [DataRow(NightlyProductKind.StarTrail, true)]
    [DataRow(NightlyProductKind.Keogram, false)]
    [DataRow(NightlyProductKind.StarTrail, false)]
    public async Task ListingCancellationAndProgrammingFailurePropagateAfterStillPublication(NightlyProductKind kind, bool cancel)
    {
        var original = await SeedMaintenanceSummaryAsync();
        using var cancellation = new CancellationTokenSource();
        Exception failure = cancel ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Synthetic unrelated maintenance listing failure.");
        var reads = 0;
        Guid stillId = default;
        byte[] provenance;
        var occurrence = NightlyProductFixture.Occurrence(kind);
        using (var harness = Create(readMaintenanceDay: async (store, date, token) =>
        {
            reads++;
            Assert.AreEqual(NightlyProductFixture.ObservingDate, date);
            Assert.AreEqual(cancellation.Token, token);
            Assert.IsFalse(token.IsCancellationRequested, "Cancellation must begin at the maintenance listing, after still work.");
            stillId = await VerifyStillFinalAsync(store, kind);
            if (cancel) await cancellation.CancelAsync();
            throw failure;
        }))
        {
            if (cancel)
            {
                var thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                    await harness.Generator.RunAsync(occurrence, cancellation.Token));
                Assert.AreSame(failure, thrown);
            }
            else
            {
                var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await harness.Generator.RunAsync(occurrence, cancellation.Token));
                Assert.AreSame(failure, thrown);
            }
            Assert.AreEqual(1, reads);
            provenance = (await harness.Store.OpenProvenanceAsync(stillId, CancellationToken.None))!.Content.ToArray();
            await AssertMaintenanceSummaryUnchangedAsync(harness.Store, original);
        }

        using var restarted = Create();
        await AssertMaintenanceSummaryUnchangedAsync(restarted.Store, original);
        await restarted.Store.VerifyPublicationAsync(stillId, CancellationToken.None);
        CollectionAssert.AreEqual(provenance, (await restarted.Store.OpenProvenanceAsync(stillId, CancellationToken.None))!.Content.ToArray());
        var recovered = await restarted.Generator.RunAsync(occurrence, CancellationToken.None);
        Assert.AreEqual(0, recovered.FailedWindows, recovered.Describe());
        Assert.AreEqual(0, recovered.ProductsPublished, "Already completed still publication must survive the propagated exception.");
        StringAssert.Contains(recovered.Describe(), "Daily summary refresh: 1 published, 0 reused, 0 unchanged.", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailingSummaryEvaluationContributesItsOwnPublicationOrReuseBeforePointerCommit(bool reuse)
    {
        var original = await SeedMaintenanceSummaryAsync();
        Guid publishedId = default;
        byte[]? publishedProvenance = null;
        if (reuse)
        {
            using var prepare = Create();
            using var pending = CreateAutomation(prepare);
            await QueueStillAsync(pending.Store, NightlyProductKind.StarTrail);
            var queued = (await pending.Store.GetStateAsync(CancellationToken.None)).Runs.Single();
            Assert.AreEqual(LocalAutomationRunOutcome.Queued, queued.Outcome);
            var report = await prepare.Generator.RunAsync(queued.Occurrence!, CancellationToken.None);
            Assert.AreEqual(0, report.FailedWindows, report.Describe());
            var day = await prepare.Store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
            publishedId = day.Products.Single(product => product.Kind == NightlyProductKind.DailySummary &&
                product.ProductId != original.Window.ProductIds.Single()).ProductId;
            publishedProvenance = (await prepare.Store.OpenProvenanceAsync(publishedId, CancellationToken.None))!.Content.ToArray();
            // Recreate publication-before-pointer recovery, while leaving the real occurrence queued.
            await prepare.Store.RecordWindowAsync(original.Window.Status, original.Window.FingerprintSha256,
                original.Window.ProductIds, CancellationToken.None);
        }

        var faults = 0;
        ExceptionDispatchInfo? hookFailure = null;
        var reachedSyntheticThrow = false;
        string failedKey;
        Guid stillId;
        byte[] stillProvenance;
        using (var harness = Create(recordWindow: async (store, status, fingerprint, ids, token) =>
        {
            if (status.Kind == NightlyProductKind.DailySummary && status.Scope == NightlyProductScope.Final &&
                status.Disposition == NightlyProductWindowDisposition.Produced)
            {
                try
                {
                    faults++;
                    Assert.AreEqual(1, faults);
                    var id = ids.Single();
                    await store.VerifyPublicationAsync(id, token);
                    Assert.AreEqual(fingerprint, (await store.GetAsync(id, token))!.OutputIdentitySha256);
                    await AssertMaintenanceSummaryUnchangedAsync(store, original, expectedProducts: 2);
                    if (reuse) Assert.AreEqual(publishedId, id, "The failing evaluation must reuse the pre-existing immutable output.");
                    publishedId = id;
                }
                catch (Exception exception)
                {
                    hookFailure ??= ExceptionDispatchInfo.Capture(exception);
                    throw;
                }
                reachedSyntheticThrow = true;
                throw new IOException("Synthetic summary pointer write failure after publication or reuse.");
            }
            try
            {
                await store.RecordWindowAsync(status, fingerprint, ids, token);
            }
            catch (Exception exception)
            {
                hookFailure ??= ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }))
        using (var automation = CreateAutomation(harness))
        {
            if (!reuse) await QueueStillAsync(automation.Store, NightlyProductKind.StarTrail);
            await automation.Runner.SweepAsync(CancellationToken.None);
            hookFailure?.Throw();
            Assert.IsTrue(reachedSyntheticThrow);
            Assert.AreEqual(1, faults);
            var failed = (await automation.Store.GetStateAsync(CancellationToken.None)).Runs.Single();
            failedKey = failed.RunKey;
            Assert.AreEqual(LocalAutomationRunOutcome.Failed, failed.Outcome, failed.Detail);
            Assert.AreEqual(1, failed.Attempt);
            StringAssert.Contains(failed.Detail, reuse ? "star-trail: 1 windows evaluated, 2 unchanged, 0 products published" : "star-trail: 1 windows evaluated, 0 unchanged, 2 products published", StringComparison.Ordinal);
            AssertRefreshFailure(failed, reuse ? "0 published, 1 reused, 0 unchanged" : "1 published, 0 reused, 0 unchanged");
            var bytes = (await harness.Store.OpenProvenanceAsync(publishedId, CancellationToken.None))!.Content.ToArray();
            if (reuse) CollectionAssert.AreEqual(publishedProvenance!, bytes);
            publishedProvenance = bytes;
            await AssertMaintenanceSummaryUnchangedAsync(harness.Store, original, expectedProducts: 2);
            stillId = await VerifyStillFinalAsync(harness.Store, NightlyProductKind.StarTrail);
            stillProvenance = (await harness.Store.OpenProvenanceAsync(stillId, CancellationToken.None))!.Content.ToArray();
        }

        using var restarted = Create();
        using var history = CreateAutomation(restarted);
        await AssertMaintenanceSummaryUnchangedAsync(restarted.Store, original, expectedProducts: 2);
        await RetryStillAsync(history.Store, failedKey, "retry-pointer-failure");
        await history.Runner.SweepAsync(CancellationToken.None);
        var runs = (await history.Store.GetStateAsync(CancellationToken.None)).Runs;
        Assert.AreEqual(2, runs.Count);
        var succeeded = runs.Single(run => run.Attempt == 2);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, succeeded.Outcome, succeeded.Detail);
        StringAssert.Contains(succeeded.Detail, "0 products published", StringComparison.Ordinal);
        StringAssert.Contains(succeeded.Detail, "Daily summary refresh: 0 published, 1 reused, 0 unchanged.", StringComparison.Ordinal);
        Assert.AreEqual(runs.Single(run => run.Attempt == 1).Occurrence!.IdentitySha256, succeeded.Occurrence!.IdentitySha256);
        AssertRefreshFailure(runs.Single(run => run.Attempt == 1),
            reuse ? "0 published, 1 reused, 0 unchanged" : "1 published, 0 reused, 0 unchanged");
        var finalDay = await restarted.Store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
        Assert.AreEqual(2, finalDay.Products.Count(product => product.Kind == NightlyProductKind.DailySummary),
            "Retry must reuse the exact published identity without a duplicate publication.");
        Assert.AreEqual(publishedId, finalDay.Windows.Single(window => window.Status.Kind == NightlyProductKind.DailySummary).FinalProduct!.ProductId);
        Assert.AreEqual(stillId, (await restarted.Store.GetPresentationAsync(publishedId, CancellationToken.None))!.DailySummary!.Products.Single().ProductId);
        await restarted.Store.VerifyPublicationAsync(publishedId, CancellationToken.None);
        await restarted.Store.VerifyPublicationAsync(stillId, CancellationToken.None);
        CollectionAssert.AreEqual(publishedProvenance!, (await restarted.Store.OpenProvenanceAsync(publishedId, CancellationToken.None))!.Content.ToArray());
        CollectionAssert.AreEqual(stillProvenance, (await restarted.Store.OpenProvenanceAsync(stillId, CancellationToken.None))!.Content.ToArray());
        CollectionAssert.AreEqual(original.Provenance, (await restarted.Store.OpenProvenanceAsync(original.Window.ProductIds.Single(), CancellationToken.None))!.Content.ToArray());
    }

    [TestMethod]
    public async Task LaterFailingSummaryAddsItsPublicationToEarlierRefreshCounts()
    {
        _reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16)));
        using (var seed = Create())
        {
            foreach (var id in new[] { "summary-a", "summary-b" })
            {
                var report = await seed.Generator.RunAsync(NightlyProductFixture.Occurrence(
                    NightlyProductKind.DailySummary, definitionId: id), CancellationToken.None);
                Assert.AreEqual(1, report.ProductsPublished, report.Describe());
            }
        }

        var produced = 0;
        var reachedSyntheticThrow = false;
        using var harness = Create(recordWindow: async (store, status, fingerprint, ids, token) =>
        {
            if (status.Kind == NightlyProductKind.DailySummary && status.Scope == NightlyProductScope.Final &&
                status.Disposition == NightlyProductWindowDisposition.Produced && ++produced == 2)
            {
                reachedSyntheticThrow = true;
                throw new IOException("Synthetic second summary pointer failure.");
            }
            await store.RecordWindowAsync(status, fingerprint, ids, token);
        });
        using var automation = CreateAutomation(harness);
        await QueueStillAsync(automation.Store, NightlyProductKind.StarTrail);
        await automation.Runner.SweepAsync(CancellationToken.None);
        Assert.IsTrue(reachedSyntheticThrow);
        Assert.AreEqual(2, produced);
        var failed = (await automation.Store.GetStateAsync(CancellationToken.None)).Runs.Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, failed.Outcome, failed.Detail);
        AssertRefreshFailure(failed, "2 published, 0 reused, 0 unchanged");
    }

    private async Task<MaintenanceSummary> SeedMaintenanceSummaryAsync()
    {
        _reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16)));
        using var seed = Create();
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.DailySummary);
        var report = await seed.Generator.RunAsync(occurrence, CancellationToken.None);
        Assert.AreEqual(1, report.ProductsPublished, report.Describe());
        var window = await seed.Store.ReadWindowAsync(NightlyProductKind.DailySummary, NightlyProductScope.Final,
            occurrence.SourceWindow!.StartUtc, CancellationToken.None, occurrence.IdentitySha256);
        Assert.IsNotNull(window);
        var bytes = (await seed.Store.OpenProvenanceAsync(window.ProductIds.Single(), CancellationToken.None))!.Content.ToArray();
        return new(window, bytes);
    }

    private static async Task<Guid> VerifyStillFinalAsync(SqliteNightlyProductStore store, NightlyProductKind kind)
    {
        var day = await store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
        var final = day.Products.Single(product => product.Kind == kind && product.Scope == NightlyProductScope.Final);
        await store.VerifyPublicationAsync(final.ProductId, CancellationToken.None);
        return final.ProductId;
    }

    private static async Task AssertMaintenanceSummaryUnchangedAsync(SqliteNightlyProductStore store, MaintenanceSummary original,
        int expectedProducts = 1)
    {
        var day = await store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
        Assert.AreEqual(expectedProducts, day.Products.Count(product => product.Kind == NightlyProductKind.DailySummary));
        Assert.AreEqual(original.Window.ProductIds.Single(),
            day.Windows.Single(window => window.Status.Kind == NightlyProductKind.DailySummary).FinalProduct!.ProductId);
        var retained = await store.ReadWindowAsync(NightlyProductKind.DailySummary, NightlyProductScope.Final,
            original.Window.Status.WindowStartUtc, CancellationToken.None, original.Window.Status.Occurrence.IdentitySha256);
        Assert.IsNotNull(retained);
        Assert.AreEqual(original.Window.FingerprintSha256, retained.FingerprintSha256);
        await store.VerifyPublicationAsync(original.Window.ProductIds.Single(), CancellationToken.None);
        CollectionAssert.AreEqual(original.Provenance,
            (await store.OpenProvenanceAsync(original.Window.ProductIds.Single(), CancellationToken.None))!.Content.ToArray());
    }

    private sealed record MaintenanceSummary(NightlyWindowState Window, byte[] Provenance);
}
