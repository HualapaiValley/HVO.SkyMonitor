using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class MeridianSamplePathTests
{
    private const double Radius = 595.84;
    private static readonly double[] QuarterAngles = [0, 45, 90, 135, 180];
    private static readonly bool[] NarrowCircleMapping = [false, false, true, true, true, false, false];

    [TestMethod]
    public void Create_ZenithFisheyeRunsNorthHorizonThroughZenithToSouthHorizon()
    {
        var samples = MeridianSamplePath.Create(Asi174(), 5);

        CollectionAssert.AreEqual(
            QuarterAngles,
            samples.Select(static sample => sample.MeridianAngleDegrees).ToArray());
        CollectionAssert.AreEqual(
            new[] { new AltAzPoint(0, 0), new AltAzPoint(45, 0), new AltAzPoint(90, 0), new AltAzPoint(45, 180), new AltAzPoint(0, 180) },
            samples.Select(static sample => sample.Direction).ToArray());
        AssertPixel(new PixelPoint(968, 12.16), samples[0].Pixel);
        AssertPixel(new PixelPoint(968, 310.08), samples[1].Pixel);
        AssertPixel(new PixelPoint(968, 608), samples[2].Pixel);
        AssertPixel(new PixelPoint(968, 905.92), samples[3].Pixel);
        AssertPixel(new PixelPoint(968, 1203.84), samples[4].Pixel);
    }

    [TestMethod]
    public void Create_FollowsRigRollInsteadOfAFixedImageColumn()
    {
        var rolled = Asi174() with { RollDegrees = 90 };
        var projector = ProjectorFactory.Create(rolled);

        var samples = MeridianSamplePath.Create(rolled, 9);

        foreach (var sample in samples)
        {
            AssertPixel(projector.Project(sample.Direction)!.Value, sample.Pixel);
            Assert.AreEqual(608, sample.Pixel!.Value.Y, 1e-6);
        }
        Assert.AreEqual(Radius * 2, Math.Abs(samples[^1].Pixel!.Value.X - samples[0].Pixel!.Value.X), 1e-6);
    }

    [TestMethod]
    public void Create_LeavesDirectionsOutsideTheImageCircleUnmapped()
    {
        var narrow = Asi174() with { ImageCircleRadiusPixels = Radius / 2 };

        var samples = MeridianSamplePath.Create(narrow, 7);

        CollectionAssert.AreEqual(
            NarrowCircleMapping,
            samples.Select(static sample => sample.Pixel is not null).ToArray());
    }

    [TestMethod]
    public void RecommendedSampleCount_TracksImagedMeridianLength()
    {
        var count = MeridianSamplePath.RecommendedSampleCount(Asi174());

        Assert.IsTrue(count is >= 1185 and <= 1200, $"Unexpected sample count {count}.");
    }

    [TestMethod]
    public void Create_RejectsOutOfRangeSampleCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeridianSamplePath.Create(Asi174(), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MeridianSamplePath.Create(Asi174(), MeridianSamplePath.MaximumSampleCount + 1));
    }

    private static ProjectionContext Asi174() => new(
        ProjectionModel.EquidistantFisheye,
        968,
        608,
        Radius / (Math.PI / 2),
        Radius / (Math.PI / 2),
        1936,
        1216,
        ProjectionAperture.Circular,
        Radius);

    private static void AssertPixel(PixelPoint expected, PixelPoint? actual)
    {
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.X, actual.Value.X, 1e-6);
        Assert.AreEqual(expected.Y, actual.Value.Y, 1e-6);
    }
}
