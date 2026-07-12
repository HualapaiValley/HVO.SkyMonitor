using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class CelestialObjectsTests
{
    [TestMethod]
    public void FixedPlanetEphemeris_ReturnsConfiguredPosition()
    {
        var expected = new SolarSystemPosition(new EquatorialPoint(12, 5), -1.5);
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Mars] = expected
        });

        Assert.AreEqual(expected, ephemeris.GetPosition(SolarSystemBody.Mars, DateTimeOffset.UnixEpoch));
        Assert.Throws<KeyNotFoundException>(() =>
            ephemeris.GetPosition(SolarSystemBody.Jupiter, DateTimeOffset.UnixEpoch));
        Assert.AreEqual("fixed-fixture-v1", ephemeris.ModelVersion);
    }

    [TestMethod]
    public void FixedPlanetEphemeris_RejectsInvalidCoordinatesAndVersion()
    {
        Assert.Throws<ArgumentException>(() => new FixedPlanetEphemeris(
            new Dictionary<SolarSystemBody, SolarSystemPosition>(), " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedPlanetEphemeris(
            new Dictionary<SolarSystemBody, SolarSystemPosition>
            {
                [SolarSystemBody.Mars] = new(new EquatorialPoint(24, 0), 1)
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedPlanetEphemeris(
            new Dictionary<SolarSystemBody, SolarSystemPosition>
            {
                [(SolarSystemBody)int.MaxValue] = new(new EquatorialPoint(1, 0), 1)
            }));
    }

    [TestMethod]
    [DataRow(SolarSystemBody.Sun, 297.020327177, -21.115031955)]
    [DataRow(SolarSystemBody.Moon, 135.730633352, 20.689738762)]
    [DataRow(SolarSystemBody.Jupiter, 70.158202815, 21.626031112)]
    public void AstronomyEngineEphemeris_AgreesWithJplHorizonsAstrometricFixture(
        SolarSystemBody body,
        double expectedRightAscensionDegrees,
        double expectedDeclinationDegrees)
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();

        var position = ephemeris.GetPosition(body, new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero));

        var separation = AngularSeparationArcMinutes(
            expectedRightAscensionDegrees,
            expectedDeclinationDegrees,
            position.EquatorialJ2000.RightAscensionHours * 15,
            position.EquatorialJ2000.DeclinationDegrees);
        Assert.IsLessThanOrEqualTo(1, separation);
        Assert.IsTrue(double.IsFinite(position.VisualMagnitude));
        Assert.AreEqual("astronomy-engine-2.1.19-eqj-v1", ephemeris.ModelVersion);
    }

    [TestMethod]
    public void AstronomyEngineEphemeris_ReturnsFinitePositionsForEverySupportedBody()
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();

        foreach (var body in Enum.GetValues<SolarSystemBody>())
        {
            var position = ephemeris.GetPosition(body, DateTimeOffset.UnixEpoch);
            Assert.IsTrue(position.EquatorialJ2000.RightAscensionHours is >= 0 and < 24);
            Assert.IsTrue(position.EquatorialJ2000.DeclinationDegrees is >= -90 and <= 90);
            Assert.IsTrue(double.IsFinite(position.VisualMagnitude));
        }
    }

    [TestMethod]
    public void ConstellationTopology_ReturnsMatchingSegmentsOnly()
    {
        var topology = new InMemoryConstellationTopology([
            new ConstellationSegment("ORI", "27989", "24436"),
            new ConstellationSegment("UMA", "54061", "53910")
        ]);

        Assert.AreEqual(1, topology.GetSegments("ORI").Count);
        Assert.AreEqual(0, topology.GetSegments("LYR").Count);
    }

    [TestMethod]
    public void StandardConstellationTopology_LoadsPinnedD3CelestialFigures()
    {
        var topology = StandardConstellationTopology.CreateD3Celestial();

        Assert.HasCount(24, topology.GetSegments("ORI"));
        Assert.HasCount(21, topology.GetSegments("UMA"));
        Assert.AreEqual("24436", topology.GetSegments("ORI")[15].FromHipparcosId);
        Assert.AreEqual("v0.7.32", topology.Metadata.Version);
        Assert.AreEqual("BSD-3-Clause", topology.Metadata.License);
    }

    private static double AngularSeparationArcMinutes(
        double firstRightAscensionDegrees,
        double firstDeclinationDegrees,
        double secondRightAscensionDegrees,
        double secondDeclinationDegrees)
    {
        static double ToRadians(double value) => value * Math.PI / 180;
        var firstDec = ToRadians(firstDeclinationDegrees);
        var secondDec = ToRadians(secondDeclinationDegrees);
        var cosine = Math.Sin(firstDec) * Math.Sin(secondDec) +
            Math.Cos(firstDec) * Math.Cos(secondDec) *
            Math.Cos(ToRadians(firstRightAscensionDegrees - secondRightAscensionDegrees));
        return Math.Acos(Math.Clamp(cosine, -1, 1)) * 180 / Math.PI * 60;
    }
}
