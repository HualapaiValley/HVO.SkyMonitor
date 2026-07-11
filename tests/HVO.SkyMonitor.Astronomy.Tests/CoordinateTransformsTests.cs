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
}
