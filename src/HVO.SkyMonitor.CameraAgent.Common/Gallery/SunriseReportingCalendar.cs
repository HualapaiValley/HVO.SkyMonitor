using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>
/// CameraAgent reporting policy over the shared Astronomy solar contract. A calendar binds one immutable site and
/// time-zone rule set. The bounded cache also ensures neighbouring periods reuse precisely the same event boundary.
/// </summary>
public sealed class SunriseReportingCalendar
{
    public const string MissingStartSunrise = "reporting.start-sunrise-unavailable";
    public const string MissingEndSunrise = "reporting.end-sunrise-unavailable";
    public const string MissingCivilDate = "reporting.civil-date-unavailable";
    public const string NoContainingPeriod = "reporting.no-containing-sunrise-period";
    public const int MaximumCachedDates = 256;

    private readonly ISolarEventCalculator _solar;
    private readonly Dictionary<DateOnly, SolarEventResult> _events = [];
    private readonly Queue<DateOnly> _cacheOrder = new();
    private readonly Dictionary<DateOnly, SunriseReportingPeriodResolution> _periods = [];
    private readonly Queue<DateOnly> _periodOrder = new();
    private readonly object _sync = new();

    public SunriseReportingCalendar(DeploymentLocationSnapshot site, ISolarEventCalculator? solar = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (!site.Validate().IsValid)
        {
            throw new ArgumentException("A reporting calendar requires a valid immutable site snapshot.", nameof(site));
        }
        Site = site;
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById(site.TimeZoneId);
        TimeZoneRulesSha256 = CaptureScheduleTimeZone.ComputeRuleSha256(TimeZone);
        _solar = solar ?? new AstronomyEngineSolarEventCalculator();
    }

    public DeploymentLocationSnapshot Site { get; }

    public TimeZoneInfo TimeZone { get; }

    public string TimeZoneRulesSha256 { get; }

    public SunriseReportingPeriodResolution Resolve(DateOnly date)
    {
        lock (_sync)
        {
            if (_periods.TryGetValue(date, out var cached))
            {
                return cached;
            }
            var result = ResolveUncached(date);
            if (_periods.Count == MaximumCachedDates)
            {
                _periods.Remove(_periodOrder.Dequeue());
            }
            _periods.Add(date, result);
            _periodOrder.Enqueue(date);
            return result;
        }
    }

    private SunriseReportingPeriodResolution ResolveUncached(DateOnly date)
    {
        if (date == DateOnly.MaxValue)
        {
            return new(date, null, MissingCivilDate);
        }
        var start = Sunrise(date);
        var end = Sunrise(date.AddDays(1));
        if (start is null || end is null)
        {
            return new(date, null, MissingCivilDate);
        }
        if (start.Utc is not { } startUtc)
        {
            return new(date, null, MissingStartSunrise);
        }
        if (end.Utc is not { } endUtc)
        {
            return new(date, null, MissingEndSunrise);
        }
        if (endUtc <= startUtc || endUtc - startUtc >= TimeSpan.FromHours(48))
        {
            return new(date, null, MissingCivilDate);
        }
        return new(date, SunriseReportingPeriod.Create(date, startUtc, endUtc, Site, TimeZoneRulesSha256,
            start.AlgorithmVersion, end.AlgorithmVersion), null);
    }

    public SunriseReportingPeriodResolution Resolve(DateTimeOffset sourceUtc)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(sourceUtc, TimeZone).DateTime);
        var current = Resolve(localDate);
        if (current.Period?.Contains(sourceUtc) == true)
        {
            return current;
        }
        if (localDate > DateOnly.MinValue)
        {
            var previous = Resolve(localDate.AddDays(-1));
            if (previous.Period?.Contains(sourceUtc) == true)
            {
                return previous;
            }
        }
        return new(localDate, null, NoContainingPeriod);
    }

    private SolarEventResult? Sunrise(DateOnly date)
    {
        lock (_sync)
        {
            if (_events.TryGetValue(date, out var cached))
            {
                return cached;
            }
            (DateTimeOffset StartUtc, DateTimeOffset EndUtc) civilDay;
            try
            {
                civilDay = CaptureScheduleTimeZone.ResolveDay(date, TimeZone);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Extreme civil dates can lack a representable UTC boundary; they have no reporting window.
                return null;
            }
            if (civilDay.EndUtc <= civilDay.StartUtc ||
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(civilDay.StartUtc, TimeZone).DateTime) != date)
            {
                // A whole-date offset change can map a nominal midnight to another civil date.
                // The solar search interval must actually begin on the requested date.
                return null;
            }
            var result = _solar.Find(SolarEventKind.Sunrise, civilDay.StartUtc, civilDay.EndUtc,
                Site.LatitudeDegrees, Site.LongitudeDegrees, Site.ElevationMeters);
            if (result.Kind != SolarEventKind.Sunrise || result.Utc is { } utc &&
                (utc.Offset != TimeSpan.Zero || utc < civilDay.StartUtc || utc >= civilDay.EndUtc) ||
                string.IsNullOrWhiteSpace(result.AlgorithmVersion) || result.AlgorithmVersion.Length > 128)
            {
                throw new InvalidDataException("The solar calculator returned an invalid reporting boundary.");
            }
            if (_events.Count == MaximumCachedDates)
            {
                _events.Remove(_cacheOrder.Dequeue());
            }
            _events.Add(date, result);
            _cacheOrder.Enqueue(date);
            return result;
        }
    }
}
