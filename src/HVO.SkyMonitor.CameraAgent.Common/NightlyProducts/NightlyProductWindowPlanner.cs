using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

/// <summary>One scheduled segment window of an observing day.</summary>
internal readonly record struct NightlyProductWindow(DateOnly ObservingDate, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>
/// Plans the windows a scheduled run evaluates. An observing day runs from local noon to local noon; its segment
/// windows are fixed offsets from that start, clipped to its end, so a daylight-saving day simply has one window more
/// or fewer. A window is due once it has been closed for the settle interval, and a night is due once its whole day
/// has.
/// </summary>
internal static class NightlyProductWindowPlanner
{
    /// <summary>The observing days a run at <paramref name="nowUtc"/> evaluates, oldest first: the previous and current day.</summary>
    internal static IReadOnlyList<ObservingDay> ResolveDays(ObservingDayCalendar calendar, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var current = calendar.Resolve(nowUtc.ToUniversalTime());
        var previous = calendar.Resolve(current.Date.AddDays(-1));
        return [previous, current];
    }

    /// <summary>The ordered segment windows of a day.</summary>
    internal static IReadOnlyList<NightlyProductWindow> SegmentWindows(ObservingDay day, int segmentMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentMinutes, 1);
        var length = TimeSpan.FromMinutes(segmentMinutes);
        var windows = new List<NightlyProductWindow>();
        for (var start = day.StartUtc; start < day.EndUtc; start += length)
        {
            var end = start + length < day.EndUtc ? start + length : day.EndUtc;
            windows.Add(new NightlyProductWindow(day.Date, start, end));
        }
        return windows;
    }

    /// <summary>Whether a window that ends at <paramref name="endUtc"/> has settled at <paramref name="nowUtc"/>.</summary>
    internal static bool IsDue(DateTimeOffset endUtc, TimeSpan settle, DateTimeOffset nowUtc) =>
        endUtc + settle <= nowUtc;
}
