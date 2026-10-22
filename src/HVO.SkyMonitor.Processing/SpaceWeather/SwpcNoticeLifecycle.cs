using System.Collections.Immutable;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Derives half-open windows, ordered adjustments and lifecycle states.</summary>
public static class SwpcNoticeLifecycle
{
    /// <summary>Evaluates a group in the complete watch context at an explicit read time.</summary>
    public static SwpcNoticeWindow Evaluate(SwpcNoticeGroup group, IReadOnlyList<SwpcNoticeGroup> groups, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(groups);
        return Compute(group, NextWatch(group, Watches(groups))).At(nowUtc);
    }

    /// <summary>Evaluates every group, in order, exactly as <see cref="Evaluate"/> does, collecting the watch context once.</summary>
    public static ImmutableArray<SwpcNoticeWindow> EvaluateAll(IReadOnlyList<SwpcNoticeGroup> groups, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return [.. Prepare(groups).Select(facts => facts.At(nowUtc))];
    }

    // Window bounds and adjustments do not depend on the read instant. Attention
    // prepares each exactly once, then asks only the cheap state function below.
    internal static WindowFacts[] Prepare(IReadOnlyList<SwpcNoticeGroup> groups)
    {
        var watches = Watches(groups);
        return groups.Select(group => Compute(group, NextWatch(group, watches))).ToArray();
    }

    private static (string Identity, DateTimeOffset Issue)[] Watches(IReadOnlyList<SwpcNoticeGroup> groups)
        => groups.Where(g => g.Family == SpaceWeatherFamily.Watch && g.Root is not null)
            .Select(g => (g.Identity, g.Root!.IssueMinuteUtc)).OrderBy(w => w.IssueMinuteUtc).ToArray();

    private static DateTimeOffset? NextWatch(SwpcNoticeGroup group, (string Identity, DateTimeOffset Issue)[] watches)
    {
        if (group.Family != SpaceWeatherFamily.Watch || group.Root is not { } root) return null;
        foreach (var watch in watches)
            if (watch.Issue > root.IssueMinuteUtc && watch.Identity != group.Identity) return watch.Issue;
        return null;
    }

    private static WindowFacts Compute(SwpcNoticeGroup group, DateTimeOffset? laterWatch)
    {
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
                var period = SwpcText.Match(nc.Header.SynopticPeriod ?? "", @"\A([0-9]{2})([0-9]{2})-([0-9]{2})([0-9]{2})\z");
                if (start.HasValue && period.Success)
                {
                    until = new DateTimeOffset(start.Value.UtcDateTime.Date, TimeSpan.Zero).AddMinutes(SwpcText.Number(period.Groups[3].Value) * 60 + SwpcText.Number(period.Groups[4].Value));
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
        if (laterWatch is { } later && later < until) { until = later; reason = SwpcNoticeEndReason.Superseded; }

        foreach (var cancel in group.Versions.Where(v => v.Message.Header.Modifier == "CANCEL").OrderBy(v => v.Message.IssueMinuteUtc))
            if (cancel.Message.IssueMinuteUtc < until) { until = cancel.Message.IssueMinuteUtc; reason = SwpcNoticeEndReason.Cancelled; }
        var cap = nc.IssueMinuteUtc.AddHours(SwpcLimits.ActiveCapHours);
        if (until > cap) { until = cap; reason = SwpcNoticeEndReason.Capped; }
        return Result(start!.Value, until!.Value, reason, fallback);

        WindowFacts Result(DateTimeOffset from, DateTimeOffset to, SwpcNoticeEndReason? end, bool usedFallback)
            => new(from, to, end, usedFallback, group.Family == SpaceWeatherFamily.Summary && nc is not null);
    }

    internal sealed record WindowFacts(DateTimeOffset From, DateTimeOffset Until, SwpcNoticeEndReason? Reason, bool Fallback, bool Summary)
    {
        internal SwpcNoticeState StateAt(DateTimeOffset nowUtc)
            => Summary ? nowUtc < Until.AddHours(SwpcLimits.RecentHours) ? SwpcNoticeState.Recent : SwpcNoticeState.History
                : nowUtc >= Until ? nowUtc < Until.AddHours(SwpcLimits.RecentHours) ? SwpcNoticeState.Recent : SwpcNoticeState.History
                : nowUtc < From ? SwpcNoticeState.Pending : SwpcNoticeState.Active;

        internal SwpcNoticeWindow At(DateTimeOffset nowUtc)
        {
            var state = StateAt(nowUtc);
            return new(From, Until, Until.AddHours(SwpcLimits.RecentHours), state,
                state is SwpcNoticeState.Recent or SwpcNoticeState.History ? Reason : null, Reason, Fallback);
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
