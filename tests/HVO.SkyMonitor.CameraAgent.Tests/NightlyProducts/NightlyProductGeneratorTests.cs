using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods do not require context-free continuations.")]
public sealed class NightlyProductGeneratorTests
{
    // Run just after the 2026-10-01 observing day has settled, so that day's segments and night are all due.
    private static readonly DateTimeOffset AfterRollover = NightlyProductFixture.DayEndUtc.AddMinutes(10);

    private static readonly int[] ExpectedPartSizes = [2, 1, 1, 1];
    private static readonly int[] ExpectedPartMinutes = [1, 2, 3, 4, 5];

    private readonly List<string> _roots = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task KeogramNight_IsByteIdenticalToDirectCompositionOfTheAdmittedFrames()
    {
        var reader = new InMemoryNightlySourceReader();
        var night = NightFrames();
        reader.Add([.. night, Daytime(), RigMismatch()]);
        using var harness = Harness(reader, NightlyProductFixture.Options());

        var report = await harness.Generator.RunAsync(NightlyProductKind.Keogram, CancellationToken.None);

        Assert.IsTrue(report.Ready);
        Assert.AreEqual(0, report.PendingWindows);
        Assert.AreEqual(0, report.FailedWindows);
        Assert.AreEqual(1, report.NightsRecorded);
        var nightState = await harness.Store.ReadWindowAsync(
            NightlyProductKind.Keogram, NightlyProductScope.Night, NightlyProductFixture.DayStartUtc, CancellationToken.None);
        Assert.IsNotNull(nightState);
        Assert.AreEqual(NightlyProductWindowDisposition.Produced, nightState.Status.Disposition);
        Assert.HasCount(1, nightState.ProductIds);
        var stored = await harness.Store.ReadStoredProductAsync(nightState.ProductIds[0], CancellationToken.None);

        var direct = await Direct(BuiltInProcessingRecipes.Keogram, KeogramOptions(), night, KeogramAuxiliary());
        CollectionAssert.AreEqual(direct.Payload.ToArray(), stored.Payload.ToArray());
        Assert.AreEqual(direct.Layout!.Width, stored.Layout.Width);
        Assert.AreEqual(direct.Layout.Height, stored.Layout.Height);

        // The night's lineage is its segments in time order, and each segment's lineage is its exact preview frames.
        var detail = stored.Detail;
        Assert.AreEqual(NightlyProductScope.Night, detail.Summary.Scope);
        Assert.IsTrue(detail.Sources.All(static source => source.SourceKind == NightlyProductSourceKind.NightlyProduct));
        var frames = new List<Guid>();
        foreach (var source in detail.Sources)
        {
            var segment = await harness.Store.GetAsync(source.ArtifactId, CancellationToken.None);
            Assert.IsNotNull(segment);
            Assert.AreEqual(source.OutputIdentitySha256, segment.OutputIdentitySha256);
            frames.AddRange(segment.Sources.Select(static frame => frame.ArtifactId));
        }
        CollectionAssert.AreEqual(night.Select(static frame => frame.Artifact.ArtifactId).ToArray(), frames);
        Assert.AreEqual(night[0].Candidate.ExposureStartedUtc, detail.Summary.FirstObservationUtc);
    }

    [TestMethod]
    public async Task KeogramSegments_RecordExclusionsAndEmptyWindowsFromCapturedFacts()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add([.. NightFrames(), Daytime(), RigMismatch()]);
        using var harness = Harness(reader, NightlyProductFixture.Options());

        await harness.Generator.RunAsync(NightlyProductKind.Keogram, CancellationToken.None);

