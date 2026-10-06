using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

public enum SiteLocalBoundaryRole { Start, End }

/// <summary>Stable civil boundary resolution and canonical time-zone rule identity shared by both hosts.</summary>
public static class SiteTimeZone
{
    public static DateTimeOffset Resolve(
        DateOnly date,
        TimeOnly time,
        TimeZoneInfo timeZone,
        SiteLocalBoundaryRole role)
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
            offset = role == SiteLocalBoundaryRole.Start ? offsets.Max() : offsets.Min();
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
        => (Resolve(date, TimeOnly.MinValue, timeZone, SiteLocalBoundaryRole.Start),
            Resolve(date.AddDays(1), TimeOnly.MinValue, timeZone, SiteLocalBoundaryRole.Start));

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
