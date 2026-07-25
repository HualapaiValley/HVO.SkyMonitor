using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Scheduling;

public enum CaptureScheduleSafetyState
{
    Available,
    Unknown,
    IngressUnavailable,
    RequiredLaneUnavailable,
    StorageUnavailable
}

public enum ExpandedScheduleDisposition
{
    Open,
    Closed
}

public enum LocalBoundaryRole
{
    Start,
    End
}

public enum CaptureScheduleOverrideMode
{
    ForceClosed,
    ForceOpen
}

public sealed record CaptureScheduleOverride(
    string Id,
    string ScheduleRevisionId,
    string ScheduleRevisionSha256,
    CaptureScheduleOverrideMode Mode,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string? SetpointProfileId = null,
    bool OneShot = false,
    DateTimeOffset? ConsumedUtc = null);

public sealed record ExpandedScheduleInterval(
    string Id,
    CaptureScheduleIntervalSource Source,
    ExpandedScheduleDisposition Disposition,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateOnly? LocalDate,
    string? SetpointProfileId,
    string? SolarAlgorithmVersion = null);

public sealed record UnavailableScheduleWindow(
    string Id,
    CaptureScheduleIntervalSource Source,
    DateOnly LocalDate,
    DateTimeOffset LocalDayStartUtc,
    DateTimeOffset LocalDayEndUtc,
    string ReasonCode,
    string SolarAlgorithmVersion);

public sealed record CaptureSchedulePreview(
    string ScheduleRevisionSha256,
    string ExpansionSha256,
    string ExpansionAlgorithmVersion,
    string TimeZoneRuleSha256,
    string SolarAlgorithmVersion,
    DateTimeOffset PreviewStartUtc,
    DateTimeOffset PreviewEndUtc,
    IReadOnlyList<ExpandedScheduleInterval> Intervals,
    IReadOnlyList<UnavailableScheduleWindow> UnavailableWindows);

public sealed record CaptureScheduleEvaluationRequest(
    DateTimeOffset Utc,
    CaptureScheduleSafetyState SafetyState,
    bool ManualPaused,
    IReadOnlyList<CaptureScheduleOverride>? Overrides = null);

public sealed record CaptureScheduleDecision(
    bool Admitted,
    CaptureScheduleAdmissionReason Reason,
    CaptureScheduleSafetyState SafetyState,
    DateTimeOffset DecisionUtc,
    string? SetpointProfileId,
    ExpandedScheduleInterval? Interval,
    string? OverrideId,
    bool ConsumeOneShotOverride,
    DateTimeOffset? NextTransitionUtc);

public sealed class CaptureScheduleValidationException : ArgumentException
{
    public CaptureScheduleValidationException()
        : this("schedule.invalid", "The capture schedule is invalid.")
    {
    }

    public CaptureScheduleValidationException(string message)
        : this("schedule.invalid", message)
    {
    }

