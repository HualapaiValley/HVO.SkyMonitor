using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

// The default deployment calendar names an actual sunrise-to-sunrise period by its starting civil date.
// Explicit legacy calendars retain the historical noon association. Every value carries its interpretation.
public readonly record struct ObservingDay(
    DateOnly Date,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string TimeZoneId,
    bool TimeZoneFallback,
    SunriseReportingPeriod? SunrisePeriod = null)
{
    public TimeSpan Duration => EndUtc - StartUtc;

    public bool Contains(DateTimeOffset instantUtc) => instantUtc >= StartUtc && instantUtc < EndUtc;

    public string CalendarVersion => SunrisePeriod?.ContractVersion ?? ObservingDayCalendar.LegacyNoonVersion;

    public long StartUnixMillisecondsInclusive => SunriseReportingPeriod.StoredMillisecondAtOrAfter(StartUtc);

    public long EndUnixMillisecondsExclusive => SunriseReportingPeriod.StoredMillisecondAtOrAfter(EndUtc);
}

public sealed class ObservingDayCalendar
{
    public const int MaximumRangeDays = 62;
    public const string LegacyNoonVersion = "hvo-noon-observing-day-v1";
    public const string SiteUnavailable = "reporting.site-unavailable";
    private static readonly TimeSpan Noon = TimeSpan.FromHours(12);
    private SunriseReportingCalendar? _sunrise;
    private string? _unavailableReason;

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

    public string CalendarVersion { get; private init; } = LegacyNoonVersion;

    public bool UsesSunrise => CalendarVersion == SunriseReportingPeriod.CurrentVersion;

    /// <summary>Explicitly retains the supported historical noon interpretation in this calendar's zone.</summary>
    public ObservingDayCalendar LegacyNoon => new(TimeZone, TimeZoneId, TimeZoneFallback);

    public ObservingDayCalendar SelectVersion(string? version)
        => version switch
        {
            null => this,
            LegacyNoonVersion => LegacyNoon,
            SunriseReportingPeriod.CurrentVersion when UsesSunrise => this,
            SunriseReportingPeriod.CurrentVersion => throw new ReportingPeriodUnavailableException(default, SiteUnavailable),
            _ => throw new ArgumentException("The reporting-calendar version is unsupported.", nameof(version))
        };

    /// <summary>Default reporting uses an initialized valid site; unavailable sites never resolve fallback periods.</summary>
    public static ObservingDayCalendar ForDeployment(DeploymentLocationSnapshot? site, ISolarEventCalculator? solar = null)
    {
        if (site is null || !site.Validate().IsValid)
        {
            return new ObservingDayCalendar(TimeZoneInfo.Utc, TimeZoneInfo.Utc.Id, timeZoneFallback: true)
            {
                CalendarVersion = SunriseReportingPeriod.CurrentVersion,
                _unavailableReason = SiteUnavailable
            };
        }
        var sunrise = new SunriseReportingCalendar(site, solar);
        return new ObservingDayCalendar(sunrise.TimeZone, site.TimeZoneId, timeZoneFallback: false)
        {
            CalendarVersion = SunriseReportingPeriod.CurrentVersion,
            _sunrise = sunrise
        };
    }

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
        if (UsesSunrise)
        {
            return FromSunrise(_sunrise?.Resolve(date), date);
        }
        var start = ToUtc(date.ToDateTime(TimeOnly.FromTimeSpan(Noon), DateTimeKind.Unspecified));
        var end = ToUtc(date.AddDays(1).ToDateTime(TimeOnly.FromTimeSpan(Noon), DateTimeKind.Unspecified));
        return new ObservingDay(date, start, end, TimeZoneId, TimeZoneFallback);
    }

    public ObservingDay Resolve(DateTimeOffset instantUtc)
    {
        if (UsesSunrise)
        {
            return FromSunrise(_sunrise?.Resolve(instantUtc),
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instantUtc, TimeZone).DateTime));
        }
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

    public bool TryResolve(DateTimeOffset instantUtc, out ObservingDay day)
    {
        try
        {
            day = Resolve(instantUtc);
            return true;
        }
        catch (ReportingPeriodUnavailableException)
        {
            day = default;
            return false;
        }
    }

    private ObservingDay FromSunrise(SunriseReportingPeriodResolution? result, DateOnly date)
    {
        var period = result?.Period;
        if (period is null)
        {
            throw new ReportingPeriodUnavailableException(date,
                result?.UnavailableReasonCode ?? _unavailableReason ?? SiteUnavailable);
        }
        return new(period.ReportDate, period.StartUtc, period.EndUtc, TimeZoneId, false, period);
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

/// <summary>A reporting date has no qualified solar/site window. Consumers must expose it as unavailable.</summary>
public sealed class ReportingPeriodUnavailableException : InvalidOperationException
{
    public ReportingPeriodUnavailableException()
        : this(default, SunriseReportingCalendar.NoContainingPeriod)
    {
    }

    public ReportingPeriodUnavailableException(string message)
        : base(message)
    {
        ReasonCode = SunriseReportingCalendar.NoContainingPeriod;
    }

    public ReportingPeriodUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
        ReasonCode = SunriseReportingCalendar.NoContainingPeriod;
    }

    public ReportingPeriodUnavailableException(DateOnly date, string reasonCode)
        : base($"The sunrise reporting period for {date:yyyy-MM-dd} is unavailable ({reasonCode}).")
    {
        ReportDate = date;
        ReasonCode = reasonCode;
    }

    public DateOnly ReportDate { get; }

    public string ReasonCode { get; }
}
