namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

// An observatory-local observing day runs from local noon to the next local
// noon so that one night never splits across two days. The identity is the
// local calendar date of the starting noon; the boundaries are UTC instants
// resolved through the deployment time zone, so a daylight-saving transition
// shortens or lengthens the day without moving evidence to another day.
public readonly record struct ObservingDay(
    DateOnly Date,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string TimeZoneId,
    bool TimeZoneFallback)
{
    public TimeSpan Duration => EndUtc - StartUtc;

    public bool Contains(DateTimeOffset instantUtc) => instantUtc >= StartUtc && instantUtc < EndUtc;
}

public sealed class ObservingDayCalendar
{
    public const int MaximumRangeDays = 62;
    private static readonly TimeSpan Noon = TimeSpan.FromHours(12);

    private ObservingDayCalendar(TimeZoneInfo timeZone, string timeZoneId, bool timeZoneFallback)
    {
        TimeZone = timeZone;
        TimeZoneId = timeZoneId;
        TimeZoneFallback = timeZoneFallback;
    }

    public TimeZoneInfo TimeZone { get; }

    // The identifier the calendar actually resolves with; UTC when the
    // configured identifier was empty or unknown on this host.
    public string TimeZoneId { get; }

    public bool TimeZoneFallback { get; }

    public static ObservingDayCalendar Utc { get; } = new(TimeZoneInfo.Utc, TimeZoneInfo.Utc.Id, timeZoneFallback: false);

    public static ObservingDayCalendar Create(string? timeZoneId)
    {
        var trimmed = timeZoneId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return new ObservingDayCalendar(TimeZoneInfo.Utc, TimeZoneInfo.Utc.Id, timeZoneFallback: true);
        }
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
            return new ObservingDayCalendar(zone, zone.Id, timeZoneFallback: false);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new ObservingDayCalendar(TimeZoneInfo.Utc, TimeZoneInfo.Utc.Id, timeZoneFallback: true);
        }
    }

    public ObservingDay Resolve(DateOnly date)
    {
        var start = ToUtc(date.ToDateTime(TimeOnly.FromTimeSpan(Noon), DateTimeKind.Unspecified));
        var end = ToUtc(date.AddDays(1).ToDateTime(TimeOnly.FromTimeSpan(Noon), DateTimeKind.Unspecified));
        return new ObservingDay(date, start, end, TimeZoneId, TimeZoneFallback);
    }

    public ObservingDay Resolve(DateTimeOffset instantUtc)
    {
        var local = TimeZoneInfo.ConvertTime(instantUtc, TimeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var day = Resolve(local.TimeOfDay >= Noon ? date : date.AddDays(-1));
        // A transition that lands exactly on a noon boundary can leave the
        // instant just outside the arithmetic day; the neighbouring day owns it.
        if (instantUtc < day.StartUtc)
        {
            return Resolve(day.Date.AddDays(-1));
        }
        return instantUtc >= day.EndUtc ? Resolve(day.Date.AddDays(1)) : day;
    }

    public IReadOnlyList<ObservingDay> Range(DateOnly fromDate, DateOnly toDate)
    {
        if (toDate < fromDate)
        {
            throw new ArgumentOutOfRangeException(nameof(toDate), toDate, "The observing-day range must not end before it starts.");
        }
        var count = toDate.DayNumber - fromDate.DayNumber + 1;
        if (count > MaximumRangeDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toDate), toDate, $"An observing-day range is bounded to {MaximumRangeDays} days; {count} were requested.");
        }
        var days = new ObservingDay[count];
        for (var index = 0; index < count; index++)
        {
            days[index] = Resolve(fromDate.AddDays(index));
        }
        return days;
    }

    // Local noon is never inside a daylight-saving gap on any deployed zone,
    // but a zone that ever shifted across noon must still resolve: an invalid
    // local time moves forward past the gap, and an ambiguous one takes the
    // earlier (daylight) instant so the day starts at the first noon.
    private DateTimeOffset ToUtc(DateTime local)
    {
        if (TimeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }
        if (TimeZone.IsAmbiguousTime(local))
        {
            var offset = TimeZone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }
        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local)).ToUniversalTime();
    }
}