    public CaptureScheduleValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        ReasonCode = "schedule.invalid";
    }

    public CaptureScheduleValidationException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public static class CaptureScheduleTimeZone
{
    public static DateTimeOffset Resolve(
        DateOnly date,
        TimeOnly time,
        TimeZoneInfo timeZone,
        LocalBoundaryRole role)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            local = AdvanceToFirstValid(local, timeZone);
        }

        TimeSpan offset;
        if (timeZone.IsAmbiguousTime(local))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(local);
            offset = role == LocalBoundaryRole.Start ? offsets.Max() : offsets.Min();
        }
        else
        {
            offset = timeZone.GetUtcOffset(local);
        }
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) ResolveDay(
        DateOnly date,
        TimeZoneInfo timeZone)
        => (Resolve(date, TimeOnly.MinValue, timeZone, LocalBoundaryRole.Start),
            Resolve(date.AddDays(1), TimeOnly.MinValue, timeZone, LocalBoundaryRole.Start));

    public static string ComputeRuleSha256(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            timeZone.Id,
            BaseUtcOffsetTicks = timeZone.BaseUtcOffset.Ticks,
            timeZone.SupportsDaylightSavingTime,
            Rules = timeZone.GetAdjustmentRules().Select(static rule => new
            {
                DateStart = DateOnly.FromDateTime(rule.DateStart),
                DateEnd = DateOnly.FromDateTime(rule.DateEnd),
                DaylightDeltaTicks = rule.DaylightDelta.Ticks,
                BaseUtcOffsetDeltaTicks = rule.BaseUtcOffsetDelta.Ticks,
                Start = ToCanonical(rule.DaylightTransitionStart),
                End = ToCanonical(rule.DaylightTransitionEnd)
            }).ToArray()
        });
    }

    private static object ToCanonical(TimeZoneInfo.TransitionTime value) => new
    {
        value.IsFixedDateRule,
        value.Month,
        value.Week,
        value.Day,
        value.DayOfWeek,
        TimeOfDayTicks = value.TimeOfDay.TimeOfDay.Ticks
    };

    private static DateTime AdvanceToFirstValid(DateTime invalid, TimeZoneInfo timeZone)
    {
        var valid = invalid.AddHours(4);
        while (timeZone.IsInvalidTime(valid))
        {
            valid = valid.AddHours(4);
        }

        var lowerTicks = invalid.Ticks;
        var upperTicks = valid.Ticks;
        while (upperTicks - lowerTicks > 1)
        {
            var middleTicks = lowerTicks + (upperTicks - lowerTicks) / 2;
            var middle = new DateTime(middleTicks, DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(middle))
            {
                lowerTicks = middleTicks;
            }
            else
            {
                upperTicks = middleTicks;
            }
        }
        return new DateTime(upperTicks, DateTimeKind.Unspecified);
    }
}

public static class CaptureScheduleIntervalExpander
{
    public const string AlgorithmVersion = "capture-schedule-expand-v1";

