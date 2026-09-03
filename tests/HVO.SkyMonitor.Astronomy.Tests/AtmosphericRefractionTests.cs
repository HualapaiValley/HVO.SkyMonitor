using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AtmosphericRefractionTests
{
    [TestMethod]
    public void Apply_AtHorizon_IncreasesApparentAltitude()
    {
        var apparent = AtmosphericRefraction.Apply(0, new RefractionOptions(true));
        Assert.AreEqual(0.483d, apparent, 0.002d);
    }

    [TestMethod]
    public void Apply_BelowConfiguredFloor_DoesNotChangeAltitude()
    {
        var apparent = AtmosphericRefraction.Apply(-2, new RefractionOptions(true, -1));
        Assert.AreEqual(-2d, apparent, 1e-10);
    }

    [TestMethod]
    public void Apply_WhenDisabled_DoesNotChangeAltitude()
    {
        Assert.AreEqual(15d, AtmosphericRefraction.Apply(15, new RefractionOptions(false)), 1e-10);
    }
}
