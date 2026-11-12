using System.Diagnostics.CodeAnalysis;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Components.Presentation;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeLapses;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods.")]
public sealed class SqliteTimeLapseStoreTests
{
    [TestMethod]
    [DataRow(CameraAgentTimeLapseState.NoSources)]
    [DataRow(CameraAgentTimeLapseState.Failed)]
    [DataRow(CameraAgentTimeLapseState.Unavailable)]
    public async Task Library_LaterNonProducedOccurrenceKeepsPublishedVideoAndProducedLink(CameraAgentTimeLapseState outcome)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-library-published");
        try
        {
            var options = Options(root);
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            using var store = new SqliteTimeLapseStore(options, clock);
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var occurrence = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.SunriseDay);
            var hours = Windows(NightlyProductFixture.ObservingDate, LocalAutomationSourceWindowKind.CompletedCivilHour);
            var preset = options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration());
            await store.EnqueueAsync(occurrence, preset, hours, CancellationToken.None);
            for (var index = 0; index < hours.Count; index++)
                await FinishAsync(store, clock, root, await ClaimAsync(store, clock), CameraAgentTimeLapseState.NoSources);
            var publishedJob = await ClaimAsync(store, clock);
            await FinishAsync(store, clock, root, publishedJob, CameraAgentTimeLapseState.Produced);
            await RecordLibraryProductAsync(root, publishedJob);
            var query = new Common.NightlyProducts.NightlyProductLibraryQuery(1);
            var original = (await store.ListLibraryAsync(query, CancellationToken.None)).Entries.Single().Product!;

            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            var later = Occurrence(options.Value.TimeLapses, occurrence.SourceWindow!, "later-video-definition");
            await store.EnqueueAsync(later, preset, hours, CancellationToken.None);
            var active = (await store.ListLibraryAsync(query with { State = Common.NightlyProducts.NightlyProductLibraryState.Running }, CancellationToken.None)).Entries.Single();
            Assert.IsNull(active.Product, "A new active occurrence must still take precedence over the published video.");
            Assert.IsEmpty((await store.ListLibraryAsync(query with { State = Common.NightlyProducts.NightlyProductLibraryState.Produced }, CancellationToken.None)).Entries);
            for (var index = 0; index < hours.Count; index++)
                await FinishAsync(store, clock, root, await ClaimAsync(store, clock), CameraAgentTimeLapseState.NoSources);
            var laterJob = await ClaimAsync(store, clock);
            await FinishAsync(store, clock, root, laterJob, outcome);

            var retained = (await store.ListLibraryAsync(query, CancellationToken.None)).Entries.Single();
            Assert.AreEqual(Common.NightlyProducts.NightlyProductLibraryState.Produced, retained.State);
            Assert.AreEqual(publishedJob.JobId, retained.JobId);
            Assert.AreEqual(original.ProductId, retained.Product!.ProductId);
            var produced = await store.ListLibraryAsync(query with { State = Common.NightlyProducts.NightlyProductLibraryState.Produced }, CancellationToken.None);
            Assert.AreEqual(original.ProductId, produced.Entries.Single().Product!.ProductId);
            Assert.IsEmpty((await store.ListLibraryAsync(query with { State = Common.NightlyProducts.NightlyProductLibraryState.NotProduced }, CancellationToken.None)).Entries);
            using var context = new BunitContext();
            context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(new TestNightlyProductUiService { VideoLibrary = produced });
            var cut = context.Render<TimeLapseLibraryView>(parameters => parameters
                .Add(page => page.Type, "time-lapse").Add(page => page.Status, "produced"));
            Assert.AreEqual("produced", cut.Find("li").GetAttribute("data-video-state"));
            Assert.AreEqual(TimeLapseLinks.Detail(original.ProductId), cut.Find("li a").GetAttribute("href"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Library_QueuedWorkingAndRestartRecoveryRetainActualStartAndWindow()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-library");
        try
        {
            var options = Options(root);
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            var daily = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.SunriseDay);
            var hours = Windows(NightlyProductFixture.ObservingDate, LocalAutomationSourceWindowKind.CompletedCivilHour);
            var preset = options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration());
            var query = new Common.NightlyProducts.NightlyProductLibraryQuery(1, State: Common.NightlyProducts.NightlyProductLibraryState.Running);
            DateTimeOffset started;
            using (var store = new SqliteTimeLapseStore(options, clock))
            {
                await store.EnqueueAsync(daily, preset, hours, CancellationToken.None);
                var queued = (await store.ListLibraryAsync(query, CancellationToken.None)).Entries.Single();
                Assert.AreEqual(CameraAgentTimeLapseState.Queued, queued.JobState);
                Assert.IsNull(queued.StartedUtc);
                Assert.AreEqual(daily.SourceWindow, queued.Window);
                Assert.IsNull(queued.Product);
                using var worker = await store.AcquireWorkerAsync(CancellationToken.None);
                while (await store.ClaimAsync(CancellationToken.None) is { ParentJobId: not null } hour)
                    await store.CompleteAsync(hour.JobId, CameraAgentTimeLapseState.NoSources, "empty", null, CancellationToken.None);
                started = clock.UtcNow;
                var running = (await store.ListLibraryAsync(query, CancellationToken.None)).Entries.Single();
                Assert.AreEqual(CameraAgentTimeLapseState.Working, running.JobState);
                Assert.AreEqual(started, running.StartedUtc);
                using var disabled = new SqliteTimeLapseStore(Microsoft.Extensions.Options.Options.Create(
                    new CameraAgentHostOptions { RawIngressRoot = root, TimeLapses = new() { Enabled = false } }), clock);
                Assert.IsEmpty((await disabled.ListLibraryAsync(query, CancellationToken.None)).Entries,
                    "A restarted host with video generation disabled must not claim an old Working row is running.");
                var stopped = (await disabled.ListLibraryAsync(query with { State = Common.NightlyProducts.NightlyProductLibraryState.Stalled }, CancellationToken.None)).Entries.Single();
                Assert.AreEqual(CameraAgentTimeLapseState.Working, stopped.JobState);
                Assert.AreEqual(started, stopped.StartedUtc);
                Assert.IsEmpty((await store.ListLibraryAsync(query with { State = Common.NightlyProducts.NightlyProductLibraryState.Produced }, CancellationToken.None)).Entries);
            }
            clock.UtcNow = clock.UtcNow.AddMinutes(15);
            using var reopened = new SqliteTimeLapseStore(options, clock);
            using var lease = await reopened.AcquireWorkerAsync(CancellationToken.None);
            var retrying = (await reopened.ListLibraryAsync(query, CancellationToken.None)).Entries.Single();
            Assert.AreEqual(CameraAgentTimeLapseState.Queued, retrying.JobState);
            Assert.AreEqual("timelapse.recovering", retrying.ReasonCode);
            Assert.AreEqual(started, retrying.StartedUtc);
            Assert.AreEqual(daily.SourceWindow, retrying.Window);
            var claimed = await reopened.ClaimAsync(CancellationToken.None);
            Assert.AreEqual(clock.UtcNow, claimed!.StartedUtc);
            await reopened.CompleteAsync(claimed.JobId, CameraAgentTimeLapseState.NoSources, "empty", null, CancellationToken.None);
            Assert.IsEmpty((await reopened.ListLibraryAsync(query, CancellationToken.None)).Entries);
            Assert.AreEqual(Common.NightlyProducts.NightlyProductLibraryState.NotProduced,
                (await reopened.ListLibraryAsync(query with { State = null }, CancellationToken.None)).Entries.Single().State);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(Common.NightlyProducts.NightlyProductLibraryState.Produced)]
    [DataRow(Common.NightlyProducts.NightlyProductLibraryState.Partial)]
    [DataRow(Common.NightlyProducts.NightlyProductLibraryState.NotProduced)]
    public async Task Library_TransitionsFromRunningToTheRecordedPublicationOutcome(Common.NightlyProducts.NightlyProductLibraryState expected)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-library-transition");
        try
        {
            var options = Options(root);
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            using var store = new SqliteTimeLapseStore(options, clock);
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var occurrence = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.SunriseDay);
            var hours = Windows(NightlyProductFixture.ObservingDate, LocalAutomationSourceWindowKind.CompletedCivilHour);
            await store.EnqueueAsync(occurrence, options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()), hours, CancellationToken.None);
            var query = new Common.NightlyProducts.NightlyProductLibraryQuery(1, State: Common.NightlyProducts.NightlyProductLibraryState.Running);
            Assert.HasCount(1, (await store.ListLibraryAsync(query, CancellationToken.None)).Entries);
            for (var index = 0; index < hours.Count; index++)
            {
                var child = await ClaimAsync(store, clock);
                var state = expected == Common.NightlyProducts.NightlyProductLibraryState.Partial && index == 0
                    ? CameraAgentTimeLapseState.Produced : CameraAgentTimeLapseState.NoSources;
                await FinishAsync(store, clock, root, child, state);
                if (state == CameraAgentTimeLapseState.Produced) await RecordLibraryProductAsync(root, child);
            }
            var daily = await ClaimAsync(store, clock);
            Assert.AreEqual(CameraAgentTimeLapseState.Working, (await store.ListLibraryAsync(query, CancellationToken.None)).Entries.Single().JobState);
            await FinishAsync(store, clock, root, daily, expected == Common.NightlyProducts.NightlyProductLibraryState.Produced
                ? CameraAgentTimeLapseState.Produced : CameraAgentTimeLapseState.NoSources);
            if (expected == Common.NightlyProducts.NightlyProductLibraryState.Produced) await RecordLibraryProductAsync(root, daily);
            Assert.IsEmpty((await store.ListLibraryAsync(query, CancellationToken.None)).Entries);
            var completed = (await store.ListLibraryAsync(query with { State = expected }, CancellationToken.None)).Entries.Single();
            Assert.AreEqual(expected, completed.State);
            Assert.AreEqual(expected == Common.NightlyProducts.NightlyProductLibraryState.Produced, completed.Product is not null);
            Assert.IsNull(completed.StartedUtc, "A terminal update time is not the historical start.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Library_PagesReportDatesByKeysetCursorWithoutDuplicatesOrGaps()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-library-paging");
        try
        {
            var dates = Enumerable.Range(0, 7).Select(NightlyProductFixture.ObservingDate.AddDays).ToArray();
            using var seed = new LibrarySeed(root, dates[^1]);
            // Outcomes alternate so a status filter has dates to skip, and one date also holds a second site's period.
            for (var index = 0; index < dates.Length; index++)
                await seed.DailyAsync(dates[index], index % 2 == 0 ? CameraAgentTimeLapseState.Produced : CameraAgentTimeLapseState.NoSources);
            await seed.DailyAsync(dates[3], CameraAgentTimeLapseState.NoSources, LibrarySeed.OtherSitePlanner);

            var everything = await ReadEveryPageAsync(seed.Store, new(3), expectedPages: 3);
            CollectionAssert.AreEqual(new[] { dates[6], dates[5], dates[4], dates[3], dates[3], dates[2], dates[1], dates[0] },
                everything.Select(static entry => entry.ReportDate).ToArray());
            Assert.HasCount(8, everything.Select(static entry => entry.JobId).Distinct());
            Assert.HasCount(2, everything.Where(entry => entry.ReportDate == dates[3])
                .Select(static entry => entry.Window.ReportingPeriod.IdentitySha256).Distinct());

            // Every page size reaches the same rows in the same order, so no cursor position loses or repeats a date.
            foreach (var size in new[] { 1, 2, 4, 7, Common.NightlyProducts.NightlyProductProjectionContract.MaximumLibraryDates })
            {
                var paged = await ReadEveryPageAsync(seed.Store, new(size), expectedPages: (dates.Length + size - 1) / size);
                CollectionAssert.AreEqual(everything.Select(static entry => entry.JobId).ToArray(), paged.Select(static entry => entry.JobId).ToArray());
            }

            // A status filter pages over its matching dates only, skipping the others without ending the walk early.
            var produced = await ReadEveryPageAsync(seed.Store, new(2, State: Common.NightlyProducts.NightlyProductLibraryState.Produced), expectedPages: 2);
            CollectionAssert.AreEqual(new[] { dates[6], dates[4], dates[2], dates[0] }, produced.Select(static entry => entry.ReportDate).ToArray());
            var notProduced = await ReadEveryPageAsync(seed.Store, new(2, State: Common.NightlyProducts.NightlyProductLibraryState.NotProduced), expectedPages: 2);
            CollectionAssert.AreEqual(new[] { dates[5], dates[3], dates[3], dates[1] }, notProduced.Select(static entry => entry.ReportDate).ToArray());

            // A cursor is exclusive, a single day is never paged, and a page size outside its bound is refused.
            var below = await seed.Store.ListLibraryAsync(new(3, Before: dates[0]), CancellationToken.None);
            Assert.IsEmpty(below.Entries);
            Assert.IsNull(below.NextBefore);
            var day = await seed.Store.ListLibraryAsync(new(3, ObservingDate: dates[3]), CancellationToken.None);
            Assert.HasCount(2, day.Entries);
            Assert.IsNull(day.NextBefore);
            foreach (var invalid in new Common.NightlyProducts.NightlyProductLibraryQuery[]
                     {
                         new(3, ObservingDate: dates[3], Before: dates[4]), new(0),
                         new(Common.NightlyProducts.NightlyProductProjectionContract.MaximumLibraryDates + 1)
                     })
                await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
                    await seed.Store.ListLibraryAsync(invalid, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Library_StatusFilteredScanStopsAtTheScannedDateCapAndReportsHowFarItSearched()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-library-scan");
        try
        {
            const int cap = Common.NightlyProducts.NightlyProductProjectionContract.MaximumLibraryScannedDates;
            // Two more report dates than one scan examines. Only the two oldest hold a produced video.
            var dates = Enumerable.Range(0, cap + 2).Select(NightlyProductFixture.ObservingDate.AddDays).ToArray();
            using var seed = new LibrarySeed(root, dates[^1]);
            for (var index = 0; index < dates.Length; index++)
                await seed.DailyAsync(dates[index], index < 2 ? CameraAgentTimeLapseState.Produced : CameraAgentTimeLapseState.NoSources);
            var query = new Common.NightlyProducts.NightlyProductLibraryQuery(5, State: Common.NightlyProducts.NightlyProductLibraryState.Produced);

            // The newest `cap` dates are examined and none matches; the page says where it stopped instead of reading on.
            var stopped = await seed.Store.ListLibraryAsync(query, CancellationToken.None);
            Assert.IsEmpty(stopped.Entries);
            Assert.AreEqual(dates[2], stopped.SearchedThrough);
            Assert.AreEqual(dates[2], stopped.NextBefore);

            // Continuing from that cursor reaches the matches the first scan never read: stopping skipped nothing.
            var continued = await seed.Store.ListLibraryAsync(query with { Before = stopped.NextBefore }, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { dates[1], dates[0] }, continued.Entries.Select(static entry => entry.ReportDate).ToArray());
            Assert.IsNull(continued.NextBefore);
            Assert.IsNull(continued.SearchedThrough);

            // Exactly `cap` remaining dates are all examined, so nothing is reported as truncated.
            var exact = await seed.Store.ListLibraryAsync(query with { Before = dates[cap] }, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { dates[1], dates[0] }, exact.Entries.Select(static entry => entry.ReportDate).ToArray());
            Assert.IsNull(exact.NextBefore);
            Assert.IsNull(exact.SearchedThrough);

            // One date more than the cap is truncated: the scan ends on its last examined date, one short of the oldest.
            var over = await seed.Store.ListLibraryAsync(query with { Before = dates[cap + 1] }, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { dates[1] }, over.Entries.Select(static entry => entry.ReportDate).ToArray());
            Assert.AreEqual(dates[1], over.SearchedThrough);
            Assert.AreEqual(dates[1], over.NextBefore);

            // A truncated scan that still fills its page carries a cursor but no bounded-search notice.
            var filled = await seed.Store.ListLibraryAsync(
                query with { State = Common.NightlyProducts.NightlyProductLibraryState.NotProduced }, CancellationToken.None);
            CollectionAssert.AreEqual(dates[^5..].Reverse().ToArray(), filled.Entries.Select(static entry => entry.ReportDate).ToArray());
            Assert.AreEqual(dates[^5], filled.NextBefore);
            Assert.IsNull(filled.SearchedThrough);

            // Without a status filter the scan is the page itself, never the cap.
            var unfiltered = await seed.Store.ListLibraryAsync(new(5), CancellationToken.None);
            CollectionAssert.AreEqual(dates[^5..].Reverse().ToArray(), unfiltered.Entries.Select(static entry => entry.ReportDate).ToArray());
            Assert.AreEqual(dates[^5], unfiltered.NextBefore);
            Assert.IsNull(unfiltered.SearchedThrough);

            // Walking every page from the top returns each produced date once, across the truncated scan.
            var walked = await ReadEveryPageAsync(seed.Store, query, expectedPages: 2);
            CollectionAssert.AreEqual(new[] { dates[1], dates[0] }, walked.Select(static entry => entry.ReportDate).ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Library_RefusesAPageWithMoreRowsThanItsWindowBound()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-library-bound");
        try
        {
            const int bound = 4096;
            var date = NightlyProductFixture.ObservingDate;
            using var seed = new LibrarySeed(root, date);
            await seed.DailyAsync(date, CameraAgentTimeLapseState.NoSources);
            await seed.PeriodsAsync(date, firstSiteVersion: 2, count: bound - 1);
            // The unfiltered page and the status-filtered scan select their dates differently and share the row bound.
            var queries = new Common.NightlyProducts.NightlyProductLibraryQuery[]
            {
                new(1), new(1, State: Common.NightlyProducts.NightlyProductLibraryState.NotProduced)
            };

            // Exactly the bound is still a page: every retained period of the date is returned once.
            foreach (var query in queries)
            {
                var full = await seed.Store.ListLibraryAsync(query, CancellationToken.None);
                Assert.HasCount(bound, full.Entries);
                Assert.HasCount(bound, full.Entries.Select(static entry => entry.Window.ReportingPeriod.IdentitySha256).Distinct());
                Assert.IsNull(full.NextBefore);
            }

            // One row more is refused outright rather than returned short.
            await seed.PeriodsAsync(date, firstSiteVersion: bound + 1, count: 1);
            foreach (var query in queries)
            {
                var refused = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await seed.Store.ListLibraryAsync(query, CancellationToken.None));
                Assert.AreEqual("The video library exceeds its window bound.", refused.Message);
            }

            // The bound is per page, not per store: a date outside the page is unaffected by the oversized one.
            await seed.DailyAsync(date.AddDays(1), CameraAgentTimeLapseState.NoSources);
            var other = await seed.Store.ListLibraryAsync(new(1), CancellationToken.None);
            Assert.AreEqual(date.AddDays(1), other.Entries.Single().ReportDate);
            Assert.AreEqual(date.AddDays(1), other.NextBefore);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Library_QueuedDailyJobReportsItsHourlySegmentProgress()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-library-hours");
        try
        {
            var options = Options(root);
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            using var store = new SqliteTimeLapseStore(options, clock);
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var hours = Windows(NightlyProductFixture.ObservingDate, LocalAutomationSourceWindowKind.CompletedCivilHour);
            await store.EnqueueAsync(Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.SunriseDay),
                options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()), hours, CancellationToken.None);
            var query = new Common.NightlyProducts.NightlyProductLibraryQuery(1);
            async Task<CameraAgentTimeLapseLibraryEntry> ReadAsync() => (await store.ListLibraryAsync(query, CancellationToken.None)).Entries.Single();

            // Nothing claimed yet: the daily job has not started in any sense.
            var waiting = await ReadAsync();
            Assert.AreEqual(CameraAgentTimeLapseState.Queued, waiting.JobState);
            Assert.AreEqual((hours.Count, 0, false, false),
                (waiting.HourlyJobs, waiting.HourlyJobsFinished, waiting.HourlyJobWorking, waiting.HourlyGenerationStarted));

            // The first hourly segment is being generated. The daily job is still Queued, but generation is under way.
            var first = await ClaimAsync(store, clock);
            Assert.IsNotNull(first.ParentJobId);
            var encoding = await ReadAsync();
            Assert.AreEqual(CameraAgentTimeLapseState.Queued, encoding.JobState);
            Assert.IsNull(encoding.StartedUtc);
            Assert.AreEqual((hours.Count, 0, true, true),
                (encoding.HourlyJobs, encoding.HourlyJobsFinished, encoding.HourlyJobWorking, encoding.HourlyGenerationStarted));

            // Between segments none is working, yet finished segments still show that the job has begun.
            await FinishAsync(store, clock, root, first, CameraAgentTimeLapseState.NoSources);
            var between = await ReadAsync();
            Assert.AreEqual((hours.Count, 1, false, true),
                (between.HourlyJobs, between.HourlyJobsFinished, between.HourlyJobWorking, between.HourlyGenerationStarted));

            // A failed segment is finished too; every outcome other than Queued and Working counts.
            await FinishAsync(store, clock, root, await ClaimAsync(store, clock), CameraAgentTimeLapseState.Failed);
            for (var index = 2; index < hours.Count; index++)
                await FinishAsync(store, clock, root, await ClaimAsync(store, clock), CameraAgentTimeLapseState.NoSources);
            var assembled = await ReadAsync();
            Assert.AreEqual(CameraAgentTimeLapseState.Queued, assembled.JobState);
            Assert.AreEqual((hours.Count, hours.Count, false, true),
                (assembled.HourlyJobs, assembled.HourlyJobsFinished, assembled.HourlyJobWorking, assembled.HourlyGenerationStarted));

            // Once the daily job itself is claimed or settled, its own state speaks and the segment counts are not read.
            var daily = await ClaimAsync(store, clock);
            Assert.IsNull(daily.ParentJobId);
            var working = await ReadAsync();
            Assert.AreEqual(CameraAgentTimeLapseState.Working, working.JobState);
            Assert.AreEqual((0, 0, false, false),
                (working.HourlyJobs, working.HourlyJobsFinished, working.HourlyJobWorking, working.HourlyGenerationStarted));
            await FinishAsync(store, clock, root, daily, CameraAgentTimeLapseState.Failed);
            var settled = await ReadAsync();
            Assert.AreEqual(CameraAgentTimeLapseState.Failed, settled.JobState);
            Assert.IsFalse(settled.HourlyGenerationStarted);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Follows <c>NextBefore</c> from the query's own cursor, checking each page against the walk so far.</summary>
    private static async Task<List<CameraAgentTimeLapseLibraryEntry>> ReadEveryPageAsync(SqliteTimeLapseStore store,
        Common.NightlyProducts.NightlyProductLibraryQuery query, int expectedPages)
    {
        var entries = new List<CameraAgentTimeLapseLibraryEntry>();
        var before = query.Before;
        for (var pages = 1; ; pages++)
        {
            Assert.IsTrue(pages <= expectedPages, $"The walk needed more than {expectedPages} pages.");
            var page = await store.ListLibraryAsync(query with { Before = before }, CancellationToken.None);
            var pageDates = page.Entries.Select(static entry => entry.ReportDate).Distinct().ToArray();
            Assert.IsTrue(pageDates.Length <= query.DateCount, "A page never holds more report dates than requested.");
            Assert.IsTrue(pageDates.All(date => before is null || date < before), "A page never reaches its own cursor.");
            Assert.IsTrue(entries.Count == 0 || pageDates.All(date => date < entries[^1].ReportDate), "Pages never overlap.");
            entries.AddRange(page.Entries);
            if (page.NextBefore is not { } next)
            {
                Assert.AreEqual(expectedPages, pages);
                return entries;
            }
            Assert.IsTrue(before is null || next < before, "The cursor only moves to older dates.");
            before = next;
        }
    }

    private static async Task RecordLibraryProductAsync(string root, CameraAgentTimeLapseJob job)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, ".time-lapses", "time-lapses.db"), Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        // Thin catalog fixture; full provenance is deliberately absent to detect eager source/packet loading.
        command.CommandText = """
            INSERT INTO products(product_id,output_sha256,job_id,report_date,period_sha256,window_sha256,start_ticks,end_ticks,
                preset_sha256,is_daily,document_json,document_sha256,payload_path,payload_bytes,created_ticks,width,height,has_gaps)
            SELECT product_id,lower(hex(randomblob(32))),job_id,report_date,$period,$window,$start,$end,$preset,$daily,
                '{}',$sha,product_id || '.mp4',1,updated_ticks,640,480,0 FROM jobs WHERE job_id=$id;
            """;
        command.Parameters.AddWithValue("$id", job.JobId.ToString("N"));
        command.Parameters.AddWithValue("$period", job.Window.ReportingPeriod.IdentitySha256);
        command.Parameters.AddWithValue("$window", job.Window.IdentitySha256);
        command.Parameters.AddWithValue("$start", job.Window.StartUtc.UtcTicks);
        command.Parameters.AddWithValue("$end", job.Window.EndUtc.UtcTicks);
        command.Parameters.AddWithValue("$preset", job.Preset.IdentitySha256);
        command.Parameters.AddWithValue("$daily", job.Window.Policy.Kind == LocalAutomationSourceWindowKind.SunriseDay ? 1 : 0);
        command.Parameters.AddWithValue("$sha", new string('A', 64));
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
    }

    [TestMethod]
    public async Task ZeroNineStoredJobReadsRecoversAndCompletesWithoutChangingItsPreset()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-upgrade");
        try
        {
            await RestoreZeroNineAsync(root);
            var options = Options(root);
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            Guid jobId;
            string presetIdentity;
            using (var store = new SqliteTimeLapseStore(options, clock))
            {
                var retained = (await store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Jobs.Single();
                jobId = retained.JobId;
                presetIdentity = retained.Preset.IdentitySha256;
                Assert.AreEqual(CameraAgentTimeLapseState.Working, retained.State);
                Assert.IsNull(retained.Preset.RendererVersion);
                await store.EnqueueAsync(retained.Occurrence, retained.Preset, [], CancellationToken.None);
                Assert.AreEqual("5EDB50EBB57E57FC43B575269FD2DF62AF6D30760DFB8807C421B69D026D98A2", presetIdentity);
                using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
                var recovered = await store.ClaimAsync(CancellationToken.None);
                Assert.IsNotNull(recovered);
                Assert.AreEqual(jobId, recovered.JobId);
                Assert.AreEqual(retained.Preset, recovered.Preset);
                await store.FreezePlanAsync(jobId, new([], new Dictionary<string, int>()), CancellationToken.None);
                await store.CompleteAsync(jobId, CameraAgentTimeLapseState.NoSources, "timelapse.no-admitted-sources", null, CancellationToken.None);
            }
            using var reopened = new SqliteTimeLapseStore(options, clock);
            var completed = (await reopened.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Jobs.Single();
            Assert.AreEqual(jobId, completed.JobId);
            Assert.AreEqual(presetIdentity, completed.Preset.IdentitySha256);
            Assert.AreEqual(CameraAgentTimeLapseState.NoSources, completed.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NewPresetRecordsItsRendererAndCannotMasqueradeAsAReleasedOrUnknownRecipe()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-renderer-preset");
        try
        {
            await RestoreZeroNineAsync(root);
            var options = Options(root);
            using var store = new SqliteTimeLapseStore(options, new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));
            var legacy = (await store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Jobs.Single().Preset;
            var settings = new TimeLapseOptions
            {
                RigProfileSha256 = HVO.SkyMonitor.Astronomy.RigProjectionContextFactory.CreateProfileHashSha256(legacy.Rig),
                Frames = legacy.Frames,
                Timing = legacy.Timing,
                MaximumDimension = legacy.MaximumDimension
            };
            var current = settings.Freeze(NightlyProductFixture.Configuration() with { Rig = legacy.Rig });
            Assert.AreEqual(TimeLapseFrameRenderer.Version, current.RendererVersion);
            Assert.IsTrue(TimeLapseOptions.Matches(current));
            Assert.IsTrue(TimeLapseOptions.Matches(legacy));
            Assert.AreNotEqual(legacy.IdentitySha256, current.IdentitySha256);
            Assert.AreNotEqual("time-lapse:" + legacy.IdentitySha256, settings.Target);
            Assert.IsFalse(TimeLapseOptions.Matches(legacy with { RendererVersion = TimeLapseFrameRenderer.Version }));
            Assert.IsFalse(TimeLapseOptions.Matches(current with { RendererVersion = "unknown" }));
            Assert.IsFalse(TimeLapseOptions.Matches(current with { RendererVersion = null }));
        }
        finally { Directory.Delete(root, true); }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only the checked-in 0.9.0 fixture is executed; no user input.")]
    internal static async Task RestoreZeroNineAsync(string root)
    {
        var directory = Path.Combine(root, ".time-lapses");
        Directory.CreateDirectory(directory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "time-lapses.db"), Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "time-lapse-v090", "working-job.sql"));
        await command.ExecuteNonQueryAsync();
    }

    [TestMethod]
    public async Task Restart_RetainsFrozenOccurrenceAndPlanAndRecoversOnlyWorkingJobs()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-store");
        try
        {
            var options = Options(root);
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            var request = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.CompletedCivilHour);
            Guid jobId;
            using (var store = new SqliteTimeLapseStore(options, clock))
            {
                using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
                await store.EnqueueAsync(request, options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()), [], CancellationToken.None);
                await store.EnqueueAsync(request, options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()), [], CancellationToken.None);
                var job = await store.ClaimAsync(CancellationToken.None);
                Assert.IsNotNull(job);
                jobId = job.JobId;
                await store.FreezePlanAsync(jobId, new([], new Dictionary<string, int> { ["timelapse.solar-excluded"] = 2 }), CancellationToken.None);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await store.FreezePlanAsync(jobId, new([], new Dictionary<string, int>()), CancellationToken.None));
                Assert.HasCount(1, (await store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Jobs);
            }
            using var reopened = new SqliteTimeLapseStore(options, clock);
            using var recoveredLease = await reopened.AcquireWorkerAsync(CancellationToken.None);
            var recovered = await reopened.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(jobId, recovered.JobId);
            Assert.AreEqual(request, recovered.Occurrence);
            Assert.AreEqual(2, (await reopened.GetPlanAsync(jobId, CancellationToken.None))!.Exclusions["timelapse.solar-excluded"]);
            await reopened.CompleteAsync(jobId, CameraAgentTimeLapseState.NoSources, "timelapse.no-admitted-sources", null, CancellationToken.None);
            Assert.IsNull(await reopened.ClaimAsync(CancellationToken.None));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await reopened.CompleteAsync(jobId, CameraAgentTimeLapseState.Failed, "changed", null, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Daily_RequiresExactPartitionAndWaitsForEveryChildBeforeAssembly()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-day");
        try
        {
            var options = Options(root);
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            using var store = new SqliteTimeLapseStore(options, clock);
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var request = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.SunriseDay);
            var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar));
            var hours = planner.ResolveWindows(request.SourceWindow!.ReportingPeriod,
                request.SourceWindow.Policy with { Kind = LocalAutomationSourceWindowKind.CompletedCivilHour });
            var preset = options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration());
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await store.EnqueueAsync(request, preset, hours.Skip(1).ToArray(), CancellationToken.None));
            await store.EnqueueAsync(request, preset, hours, CancellationToken.None);
            Guid? parent = null;
            for (var i = 0; i < hours.Count; i++)
            {
                var child = await store.ClaimAsync(CancellationToken.None);
                Assert.IsNotNull(child);
                Assert.IsNotNull(child.ParentJobId);
                parent ??= child.ParentJobId;
                Assert.AreEqual(parent, child.ParentJobId);
                Assert.AreEqual(request, child.Occurrence);
                Assert.AreEqual(LocalAutomationSourceWindowKind.CompletedCivilHour, child.Window.Policy.Kind);
                await store.CompleteAsync(child.JobId, CameraAgentTimeLapseState.NoSources, "empty", null, CancellationToken.None);
            }
            var daily = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(daily);
            Assert.AreEqual(parent, daily.JobId);
            Assert.IsNull(daily.ParentJobId);
            Assert.AreEqual(request.SourceWindow, daily.Window);
            var hourly = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.CompletedCivilHour);
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await store.EnqueueAsync(hourly, preset, [hourly.SourceWindow!], CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Retry_IsAuditedIdempotentAndRevisionBoundWithoutChangingFrozenInputs()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-retry");
        try
        {
            var options = Options(root);
            using var store = new SqliteTimeLapseStore(options, new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var occurrence = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.CompletedCivilHour);
            await store.EnqueueAsync(occurrence, options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()), [], CancellationToken.None);
            var job = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(job);
            await store.FreezePlanAsync(job.JobId, new([], new Dictionary<string, int> { ["excluded"] = 3 }), CancellationToken.None);
            await store.CompleteAsync(job.JobId, CameraAgentTimeLapseState.Unavailable, "encoder-missing", null, CancellationToken.None);
            var failed = (await store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Jobs.Single();
            Assert.IsGreaterThan(job.Revision, failed.Revision);
            var requestId = Guid.NewGuid();
            Assert.IsFalse(await store.RetryAsync(job.JobId, job.Revision, requestId, "owner", "Retry", CancellationToken.None));
            Assert.IsTrue(await store.RetryAsync(job.JobId, failed.Revision, requestId, "owner", "Retry", CancellationToken.None));
            Assert.IsTrue(await store.RetryAsync(job.JobId, failed.Revision, requestId, "owner", "Retry", CancellationToken.None));
            Assert.IsFalse(await store.RetryAsync(job.JobId, failed.Revision, requestId, "different-owner", "Retry", CancellationToken.None));
            Assert.IsFalse(await store.RetryAsync(job.JobId, failed.Revision, Guid.NewGuid(), "owner", "Retry", CancellationToken.None));
            var retry = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(retry);
            Assert.AreEqual(occurrence, retry.Occurrence);
            Assert.AreEqual(3, (await store.GetPlanAsync(job.JobId, CancellationToken.None))!.Exclusions["excluded"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RestartRemovesOnlyRecordedUnpublishedFiles()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-publication-recovery");
        try
        {
            var options = Options(root);
            using var store = new SqliteTimeLapseStore(options, new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));
            await store.InitializeAsync(CancellationToken.None);
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(root, ".time-lapses", "products");
            Directory.CreateDirectory(directory);
            var interrupted = Path.Combine(directory, id + ".mp4");
            var temporary = Path.Combine(directory, id + ".json." + Guid.NewGuid().ToString("N") + ".tmp");
            var unrelated = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp4");
            await File.WriteAllTextAsync(interrupted, "interrupted");
            await File.WriteAllTextAsync(temporary, "interrupted");
            await File.WriteAllTextAsync(unrelated, "retained");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, ".time-lapses", "time-lapses.db"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO publications(product_id) VALUES($id);";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync();
            }
            using var worker = await store.AcquireWorkerAsync(CancellationToken.None);
            Assert.IsFalse(File.Exists(interrupted));
            Assert.IsFalse(File.Exists(temporary));
            Assert.AreEqual("retained", await File.ReadAllTextAsync(unrelated));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task TamperedFrozenPlan_FailsClosedInsteadOfChangingRetryInputs()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-tamper");
        try
        {
            var options = Options(root);
            using var store = new SqliteTimeLapseStore(options, new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var request = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.CompletedCivilHour);
            await store.EnqueueAsync(request, options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()), [], CancellationToken.None);
            var job = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(job);
            await store.FreezePlanAsync(job.JobId, new([], new Dictionary<string, int>()), CancellationToken.None);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, ".time-lapses", "time-lapses.db"), Pooling = false }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER immutable_job_inputs; UPDATE jobs SET plan_sha256='bad';";
            await command.ExecuteNonQueryAsync();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await store.GetPlanAsync(job.JobId, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Summaries_UseLatestJobPerWindowForDailyStateProducedHoursAndPendingWork()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-summary");
        try
        {
            var options = Options(root);
            var preset = options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration());
            var hourlyDate = NightlyProductFixture.ObservingDate;
            var dailyDate = hourlyDate.AddDays(1);
            var workingDate = hourlyDate.AddDays(2);
            var clock = new NightlyClock(NightlyProductFixture.Calendar.Resolve(workingDate.AddDays(1)).EndUtc.AddMinutes(10));
            using var store = new SqliteTimeLapseStore(options, clock);
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var hours = Windows(hourlyDate, LocalAutomationSourceWindowKind.CompletedCivilHour);
            // The rerun definition targets the same hour windows, so its newer jobs replace hours 0 and 1.
            foreach (var (definition, hour, state) in new[]
            {
                ("video-hour", 0, CameraAgentTimeLapseState.Produced), ("video-hour", 1, CameraAgentTimeLapseState.Produced),
                ("video-hour", 2, CameraAgentTimeLapseState.NoSources), ("video-hour", 3, CameraAgentTimeLapseState.Produced),
                ("video-hour-rerun", 1, CameraAgentTimeLapseState.Failed), ("video-hour-rerun", 0, CameraAgentTimeLapseState.Produced)
            })
            {
                await store.EnqueueAsync(Occurrence(options.Value.TimeLapses, hours[hour], definition), preset, [], CancellationToken.None);
                await FinishAsync(store, clock, root, await ClaimAsync(store, clock), state);
            }
            var dayHours = Windows(dailyDate, LocalAutomationSourceWindowKind.CompletedCivilHour);
            await store.EnqueueAsync(Occurrence(options.Value.TimeLapses, Windows(dailyDate, LocalAutomationSourceWindowKind.SunriseDay).Single(), "video-day"),
                preset, dayHours, CancellationToken.None);
            for (var i = 0; i < dayHours.Count; i++)
            {
                var child = await ClaimAsync(store, clock);
                await FinishAsync(store, clock, root, child, dayHours.Take(3).Any(hour => hour.IdentitySha256 == child.Window.IdentitySha256)
                    ? CameraAgentTimeLapseState.Produced : CameraAgentTimeLapseState.NoSources);
            }
            await FinishAsync(store, clock, root, await ClaimAsync(store, clock), CameraAgentTimeLapseState.Produced);
            var workingHour = Windows(workingDate, LocalAutomationSourceWindowKind.CompletedCivilHour)[0];
            await store.EnqueueAsync(Occurrence(options.Value.TimeLapses, workingHour, "video-hour"), preset, [], CancellationToken.None);
            await ClaimAsync(store, clock);
            await store.EnqueueAsync(Occurrence(options.Value.TimeLapses, hours[4], "video-hour"), preset, [], CancellationToken.None);
            foreach (var outside in new[] { hourlyDate.AddDays(-1), workingDate.AddDays(1) })
                await store.EnqueueAsync(Occurrence(options.Value.TimeLapses, Windows(outside, LocalAutomationSourceWindowKind.CompletedCivilHour)[0], "video-hour"),
                    preset, [], CancellationToken.None);

            var summaries = (await store.SummarizeAsync(hourlyDate, workingDate, CancellationToken.None)).ToDictionary(summary => summary.ReportDate);

            Assert.HasCount(3, summaries);
            Assert.AreEqual(new CameraAgentTimeLapseDateSummary(hourlyDate, hours[0].ReportingPeriod.IdentitySha256, null, 2, true), summaries[hourlyDate]);
            Assert.AreEqual(new CameraAgentTimeLapseDateSummary(dailyDate, dayHours[0].ReportingPeriod.IdentitySha256,
                CameraAgentTimeLapseState.Produced, 3, false), summaries[dailyDate]);
            Assert.AreEqual(new CameraAgentTimeLapseDateSummary(workingDate, workingHour.ReportingPeriod.IdentitySha256, null, 0, true), summaries[workingDate]);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static IOptions<CameraAgentHostOptions> Options(string root) => Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
    {
        RawIngressRoot = root,
        TimeLapses = new() { Enabled = true, RigProfileSha256 = NightlyProductFixture.RigProfileSha256 }
    });

    internal static LocalAutomationOccurrence Occurrence(TimeLapseOptions options, LocalAutomationSourceWindowKind kind) =>
        Occurrence(options, NightlyProductFixture.Occurrence(Common.NightlyProducts.NightlyProductKind.Keogram, windowKind: kind).SourceWindow!,
            kind == LocalAutomationSourceWindowKind.SunriseDay ? "video-day" : "video-hour");

    private static LocalAutomationOccurrence Occurrence(TimeLapseOptions options, LocalAutomationSourceWindow window, string definitionId)
    {
        var definition = new LocalAutomationDefinition(definitionId, "Video", true, LocalAutomationTaskKind.TimeLapseGeneration,
            options.Target, LocalAutomationTriggerKind.SourceWindowClosed, 1, NightlyProductFixture.DayStartUtc, window.Policy);
        return LocalAutomationWindowPlanner.CreateOccurrence(new(definition, 1,
            LocalAutomationContract.ComputeRevisionSha256(definition), null, null), window);
    }

    private static IReadOnlyList<LocalAutomationSourceWindow> Windows(DateOnly date, LocalAutomationSourceWindowKind kind) =>
        new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar)).ResolveWindows(date,
            NightlyProductFixture.Occurrence(Common.NightlyProducts.NightlyProductKind.Keogram, windowKind: kind).SourceWindow!.Policy);

    /// <summary>
    /// Seeds library rows over several report dates. Every row is enqueued by the store itself with its complete
    /// hourly partition, or carries a valid window from the product's own factories; only settling a job to its
    /// terminal state, which normally takes an encoded publication, and the bulk rows of the page-bound test are
    /// written directly.
    /// </summary>
    private sealed class LibrarySeed : IDisposable
    {
        internal static readonly LocalAutomationWindowPlanner OtherSitePlanner = new(new FixedObservingDayCalendarProvider(
            ObservingDayCalendar.ForDeployment(NightlyProductFixture.OtherSite)));
        private static readonly LocalAutomationWindowPlanner SitePlanner = new(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar));
        private static readonly LocalAutomationSourceWindowPolicy DailyPolicy = NightlyProductFixture.Occurrence(
            Common.NightlyProducts.NightlyProductKind.Keogram, windowKind: LocalAutomationSourceWindowKind.SunriseDay).SourceWindow!.Policy;
        private static readonly System.Text.Json.JsonSerializerOptions WindowJson = new(System.Text.Json.JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        private readonly string _root;
        private readonly IOptions<CameraAgentHostOptions> _options;
        private readonly CameraAgentTimeLapsePreset _preset;
        private readonly NightlyClock _clock;

        internal LibrarySeed(string root, DateOnly lastDate)
        {
            _root = root;
            _options = Options(root);
            _preset = _options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration());
            // Every seeded period, at either site, has ended and settled.
            _clock = new NightlyClock(NightlyProductFixture.Calendar.Resolve(lastDate.AddDays(2)).EndUtc);
            Store = new SqliteTimeLapseStore(_options, _clock);
        }

        internal SqliteTimeLapseStore Store { get; }

        public void Dispose() => Store.Dispose();

        /// <summary>One daily job of a report date, with its hourly jobs, settled to <paramref name="state"/>.</summary>
        internal async Task DailyAsync(DateOnly date, CameraAgentTimeLapseState state, LocalAutomationWindowPlanner? planner = null)
        {
            planner ??= SitePlanner;
            var window = planner.ResolveWindows(date, DailyPolicy).Single();
            var hours = planner.ResolveWindows(date, DailyPolicy with { Kind = LocalAutomationSourceWindowKind.CompletedCivilHour });
            var occurrence = Occurrence(_options.Value.TimeLapses, window, "video-day");
            await Store.EnqueueAsync(occurrence, _preset, hours, CancellationToken.None);
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
            using var connection = await OpenAsync();
            using var settle = connection.CreateCommand();
            settle.CommandText = """
                UPDATE jobs SET state='NoSources', reason='fixture', updated_ticks=$now, revision=revision+1
                WHERE state='Queued' AND parent_job_id IS NOT NULL;
                UPDATE jobs SET state=$state, reason=CASE WHEN $state='Produced' THEN NULL ELSE 'fixture' END,
                    product_id=CASE WHEN $state='Produced' THEN lower(hex(randomblob(16))) END, updated_ticks=$now, revision=revision+1
                WHERE state='Queued' AND parent_job_id IS NULL RETURNING job_id, product_id;
                """;
            settle.Parameters.AddWithValue("$now", _clock.UtcNow.UtcTicks);
            settle.Parameters.AddWithValue("$state", state.ToString());
            Guid jobId;
            Guid? productId;
            using (var reader = await settle.ExecuteReaderAsync())
            {
                Assert.IsTrue(await reader.ReadAsync(), "Exactly the daily job just enqueued is settled.");
                jobId = Guid.ParseExact(reader.GetString(0), "N");
                productId = await reader.IsDBNullAsync(1) ? null : Guid.ParseExact(reader.GetString(1), "N");
                Assert.IsFalse(await reader.ReadAsync());
            }
            if (productId is not null)
                await RecordLibraryProductAsync(_root, new(jobId, occurrence, window, null, _preset, state, _clock.UtcNow, null, productId));
        }

        /// <summary>
        /// More retained periods of one report date, one per site revision. A revision at the same coordinates has the
        /// same sunrise times and its own identity, so each window is built by the product's own factories from the
        /// fixture site's period with the revised site. The remaining columns are those of the date's stored daily job.
        /// </summary>
        internal async Task PeriodsAsync(DateOnly date, int firstSiteVersion, int count)
        {
            var site = NightlyProductFixture.Site;
            var period = SitePlanner.ResolveWindows(date, DailyPolicy).Single().ReportingPeriod;
            var day = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            using var connection = await OpenAsync();
            string occurrence, preset;
            long updated;
            using (var template = connection.CreateCommand())
            {
                template.CommandText = "SELECT occurrence_json, preset_json, updated_ticks FROM jobs WHERE report_date=$date AND parent_job_id IS NULL ORDER BY job_id LIMIT 1;";
                template.Parameters.AddWithValue("$date", day);
                using var reader = await template.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync(), "The date's own daily job is seeded first.");
                (occurrence, preset, updated) = (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
            }
            using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO jobs(job_id, occurrence_json, window_json, parent_job_id, preset_json, report_date, state, updated_ticks, reason)
                VALUES($id, $occurrence, $window, NULL, $preset, $date, 'NoSources', $updated, 'fixture');
                """;
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var json = insert.Parameters.Add("$window", SqliteType.Text);
            insert.Parameters.AddWithValue("$occurrence", occurrence);
            insert.Parameters.AddWithValue("$preset", preset);
            insert.Parameters.AddWithValue("$date", day);
            insert.Parameters.AddWithValue("$updated", updated);
            for (var version = firstSiteVersion; version < firstSiteVersion + count; version++)
            {
                var revised = HVO.SkyMonitor.AgentCore.DeploymentLocationSnapshot.Create(site.LocationId, version, site.Source, null,
                    site.EffectiveFromUtc, null, site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters, site.TimeZoneId);
                var window = LocalAutomationSourceWindow.Create(DailyPolicy, HVO.SkyMonitor.Astronomy.SunriseReportingPeriod.Create(
                    date, period.StartUtc, period.EndUtc, revised, period.TimeZoneRulesSha256,
                    period.StartSolarAlgorithmVersion, period.EndSolarAlgorithmVersion), period.StartUtc, period.EndUtc);
                id.Value = Guid.NewGuid().ToString("N");
                json.Value = System.Text.Json.JsonSerializer.Serialize(window, WindowJson);
                Assert.AreEqual(1, await insert.ExecuteNonQueryAsync());
            }
            await transaction.CommitAsync();
        }

        private async Task<SqliteConnection> OpenAsync()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(_root, ".time-lapses", "time-lapses.db"), Pooling = false }.ToString());
            await connection.OpenAsync();
            return connection;
        }
    }

    private static async Task<CameraAgentTimeLapseJob> ClaimAsync(SqliteTimeLapseStore store, NightlyClock clock)
    {
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        var job = await store.ClaimAsync(CancellationToken.None);
        Assert.IsNotNull(job);
        return job;
    }

    private static async Task FinishAsync(SqliteTimeLapseStore store, NightlyClock clock, string root, CameraAgentTimeLapseJob job,
        CameraAgentTimeLapseState state)
    {
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        if (state != CameraAgentTimeLapseState.Produced)
        {
            await store.CompleteAsync(job.JobId, state, "fixture", null, CancellationToken.None);
            return;
        }
        // A real Produced completion requires an encoded publication; summaries read only job rows, so record the terminal row directly.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, ".time-lapses", "time-lapses.db"), Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET state='Produced', product_id=$product, updated_ticks=$now, revision=revision+1 WHERE job_id=$id AND state='Working';";
        command.Parameters.AddWithValue("$product", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$now", clock.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$id", job.JobId.ToString("N"));
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
    }
}
