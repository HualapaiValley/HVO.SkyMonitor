// Differential oracle copied from reviewed head fc52a26f (F:850–886).
using System.Collections.Immutable;

using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.Processing.Tests.SpaceWeather;

/// <summary>Computes notice attention and replacement raised ranks without side effects.</summary>
internal static class OriginalAttentionEvaluator
{
    /// <summary>Evaluates touched groups in root-time order and returns events in ordinal group order.</summary>
    internal static SwpcAttentionResult Evaluate(in SwpcAttentionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var groups = input.Groups;
        var now = input.NowUtc;
        var touched = input.Touched;
        var raised = input.RaisedRanks.ToBuilder();
        var events = ImmutableArray.CreateBuilder<SwpcAttentionEvent>();
        int? Rank(SwpcNoticeGroup g)
        {
            var ranks = g.Nodes.Where(raised.ContainsKey).Select(n => raised[n]).ToArray();
            return ranks.Length == 0 ? null : ranks.Max();
        }
        foreach (var group in groups.Where(g => g.Root is not null && g.Family != SpaceWeatherFamily.Summary && g.Nodes.Overlaps(touched))
            .OrderBy(g => g.Root!.IssueMinuteUtc).ThenBy(g => g.Identity, StringComparer.Ordinal))
        {
            var rank = group.Level is null or "K4" ? 0 : group.Level[1] - '0';
            var prior = Rank(group);
            if (prior is null)
            {
                var state = OriginalNoticeLifecycle.Evaluate(group, groups, now).State;
                var eligible = state is SwpcNoticeState.Pending or SwpcNoticeState.Active
                    || state == SwpcNoticeState.Recent && now < group.LatestNonCancel!.IssueMinuteUtc.AddSeconds(SwpcLimits.NewGraceSeconds);
                var reason = "new";
                if (group.Family == SpaceWeatherFamily.Watch)
                {
                    var before = group.Root!.IssueMinuteUtc.AddSeconds(-1);
                    var predecessors = groups.Where(g => g.Family == SpaceWeatherFamily.Watch && g.Identity != group.Identity && g.Root is { } root
                        && root.IssueMinuteUtc < group.Root.IssueMinuteUtc && OriginalNoticeLifecycle.Evaluate(g, groups, before).State == SwpcNoticeState.Active)
                        .Select(Rank).Where(r => r.HasValue).Select(r => r!.Value).ToArray();
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