        var windows = await harness.Store.ListWindowsAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
        var segments = windows.Where(static window => window.Scope == NightlyProductScope.Segment).ToArray();
        Assert.HasCount(24, segments);
        var daytime = segments.Single(window => window.WindowStartUtc == Daytime().Candidate.ExposureStartedUtc.AddMinutes(-30));
        Assert.AreEqual(NightlyProductWindowDisposition.NoSources, daytime.Disposition);
        Assert.AreEqual(1, daytime.Exclusions[NightlyProductContract.ExcludedSolarAltitudeReasonCode]);
        var mismatch = segments.Single(window => window.WindowStartUtc <= RigMismatch().Candidate.ExposureStartedUtc &&
                                                 RigMismatch().Candidate.ExposureStartedUtc < window.WindowEndUtc);
        Assert.AreEqual(1, mismatch.Exclusions[NightlyProductContract.ExcludedRigReasonCode]);
        Assert.AreEqual(mismatch.CandidateCount - 1, mismatch.AdmittedCount);
        Assert.AreEqual(
            4,
            segments.Count(static window => window.Disposition == NightlyProductWindowDisposition.Produced));
    }

    [TestMethod]
    public async Task StarTrailNight_ReducedThroughRollups_EqualsDirectLightenOfTheAdmittedFrames()
    {
        var reader = new InMemoryNightlySourceReader();
        var night = NightFrames();
        reader.Add([.. night, Daytime()]);
        using var harness = Harness(reader, NightlyProductFixture.Options(maximumSegmentSources: 2, maximumSegmentsPerRun: 256));

        var report = await harness.Generator.RunAsync(NightlyProductKind.StarTrail, CancellationToken.None);

        Assert.AreEqual(0, report.PendingWindows);
        Assert.AreEqual(1, report.NightsRecorded);
        var nightState = await harness.Store.ReadWindowAsync(
            NightlyProductKind.StarTrail, NightlyProductScope.Night, NightlyProductFixture.DayStartUtc, CancellationToken.None);
        Assert.IsNotNull(nightState);
        Assert.IsGreaterThan(1, nightState.ProductIds.Count, "A fan-in of two must publish rollups before the night.");
        var final = await harness.Store.ReadStoredProductAsync(nightState.ProductIds[^1], CancellationToken.None);
        Assert.AreEqual(NightlyProductScope.Night, final.Detail.Summary.Scope);
        foreach (var rollupId in nightState.ProductIds.Take(nightState.ProductIds.Count - 1))
        {
            var rollup = await harness.Store.GetAsync(rollupId, CancellationToken.None);
            Assert.AreEqual(NightlyProductScope.Rollup, rollup!.Summary.Scope);
            Assert.IsLessThanOrEqualTo(2, rollup.Sources.Count);
        }

        var direct = await Direct(BuiltInProcessingRecipes.StarTrail, StarTrailOptions(), night, null);
        CollectionAssert.AreEqual(direct.Payload.ToArray(), final.Payload.ToArray());
        Assert.AreEqual(night[0].Candidate.ExposureStartedUtc, final.Detail.Summary.FirstObservationUtc);
        Assert.AreEqual(night[^1].Candidate.ExposureStartedUtc, final.Detail.Summary.LastObservationUtc);
        Assert.AreEqual(
            TimeSpan.FromSeconds(60 * night.Count),
            final.Detail.Summary.TotalIntegration);
    }

    [TestMethod]
    public async Task SecondRun_OverUnchangedSources_PublishesNothingAndRestoresNothing()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightFrames().ToArray());
        using var harness = Harness(reader, NightlyProductFixture.Options());
        var first = await harness.Generator.RunAsync(NightlyProductKind.Keogram, CancellationToken.None);
        var restores = reader.Restores;

        var second = await harness.Generator.RunAsync(NightlyProductKind.Keogram, CancellationToken.None);

        Assert.IsGreaterThan(0, first.ProductsPublished);
        Assert.AreEqual(0, second.ProductsPublished);
        Assert.AreEqual(0, second.ProductsReused);
        Assert.AreEqual(restores, reader.Restores);
        Assert.AreEqual(25, second.WindowsUnchanged, "Every segment window and the night are unchanged.");
        Assert.AreEqual(Common.Automation.LocalAutomationRunOutcome.Succeeded, LocalAutomationRunOutcomeOf(second));
    }

    [TestMethod]
    public async Task BudgetedRuns_LeaveWorkPendingAndConvergeOnTheUnbudgetedNight()
    {
        var night = NightFrames();
        var unbudgetedReader = new InMemoryNightlySourceReader();
        unbudgetedReader.Add([.. night]);
        using var unbudgeted = Harness(unbudgetedReader, NightlyProductFixture.Options(maximumSegmentSources: 8));
        await unbudgeted.Generator.RunAsync(NightlyProductKind.Keogram, CancellationToken.None);
        var expected = await unbudgeted.Store.ReadWindowAsync(
            NightlyProductKind.Keogram, NightlyProductScope.Night, NightlyProductFixture.DayStartUtc, CancellationToken.None);

        var reader = new InMemoryNightlySourceReader();
        reader.Add([.. night]);
        using var harness = Harness(reader, NightlyProductFixture.Options(maximumSegmentSources: 8, maximumSegmentsPerRun: 1));
        var first = await harness.Generator.RunAsync(NightlyProductKind.Keogram, CancellationToken.None);
        Assert.AreEqual(1, first.ProductsPublished);
        Assert.IsGreaterThan(0, first.PendingWindows);
        Assert.AreEqual(0, first.NightsRecorded);
        Assert.AreEqual(
            Common.Automation.LocalAutomationRunOutcome.Succeeded,
            LocalAutomationRunOutcomeOf(first),
            "Budgeted pending work is progress, not failure.");

        var runs = 1;
        NightlyProductRunReport report;
        do
        {
            report = await harness.Generator.RunAsync(NightlyProductKind.Keogram, CancellationToken.None);
            Assert.IsLessThanOrEqualTo(1, report.ProductsPublished);
            runs++;
        }
        while (report.PendingWindows > 0 && runs < 64);

        Assert.AreEqual(0, report.PendingWindows);
        var actual = await harness.Store.ReadWindowAsync(
            NightlyProductKind.Keogram, NightlyProductScope.Night, NightlyProductFixture.DayStartUtc, CancellationToken.None);
        Assert.IsNotNull(expected);
        Assert.IsNotNull(actual);
        CollectionAssert.AreEqual(expected.ProductIds.ToArray(), actual.ProductIds.ToArray());
    }

    [TestMethod]
    public async Task RestoreFailure_FailsTheWindowLeavesItUnrecordedAndHoldsTheNight()
    {
        var reader = new InMemoryNightlySourceReader { RestoreFailure = new InvalidDataException("expired") };
        reader.Add(NightFrames().ToArray());
        using var harness = Harness(reader, NightlyProductFixture.Options());

        var report = await harness.Generator.RunAsync(NightlyProductKind.Keogram, CancellationToken.None);

        Assert.AreEqual(4, report.FailedWindows);
        Assert.AreEqual(0, report.NightsRecorded);
        Assert.AreEqual(Common.Automation.LocalAutomationRunOutcome.Failed, LocalAutomationRunOutcomeOf(report));
        var windows = await harness.Store.ListWindowsAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
        Assert.HasCount(20, windows, "Only the windows without sources are recorded.");
        Assert.IsTrue(windows.All(static window => window.Disposition == NightlyProductWindowDisposition.NoSources));
        Assert.IsEmpty(await harness.Store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None));
    }

    [TestMethod]
    public async Task WindowAboveTheCandidateBound_IsRejectedNotTruncated()
    {
        var reader = new InMemoryNightlySourceReader { CandidateOverride = NightlyProductContract.MaximumWindowCandidates + 1 };
        reader.Add(NightlyProductFixture.Frame(1, new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero)));
        using var harness = Harness(reader, NightlyProductFixture.Options());

        await harness.Generator.RunAsync(NightlyProductKind.StarTrail, CancellationToken.None);

        var window = await harness.Store.ReadWindowAsync(
            NightlyProductKind.StarTrail,
            NightlyProductScope.Segment,
            new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        Assert.IsNotNull(window);
        Assert.AreEqual(NightlyProductWindowDisposition.Rejected, window.Status.Disposition);
        Assert.AreEqual(NightlyProductContract.WindowSourceBoundReasonCode, window.Status.ReasonCode);
        Assert.AreEqual(NightlyProductContract.MaximumWindowCandidates + 1, window.Status.CandidateCount);
        Assert.AreEqual(0, reader.Restores);
        Assert.IsEmpty(await harness.Store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None));
    }

    [TestMethod]
    public async Task UnsupportedOutputs_AreCountedAsExclusions()
    {
        var reader = new InMemoryNightlySourceReader { UnsupportedPerWindow = 2 };
        reader.Add(NightlyProductFixture.Frame(1, new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero)));
        using var harness = Harness(reader, NightlyProductFixture.Options());

        await harness.Generator.RunAsync(NightlyProductKind.StarTrail, CancellationToken.None);

        var window = await harness.Store.ReadWindowAsync(
            NightlyProductKind.StarTrail,
            NightlyProductScope.Segment,
            new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        Assert.AreEqual(3, window!.Status.CandidateCount);
        Assert.AreEqual(1, window.Status.AdmittedCount);
        Assert.AreEqual(2, window.Status.Exclusions[NightlyProductContract.ExcludedUnsupportedSourceReasonCode]);
    }

    [TestMethod]
    public async Task RedactedDeploymentLocation_ExcludesFramesWithoutProvenanceInsteadOfAssumingTheObservatory()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightFrames().ToArray());
        using var harness = Harness(reader, NightlyProductFixture.Options(), NightlyProductFixture.Configuration(redactedLocation: true));

        var report = await harness.Generator.RunAsync(NightlyProductKind.StarTrail, CancellationToken.None);

        Assert.AreEqual(0, report.ProductsPublished);
        Assert.AreEqual(1, report.NightsRecorded);
        var windows = await harness.Store.ListWindowsAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
        Assert.AreEqual(
            NightFrames().Count,
            windows.Sum(static window => window.Exclusions.GetValueOrDefault(NightlyProductContract.ExcludedLocationReasonCode)));
        Assert.AreEqual(
            NightlyProductWindowDisposition.NoSources,
            windows.Single(static window => window.Scope == NightlyProductScope.Night).Disposition);
    }

    [TestMethod]
    public async Task DisabledOrUnconfigured_IsNotReadyAndTouchesNothing()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightFrames().ToArray());
        using var disabled = Harness(reader, NightlyProductFixture.Options(enabled: false));
        using var unconfigured = Harness(reader, NightlyProductFixture.Options(), configuration: null, configured: false);

        var disabledReport = await disabled.Generator.RunAsync(NightlyProductKind.Keogram, CancellationToken.None);
        var unconfiguredReport = await unconfigured.Generator.RunAsync(NightlyProductKind.Keogram, CancellationToken.None);

        Assert.IsFalse(disabledReport.Ready);
        Assert.IsFalse(unconfiguredReport.Ready);
        Assert.AreEqual(Common.Automation.LocalAutomationRunOutcome.Skipped, LocalAutomationRunOutcomeOf(disabledReport));
        Assert.AreEqual(0, reader.Restores);
        Assert.IsFalse(string.IsNullOrWhiteSpace(unconfiguredReport.Describe()));
    }

    [TestMethod]
    public async Task UnsettledDay_ComposesSettledSegmentsButNotTheNight()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightFrames().ToArray());
        // 07:03Z: the 04:00-05:00 and 05:00-06:00 windows have settled; 06:00-07:00 has not.
        using var harness = Harness(
            reader, NightlyProductFixture.Options(), now: new DateTimeOffset(2026, 10, 2, 7, 3, 0, TimeSpan.Zero));

        await harness.Generator.RunAsync(NightlyProductKind.Keogram, CancellationToken.None);

        Assert.IsNull(await harness.Store.ReadWindowAsync(
            NightlyProductKind.Keogram, NightlyProductScope.Night, NightlyProductFixture.DayStartUtc, CancellationToken.None));
        var windows = await harness.Store.ListWindowsAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
        Assert.IsTrue(windows.All(static window => window.WindowEndUtc <= new DateTimeOffset(2026, 10, 2, 6, 0, 0, TimeSpan.Zero)));
        Assert.AreEqual(
            2,
            windows.Count(static window => window.Disposition == NightlyProductWindowDisposition.Produced));
    }

    [TestMethod]
    public void Partition_SplitsInRecipeOrderBySizeAndSelector()
    {
        var at = new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero);
        var frames = Enumerable.Range(1, 5).Select(i => NightlyProductFixture.Frame(i, at.AddMinutes(i)).Candidate).ToList();
        frames[3] = frames[3] with { RecipeIdentitySha256 = new string('B', 64) };
        frames.Reverse();

        var parts = NightlyProductGenerator.Partition(frames, 2);

        CollectionAssert.AreEqual(ExpectedPartSizes, parts.Select(static part => part.Count).ToArray());
        CollectionAssert.AreEqual(
            ExpectedPartMinutes,
            parts.SelectMany(static part => part).Select(c => (int)(c.ExposureStartedUtc - at).TotalMinutes).ToArray());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NightlyProductGenerator.Partition(frames, 0));
    }

    /// <summary>
    /// Night frames every four minutes from 04:00Z to 07:56Z on 2026-10-02 (21:00-00:56 MST, Sun below -18 degrees),
    /// with one gap inside a window (05:20-05:52) and one spanning the 07:00 window boundary (06:36-07:24).
    /// </summary>
    private static List<NightlyFrame> NightFrames()
    {
        var start = new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero);
        var frames = new List<NightlyFrame>();
        for (var minute = 0; minute < 240; minute += 4)
        {
            var at = start.AddMinutes(minute);
            if (at.TimeOfDay > new TimeSpan(5, 20, 0) && at.TimeOfDay < new TimeSpan(5, 52, 0) ||
                at.TimeOfDay > new TimeSpan(6, 36, 0) && at.TimeOfDay < new TimeSpan(7, 24, 0))
            {
                continue;
            }
            frames.Add(NightlyProductFixture.Frame(minute + 1, at));
        }
        return frames;
    }

    // 14:30 MST: the Sun is far above every admission limit.
    private static NightlyFrame Daytime() =>
        NightlyProductFixture.Frame(9001, new DateTimeOffset(2026, 10, 1, 21, 30, 0, TimeSpan.Zero));

    private static NightlyFrame RigMismatch() =>
        NightlyProductFixture.Frame(9002, new DateTimeOffset(2026, 10, 2, 4, 30, 30, TimeSpan.Zero), rig: new string('C', 64));

    private static Common.Automation.LocalAutomationRunOutcome LocalAutomationRunOutcomeOf(NightlyProductRunReport report) =>
        NightlyProductAutomationTaskRegistry.Outcome(report);

    private static JsonElement KeogramOptions() =>
        JsonSerializer.SerializeToElement(new KeogramRecipeOptions(300, 8, 4096));

    private static JsonElement StarTrailOptions() =>
        JsonSerializer.SerializeToElement(new StarTrailRecipeOptions(NightlyProductRecipeLimits.MaximumSourceCount));

    private static IReadOnlyList<ProcessingAuxiliaryInput> KeogramAuxiliary() =>
    [
        KeogramGeometryJson.CreateAuxiliaryInput(KeogramGeometryV1.Create(
            RigProjectionContextFactory.Create(NightlyProductFixture.Rig), NightlyProductFixture.RigProfileSha256))
    ];

    private static async Task<ProcessingProduct> Direct(
        string recipe,
        JsonElement options,
        IReadOnlyList<NightlyFrame> frames,
        IReadOnlyList<ProcessingAuxiliaryInput>? auxiliary)
    {
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(
            new ProcessingExecutionRequest(
                recipe,
                options,
                ProcessingInputSelector.RecipeResult(
                    FrameArtifactRole.Preview, NightlyProductFixture.PreviewVariant, NightlyProductFixture.PreviewRecipe),
                [.. frames.Select(static frame => frame.Artifact)],
                "direct",
                null,
                auxiliary),
            CancellationToken.None);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        return outcome.Products[0];
    }

    private GeneratorHarness Harness(
        InMemoryNightlySourceReader reader,
        NightlyProductOptions nightly,
        CameraModuleConfig? configuration = null,
        bool configured = true,
        DateTimeOffset? now = null)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-nightly-generator");
        _roots.Add(root);
        var options = NightlyProductFixture.HostOptions(root, nightly);
        var clock = new NightlyClock(now ?? AfterRollover);
        var store = new SqliteNightlyProductStore(options, clock);
        var generator = new NightlyProductGenerator(
            options,
            new FixedConfigurationAccessor(configured ? configuration ?? NightlyProductFixture.Configuration() : null),
            new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar),
            reader,
            store,
            new AstronomyEnginePlanetEphemeris(),
            static () => null,
            clock);
        return new GeneratorHarness(store, generator);
    }

    private sealed record GeneratorHarness(SqliteNightlyProductStore Store, NightlyProductGenerator Generator) : IDisposable
    {
        public void Dispose()
        {
            Generator.Dispose();
            Store.Dispose();
        }
    }
}
