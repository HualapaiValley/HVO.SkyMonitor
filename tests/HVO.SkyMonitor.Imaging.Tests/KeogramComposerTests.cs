using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class KeogramComposerTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse(
        "2026-08-31T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public void Compose_AppendsSlicesAlongTimeAndPatternsGaps()
    {
        var frames = new[]
        {
            Mono8([1, 2, 3, 4], Origin),
            Mono8([5, 6, 7, 8], Origin.AddMinutes(1)),
            Mono8([9, 10, 11, 12], Origin.AddMinutes(10))
        };
        var options = new KeogramCompositionOptions(SliceColumn: 0, MaximumGapSeconds: 300, GapColumnCount: 1);

        var result = KeogramComposer.Compose(frames, options);

        Assert.AreEqual(4, KeogramComposer.ComputeOutputWidth(frames, options));
        Assert.AreEqual(4, result.Width);
        Assert.AreEqual(2, result.Height);
        Assert.AreEqual(4, result.StrideBytes);
        Assert.AreEqual(CameraPixelFormat.Mono8, result.PixelFormat);
        Assert.AreEqual(3, result.FrameCount);
        Assert.AreEqual(1, result.GapCount);
        Assert.AreEqual(KeogramComposer.AlgorithmVersion, result.AlgorithmVersion);
        CollectionAssert.AreEqual(
            new byte[] { 1, 5, 0x20, 9, 3, 7, 0x60, 11 },
            result.PixelData.ToArray());
    }

    [TestMethod]
    public void Compose_Rgb24PreservesChannelsAndSliceColumn()
    {
        var frames = new[]
        {
            Rgb24([10, 20, 30], Origin),
            Rgb24([40, 50, 60], Origin.AddMinutes(1))
        };
        var options = new KeogramCompositionOptions(SliceColumn: 0, MaximumGapSeconds: 300);

        var result = KeogramComposer.Compose(frames, options);

        Assert.AreEqual(2, result.Width);
        Assert.AreEqual(6, result.StrideBytes);
        CollectionAssert.AreEqual(
            new byte[] { 10, 20, 30, 40, 50, 60 },
            result.PixelData.ToArray());
    }

    [TestMethod]
    public void Compose_Mono16PreservesLittleEndianSamples()
    {
        var frames = new[]
        {
            Mono16([0x02, 0x01], Origin),
            Mono16([0x04, 0x03], Origin.AddMinutes(1))
        };

        var result = KeogramComposer.Compose(frames, new KeogramCompositionOptions(SliceColumn: 0));

        Assert.AreEqual(2, result.Width);
        Assert.AreEqual(4, result.StrideBytes);
        CollectionAssert.AreEqual(
            new byte[] { 0x02, 0x01, 0x04, 0x03 },
            result.PixelData.ToArray());
    }

    [TestMethod]
    public void ComputeOutputWidth_RejectsEmptyIncompatibleAndUnboundedInputs()
    {
        var first = Mono8([1, 2, 3, 4], Origin);
        Assert.Throws<ArgumentException>(() =>
            KeogramComposer.ComputeOutputWidth([], new KeogramCompositionOptions()));
        Assert.Throws<ArgumentException>(() => KeogramComposer.ComputeOutputWidth(
            new[] { first, Mono8([1, 2, 3, 4], Origin) with { Width = 1, Height = 1, StrideBytes = 1 } },
            new KeogramCompositionOptions()));
        Assert.Throws<ArgumentException>(() => KeogramComposer.ComputeOutputWidth(
            new[] { Mono8([1, 2, 3, 4], Origin.AddMinutes(1)), first },
            new KeogramCompositionOptions()));
        Assert.Throws<ArgumentException>(() => KeogramComposer.ComputeOutputWidth(
            new[] { first },
            new KeogramCompositionOptions(SliceColumn: 2)));
        Assert.Throws<ArgumentException>(() => KeogramComposer.ComputeOutputWidth(
            new[]
            {
                Mono8([1, 2, 3, 4], Origin),
                Mono8([5, 6, 7, 8], Origin.AddMinutes(10))
            },
            new KeogramCompositionOptions(MaximumColumnCount: 2)));
    }

    [TestMethod]
    public void Compose_PreCanceledTokenThrows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => KeogramComposer.Compose(
            new[] { Mono8([1, 2, 3, 4], Origin) },
            new KeogramCompositionOptions(),
            cancellation.Token));
    }

    private static KeogramFrame Mono8(byte[] pixels, DateTimeOffset timestamp) =>
        new(2, 2, 2, CameraPixelFormat.Mono8, pixels, timestamp);

    private static KeogramFrame Mono16(byte[] pixels, DateTimeOffset timestamp) =>
        new(1, 1, 2, CameraPixelFormat.Mono16, pixels, timestamp);

    private static KeogramFrame Rgb24(byte[] pixels, DateTimeOffset timestamp) =>
        new(1, 1, 3, CameraPixelFormat.Rgb24, pixels, timestamp);
}
