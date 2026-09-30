using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>One step placed on the dependency grid: its column is its rank and its row its order within that column.</summary>
internal sealed record PipelineGraphPlacement(
    CaptureProcessingPlanNode Node,
    int Rank,
    int Row,
    IReadOnlyList<string> Dependencies);

/// <summary>
/// The column layout both graph views draw. A step's rank is the longest dependency chain back to a source, so every
/// dependency sits in an earlier column; steps keep their configured order within a column. The raw frame and
/// dependencies on unknown steps do not add a column, and a cycle stops ranking rather than recursing forever.
/// </summary>
internal static class PipelineGraphLayout
{
    internal const string RawInput = "$raw";

    internal static IReadOnlyList<PipelineGraphPlacement> Arrange(IReadOnlyList<CaptureProcessingPlanNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var byId = new Dictionary<string, CaptureProcessingPlanNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            byId.TryAdd(node.Id, node);
        }
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Rank(string id, int depth)
        {
            if (ranks.TryGetValue(id, out var known))
            {
                return known;
            }
            if (depth > nodes.Count || !byId.TryGetValue(id, out var node))
            {
                return 0;
            }
            var rank = 0;
            foreach (var dependency in node.Dependencies ?? [])
            {
                if (byId.ContainsKey(dependency))
                {
                    rank = Math.Max(rank, Rank(dependency, depth + 1) + 1);
                }
            }
            ranks[id] = rank;
            return rank;
        }
        foreach (var node in nodes)
        {
            Rank(node.Id, 0);
        }
        var rows = new Dictionary<int, int>();
        var placements = new List<PipelineGraphPlacement>(nodes.Count);
        foreach (var node in nodes)
        {
            var rank = ranks.GetValueOrDefault(node.Id);
            var row = rows.GetValueOrDefault(rank);
            rows[rank] = row + 1;
            placements.Add(new PipelineGraphPlacement(node, rank, row, node.Dependencies ?? []));
        }
        return placements;
    }

    internal static bool IsRawInput(string dependency)
        => string.Equals(dependency, RawInput, StringComparison.OrdinalIgnoreCase);
}
