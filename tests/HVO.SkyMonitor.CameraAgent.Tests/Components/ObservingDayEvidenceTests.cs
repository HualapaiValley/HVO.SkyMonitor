using System.Globalization;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ObservingDayEvidenceTests
{
    private static readonly ObservingDay Day = ObservingDayCalendar.Create("America/Phoenix").Resolve(new DateOnly(2026, 7, 21));

    [TestMethod]
    public void CaptureBinsRejectOutsideInstantsClipTheEndAndPreserveGaps()
    {
        var segments = ObservingDayTimeline.CaptureSegments(Day,
        [
            Day.EndUtc, Day.StartUtc.AddTicks(-1), Day.StartUtc,
            Day.StartUtc.AddSeconds(30), Day.StartUtc.AddMinutes(4), Day.EndUtc.AddSeconds(-10)
        ]);

        Assert.HasCount(3, segments);
        Assert.AreEqual(new ObservingDayTimeline.CaptureSegment(Day.StartUtc, Day.StartUtc.AddSeconds(90), 2), segments[0]);
        Assert.AreEqual(new ObservingDayTimeline.CaptureSegment(Day.StartUtc.AddMinutes(4), Day.StartUtc.AddMinutes(5), 1), segments[1]);
        Assert.AreEqual(new ObservingDayTimeline.CaptureSegment(Day.EndUtc.AddSeconds(-10), Day.EndUtc, 1), segments[2]);
    }

    [TestMethod]
    public void SparseBandsKeepTheirMathematicalWidthRatherThanAMinimumNightFraction()
    {
        using var context = new BunitContext();
        var cut = context.Render<ObservingDayTimeline>(parameters => parameters
            .Add(component => component.Day, Day)
            .Add(component => component.ExposureInstantsUtc, [Day.StartUtc, Day.EndUtc.AddSeconds(-10)]));

        var bands = cut.FindAll(".timeline-bar--captures");
        Assert.HasCount(2, bands);
        Assert.AreEqual("left: 0%; width: 0.069444%", bands[0].GetAttribute("style"));
        Assert.AreEqual("left: 99.988426%; width: 0.011574%", bands[1].GetAttribute("style"));
        StringAssert.Contains(cut.Markup, "not exposure duration", StringComparison.Ordinal);
    }

    [TestMethod]
    public void EmptyEvidenceKeepsEveryLaneWithoutClaimingClearOrUsableSky()
    {
        using var context = new BunitContext();
        var cut = context.Render<ObservingDayTimeline>(parameters => parameters.Add(component => component.Day, Day));

        Assert.HasCount(5, cut.FindAll(".timeline-track"));
        Assert.HasCount(2, cut.FindAll(".timeline-track--unavailable"));
        Assert.HasCount(5, cut.FindAll(".timeline-track__empty"));
        Assert.IsEmpty(cut.FindAll(".timeline-bar, .timeline-marker"));
        StringAssert.Contains(cut.Markup, "Schedule unavailable", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "No retained exposures", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "No candidate records", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AClosedScheduleIsDistinctFromAnUnavailableSchedule()
    {
        using var context = new BunitContext();
        var cut = context.Render<ObservingDayTimeline>(parameters => parameters
            .Add(component => component.Day, Day).Add(component => component.ScheduleWindows, []));

        Assert.AreEqual("No schedule window", cut.FindAll(".timeline-track__empty")[0].TextContent);
    }

    [TestMethod]
    public void CandidateMarkersRespectTheHalfOpenPeriodAndLinkToTheirExactRecord()
    {
        using var context = new BunitContext();
        var start = Candidate(Day.StartUtc);
        var end = Candidate(Day.EndUtc);
        var before = Candidate(Day.StartUtc.AddTicks(-1));
        var cut = context.Render<ObservingDayTimeline>(parameters => parameters
            .Add(component => component.Day, Day).Add(component => component.Candidates, [before, start, end]));

        var marker = cut.Find(".timeline-marker");
        Assert.HasCount(1, cut.FindAll(".timeline-marker"));
        Assert.AreEqual($"/transients/{start.CandidateId:D}", marker.GetAttribute("href"));
        Assert.AreEqual("left: 0%", marker.GetAttribute("style"));
        Assert.AreEqual("Candidate recorded 12:00", marker.TextContent);
        StringAssert.Contains(cut.Markup, "not verified observation times", StringComparison.Ordinal);
    }

    [TestMethod]
    public void TruncatedCandidateEvidenceHasAnExplicitRangeLink()
    {
        using var context = new BunitContext();
        var cut = context.Render<ObservingDayTimeline>(parameters => parameters
            .Add(component => component.Day, Day).Add(component => component.Candidates, [Candidate(Day.StartUtc)])
            .Add(component => component.CandidatesTruncated, true).Add(component => component.ActiveRevisionCaveat, true));

        StringAssert.Contains(cut.Markup, "Partial candidate timeline: only the first 1 retained records", StringComparison.Ordinal);
        Assert.AreEqual(ArchiveCalendarPage.CandidatesUrl(Day), cut.Find(".night-timeline__note a").GetAttribute("href"));
        StringAssert.Contains(cut.Markup, "not a historical admission record", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(2026, 3, 7, 23)]
    [DataRow(2026, 10, 31, 25)]
    public void DstPeriodsUseElapsedTimeAndDiscloseBothOffsets(int year, int month, int date, int hours)
    {
        using var context = new BunitContext();
        var day = ObservingDayCalendar.Create("America/Denver").Resolve(new DateOnly(year, month, date));
        var cut = context.Render<ObservingDayTimeline>(parameters => parameters.Add(component => component.Day, day)
            .Add(component => component.ExposureInstantsUtc, [day.StartUtc.AddHours(1)]));

        Assert.AreEqual(hours, day.Duration.TotalHours);
        var expected = 100d / hours;
        StringAssert.StartsWith(cut.Find(".timeline-bar--captures").GetAttribute("style")!,
            "left: " + expected.ToString("0.######", CultureInfo.InvariantCulture) + "%", StringComparison.Ordinal);
        var ticks = cut.FindAll(".timeline-hours span");
        Assert.HasCount(7, ticks);
        Assert.IsTrue(ticks.Any(tick => tick.TextContent.Contains("-06:00", StringComparison.Ordinal)));
        Assert.IsTrue(ticks.Any(tick => tick.TextContent.Contains("-07:00", StringComparison.Ordinal)));
        Assert.AreEqual("left: 100%", ticks[^1].GetAttribute("style"));
    }

    [TestMethod]
    public void InvalidConfiguredZoneUsesTheCalendarsUtcFallback()
    {
        using var context = new BunitContext();
        var day = ObservingDayCalendar.Create("Unknown/Zone").Resolve(Day.Date);
        var cut = context.Render<ObservingDayTimeline>(parameters => parameters.Add(component => component.Day, day));

        Assert.IsTrue(day.TimeZoneFallback);
        StringAssert.Contains(cut.Find(".night-timeline__range").TextContent, "(UTC)", StringComparison.Ordinal);
    }

    [TestMethod]
    public void FailedRepresentativePreviewKeepsItsCaptureLinkAndNeverSubstitutesAnotherImage()
    {
        using var context = new BunitContext();
        RetainedPreviewImageTestSupport.Configure(context);
        var capture = Guid.NewGuid();
        var exposure = Day.StartUtc.AddHours(1);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Day.EndUtc));
        context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(new TestNightlyProductUiService());
        context.Services.AddSingleton<ICameraAgentObservingDayUiService>(new DayService(new(
            new(Day, 1, 0, exposure, exposure, capture, exposure), [exposure], TimeSpan.FromSeconds(10),
            null, [], false, [], capture, null, null)));
        var cut = context.Render<ObservingDayPage>(parameters => parameters.Add(page => page.DateText, "2026-07-21"));
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll(".day-hero__image img")));

        cut.Find(".day-hero__image img").TriggerEvent("onerror", EventArgs.Empty);

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll(".day-hero__image img"));
            Assert.AreEqual($"/gallery/{capture:D}", cut.Find(".day-hero__image a").GetAttribute("href"));
            StringAssert.Contains(cut.Find(".day-hero__empty").TextContent, "No other image has been substituted", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".day-hero__caption").TextContent, "2026-07-21 13:00:00", StringComparison.Ordinal);
        });
    }

    private static CameraAgentTransientOperatorCandidate Candidate(DateTimeOffset created) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Extracted", "None", "CausalRetained", created, created,
        "Retained", "Pending", "Pending", "Pending");

    private sealed class DayService(CameraAgentObservingDayView view) : ICameraAgentObservingDayUiService
    {
        public ValueTask<OperatorUiResult<CameraAgentObservingDayView>> GetAsync(DateOnly observingDate, CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CameraAgentObservingDayView>.Success(view));
    }
}
