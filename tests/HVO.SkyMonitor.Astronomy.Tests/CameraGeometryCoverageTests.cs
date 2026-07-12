using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class CameraGeometryCoverageTests
{
    [TestMethod]
    public void EnuVector_ArithmeticAndNormalization_ProduceFiniteExpectedValues()
    {
        var left = new EnuVector(3, 4, 0);
        var right = new EnuVector(-1, 2, 5);

        Assert.AreEqual(new EnuVector(2, 6, 5), EnuVector.Add(left, right));
        Assert.AreEqual(new EnuVector(4, 2, -5), EnuVector.Subtract(left, right));
        Assert.AreEqual(new EnuVector(6, 8, 0), EnuVector.Multiply(left, 2));
        Assert.AreEqual(new EnuVector(1.5, 2, 0), EnuVector.Divide(left, 2));
        Assert.AreEqual(new EnuVector(0.6, 0.8, 0), left.Normalize());
        Assert.IsTrue(double.IsFinite(EnuVector.Dot(left, right)));
        Assert.AreEqual(new EnuVector(20, -15, 10), EnuVector.Cross(left, right));
    }

    [TestMethod]
    public void EnuVector_Normalize_RejectsZeroAndNonFiniteVectors()
    {
        EnuVector[] invalid =
        [
            default,
            new(double.NaN, 0, 0),
            new(double.PositiveInfinity, 0, 0)
        ];

        foreach (var vector in invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => vector.Normalize());
        }
    }

    [TestMethod]
    public void CameraBasis_Conversions_RoundTripAndRemainFinite()
    {
        var basis = CameraBasis.Create(-20, -450, -35, horizontalFlip: true);
        var horizontal = new AltAzPoint(12.5, -725);
        var enu = CameraBasis.FromHorizontal(horizontal);
        var roundTrip = basis.ToEnu(basis.ToCamera(enu));
        var result = CameraBasis.ToHorizontal(roundTrip);

        Assert.AreEqual(horizontal.AltitudeDegrees, result.AltitudeDegrees, 1e-10);
        Assert.AreEqual(355, result.AzimuthDegrees, 1e-10);
        Assert.AreEqual(1, roundTrip.Length, 1e-12);
        Assert.IsTrue(double.IsFinite(result.AltitudeDegrees));
        Assert.IsTrue(double.IsFinite(result.AzimuthDegrees));
    }

    [TestMethod]
    public void CameraBasis_RejectsInvalidHorizontalCoordinatesRollAndDirections()
    {
        AltAzPoint[] invalidDirections =
        [
            new(double.NaN, 0),
            new(-91, 0),
            new(91, 0),
            new(0, double.PositiveInfinity)
        ];

        foreach (var direction in invalidDirections)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraBasis.FromHorizontal(direction));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => CameraBasis.Create(0, 0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraBasis.Create(0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraBasis.ToHorizontal(default));
        var basis = CameraBasis.Create(0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => basis.ToCamera(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => basis.ToEnu(default));
    }
}
