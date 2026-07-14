using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class Linear16ArithmeticMeanTests
{
    [TestMethod]
    public void Compute_UsesWideAccumulationAndEmitsPackedLittleEndianPixels()
    {
        var maximum = Frame(1, 1, 2, CameraPixelFormat.Mono16, ushort.MaxValue);
        var frames = Enumerable.Repeat(maximum, 65_538).ToArray();

        var result = Linear16ArithmeticMean.Compute(frames);

        Assert.AreEqual(1, result.Width);
        Assert.AreEqual(1, result.Height);
        Assert.AreEqual(2, result.StrideBytes);
        Assert.AreEqual(65_538, result.SourceCount);
        Assert.AreEqual(Linear16ArithmeticMean.AlgorithmVersion, result.AlgorithmVersion);
        CollectionAssert.AreEqual(new byte[] { 0xff, 0xff }, result.PixelData.ToArray());
    }

    [TestMethod]
    public void Compute_AveragesPaddedFramesWithoutChangingOrRetainingInputs()
    {
        var firstPixels = new byte[] { 1, 0, 3, 0, 99, 99, 10, 0, 20, 0, 99, 99 };
        var secondPixels = new byte[] { 2, 0, 4, 0, 88, 88, 20, 0, 40, 0, 88, 88 };
        var firstOriginal = firstPixels.ToArray();
        var secondOriginal = secondPixels.ToArray();
        var frames = new[]
        {
            new Linear16Frame(2, 2, 6, CameraPixelFormat.BayerRggb16, firstPixels),
            new Linear16Frame(2, 2, 6, CameraPixelFormat.BayerRggb16, secondPixels)
        };

        var result = Linear16ArithmeticMean.Compute(frames);
        CollectionAssert.AreEqual(firstOriginal, firstPixels);
        CollectionAssert.AreEqual(secondOriginal, secondPixels);
        firstPixels[0] = 200;
        secondPixels[0] = 200;

        CollectionAssert.AreEqual(firstOriginal[1..], firstPixels[1..]);
        CollectionAssert.AreEqual(secondOriginal[1..], secondPixels[1..]);
        CollectionAssert.AreEqual(
            new byte[] { 1, 0, 3, 0, 15, 0, 30, 0 }, result.PixelData.ToArray());
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, result.PixelFormat);
    }

    [TestMethod]
    public void Compute_RejectsEmptyUnsupportedIncompatibleAndShortFrames()
    {
        Assert.Throws<ArgumentException>(() =>
            Linear16ArithmeticMean.Compute(Array.Empty<Linear16Frame>()));
        Assert.Throws<ArgumentException>(() => Linear16ArithmeticMean.Compute(new[]
        {
            new Linear16Frame(1, 1, 2, CameraPixelFormat.Mono8, new byte[2])
        }));
        Assert.Throws<ArgumentException>(() => Linear16ArithmeticMean.Compute(new[]
        {
            Frame(1, 1, 2, CameraPixelFormat.Mono16, 1),
            Frame(1, 1, 2, CameraPixelFormat.BayerRggb16, 1)
        }));
        Assert.Throws<ArgumentException>(() => Linear16ArithmeticMean.Compute(new[]
        {
            new Linear16Frame(2, 1, 4, CameraPixelFormat.Mono16, new byte[3])
        }));
        Assert.Throws<ArgumentException>(() => Linear16ArithmeticMean.Compute(
            new Linear16Frame[] { null! }));
        Assert.Throws<ArgumentException>(() => Linear16ArithmeticMean.Compute(new[]
        {
            Frame(1, 1, 2, CameraPixelFormat.Mono16, 1),
            new Linear16Frame(2, 1, 4, CameraPixelFormat.Mono16, new byte[4])
        }));
        Assert.Throws<ArgumentException>(() => Linear16ArithmeticMean.Compute(new[]
        {
            new Linear16Frame(2, 1, 2, CameraPixelFormat.Mono16, new byte[4])
        }));
    }

    [TestMethod]
    public void Compute_PreCanceledTokenThrows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Linear16ArithmeticMean.Compute(
            new[] { Frame(1, 1, 2, CameraPixelFormat.Mono16, 1) }, cancellation.Token));
    }

    private static Linear16Frame Frame(
        int width,
        int height,
        int stride,
        CameraPixelFormat pixelFormat,
        ushort value)
        => new(width, height, stride, pixelFormat, new byte[] { (byte)value, (byte)(value >> 8) });
}
