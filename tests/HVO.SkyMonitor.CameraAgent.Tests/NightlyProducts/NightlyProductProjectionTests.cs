using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods.")]
public sealed class NightlyProductProjectionTests
{
    private readonly List<string> _roots = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var root in _roots.Where(Directory.Exists)) Directory.Delete(root, true);
    }

    [TestMethod]
    public async Task FinalKeogram_RebuildsItsPlannedAxisWithLeadingInternalAndTrailingGaps()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddMinutes(10)),
            NightlyProductFixture.Frame(2, NightlyProductFixture.DayStartUtc.AddMinutes(41)),
            NightlyProductFixture.Frame(3, NightlyProductFixture.DayStartUtc.AddHours(16)));
        var options = NightlyProductFixture.Options(maximumSegmentSources: 2);
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, options);
        using var harness = Harness(reader, options);
        await harness.Run(occurrence);
        var final = await FinalId(harness.Store, occurrence);

        var presentation = await harness.Store.GetPresentationAsync(final, CancellationToken.None);

        Assert.IsNotNull(presentation);
        var axis = presentation.TimeAxis;
        Assert.IsNotNull(axis);
        Assert.IsTrue(axis.Planned);
        Assert.AreEqual(presentation.Detail.Summary.Width, axis.Width);
        Assert.AreEqual(occurrence.SourceWindow!.StartUtc, axis.StartUtc);
        Assert.AreEqual(occurrence.SourceWindow.EndUtc, axis.EndUtc);
        Assert.AreEqual(60, axis.ColumnSeconds);
        Assert.AreEqual(3, axis.SampledColumns);
        Assert.AreEqual(3, presentation.LineageFrameCount);
        CollectionAssert.AreEqual(
            new[] { (0, 10), (11, 30), (42, 918), (961, axis.Width - 961) },
            axis.Gaps.Select(static gap => (gap.FirstColumn, gap.ColumnCount)).ToArray());
        Assert.AreEqual(axis.StartUtc, axis.Gaps[0].StartUtc);
        Assert.AreEqual(axis.StartUtc.AddMinutes(10), axis.Gaps[0].EndUtc);
        Assert.AreEqual(axis.EndUtc, axis.Gaps[^1].EndUtc);
        Assert.IsEmpty(axis.Frames);
        Assert.IsTrue(presentation.Algorithms.Any(static algorithm => algorithm.Name == "planned-time-axis"));
        Assert.IsEmpty(presentation.OtherOutputs);
    }

    [TestMethod]
    public async Task SegmentKeogram_RecomputesItsActualAxisFromItsOrderedSources()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddMinutes(10)),
            NightlyProductFixture.Frame(2, NightlyProductFixture.DayStartUtc.AddMinutes(12)),
            NightlyProductFixture.Frame(3, NightlyProductFixture.DayStartUtc.AddMinutes(41)),
            NightlyProductFixture.Frame(4, NightlyProductFixture.DayStartUtc.AddHours(16)));
        var options = NightlyProductFixture.Options(maximumSegmentSources: 3);
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, options);
        using var harness = Harness(reader, options);
        await harness.Run(occurrence);
        var day = await harness.Store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
        var segment = day.Windows.Single(static record => record.Status.Scope == NightlyProductScope.Segment)
            .CurrentProducts.Single(static product => product.PartOrdinal == 0);

        var presentation = await harness.Store.GetPresentationAsync(segment.ProductId, CancellationToken.None);

        var axis = presentation!.TimeAxis;
        Assert.IsNotNull(axis);
        Assert.IsFalse(axis.Planned);
        Assert.AreEqual(segment.Width, axis.Width);
        Assert.AreEqual(3, axis.SampledColumns);
        Assert.AreEqual(3, presentation.LineageFrameCount);
        CollectionAssert.AreEqual(
            new[] { NightlyProductFixture.DayStartUtc.AddMinutes(10), NightlyProductFixture.DayStartUtc.AddMinutes(12),
                NightlyProductFixture.DayStartUtc.AddMinutes(41) },
            axis.Frames.Select(static frame => frame.ObservationStartedUtc).ToArray());
        Assert.AreEqual(0, axis.Frames[0].Column);
        Assert.AreEqual(axis.Width - 1, axis.Frames[^1].Column);
        var gap = axis.Gaps.Single();
        Assert.AreEqual(axis.Frames[1].Column + 1, gap.FirstColumn);
        Assert.AreEqual(axis.Frames[2].Column, gap.FirstColumn + gap.ColumnCount);
    }

    [TestMethod]
    public async Task FinalStarTrail_HasNoAxisAndCountsItsWholeLineageThroughRollups()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightlyProductFixture.Frame(1, new(2026, 10, 2, 4, 0, 0, TimeSpan.Zero)),
            NightlyProductFixture.Frame(2, new(2026, 10, 2, 5, 0, 0, TimeSpan.Zero)),
            NightlyProductFixture.Frame(3, new(2026, 10, 2, 6, 0, 0, TimeSpan.Zero)));
        var options = NightlyProductFixture.Options(maximumSegmentSources: 2);
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail, options);
        using var harness = Harness(reader, options);
        await harness.Run(occurrence);

        var presentation = await harness.Store.GetPresentationAsync(
            await FinalId(harness.Store, occurrence), CancellationToken.None);

        Assert.IsNotNull(presentation);
        Assert.IsNull(presentation.TimeAxis);
        Assert.AreEqual(3, presentation.LineageFrameCount);
        Assert.IsNotEmpty(presentation.Algorithms);
        Assert.IsNull(await harness.Store.GetPresentationAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [TestMethod]
    public async Task SameSpanFromAnotherDefinition_IsListedAsAnotherOutputOfTheWindow()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16)));
        using var harness = Harness(reader, NightlyProductFixture.Options());
        var alpha = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, definitionId: "alpha");
        var beta = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, definitionId: "beta", version: 2);
        await harness.Run(alpha);
        await harness.Run(beta);
        var first = await FinalId(harness.Store, alpha);
        var second = await FinalId(harness.Store, beta);

        var presentation = await harness.Store.GetPresentationAsync(first, CancellationToken.None);

        Assert.AreEqual(second, presentation!.OtherOutputs.Single().ProductId);
        Assert.AreEqual(first, (await harness.Store.GetPresentationAsync(second, CancellationToken.None))!
            .OtherOutputs.Single().ProductId);
    }

    [TestMethod]
    public async Task Day_JoinsEveryRecordedEvaluationToItsCurrentProductsInPartOrder()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightlyProductFixture.Frame(1, new(2026, 10, 2, 4, 0, 0, TimeSpan.Zero)),
            NightlyProductFixture.Frame(2, new(2026, 10, 2, 5, 0, 0, TimeSpan.Zero)),
            NightlyProductFixture.Frame(3, new(2026, 10, 2, 6, 0, 0, TimeSpan.Zero)));
        var options = NightlyProductFixture.Options(maximumSegmentSources: 2);
        using var harness = Harness(reader, options);
        var keogram = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, options);
        var starTrail = NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail, options);
        await harness.Run(keogram);
        await harness.Run(starTrail);

        var day = await harness.Store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);

        Assert.AreEqual(NightlyProductFixture.ObservingDate, day.ObservingDate);
        CollectionAssert.AreEqual(
            (await harness.Store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).ToArray(),
            day.Products.ToArray());
        Assert.HasCount(4, day.Windows);
        foreach (var record in day.Windows)
        {
            var state = await harness.Store.ReadWindowAsync(record.Status.Kind, record.Status.Scope,
                record.Status.WindowStartUtc, CancellationToken.None, record.Status.Occurrence.IdentitySha256);
            CollectionAssert.AreEqual(state!.ProductIds.ToArray(),
                record.CurrentProducts.Select(static product => product.ProductId).ToArray());
            Assert.AreEqual(LocalAutomationSourceWindowKind.SunriseDay, record.WindowKind);
            Assert.AreEqual(record.Status.Scope == NightlyProductScope.Final, record.FinalProduct is not null);
        }
        var trail = day.Windows.Single(static record =>
            record.Status is { Kind: NightlyProductKind.StarTrail, Scope: NightlyProductScope.Final });
        Assert.AreEqual(NightlyProductScope.Final, trail.CurrentProducts[^1].Scope);
        Assert.IsTrue(trail.CurrentProducts.SkipLast(1).All(static product => product.Scope == NightlyProductScope.Rollup));
        Assert.IsEmpty((await harness.Store.GetDayAsync(NightlyProductFixture.ObservingDate.AddDays(1),
            CancellationToken.None)).Windows);
    }

    [TestMethod]
    public async Task DateSummary_PrefersAProducedEvaluationAndCountsEachHourOnce()
    {
        var reader = new InMemoryNightlySourceReader();
        var produced = new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero);
        var empty = new DateTimeOffset(2026, 10, 2, 5, 0, 0, TimeSpan.Zero);
        reader.Add(NightlyProductFixture.Frame(1, produced.AddMinutes(5)));
        using var harness = Harness(reader, NightlyProductFixture.Options());
        var withoutSources = harness.Generator(new InMemoryNightlySourceReader());
        LocalAutomationOccurrence Daily(string id) =>
            NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, definitionId: id);
        LocalAutomationOccurrence Hourly(string id, DateTimeOffset start) => NightlyProductFixture.Occurrence(
            NightlyProductKind.Keogram, windowKind: LocalAutomationSourceWindowKind.CompletedCivilHour, hourStart: start,
            definitionId: id);
        await harness.Run(Daily("alpha"));
        await harness.Run(Hourly("alpha", produced));
        await harness.Run(Hourly("alpha", empty));
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(1);
        await withoutSources.RunAsync(Daily("beta"), CancellationToken.None);
        await withoutSources.RunAsync(Hourly("beta", produced), CancellationToken.None);
        await withoutSources.RunAsync(Hourly("beta", empty), CancellationToken.None);
        var daily = await FinalId(harness.Store, Daily("alpha"));

        var summaries = await harness.Store.SummarizeDatesAsync(
            NightlyProductFixture.ObservingDate.AddDays(-1), NightlyProductFixture.ObservingDate.AddDays(1), CancellationToken.None);

        var summary = summaries.Single();
        Assert.AreEqual(NightlyProductFixture.ObservingDate, summary.ObservingDate);
        Assert.AreEqual(NightlyProductKind.Keogram, summary.Kind);
        Assert.AreEqual(daily, summary.DailyProductId);
        Assert.AreEqual(NightlyProductWindowDisposition.Produced, summary.DailyDisposition);
        Assert.AreEqual(1, summary.HourlyProduced);
        Assert.AreEqual(1, summary.HourlyWithoutProduct);
    }

    [TestMethod]
    public async Task DateSummary_ReportsTheLatestEvaluationWhenNoneProducedAndRejectsUnboundedRanges()
    {
        using var harness = Harness(new InMemoryNightlySourceReader(), NightlyProductFixture.Options());
        await harness.Run(NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail, definitionId: "alpha"));
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(1);
        await harness.Run(NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail, definitionId: "beta"));
        var date = NightlyProductFixture.ObservingDate;

        var summary = (await harness.Store.SummarizeDatesAsync(date, date, CancellationToken.None)).Single();

        Assert.IsNull(summary.DailyProductId);
        Assert.AreEqual(NightlyProductWindowDisposition.NoSources, summary.DailyDisposition);
        Assert.AreEqual(0, summary.HourlyProduced + summary.HourlyWithoutProduct);
        Assert.IsEmpty(await harness.Store.SummarizeDatesAsync(date.AddDays(1), date.AddDays(61), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await harness.Store.SummarizeDatesAsync(date, date.AddDays(62), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await harness.Store.SummarizeDatesAsync(date, date.AddDays(-1), CancellationToken.None));
    }

    [TestMethod]
    public void PlannedGaps_MirrorTheComposerBinsAndRejectFramesOutsideThePlannedPeriod()
    {
        var start = new DateTimeOffset(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);
        var axis = new PlannedKeogramAxis(start, start.AddMinutes(4).AddSeconds(30), TimeSpan.FromMinutes(1));
        var width = axis.Width(4096);
        Assert.AreEqual(5, width);

        var gaps = SqliteNightlyProductStore.PlannedGaps(axis, width, [start.AddSeconds(59), start.AddMinutes(1), start.AddMinutes(3)]);

        CollectionAssert.AreEqual(new[] { (2, 1), (4, 1) },
            gaps.Select(static gap => (gap.FirstColumn, gap.ColumnCount)).ToArray());
        Assert.AreEqual(axis.EndUtc, gaps[^1].EndUtc);
        Assert.IsEmpty(SqliteNightlyProductStore.PlannedGaps(axis, width,
            [start, start.AddMinutes(1), start.AddMinutes(2), start.AddMinutes(3), start.AddMinutes(4)]));
        Assert.ThrowsExactly<InvalidDataException>(() => SqliteNightlyProductStore.PlannedGaps(axis, width, [axis.EndUtc]));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            SqliteNightlyProductStore.PlannedGaps(axis, width, [start.AddTicks(-1)]));
    }

    private static async Task<Guid> FinalId(SqliteNightlyProductStore store, LocalAutomationOccurrence occurrence)
    {
        Assert.IsTrue(NightlyProductPreset.TryParseTarget(occurrence.Definition.TaskTarget, out var kind));
        var state = await store.ReadWindowAsync(kind, NightlyProductScope.Final, occurrence.SourceWindow!.StartUtc,
            CancellationToken.None, occurrence.IdentitySha256);
        Assert.IsNotNull(state);
        Assert.AreEqual(NightlyProductWindowDisposition.Produced, state.Status.Disposition);
        return state.ProductIds[^1];
    }

    private HarnessState Harness(InMemoryNightlySourceReader reader, NightlyProductOptions options)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-nightly-projection-tests");
        _roots.Add(root);
        var host = NightlyProductFixture.HostOptions(root, options);
        var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
        return new(host, clock, new SqliteNightlyProductStore(host, clock), reader);
    }

    private sealed class HarnessState : IDisposable
    {
        private readonly Microsoft.Extensions.Options.IOptions<CameraAgentHostOptions> _host;
        private readonly List<NightlyProductGenerator> _generators = [];

        internal HarnessState(Microsoft.Extensions.Options.IOptions<CameraAgentHostOptions> host, NightlyClock clock,
            SqliteNightlyProductStore store, InMemoryNightlySourceReader reader)
        {
            _host = host;
            Clock = clock;
            Store = store;
            Generator(reader);
        }

        internal NightlyClock Clock { get; }

        internal SqliteNightlyProductStore Store { get; }

        internal NightlyProductGenerator Generator(InMemoryNightlySourceReader reader)
        {
            var generator = new NightlyProductGenerator(_host, new FixedConfigurationAccessor(NightlyProductFixture.Configuration()),
                reader, Store, new AstronomyEnginePlanetEphemeris(), static () => null, Clock);
            _generators.Add(generator);
            return generator;
        }

        internal async Task Run(LocalAutomationOccurrence occurrence)
        {
            var report = await _generators[0].RunAsync(occurrence, CancellationToken.None);
            Assert.AreEqual(0, report.FailedWindows, report.Describe());
        }

        public void Dispose()
        {
            foreach (var generator in _generators) generator.Dispose();
            Store.Dispose();
        }
    }
}
