using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// The observing-day page's read model (#988, prototype <c>day.html</c>): the night's retained
/// facts, its expected schedule window and the coverage achieved against it, its local candidates,
/// and the automation runs scheduled inside it. Every field is a durable fact or an explicit
/// "not available" state; nothing is estimated.
/// </summary>
internal sealed record CameraAgentObservingDayView(
    CameraAgentGalleryCalendarDay Day,
    IReadOnlyList<CameraAgentGalleryCapture> Captures,
    bool CapturesTruncated,
    TimeSpan TotalIntegration,
    CameraAgentObservingDayScheduleView? Schedule,
    IReadOnlyList<CameraAgentTransientOperatorCandidate> Candidates,
    bool CandidatesTruncated,
    IReadOnlyList<LocalAutomationRun> AutomationRuns,
    Guid? RepresentativeCaptureId,
    Guid? PreviousDayCaptureId,
    Guid? NextDayCaptureId);

/// <summary>
/// The active schedule's open windows intersected with the observing night, and the fraction of
/// that expected time covered by retained captures. Windows are the currently active revision's;
/// <c>ActiveRevisionCaveat</c> is true when the night predates that revision's activation, in which
/// case the windows are what the schedule would open today, not what admitted captures then.
/// </summary>
internal sealed record CameraAgentObservingDayScheduleView(
    IReadOnlyList<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)> OpenWindows,
    TimeSpan ExpectedDuration,
    TimeSpan CoveredDuration,
    bool ActiveRevisionCaveat);

internal interface ICameraAgentObservingDayUiService
{
    ValueTask<OperatorUiResult<CameraAgentObservingDayView>> GetAsync(DateOnly observingDate, CancellationToken cancellationToken);
}

