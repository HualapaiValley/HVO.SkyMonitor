using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ImageStatisticsCalculatorTests
{
    [TestMethod]
    public void Calculate_Mono8ReportsExactValuesAndIgnoresPadding()
    {
        var pixels = new byte[] { 0, 2, 255, 99, 4, 5, 6, 99 };

        var result = ImageStatisticsCalculator.Calculate(
            3, 2, 4, CameraPixelFormat.Mono8, pixels);

        Assert.AreEqual(6, result.PixelCount);
        Assert.AreEqual(1, result.ChannelCount);
        Assert.AreEqual(6, result.SampleCount);
        Assert.AreEqual((ushort)0, result.Minimum);
        Assert.AreEqual((ushort)255, result.Maximum);
        Assert.AreEqual(272UL, result.Sum);
        Assert.AreEqual(65_106UL, result.SumOfSquares);
        Assert.AreEqual(1, result.ZeroCount);
        Assert.AreEqual(1, result.SaturatedCount);
        Assert.AreEqual(ImageStatisticsCalculator.AlgorithmVersion, result.AlgorithmVersion);
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public void Calculate_Linear16ReportsExactWideStatistics(CameraPixelFormat pixelFormat)
    {
        var pixels = new byte[] { 0, 0, 1, 0, 0xff, 0xff, 77, 77 };

        var result = ImageStatisticsCalculator.Calculate(
            new ImageLayout(3, 1, pixelFormat, 8), pixels);

        Assert.AreEqual(3, result.SampleCount);
        Assert.AreEqual((ushort)0, result.Minimum);
        Assert.AreEqual(ushort.MaxValue, result.Maximum);
        Assert.AreEqual(65_536UL, result.Sum);
        Assert.AreEqual(4_294_836_226UL, result.SumOfSquares);
        Assert.AreEqual(1, result.ZeroCount);
        Assert.AreEqual(1, result.SaturatedCount);
    }

    [TestMethod]
    public void Calculate_Rgb24CountsEveryChannelSample()
    {
        var pixels = new byte[] { 0, 10, 20, 255, 30, 40, 99, 99 };

        var result = ImageStatisticsCalculator.Calculate(
            2, 1, 8, CameraPixelFormat.Rgb24, pixels);

        Assert.AreEqual(2, result.PixelCount);
        Assert.AreEqual(3, result.ChannelCount);
        Assert.AreEqual(6, result.SampleCount);
        Assert.AreEqual(355UL, result.Sum);
        Assert.AreEqual(68_025UL, result.SumOfSquares);
        Assert.AreEqual(1, result.ZeroCount);
        Assert.AreEqual(1, result.SaturatedCount);
    }

    [TestMethod]
    public void Calculate_UsesDeclaredLinearWhiteLevelForSaturation()
    {
        var result = ImageStatisticsCalculator.Calculate(
            2, 1, 4, CameraPixelFormat.BayerRggb16,
            new byte[] { 0xff, 0x3f, 0xff, 0xff },
            saturationLevel: 16_383);

        Assert.AreEqual(1, result.SaturatedCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageStatisticsCalculator.Calculate(
            1, 1, 1, CameraPixelFormat.Mono8, new byte[1], saturationLevel: 256));
    }

    [TestMethod]
    public void Calculate_ValidatesBufferAndHonorsCancellation()
    {
        Assert.Throws<ArgumentException>(() => ImageStatisticsCalculator.Calculate(
            2, 1, 2, CameraPixelFormat.Mono8, new byte[1]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ImageStatisticsCalculator.Calculate(
            1, 1, 1, CameraPixelFormat.Mono8, new byte[1], cancellationToken: cancellation.Token));
    }
}