    public static CaptureSchedulePreview Expand(
        CaptureScheduleDefinition definition,
        DateOnly firstLocalDate,
        int dayCount,
        TimeZoneInfo timeZone,
        ObservatoryLocation observer,
        ISolarEventCalculator solarEvents)
    {
        ArgumentNullException.ThrowIfNull(definition);
        CaptureScheduleValidator.Validate(definition);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(solarEvents);
        if (dayCount is < 1 or > 366)
        {
            throw new ArgumentOutOfRangeException(nameof(dayCount));
        }

        var revisionSha256 = CaptureScheduleContract.ComputeSha256(definition);
        var intervals = new List<ExpandedScheduleInterval>();
        var unavailable = new List<UnavailableScheduleWindow>();
        var solarVersions = new HashSet<string>(StringComparer.Ordinal);
        var dateExceptions = (definition.DateExceptions ?? []).ToDictionary(static item => item.Date);
        var previewStart = CaptureScheduleTimeZone.ResolveDay(firstLocalDate, timeZone).StartUtc;
        var previewEnd = CaptureScheduleTimeZone.ResolveDay(firstLocalDate.AddDays(dayCount - 1), timeZone).EndUtc;

        for (var dayIndex = -2; dayIndex < dayCount; dayIndex++)
        {
            var date = firstLocalDate.AddDays(dayIndex);
            var localDay = CaptureScheduleTimeZone.ResolveDay(date, timeZone);
            if (dateExceptions.TryGetValue(date, out var dateRule))
            {
                intervals.Add(new ExpandedScheduleInterval(
                    $"{dateRule.Id}:{date:yyyy-MM-dd}:closed",
                    CaptureScheduleIntervalSource.DateExceptionClosed,
                    ExpandedScheduleDisposition.Closed,
                    localDay.StartUtc,
                    localDay.EndUtc,
                    date,
                    null));
                if (!dateRule.ForceClosed)
                {
                    foreach (var window in dateRule.Windows ?? [])
                    {
                        ExpandWindow(
                            window.Id, CaptureScheduleIntervalSource.DateExceptionWindow,
                            date, window.Start, window.End, window.SetpointProfileId,
                            timeZone, observer, solarEvents, intervals, unavailable, solarVersions);
                    }
                }
            }
            else
            {
                foreach (var window in definition.WeeklyWindows.Where(item => item.Day == date.DayOfWeek))
                {
                    ExpandWindow(
                        window.Id, CaptureScheduleIntervalSource.WeeklyWindow,
                        date, window.Start, window.End, window.SetpointProfileId,
                        timeZone, observer, solarEvents, intervals, unavailable, solarVersions);
                }
            }
        }

        foreach (var blackout in definition.Blackouts ?? [])
        {
            var start = ToMilliseconds(blackout.StartUtc);
            var end = ToMilliseconds(blackout.EndUtc);
            if (start < previewEnd && end > previewStart)
            {
                intervals.Add(new ExpandedScheduleInterval(
                    blackout.Id,
                    CaptureScheduleIntervalSource.Blackout,
                    ExpandedScheduleDisposition.Closed,
                    start,
                    end,
                    TimeZoneInfo.ConvertTime(start, timeZone).Date.ToDateOnly(),
                    null));
            }
        }

        if (definition.LegacyAlwaysOpen)
        {
            intervals.Add(new ExpandedScheduleInterval(
                "legacy-always-open",
                CaptureScheduleIntervalSource.LegacyCompatibility,
                ExpandedScheduleDisposition.Open,
                previewStart,
                previewEnd,
                firstLocalDate,
                definition.LegacySetpointProfileId));
        }

        if (solarVersions.Count > 1)
        {
            throw new CaptureScheduleValidationException(
                "schedule.solar-version.conflict",
                "A preview cannot combine different solar algorithm versions.");
        }
        var solarVersion = solarVersions.SingleOrDefault() ?? "none";
        var relevantIntervals = intervals.Where(item => item.StartUtc < previewEnd && item.EndUtc > previewStart).ToArray();
        var relevantUnavailable = unavailable.Where(item =>
            item.LocalDayStartUtc < previewEnd && item.LocalDayEndUtc > previewStart).ToArray();
        ValidateExpandedConflicts(relevantIntervals);
        var orderedIntervals = relevantIntervals.OrderBy(static item => item.StartUtc)
            .ThenBy(static item => item.Source)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var orderedUnavailable = relevantUnavailable.OrderBy(static item => item.LocalDayStartUtc)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var timeZoneRuleSha256 = CaptureScheduleTimeZone.ComputeRuleSha256(timeZone);
        var expansionSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            revisionSha256,
            AlgorithmVersion,
            timeZoneRuleSha256,
            solarVersion,
            previewStart,
            previewEnd,
            Intervals = orderedIntervals,
            Unavailable = orderedUnavailable
        });
        return new CaptureSchedulePreview(
            revisionSha256,
            expansionSha256,
            AlgorithmVersion,
            timeZoneRuleSha256,
            solarVersion,
            previewStart,
            previewEnd,
            orderedIntervals,
            orderedUnavailable);
    }

    private static void ExpandWindow(
        string id,
        CaptureScheduleIntervalSource source,
        DateOnly date,
        CaptureScheduleBoundary startBoundary,
        CaptureScheduleBoundary endBoundary,
        string profileId,
        TimeZoneInfo timeZone,
        ObservatoryLocation observer,
        ISolarEventCalculator solarEvents,
        List<ExpandedScheduleInterval> intervals,
        List<UnavailableScheduleWindow> unavailable,
        HashSet<string> solarVersions)
    {
        var start = ResolveBoundary(date, startBoundary, LocalBoundaryRole.Start, timeZone, observer, solarEvents);
        var end = ResolveBoundary(date, endBoundary, LocalBoundaryRole.End, timeZone, observer, solarEvents);
        if (start.SolarVersion is { } startVersion)
        {
            solarVersions.Add(startVersion);
        }
        if (end.SolarVersion is { } endVersion)
        {
            solarVersions.Add(endVersion);
        }
        if (!start.Utc.HasValue || !end.Utc.HasValue)
        {
            var startDay = CaptureScheduleTimeZone.ResolveDay(date.AddDays(startBoundary.DayOffset), timeZone);
            var endDay = CaptureScheduleTimeZone.ResolveDay(date.AddDays(endBoundary.DayOffset), timeZone);
            var unavailableStart = start.Utc ?? startDay.StartUtc;
            var unavailableEnd = end.Utc ?? endDay.EndUtc;
            if (unavailableEnd <= unavailableStart)
            {
                unavailableStart = startDay.StartUtc;
                unavailableEnd = endDay.EndUtc;
            }
            unavailable.Add(new UnavailableScheduleWindow(
                id,
                source,
                date,
                unavailableStart,
                unavailableEnd,
                "schedule.solar-event-unavailable",
                start.SolarVersion ?? end.SolarVersion ?? "none"));
            return;
        }
        if (end.Utc <= start.Utc)
        {
            throw new CaptureScheduleValidationException(
                "schedule.interval.invalid",
                $"Expanded schedule window '{id}' is not a positive half-open interval.");
        }
        intervals.Add(new ExpandedScheduleInterval(
            $"{id}:{date:yyyy-MM-dd}", source, ExpandedScheduleDisposition.Open,
            start.Utc.Value, end.Utc.Value, date, profileId,
            start.SolarVersion ?? end.SolarVersion));
    }

    private static (DateTimeOffset? Utc, string? SolarVersion) ResolveBoundary(
        DateOnly date,
        CaptureScheduleBoundary boundary,
        LocalBoundaryRole role,
        TimeZoneInfo timeZone,
        ObservatoryLocation observer,
        ISolarEventCalculator solarEvents)
    {
        var anchorDate = date.AddDays(boundary.DayOffset);
        if (boundary.Kind == CaptureScheduleBoundaryKind.FixedLocalTime)
        {
            return (ToMilliseconds(CaptureScheduleTimeZone.Resolve(
                anchorDate, boundary.LocalTime!.Value, timeZone, role).Add(boundary.Offset)), null);
        }

        var day = CaptureScheduleTimeZone.ResolveDay(anchorDate, timeZone);
        var result = solarEvents.Find(
            ToSolarEvent(boundary.Kind),
            day.StartUtc,
            day.EndUtc,
            observer.LatitudeDegrees,
            observer.LongitudeDegrees,
            observer.ElevationMeters);
        if (result.Utc.HasValue)
        {
            return (ToMilliseconds(result.Utc.Value.Add(boundary.Offset)), result.AlgorithmVersion);
        }
        return boundary.NoEventFallbackLocalTime.HasValue
            ? (ToMilliseconds(CaptureScheduleTimeZone.Resolve(
                anchorDate, boundary.NoEventFallbackLocalTime.Value, timeZone, role).Add(boundary.Offset)),
                result.AlgorithmVersion)
            : (null, result.AlgorithmVersion);
    }

    private static SolarEventKind ToSolarEvent(CaptureScheduleBoundaryKind kind) => kind switch
    {
        CaptureScheduleBoundaryKind.Sunrise => SolarEventKind.Sunrise,
        CaptureScheduleBoundaryKind.Sunset => SolarEventKind.Sunset,
        CaptureScheduleBoundaryKind.CivilDawn => SolarEventKind.CivilDawn,
        CaptureScheduleBoundaryKind.CivilDusk => SolarEventKind.CivilDusk,
        CaptureScheduleBoundaryKind.NauticalDawn => SolarEventKind.NauticalDawn,
        CaptureScheduleBoundaryKind.NauticalDusk => SolarEventKind.NauticalDusk,
        CaptureScheduleBoundaryKind.AstronomicalDawn => SolarEventKind.AstronomicalDawn,
        CaptureScheduleBoundaryKind.AstronomicalDusk => SolarEventKind.AstronomicalDusk,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static DateTimeOffset ToMilliseconds(DateTimeOffset value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUniversalTime().ToUnixTimeMilliseconds());

    private static void ValidateExpandedConflicts(IReadOnlyList<ExpandedScheduleInterval> intervals)
    {
        foreach (var source in new[]
        {
            CaptureScheduleIntervalSource.WeeklyWindow,
            CaptureScheduleIntervalSource.DateExceptionWindow
        })
        {
            var sourceIntervals = intervals.Where(item => item.Source == source);
            var groups = source == CaptureScheduleIntervalSource.DateExceptionWindow
                ? sourceIntervals.GroupBy(static item => item.LocalDate)
                : sourceIntervals.GroupBy(static _ => (DateOnly?)null);
            foreach (var values in groups.Select(static group => group.OrderBy(static item => item.StartUtc)
                         .ThenBy(static item => item.Id, StringComparer.Ordinal)
                         .ToArray()))
            {
                for (var index = 0; index < values.Length; index++)
                {
                    for (var otherIndex = index + 1;
                         otherIndex < values.Length && values[otherIndex].StartUtc < values[index].EndUtc;
                         otherIndex++)
                    {
                        if (!string.Equals(
                            values[index].SetpointProfileId,
                            values[otherIndex].SetpointProfileId,
                            StringComparison.Ordinal))
                        {
                            throw new CaptureScheduleValidationException(
                                "schedule.interval.conflict",
                                $"Expanded schedule windows '{values[index].Id}' and '{values[otherIndex].Id}' conflict.");
                        }
                    }
                }
            }
        }
    }
}

