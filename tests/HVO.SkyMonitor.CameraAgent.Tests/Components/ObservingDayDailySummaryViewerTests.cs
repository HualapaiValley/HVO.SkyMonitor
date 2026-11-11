using System.Globalization;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>
/// The Observing Day viewer names a daily summary's facts as Product Detail names them (Refs #1780, Refs #1713): its
/// count is the previews it admitted out of its candidates, never "segment products", and its duration is the
/// representative preview's exposure, never "Total integration". A star trail or keogram keeps its segment-product and
/// total-integration wording.
/// </summary>
[TestClass]
public sealed class ObservingDayDailySummaryViewerTests
{
    private static readonly int[] ExposureSeconds = [10, 20, 30, 40, 50];

    // The viewer's fact terms in rendered order: a daily summary's, and a star trail's or keogram's.
    private static readonly string[] DailySummaryTerms = ["Admitted frames", "Admitted previews", "Encoded size", "Representative exposure"];
    private static readonly string[] CompositeTerms = ["Admitted frames", "Composed from", "Encoded size", "Total integration"];

    // Wording that belongs to the other kind of viewer.
    private static readonly string[] CompositeWording = ["segment product", "Composed from", "Total integration"];
    private static readonly string[] DailySummaryWording = ["admitted preview", "Admitted previews", "Representative exposure"];

    public Microsoft.VisualStudio.TestTools.UnitTesting.TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Ported from the 2026-11-10 closed-issue audit reproducer, with its defect assertions inverted and its ground-truth
    /// controls unchanged. A real daily summary is published by <see cref="NightlyProductGenerator"/> into a temporary
    /// SQLite store from five admitted previews of 10, 20, 30, 40 and 50 s (sum 150 s), after the star-trail and keogram
    /// finals of the same period, and the page is rendered over the real <see cref="CameraAgentNightlyProductUiService"/>.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task DailySummaryViewer_LabelsAdmittedPreviewsAndRepresentativeExposure()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-observing-day-summary");
        try
        {
            // ---- Fixture: five admitted previews, distinct exposures, spread around the period midpoint. ----
            var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.DailySummary);
            var window = occurrence.SourceWindow!;
            var midpoint = window.StartUtc + (window.EndUtc - window.StartUtc) / 2;
            // Offsets from the period midpoint: two before, one closest, two after, all inside the sunrise period.
            TimeSpan[] offsets =
            [
                TimeSpan.FromMinutes(-180), TimeSpan.FromMinutes(-60), TimeSpan.FromMinutes(7),
                TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(240)
            ];
            var frames = offsets.Select((offset, index) =>
            {
                var frame = NightlyProductFixture.Frame(index + 1, midpoint + offset);
                return frame with { Artifact = frame.Artifact with { Integration = TimeSpan.FromSeconds(ExposureSeconds[index]) } };
            }).ToArray();
            var exposureSum = TimeSpan.FromSeconds(ExposureSeconds.Sum());

            var hostOptions = NightlyProductFixture.HostOptions(root, NightlyProductFixture.Options());
            var clock = new FixedTimeProvider(NightlyProductFixture.DayEndUtc.AddDays(1));
            using var store = new SqliteNightlyProductStore(hostOptions, clock);
            var reader = new InMemoryNightlySourceReader();
            reader.Add(frames);
            using (var generator = new NightlyProductGenerator(
                       hostOptions, new FixedConfigurationAccessor(NightlyProductFixture.Configuration()), reader, store,
                       new HVO.SkyMonitor.Astronomy.AstronomyEnginePlanetEphemeris(), static () => null, clock))
            {
                // The star trail and the keogram of the same period first, so the summary links two final products.
                foreach (var kind in new[] { NightlyProductKind.StarTrail, NightlyProductKind.Keogram, NightlyProductKind.DailySummary })
                {
                    var report = await generator.RunAsync(NightlyProductFixture.Occurrence(kind), CancellationToken.None).ConfigureAwait(false);
                    TestContext.WriteLine($"[generate {kind}] {report.Describe()}");
                    Assert.AreEqual(0, report.FailedWindows, report.Describe());
                }
            }

            // ---- Ground truth, read back from the durable store. ----
            var statuses = await store.ListWindowsAsync(NightlyDayFixture.Date, CancellationToken.None).ConfigureAwait(false);
            var products = await store.ListAsync(NightlyDayFixture.Date, CancellationToken.None).ConfigureAwait(false);
            var summaryStatus = statuses.Single(static status =>
                status.Kind == NightlyProductKind.DailySummary && status.Scope == NightlyProductScope.Final);
            var summaryProduct = products.Single(static product => product.Kind == NightlyProductKind.DailySummary && product.IsCurrent);
            var detail = (await store.GetAsync(summaryProduct.ProductId, CancellationToken.None).ConfigureAwait(false))!;
            var presentation = (await store.GetPresentationAsync(summaryProduct.ProductId, CancellationToken.None).ConfigureAwait(false))!;
            var facts = presentation.DailySummary!;
            var representative = frames.Single(frame => frame.Artifact.ArtifactId == facts.RepresentativeArtifactId);

