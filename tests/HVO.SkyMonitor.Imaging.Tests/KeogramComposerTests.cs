using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class KeogramComposerTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse(
        "2026-08-31T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly PixelPoint?[] LeftColumn = [new PixelPoint(0.5, 0.5), new PixelPoint(0.5, 1.5)];
    private static readonly int[] GappedFrameColumns = [0, 1, 5];

    [TestMethod]
    public void Compose_SamplesPathAlongTimeAndPatternsGapsInProportionToMissingTime()
    {
        var frames = new[]
        {
            Mono8([1, 2, 3, 4], Origin),
            Mono8([5, 6, 7, 8], Origin.AddMinutes(1)),
            Mono8([9, 10, 11, 12], Origin.AddMinutes(5))
        };
        var options = new KeogramCompositionOptions(LeftColumn, MaximumGapSeconds: 90);

        var result = KeogramComposer.Compose(frames, options);

        // A 60 s cadence leaves three missing frames in the 240 s interval.
        Assert.AreEqual(6, KeogramComposer.ComputeOutputWidth(frames, options));
        Assert.AreEqual(6, result.Width);
        Assert.AreEqual(2, result.Height);
        Assert.AreEqual(6, result.StrideBytes);
        Assert.AreEqual(CameraPixelFormat.Mono8, result.PixelFormat);
        Assert.AreEqual(3, result.FrameCount);
        Assert.AreEqual(60, result.CadenceSeconds);
        Assert.AreEqual(2, result.MappedRowCount);
        Assert.AreEqual(KeogramComposer.AlgorithmVersion, result.AlgorithmVersion);
        Assert.AreEqual(1, result.GapCount);
        Assert.AreEqual(new KeogramGap(Origin.AddMinutes(1), Origin.AddMinutes(5), 2, 3), result.Gaps.Single());
        CollectionAssert.AreEqual(
            new byte[]
            {
                1, 5, 0x20, 0x60, 0x20, 9,
                3, 7, 0x60, 0x20, 0x60, 11
            },
            result.PixelData.ToArray());
    }

    [TestMethod]
    public void Compose_InterpolatesBetweenPixelCentresAndLeavesUnmappedRowsBlack()
    {
        var frames = new[] { Mono8([10, 20, 30, 40], Origin) };
        PixelPoint?[] path = [null, new PixelPoint(1, 0.5), new PixelPoint(1, 1)];

        var result = KeogramComposer.Compose(frames, new KeogramCompositionOptions(path));

        Assert.AreEqual(3, result.Height);
        Assert.AreEqual(2, result.MappedRowCount);
        CollectionAssert.AreEqual(new byte[] { 0, 15, 25 }, result.PixelData.ToArray());
    }

    [TestMethod]
    public void Compose_Rgb24InterpolatesEachChannel()
    {
        var frames = new[]
        {
            new KeogramFrame(2, 1, 6, CameraPixelFormat.Rgb24, new byte[] { 10, 20, 30, 30, 40, 50 }, Origin),
            new KeogramFrame(2, 1, 6, CameraPixelFormat.Rgb24, new byte[] { 0, 0, 0, 2, 4, 6 }, Origin.AddMinutes(1))
        };
        PixelPoint?[] path = [new PixelPoint(1, 0.5), new PixelPoint(1.5, 0.5)];

        var result = KeogramComposer.Compose(frames, new KeogramCompositionOptions(path));

        Assert.AreEqual(2, result.Width);
        Assert.AreEqual(6, result.StrideBytes);
        CollectionAssert.AreEqual(
            new byte[] { 20, 30, 40, 1, 2, 3, 30, 40, 50, 2, 4, 6 },
            result.PixelData.ToArray());
    }

    [TestMethod]
    public void Compose_Mono16PreservesLittleEndianSamples()
    {
        var frames = new[]
        {
            new KeogramFrame(1, 1, 2, CameraPixelFormat.Mono16, new byte[] { 0x02, 0x01 }, Origin),
            new KeogramFrame(1, 1, 2, CameraPixelFormat.Mono16, new byte[] { 0x04, 0x03 }, Origin.AddMinutes(1))
        };
        PixelPoint?[] path = [new PixelPoint(0.5, 0.5), new PixelPoint(0.5, 0.5)];

        var result = KeogramComposer.Compose(frames, new KeogramCompositionOptions(path));

        Assert.AreEqual(2, result.Width);
        Assert.AreEqual(4, result.StrideBytes);
        CollectionAssert.AreEqual(
            new byte[] { 0x02, 0x01, 0x04, 0x03, 0x02, 0x01, 0x04, 0x03 },
            result.PixelData.ToArray());
    }

    [TestMethod]
    public void ComputeTimeAxis_BoundsEachGapAndRecordsTheRenderedSpan()
    {
        var frames = new[]
        {
            Mono8([1, 2, 3, 4], Origin),
            Mono8([1, 2, 3, 4], Origin.AddMinutes(1)),
            Mono8([1, 2, 3, 4], Origin.AddHours(2))
        };

        var axis = KeogramComposer.ComputeTimeAxis(
            frames, new KeogramCompositionOptions(LeftColumn, MaximumGapSeconds: 90, MaximumGapColumnCount: 16));

        Assert.AreEqual(19, axis.Width);
        Assert.AreEqual(new KeogramGap(Origin.AddMinutes(1), Origin.AddHours(2), 2, 16), axis.Gaps.Single());
    }

    [TestMethod]
    public void ComputeCadenceSeconds_UsesMedianObservedIntervalAndFallsBackToTheThreshold()
    {
        DateTimeOffset[] timestamps =
        [
            Origin, Origin.AddSeconds(30), Origin.AddSeconds(90), Origin.AddSeconds(90),
            Origin.AddSeconds(120), Origin.AddSeconds(900)
        ];

        Assert.AreEqual(30, KeogramComposer.ComputeCadenceSeconds(timestamps, 300));
        Assert.AreEqual(300, KeogramComposer.ComputeCadenceSeconds([Origin, Origin.AddHours(1)], 300));
        Assert.AreEqual(1, KeogramComposer.ComputeGapColumnCount(70, 60, 64));
        Assert.AreEqual(64, KeogramComposer.ComputeGapColumnCount(86400, 60, 64));
    }

    [TestMethod]
    public void ComputeOutputWidth_RejectsEmptyIncompatibleAndUnboundedInputs()
    {
        var first = Mono8([1, 2, 3, 4], Origin);
        var options = new KeogramCompositionOptions(LeftColumn);
        Assert.Throws<ArgumentException>(() => KeogramComposer.ComputeOutputWidth([], options));
        Assert.Throws<ArgumentException>(() => KeogramComposer.ComputeOutputWidth(
            new[] { first, Mono8([1, 2, 3, 4], Origin) with { Width = 1, Height = 1, StrideBytes = 1 } },
            options));
        Assert.Throws<ArgumentException>(() => KeogramComposer.ComputeOutputWidth(
            new[] { Mono8([1, 2, 3, 4], Origin.AddMinutes(1)), first },
            options));
        Assert.Throws<ArgumentException>(() => KeogramComposer.ComputeOutputWidth(
            new[] { first },
            new KeogramCompositionOptions([new PixelPoint(0.5, 0.5), new PixelPoint(2.5, 0.5)])));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeogramComposer.ComputeOutputWidth(
            new[] { first },
            new KeogramCompositionOptions([new PixelPoint(0.5, 0.5)])));
        Assert.Throws<ArgumentException>(() => KeogramComposer.ComputeOutputWidth(
            new[]
            {
                Mono8([1, 2, 3, 4], Origin),
                Mono8([5, 6, 7, 8], Origin.AddMinutes(10))
            },
            new KeogramCompositionOptions(LeftColumn, MaximumGapColumnCount: 1, MaximumColumnCount: 2)));
    }

    [TestMethod]
    public void Compose_PreCanceledTokenThrows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => KeogramComposer.Compose(
            new[] { Mono8([1, 2, 3, 4], Origin) },
            new KeogramCompositionOptions(LeftColumn),
            cancellation.Token));
    }

    [TestMethod]
    public void Assemble_SplitSegmentsReproduceDirectCompositionByteForByte()
    {
        // Gaps fall inside the first segment, at the segment boundary, and at the end of the night.
        int[] minutes = [0, 1, 2, 6, 7, 8, 30, 31, 32, 33, 60];
        var frames = minutes
            .Select((minute, index) => Mono8([(byte)(index * 4), (byte)(index * 4 + 1), (byte)(index * 4 + 2), (byte)(index * 4 + 3)], Origin.AddMinutes(minute)))
            .ToArray();
        var options = new KeogramCompositionOptions(LeftColumn, MaximumGapSeconds: 90, MaximumGapColumnCount: 8);
        var direct = KeogramComposer.Compose(frames, options);

        var segments = new[] { frames[..5], frames[5..6], frames[6..] }
            .Select(chunk => ToSegment(KeogramComposer.Compose(chunk, options), KeogramComposer.ComputeTimeAxis(chunk, options), chunk))
            .ToArray();
        var assembled = KeogramComposer.Assemble(segments, options);

        Assert.AreEqual(direct.Width, assembled.Width);
        Assert.AreEqual(direct.Height, assembled.Height);
        Assert.AreEqual(direct.StrideBytes, assembled.StrideBytes);
        Assert.AreEqual(direct.FrameCount, assembled.FrameCount);
        Assert.AreEqual(direct.CadenceSeconds, assembled.CadenceSeconds);
        Assert.AreEqual(direct.MappedRowCount, assembled.MappedRowCount);
        CollectionAssert.AreEqual(direct.Gaps.ToArray(), assembled.Gaps.ToArray());
        CollectionAssert.AreEqual(direct.PixelData.ToArray(), assembled.PixelData.ToArray());
        Assert.AreEqual(direct.Width, KeogramComposer.ComputeAssemblyTimeAxis(segments, options).Width);
    }

    [TestMethod]
    public void ComputeFrameColumns_SkipsPatternedGapColumns()
    {
        var frames = new[]
        {
            Mono8([1, 2, 3, 4], Origin),
            Mono8([5, 6, 7, 8], Origin.AddMinutes(1)),
            Mono8([9, 10, 11, 12], Origin.AddMinutes(5))
        };

        var axis = KeogramComposer.ComputeTimeAxis(frames, new KeogramCompositionOptions(LeftColumn, MaximumGapSeconds: 90));
        var timeOnly = KeogramComposer.ComputeTimeAxis(
            [.. frames.Select(static frame => frame.TimestampUtc)],
            90,
            KeogramComposer.DefaultMaximumGapColumnCount,
            KeogramComposer.DefaultMaximumColumnCount);

        CollectionAssert.AreEqual(GappedFrameColumns, KeogramComposer.ComputeFrameColumns(axis));
        Assert.AreEqual(axis.Width, timeOnly.Width);
        Assert.AreEqual(axis.CadenceSeconds, timeOnly.CadenceSeconds);
        CollectionAssert.AreEqual(axis.Gaps.ToArray(), timeOnly.Gaps.ToArray());
        Assert.Throws<ArgumentException>(() => KeogramComposer.ComputeTimeAxis(
            [Origin.ToOffset(TimeSpan.FromHours(-7))], 90, 1, 2));
        Assert.Throws<ArgumentException>(() => KeogramComposer.ComputeTimeAxis([Origin, Origin.AddSeconds(-1)], 90, 1, 2));
    }

    [TestMethod]
    public void Assemble_RejectsMalformedSegments()
    {
        var options = new KeogramCompositionOptions(LeftColumn);
        var valid = new KeogramSegment(1, 2, 1, CameraPixelFormat.Mono8, new byte[] { 1, 2 }, [new KeogramSegmentColumn(0, Origin)]);

        Assert.Throws<ArgumentException>(() => KeogramComposer.Assemble([], options));
        Assert.Throws<ArgumentException>(() => KeogramComposer.Assemble([valid with { FrameColumns = [] }], options));
        Assert.Throws<ArgumentException>(() => KeogramComposer.Assemble([valid with { Height = 1, PixelData = new byte[] { 1 } }], options));
        Assert.Throws<ArgumentException>(() => KeogramComposer.Assemble([valid with { PixelData = new byte[] { 1 } }], options));
        Assert.Throws<ArgumentException>(() => KeogramComposer.Assemble(
            [valid with { FrameColumns = [new KeogramSegmentColumn(1, Origin)] }], options));
        Assert.Throws<ArgumentException>(() => KeogramComposer.Assemble(
            [valid with { FrameColumns = [new KeogramSegmentColumn(0, Origin.ToOffset(TimeSpan.FromHours(-7)))] }], options));
        Assert.Throws<ArgumentException>(() => KeogramComposer.Assemble(
            [valid with { FrameColumns = [new KeogramSegmentColumn(0, Origin.AddMinutes(1))] }, valid], options));
        Assert.Throws<ArgumentException>(() => KeogramComposer.Assemble(
            [valid, valid with { PixelFormat = CameraPixelFormat.Mono16, StrideBytes = 2, PixelData = new byte[4] }], options));
    }

    private static KeogramSegment ToSegment(KeogramResult result, KeogramTimeAxis axis, KeogramFrame[] frames)
    {
        var columns = KeogramComposer.ComputeFrameColumns(axis);
        return new KeogramSegment(
            result.Width,
            result.Height,
            result.StrideBytes,
            result.PixelFormat,
            result.PixelData,
            [.. columns.Select((column, index) => new KeogramSegmentColumn(column, frames[index].TimestampUtc))]);
    }

    private static KeogramFrame Mono8(byte[] pixels, DateTimeOffset timestamp) =>
        new(2, 2, 2, CameraPixelFormat.Mono8, pixels, timestamp);
}