public static class CaptureScheduleEvaluator
{
    public static CaptureScheduleDecision Evaluate(
        CaptureScheduleDefinition definition,
        CaptureSchedulePreview preview,
        CaptureScheduleEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(definition);
        CaptureScheduleValidator.Validate(definition);
        var utc = request.Utc.ToUniversalTime();
        if (utc < preview.PreviewStartUtc || utc >= preview.PreviewEndUtc)
        {
            throw new CaptureScheduleValidationException(
                "schedule.preview.out-of-range",
                "The evaluation instant is outside the preview bounds.");
        }
        if (!string.Equals(
            preview.ScheduleRevisionSha256,
            CaptureScheduleContract.ComputeSha256(definition),
            StringComparison.Ordinal))
        {
            throw new CaptureScheduleValidationException(
                "schedule.preview.revision-mismatch",
                "The preview does not belong to the supplied schedule revision.");
        }
        if ((request.Overrides ?? []).Any(candidate =>
                !string.Equals(
                    candidate.ScheduleRevisionSha256,
                    preview.ScheduleRevisionSha256,
                    StringComparison.Ordinal)))
        {
            throw new CaptureScheduleValidationException(
                "schedule.override.revision-mismatch",
                "The override does not belong to the active schedule revision.");
        }
        ValidateOverrides(definition, request.Overrides);

        return EvaluateAt(preview, request, utc, includeNextTransition: true);
    }

