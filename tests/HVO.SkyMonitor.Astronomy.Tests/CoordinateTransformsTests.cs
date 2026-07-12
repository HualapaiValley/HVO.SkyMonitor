using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class CoordinateTransformsTests
{
    [TestMethod]
    public void EquatorialToHorizontal_AtNorthPole_UsesDeclinationForAltitude()
    {
        var result = CoordinateTransforms.EquatorialToHorizontal(
            new EquatorialPoint(0, 30),
            DateTimeOffset.UnixEpoch,
            latitudeDegrees: 90,
            longitudeDegrees: 0);

        Assert.AreEqual(30d, result.AltitudeDegrees, 1e-10);
    }

    [TestMethod]
    public void EquatorialToHorizontal_InvalidLatitude_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CoordinateTransforms.EquatorialToHorizontal(
            new EquatorialPoint(0, 0), DateTimeOffset.UnixEpoch, 91, 0));
    }

    [TestMethod]
    [DataRow(0.01, -80d, -70d, -179d)]
    [DataRow(23.99, 75d, 35.347d, -113.878d)]
    [DataRow(12.25, -31.5d, 89d, 180d)]
    public void HorizontalToEquatorial_RoundTripsEquatorialCoordinates(
        double rightAscensionHours,
        double declinationDegrees,
        double latitudeDegrees,
        double longitudeDegrees)
    {
        var utc = new DateTimeOffset(2099, 7, 4, 3, 2, 1, TimeSpan.Zero);
        var input = new EquatorialPoint(rightAscensionHours, declinationDegrees);

        var horizontal = CoordinateTransforms.EquatorialToHorizontal(
            input, utc, latitudeDegrees, longitudeDegrees);
        var actual = CoordinateTransforms.HorizontalToEquatorial(
            horizontal, utc, latitudeDegrees, longitudeDegrees);

        Assert.AreEqual(input.RightAscensionHours, actual.RightAscensionHours, 1e-10);
        Assert.AreEqual(input.DeclinationDegrees, actual.DeclinationDegrees, 1e-10);
    }

    [TestMethod]
    public void HorizontalToEquatorial_RejectsInvalidCoordinates()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CoordinateTransforms.HorizontalToEquatorial(
            new AltAzPoint(91, 0), DateTimeOffset.UnixEpoch, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CoordinateTransforms.HorizontalToEquatorial(
            new AltAzPoint(0, double.NaN), DateTimeOffset.UnixEpoch, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CoordinateTransforms.HorizontalToEquatorial(
            new AltAzPoint(0, 0), DateTimeOffset.UnixEpoch, 0, 181));
    }
}