            var previewSources = detail.Sources.Count(static source => source.SourceKind == NightlyProductSourceKind.PreviewFrame);
            var linkedSources = detail.Sources.Where(static source => source.SourceKind == NightlyProductSourceKind.NightlyProduct).ToArray();
            var linkedScopes = new List<string>();
            foreach (var linked in linkedSources)
            {
                var linkedDetail = (await store.GetAsync(linked.ArtifactId, CancellationToken.None).ConfigureAwait(false))!;
                linkedScopes.Add($"{linkedDetail.Summary.Kind}/{linkedDetail.Summary.Scope}");
            }
            var segmentProductsOfTheDay = products.Count(static product => product.Scope == NightlyProductScope.Segment);
            var segmentProductsInSummaryLineage = linkedScopes.Count(static scope => scope.EndsWith("/Segment", StringComparison.Ordinal));

            TestContext.WriteLine("---- ground truth (durable store) ----");
            TestContext.WriteLine($"admitted previews (status)     : {summaryStatus.AdmittedCount} of {summaryStatus.CandidateCount} candidates");
            TestContext.WriteLine($"lineage                        : {previewSources} preview frames + {linkedSources.Length} nightly products [{string.Join(", ", linkedScopes)}]");
            TestContext.WriteLine($"segment products of the day    : {segmentProductsOfTheDay}");
            TestContext.WriteLine($"representative preview         : exposure {representative.Artifact.Integration.TotalSeconds:0}s, sum {exposureSum.TotalSeconds:0}s");
            TestContext.WriteLine($"product.TotalIntegration       : {summaryProduct.TotalIntegration.TotalSeconds:0}s");

            // The fixture separates the quantities the viewer used to conflate.
            Assert.AreEqual(5, summaryStatus.AdmittedCount, "five previews are admitted");
            Assert.AreEqual(5, summaryStatus.CandidateCount, "every candidate is admitted");
            Assert.AreEqual(5, previewSources);
            Assert.AreEqual(2, linkedSources.Length, "the star-trail and keogram finals are linked");
            Assert.AreEqual(0, segmentProductsInSummaryLineage, "a daily summary composes no segment product");
            Assert.AreNotEqual(summaryStatus.AdmittedCount, segmentProductsOfTheDay, "segment products of the day differ from admitted previews");
            Assert.AreNotEqual(summaryStatus.AdmittedCount, linkedSources.Length);
            Assert.AreEqual(TimeSpan.FromSeconds(30), representative.Artifact.Integration, "the preview nearest the midpoint is the 30 s one");
            Assert.AreEqual(TimeSpan.FromSeconds(150), exposureSum);
            Assert.AreEqual(representative.Artifact.Integration, summaryProduct.TotalIntegration,
                "the product records the representative frame's exposure, not the sum");

