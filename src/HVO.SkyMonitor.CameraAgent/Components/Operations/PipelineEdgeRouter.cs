using System.Globalization;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>A point in a graph's SVG user units.</summary>
internal readonly record struct GraphPoint(double X, double Y);

/// <summary>One column or row of a graph grid: where it starts and how far it extends, in SVG user units.</summary>
internal readonly record struct GraphTrack(double Start, double Size)
{
    internal double End => Start + Size;

    internal double Middle => Start + (Size / 2);
}

internal readonly record struct GraphConnection(int SourceColumn, int SourceRow, int TargetColumn, int TargetRow);

/// <summary>
/// Routes dependency edges across a grid of boxes so that no edge passes through a box. An edge leaves the middle of
/// its source's right side and enters the middle of its target's left side. Between adjacent columns it is a single
/// S-curve inside the column gap. A longer edge turns into a horizontal channel in the gap after its source, runs along
/// the channel past every column in between, and turns to its target in the gap before it. The channel is the middle
/// of a row that is empty in every column it passes, or the gutter between two rows, whichever keeps the edge closest
/// to its ends; a row is added below the grid only when neither exists. Every segment is a cubic whose tangents are
/// horizontal at both ends, so it stays inside the rectangle its two anchors span, and none of those rectangles
/// overlaps a box. Edges from one source that share a channel share its lane; other edges take their own lane.
/// </summary>
internal sealed class PipelineEdgeRouter
{
    private const int LanesPerChannel = 5;
    internal const double SeparatedLaneSpacing = 8;
    private const double ChannelPadding = 8;

    private readonly IReadOnlyList<GraphTrack> _columns;
    private readonly List<GraphTrack> _rows;
    private readonly double _rowGap;
    private readonly HashSet<(int Column, int Row)> _occupied;
    private readonly Dictionary<double, List<(int Column, int Row)>> _lanes = [];

