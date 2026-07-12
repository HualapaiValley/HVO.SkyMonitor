using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class CameraBasisTests
{
    [TestMethod]
    public void ZenithZeroRoll_UsesEastRightNorthUp()
    {
        var basis = CameraBasis.Create(90, 0);

        AssertVector(new EnuVector(1, 0, 0), basis.Right);
        AssertVector(new EnuVector(0, 1, 0), basis.ImageUp);
        AssertVector(new EnuVector(0, 0, 1), basis.Boresight);
    }

    [TestMethod]
    public void ArbitraryBoresight_ProducesOrthonormalRightHandedBasis()
    {
        var basis = CameraBasis.Create(37, 123, 28);

        Assert.AreEqual(1d, basis.Right.Length, 1e-12);
        Assert.AreEqual(1d, basis.ImageUp.Length, 1e-12);
        Assert.AreEqual(1d, basis.Boresight.Length, 1e-12);
        Assert.AreEqual(0d, EnuVector.Dot(basis.Right, basis.ImageUp), 1e-12);
        Assert.AreEqual(0d, EnuVector.Dot(basis.Right, basis.Boresight), 1e-12);
        Assert.AreEqual(0d, EnuVector.Dot(basis.ImageUp, basis.Boresight), 1e-12);
        AssertVector(basis.Boresight, EnuVector.Cross(basis.Right, basis.ImageUp));
    }

    [TestMethod]
    public void PositiveNinetyDegreeRoll_RotatesRightTowardNorthAtZenith()
    {
        var basis = CameraBasis.Create(90, 0, 90);

        AssertVector(new EnuVector(0, 1, 0), basis.Right);
        AssertVector(new EnuVector(-1, 0, 0), basis.ImageUp);
    }

    [TestMethod]
    public void HorizontalFlip_ReversesOnlyCameraRight()
    {
        var normal = CameraBasis.Create(42, 210, 17);
        var flipped = CameraBasis.Create(42, 210, 17, horizontalFlip: true);

        AssertVector(normal.Right * -1, flipped.Right);
        AssertVector(normal.ImageUp, flipped.ImageUp);
        AssertVector(normal.Boresight, flipped.Boresight);
    }

    [TestMethod]
    public void HorizontalAndEnu_CardinalsAndBelowHorizonRoundTrip()
    {
        AltAzPoint[] directions =
        [
            new(0, 0), new(0, 90), new(0, 180), new(0, 270), new(90, 0), new(-30, 42)
        ];

        foreach (var direction in directions)
        {
            var roundTrip = CameraBasis.ToHorizontal(CameraBasis.FromHorizontal(direction));
            Assert.AreEqual(direction.AltitudeDegrees, roundTrip.AltitudeDegrees, 1e-12);
            if (Math.Abs(direction.AltitudeDegrees) < 90)
            {
                Assert.AreEqual(direction.AzimuthDegrees, roundTrip.AzimuthDegrees, 1e-12);
            }
        }
    }

    private static void AssertVector(EnuVector expected, EnuVector actual)
    {
        Assert.AreEqual(expected.East, actual.East, 1e-12);
        Assert.AreEqual(expected.North, actual.North, 1e-12);
        Assert.AreEqual(expected.Up, actual.Up, 1e-12);
    }
}
