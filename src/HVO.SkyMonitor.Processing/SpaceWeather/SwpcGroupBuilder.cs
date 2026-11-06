using System.Collections.Immutable;
using System.Globalization;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Builds code-local linked groups without changing or discarding versions.</summary>
public static class SwpcGroupBuilder
{
    /// <summary>Unions source links, including absent targets, and orders versions by issue, source text and ingest sequence.</summary>
    public static ImmutableArray<SwpcNoticeGroup> Build(IReadOnlyList<SwpcMessageVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var parent = new Dictionary<SwpcMessageNode, SwpcMessageNode>();
        var known = new HashSet<SwpcMessageNode>();
        SwpcMessageNode Find(SwpcMessageNode node)
        {
            parent.TryAdd(node, node);
            var root = node;
            while (parent[root] != root) root = parent[root];
            while (parent[node] != node)
            {
                var next = parent[node];
                parent[node] = root;
                node = next;
            }
            return root;
        }
        foreach (var version in versions)
        {
            var msg = version.Message;
            known.Add(msg.Node);
            var a = Find(msg.Node);
            if (msg.Header.Link is not { } link) continue;
            var b = Find(new(msg.Code, link.TargetSerial));
            // A code-local group root is its lowest serial, even if that serial has not arrived.
            if (a != b) parent[a.Serial > b.Serial ? a : b] = a.Serial > b.Serial ? b : a;
        }
        var nodes = parent.Keys.ToArray().GroupBy(Find).ToDictionary(g => g.Key, g => g.ToImmutableHashSet());
        return [.. versions.GroupBy(v => Find(v.Message.Node)).Select(g =>
        {
            var members = nodes[g.Key];
            return new SwpcNoticeGroup(g.Key.Code + "/" + g.Key.Serial.ToString(CultureInfo.InvariantCulture), members,
                [.. g.OrderBy(v => v.Message.IssueMinuteUtc).ThenBy(v => v.Message.IssueDatetimeText, StringComparer.Ordinal).ThenBy(v => v.IngestSequence)],
                members.Any(n => !known.Contains(n)));
        }).OrderBy(g => g.Identity, StringComparer.Ordinal)];
    }

    /// <summary>Returns the acknowledgement on the last acknowledged node in code/serial order.</summary>
    public static SwpcAcknowledgement? Acknowledgement(SwpcNoticeGroup group, ImmutableDictionary<SwpcMessageNode, SwpcAcknowledgement> acknowledgements)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(acknowledgements);
        return group.Nodes.OrderBy(n => n.Code, StringComparer.Ordinal).ThenBy(n => n.Serial)
            .Where(acknowledgements.ContainsKey).Select(n => acknowledgements[n]).LastOrDefault();
    }
}
