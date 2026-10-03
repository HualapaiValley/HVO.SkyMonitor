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
public sealed class NightlyProductGeneratorTests
{
    private readonly List<string> _roots = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var root in _roots.Where(Directory.Exists)) Directory.Delete(root, true);
    }

    [TestMethod]
    public async Task FullDayKeogram_KeepsLeadingInternalTrailingMissingBinsAndActualSampleColumns()
    {
        var reader = new InMemoryNightlySourceReader();
        var frames = new[] { NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddMinutes(10)),
            NightlyProductFixture.Frame(2, NightlyProductFixture.DayStartUtc.AddMinutes(41)),
            NightlyProductFixture.Frame(3, NightlyProductFixture.DayStartUtc.AddHours(16)) };
        reader.Add(frames);
        var options = NightlyProductFixture.Options(maximumSegmentSources: 2);
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, options);
        using var harness = Harness(reader, options);
        var report = await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        Assert.AreEqual(0, report.FailedWindows, report.Describe());
        var final = await Final(harness.Store, occurrence);
        var stored = await harness.Store.ReadStoredProductAsync(final.Summary.ProductId, CancellationToken.None);
        var duration = occurrence.SourceWindow!.EndUtc - occurrence.SourceWindow.StartUtc;
        Assert.AreEqual((int)Math.Ceiling(duration.TotalMinutes), stored.Layout.Width);
        Assert.AreEqual(occurrence, final.Occurrence);
        Assert.AreEqual(occurrence.SourceWindow.StartUtc, final.Summary.WindowStartUtc);
        Assert.AreEqual(occurrence.SourceWindow.EndUtc, final.Summary.WindowEndUtc);
        Assert.AreEqual(NightlyProductFixture.ObservingDate, final.Summary.ObservingDate);
        var path = MeridianSamplePath.Create(RigProjectionContextFactory.Create(NightlyProductFixture.Rig), stored.Layout.Height)
            .Select(static sample => sample.Pixel).ToArray();
        foreach (var frame in frames)
        {
            var sample = KeogramComposer.Compose([new(NightlyProductFixture.Size, NightlyProductFixture.Size,
                NightlyProductFixture.Size, frame.Artifact.Layout!.PixelFormat, frame.Artifact.Payload,
                frame.Candidate.ExposureStartedUtc)], new(path));
            var column = (int)((frame.Candidate.ExposureStartedUtc - occurrence.SourceWindow.StartUtc).Ticks / TimeSpan.TicksPerMinute);
            for (var row = 0; row < stored.Layout.Height; row++)
                Assert.AreEqual(sample.PixelData.Span[row], stored.Payload.Span[row * stored.Layout.Width + column]);
        }
        foreach (var column in new[] { 0, 11, 25, 40, stored.Layout.Width - 1 })
            for (var row = 0; row < stored.Layout.Height; row++)
                Assert.AreEqual((byte)((column + row) % 8 < 4 ? 32 : 96), stored.Payload.Span[row * stored.Layout.Width + column]);
    }

    [TestMethod]
    public async Task StarTrail_UsesOnlyActualDarkNightSourcesAndEqualsTheirDirectLighten()
    {
        var reader = new InMemoryNightlySourceReader();
        var night = new[] { NightlyProductFixture.Frame(1, new(2026, 10, 2, 4, 0, 0, TimeSpan.Zero)),
            NightlyProductFixture.Frame(2, new(2026, 10, 2, 5, 0, 0, TimeSpan.Zero)),
            NightlyProductFixture.Frame(3, new(2026, 10, 2, 6, 0, 0, TimeSpan.Zero)) };
        reader.Add([.. night, NightlyProductFixture.Frame(4, NightlyProductFixture.DayStartUtc.AddHours(4))]);
        var options = NightlyProductFixture.Options(maximumSegmentSources: 2);
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail, options);
        using var harness = Harness(reader, options);
        var report = await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        Assert.AreEqual(0, report.FailedWindows, report.Describe());
        var final = await Final(harness.Store, occurrence);
        var stored = await harness.Store.ReadStoredProductAsync(final.Summary.ProductId, CancellationToken.None);
        for (var index = 0; index < stored.Payload.Length; index++)
            Assert.AreEqual(night.Max(frame => frame.Artifact.Payload.Span[index]), stored.Payload.Span[index]);
        var statuses = await harness.Store.ListWindowsAsync(NightlyProductFixture.ObservingDate, CancellationToken.None);
        Assert.AreEqual(1, statuses.Single(static item => item.Scope == NightlyProductScope.Segment)
            .Exclusions[NightlyProductContract.ExcludedSolarAltitudeReasonCode]);
    }

    [TestMethod]
    public async Task ExactWindow_IsHalfOpenAndRetainsSubMillisecondSourceFacts()
    {
        var reader = new InMemoryNightlySourceReader();
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram);
        var window = occurrence.SourceWindow!;
        reader.Add(NightlyProductFixture.Frame(1, window.StartUtc.AddTicks(-1)),
            NightlyProductFixture.Frame(2, window.StartUtc), NightlyProductFixture.Frame(3, window.EndUtc.AddTicks(-1)),
            NightlyProductFixture.Frame(4, window.EndUtc));
        using var harness = Harness(reader, NightlyProductFixture.Options());
        await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        var final = await Final(harness.Store, occurrence);
        Assert.AreEqual(window.StartUtc, final.Summary.FirstObservationUtc);
        Assert.AreEqual(window.EndUtc.AddTicks(-1), final.Summary.LastObservationUtc);
        Assert.AreEqual(window.StartUtc, final.Summary.WindowStartUtc);
        Assert.AreEqual(window.EndUtc, final.Summary.WindowEndUtc);
        Assert.AreEqual(2, (await harness.Store.ListWindowsAsync(final.Summary.ObservingDate, CancellationToken.None))
            .Single(static status => status.Scope == NightlyProductScope.Segment).AdmittedCount);
    }

    [TestMethod]
    public async Task Retry_ReusesPublishedOutputsAndDoesNotRestoreUnchangedSources()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16)));
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail);
        using var harness = Harness(reader, NightlyProductFixture.Options());
        var first = await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        var restores = reader.Restores;
        var second = await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        Assert.IsGreaterThan(0, first.ProductsPublished);
        Assert.AreEqual(0, second.ProductsPublished);
        Assert.AreEqual(restores, reader.Restores);
        Assert.AreEqual(0, second.FailedWindows);
    }

    [TestMethod]
    public async Task DefinitionsAndRevisions_ForTheSameSpanRetainIndependentProductsAndPointers()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16)));
        using var harness = Harness(reader, NightlyProductFixture.Options());
        var a = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, definitionId: "alpha");
        var b = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, definitionId: "beta", version: 2);
        await harness.Generator.RunAsync(a, CancellationToken.None);
        await harness.Generator.RunAsync(b, CancellationToken.None);
        var first = await Final(harness.Store, a);
        var second = await Final(harness.Store, b);
        Assert.AreNotEqual(first.Summary.ProductId, second.Summary.ProductId);
        Assert.AreNotEqual(first.RecipeIdentitySha256, second.RecipeIdentitySha256);
        Assert.IsTrue(first.Summary.IsCurrent);
        Assert.IsTrue(second.Summary.IsCurrent);
        CollectionAssert.AreEqual((await harness.Store.ReadStoredProductAsync(first.Summary.ProductId, CancellationToken.None)).Payload.ToArray(),
            (await harness.Store.ReadStoredProductAsync(second.Summary.ProductId, CancellationToken.None)).Payload.ToArray());
    }

    [TestMethod]
    public async Task SourceRestorationFailure_FailsTheOccurrenceWithoutPublishingAThinnerProduct()
    {
        var reader = new InMemoryNightlySourceReader { RestoreFailure = new InvalidDataException("expired") };
        reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16)));
        using var harness = Harness(reader, NightlyProductFixture.Options());
        var report = await harness.Generator.RunAsync(NightlyProductFixture.Occurrence(NightlyProductKind.Keogram), CancellationToken.None);
        Assert.AreEqual(1, report.FailedWindows);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, NightlyProductAutomationTaskRegistry.Outcome(report));
        Assert.IsEmpty(await harness.Store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None));
    }

    [TestMethod]
    public async Task SourceByteBound_RejectsTheWholeOccurrenceBeforeRestorationAndRemainsFailedOnRetry()
    {
        var reader = new InMemoryNightlySourceReader();
        var frame = NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16));
        reader.Add(frame with { Candidate = frame.Candidate with { PayloadBytes = 256L * 1024 * 1024 + 1 } });
        var options = NightlyProductFixture.Options();
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, options);
        using var harness = Harness(reader, options);

        foreach (var attempt in Enumerable.Range(0, 2))
        {
            var report = await harness.Generator.RunAsync(occurrence, CancellationToken.None);
            Assert.AreEqual(1, report.FailedWindows, $"Attempt {attempt} must remain failed.");
            Assert.AreEqual(LocalAutomationRunOutcome.Failed, NightlyProductAutomationTaskRegistry.Outcome(report));
        }
        Assert.AreEqual(0, reader.Restores);
        Assert.IsEmpty(await harness.Store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None));
        var status = await harness.Store.ReadWindowAsync(NightlyProductKind.Keogram, NightlyProductScope.Final,
            occurrence.SourceWindow!.StartUtc, CancellationToken.None, occurrence.IdentitySha256);
        Assert.AreEqual(NightlyProductWindowDisposition.Rejected, status!.Status.Disposition);
        Assert.AreEqual("nightly.source-byte-bound", status.Status.ReasonCode);
    }

    [TestMethod]
    [DataRow(1, 4L)]
    [DataRow(32, 200L * 1024 * 1024)]
    public async Task ImpossibleStarTrailRollup_RejectsBeforePublishingParts(int countBound, long frameBytes)
    {
        var reader = new InMemoryNightlySourceReader();
        var time = new DateTimeOffset(2026, 10, 2, 5, 0, 0, TimeSpan.Zero);
        foreach (var index in Enumerable.Range(1, 2))
        {
            var frame = NightlyProductFixture.Frame(index, time.AddMinutes(index));
            reader.Add(frame with { Candidate = frame.Candidate with { PayloadBytes = frameBytes } });
        }
        var options = NightlyProductFixture.Options(maximumSegmentSources: countBound);
        using var harness = Harness(reader, options);

        var report = await harness.Generator.RunAsync(NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail, options), CancellationToken.None);

        Assert.AreEqual(1, report.FailedWindows);
        Assert.AreEqual(0, reader.Restores);
        Assert.IsEmpty(await harness.Store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None));
    }

    [TestMethod]
    public async Task ExecutionBudget_RejectsBeforeRestoringOrPublishingPartialProducts()
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16)));
        var options = NightlyProductFixture.Options(maximumSegmentsPerRun: 1);
        using var harness = Harness(reader, options);
        var report = await harness.Generator.RunAsync(NightlyProductFixture.Occurrence(NightlyProductKind.Keogram, options), CancellationToken.None);
        Assert.AreEqual(1, report.FailedWindows);
        Assert.AreEqual(0, reader.Restores);
        Assert.AreEqual(0, report.ProductsPublished);
        Assert.AreEqual(NightlyProductContract.ExecutionBoundReasonCode,
            (await harness.Store.ListWindowsAsync(NightlyProductFixture.ObservingDate, CancellationToken.None))
                .Single(static item => item.Scope == NightlyProductScope.Segment).ReasonCode);
    }

    [TestMethod]
    [DataRow("*.bin")]
    [DataRow("*.jpg")]
    [DataRow("*.provenance.json")]
    public async Task CorruptedPublishedFile_FailsAReuseWithoutRestoringOrRepublishingSources(string pattern)
    {
        var reader = new InMemoryNightlySourceReader();
        reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16)));
        using var harness = Harness(reader, NightlyProductFixture.Options());
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram);
        await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        var restores = reader.Restores;
        var file = Directory.GetFiles(Path.Combine(_roots[^1], SqliteNightlyProductStore.ProductDirectoryName), pattern,
            SearchOption.AllDirectories)[0];
        var bytes = await File.ReadAllBytesAsync(file); bytes[^1] ^= 1; await File.WriteAllBytesAsync(file, bytes);
        var retry = await harness.Generator.RunAsync(occurrence, CancellationToken.None);
        Assert.AreEqual(1, retry.FailedWindows);
        Assert.AreEqual(0, retry.ProductsPublished);
        Assert.AreEqual(restores, reader.Restores);
    }

    [TestMethod]
    public async Task ChangedPresetOrUnsettledWindow_CannotSubstituteCurrentSettingsOrPublishEarly()
    {
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram);
        var changed = new NightlyProductOptions
        {
            Enabled = true,
            SourceNodeId = "changed",
            SourceRecipeIdentitySha256 = NightlyProductFixture.PreviewRecipe,
            RigProfileSha256 = NightlyProductFixture.RigProfileSha256
        };
        using var first = Harness(new(), changed);
        Assert.IsFalse((await first.Generator.RunAsync(occurrence, CancellationToken.None)).Ready);
        using var early = Harness(new(), NightlyProductFixture.Options(), occurrence.SourceWindow!.EarliestFinalUtc.AddTicks(-1));
        Assert.IsFalse((await early.Generator.RunAsync(occurrence, CancellationToken.None)).Ready);
    }

    private static async Task<NightlyProductDetail> Final(SqliteNightlyProductStore store, LocalAutomationOccurrence occurrence)
    {
        Assert.IsTrue(NightlyProductPreset.TryParseTarget(occurrence.Definition.TaskTarget, out var kind));
        var state = await store.ReadWindowAsync(kind, NightlyProductScope.Final, occurrence.SourceWindow!.StartUtc,
            CancellationToken.None, occurrence.IdentitySha256);
        Assert.IsNotNull(state);
        Assert.AreEqual(NightlyProductWindowDisposition.Produced, state.Status.Disposition);
        var details = new List<NightlyProductDetail>();
        foreach (var id in state.ProductIds) details.Add((await store.GetAsync(id, CancellationToken.None))!);
        return details.Single(static detail => detail.Summary.Scope == NightlyProductScope.Final);
    }

    private HarnessState Harness(InMemoryNightlySourceReader reader, NightlyProductOptions options, DateTimeOffset? now = null)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-still-window-tests");
        _roots.Add(root);
        var host = NightlyProductFixture.HostOptions(root, options);
        var clock = new NightlyClock(now ?? NightlyProductFixture.DayEndUtc.AddMinutes(10));
        var store = new SqliteNightlyProductStore(host, clock);
        var generator = new NightlyProductGenerator(host, new FixedConfigurationAccessor(NightlyProductFixture.Configuration()),
            reader, store, new AstronomyEnginePlanetEphemeris(), static () => null, clock);
        return new(store, generator);
    }

    private sealed record HarnessState(SqliteNightlyProductStore Store, NightlyProductGenerator Generator) : IDisposable
    {
        public void Dispose() { Generator.Dispose(); Store.Dispose(); }
    }
}
