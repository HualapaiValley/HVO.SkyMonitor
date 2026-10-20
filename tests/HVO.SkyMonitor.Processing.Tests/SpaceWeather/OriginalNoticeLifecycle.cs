// Differential oracle copied from reviewed head fc52a26f (F:472–536).
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.Processing.Tests.SpaceWeather;

/// <summary>Derives half-open windows, ordered adjustments and lifecycle states.</summary>
internal static class OriginalNoticeLifecycle
{
    /// <summary>Evaluates a group in the complete watch context at an explicit read time.</summary>
    public static SwpcNoticeWindow Evaluate(SwpcNoticeGroup group, IReadOnlyList<SwpcNoticeGroup> groups, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(groups);
        var root = group.Root;
        var nc = group.LatestNonCancel;
        if (root is null || nc is null) return Result(group.Latest.IssueMinuteUtc, group.Latest.IssueMinuteUtc, SwpcNoticeEndReason.Cancelled, false);
        if (group.Family == SpaceWeatherFamily.Summary) return Result(root.IssueMinuteUtc, root.IssueMinuteUtc, null, false);
        DateTimeOffset? start = null, until = null;
        switch (group.Family)
        {
            case SpaceWeatherFamily.Watch:
                start = root.IssueMinuteUtc;
                if (nc.Header.Days is { Length: > 0 } days)
                {
                    var activeDays = days.Where(d => d.Level != "None").Select(d => d.DateUtc).ToArray();
                    until = activeDays.Length == 0 ? start : new DateTimeOffset(activeDays.Max().ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1);
                }
                break;
            case SpaceWeatherFamily.Warning:
                start = Time(root, "Valid From");
                until = Time(nc, "Now Valid Until") ?? Time(nc, "Valid To");
                break;
            case SpaceWeatherFamily.KAlert:
                start = Time(nc, "Threshold Reached");
                var period = System.Text.RegularExpressions.Regex.Match(nc.Header.SynopticPeriod ?? "", @"\A([0-9]{2})([0-9]{2})-([0-9]{2})([0-9]{2})\z");
                if (start.HasValue && period.Success)
                {
                    until = new DateTimeOffset(start.Value.UtcDateTime.Date, TimeSpan.Zero).AddMinutes(int.Parse(period.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) * 60 + int.Parse(period.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture));
                    if (until <= start) until = null;
                }
                break;
            case SpaceWeatherFamily.XrayAlert:
                start = Time(nc, "Threshold Reached");
                until = start?.AddHours(3);
                break;
            case SpaceWeatherFamily.RadioSweep:
                start = Time(nc, "Begin Time");
                until = start?.AddHours(1);
                break;
            case SpaceWeatherFamily.FluxAlert:
                start = Time(root, "Threshold Reached") ?? Time(root, "Begin Time");
                until = nc.IssueMinuteUtc.AddHours(30);
                break;
            case SpaceWeatherFamily.Unclassified:
                start = root.IssueMinuteUtc;
                until = start.Value.AddHours(6);
                break;
        }
        var fallback = !start.HasValue || !until.HasValue;
        if (fallback) { start = root.IssueMinuteUtc; until = start.Value.AddHours(6); }
        var reason = SwpcNoticeEndReason.Expired;
        if (group.Family == SpaceWeatherFamily.Watch)
        {
            var later = groups.Where(g => g.Identity != group.Identity && g.Family == SpaceWeatherFamily.Watch && g.Root is { } r && r.IssueMinuteUtc > root.IssueMinuteUtc)
                .Select(g => g.Root!.IssueMinuteUtc).Order().FirstOrDefault();
            if (later != default && later < until) { until = later; reason = SwpcNoticeEndReason.Superseded; }
        }
        foreach (var cancel in group.Versions.Where(v => v.Message.Header.Modifier == "CANCEL").OrderBy(v => v.Message.IssueMinuteUtc))
            if (cancel.Message.IssueMinuteUtc < until) { until = cancel.Message.IssueMinuteUtc; reason = SwpcNoticeEndReason.Cancelled; }
        var cap = nc.IssueMinuteUtc.AddHours(SwpcLimits.ActiveCapHours);
        if (until > cap) { until = cap; reason = SwpcNoticeEndReason.Capped; }
        return Result(start!.Value, until!.Value, reason, fallback);

        SwpcNoticeWindow Result(DateTimeOffset from, DateTimeOffset to, SwpcNoticeEndReason? end, bool usedFallback)
        {
            var recentUntil = to.AddHours(SwpcLimits.RecentHours);
            var state = group.Family == SpaceWeatherFamily.Summary && nc is not null
                ? nowUtc < recentUntil ? SwpcNoticeState.Recent : SwpcNoticeState.History
                : nowUtc >= to ? nowUtc < recentUntil ? SwpcNoticeState.Recent : SwpcNoticeState.History
                : nowUtc < from ? SwpcNoticeState.Pending : SwpcNoticeState.Active;
            return new(from, to, recentUntil, state,
                state is SwpcNoticeState.Recent or SwpcNoticeState.History ? end : null, end, usedFallback);
        }
    }

    /// <summary>Reports whether the retention boundary has been reached, without deleting anything.</summary>
    public static bool IsRetentionExpired(SwpcNoticeWindow window, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(window);
        return nowUtc >= window.UntilUtc.AddDays(SwpcLimits.RetentionDays);
    }

    private static DateTimeOffset? Time(SwpcMessageRecord msg, string label) => msg.Header.Times.GetValueOrDefault(label);
}
