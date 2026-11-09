using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>A time-proportional display of the selected period's retained evidence, including gaps and unavailable lanes.</summary>
public sealed partial class ObservingDayTimeline : ComponentBase
{
    internal sealed record CaptureSegment(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Count);
    private sealed record HourTick(double Percent, string Label);

    [Parameter, EditorRequired] public ObservingDay Day { get; set; } = default!;
    [Parameter] public IReadOnlyList<DateTimeOffset> ExposureInstantsUtc { get; set; } = [];
    [Parameter] public IReadOnlyList<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)>? ScheduleWindows { get; set; }
    [Parameter] public IReadOnlyList<CameraAgentTransientOperatorCandidate> Candidates { get; set; } = [];
    [Parameter] public bool CandidatesTruncated { get; set; }
    [Parameter] public bool ActiveRevisionCaveat { get; set; }

    private string TimeZoneId => Day.TimeZoneFallback ? TimeZoneInfo.Utc.Id : Day.TimeZoneId;
    private string CandidatesUrl => ArchiveCalendarPage.CandidatesUrl(Day);
    private static string CoverageBinLabel => FormattableString.Invariant($"{CameraAgentObservingDayUiService.CoverageBin.TotalMinutes:0.######}-minute");

    private string RangeLabel => $"{LocalBoundary(Day.StartUtc)} – {LocalBoundary(Day.EndUtc)} ({TimeZoneId})";

    private string LocalBoundary(DateTimeOffset utc)
        => Local(utc).ToString("HH:mm dd MMM", CultureInfo.InvariantCulture);

    private DateTimeOffset Local(DateTimeOffset utc)
        => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, TimeZoneId);

    private string LocalTime(DateTimeOffset utc) => Local(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    private double Percent(DateTimeOffset utc)
        => Math.Clamp(100 * (utc - Day.StartUtc).TotalSeconds / Day.Duration.TotalSeconds, 0, 100);

    private string BarStyle(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var left = Percent(startUtc);
        // Keep the mathematical width. A minimum percentage falsely lengthens sparse captures and can overrun the period.
        return FormattableString.Invariant($"left: {left:0.######}%; width: {Math.Max(0, Percent(endUtc) - left):0.######}%");
    }

    private string MarkerStyle(DateTimeOffset utc) => FormattableString.Invariant($"left: {Percent(utc):0.######}%");

    /// <summary>Union coverage bins only for exposures inside the half-open period; clip the final bin to its end.</summary>
    internal static IReadOnlyList<CaptureSegment> CaptureSegments(ObservingDay day, IReadOnlyList<DateTimeOffset> exposures)
    {
        var segments = new List<CaptureSegment>();
        foreach (var start in exposures.Where(day.Contains).Order())
        {
            var binEnd = start + CameraAgentObservingDayUiService.CoverageBin;
            var end = binEnd < day.EndUtc ? binEnd : day.EndUtc;
            if (segments.Count > 0 && start <= segments[^1].EndUtc)
            {
                var prior = segments[^1];
                segments[^1] = prior with { EndUtc = end > prior.EndUtc ? end : prior.EndUtc, Count = prior.Count + 1 };
            }
            else
            {
                segments.Add(new CaptureSegment(start, end, 1));
            }
        }
        return segments;
    }

    private IEnumerable<HourTick> HourTicks()
    {
        // Real elapsed time preserves both DST-short and DST-long periods. Offsets disclose the transition.
        var offsetChanges = Local(Day.StartUtc).Offset != Local(Day.EndUtc).Offset;
        for (var index = 0; index <= 6; index++)
        {
            var utc = Day.StartUtc + TimeSpan.FromTicks(Day.Duration.Ticks * index / 6);
            var label = LocalTime(utc);
            if (offsetChanges)
            {
                label += " " + Local(utc).ToString("zzz", CultureInfo.InvariantCulture);
            }
            yield return new HourTick(Percent(utc), label);
        }
    }
}
