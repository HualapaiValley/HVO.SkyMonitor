using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class Mono16DisplayStretchTests
{
    [TestMethod]
    public void Apply_UsesNonlinearStretchAndPreservesZeroAperturePixels()
    {
        var samples = Enumerable.Repeat((ushort)1000, 1000)
            .Append((ushort)1100)
            .Append((ushort)10_000)
            .ToArray();
        samples[0] = 0;

        var result = Mono16DisplayStretch.Apply(samples.Length, 1, Pack(samples));

        Assert.AreEqual(0, result[0]);
        Assert.IsGreaterThan(0, result[1000]);
        Assert.AreEqual(byte.MaxValue, result[1001]);
    }

    [TestMethod]
    public void Apply_UsesConfiguredStrideAndRejectsInvalidOptions()
    {
        var pixels = new byte[] { 0, 1, 0, 2, 99, 99, 0, 3, 0, 4, 99, 99 };

        var result = Mono16DisplayStretch.Apply(2, 2, pixels, 6);

        Assert.AreEqual(0, result[0]);
        Assert.AreEqual(0, result[1]);
        Assert.IsGreaterThan(result[1], result[2]);
        Assert.AreEqual(byte.MaxValue, result[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Mono16DisplayStretch.Apply(
            1, 1, new byte[2], options: new Mono16DisplayStretchOptions(0.9, 0.1, 1)));
    }

    [TestMethod]
    public void SkyBrightnessModel_ScalesAroundBortleThreeReference()
    {
        Assert.AreEqual(2, SkyBrightnessModel.BackgroundElectronsPerSecond(3, 2), 1e-12);
        Assert.IsLessThan(2, SkyBrightnessModel.BackgroundElectronsPerSecond(2, 2));
        Assert.IsGreaterThan(2, SkyBrightnessModel.BackgroundElectronsPerSecond(7, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SkyBrightnessModel.BackgroundElectronsPerSecond(0, 2));
    }

    [TestMethod]
    public void SkyBrightnessModel_DerivesPixelBackgroundFromPhotometricZeroPoint()
    {
        var background = SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(3, 300, 379.3, 379.3);

        Assert.AreEqual(0.1709, background, 0.001);
        Assert.IsLessThan(background,
            SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(2, 300, 379.3, 379.3));
    }

    [TestMethod]
    public void SkyBrightnessModel_ReportsTheInvalidPhotometricParameter()
    {
        var rate = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(3, -1, 1, 1));
        var focalX = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(3, 1, 0, 1));
        var focalY = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(3, 1, 1, double.NaN));

        Assert.AreEqual("magnitudeZeroElectronsPerSecond", rate.ParamName);
        Assert.AreEqual("focalLengthXPixels", focalX.ParamName);
        Assert.AreEqual("focalLengthYPixels", focalY.ParamName);
    }

    private static byte[] Pack(IEnumerable<ushort> samples)
        => samples.SelectMany(BitConverter.GetBytes).ToArray();
}
