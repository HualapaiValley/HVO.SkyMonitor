using HVO.SkyMonitor.CameraAgent.Components.Operations;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class PipelineEdgeRouterTests
{
    private const double Width = 160;
    private const double Gap = 40;
    private const double Height = 68;
    private const double RowGap = 16;

    [TestMethod]
    public void Route_BetweenAdjacentColumns_IsOneCurveFromSideToSide()
    {
        var router = Router(columns: 2, rows: 2, (0, 0), (1, 1));

        var route = router.Route(0, 0, 1, 1);

        CollectionAssert.AreEqual(new[] { new GraphPoint(160, 34), new GraphPoint(200, 118) }, route.ToArray());
        Assert.AreEqual("M160 34 C180 34 180 118 200 118", PipelineEdgeRouter.Path(route));
    }

    [TestMethod]
    public void Route_PastABoxInTheSameRow_RunsAlongTheFreeRowBelowIt()
    {
        // The profile pipeline's Calibrated Preview (column 1, row 1) feeds Local Storage (column 4, row 0) while
        // Preview and Annotation fill row 0 between them.
        var router = Router(columns: 5, rows: 2, (0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (1, 1));

        var route = router.Route(1, 1, 4, 0);

        CollectionAssert.AreEqual(
            new[] { new GraphPoint(360, 118), new GraphPoint(400, 118), new GraphPoint(760, 118), new GraphPoint(800, 34) },
            route.ToArray());
        Assert.AreEqual("M360 118 H400 H760 C780 118 780 34 800 34", PipelineEdgeRouter.Path(route));
        AssertAvoidsBoxes(router, route, [(0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (1, 1)]);
    }

    [TestMethod]
    public void Route_WhenEveryRowIsBlocked_UsesTheGutterBetweenRows()
    {
        var router = Router(columns: 3, rows: 2, (0, 0), (1, 0), (1, 1), (2, 0));

        var route = router.Route(0, 0, 2, 0);

        Assert.AreEqual(76, route[1].Y, "The gutter between rows 0 and 1 is 68..84.");
        Assert.AreEqual(route[1].Y, route[2].Y);
        Assert.HasCount(2, router.Rows);
        AssertAvoidsBoxes(router, route, [(0, 0), (1, 0), (1, 1), (2, 0)]);
    }

    [TestMethod]
    public void Route_WithNoRowOrGutter_AddsARowBelowTheGrid()
    {
        var router = Router(columns: 3, rows: 1, (0, 0), (1, 0), (2, 0));

        var route = router.Route(0, 0, 2, 0);

        Assert.HasCount(2, router.Rows);
        Assert.AreEqual(new GraphTrack(84, 16), router.Rows[1]);
        Assert.AreEqual(92, route[1].Y);
        AssertAvoidsBoxes(router, route, [(0, 0), (1, 0), (2, 0)]);
    }

    [TestMethod]
    public void Route_EdgesFromOneSourceShareALaneAndOthersTakeTheirOwn()
    {
        var router = Router(columns: 5, rows: 2, (0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (0, 1));

        var first = router.Route(0, 0, 3, 0);
        var sibling = router.Route(0, 0, 4, 0);
        var other = router.Route(1, 0, 4, 0);

        Assert.AreEqual(76, first[1].Y, "Both rows are blocked, so the edge takes the gutter at 68..84.");
        Assert.AreEqual(first[1].Y, sibling[1].Y);
        Assert.AreNotEqual(first[1].Y, other[1].Y);
        Assert.IsTrue(other[1].Y > 68 && other[1].Y < 84, "Every lane stays inside the gutter.");
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5394:Do not use insecure randomness",
        Justification = "A seeded generator makes the generated graphs reproducible; it is not a security input.")]
    public void Route_AcrossGeneratedGraphs_NeverEntersABox()
    {
        for (var seed = 1; seed <= 200; seed++)
        {
            var random = new Random(seed);
            var columns = random.Next(3, 8);
            var rows = random.Next(1, 5);
            var occupied = new List<(int Column, int Row)>();
            for (var column = 0; column < columns; column++)
            {
                for (var row = 0; row < rows; row++)
                {
                    if (row == 0 || random.NextDouble() < 0.55)
                    {
                        occupied.Add((column, row));
                    }
                }
            }
            var router = Router(columns, rows, [.. occupied]);
            for (var edge = 0; edge < 12; edge++)
            {
                var source = occupied[random.Next(occupied.Count)];
                var targets = occupied.Where(cell => cell.Column > source.Column).ToArray();
                if (targets.Length == 0)
                {
                    continue;
                }
                var target = targets[random.Next(targets.Length)];
                var route = router.Route(source.Column, source.Row, target.Column, target.Row);
                AssertAvoidsBoxes(router, route, [.. occupied], $"seed {seed}");
            }
        }
    }

    [TestMethod]
    public void Route_Backwards_IsRejected()
    {
        var router = Router(columns: 2, rows: 1, (0, 0), (1, 0));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => router.Route(1, 0, 1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => router.Route(1, 0, 0, 0));
    }

    [TestMethod]
    public void SeparatedRoutes_ReserveDistinctChannelsPortsAndTurnsWithoutLaneWrapping()
    {
        const double separatedWidth = 204;
        const double separatedGap = 160;
        var columns = Enumerable.Range(0, 7).Select(index => new GraphTrack(index * (separatedWidth + separatedGap), separatedWidth)).ToArray();
        var rows = new[] { new GraphTrack(0, 120), new GraphTrack(160, 120) };
        var occupied = Enumerable.Range(0, 7).Select(static column => (column, 0)).ToArray();
        var router = new PipelineEdgeRouter(columns, rows, 40, occupied);
        var connections = Enumerable.Range(2, 5).Select(static column => new GraphConnection(0, 0, column, 0))
            .Concat(Enumerable.Range(2, 4).Select(static column => new GraphConnection(1, 0, column + 1, 0))).ToArray();

        var routes = router.RouteSeparated(connections);

        Assert.AreEqual(connections.Length, routes.Length);
        Assert.AreEqual(routes.Length, routes.Select(static route => route[2].Y).Distinct().Count(),
            "Every long edge gets its own horizontal lane, including more than five sources/targets.");
        Assert.AreEqual(5, routes.Take(5).Select(static route => route[0].Y).Distinct().Count());
        Assert.AreEqual(5, routes.Take(5).Select(static route => route[1].X).Distinct().Count());
        foreach (var route in routes)
        {
            Assert.IsTrue(routes.Where(other => other != route).All(other =>
                Math.Abs(other[2].Y - route[2].Y) >= PipelineEdgeRouter.SeparatedLaneSpacing));
            foreach (var other in routes)
            {
                Assert.IsTrue(Math.Abs(other[0].Y - route[2].Y) >= PipelineEdgeRouter.SeparatedLaneSpacing);
                Assert.IsTrue(Math.Abs(other[^1].Y - route[2].Y) >= PipelineEdgeRouter.SeparatedLaneSpacing);
            }
            for (var segment = 1; segment < route.Length; segment++)
                foreach (var (column, row) in occupied)
                {
                    var left = Math.Min(route[segment - 1].X, route[segment].X);
                    var right = Math.Max(route[segment - 1].X, route[segment].X);
                    var top = Math.Min(route[segment - 1].Y, route[segment].Y);
                    var bottom = Math.Max(route[segment - 1].Y, route[segment].Y);
                    Assert.IsFalse(left < columns[column].End && right > columns[column].Start &&
                        top < rows[row].End && bottom > rows[row].Start, "Separated route entered an occupied card.");
                }
        }
        Assert.AreEqual(routes.Length, routes.Select(PipelineEdgeRouter.SeparatedPath).Distinct().Count());
        var segments = routes.SelectMany((route, edge) => route.Zip(route.Skip(1), (start, end) =>
            (Edge: edge, Start: start, End: end))).ToArray();
        foreach (var first in segments)
            foreach (var second in segments.Where(segment => segment.Edge > first.Edge))
            {
                var horizontal = first.Start.Y == first.End.Y && second.Start.Y == second.End.Y;
                var vertical = first.Start.X == first.End.X && second.Start.X == second.End.X;
                if (!horizontal && !vertical) continue;
                var firstAt = horizontal ? first.Start.Y : first.Start.X;
                var secondAt = horizontal ? second.Start.Y : second.Start.X;
                var firstStart = horizontal ? Math.Min(first.Start.X, first.End.X) : Math.Min(first.Start.Y, first.End.Y);
                var firstEnd = horizontal ? Math.Max(first.Start.X, first.End.X) : Math.Max(first.Start.Y, first.End.Y);
                var secondStart = horizontal ? Math.Min(second.Start.X, second.End.X) : Math.Min(second.Start.Y, second.End.Y);
                var secondEnd = horizontal ? Math.Max(second.Start.X, second.End.X) : Math.Max(second.Start.Y, second.End.Y);
                if (Math.Min(firstEnd, secondEnd) <= Math.Max(firstStart, secondStart)) continue;
                Assert.IsTrue(Math.Abs(firstAt - secondAt) >= PipelineEdgeRouter.SeparatedLaneSpacing,
                    "Overlapping horizontal or vertical extents must occupy visibly separate lanes.");
            }
    }

    [TestMethod]
    public void SeparatedRoutes_RejectInsufficientPortAndTurnSpaceInsteadOfOverlapping()
    {
        var connections = Enumerable.Range(0, 12).Select(static _ => new GraphConnection(0, 0, 2, 0)).ToArray();
        var router = Router(3, 1, (0, 0), (1, 0), (2, 0));
        Assert.ThrowsExactly<ArgumentException>(() => router.RouteSeparated(connections));
        var roomyPorts = new PipelineEdgeRouter([new(0, 160), new(200, 160), new(400, 160)], [new(0, 200)], 32, [(0, 0), (1, 0), (2, 0)]);
        Assert.ThrowsExactly<ArgumentException>(() => roomyPorts.RouteSeparated(connections));
    }

    private static PipelineEdgeRouter Router(int columns, int rows, params (int Column, int Row)[] occupied)
        => new(
            Enumerable.Range(0, columns).Select(static column => new GraphTrack(column * (Width + Gap), Width)).ToArray(),
            Enumerable.Range(0, rows).Select(static row => new GraphTrack(row * (Height + RowGap), Height)).ToArray(),
            RowGap,
            occupied);

    /// <summary>
    /// Every segment is drawn inside the rectangle its two anchors span, so a route clears the boxes when none of those
    /// rectangles overlaps a box's interior. Touching a box's side is where an edge starts or ends.
    /// </summary>
    private static void AssertAvoidsBoxes(
        PipelineEdgeRouter router, IReadOnlyList<GraphPoint> route, (int Column, int Row)[] occupied, string context = "")
    {
        for (var index = 1; index < route.Count; index++)
        {
            var left = Math.Min(route[index - 1].X, route[index].X);
            var right = Math.Max(route[index - 1].X, route[index].X);
            var top = Math.Min(route[index - 1].Y, route[index].Y);
            var bottom = Math.Max(route[index - 1].Y, route[index].Y);
            foreach (var (column, row) in occupied)
            {
                var boxLeft = column * (Width + Gap);
                var boxTop = router.Rows[row].Start;
                var overlaps = left < boxLeft + Width && right > boxLeft && top < boxTop + Height && bottom > boxTop;
                Assert.IsFalse(overlaps, $"Segment {index} of {PipelineEdgeRouter.Path(route)} enters box ({column},{row}). {context}");
            }
        }
    }
}
