using HVO.SkyMonitor.Astronomy;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class NightlyProductDetailPageTests
{
    private static readonly TimeSpan Mst = TimeSpan.FromHours(-7);

    // Two days after the fixture's report date, so its sunrise period has closed.
    private static readonly DateTimeOffset Closed = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AxisTicks_AcrossDstFold_DistinguishBothLocalHoursWithoutMovingColumns()
    {
        var start = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var axis = new NightlyProductTimeAxis(true, start, start.AddMinutes(125), 126, 60, 126, [], []);
        var ticks = NightlyProductDetailPage.AxisTicks(axis, instant => TimeZoneInfo.ConvertTime(instant, zone));
        Assert.IsTrue(ticks.Any(tick => tick.Label == "01:00 -04:00"));
        Assert.IsTrue(ticks.Any(tick => tick.Label == "01:00 -05:00"));
        Assert.AreEqual(0, ticks.First(tick => tick.Label == "01:00 -04:00").Percent, 1e-9);
        Assert.AreEqual(100.0 * 60 / 126, ticks.First(tick => tick.Label == "01:00 -05:00").Percent, 1e-9);
    }

    [TestMethod]
    public void AxisTicks_PlannedAxisLabelsRoundLocalTimesAtTheirExactPositions()
    {
        var start = new DateTimeOffset(2026, 10, 1, 13, 35, 0, TimeSpan.Zero);
        var axis = new NightlyProductTimeAxis(true, start, start.AddMinutes(95), 48, 120, 48, [], []);

        var ticks = NightlyProductDetailPage.AxisTicks(axis, utc => utc.ToOffset(Mst));

        Assert.AreEqual("06:45 07:00 07:15 07:30 07:45 08:00", string.Join(' ', ticks.Select(static tick => tick.Label)));
        // The last of 48 two-minute bins is partial, so the drawn width spans 96 minutes rather than the 95-minute window.
        Assert.AreEqual(100.0 * 10 / 96, ticks[0].Percent, 1e-9);
        Assert.AreEqual(100.0 * 85 / 96, ticks[^1].Percent, 1e-9);
    }

    [TestMethod]
    public void AxisTicks_ActualAxisLabelsOnlyFrameColumns()
    {
        var start = new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero);
        KeogramSegmentFrameV1[] frames =
        [
            new(0, start), new(1, start.AddMinutes(2)), new(2, start.AddMinutes(4)),
            new(5, start.AddMinutes(30)), new(6, start.AddMinutes(32))
        ];
        var axis = new NightlyProductTimeAxis(false, start, start.AddMinutes(32), 7, 120, 5,
            [new KeogramGap(start.AddMinutes(4), start.AddMinutes(30), 3, 2)], frames);

        var ticks = NightlyProductDetailPage.AxisTicks(axis, utc => utc.ToOffset(Mst));

        // Nothing is labelled inside the gap: time is not linear across gap columns.
        Assert.AreEqual("21:00 21:02 21:04 21:30 21:32", string.Join(' ', ticks.Select(static tick => tick.Label)));
        CollectionAssert.AreEqual(frames.Select(static frame => 100.0 * (frame.Column + 0.5) / 7).ToArray(),
            ticks.Select(static tick => tick.Percent).ToArray());
    }

    [TestMethod]
    public void NightlyDetail_PlannedKeogramShowsItsAxisGapsAndDownloadsWithoutRegeneration()
    {
        using var context = new BunitContext();
        var nightly = Configure(context, Closed);
        var occurrence = NightlyDayFixture.Daily(NightlyProductKind.Keogram);
        var window = occurrence.SourceWindow!;
        var width = (int)Math.Ceiling((window.EndUtc - window.StartUtc).TotalMinutes);
        var gap = new KeogramGap(window.StartUtc.AddMinutes(60), window.StartUtc.AddMinutes(90), 60, 30);
        var presentation = Presentation(occurrence, NightlyProductKind.Keogram,
            new NightlyProductTimeAxis(true, window.StartUtc, window.EndUtc, width, 60, width - 30, [gap], []));
        var id = presentation.Detail.Summary.ProductId;
        nightly.PresentationHandler = _ => OperatorUiResult<NightlyProductPresentation>.Success(presentation);

        var cut = context.Render<NightlyProductDetailPage>(parameters => parameters.Add(page => page.ProductId, id));

        cut.WaitForAssertion(() => Assert.AreEqual("Nightly keogram", cut.Find("h1").TextContent));
        CollectionAssert.AreEqual(new[] { id }, nightly.Presentations);
        var strip = cut.Find(".keogram-axis");
        Assert.AreEqual("planned", strip.GetAttribute("data-axis"));
        StringAssert.Contains(strip.GetAttribute("aria-label"), "1 gap.", StringComparison.Ordinal);
        // The period spans two local dates, so the span names both.
        StringAssert.Contains(strip.GetAttribute("aria-label"), "from " +
            window.StartUtc.ToOffset(Mst).ToString("HH:mm dd MMM", System.Globalization.CultureInfo.InvariantCulture) + " to " +
            window.EndUtc.ToOffset(Mst).ToString("HH:mm dd MMM", System.Globalization.CultureInfo.InvariantCulture) + ";", StringComparison.Ordinal);
        var drawn = cut.Find(".keogram-axis__gap");
        StringAssert.StartsWith(drawn.GetAttribute("style"), FormattableString.Invariant($"left: {6000.0 / width:0.###}%;"), StringComparison.Ordinal);
        var labels = cut.FindAll(".keogram-axis__tick").Select(static tick => tick.TextContent).ToList();
        Assert.IsTrue(labels.Count is >= 4 and <= NightlyProductDetailPage.MaximumTicks, string.Join(' ', labels));
        Assert.IsTrue(labels.TrueForAll(static label => label.EndsWith(":00", StringComparison.Ordinal)), string.Join(' ', labels));
        StringAssert.Contains(cut.Find(".gap-list li").TextContent, "(30 columns)", StringComparison.Ordinal);

        Assert.AreEqual(NightlyProductLinks.PreviewDownload(id), cut.Find("a[download][href*='/preview']").GetAttribute("href"));
        Assert.AreEqual(NightlyProductLinks.ProvenanceDownload(id), cut.Find("a[download][href*='/provenance']").GetAttribute("href"));
        Assert.AreEqual("/archive/day/2026-10-01?calendar=" + SunriseReportingPeriod.CurrentVersion,
            cut.Find(".detail-heading-actions a").GetAttribute("href"));
        Assert.AreEqual("Nightly", cut.Find("[data-nightly-fact='scope']").TextContent);
        Assert.AreEqual("Current", cut.Find("[data-nightly-fact='current']").TextContent);
        StringAssert.Contains(cut.Find(".product-provenance").TextContent, "Final; the sunrise period has closed.", StringComparison.Ordinal);
        // No regeneration control of any kind.
        Assert.IsEmpty(cut.FindAll("button"));
        Assert.IsFalse(cut.Markup.Contains("Regenerate", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void NightlyDetail_SegmentShowsFrameColumnsBoundedSourcesAndOtherOutputsWithoutSuccession()
    {
        using var context = new BunitContext();
        var nightly = Configure(context, Closed);
        var occurrence = NightlyDayFixture.Daily(NightlyProductKind.Keogram);
        var start = occurrence.SourceWindow!.StartUtc.AddHours(8);
        var frames = Enumerable.Range(0, 75).Select(index => new KeogramSegmentFrameV1(index, start.AddMinutes(2 * index))).ToArray();
        var axis = new NightlyProductTimeAxis(false, start, frames[^1].ObservationStartedUtc, 75, 120, 75, [], frames);
        var current = NightlyDayFixture.Product(occurrence, NightlyProductKind.Keogram) with { Scope = NightlyProductScope.Segment };
        var older = current with { ProductId = Guid.NewGuid(), IsCurrent = false, CreatedUtc = current.CreatedUtc.AddHours(-1) };
        var presentation = Presentation(occurrence, NightlyProductKind.Keogram, axis, sources: 75,
            scope: NightlyProductScope.Segment, isCurrent: false, others: [current, older]);
        // Daylight exposures sum to milliseconds; the total must not round to zero.
        presentation = presentation with
        {
            Detail = presentation.Detail with { Summary = presentation.Detail.Summary with { TotalIntegration = TimeSpan.FromMilliseconds(32.5) } }
        };
        nightly.PresentationHandler = _ => OperatorUiResult<NightlyProductPresentation>.Success(presentation);

        var cut = context.Render<NightlyProductDetailPage>(parameters => parameters.Add(page => page.ProductId, presentation.Detail.Summary.ProductId));

        cut.WaitForAssertion(() => Assert.AreEqual("Keogram segment part 1", cut.Find("h1").TextContent));
        Assert.AreEqual("actual", cut.Find(".keogram-axis").GetAttribute("data-axis"));
        var summary = cut.Find(".axis-summary").TextContent;
        StringAssert.Contains(summary, "75 frames from " + start.ToOffset(Mst).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " to " +
            frames[^1].ObservationStartedUtc.ToOffset(Mst).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "; 0 rendered gaps.", StringComparison.Ordinal);
        Assert.DoesNotContain("not linear", summary, StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".product-provenance").TextContent, "Total integration32.5 ms", StringComparison.Ordinal);
        var ticks = cut.FindAll(".keogram-axis__tick");
        Assert.HasCount(NightlyProductDetailPage.MaximumTicks, ticks);
        StringAssert.StartsWith(ticks[0].GetAttribute("style"), "left: 0.667%", StringComparison.Ordinal);
        Assert.AreEqual("Segment", cut.Find("[data-nightly-fact='scope']").TextContent);
        Assert.AreEqual("Not current", cut.Find("[data-nightly-fact='current']").TextContent);
        Assert.AreEqual(
            "Not current; another output of this window and part is current, listed below. No succession between them is recorded.",
            cut.Find("[data-nightly-fact='currency']").TextContent);

        var sources = cut.FindAll(".source-list li");
        Assert.HasCount(NightlyProductDetailPage.MaximumShownSources, sources);
        var firstCapture = presentation.Detail.Sources[0].CaptureId!.Value;
        Assert.AreEqual($"/gallery/{firstCapture:D}", sources[0].QuerySelector("a")!.GetAttribute("href"));
        StringAssert.Contains(cut.Find("[data-nightly-notice='sources']").TextContent, "Showing the first 60 of 75 sources", StringComparison.Ordinal);

        var outputs = cut.FindAll("[data-nightly-section='outputs'] li a");
        Assert.AreEqual("Current output|Not current output", string.Join('|', outputs.Select(static link => link.TextContent)));
        Assert.AreEqual(NightlyProductLinks.Detail(current.ProductId), outputs[0].GetAttribute("href"));
        var outputsText = cut.Find("[data-nightly-section='outputs']").TextContent;
        Assert.IsFalse(outputsText.Contains("Earlier", StringComparison.Ordinal) || outputsText.Contains("Later", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NightlyDetail_NotCurrentWithoutACurrentOtherOutputClaimsNoReplacement()
    {
        using var context = new BunitContext();
        var nightly = Configure(context, Closed);
        var occurrence = NightlyDayFixture.Daily(NightlyProductKind.StarTrail);
        var older = NightlyDayFixture.Product(occurrence, NightlyProductKind.StarTrail) with { IsCurrent = false };
        // A later no-source or rejected evaluation, or a publication whose evaluation is not yet recorded, leaves no
        // current output at all; a non-current sibling is not one either.
        var presentation = Presentation(occurrence, NightlyProductKind.StarTrail, isCurrent: false, others: [older]);
        nightly.PresentationHandler = _ => OperatorUiResult<NightlyProductPresentation>.Success(presentation);

        var cut = context.Render<NightlyProductDetailPage>(parameters => parameters.Add(page => page.ProductId, presentation.Detail.Summary.ProductId));

        cut.WaitForAssertion(() => Assert.AreEqual("Not current", cut.Find("[data-nightly-fact='current']").TextContent));
        const string Unnamed = "Not current; no recorded evaluation names it, and no other output of this window and part is current.";
        Assert.AreEqual(Unnamed, cut.Find("[data-nightly-fact='currency']").TextContent);
        Assert.DoesNotContain("is current, listed below", cut.Markup, StringComparison.Ordinal);
        Assert.AreEqual("Not current output", cut.Find("[data-nightly-section='outputs'] li a").TextContent);
        Assert.AreEqual(Unnamed, NightlyProductDetailPage.Currency(presentation with { OtherOutputs = [] }));
        Assert.AreEqual("Current; a recorded evaluation names it as its product of this window and part.",
            NightlyProductDetailPage.Currency(Presentation(occurrence, NightlyProductKind.StarTrail, others: [older])));
    }

    [TestMethod]
    public void NightlyDetail_HourlyStarTrailInAnOpenPeriodHasNoAxisAndLinksItsParts()
    {
        using var context = new BunitContext();
        var nightly = Configure(context, NightlyDayFixture.FirstHourUtc);
        var occurrence = NightlyDayFixture.Hour(NightlyProductKind.StarTrail, NightlyProductFixture.DayStartUtc);
        var presentation = Presentation(occurrence, NightlyProductKind.StarTrail, sources: 2,
            sourceKind: NightlyProductSourceKind.NightlyProduct);
        nightly.PresentationHandler = _ => OperatorUiResult<NightlyProductPresentation>.Success(presentation);

        var cut = context.Render<NightlyProductDetailPage>(parameters => parameters.Add(page => page.ProductId, presentation.Detail.Summary.ProductId));

        cut.WaitForAssertion(() => Assert.AreEqual("Hourly star trail", cut.Find("h1").TextContent));
        Assert.AreEqual("Partial hour", cut.Find("[data-nightly-fact='partial']").TextContent);
        Assert.IsEmpty(cut.FindAll(".keogram-axis"));
        Assert.IsEmpty(cut.FindAll("[data-nightly-section='axis']"));
        var facts = cut.Find(".product-provenance").TextContent;
        StringAssert.Contains(facts, "the sunrise period is still open", StringComparison.Ordinal);
        StringAssert.Contains(facts, "Dark-night frames only", StringComparison.Ordinal);
        var part = cut.Find(".source-list li");
        Assert.AreEqual(NightlyProductLinks.Detail(presentation.Detail.Sources[0].ArtifactId), part.QuerySelector("a")!.GetAttribute("href"));
        StringAssert.Contains(part.TextContent, "First frame", StringComparison.Ordinal);
    }

    [TestMethod]
    public void NightlyDetail_KeogramWithoutARecordedAxisDrawsNoMarkers()
    {
        using var context = new BunitContext();
        var nightly = Configure(context, Closed);
        var presentation = Presentation(NightlyDayFixture.Daily(NightlyProductKind.Keogram), NightlyProductKind.Keogram);
        nightly.PresentationHandler = _ => OperatorUiResult<NightlyProductPresentation>.Success(presentation);

        var cut = context.Render<NightlyProductDetailPage>(parameters => parameters.Add(page => page.ProductId, presentation.Detail.Summary.ProductId));

        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find("[data-nightly-notice='no-axis']")));
        Assert.IsEmpty(cut.FindAll(".keogram-axis"));
        Assert.IsEmpty(cut.FindAll(".keogram-axis__tick"));
    }

    [TestMethod]
    public void NightlyDetail_FailedPreviewIsStatedWithoutASubstitute()
    {
        using var context = new BunitContext();
        var nightly = Configure(context, Closed, previewFailed: true);
        var occurrence = NightlyDayFixture.Daily(NightlyProductKind.Keogram);
        var window = occurrence.SourceWindow!;
        var width = (int)Math.Ceiling((window.EndUtc - window.StartUtc).TotalMinutes);
        var presentation = Presentation(occurrence, NightlyProductKind.Keogram,
            new NightlyProductTimeAxis(true, window.StartUtc, window.EndUtc, width, 60, width, [], []));
        nightly.PresentationHandler = _ => OperatorUiResult<NightlyProductPresentation>.Success(presentation);

        var cut = context.Render<NightlyProductDetailPage>(parameters => parameters.Add(page => page.ProductId, presentation.Detail.Summary.ProductId));

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".media-unavailable").TextContent, "Preview unavailable", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll("img"));
        Assert.IsEmpty(cut.FindAll(".keogram-axis"));
        StringAssert.Contains(cut.Find("[data-nightly-section='axis']").TextContent, "Planned axis", StringComparison.Ordinal);
    }

    [TestMethod]
    public void NightlyDetail_MissingUnavailableAndUnauthorizedReadsAreStated()
    {
        using var context = new BunitContext();
        var nightly = Configure(context, Closed);
        var missing = context.Render<NightlyProductDetailPage>(parameters => parameters.Add(page => page.ProductId, Guid.NewGuid()));
        missing.WaitForAssertion(() => StringAssert.Contains(missing.Markup, "The requested nightly product was not found.", StringComparison.Ordinal));
        Assert.IsEmpty(missing.FindAll("button"));
        Assert.AreEqual("/archive/calendar?calendar=" + SunriseReportingPeriod.CurrentVersion, missing.Find("a").GetAttribute("href"));

        nightly.PresentationHandler = static _ => OperatorUiResult<NightlyProductPresentation>.Failure(
            OperatorUiResultKind.Unavailable, "Nightly products are temporarily unavailable.");
        nightly.Presentations.Clear();
        var unavailable = context.Render<NightlyProductDetailPage>(parameters => parameters.Add(page => page.ProductId, Guid.NewGuid()));
        unavailable.WaitForAssertion(() => StringAssert.Contains(unavailable.Markup, "Nightly products are temporarily unavailable.", StringComparison.Ordinal));
        unavailable.Find("button").Click();
        unavailable.WaitForAssertion(() => Assert.HasCount(2, nightly.Presentations));

        nightly.PresentationHandler = static _ => OperatorUiResult<NightlyProductPresentation>.Failure(
            OperatorUiResultKind.Unauthorized, "Local operator access is required.");
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var denied = context.Render<NightlyProductDetailPage>(parameters => parameters.Add(page => page.ProductId, Guid.NewGuid()));
        denied.WaitForAssertion(() => StringAssert.EndsWith(navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal));
    }

    private static TestNightlyProductUiService Configure(BunitContext context, DateTimeOffset now, bool previewFailed = false)
    {
        RetainedPreviewImageTestSupport.Configure(context, previewFailed);
        var nightly = new TestNightlyProductUiService();
        context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(nightly);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
        return nightly;
    }

    private static NightlyProductPresentation Presentation(
        LocalAutomationOccurrence occurrence,
        NightlyProductKind kind,
        NightlyProductTimeAxis? axis = null,
        int sources = 3,
        NightlyProductScope scope = NightlyProductScope.Final,
        bool isCurrent = true,
        NightlyProductSourceKind sourceKind = NightlyProductSourceKind.PreviewFrame,
        IReadOnlyList<NightlyProductSummary>? others = null)
    {
        var summary = NightlyDayFixture.Product(occurrence, kind, sources) with { Scope = scope, IsCurrent = isCurrent };
        var lineage = Enumerable.Range(0, sources).Select(index => new NightlyProductSource(index, sourceKind, Guid.NewGuid(),
            new string('E', 64), sourceKind == NightlyProductSourceKind.PreviewFrame ? Guid.NewGuid() : null,
            summary.FirstObservationUtc.AddMinutes(2 * index))).ToArray();
        var detail = new NightlyProductDetail(summary, new string('1', 64), new string('2', 64),
            "nightly-" + NightlyProductLinks.KindNoun(kind), "preview", new string('3', 64), new string('4', 64), 1_228_800,
            new string('5', 64), 48_213, new string('6', 64), lineage)
        { Occurrence = occurrence };
        return new NightlyProductPresentation(detail, [new ProcessingAlgorithmIdentity("nightly-composer", "1.0.0")],
            sources, axis, others ?? []);
    }
}
