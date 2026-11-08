using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Components.Presentation;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.Video.FFmpeg;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class GeneratedTimeLapsePagesTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void DetailSeparatesVerifiedPlaybackFromMissingBytesAndKeepsProvenance(bool available)
    {
        using var context = new BunitContext();
        TimeLapsePlayerTestSupport.Configure(context);
        var product = Product();
        var window = NightlyDayFixture.Daily(NightlyProductKind.Keogram).SourceWindow!;
        context.Services.AddSingleton<IObservingDayCalendarProvider>(new CurrentSite());
        var service = new TestNightlyProductUiService { VideoHandler = _ => OperatorUiResult<CameraAgentTimeLapsePresentation>.Success(new(product, available, window)) };
        context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(service);
        var cut = context.Render<TimeLapseDetailPage>(parameters => parameters.Add(page => page.ProductId, product.ProductId));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "partial capture coverage", StringComparison.Ordinal));
        Assert.AreEqual(available ? 1 : 0, cut.FindAll("video").Count);
        Assert.AreEqual(TimeLapseLinks.Provenance(product.ProductId), cut.Find("a[href$='/provenance']").GetAttribute("href"));
        StringAssert.Contains(cut.Markup, "1920", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "timelapse.solar-excluded", StringComparison.Ordinal);
        var periodText = cut.FindAll("dt").Single(node => node.TextContent == "Planned capture period").NextElementSibling!.TextContent;
        StringAssert.Contains(periodText, window.ReportingPeriod.Site.TimeZoneId, StringComparison.Ordinal);
        Assert.IsFalse(periodText.Contains("Asia/Kolkata", StringComparison.Ordinal));
        var generationText = cut.FindAll("dt").Single(node => node.TextContent == "Generation").NextElementSibling!.TextContent;
        StringAssert.Contains(generationText, "+05:30 (Asia/Kolkata)", StringComparison.Ordinal);
        if (available)
        {
            Assert.AreEqual(TimeLapseLinks.Video(product.ProductId), cut.Find("video source").GetAttribute("src"));
            Assert.AreEqual(TimeLapseLinks.Video(product.ProductId), cut.Find("figcaption a[download]").GetAttribute("href"));
        }
        else StringAssert.Contains(cut.Markup, "missing or failed checksum verification", StringComparison.Ordinal);
    }

    [TestMethod]
    public void DayShowsFailedAndEmptyStatesAndRetryKeepsTheDisplayedRevision()
    {
        using var context = new BunitContext();
        var service = new TestNightlyProductUiService();
        context.Services.AddSingleton<IObservingDayCalendarProvider>(new CurrentSite());
        context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(service);
        var occurrence = NightlyDayFixture.Daily(NightlyProductKind.Keogram);
        var window = occurrence.SourceWindow!;
        var preset = new TimeLapseOptions().Freeze(NightlyProductFixture.Configuration());
        var failed = new CameraAgentTimeLapseJob(Guid.NewGuid(), occurrence, window, null, preset,
            CameraAgentTimeLapseState.Failed, window.EndUtc, "timelapse.timeout", null)
        { Revision = 7 };
        var empty = failed with { JobId = Guid.NewGuid(), ParentJobId = failed.JobId, State = CameraAgentTimeLapseState.NoSources, ReasonCode = "empty" };
        var cut = context.Render<TimeLapseDayProducts>(parameters => parameters
            .Add(page => page.Day, new CameraAgentTimeLapseDay(window.ReportingPeriod.ReportDate, [failed, empty], []))
            .Add(page => page.Enabled, true));
        StringAssert.Contains(cut.Markup, "Generation failed", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "No admitted images", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find("small").TextContent, window.ReportingPeriod.Site.TimeZoneId, StringComparison.Ordinal);
        Assert.IsFalse(cut.Find("small").TextContent.Contains("Asia/Kolkata", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll("video"));
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.AreEqual((failed.JobId, 7L), service.VideoRetries.Single()));
        StringAssert.Contains(cut.Markup, "original inputs and settings", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("button"));
    }

    [TestMethod]
    public void LibraryLinksRealDailyProductsAndPagesByReportingDate()
    {
        using var context = new BunitContext();
        var date = new DateOnly(2026, 10, 5);
        var service = new TestNightlyProductUiService
        {
            VideoSummaries = Enumerable.Range(0, 31).Select(index => new CameraAgentTimeLapseSummary(Guid.NewGuid(),
                date.AddDays(-index), 1210, 760, index == 0)).ToArray()
        };
        context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(service);
        var cut = context.Render<TimeLapseLibraryView>(parameters => parameters
            .Add(page => page.Type, "time-lapse").Add(page => page.Status, "produced"));
        cut.WaitForAssertion(() => Assert.HasCount(31, cut.FindAll("li")));
        Assert.AreEqual(TimeLapseLinks.Detail(service.VideoSummaries[0].ProductId), cut.Find("li a").GetAttribute("href"));
        Assert.AreEqual("/archive/products?type=time-lapse&status=produced&videoBefore=2026-09-05", cut.Find("a[href*='videoBefore']").GetAttribute("href"));
        StringAssert.Contains(cut.Markup, "Final with capture gaps", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("video"));
    }

    [TestMethod]
    [DataRow(CameraAgentTimeLapseState.Queued, null, "Queued")]
    [DataRow(CameraAgentTimeLapseState.Queued, "timelapse.recovering", "Retrying (queued)")]
    [DataRow(CameraAgentTimeLapseState.Working, null, "Running")]
    public void LibraryShowsGenerationWithoutAProducedLink(CameraAgentTimeLapseState state, string? reason, string label)
    {
        using var context = new BunitContext();
        var window = NightlyDayFixture.Daily(NightlyProductKind.Keogram).SourceWindow!;
        var service = new TestNightlyProductUiService
        {
            VideoLibrary = new([new(Guid.NewGuid(), window.ReportingPeriod.ReportDate, window, state,
                state == CameraAgentTimeLapseState.Working ? window.EndUtc : null, reason, null, false)], null, null)
        };
        context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(service);
        var cut = context.Render<TimeLapseLibraryView>(parameters => parameters
            .Add(page => page.Type, "time-lapse").Add(page => page.Status, "running"));
        var item = cut.Find("li");
        Assert.AreEqual("running", item.GetAttribute("data-video-state"));
        StringAssert.Contains(item.TextContent, label, StringComparison.Ordinal);
        StringAssert.Contains(item.TextContent, "Source window:", StringComparison.Ordinal);
        StringAssert.Contains(item.TextContent, window.ReportingPeriod.Site.TimeZoneId, StringComparison.Ordinal);
        Assert.AreEqual(ArchiveCalendarPage.DayUrl(window.ReportingPeriod.ReportDate), item.QuerySelector("a")!.GetAttribute("href"));
        Assert.IsFalse(item.TextContent.Contains("Produced", StringComparison.Ordinal));
        service.VideoLibrary = new([service.VideoLibrary!.Entries.Single() with { GenerationEnabled = false }], null, null);
        var disabled = context.Render<TimeLapseLibraryView>(parameters => parameters
            .Add(page => page.Type, "time-lapse").Add(page => page.Status, "stalled"));
        Assert.AreEqual("stalled", disabled.Find("li").GetAttribute("data-video-state"));
        StringAssert.Contains(disabled.Find("li").TextContent, "Generation disabled", StringComparison.Ordinal);
    }

    private sealed class CurrentSite : IObservingDayCalendarProvider
    {
        public ObservingDayCalendar Current => ObservingDayCalendar.Create("Asia/Kolkata");
    }

    private static CameraAgentTimeLapseProduct Product()
    {
        var window = NightlyDayFixture.Daily(NightlyProductKind.Keogram).SourceWindow!;
        var sha = new string('A', 64);
        var media = new TimeLapseMediaProof(1920, 1080, 20_000_000, sha, [new(0, 0, 20_000_000, sha)]);
        var evidence = new TimeLapseEncodingEvidence(FFmpegTimeLapseEncoder.ProfileVersion, TimeLapseEncoderProfile.Software,
            new(true, null, "6.1.1", sha, sha), sha, sha, new Dictionary<int, string> { [0] = sha }, sha, 1024, media);
        return new(Guid.NewGuid(), sha, Guid.NewGuid(), window.ReportingPeriod.ReportDate, sha, sha, window.StartUtc, window.EndUtc,
            true, sha, null, [], [], new Dictionary<string, int> { ["timelapse.solar-excluded"] = 2 }, evidence, window.EndUtc)
        { HasGaps = true };
    }
}
