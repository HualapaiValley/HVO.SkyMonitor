using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
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
        var service = new TestNightlyProductUiService { VideoHandler = _ => OperatorUiResult<CameraAgentTimeLapsePresentation>.Success(new(product, available)) };
        context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(service);
        var cut = context.Render<TimeLapseDetailPage>(parameters => parameters.Add(page => page.ProductId, product.ProductId));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "partial capture coverage", StringComparison.Ordinal));
        Assert.AreEqual(available ? 1 : 0, cut.FindAll("video").Count);
        Assert.AreEqual(TimeLapseLinks.Provenance(product.ProductId), cut.Find("a[href$='/provenance']").GetAttribute("href"));
        StringAssert.Contains(cut.Markup, "1920", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "timelapse.solar-excluded", StringComparison.Ordinal);
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
        context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(service);
        var occurrence = NightlyDayFixture.Daily(NightlyProductKind.Keogram);
        var window = occurrence.SourceWindow!;
        var preset = new TimeLapseOptions().Freeze(NightlyProductFixture.Configuration());
        var failed = new CameraAgentTimeLapseJob(Guid.NewGuid(), occurrence, window, null, preset,
            CameraAgentTimeLapseState.Failed, window.EndUtc, "timelapse.timeout", null) { Revision = 7 };
        var empty = failed with { JobId = Guid.NewGuid(), ParentJobId = failed.JobId, State = CameraAgentTimeLapseState.NoSources, ReasonCode = "empty" };
        var cut = context.Render<TimeLapseDayProducts>(parameters => parameters
            .Add(page => page.Day, new CameraAgentTimeLapseDay(window.ReportingPeriod.ReportDate, [failed, empty], []))
            .Add(page => page.Enabled, true));
        StringAssert.Contains(cut.Markup, "Generation failed", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "No admitted images", StringComparison.Ordinal);
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
        var cut = context.Render<TimeLapseLibraryView>();
        cut.WaitForAssertion(() => Assert.HasCount(31, cut.FindAll("li")));
        Assert.AreEqual(TimeLapseLinks.Detail(service.VideoSummaries[0].ProductId), cut.Find("li a").GetAttribute("href"));
        Assert.AreEqual("/archive/products?videoBefore=2026-09-05", cut.Find("a[href*='videoBefore']").GetAttribute("href"));
        StringAssert.Contains(cut.Markup, "Final with capture gaps", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("video"));
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
