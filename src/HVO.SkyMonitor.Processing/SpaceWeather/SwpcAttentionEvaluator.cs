using System.Collections.Immutable;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Computes notice attention and replacement raised ranks without side effects.</summary>
public static class SwpcAttentionEvaluator
{
    /// <summary>Evaluates touched groups in root-time order and returns events in ordinal group order.</summary>
    public static SwpcAttentionResult Evaluate(in SwpcAttentionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var groups = input.Groups;
        var now = input.NowUtc;
        var windows = SwpcNoticeLifecycle.Prepare(groups);
        var roots = groups.Select(g => g.Root).ToArray();
        var families = groups.Select(g => g.Family).ToArray();
        var touched = input.Touched;
        var raised = input.RaisedRanks.ToBuilder();
        var events = ImmutableArray.CreateBuilder<SwpcAttentionEvent>();
        int? Rank(SwpcNoticeGroup g)
        {
            int? maximum = null;
            foreach (var node in g.Nodes)
                if (raised.TryGetValue(node, out var rank) && (maximum is null || rank > maximum)) maximum = rank;
            return maximum;
        }
        foreach (var index in Enumerable.Range(0, groups.Length)
            .Where(i => roots[i] is not null && families[i] != SpaceWeatherFamily.Summary && groups[i].Nodes.Overlaps(touched))
            .OrderBy(i => roots[i]!.IssueMinuteUtc).ThenBy(i => groups[i].Identity, StringComparer.Ordinal))
        {
            var group = groups[index];
            var rank = group.Level is null or "K4" ? 0 : group.Level[1] - '0';
            var prior = Rank(group);
            if (prior is null)
            {
                var state = windows[index].StateAt(now);
                var eligible = state is SwpcNoticeState.Pending or SwpcNoticeState.Active
                    || state == SwpcNoticeState.Recent && now < group.LatestNonCancel!.IssueMinuteUtc.AddSeconds(SwpcLimits.NewGraceSeconds);
                var reason = "new";
                if (families[index] == SpaceWeatherFamily.Watch)
                {
                    var rootIssue = roots[index]!.IssueMinuteUtc;
                    var before = rootIssue.AddSeconds(-1);
                    var predecessors = Enumerable.Range(0, groups.Length).Where(i => families[i] == SpaceWeatherFamily.Watch && groups[i].Identity != group.Identity && roots[i] is { } root
                        && root.IssueMinuteUtc < rootIssue && windows[i].StateAt(before) == SwpcNoticeState.Active)
                        .Select(i => Rank(groups[i])).Where(r => r.HasValue).Select(r => r!.Value).ToArray();
                    if (predecessors.Length > 0 && rank <= predecessors.Max()) { raised[group.CanonicalNode] = rank; continue; }
                    if (predecessors.Length > 0) reason = "escalated";
                }
                if (!eligible) continue;
                raised[group.CanonicalNode] = rank;
                events.Add(new(group.Identity, reason, group.Level));
            }
            else if (rank > prior)
            {
                raised[group.CanonicalNode] = rank;
                events.Add(new(group.Identity, "escalated", group.Level));
            }
        }
        return new([.. events.OrderBy(e => e.Group, StringComparer.Ordinal)], raised.ToImmutable());
    }
}
