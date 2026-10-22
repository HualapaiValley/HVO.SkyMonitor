using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ElectronsPerAduFixedPointTests
{
    [TestMethod]
    [DataRow(2.0, 2_000_000_000_000L)]
    [DataRow(1.0, 1_000_000_000_000L)]
    [DataRow(7.9, 7_900_000_000_000L)]
    [DataRow(0.079, 79_000_000_000L)]
    [DataRow(1e-12, 1L)]
    [DataRow(1_000_000.0, ElectronsPerAduFixedPoint.MaximumScaledValue)]
    public void TryFromDouble_RoundsTheExactValueToTheTwelveDigitGrid(double electronsPerAdu, long expected)
    {
        Assert.IsTrue(ElectronsPerAduFixedPoint.TryFromDouble(electronsPerAdu, out var scaled));

        Assert.AreEqual(expected, scaled);
    }

    [TestMethod]
    public void TryFromDouble_BreaksExactBinaryTiesToEven()
    {
        // 2^-13 and 3 × 2^-13 are exactly 122070312.5 and 366210937.5 grid units.
        Assert.IsTrue(ElectronsPerAduFixedPoint.TryFromDouble(Math.ScaleB(1, -13), out var down));
        Assert.IsTrue(ElectronsPerAduFixedPoint.TryFromDouble(Math.ScaleB(3, -13), out var up));

        Assert.AreEqual(122_070_312L, down);
        Assert.AreEqual(366_210_938L, up);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(0.0)]
    [DataRow(-0.0)]
    [DataRow(-1.0)]
    [DataRow(5e-13)]
    [DataRow(double.Epsilon)]
    [DataRow(1_000_000.0000001)]
    public void TryFromDouble_RejectsNonPositiveNonFiniteAndOutOfRangeValues(double electronsPerAdu)
    {
        Assert.IsFalse(ElectronsPerAduFixedPoint.TryFromDouble(electronsPerAdu, out var scaled));
        Assert.AreEqual(0L, scaled);
    }

    [TestMethod]
    public void TryFromDouble_AppliesAFullRangeStoredCodeScaleWithOneRounding()
    {
        // ASI178 native 14-bit codes in a full-range 16-bit container: 0.916 × 16383 / 65535 = 0.22898951705195...
        Assert.IsTrue(ElectronsPerAduFixedPoint.TryFromDouble(0.916, 16_383, 65_535, out var stored));

        Assert.AreEqual(228_989_517_052L, stored);
    }

    [TestMethod]
    public void TryFromDouble_RejectsARationalTermOutsideTheSupportedRange()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ElectronsPerAduFixedPoint.TryFromDouble(1, 0, 1, out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ElectronsPerAduFixedPoint.TryFromDouble(1, 1, 65_536, out _));
    }

    [TestMethod]
    public void IsRatioWithin_ComparesTheCoefficientRatioExactly()
    {
        const long one = ElectronsPerAduFixedPoint.Scale;

        Assert.IsTrue(ElectronsPerAduFixedPoint.IsRatioWithin(one, 4 * one, 4000, 1000));
        Assert.IsTrue(ElectronsPerAduFixedPoint.IsRatioWithin(one, one, 4000, 1000));
        Assert.IsFalse(ElectronsPerAduFixedPoint.IsRatioWithin(one, (4 * one) + 1, 4000, 1000));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ElectronsPerAduFixedPoint.IsRatioWithin(2 * one, one, 4000, 1000));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ElectronsPerAduFixedPoint.IsRatioWithin(0, one, 4000, 1000));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ElectronsPerAduFixedPoint.IsRatioWithin(one, one, 0, 1000));
    }

    [TestMethod]
    public void DesignNoteExample_GivesTheSameElectronsAndAnExactReferenceGainOutput()
    {
        // #1475 design note: corrected 100 ADU at k=2 and 200 ADU at k=1 are both 200 electrons; their mean is
        // 200 electrons, which is 100 ADU at reference k=2.
        Assert.IsTrue(ElectronsPerAduFixedPoint.TryFromDouble(2, out var highGainCoefficient));
        Assert.IsTrue(ElectronsPerAduFixedPoint.TryFromDouble(1, out var lowGainCoefficient));
        Assert.IsTrue(ElectronsPerAduFixedPoint.IsRatioWithin(lowGainCoefficient, highGainCoefficient, 4000, 1000));

        var first = (Int128)100 * highGainCoefficient;
        var second = (Int128)200 * lowGainCoefficient;
        var mean = (first + second) / 2;

        Assert.AreEqual((Int128)200 * ElectronsPerAduFixedPoint.Scale, first);
        Assert.AreEqual(first, second);
        Assert.AreEqual((Int128)100, mean / highGainCoefficient);
        Assert.AreEqual(Int128.Zero, mean % highGainCoefficient);
    }

    [TestMethod]
    public void Format_WritesAnInvariantTwelveDigitDecimal()
    {
        Assert.AreEqual("7.900000000000", ElectronsPerAduFixedPoint.Format(7_900_000_000_000));
        Assert.AreEqual("0.000000000001", ElectronsPerAduFixedPoint.Format(1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ElectronsPerAduFixedPoint.Format(0));
    }
}
