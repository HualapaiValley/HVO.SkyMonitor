namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StellarSkyBackgroundModelTests
{
    [TestMethod]
    public void SolarAltitudeLogRateHasContinuousBoundariesAndExplicitApproximatePlateaus()
    {
        Assert.AreEqual(2, StellarSkyBackgroundModel.Resolve(2, -90));
        Assert.AreEqual(2, StellarSkyBackgroundModel.Resolve(2, -18));
        Assert.AreEqual(2000, StellarSkyBackgroundModel.Resolve(2, -9), 1e-9);
        Assert.AreEqual(2000000, StellarSkyBackgroundModel.Resolve(2, 0), 1e-9);
        Assert.AreEqual(2000000, StellarSkyBackgroundModel.Resolve(2, 90), 1e-9);
        Assert.AreEqual(0, StellarSkyBackgroundModel.Resolve(0, 90));
        Assert.AreEqual(StellarSkyBackgroundModel.Resolve(2, -18), StellarSkyBackgroundModel.Resolve(2, -18 + 1e-9), 1e-8);
        Assert.AreEqual(StellarSkyBackgroundModel.Resolve(2, 0), StellarSkyBackgroundModel.Resolve(2, -1e-9), .002);
    }

    [TestMethod]
    public void ExplicitRatePreservesFixturesAndOneOverPhysicalBudgetRefuses()
    {
        Assert.AreEqual(17, StellarSkyBackgroundModel.Resolve(2, -90, 17));
        Assert.AreEqual(17, StellarSkyBackgroundModel.Resolve(2, 90, 17));
        Assert.AreEqual(1e12, StellarSkyBackgroundModel.Resolve(1e12, 90, 1e12));
        Assert.Throws<InvalidOperationException>(() => StellarSkyBackgroundModel.Resolve(1000001, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StellarSkyBackgroundModel.Resolve(2, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => StellarSkyBackgroundModel.Resolve(2, 0, -1));
    }
}
