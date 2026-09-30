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
