using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StarTrailComposerTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse(
        "2026-08-31T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public void Compose_TakesPerPixelMaximumForMono8()
    {
        var frames = new[]
        {
            Mono8(2, 2, 2, [1, 5, 3, 4], Origin),
            Mono8(2, 2, 2, [2, 4, 9, 0], Origin.AddMinutes(1))
        };

        var result = StarTrailComposer.Compose(frames);

        Assert.AreEqual(2, result.Width);
        Assert.AreEqual(2, result.Height);
        Assert.AreEqual(2, result.StrideBytes);
        Assert.AreEqual(2, result.FrameCount);
        Assert.AreEqual(StarTrailComposer.AlgorithmVersion, result.AlgorithmVersion);
        CollectionAssert.AreEqual(new byte[] { 2, 5, 9, 4 }, result.PixelData.ToArray());
    }

    [TestMethod]
    public void Compose_ComparesMono16ValuesNotBytes()
    {
        var frames = new[]
        {
            Mono16(1, 2, [0xff, 0x00, 0x00, 0x02], Origin),
            Mono16(1, 2, [0x00, 0x01, 0xff, 0x01], Origin.AddMinutes(1))
        };

        var result = StarTrailComposer.Compose(frames);

        CollectionAssert.AreEqual(new byte[] { 0x00, 0x01, 0x00, 0x02 }, result.PixelData.ToArray());
    }

    [TestMethod]
    public void Compose_TakesMaximumPerRgbChannelAndDropsPadding()
    {
        var frames = new[]
        {
            new StarTrailFrame(1, 1, 6, CameraPixelFormat.Rgb24, new byte[] { 10, 200, 30, 99, 99, 99 }, Origin),
            new StarTrailFrame(1, 1, 6, CameraPixelFormat.Rgb24, new byte[] { 40, 50, 60, 88, 88, 88 }, Origin.AddMinutes(1))
        };

        var result = StarTrailComposer.Compose(frames);

        Assert.AreEqual(3, result.StrideBytes);
        CollectionAssert.AreEqual(new byte[] { 40, 200, 60 }, result.PixelData.ToArray());
    }

    [TestMethod]
    public void Compose_RejectsEmptyUnsupportedIncompatibleShortAndNullFrames()
    {
        Assert.Throws<ArgumentException>(() => StarTrailComposer.Compose([]));
        Assert.Throws<ArgumentException>(() => StarTrailComposer.Compose(
            new[] { new StarTrailFrame(1, 1, 2, CameraPixelFormat.BayerRggb16, new byte[2], Origin) }));
        Assert.Throws<ArgumentException>(() => StarTrailComposer.Compose(
            new[]
            {
                Mono8(2, 2, 2, [1, 2, 3, 4], Origin),
                Mono8(1, 1, 1, [5], Origin.AddMinutes(1))
            }));
        Assert.Throws<ArgumentException>(() => StarTrailComposer.Compose(
            new[] { new StarTrailFrame(2, 1, 2, CameraPixelFormat.Mono8, new byte[1], Origin) }));
        Assert.Throws<ArgumentException>(() => StarTrailComposer.Compose(new StarTrailFrame[] { null! }));
    }

    [TestMethod]
    public void Compose_PreCanceledTokenThrows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => StarTrailComposer.Compose(
            new[] { Mono8(1, 1, 1, [1], Origin) },
            cancellation.Token));
    }

    private static StarTrailFrame Mono8(int width, int height, int stride, byte[] pixels, DateTimeOffset timestamp) =>
        new(width, height, stride, CameraPixelFormat.Mono8, pixels, timestamp);

    private static StarTrailFrame Mono16(int width, int height, byte[] pixels, DateTimeOffset timestamp) =>
        new(width, height, width * 2, CameraPixelFormat.Mono16, pixels, timestamp);
}
