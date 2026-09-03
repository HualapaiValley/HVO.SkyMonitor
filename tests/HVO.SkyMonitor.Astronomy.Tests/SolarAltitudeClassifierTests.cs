using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class SolarAltitudeClassifierTests
{
    private static readonly DateTimeOffset J2000 =
        new(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(30d, SolarAltitudeRegime.Day)]
    [DataRow(0d, SolarAltitudeRegime.Twilight)]
    [DataRow(-30d, SolarAltitudeRegime.Night)]
    public void Classify_ReturnsRegimeFromSolarAltitude(
        double declinationDegrees,
        SolarAltitudeRegime expectedRegime)
    {
        var result = SolarAltitudeClassifier.Classify(
            CreateEphemeris(declinationDegrees), J2000, 90, 0, 20, -20);

        Assert.AreEqual(expectedRegime, result.Regime);
        Assert.AreEqual(declinationDegrees, result.AltitudeDegrees, 1e-10);
    }

    [TestMethod]
    public void Classify_UsesInclusiveThresholdBoundaries()
    {
        var day = SolarAltitudeClassifier.Classify(
            CreateEphemeris(90), J2000, 90, 0, 90, -10);
        var night = SolarAltitudeClassifier.Classify(
            CreateEphemeris(-90), J2000, 90, 0, 10, -90);

        Assert.AreEqual(SolarAltitudeRegime.Day, day.Regime);
        Assert.AreEqual(90d, day.AltitudeDegrees);
        Assert.AreEqual(SolarAltitudeRegime.Night, night.Regime);
        Assert.AreEqual(-90d, night.AltitudeDegrees);
    }

    [TestMethod]
    public void Classify_NormalizesUtcBeforeRequestingAndCalculatingPosition()
    {
        var ephemeris = new TrackingEphemeris(new EquatorialPoint(0, 30));
        var offsetInstant = new DateTimeOffset(2000, 1, 1, 7, 0, 0, TimeSpan.FromHours(-5));

        var result = SolarAltitudeClassifier.Classify(ephemeris, offsetInstant, 90, 0, 20, -20);

        Assert.AreEqual(J2000, ephemeris.RequestedUtc);
        Assert.AreEqual(TimeSpan.Zero, ephemeris.RequestedUtc.Offset);
        Assert.AreEqual(30d, result.AltitudeDegrees, 1e-10);
    }

    [TestMethod]
    public void Classify_PrecessesJ2000PositionBeforeHorizontalConversion()
    {
        var utc = new DateTimeOffset(2100, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var j2000 = new EquatorialPoint(6, 20);
        var expectedAltitude = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(j2000, utc), utc, 35, -113).AltitudeDegrees;

        var result = SolarAltitudeClassifier.Classify(
            CreateEphemeris(j2000), utc, 35, -113, 10, -10);

        Assert.AreEqual(expectedAltitude, result.AltitudeDegrees, 1e-12);
    }

    [TestMethod]
    public void Classify_RejectsInvalidInputs()
    {
        var ephemeris = CreateEphemeris(0);

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SolarAltitudeClassifier.Classify(null!, J2000, 0, 0, 0, -10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SolarAltitudeClassifier.Classify(ephemeris, J2000, double.NaN, 0, 0, -10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SolarAltitudeClassifier.Classify(ephemeris, J2000, 91, 0, 0, -10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SolarAltitudeClassifier.Classify(ephemeris, J2000, 0, double.PositiveInfinity, 0, -10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SolarAltitudeClassifier.Classify(ephemeris, J2000, 0, -181, 0, -10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SolarAltitudeClassifier.Classify(ephemeris, J2000, 0, 0, double.NaN, -10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SolarAltitudeClassifier.Classify(ephemeris, J2000, 0, 0, 91, -10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SolarAltitudeClassifier.Classify(ephemeris, J2000, 0, 0, 0, -91));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SolarAltitudeClassifier.Classify(ephemeris, J2000, 0, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SolarAltitudeClassifier.Classify(ephemeris, J2000, 0, 0, -10, 0));
    }

    private static FixedPlanetEphemeris CreateEphemeris(double declinationDegrees)
        => CreateEphemeris(new EquatorialPoint(0, declinationDegrees));

    private static FixedPlanetEphemeris CreateEphemeris(EquatorialPoint position)
        => new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Sun] = new(position, -26.74)
        });

    private sealed class TrackingEphemeris(EquatorialPoint position) : IPlanetEphemeris
    {
        public string ModelVersion => "tracking-v1";

        public DateTimeOffset RequestedUtc { get; private set; }

        public SolarSystemPosition GetPosition(SolarSystemBody body, DateTimeOffset utc)
        {
            Assert.AreEqual(SolarSystemBody.Sun, body);
            RequestedUtc = utc;
            return new SolarSystemPosition(position, -26.74);
        }
    }
}