    private static CaptureScheduleDecision EvaluateAt(
        CaptureSchedulePreview preview,
        CaptureScheduleEvaluationRequest request,
        DateTimeOffset utc,
        bool includeNextTransition)
    {
        var nextTransition = includeNextTransition ? NextTransition(preview, request, utc) : null;
        if (request.SafetyState != CaptureScheduleSafetyState.Available)
        {
            return Denied(CaptureScheduleAdmissionReason.SafetyUnavailable, request.SafetyState, utc, nextTransition);
        }
        if (request.ManualPaused)
        {
            return Denied(CaptureScheduleAdmissionReason.ManualPause, request.SafetyState, utc, nextTransition);
        }

        var blackout = Active(preview, utc, CaptureScheduleIntervalSource.Blackout);
        if (blackout is not null)
        {
            return Denied(CaptureScheduleAdmissionReason.Blackout, request.SafetyState, utc, nextTransition, blackout);
        }
        var activeOverride = ActiveOverride(request.Overrides, utc);
        if (activeOverride?.Mode == CaptureScheduleOverrideMode.ForceClosed)
        {
            return new CaptureScheduleDecision(
                false, CaptureScheduleAdmissionReason.ForceClosedOverride, request.SafetyState, utc,
                null, null, activeOverride.Id, false, nextTransition);
        }
        if (activeOverride?.Mode == CaptureScheduleOverrideMode.ForceOpen)
        {
            var effectiveInterval = new ExpandedScheduleInterval(
                activeOverride.Id,
                CaptureScheduleIntervalSource.ForceOpenOverride,
                ExpandedScheduleDisposition.Open,
                activeOverride.StartUtc.ToUniversalTime(),
                activeOverride.EndUtc.ToUniversalTime(),
                null,
                activeOverride.SetpointProfileId);
            return new CaptureScheduleDecision(
                true, CaptureScheduleAdmissionReason.ForceOpenOverride, request.SafetyState, utc,
                activeOverride.SetpointProfileId, effectiveInterval, activeOverride.Id,
                activeOverride.OneShot && !activeOverride.ConsumedUtc.HasValue, nextTransition);
        }

        var exceptionClosed = Active(preview, utc, CaptureScheduleIntervalSource.DateExceptionClosed);
        if (exceptionClosed is not null)
        {
            var exception = preview.Intervals.FirstOrDefault(item =>
                item.Source == CaptureScheduleIntervalSource.DateExceptionWindow &&
                item.LocalDate == exceptionClosed.LocalDate &&
                utc >= item.StartUtc && utc < item.EndUtc);
            if (exception is not null)
            {
                return Admitted(
                    CaptureScheduleAdmissionReason.DateException, request.SafetyState, utc, exception, nextTransition);
            }
            var unavailable = preview.UnavailableWindows.Any(item =>
                item.Source == CaptureScheduleIntervalSource.DateExceptionWindow &&
                item.LocalDate == exceptionClosed.LocalDate &&
                utc >= item.LocalDayStartUtc && utc < item.LocalDayEndUtc);
            return Denied(
                unavailable ? CaptureScheduleAdmissionReason.NoSolarEvent : CaptureScheduleAdmissionReason.DateException,
                request.SafetyState,
                utc,
                nextTransition,
                exceptionClosed);
        }
        var carriedException = Active(preview, utc, CaptureScheduleIntervalSource.DateExceptionWindow);
        if (carriedException is not null)
        {
            return Admitted(
                CaptureScheduleAdmissionReason.DateException,
                request.SafetyState,
                utc,
                carriedException,
                nextTransition);
        }
        var carriedExceptionUnavailable = preview.UnavailableWindows.Any(item =>
            item.Source == CaptureScheduleIntervalSource.DateExceptionWindow &&
            utc >= item.LocalDayStartUtc && utc < item.LocalDayEndUtc);
        if (carriedExceptionUnavailable)
        {
            return Denied(
                CaptureScheduleAdmissionReason.NoSolarEvent,
                request.SafetyState,
                utc,
                nextTransition);
        }
        var weekly = Active(preview, utc, CaptureScheduleIntervalSource.WeeklyWindow);
        if (weekly is not null)
        {
            return Admitted(
                CaptureScheduleAdmissionReason.WeeklyWindow, request.SafetyState, utc, weekly, nextTransition);
        }
        var legacy = Active(preview, utc, CaptureScheduleIntervalSource.LegacyCompatibility);
        if (legacy is not null)
        {
            return Admitted(
                CaptureScheduleAdmissionReason.LegacyCompatibility, request.SafetyState, utc, legacy, nextTransition);
        }
        var solarUnavailable = preview.UnavailableWindows.Any(item =>
            utc >= item.LocalDayStartUtc && utc < item.LocalDayEndUtc);
        return Denied(
            solarUnavailable ? CaptureScheduleAdmissionReason.NoSolarEvent : CaptureScheduleAdmissionReason.DefaultClosed,
            request.SafetyState,
            utc,
            nextTransition);
    }

