using System.Buffers.Binary;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class PlannedStillProductTests
{
    [TestMethod]
    public void FixedTransfer_PreservesAbsoluteBrightnessWhenTheRestOfTheHistogramChanges()
    {
        var layout = new FrameLayoutDescriptor(5, 1, 10, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
            16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, null, ushort.MaxValue, 10);
        var a = Samples(64, 2048, 4095, 4096, 65535);
        var b = Samples(64, 2048, 70, 80, 90);
        var first = FixedDisplayTransfer.Apply(layout, a, new());
        var second = FixedDisplayTransfer.Apply(layout, b, new());
        Assert.AreEqual(0, first[0]);
        Assert.AreEqual(first[1], second[1], "A fixed native sample must remain the same display brightness.");
        Assert.IsGreaterThan(second[2], first[2]);
        CollectionAssert.AreEqual(new byte[] { 255, 255, 255 }, first[2..]);
        Assert.IsLessThan(255, first[1]);
    }

    [TestMethod]
    public void FixedBayer_ReconstructsLinearChannelsBeforeGammaAndKeepsTheUnreconstructableBorderBlack()
    {
        var pixels = Samples(Enumerable.Repeat((ushort)2048, 25).ToArray());
        var layout = new FrameLayoutDescriptor(5, 5, 10, CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian,
            12, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb, null, 4095, 50);
        var result = FixedDisplayTransfer.Apply(layout, pixels, new());
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0 }, result[..3]);
        Assert.AreEqual(result[36], result[37]);
        Assert.AreEqual(result[37], result[38]);
        Assert.IsGreaterThan(128, result[36]);
        Assert.IsLessThan(255, result[36]);
    }

    [TestMethod]
    public void PlannedAxis_KeepsAllMissingCoverageAndUsesEarliestActualSampleWithinEachBin()
    {
        var start = new DateTimeOffset(2026, 10, 12, 13, 0, 0, TimeSpan.Zero).AddTicks(123);
        var axis = new PlannedKeogramAxis(start, start.AddHours(1).AddTicks(1), TimeSpan.FromMinutes(1));
        var path = new PixelPoint?[] { new(.5, .5), new(.5, 1.5) };
        var source = new KeogramSegment(3, 2, 3, CameraPixelFormat.Mono8, new byte[] { 10, 20, 30, 40, 50, 60 },
            [new(0, start.AddMinutes(5)), new(1, start.AddMinutes(5).AddSeconds(20)), new(2, start.AddMinutes(36))]);
        var result = PlannedKeogramComposer.Assemble([source], new(path), axis);
        Assert.AreEqual(61, result.Width);
        Assert.AreEqual(2, result.FrameCount);
        Assert.AreEqual(3, result.Gaps.Count);
        Assert.AreEqual(10, result.PixelData.Span[5]);
        Assert.AreEqual(40, result.PixelData.Span[61 + 5]);
        Assert.AreEqual(30, result.PixelData.Span[36]);
        Assert.AreEqual(start, result.Gaps[0].StartUtc);
        Assert.AreEqual(start.AddMinutes(6), result.Gaps[1].StartUtc);
        Assert.AreEqual(start.AddMinutes(36), result.Gaps[1].EndUtc);
        Assert.AreEqual(axis.EndUtc, result.Gaps[^1].EndUtc);
        Assert.AreEqual(24, result.Gaps[^1].ColumnCount);
    }

    [TestMethod]
    public void PlannedAxis_RejectsOutsideSourcesAndWholeSpanOverBoundsRatherThanClipping()
    {
        var start = DateTimeOffset.UnixEpoch;
        var axis = new PlannedKeogramAxis(start, start.AddMinutes(10), TimeSpan.FromMinutes(1));
        var path = new PixelPoint?[] { new(.5, .5), new(.5, 1.5) };
        var source = new KeogramSegment(1, 2, 1, CameraPixelFormat.Mono8, new byte[] { 1, 2 }, [new(0, axis.EndUtc)]);
        Assert.ThrowsExactly<ArgumentException>(() => PlannedKeogramComposer.Assemble([source], new(path), axis));
        Assert.ThrowsExactly<ArgumentException>(() => axis.Width(9));
    }

    private static byte[] Samples(params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), values[i]);
        return bytes;
    }
}