            // ---- Render the page as an operator sees it. ----
            using var context = new BunitContext();
            ConfigureViewer(context, clock);
            var authorization = context.AddAuthorization();
            authorization.SetAuthorized("owner");
            authorization.SetPolicies(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
            context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(services => new CameraAgentNightlyProductUiService(
                services.GetRequiredService<AuthenticationStateProvider>(),
                services.GetRequiredService<IAuthorizationService>(), store, hostOptions,
                NullLogger<CameraAgentNightlyProductUiService>.Instance));

            var cut = context.Render<ObservingDayPage>(parameters => parameters.Add(page => page.DateText, "2026-10-01"));
            string tileText = string.Empty;
            cut.WaitForAssertion(() =>
            {
                var tile = cut.Find("[data-product-kind='daily summary']");
                Assert.AreEqual(NightlyProductLinks.Preview(summaryProduct.ProductId), tile.QuerySelector("img")!.GetAttribute("src"));
                tileText = tile.QuerySelector("p")!.TextContent.Trim();
            });
            // The tile already labelled the count correctly, and still does.
            StringAssert.Contains(tileText, "from 5 admitted previews;", StringComparison.Ordinal);

            var viewer = OpenViewer(cut, NightlyProductKind.DailySummary);
            foreach (var fact in viewer.Rows) TestContext.WriteLine($"viewer fact                    : {fact.Key} = {fact.Value}");
            Assert.AreEqual("Daily summary · 1 October 2026", viewer.Title);

            // The count is the admitted previews out of the candidates, with a preview noun and never "segment products".
            Assert.AreEqual(string.Join(" | ", DailySummaryTerms), string.Join(" | ", viewer.Terms));
            Assert.AreEqual(
                string.Create(CultureInfo.InvariantCulture, $"{summaryStatus.AdmittedCount:N0} / {summaryStatus.CandidateCount:N0} candidates"),
                viewer["Admitted previews"]);
            Assert.AreEqual("5 / 5 candidates", viewer["Admitted previews"]);
            Assert.DoesNotContain("segment product", viewer.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Composed from", viewer.Text, StringComparison.Ordinal);

            // The duration is the representative preview's exposure, never presented as a total integration.
            Assert.AreEqual(ObservingDayPage.FormatDuration(representative.Artifact.Integration), viewer["Representative exposure"]);
            Assert.AreEqual("30s", viewer["Representative exposure"]);
            Assert.AreNotEqual(ObservingDayPage.FormatDuration(exposureSum), viewer["Representative exposure"]);
            Assert.DoesNotContain("Total integration", viewer.Text, StringComparison.Ordinal);

            // A star trail of the same render keeps its segment-product and total-integration wording.
            await cut.Find(".large-viewer__close").ClickAsync(new()).ConfigureAwait(false);
            var starTrailStatus = statuses.Single(static status =>
                status.Kind == NightlyProductKind.StarTrail && status.Scope == NightlyProductScope.Final);
            var starTrail = products.Single(static product =>
                product.Kind == NightlyProductKind.StarTrail && product.Scope == NightlyProductScope.Final && product.IsCurrent);
            var trailViewer = OpenViewer(cut, NightlyProductKind.StarTrail);
            Assert.AreEqual(string.Join(" | ", CompositeTerms), string.Join(" | ", trailViewer.Terms));
            Assert.AreEqual(string.Create(CultureInfo.InvariantCulture, $"{starTrailStatus.AdmittedCount:N0} segment products"),
                trailViewer["Composed from"]);
            Assert.AreEqual(ObservingDayPage.FormatDuration(starTrail.TotalIntegration), trailViewer["Total integration"]);
            Assert.DoesNotContain("Admitted previews", trailViewer.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Representative exposure", trailViewer.Text, StringComparison.Ordinal);

            // ---- Product Detail of the same product names the same numbers the same way. ----
            var detailPage = context.Render<NightlyProductDetailPage>(parameters => parameters.Add(page => page.ProductId, summaryProduct.ProductId));
            var provenance = new Dictionary<string, string>(StringComparer.Ordinal);
            detailPage.WaitForAssertion(() =>
            {
                Assert.HasCount(1, detailPage.FindAll("[data-nightly-section='daily-summary']"));
                provenance.Clear();
                foreach (var row in detailPage.FindAll("dl.product-provenance > div"))
                {
                    var term = row.QuerySelector("dt");
                    var value = row.QuerySelector("dd");
                    if (term is not null && value is not null) provenance[term.TextContent.Trim()] = value.TextContent.Trim();
                }
                Assert.IsTrue(provenance.ContainsKey("Representative exposure"));
            });
            Assert.IsFalse(provenance.ContainsKey("Total integration"), "Product Detail does not call it total integration");
            Assert.AreEqual(provenance["Admitted previews"], viewer["Admitted previews"], "both surfaces state the same admitted previews");
            Assert.AreEqual("30 s", provenance["Representative exposure"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The Unit-lane presentation of a produced daily summary (Refs #1713 AC4b) over a fixture read model, with no store:
    /// a complete summary that admitted every candidate, and a partial one whose admitted and candidate counts differ so
    /// their order is evident. Switching the viewer to the star trail of the same day restores that kind's wording.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(false, 5, 5)]
    [DataRow(true, 4, 6)]
    public void DailySummaryViewer_FixtureDayLabelsAdmittedPreviewsAndRepresentativeExposure(bool partial, int admitted, int candidates)
    {
        using var context = new BunitContext();
        ConfigureViewer(context, new FixedTimeProvider(NightlyProductFixture.DayEndUtc.AddDays(1)));
        var summaryOccurrence = NightlyDayFixture.Daily(NightlyProductKind.DailySummary);
        // The fixture summary records a 30 s representative exposure, as the producer does, not one minute per source.
        var summary = NightlyDayFixture.Product(summaryOccurrence, NightlyProductKind.DailySummary, sources: admitted + 2) with
        {
            TotalIntegration = TimeSpan.FromSeconds(30)
        };
        var produced = NightlyDayFixture.Produced(summaryOccurrence, summary, candidates, admitted);
        if (partial)
        {
            produced = produced with { Status = produced.Status with { ReasonCode = NightlyProductContract.PartialSummaryReasonCode } };
        }
        var trailOccurrence = NightlyDayFixture.Daily(NightlyProductKind.StarTrail);
        var trail = NightlyDayFixture.Product(trailOccurrence, NightlyProductKind.StarTrail);
        context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(new TestNightlyProductUiService
        {
            DayHandler = date => OperatorUiResult<CameraAgentNightlyDayView>.Success(new(new NightlyProductDay(date,
            [
                produced,
                NightlyDayFixture.Produced(trailOccurrence, trail, candidates: 12, admitted: 12)
            ], [summary, trail]), null))
        });

        var cut = context.Render<ObservingDayPage>(parameters => parameters.Add(page => page.DateText, "2026-10-01"));
        cut.WaitForAssertion(() =>
        {
            var tile = cut.Find("[data-product-kind='daily summary']");
            Assert.AreEqual(partial ? "partial" : "available", tile.GetAttribute("data-nightly-state"));
            StringAssert.Contains(tile.TextContent, $"from {admitted} admitted previews;", StringComparison.Ordinal);
        });

        var viewer = OpenViewer(cut, NightlyProductKind.DailySummary);
        Assert.AreEqual("Daily summary · 1 October 2026", viewer.Title);
        Assert.AreEqual(partial ? "Partial" : "Produced", viewer.Chip);
        Assert.AreEqual(string.Join(" | ", DailySummaryTerms), string.Join(" | ", viewer.Terms));
        Assert.AreEqual($"{admitted} / {candidates} candidates", viewer["Admitted previews"]);
        Assert.AreEqual("30s", viewer["Representative exposure"]);
        Assert.AreEqual("640 × 480 Mono8", viewer["Encoded size"]);
        foreach (var wrong in CompositeWording)
        {
            Assert.DoesNotContain(wrong, viewer.Text, StringComparison.Ordinal);
        }
        Assert.AreEqual(NightlyProductLinks.Detail(summary.ProductId),
            cut.Find("[data-viewer-kind='daily summary'] .product-viewer__actions a").GetAttribute("href"));

        cut.Find(".large-viewer__close").Click();
        var trailViewer = OpenViewer(cut, NightlyProductKind.StarTrail);
        Assert.AreEqual("Star trail · 1 October 2026", trailViewer.Title);
        Assert.AreEqual(string.Join(" | ", CompositeTerms), string.Join(" | ", trailViewer.Terms));
        Assert.AreEqual("12 segment products", trailViewer["Composed from"]);
        Assert.AreEqual("12m 00s", trailViewer["Total integration"]);
        foreach (var wrong in DailySummaryWording)
        {
            Assert.DoesNotContain(wrong, trailViewer.Text, StringComparison.Ordinal);
        }
    }

    private static void ConfigureViewer(BunitContext context, TimeProvider clock)
    {
        RetainedPreviewImageTestSupport.Configure(context);
        LargeImageViewerTestSupport.Configure(context);
        ProductDetailTestSupport.Configure(context);
        TimeLapsePlayerTestSupport.Configure(context);
        context.Services.AddSingleton(clock);
        context.Services.AddSingleton<IObservingDayCalendarProvider>(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar));
        context.Services.AddSingleton<ICameraAgentObservingDayUiService>(new FixtureDayService());
    }

    private sealed record ViewerFacts(string Title, string Chip, IReadOnlyList<KeyValuePair<string, string>> Rows, string Text)
    {
        internal string[] Terms => [.. Rows.Select(static row => row.Key)];

        internal string this[string term] => Rows.Single(row => row.Key == term).Value;
    }

    // Opens one product's viewer from its tile and reads its fact list in document order.
    private static ViewerFacts OpenViewer(IRenderedComponent<ObservingDayPage> cut, NightlyProductKind kind)
    {
        var noun = NightlyProductLinks.KindNoun(kind);
        cut.Find($"#{ObservingDayPage.TileId(kind)}").Click();
        ViewerFacts? read = null;
        cut.WaitForAssertion(() =>
        {
            var details = cut.Find($"[data-viewer-kind='{noun}']");
            var rows = details.QuerySelectorAll("dl.product-viewer__facts > div")
                .Select(static row => KeyValuePair.Create(row.QuerySelector("dt")!.TextContent.Trim(), row.QuerySelector("dd")!.TextContent.Trim()))
                .ToArray();
            Assert.HasCount(4, rows);
            Assert.HasCount(4, rows.Select(static row => row.Key).Distinct(StringComparer.Ordinal));
            read = new(cut.Find(".large-viewer h2").TextContent, details.QuerySelector(".hvo-chip")!.TextContent.Trim(), rows,
                details.TextContent);
        });
        return read!;
    }

    private sealed class FixtureDayService : ICameraAgentObservingDayUiService
    {
        public ValueTask<OperatorUiResult<CameraAgentObservingDayView>> GetAsync(DateOnly observingDate, CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CameraAgentObservingDayView>.Success(new(
                new CameraAgentGalleryCalendarDay(NightlyProductFixture.Calendar.Resolve(observingDate), 0, 0, null, null),
                [], TimeSpan.Zero, null, [], false, [], null, null, null)));
    }
}
