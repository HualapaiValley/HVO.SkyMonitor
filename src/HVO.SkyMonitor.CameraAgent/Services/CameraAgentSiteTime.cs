using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>Converts only UI boundaries; durable and transport timestamps stay UTC.</summary>
public sealed class CameraAgentSiteTime(ObservingDayCalendar calendar)
{
    public string Label => calendar.TimeZoneFallback ? "UTC (site time zone unavailable)" : calendar.TimeZoneId;

    public DateTimeOffset Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, calendar.TimeZone);

    public string Format(DateTimeOffset? instant, string format = "d MMM yyyy HH:mm:ss", string unavailable = "Unavailable")
    {
        if (instant is not { } value) return unavailable;
        var local = Local(value);
        return $"{local.ToString(format, CultureInfo.InvariantCulture)} {local.ToString("zzz", CultureInfo.InvariantCulture)} ({Label})";
    }

    public string Input(DateTimeOffset instant)
    {
        var local = Local(instant);
        // HTML datetime-local supports milliseconds and normalizes zero seconds away.
        // Callers retain the original instant when this displayed boundary is unchanged.
        return local.ToString(local.Second == 0 && local.Millisecond == 0
            ? "yyyy-MM-ddTHH:mm" : "yyyy-MM-ddTHH:mm:ss.FFF", CultureInfo.InvariantCulture);
    }

    public bool TryInput(string? text, out DateTimeOffset? utc, out string? error)
    {
        utc = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var entered))
        {
            error = $"Enter a valid date and time in {Label}.";
            return false;
        }
        var local = DateTime.SpecifyKind(entered, DateTimeKind.Unspecified);
        if (calendar.TimeZone.IsInvalidTime(local))
        {
            error = $"This time does not exist in {Label} because the clocks skip it. Choose another time.";
            return false;
        }
        if (calendar.TimeZone.IsAmbiguousTime(local))
        {
            error = $"This time occurs twice in {Label}. Choose a time outside the repeated hour.";
            return false;
        }
        utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, calendar.TimeZone), TimeSpan.Zero);
        return true;
    }
}