internal sealed class CameraAgentObservingDayUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    ICameraAgentArchive archive,
    ICameraAgentTransientOperatorProjection transients,
    ILocalAutomationStore automations,
    CaptureScheduleRuntimeCoordinator schedule,
    ILogger<CameraAgentObservingDayUiService> logger) : ICameraAgentObservingDayUiService
{
    private const int MaximumCandidates = 100;
    // A capture's coverage is the interval [exposure start, exposure start + cadence] clipped to
    // the expected window; cadence is unknown per capture here, so a fixed observation bin is
    // used and stated in the page. Overlapping bins do not double count.
    internal static readonly TimeSpan CoverageBin = TimeSpan.FromMinutes(1);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The operator boundary logs internal failures and returns only fixed, sanitized states.")]
    public async ValueTask<OperatorUiResult<CameraAgentObservingDayView>> GetAsync(DateOnly observingDate, CancellationToken cancellationToken)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var authorized = await authorizationService.AuthorizeAsync(state.User, resource: null, CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false);
        if (!authorized.Succeeded)
        {
            return OperatorUiResult<CameraAgentObservingDayView>.Failure(OperatorUiResultKind.Unauthorized, "Local operator access is required.");
        }
        try
        {
            var detail = await archive.GetObservingDayAsync(observingDate, cancellationToken).ConfigureAwait(false);
            if (detail is null)
            {
                return OperatorUiResult<CameraAgentObservingDayView>.Failure(OperatorUiResultKind.NotFound, "The requested observing day is outside the calendar.");
            }
            var day = detail.Day.Day;
            var candidates = await transients.GetPageAsync(
                new CameraAgentTransientOperatorQuery(MaximumCandidates, null, day.StartUtc, day.EndUtc.AddMilliseconds(-1)), cancellationToken).ConfigureAwait(false);
            var automationState = await automations.GetStateAsync(cancellationToken).ConfigureAwait(false);
            var runs = automationState.Runs
                .Where(run => run.ScheduledForUtc >= day.StartUtc && run.ScheduledForUtc < day.EndUtc)
                .OrderBy(static run => run.ScheduledForUtc)
                .ToArray();
            var scheduleView = BuildSchedule(day, detail.Captures);
            var neighbours = await archive.GetCalendarAsync(
                new CameraAgentGalleryCalendarQuery(observingDate.AddDays(-1), observingDate.AddDays(1)), cancellationToken).ConfigureAwait(false);
            var previous = neighbours.Days.FirstOrDefault(item => item.Day.Date == observingDate.AddDays(-1));
            var next = neighbours.Days.FirstOrDefault(item => item.Day.Date == observingDate.AddDays(1));
            return OperatorUiResult<CameraAgentObservingDayView>.Success(new CameraAgentObservingDayView(
                detail.Day,
                detail.Captures,
                detail.CapturesTruncated,
                detail.TotalIntegration,
                scheduleView,
                candidates.Items,
                candidates.NextCursor is not null,
                runs,
                detail.Day.RepresentativeCaptureId,
                previous?.RepresentativeCaptureId,
                next?.RepresentativeCaptureId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent observing day read failed.");
            return OperatorUiResult<CameraAgentObservingDayView>.Failure(OperatorUiResultKind.Unavailable, "The observing day is temporarily unavailable.");
        }
    }

    private CameraAgentObservingDayScheduleView? BuildSchedule(ObservingDay day, IReadOnlyList<CameraAgentGalleryCapture> captures)
    {
        CaptureSchedulePreview? preview;
        try
        {
            // The observing day spans two local calendar days (noon to noon); expand both.
            preview = schedule.ExpandActive(day.Date, 2);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeZoneNotFoundException or ArgumentException)
        {
            logger.LogDebug(exception, "Schedule expansion unavailable for the observing day.");
            return null;
        }
        if (preview is null)
        {
            return null;
        }
        var windows = ClipOpenWindows(preview, day.StartUtc, day.EndUtc);
        var expected = windows.Aggregate(TimeSpan.Zero, static (sum, window) => sum + (window.EndUtc - window.StartUtc));
        var covered = CoveredDuration(windows, captures);
        var revisionCreated = schedule.Snapshot?.Revision.CreatedUtc;
        return new CameraAgentObservingDayScheduleView(windows, expected, covered, revisionCreated is { } created && created > day.EndUtc);
    }

    /// <summary>
    /// Open intervals minus every closed interval (blackouts, forced-closed overrides, closed date
    /// exceptions), clipped to the night. The expander emits both dispositions as flat intervals, so
    /// subtraction is done here.
    /// </summary>
    internal static IReadOnlyList<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)> ClipOpenWindows(
        CaptureSchedulePreview preview, DateTimeOffset nightStart, DateTimeOffset nightEnd)
    {
        var open = preview.Intervals
            .Where(static interval => interval.Disposition == ExpandedScheduleDisposition.Open)
            .Select(interval => (Start: Max(interval.StartUtc, nightStart), End: Min(interval.EndUtc, nightEnd)))
            .Where(static window => window.End > window.Start)
            .OrderBy(static window => window.Start)
            .ToList();
        var closed = preview.Intervals
            .Where(static interval => interval.Disposition == ExpandedScheduleDisposition.Closed)
            .Select(static interval => (Start: interval.StartUtc, End: interval.EndUtc))
            .ToList();
        var result = new List<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)>();
        foreach (var window in open)
        {
            var pieces = new List<(DateTimeOffset, DateTimeOffset)> { window };
            foreach (var gap in closed)
            {
                var next = new List<(DateTimeOffset, DateTimeOffset)>();
                foreach (var (start, end) in pieces)
                {
                    if (gap.End <= start || gap.Start >= end)
                    {
                        next.Add((start, end));
                        continue;
                    }
                    if (gap.Start > start)
                    {
                        next.Add((start, gap.Start));
                    }
                    if (gap.End < end)
                    {
                        next.Add((gap.End, end));
                    }
                }
                pieces = next;
            }
            result.AddRange(pieces);
        }
        // Merge touching/overlapping pieces so the expected duration is not double counted.
        var merged = new List<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)>();
        foreach (var piece in result.OrderBy(static piece => piece.Item1))
        {
            if (merged.Count > 0 && piece.Item1 <= merged[^1].EndUtc)
            {
                merged[^1] = (merged[^1].StartUtc, Max(merged[^1].EndUtc, piece.Item2));
            }
            else
            {
                merged.Add(piece);
            }
        }
        return merged;
    }

    /// <summary>Union of one-minute bins around each capture's exposure start, clipped to the windows.</summary>
    internal static TimeSpan CoveredDuration(
        IReadOnlyList<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)> windows,
        IReadOnlyList<CameraAgentGalleryCapture> captures)
    {
        if (windows.Count == 0 || captures.Count == 0)
        {
            return TimeSpan.Zero;
        }
        var covered = TimeSpan.Zero;
        DateTimeOffset? lastEnd = null;
        foreach (var capture in captures.OrderBy(static capture => capture.ExposureStartedUtc))
        {
            var binStart = capture.ExposureStartedUtc;
            var binEnd = binStart + CoverageBin;
            if (lastEnd is { } previous && binStart < previous)
            {
                binStart = previous;
            }
            foreach (var window in windows)
            {
                var start = Max(binStart, window.StartUtc);
                var end = Min(binEnd, window.EndUtc);
                if (end > start)
                {
                    covered += end - start;
                }
            }
            lastEnd = Max(lastEnd ?? binEnd, binEnd);
        }
        return covered;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
