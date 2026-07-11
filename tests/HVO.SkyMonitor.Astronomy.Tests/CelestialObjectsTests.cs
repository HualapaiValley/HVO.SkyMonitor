using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class CelestialObjectsTests
{
    [TestMethod]
    public void FixedPlanetEphemeris_ReturnsConfiguredPosition()
    {
        var expected = new EquatorialPoint(12, 5);
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, EquatorialPoint>
        {
            [SolarSystemBody.Mars] = expected
        });

        Assert.AreEqual(expected, ephemeris.GetEquatorialPosition(SolarSystemBody.Mars, DateTimeOffset.UnixEpoch));
        Assert.IsNull(ephemeris.GetEquatorialPosition(SolarSystemBody.Jupiter, DateTimeOffset.UnixEpoch));
    }

    [TestMethod]
    public void ConstellationTopology_ReturnsMatchingSegmentsOnly()
    {
        var topology = new InMemoryConstellationTopology([
            new ConstellationSegment("ORI", "bet-ori", "alf-ori"),
            new ConstellationSegment("UMA", "alf-uma", "bet-uma")
        ]);

        Assert.AreEqual(1, topology.GetSegments("ORI").Count);
        Assert.AreEqual(0, topology.GetSegments("LYR").Count);
    }
}
