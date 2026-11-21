using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DirectCentroidRegistrationTests
{
    private static readonly PixelPoint[] Positions =
    [
        new(27.35, 22.55), new(79.8, 28.25), new(126.45, 40.65), new(42.65, 78.1),
        new(94.1, 91.4), new(125.7, 101.45), new(53.9, 48.15), new(23.2, 102.4),
        new(112.75, 67.9), new(65.3, 114.45), new(141.4, 74.45), new(54.1, 19.95)
    ];
    private static readonly int[] MissingSiteTrueRanks = [0, 1, 3, 5, 4, 6, 9, 7, 11, 10];

    [TestMethod]
    public void RecoversQuarterTurn_VerifiesDistinctHeldOutPoints_RejectsReflection()
    {
        var source = Points(Positions);
        var reference = Points(Positions.Select(p => new PixelPoint(142.5 - p.Y, p.X - .25)));
        var fit = DirectCentroidRegistration.Fit(source, reference, 160, 160);
        Assert.AreEqual(DirectRegistrationStatus.Verified, fit.Check.Status);
        Assert.AreEqual(8, fit.Check.Fit.Count);
        Assert.AreEqual(4, fit.Check.Verification.Count);
        Assert.IsFalse(fit.Check.Fit.Select(p => p.SourceIndex).Intersect(fit.Check.Verification.Select(p => p.SourceIndex)).Any());
        Assert.IsFalse(fit.Check.Fit.Select(p => p.ReferenceIndex).Intersect(fit.Check.Verification.Select(p => p.ReferenceIndex)).Any());
        var mapped = fit.Map!.Value.SourceToReference(new(12.5, 20.5))!.Value;
        Assert.AreEqual(122, mapped.X, 1e-10);
        Assert.AreEqual(12.25, mapped.Y, 1e-10);

        var reflection = Points(Positions.Select(p => new PixelPoint(160 - p.X, p.Y)));
        Assert.IsNull(DirectCentroidRegistration.Fit(source, reflection, 160, 160).Map);
    }

    [TestMethod]
    public void SeparateEndpointsCannotUseACompatibleIntermediaryAsAChain()
    {
        var a = Positions.Select(p => new PixelPoint(p.X, p.Y)).ToArray();
        PixelPoint[] b =
        [
            new(35, 17), new(89, 43), new(137, 84), new(44, 88), new(113, 112), new(121, 22),
            new(57, 113), new(28, 45), new(69, 80), new(142, 61), new(64, 52), new(25, 123)
        ];
        var center103 = Points(a);
        var center104 = Points(a.Concat(b));
        var source105 = Points(b.Select(p => new PixelPoint(p.X + 3, p.Y - 2)), brighterHalf: false);

        Assert.IsNull(DirectCentroidRegistration.Fit(source105, center103, 160, 160).Map);
        var to104 = DirectCentroidRegistration.Fit(source105, center104, 160, 160);
        Assert.AreEqual(DirectRegistrationStatus.Verified, to104.Check.Status);
        var to103 = DirectCentroidRegistration.Fit(center104, center103, 160, 160);
        Assert.AreEqual(DirectRegistrationStatus.Verified, to103.Check.Status);
    }

    [TestMethod]
    public void CollinearSourcesAndMutatedHeldOutPositionsAreRejected()
    {
        var straight = Points(Enumerable.Range(0, 12).Select(i => new PixelPoint(10 + 10 * i, 40)));
        var collinear = DirectCentroidRegistration.Fit(straight, straight, 160, 160);
        Assert.IsNull(collinear.Map);

        var source = Points(Positions);
        var badHoldout = Points(Positions.Select((p, i) => i % 3 != 1 ? p : new PixelPoint(p.X + 3, p.Y + 4)));
        var unverified = DirectCentroidRegistration.Fit(source, badHoldout, 160, 160);
        Assert.IsNull(unverified.Map);
        Assert.AreEqual(8, unverified.Check.Fit.Count);
        Assert.AreEqual(0, unverified.Check.Verification.Count);
    }

    [TestMethod]
    public void BrighterRepeatedFieldCannotHideTheGenuineFieldAmongUnrelatedPoints()
    {
        var repeated = Positions.Where((_, index) => index is not (2 or 8))
            .Concat(Positions.Select(p => new PixelPoint(p.X + 20, p.Y)));
        Assert.IsNull(DirectCentroidRegistration.Fit(Points(repeated), Points(Positions), 180, 160).Map);

        var ranked = MissingSiteTrueRanks.Select((index, rank) => new StellarDetection(index, Positions[index],
            200 - rank, 100, 1, 1, 0, 0)).ToArray();
        var correct = DirectCentroidRegistration.Fit(ranked, Points(Positions), 180, 160);
        Assert.AreEqual(0, correct.Map!.Value.Tx, 1e-10);
        var brighterAlias = Positions.Select((point, index) => new StellarDetection(index + 12,
            new(point.X + 20, point.Y), 300 - index, 100, 1, 1, 0, 0));
        var distractor = new StellarDetection(24, new(170, 148), 250, 100, 1, 1, 0, 0);
        var contested = DirectCentroidRegistration.Fit([.. ranked, .. brighterAlias, distractor], Points(Positions), 180, 160);
        Assert.IsNull(contested.Map);
        Assert.AreEqual(DirectRegistrationStatus.AmbiguousMatch, contested.Check.Status);

        var corner = Enumerable.Range(1, 8).Select(index => new StellarDetection(index + 24,
            new(154 + 4 * (index % 3), 134 + 4 * (index / 3)), 250 - index, 100, 1, 1, 0, 0));
        StellarDetection[] crowded = [distractor, .. corner];
        var crowdedControl = DirectCentroidRegistration.Fit([.. ranked, .. crowded], Points(Positions), 180, 160);
        Assert.AreEqual(0, crowdedControl.Map!.Value.Tx, 1e-10);
        var obscured = DirectCentroidRegistration.Fit([.. ranked, .. brighterAlias, .. crowded], Points(Positions), 180, 160);
        Assert.IsNull(obscured.Map);
        Assert.AreEqual(DirectRegistrationStatus.AmbiguousMatch, obscured.Check.Status);

        var rescanned = ranked.Concat(brighterAlias).Concat(crowded)
            .OrderBy(point => point.Pixel.Y).ThenBy(point => point.Pixel.X)
            .Select((point, index) => point with { Index = index }).ToArray();
        Assert.AreEqual(DirectRegistrationStatus.AmbiguousMatch,
            DirectCentroidRegistration.Fit(rescanned, Points(Positions), 180, 160).Check.Status);
        Assert.IsNull(DirectCentroidRegistration.Fit(Points(Positions), rescanned, 180, 160).Map);

        var unbounded = Enumerable.Range(0, 192).Select(index => new StellarDetection(index + 100,
            new(145 + index % 30, 140 + index / 30), 1, 100, 1, 1, 0, 0));
        var overLimit = DirectCentroidRegistration.Fit([.. ranked, .. unbounded], Points(Positions), 180, 160);
        Assert.IsNull(overLimit.Map);
        Assert.AreEqual(DirectRegistrationStatus.AmbiguityCheckLimitExceeded, overLimit.Check.Status);
    }

    [TestMethod]
    public void PlateCheckDoesNotRecenterAnOtherwiseGoodTransform()
    {
        var reference = Points(Positions);
        var source = Points(Positions.Select(p => new PixelPoint(p.X + 2, p.Y + 1)));
        var valid = DirectCentroidRegistration.VerifyPlatePair(new ImageSimilarity(1, 0, -2, -1), source, reference, 160, 160);
        Assert.AreEqual(DirectRegistrationStatus.Verified, valid.Status);
        Assert.IsEmpty(valid.Fit);
        Assert.AreEqual(12, valid.Verification.Count);
        var shifted = DirectCentroidRegistration.VerifyPlatePair(new ImageSimilarity(1, 0, -1.1, -1), source, reference, 160, 160);
        Assert.AreEqual(DirectRegistrationStatus.ResidualExceeded, shifted.Status);
    }

    private static StellarDetection[] Points(IEnumerable<PixelPoint> points, bool brighterHalf = true) =>
        points.Select((point, index) => new StellarDetection(index, point, (brighterHalf ? 200 : 100) - index,
            100, 1, 1, 0, 0)).ToArray();
}
