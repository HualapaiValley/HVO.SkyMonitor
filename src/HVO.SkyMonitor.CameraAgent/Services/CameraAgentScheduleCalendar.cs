using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// The active schedule resolved into local observing nights for display. Each night runs from local noon to
/// the next local noon, so a dusk-to-dawn window is one contiguous span instead of two calendar days.
/// </summary>
internal sealed record CameraAgentScheduleCalendar(
    string TimeZoneId,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<CameraAgentScheduleNight> Nights);

/// <summary>One observing night, covered end to end by contiguous planned admission segments.</summary>
internal sealed record CameraAgentScheduleNight(
    DateOnly LocalDate,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<CameraAgentScheduleSegment> Segments);

/// <summary>A span with one planned admission outcome, ignoring live safety and manual pause.</summary>
internal sealed record CameraAgentScheduleSegment(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool Admitted,
    CaptureScheduleAdmissionReason Reason,
    string? SetpointProfileId,
    string? OverrideId);

internal static class CameraAgentScheduleCalendarProjection
{
    internal const int MaximumNights = 14;

    /// <summary>The local date the current observing night began on: today after local noon, otherwise yesterday.</summary>
    internal static DateOnly CurrentNight(DateTimeOffset utc, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, timeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        return local.Hour < 12 ? date.AddDays(-1) : date;
    }

    /// <summary>
    /// Evaluates the schedule at every interval and override boundary inside each night, so the segments carry the
    /// engine's own precedence (blackout, override, date exception, weekly window) instead of a display copy of it.
    /// </summary>
    internal static CameraAgentScheduleCalendar Create(
        CaptureScheduleDefinition definition,
        CaptureSchedulePreview expansion,
        IReadOnlyList<CaptureScheduleOverride> overrides,
        string timeZoneId,
        TimeZoneInfo timeZone,
        DateOnly firstNight,
        int nightCount,
        DateTimeOffset generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(expansion);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (nightCount is < 1 or > MaximumNights)
        {
            throw new ArgumentOutOfRangeException(nameof(nightCount));
        }
        var applicable = overrides
            .Where(item => string.Equals(item.ScheduleRevisionSha256, expansion.ScheduleRevisionSha256, StringComparison.Ordinal))
            .ToArray();
        var nights = new List<CameraAgentScheduleNight>(nightCount);
        for (var index = 0; index < nightCount; index++)
        {
            var date = firstNight.AddDays(index);
            var start = LocalNoon(date, timeZone);
            var end = LocalNoon(date.AddDays(1), timeZone);
            nights.Add(new CameraAgentScheduleNight(
                date, start, end, Segments(definition, expansion, applicable, start, end)));
        }
        return new CameraAgentScheduleCalendar(timeZoneId, generatedUtc, nights);
    }

    private static List<CameraAgentScheduleSegment> Segments(
        CaptureScheduleDefinition definition,
        CaptureSchedulePreview expansion,
        IReadOnlyList<CaptureScheduleOverride> overrides,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var boundaries = new SortedSet<DateTimeOffset> { start };
        foreach (var interval in expansion.Intervals)
        {
            AddBoundary(boundaries, interval.StartUtc, start, end);
            AddBoundary(boundaries, interval.EndUtc, start, end);
        }
        foreach (var window in expansion.UnavailableWindows)
        {
            AddBoundary(boundaries, window.LocalDayStartUtc, start, end);
            AddBoundary(boundaries, window.LocalDayEndUtc, start, end);
        }
        foreach (var item in overrides)
        {
            AddBoundary(boundaries, item.StartUtc.ToUniversalTime(), start, end);
            AddBoundary(boundaries, item.EndUtc.ToUniversalTime(), start, end);
        }

        var segments = new List<CameraAgentScheduleSegment>();
        var points = boundaries.ToArray();
        for (var index = 0; index < points.Length; index++)
        {
            var segmentStart = points[index];
            var segmentEnd = index + 1 < points.Length ? points[index + 1] : end;
            var decision = CaptureScheduleEvaluator.Evaluate(
                definition,
                expansion,
                new CaptureScheduleEvaluationRequest(
                    segmentStart, CaptureScheduleSafetyState.Available, ManualPaused: false, overrides));
            var previous = segments.Count > 0 ? segments[^1] : null;
            if (previous is not null &&
                previous.Admitted == decision.Admitted &&
                previous.Reason == decision.Reason &&
                string.Equals(previous.SetpointProfileId, decision.SetpointProfileId, StringComparison.Ordinal) &&
                string.Equals(previous.OverrideId, decision.OverrideId, StringComparison.Ordinal))
            {
                segments[^1] = previous with { EndUtc = segmentEnd };
                continue;
            }
            segments.Add(new CameraAgentScheduleSegment(
                segmentStart, segmentEnd, decision.Admitted, decision.Reason, decision.SetpointProfileId, decision.OverrideId));
        }
        return segments;
    }

    private static void AddBoundary(SortedSet<DateTimeOffset> boundaries, DateTimeOffset value, DateTimeOffset start, DateTimeOffset end)
    {
        if (value > start && value < end)
        {
            boundaries.Add(value);
        }
    }

    private static DateTimeOffset LocalNoon(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified);
        // No real zone skips noon, but step forward rather than throw if one ever does.
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}
