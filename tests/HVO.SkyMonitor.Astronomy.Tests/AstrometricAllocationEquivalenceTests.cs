namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AstrometricAllocationEquivalenceTests
{
    [TestMethod]
    public void ScalarTriangleOrderingMatchesStableReferenceForEveryPermutationAndTie()
    {
        double[] sides = [0, .01, .1, 1, Math.BitIncrement(1)];
        foreach (var ab in sides)
            foreach (var ac in sides)
                foreach (var bc in sides)
                {
                    var reference = new[] { (Angle: bc, Vertex: 11), (Angle: ac, Vertex: 7), (Angle: ab, Vertex: 29) }
                        .OrderBy(s => s.Angle).ToArray();
                    var actual = AstrometricSolverCore.OrderTriangle(11, 7, 29, ab, ac, bc);
                    Assert.AreEqual((reference[0].Vertex, reference[1].Vertex, reference[2].Vertex), (actual.A, actual.B, actual.C));
                    Assert.AreEqual((reference[0].Angle, reference[1].Angle, reference[2].Angle), (actual.X, actual.Y, actual.Z));
                }
    }

    [TestMethod]
    public void IncrementalRankingMatchesStableSortAndTakeIncludingEqualKeys()
    {
        (int Count, double Rms, string Id)[] inputs = [(12, .4, "a"), (14, .2, "b"), (14, .2, "c"),
            (12, .4, "d"), (12, .1, "e"), (14, .3, "f"), (12, double.NaN, "g"), (14, Math.BitIncrement(.2), "h")];
        foreach (var order in new[] { inputs, inputs.Reverse().ToArray(), inputs.Skip(3).Concat(inputs.Take(3)).ToArray() })
            for (var maximum = 1; maximum <= inputs.Length; maximum++)
            {
                var actual = new List<(int Count, double Rms, string Id)>();
                var reference = new List<(int Count, double Rms, string Id)>();
                foreach (var item in order)
                {
                    reference.Add(item);
                    reference = reference.OrderByDescending(c => c.Count).ThenBy(c => c.Rms).Take(maximum).ToList();
                    AstrometricSolverCore.RetainRanked(actual, item, maximum, static (a, b) =>
                    {
                        var count = b.Count.CompareTo(a.Count);
                        return count != 0 ? count : a.Rms.CompareTo(b.Rms);
                    });
                    CollectionAssert.AreEqual(reference.ToArray(), actual.ToArray());
                }
            }
    }

    [TestMethod]
    public void ScalarNearestTwoMatchesStableReferenceAcrossCellsTiesAndOpenRadius()
    {
        CoreDetection[] detections = [new(1, -.5, 0, 1, 1), new(2, .5, 0, 1, 1), new(3, 0, 1.5, 1, 1),
            new(4, 0, 0, 1, 1), new(5, 8, 8, 1, 1), new(6, 7.5, 8, 1, 1), new(7, 8.5, 8, 1, 1), new(8, -8, -8, 1, 1)];
        foreach (var order in new[] { detections, detections.Reverse().ToArray(), detections.OrderBy(d => d.X).ToArray() })
        {
            var grid = new AstrometricSolverCore.CoreDetectionGrid(order);
            foreach (var point in new PixelPoint[] { new(0, 0), new(0, .25), new(8, 8), new(-8, -8), new(100, 100) })
                foreach (var radius in new[] { .5, 1.5, 7.5, 20 })
                {
                    var reference = grid.Near(point, radius).OrderBy(d => AstrometricMath.Distance(d.Pixel, point)).Take(2).ToArray();
                    var actual = grid.NearestTwo(point, radius);
                    Assert.AreEqual(reference.FirstOrDefault()?.Index, actual.First?.Index);
                    Assert.AreEqual(reference.Length == 0 ? double.PositiveInfinity : AstrometricMath.Distance(reference[0].Pixel, point), actual.FirstDistance);
                    Assert.AreEqual(reference.Length < 2 ? double.PositiveInfinity : AstrometricMath.Distance(reference[1].Pixel, point), actual.SecondDistance);
                }
        }
    }
}
