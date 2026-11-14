using System.Globalization;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
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
        // The band at the end of the period is anchored to that end; its width is still the mathematical one.
        Assert.AreEqual("right: 0%; width: 0.011574%", bands[1].GetAttribute("style"));
        StringAssert.Contains(cut.Markup, "not exposure duration", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ALoneCaptureKeepsItsWidthAndAVisibleFloorThatCannotLeaveThePeriod()
    {
        using var context = new BunitContext();
        var noon = Day.StartUtc + (Day.Duration / 2);
        // One retained exposure in a whole day, at each end, either side of the middle, and exactly in the middle.
        foreach (var (instant, expected) in new[]
                 {
                     (Day.StartUtc, "left: 0%; width: 0.069444%"),
                     (noon.AddMinutes(-1), "left: 49.930556%; width: 0.069444%"),
                     (noon, "right: 49.930556%; width: 0.069444%"),
                     (Day.EndUtc.AddMinutes(-1), "right: 0%; width: 0.069444%")
                 })
        {
            var cut = context.Render<ObservingDayTimeline>(parameters => parameters
                .Add(component => component.Day, Day).Add(component => component.ExposureInstantsUtc, [instant]));

            var band = cut.FindAll(".timeline-bar--captures").Single();
            Assert.AreEqual(expected, band.GetAttribute("style"));
            StringAssert.StartsWith(band.GetAttribute("title"), "1 captures ", StringComparison.Ordinal);
            Assert.DoesNotContain("No retained exposures", cut.Markup, StringComparison.Ordinal);
        }

        // A band is anchored to its nearer end, so the floor below can only grow toward the middle of the track.
        var css = File.ReadAllText(Path.Combine(CameraAgentStateCompatibilityLabelTestsRoot(),
            "src", "HVO.SkyMonitor.CameraAgent", "Components", "Pages", "ObservingDayTimeline.razor.css"));
        var rule = css.Split('\n').Single(static line => line.StartsWith(".timeline-bar--captures ", StringComparison.Ordinal));
        StringAssert.Contains(rule, "min-width: 2px;", StringComparison.Ordinal);
        // The floor is a fixed pixel size, never a share of the night, and schedule bars keep no floor at all.
        Assert.DoesNotContain("%", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("min-width", css.Split('\n').Single(static line => line.StartsWith(".timeline-bar--schedule ", StringComparison.Ordinal)),
            StringComparison.Ordinal);
    }

    private static string CameraAgentStateCompatibilityLabelTestsRoot()
        => HVO.SkyMonitor.CameraAgent.Tests.Deployment.CameraAgentStateCompatibilityLabelTests.RepositoryRoot();

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
    [DataRow(ObservingDayCalendar.LegacyNoonVersion)]
    [DataRow(SunriseReportingPeriod.CurrentVersion)]
    public void CandidateListLinkResolvesToTheSelectedDayWithAnAppRootBase(string calendarVersion)
    {
        using var context = new BunitContext();
        var calendar = ObservingDayCalendar.ForDeployment(DeploymentLocationSnapshot.Create(
            "fixture", 1, "Fixture site", null, DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 850, "America/Phoenix"));
        var day = calendar.SelectVersion(calendarVersion).Resolve(Day.Date);
        var cut = context.Render<ObservingDayTimeline>(parameters => parameters
            .Add(component => component.Day, day).Add(component => component.Candidates, [Candidate(day.StartUtc)]));

        var href = cut.Find(".night-timeline__note a").GetAttribute("href");
        Assert.IsNotNull(href);
        var destination = new Uri(new Uri("https://cameraagent.test/"), href);
        Assert.AreEqual("/archive/day/2026-07-21", destination.AbsolutePath);
        Assert.AreEqual("?calendar=" + Uri.EscapeDataString(calendarVersion), destination.Query);
        Assert.AreEqual("#day-candidates", destination.Fragment);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void TruncatedCandidateEvidenceNamesTheMostRecentRecordsAndLinksTheRange(int count)
    {
        using var context = new BunitContext();
        var history = Enumerable.Range(0, 5).Select(index => Candidate(Day.StartUtc.AddHours(index))).ToArray();
        var newest = history.Reverse().Take(count).ToArray();
        var cut = context.Render<ObservingDayTimeline>(parameters => parameters
            .Add(component => component.Day, Day).Add(component => component.Candidates, newest)
            .Add(component => component.CandidatesTruncated, true).Add(component => component.ActiveRevisionCaveat, true));

        var note = cut.Find("[role=note]");
        StringAssert.Contains(note.TextContent, count == 1 ? "the most recent record is shown" : "the 3 most recent records are shown", StringComparison.Ordinal);
        StringAssert.Contains(note.TextContent, "earlier records are not plotted", StringComparison.Ordinal);
        Assert.AreEqual(ArchiveCalendarPage.CandidatesUrl(Day), note.QuerySelector("a")!.GetAttribute("href"));
        CollectionAssert.AreEqual(newest.Select(candidate => $"/transients/{candidate.CandidateId:D}").ToArray(),
            cut.FindAll(".timeline-marker").Select(marker => marker.GetAttribute("href")).ToArray());
        Assert.IsFalse(cut.Markup.Contains(history[0].CandidateId.ToString("D"), StringComparison.Ordinal));
        StringAssert.Contains(cut.Markup, "not a historical admission record", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CaptureResolutionLabelsFollowTheCoverageBin()
    {
        using var context = new BunitContext();
        var cut = context.Render<ObservingDayTimeline>(parameters => parameters.Add(component => component.Day, Day));
        var label = FormattableString.Invariant($"{CameraAgentObservingDayUiService.CoverageBin.TotalMinutes:0.######}-minute bins");

        StringAssert.Contains(cut.FindAll(".timeline-track")[1].GetAttribute("aria-label")!, label, StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".timeline-key").TextContent, label, StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".night-timeline__note").TextContent, label, StringComparison.Ordinal);
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
