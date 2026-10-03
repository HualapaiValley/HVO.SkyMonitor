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
