using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

/// <summary>Opt-in proper-motion propagation for schema-3 catalogs (issue #521); catalog positions never move implicitly.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ProperMotionPropagationTests
{
    private const double MasPerDegree = 3_600_000d;

    [TestMethod]
    public void ZeroYearsIsIdentity()
    {
        var (ra, dec) = ProperMotionPropagation.Propagate(21.1, 38.7, new CatalogProperMotion(4155.1, 3258.9), 0);

        Assert.AreEqual(21.1, ra, 1e-12);
        Assert.AreEqual(38.7, dec, 1e-12);
    }

    [TestMethod]
    public void ZeroMotionIsIdentityAtAnyEpoch()
    {
        var (ra, dec) = ProperMotionPropagation.Propagate(6.75, -16.7, new CatalogProperMotion(0, 0), 250);

        Assert.AreEqual(6.75, ra, 1e-12);
        Assert.AreEqual(-16.7, dec, 1e-12);
    }

    [TestMethod]
    public void SmallDisplacementMatchesTheTangentPlaneRate()
    {
        // Barnard's Star scale: -798.58 mas/yr in RA·cos(dec), 10328.12 mas/yr in dec, over 25 years.
        const double RaHours = 17.963471675;
        const double DecDegrees = 4.69339088;
        var motion = new CatalogProperMotion(-798.58, 10_328.12);

        var (ra, dec) = ProperMotionPropagation.Propagate(RaHours, DecDegrees, motion, 25);

        var cosDec = Math.Cos(DecDegrees * Math.PI / 180d);
        Assert.AreEqual(motion.DeclinationMasPerYear * 25 / MasPerDegree, dec - DecDegrees, 1e-7);
        // The first-order RA rate drifts as declination moves; tan(dec)·Δdec bounds that second-order term (~1.5 mas).
        Assert.AreEqual(motion.RightAscensionCosDeclinationMasPerYear * 25 / MasPerDegree / cosDec / 15d, ra - RaHours, 1e-7);
    }

    [TestMethod]
    public void TotalAngularDisplacementEqualsTheProperMotionMagnitude()
    {
        var motion = new CatalogProperMotion(4155.1, 3258.9);
        const double Years = 100;

        var (ra, dec) = ProperMotionPropagation.Propagate(21.1, 38.7, motion, Years);

        var expected = Math.Atan(Math.Sqrt(
            Math.Pow(motion.RightAscensionCosDeclinationMasPerYear, 2) + Math.Pow(motion.DeclinationMasPerYear, 2)) *
            Years / MasPerDegree * Math.PI / 180d);
        Assert.AreEqual(expected, Separation(21.1, 38.7, ra, dec), 1e-12);
    }

    [TestMethod]
    public void ForwardThenBackwardReturnsWithinTheLinearModelError()
    {
        var motion = new CatalogProperMotion(-546.01, -1223.08);
        const double Years = 50;

        var (ra, dec) = ProperMotionPropagation.Propagate(6.752477, -16.716116, motion, Years);
        var (raBack, decBack) = ProperMotionPropagation.Propagate(ra, dec, motion, -Years);

        // Reversing applies the catalog motion at the moved position, so the residual is second order in μt.
        var displacement = Math.Sqrt(
            Math.Pow(motion.RightAscensionCosDeclinationMasPerYear, 2) + Math.Pow(motion.DeclinationMasPerYear, 2)) *
            Years / MasPerDegree * Math.PI / 180d;
        Assert.IsLessThan(displacement * displacement, Separation(6.752477, -16.716116, raBack, decBack));
    }

    [TestMethod]
    public void RightAscensionWrapsIntoTheHalfOpenDay()
    {
        var (ra, _) = ProperMotionPropagation.Propagate(23.99999, 0, new CatalogProperMotion(1_000_000, 0), 1);

        Assert.IsGreaterThanOrEqualTo(0d, ra);
        Assert.IsLessThan(24d, ra);
        Assert.IsLessThan(1d, ra);
    }

    [TestMethod]
    [DataRow(double.NaN, 0d, 0d, 0d, 1d)]
    [DataRow(0d, double.PositiveInfinity, 0d, 0d, 1d)]
    [DataRow(0d, 0d, 24d, 0d, 1d)]
    [DataRow(0d, 0d, -0.1, 0d, 1d)]
    [DataRow(0d, 0d, 1d, 90.1, 1d)]
    [DataRow(0d, 0d, 1d, 0d, double.NaN)]
    public void NonFiniteOrOutOfRangeInputsAreRefused(
        double raMotion, double decMotion, double raHours, double decDegrees, double years)
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ProperMotionPropagation.Propagate(raHours, decDegrees, new CatalogProperMotion(raMotion, decMotion), years));

    private static double Separation(double raHours1, double decDegrees1, double raHours2, double decDegrees2)
    {
        var (a1, d1) = (raHours1 * Math.PI / 12d, decDegrees1 * Math.PI / 180d);
        var (a2, d2) = (raHours2 * Math.PI / 12d, decDegrees2 * Math.PI / 180d);
        var x = Math.Cos(d1) * Math.Cos(a1) * Math.Cos(d2) * Math.Cos(a2) +
                Math.Cos(d1) * Math.Sin(a1) * Math.Cos(d2) * Math.Sin(a2) + Math.Sin(d1) * Math.Sin(d2);
        var cross = Math.Sqrt(
            Math.Pow(Math.Cos(d1) * Math.Sin(a1) * Math.Sin(d2) - Math.Sin(d1) * Math.Cos(d2) * Math.Sin(a2), 2) +
            Math.Pow(Math.Sin(d1) * Math.Cos(d2) * Math.Cos(a2) - Math.Cos(d1) * Math.Cos(a1) * Math.Sin(d2), 2) +
            Math.Pow(Math.Cos(d1) * Math.Cos(a1) * Math.Cos(d2) * Math.Sin(a2) - Math.Cos(d1) * Math.Sin(a1) * Math.Cos(d2) * Math.Cos(a2), 2));
        return Math.Atan2(cross, x);
    }
}