    internal PipelineEdgeRouter(
        IReadOnlyList<GraphTrack> columns, IReadOnlyList<GraphTrack> rows, double rowGap, IEnumerable<(int Column, int Row)> occupied)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(occupied);
        ArgumentOutOfRangeException.ThrowIfZero(rows.Count);
        _columns = columns;
        _rows = [.. rows];
        _rowGap = rowGap;
        _occupied = [.. occupied];
    }

    /// <summary>The grid's rows, including the row added below it when an edge found no other channel.</summary>
    internal IReadOnlyList<GraphTrack> Rows => _rows;

    /// <summary>
    /// Routes a complete frozen edge set with individual card ports and distinct horizontal/vertical lanes.
    /// Callers reserve eight units per long-edge turn in each column gap; no lane wraps or source trunk is reused.
    /// Perpendicular crossings are points, never shared line segments.
    /// </summary>
    internal GraphPoint[][] RouteSeparated(IReadOnlyList<GraphConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var outgoing = connections.GroupBy(static edge => (edge.SourceColumn, edge.SourceRow))
            .GroupBy(static group => group.Key.SourceRow).ToDictionary(static group => group.Key, static group => group.Max(static ports => ports.Count()));
        var incoming = connections.GroupBy(static edge => (edge.TargetColumn, edge.TargetRow))
            .GroupBy(static group => group.Key.TargetRow).ToDictionary(static group => group.Key, static group => group.Max(static ports => ports.Count()));
        foreach (var row in outgoing.Keys.Concat(incoming.Keys).Distinct())
            if ((outgoing.GetValueOrDefault(row) + incoming.GetValueOrDefault(row) - 1) * SeparatedLaneSpacing + ChannelPadding * 2 > _rows[row].Size)
                throw new ArgumentException("Node row does not contain the reserved separated edge ports.", nameof(connections));
        var sourcePorts = Ports(connections, source: true);
        var targetPorts = Ports(connections, source: false);
        var gapUses = new Dictionary<int, List<(int Edge, bool Source)>>();
        for (var index = 0; index < connections.Count; index++)
        {
            var edge = connections[index];
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(edge.TargetColumn, edge.SourceColumn);
            if (edge.TargetColumn == edge.SourceColumn + 1) continue;
            AddGap(edge.SourceColumn, index, true);
            AddGap(edge.TargetColumn - 1, index, false);
        }
        void AddGap(int gap, int index, bool source)
        {
            if (!gapUses.TryGetValue(gap, out var uses)) gapUses[gap] = uses = [];
            uses.Add((index, source));
        }
        var turns = new Dictionary<(int Edge, bool Source), double>();
        foreach (var (gap, uses) in gapUses)
        {
            var left = _columns[gap].End;
            var right = _columns[gap + 1].Start;
            var needed = (uses.Count - 1) * SeparatedLaneSpacing + ChannelPadding * 2;
            if (right - left < needed)
                throw new ArgumentException("Column gap does not contain the reserved separated edge lanes.", nameof(connections));
            var start = (left + right - (uses.Count - 1) * SeparatedLaneSpacing) / 2;
            for (var slot = 0; slot < uses.Count; slot++)
                turns[(uses[slot].Edge, uses[slot].Source)] = start + slot * SeparatedLaneSpacing;
        }
        var horizontalLanes = new List<double>();
        // A long horizontal channel must not reuse a card's departure/arrival stub height either.
        var portHeights = sourcePorts.Concat(targetPorts).Distinct().ToArray();
        var routes = new GraphPoint[connections.Count][];
        for (var index = 0; index < connections.Count; index++)
        {
            var edge = connections[index];
            var start = new GraphPoint(_columns[edge.SourceColumn].End, sourcePorts[index]);
            var end = new GraphPoint(_columns[edge.TargetColumn].Start, targetPorts[index]);
            if (edge.TargetColumn == edge.SourceColumn + 1)
            {
                routes[index] = [start, end];
                continue;
            }
            var channel = SeparatedChannel(edge.SourceColumn + 1, edge.TargetColumn - 1, start.Y, end.Y, horizontalLanes, portHeights);
            horizontalLanes.Add(channel);
            var departure = turns[(index, true)];
            var arrival = turns[(index, false)];
            routes[index] = [start, new(departure, start.Y), new(departure, channel),
                new(arrival, channel), new(arrival, end.Y), end];
        }
        return routes;
    }

    private double[] Ports(IReadOnlyList<GraphConnection> connections, bool source)
    {
        var values = new double[connections.Count];
        var groups = Enumerable.Range(0, connections.Count).GroupBy(index => source
            ? (connections[index].SourceColumn, connections[index].SourceRow)
            : (connections[index].TargetColumn, connections[index].TargetRow));
        foreach (var group in groups)
        {
            var indexes = group.ToArray();
            var row = _rows[group.Key.Item2];
            // Outgoing ports grow down from the top and incoming ports up from the bottom.
            // The complete endpoint count reserves enough space to keep the two sets disjoint.
            for (var slot = 0; slot < indexes.Length; slot++)
                values[indexes[slot]] = source ? row.Start + ChannelPadding + slot * SeparatedLaneSpacing
                    : row.End - ChannelPadding - slot * SeparatedLaneSpacing;
        }
        return values;
    }

    private double SeparatedChannel(int first, int last, double from, double to, IReadOnlyList<double> used, double[] ports)
    {
        (double Y, double Cost)? best = null;
        void ConsiderBand(double top, double bottom)
        {
            for (var y = top + ChannelPadding; y <= bottom - ChannelPadding; y += SeparatedLaneSpacing)
            {
                if (used.Any(existing => Math.Abs(existing - y) < SeparatedLaneSpacing)) continue;
                if (ports.Any(port => Math.Abs(port - y) < SeparatedLaneSpacing)) continue;
                var cost = Math.Abs(y - from) + Math.Abs(y - to);
                if (best is null || cost < best.Value.Cost) best = (y, cost);
            }
        }
        for (var row = 0; row < _rows.Count; row++)
        {
            if (Free(row, first, last)) ConsiderBand(_rows[row].Start, _rows[row].End);
            if (row > 0) ConsiderBand(_rows[row - 1].End, _rows[row].Start);
        }
        if (best is not null) return best.Value.Y;
        var added = new GraphTrack(_rows[^1].End + _rowGap, Math.Max(_rowGap, SeparatedLaneSpacing * 4));
        _rows.Add(added);
        ConsiderBand(added.Start, added.End);
        return best!.Value.Y;
    }

    /// <summary>Separated routes keep each turn on its reserved vertical track.</summary>
    internal static string SeparatedPath(IReadOnlyList<GraphPoint> points)
    {
        if (points.Count == 2) return Path(points);
        var path = new StringBuilder();
        path.Append(CultureInfo.InvariantCulture, $"M{Unit(points[0].X)} {Unit(points[0].Y)}");
        foreach (var point in points.Skip(1))
            path.Append(CultureInfo.InvariantCulture, $" L{Unit(point.X)} {Unit(point.Y)}");
        return path.ToString();
    }

    /// <summary>The anchors of one edge, from the source's right side to the target's left side.</summary>
    internal IReadOnlyList<GraphPoint> Route(int sourceColumn, int sourceRow, int targetColumn, int targetRow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetColumn, sourceColumn);
        var start = new GraphPoint(_columns[sourceColumn].End, _rows[sourceRow].Middle);
        var end = new GraphPoint(_columns[targetColumn].Start, _rows[targetRow].Middle);
        if (targetColumn == sourceColumn + 1)
        {
            return [start, end];
        }
        var channel = Channel(sourceColumn + 1, targetColumn - 1, start.Y, end.Y, (sourceColumn, sourceRow));
        return [start, new(_columns[sourceColumn + 1].Start, channel), new(_columns[targetColumn - 1].End, channel), end];
    }

    /// <summary>The SVG path for a route: horizontal runs as lines, every turn as an S-curve.</summary>
    internal static string Path(IReadOnlyList<GraphPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThan(points.Count, 2);
        var path = new StringBuilder();
        path.Append(CultureInfo.InvariantCulture, $"M{Unit(points[0].X)} {Unit(points[0].Y)}");
        for (var index = 1; index < points.Count; index++)
        {
            var from = points[index - 1];
            var to = points[index];
            if (from.Y.Equals(to.Y))
            {
                path.Append(CultureInfo.InvariantCulture, $" H{Unit(to.X)}");
                continue;
            }
            var middle = Unit((from.X + to.X) / 2);
            path.Append(CultureInfo.InvariantCulture,
                $" C{middle} {Unit(from.Y)} {middle} {Unit(to.Y)} {Unit(to.X)} {Unit(to.Y)}");
        }
        return path.ToString();
    }

    private double Channel(int first, int last, double from, double to, (int Column, int Row) source)
    {
        (double Y, double Half, double Cost)? best = null;
        void Consider(double y, double half, double cost)
        {
            if (best is null || cost < best.Value.Cost)
            {
                best = (y, half, cost);
            }
        }
        for (var row = 0; row < _rows.Count; row++)
        {
            if (Free(row, first, last))
            {
                var y = _rows[row].Middle;
                Consider(y, _rows[row].Size / 2, Math.Abs(y - from) + Math.Abs(y - to));
            }
        }
        for (var row = 1; row < _rows.Count; row++)
        {
            // A gutter is as narrow as the row gap, so a free row wins a tie.
            var top = _rows[row - 1].End;
            var bottom = _rows[row].Start;
            var y = (top + bottom) / 2;
            Consider(y, (bottom - top) / 2, Math.Abs(y - from) + Math.Abs(y - to) + (bottom - top));
        }
        if (best is null)
        {
            var added = new GraphTrack(_rows[^1].End + _rowGap, _rowGap);
            _rows.Add(added);
            best = (added.Middle, added.Size / 2, 0);
        }
        return Lane(best.Value.Y, best.Value.Half, source);
    }

    private bool Free(int row, int first, int last)
    {
        for (var column = first; column <= last; column++)
        {
            if (_occupied.Contains((column, row)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Lanes alternate about the channel's middle (0, -1, +1, -2, +2 steps) and stay inside its band.</summary>
    private double Lane(double y, double half, (int Column, int Row) source)
    {
        if (!_lanes.TryGetValue(y, out var sources))
        {
            _lanes[y] = sources = [];
        }
        var index = sources.IndexOf(source);
        if (index < 0)
        {
            index = sources.Count;
            sources.Add(source);
        }
        var slot = index % LanesPerChannel;
        var step = half / 3;
        var offset = ((slot + 1) / 2) * step;
        return slot % 2 == 1 ? y - offset : y + offset;
    }

    private static string Unit(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