    private static CaptureScheduleDecision Admitted(
        CaptureScheduleAdmissionReason reason,
        CaptureScheduleSafetyState safety,
        DateTimeOffset utc,
        ExpandedScheduleInterval interval,
        DateTimeOffset? nextTransition)
        => new(true, reason, safety, utc, interval.SetpointProfileId, interval, null, false, nextTransition);

    private static CaptureScheduleDecision Denied(
        CaptureScheduleAdmissionReason reason,
        CaptureScheduleSafetyState safety,
        DateTimeOffset utc,
        DateTimeOffset? nextTransition,
        ExpandedScheduleInterval? interval = null)
        => new(false, reason, safety, utc, null, interval, null, false, nextTransition);

    private static ExpandedScheduleInterval? Active(
        CaptureSchedulePreview preview,
        DateTimeOffset utc,
        CaptureScheduleIntervalSource source)
        => preview.Intervals.FirstOrDefault(item =>
            item.Source == source && utc >= item.StartUtc && utc < item.EndUtc);

    private static CaptureScheduleOverride? ActiveOverride(
        IReadOnlyList<CaptureScheduleOverride>? values,
        DateTimeOffset utc)
        => (values ?? []).Where(value =>
                utc >= value.StartUtc.ToUniversalTime() && utc < value.EndUtc.ToUniversalTime() &&
                (!value.OneShot || !value.ConsumedUtc.HasValue))
            .OrderBy(static value => value.Mode == CaptureScheduleOverrideMode.ForceClosed ? 0 : 1)
            .ThenBy(static value => value.StartUtc)
            .ThenBy(static value => value.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    private static void ValidateOverrides(
        CaptureScheduleDefinition definition,
        IReadOnlyList<CaptureScheduleOverride>? overrides)
    {
        var profileIds = definition.SetpointProfiles.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scheduleOverride in overrides ?? [])
        {
            if (string.IsNullOrWhiteSpace(scheduleOverride.Id) || scheduleOverride.Id.Length > 128 ||
                !ids.Add(scheduleOverride.Id) || !Enum.IsDefined(scheduleOverride.Mode) ||
                scheduleOverride.StartUtc.Offset != TimeSpan.Zero ||
                scheduleOverride.EndUtc.Offset != TimeSpan.Zero ||
                scheduleOverride.StartUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
                scheduleOverride.EndUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
                scheduleOverride.EndUtc <= scheduleOverride.StartUtc ||
                scheduleOverride.ConsumedUtc is { } consumedUtc && consumedUtc.Offset != TimeSpan.Zero ||
                scheduleOverride.OneShot && scheduleOverride.Mode != CaptureScheduleOverrideMode.ForceOpen ||
                scheduleOverride.Mode == CaptureScheduleOverrideMode.ForceOpen &&
                (string.IsNullOrWhiteSpace(scheduleOverride.SetpointProfileId) ||
                 !profileIds.Contains(scheduleOverride.SetpointProfileId)) ||
                scheduleOverride.Mode == CaptureScheduleOverrideMode.ForceClosed &&
                scheduleOverride.SetpointProfileId is not null)
            {
                throw new CaptureScheduleValidationException(
                    "schedule.override.invalid",
                    "A schedule override is invalid.");
            }
        }
    }

