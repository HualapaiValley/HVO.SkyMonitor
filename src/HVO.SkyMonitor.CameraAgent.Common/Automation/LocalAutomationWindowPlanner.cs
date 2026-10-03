using System.Collections.Immutable;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

public sealed record LocalAutomationWindowPlan(
    LocalAutomationOccurrence? DueOccurrence,
    LocalAutomationOccurrence? NextOccurrence,
    int MissedOccurrencesInLookback,
    bool EarlierOccurrencesOutsideLookback,
    string? UnavailableReasonCode,
    IReadOnlyList<SunriseReportingPeriodResolution> UnavailableDates);

/// <summary>Shared preview/runner arithmetic over the configured site's delivered sunrise calendar.</summary>
public sealed class LocalAutomationWindowPlanner(IObservingDayCalendarProvider calendarProvider)
{
    public const int MaximumLookbackDays = 7;
    // The maximum 32 definitions can each use a distinct policy over ten local dates.
    // Keep that working set bounded without rebuilding every hourly partition on each sweep.
    public const int MaximumCachedWindowSets = 512;
    private readonly object _sync = new();
    private readonly Dictionary<string, ImmutableArray<LocalAutomationSourceWindow>> _cache = [];
    private readonly Queue<string> _cacheOrder = new();

    public LocalAutomationWindowPlan Resolve(LocalAutomationRunnerEntry entry, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var policy = entry.Definition.SourceWindow;
        if (policy is null || !policy.IsValid() ||
            entry.Definition.TriggerKind != LocalAutomationTriggerKind.SourceWindowClosed)
        {
            return new(null, null, 0, false, "automation.source-window-policy-invalid", []);
        }
        var calendar = calendarProvider.Current;
        if (!calendar.UsesSunrise)
        {
            return new(null, null, 0, false, "automation.sunrise-calendar-required", []);
        }
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, calendar.TimeZone).DateTime);
        var cursor = entry.LastOccurrenceUtc ?? entry.Definition.TriggerEpochUtc;
        var cursorDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(cursor, calendar.TimeZone).DateTime);
        var earliestDate = DateOnly.FromDayNumber(Math.Max(DateOnly.MinValue.DayNumber,
            Math.Max(localDate.DayNumber - MaximumLookbackDays, cursorDate.DayNumber - 2)));
        var latestDate = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, localDate.DayNumber + 2));
        var windows = new List<LocalAutomationSourceWindow>();
        var unavailable = new List<SunriseReportingPeriodResolution>();
        for (var dayNumber = earliestDate.DayNumber; dayNumber <= latestDate.DayNumber; dayNumber++)
        {
            var date = DateOnly.FromDayNumber(dayNumber);
            try
            {
                var reporting = calendar.Resolve(date).SunrisePeriod;
                if (reporting is null)
                {
                    return new(null, null, 0, false, ObservingDayCalendar.SiteUnavailable, unavailable);
                }
                windows.AddRange(ResolveWindows(reporting, policy));
            }
            catch (ReportingPeriodUnavailableException exception)
            {
                unavailable.Add(new(date, null, exception.ReasonCode));
            }
        }
        var lookback = LookbackStart(nowUtc);
        var due = windows.Where(window => window.EarliestFinalUtc > cursor && window.EarliestFinalUtc <= nowUtc &&
            window.EarliestFinalUtc >= lookback)
            .OrderBy(static window => window.EarliestFinalUtc).ToArray();
        var next = windows.Where(window => window.EarliestFinalUtc > nowUtc && window.EarliestFinalUtc > cursor)
            .MinBy(static window => window.EarliestFinalUtc);
        var cutoff = windows.Count > 0 ? windows.Min(static window => window.EarliestFinalUtc) : nowUtc;
        return new(due.Length > 0 ? CreateOccurrence(entry, due[^1]) : null,
            next is not null ? CreateOccurrence(entry, next) : null, Math.Max(0, due.Length - 1),
            cursor < cutoff && cursor < LookbackStart(nowUtc),
            next is null ? unavailable.LastOrDefault()?.UnavailableReasonCode ?? ObservingDayCalendar.SiteUnavailable : null,
            unavailable);
    }

    /// <summary>Explicit bounded backfill resolves a named reporting date; it never infers a sliding period.</summary>
    public IReadOnlyList<LocalAutomationSourceWindow> ResolveWindows(
        DateOnly reportDate, LocalAutomationSourceWindowPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var reporting = calendarProvider.Current.Resolve(reportDate).SunrisePeriod
            ?? throw new ReportingPeriodUnavailableException(reportDate, "automation.sunrise-calendar-required");
        return ResolveWindows(reporting, policy);
    }

    public IReadOnlyList<LocalAutomationSourceWindow> ResolveWindows(
        SunriseReportingPeriod reporting, LocalAutomationSourceWindowPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(reporting);
        ArgumentNullException.ThrowIfNull(policy);
        if (!reporting.IsValid() || !policy.IsValid())
        {
            throw new ArgumentException("Source-window planning requires a valid period and policy.");
        }
        var key = CaptureContractJson.ComputeCanonicalJsonSha256(new { reporting.IdentitySha256, policy });
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var result = policy.Kind == LocalAutomationSourceWindowKind.SunriseDay
                ? ImmutableArray.Create(LocalAutomationSourceWindow.Create(policy, reporting, reporting.StartUtc, reporting.EndUtc))
                : ResolveCivilHours(reporting, policy);
            if (_cache.Count == MaximumCachedWindowSets)
            {
                _cache.Remove(_cacheOrder.Dequeue());
            }
            _cache.Add(key, result);
            _cacheOrder.Enqueue(key);
            return result;
        }
    }

    internal static LocalAutomationOccurrence CreateOccurrence(
        LocalAutomationRunnerEntry entry, LocalAutomationSourceWindow window)
    {
        var runIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            entry.Definition.DefinitionId,
            entry.Version,
            entry.RevisionSha256,
            SourceWindowIdentity = window.IdentitySha256
        });
        return LocalAutomationOccurrence.Create(entry, "w:" + runIdentity, window.EarliestFinalUtc, window);
    }

    internal static DateTimeOffset LookbackStart(DateTimeOffset nowUtc)
        => new(Math.Max(DateTimeOffset.MinValue.UtcTicks, nowUtc.UtcTicks - TimeSpan.FromDays(MaximumLookbackDays).Ticks), TimeSpan.Zero);

    private static ImmutableArray<LocalAutomationSourceWindow> ResolveCivilHours(
        SunriseReportingPeriod reporting, LocalAutomationSourceWindowPolicy policy)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(reporting.Site.TimeZoneId);
        if (!string.Equals(CaptureScheduleTimeZone.ComputeRuleSha256(zone), reporting.TimeZoneRulesSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ReportingPeriodUnavailableException(reporting.ReportDate, "automation.time-zone-rules-changed");
        }
        var boundaries = new SortedSet<DateTimeOffset> { reporting.StartUtc, reporting.EndUtc };
        var civilStart = reporting.ReportDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Two civil dates contain both sunrises. Invalid top-of-hour instants are absent; both UTC
        // instants of an ambiguous boundary are retained. Non-hour offset transitions may yield a
        // longer/shorter civil hour. Sunrise clips only the first/last hour, preserving complete coverage.
        for (var hour = 0; hour <= 48 && civilStart.Ticks <= DateTime.MaxValue.Ticks - hour * TimeSpan.TicksPerHour; hour++)
        {
            var local = civilStart.AddHours(hour);
            if (zone.IsInvalidTime(local))
            {
                continue;
            }
            var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
            foreach (var offset in offsets)
            {
                if (local.Ticks < offset.Ticks || local.Ticks - offset.Ticks > DateTimeOffset.MaxValue.UtcTicks)
                {
                    continue;
                }
                var utc = new DateTimeOffset(local, offset).ToUniversalTime();
                if (utc > reporting.StartUtc && utc < reporting.EndUtc)
                {
                    boundaries.Add(utc);
                }
            }
        }
        var values = boundaries.ToArray();
        return values.Zip(values.Skip(1), (start, end) => LocalAutomationSourceWindow.Create(policy, reporting, start, end))
            .ToImmutableArray();
    }
}
