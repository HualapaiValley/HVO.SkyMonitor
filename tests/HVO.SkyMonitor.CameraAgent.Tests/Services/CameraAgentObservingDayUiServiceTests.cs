using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentObservingDayUiServiceTests
{
    private static readonly DateTimeOffset NightStart = new(2026, 7, 21, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NightEnd = NightStart.AddDays(1);

    [TestMethod]
    [DataRow(11, true)]
    [DataRow(14, true)]
    [DataRow(12, false)]
    [DataRow(13, false)]
    public async Task SunriseRead_AnUnavailableNeighbourDoesNotHideAValidSelectedPeriod(int missingDay, bool available)
    {
        var site = DeploymentLocationSnapshot.Create("test-site", 1, "Fixture site", null,
            DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 850, "Etc/UTC");
        var calendar = ObservingDayCalendar.ForDeployment(site, new MissingSunrise(new(2026, 10, missingDay)));
        var date = new DateOnly(2026, 10, 12);
        var captureIds = new Dictionary<DateOnly, Guid>
        {
            [date.AddDays(-1)] = Guid.NewGuid(),
            [date] = Guid.NewGuid(),
            [date.AddDays(1)] = Guid.NewGuid()
        };
        CameraAgentGalleryCalendarDay Day(DateOnly value)
        {
            var period = calendar.Resolve(value);
            var exposure = period.StartUtc.AddHours(1);
            return new(period, 1, 0, exposure, exposure, captureIds[value], exposure);
        }
        var archive = new Mock<ICameraAgentArchive>(MockBehavior.Strict);
        archive.Setup(value => value.GetObservingDayAsync(date, SunriseReportingPeriod.CurrentVersion,
                CancellationToken.None))
            .Returns(() => ValueTask.FromResult<CameraAgentObservingDayDetail?>(new(Day(date),
                [calendar.Resolve(date).StartUtc.AddHours(1)], TimeSpan.FromSeconds(20))));
        archive.Setup(value => value.GetCalendarAsync(It.IsAny<CameraAgentGalleryCalendarQuery>(),
                CancellationToken.None))
            .Returns((CameraAgentGalleryCalendarQuery query, CancellationToken _) =>
                ValueTask.FromResult(new CameraAgentGalleryCalendar(calendar.TimeZoneId, false,
                    calendar.Range(query.FromDate, query.ToDate).Select(period => Day(period.Date)).ToArray(),
                    calendar.CalendarVersion)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity("test"));
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(value => value.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(principal));
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(value => value.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        var transients = new Mock<ICameraAgentTransientOperatorProjection>();
        transients.Setup(value => value.GetPageAsync(It.IsAny<CameraAgentTransientOperatorQuery>(), CancellationToken.None))
            .ReturnsAsync(new CameraAgentTransientOperatorPage([], null));
        var automations = new Mock<ILocalAutomationStore>();
        automations.Setup(value => value.GetStateAsync(CancellationToken.None)).ReturnsAsync(LocalAutomationOperatorState.Empty);
        // A real, uninitialized coordinator truthfully has no active schedule and touches no persistence/module.
        using var schedule = new CaptureScheduleRuntimeCoordinator(null!, null!, null!, null!, null!, TimeProvider.System);
        var service = new CameraAgentObservingDayUiService(authentication.Object, authorization.Object, archive.Object,
            transients.Object, automations.Object, schedule, NullLogger<CameraAgentObservingDayUiService>.Instance);

        var result = await service.GetAsync(date, SunriseReportingPeriod.CurrentVersion, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(available ? OperatorUiResultKind.Success : OperatorUiResultKind.Unavailable, result.Kind);
        if (available)
        {
            Assert.AreEqual(calendar.Resolve(date), result.Value!.Day.Day);
            Assert.AreEqual(captureIds[date], result.Value.RepresentativeCaptureId);
            Assert.AreEqual(missingDay == 11 ? null : captureIds[date.AddDays(-1)], result.Value.PreviousDayCaptureId);
            Assert.AreEqual(missingDay == 14 ? null : captureIds[date.AddDays(1)], result.Value.NextDayCaptureId);
            archive.Verify(value => value.GetCalendarAsync(It.Is<CameraAgentGalleryCalendarQuery>(query =>
                query.FromDate == query.ToDate && query.CalendarVersion == SunriseReportingPeriod.CurrentVersion),
                CancellationToken.None), Times.Exactly(2));
        }
        else
        {
            Assert.IsNull(result.Value);
            archive.Verify(value => value.GetCalendarAsync(It.IsAny<CameraAgentGalleryCalendarQuery>(),
                CancellationToken.None), Times.Never());
        }
    }

    [TestMethod]
    public void ClipOpenWindows_ClipsToTheNightSubtractsClosedIntervalsAndMerges()
    {
        var preview = Preview(
            Open("w1", NightStart.AddHours(-2), NightStart.AddHours(4)),
            Open("w2", NightStart.AddHours(3), NightStart.AddHours(10)),
            Closed("blackout", NightStart.AddHours(5), NightStart.AddHours(6)),
            Open("w3", NightStart.AddHours(20), NightEnd.AddHours(3)));

        var windows = CameraAgentObservingDayUiService.ClipOpenWindows(preview, NightStart, NightEnd);

        CollectionAssert.AreEqual(
            new[]
            {
                (NightStart, NightStart.AddHours(5)),
                (NightStart.AddHours(6), NightStart.AddHours(10)),
                (NightStart.AddHours(20), NightEnd)
            },
            windows.ToArray());
    }

    [TestMethod]
    public void ClipOpenWindows_KeepsADateExceptionsOwnWindowsWhileClosingItsWeeklyOnes()
    {
        // A closed date-exception day removes the weekly window on that local day but keeps the
        // exception's own window, which the expander emits alongside the whole-day closure; a
        // blackout removes both.
        var localDay = (Start: NightStart.AddHours(-12), End: NightStart.AddHours(12));
        var preview = Preview(
            Open("weekly", NightStart.AddHours(1), NightStart.AddHours(9)),
            new ExpandedScheduleInterval("exception:closed", CaptureScheduleIntervalSource.DateExceptionClosed, ExpandedScheduleDisposition.Closed, localDay.Start, localDay.End, new DateOnly(2026, 7, 21), null),
            new ExpandedScheduleInterval("exception:window", CaptureScheduleIntervalSource.DateExceptionWindow, ExpandedScheduleDisposition.Open, NightStart.AddHours(2), NightStart.AddHours(6), new DateOnly(2026, 7, 21), "night"),
            Closed("blackout", NightStart.AddHours(3), NightStart.AddHours(4)));

        var windows = CameraAgentObservingDayUiService.ClipOpenWindows(preview, NightStart, NightEnd);

        CollectionAssert.AreEqual(
            new[] { (NightStart.AddHours(2), NightStart.AddHours(3)), (NightStart.AddHours(4), NightStart.AddHours(6)) },
            windows.ToArray());

        // The exception window is exempt from its own day's closure only: the next local day's
        // closed exception still closes the part of the window that crosses into it.
        var crossing = Preview(
            new ExpandedScheduleInterval("d1:closed", CaptureScheduleIntervalSource.DateExceptionClosed, ExpandedScheduleDisposition.Closed, localDay.Start, localDay.End, new DateOnly(2026, 7, 21), null),
            new ExpandedScheduleInterval("d1:window", CaptureScheduleIntervalSource.DateExceptionWindow, ExpandedScheduleDisposition.Open, NightStart.AddHours(10), NightStart.AddHours(14), new DateOnly(2026, 7, 21), "night"),
            new ExpandedScheduleInterval("d2:closed", CaptureScheduleIntervalSource.DateExceptionClosed, ExpandedScheduleDisposition.Closed, localDay.End, localDay.End.AddDays(1), new DateOnly(2026, 7, 22), null));
        CollectionAssert.AreEqual(
            new[] { (NightStart.AddHours(10), NightStart.AddHours(12)) },
            CameraAgentObservingDayUiService.ClipOpenWindows(crossing, NightStart, NightEnd).ToArray());
    }

    [TestMethod]
    public void CoveredDuration_UnionsOneMinuteBinsInsideTheWindowsOnly()
    {
        var windows = new[] { (NightStart.AddHours(1), NightStart.AddHours(2)) };
        var exposures = new[]
        {
            // Outside every window.
            NightStart,
            // Two captures 30 s apart share half a bin: 1.5 minutes, not 2.
            NightStart.AddHours(1).AddMinutes(10),
            NightStart.AddHours(1).AddMinutes(10).AddSeconds(30),
            // Straddles the window end: only the inside 30 s count.
            NightStart.AddHours(2).AddSeconds(-30)
        };

        var covered = CameraAgentObservingDayUiService.CoveredDuration(windows, exposures);

        Assert.AreEqual(TimeSpan.FromSeconds(120), covered);
        Assert.AreEqual(TimeSpan.Zero, CameraAgentObservingDayUiService.CoveredDuration([], exposures));
        Assert.AreEqual(TimeSpan.Zero, CameraAgentObservingDayUiService.CoveredDuration(windows, []));
    }

    private static CaptureSchedulePreview Preview(params ExpandedScheduleInterval[] intervals)
        => new("rev", "exp", CaptureScheduleIntervalExpander.AlgorithmVersion, "tz", "solar", NightStart.AddDays(-1), NightEnd.AddDays(1), intervals, []);

    private static ExpandedScheduleInterval Open(string id, DateTimeOffset start, DateTimeOffset end)
        => new(id, CaptureScheduleIntervalSource.WeeklyWindow, ExpandedScheduleDisposition.Open, start, end, null, "night");

    private static ExpandedScheduleInterval Closed(string id, DateTimeOffset start, DateTimeOffset end)
        => new(id, CaptureScheduleIntervalSource.Blackout, ExpandedScheduleDisposition.Closed, start, end, null, null);

    private sealed class MissingSunrise(DateOnly missingDate) : ISolarEventCalculator
    {
        public SolarEventResult Find(SolarEventKind kind, DateTimeOffset startUtc, DateTimeOffset endUtc,
            double latitudeDegrees, double longitudeDegrees, double elevationMeters)
            => new(kind, DateOnly.FromDateTime(startUtc.UtcDateTime) == missingDate ? null : startUtc.AddHours(6),
                "service-no-event-fixture-v1");
    }
}