    private static DateTimeOffset? NextTransition(
        CaptureSchedulePreview preview,
        CaptureScheduleEvaluationRequest request,
        DateTimeOffset utc)
    {
        var current = EvaluateAt(preview, request, utc, includeNextTransition: false);
        var values = preview.Intervals.SelectMany(static item => new[] { item.StartUtc, item.EndUtc })
            .Concat(preview.UnavailableWindows.SelectMany(static item =>
                new[] { item.LocalDayStartUtc, item.LocalDayEndUtc }))
            .Append(preview.PreviewEndUtc)
            .ToList();
        foreach (var scheduleOverride in request.Overrides ?? [])
        {
            values.Add(scheduleOverride.StartUtc.ToUniversalTime());
            values.Add(scheduleOverride.EndUtc.ToUniversalTime());
        }
        foreach (var candidate in values.Where(item => item > utc).Distinct().OrderBy(static item => item))
        {
            if (candidate >= preview.PreviewEndUtc)
            {
                return preview.PreviewEndUtc;
            }
            var candidateDecision = EvaluateAt(
                preview,
                request with { Utc = candidate },
                candidate,
                includeNextTransition: false);
            if (!Equivalent(current, candidateDecision))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool Equivalent(CaptureScheduleDecision left, CaptureScheduleDecision right)
        => left.Admitted == right.Admitted &&
           left.Reason == right.Reason &&
           string.Equals(left.SetpointProfileId, right.SetpointProfileId, StringComparison.Ordinal) &&
           string.Equals(left.Interval?.Id, right.Interval?.Id, StringComparison.Ordinal) &&
           left.Interval?.Source == right.Interval?.Source &&
           string.Equals(left.OverrideId, right.OverrideId, StringComparison.Ordinal) &&
           left.ConsumeOneShotOverride == right.ConsumeOneShotOverride;
}

internal static class CaptureScheduleValidator
{
    internal static void Validate(CaptureScheduleDefinition definition)
    {
        var result = CaptureScheduleContract.Validate(definition);
        if (!result.IsValid)
        {
            throw new CaptureScheduleValidationException(
                result.ReasonCode!,
                $"The capture schedule is invalid ({result.FieldPath}).");
        }
    }

}

internal static class DateTimeScheduleExtensions
{
    internal static DateOnly ToDateOnly(this DateTime value) => DateOnly.FromDateTime(value);
}
