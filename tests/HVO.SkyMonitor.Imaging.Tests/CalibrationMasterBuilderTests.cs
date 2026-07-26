using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CalibrationMasterBuilderTests
{
    [TestMethod]
    public void BuildMedian_NormalizesNative12BitSourcesAndUsesPerPixelMedianWithoutMutation()
    {
        var layout = CreateNative12Layout();
        var firstBytes = Bytes([64, 1000, 4095, 1064], strideBytes: 10);
        var secondBytes = Bytes([64, 2000, 2064, 2064], strideBytes: 10);
        var thirdBytes = Bytes([64, 3000, 64, 3064], strideBytes: 10);
        var firstBefore = firstBytes.ToArray();
        var sources = new[]
        {
            new CalibrationSourceFrame(layout, firstBytes),
            new CalibrationSourceFrame(layout, secondBytes),
            new CalibrationSourceFrame(layout, thirdBytes)
        };

        var result = CalibrationMasterBuilder.BuildMedian(sources);

        Assert.AreEqual(CalibrationMasterAlgorithms.MedianV1, result.AlgorithmVersion);
        Assert.AreEqual(3, result.SourceCount);
        Assert.AreEqual(8, result.Layout.StrideBytes);
        Assert.AreEqual(16, result.Layout.SampleDepthBits);
        Assert.AreEqual(ushort.MaxValue, result.Layout.WhiteLevel);
        Assert.AreEqual(FrameStoredCodeTransform.IdentityV1, result.Layout.StoredCodeTransform);
        Assert.AreEqual(FrameLevelCodeSpace.StoredContainer, result.Layout.LevelCodeSpace);
        CollectionAssert.AreEqual(new ushort[]
        {
            0,
            Normalize(2000),
            Normalize(2064),
            Normalize(2064)
        }, Values(result.PixelData.Span));
        CollectionAssert.AreEqual(firstBefore, firstBytes);
    }

    [TestMethod]
    public void BuildDefectMask_UsesOrderedSourceBitwiseOrWithoutScalingMaskBits()
    {
        var layout = CreateNative12Layout();
        var sources = new[]
        {
            new CalibrationSourceFrame(layout, Bytes([0, 1, 0, 0], strideBytes: 10)),
            new CalibrationSourceFrame(layout, Bytes([0, 0, 2, 0], strideBytes: 10)),
            new CalibrationSourceFrame(layout, Bytes([0, 0, 0, 4], strideBytes: 10))
        };

        var result = CalibrationMasterBuilder.BuildDefectMask(sources);

        Assert.AreEqual(CalibrationMasterAlgorithms.BitwiseOrV1, result.AlgorithmVersion);
        CollectionAssert.AreEqual(new ushort[] { 0, 1, 2, 4 }, Values(result.PixelData.Span));
    }

    [TestMethod]
    public void Normalize_UsesTheSameNativeCodeTransformAndRemovesRowPadding()
    {
        var layout = CreateNative12Layout();
        var sourceBytes = Bytes([64, 2064, 4095, 1000], strideBytes: 10);
        var sourceBefore = sourceBytes.ToArray();
        var source = new CalibrationSourceFrame(layout, sourceBytes);

        var result = CalibrationMasterBuilder.Normalize(source);

        Assert.AreEqual(8, result.Layout.StrideBytes);
        Assert.AreEqual(8, result.PixelData.Length);
        CollectionAssert.AreEqual(
            new ushort[] { 0, Normalize(2064), ushort.MaxValue, Normalize(1000) },
            Values(result.PixelData.Span));
        CollectionAssert.AreEqual(sourceBefore, sourceBytes);
    }

    [TestMethod]
    public void Build_RejectsWrongCountLayoutMismatchAndCodesAboveNativeRange()
    {
        var layout = CreateNative12Layout();
        Assert.ThrowsExactly<ArgumentException>(() => CalibrationMasterBuilder.BuildMedian([]));
        Assert.ThrowsExactly<ArgumentException>(() => CalibrationMasterBuilder.BuildMedian(
        [
            new(layout, Bytes([64, 64, 64, 64], 10)),
            new(layout with { BlackLevel = 65 }, Bytes([64, 64, 64, 64], 10)),
            new(layout, Bytes([64, 64, 64, 64], 10))
        ]));

        var highBits = new CalibrationSourceFrame(layout, Bytes([0xF064, 64, 64, 64], 10));
        Assert.ThrowsExactly<ArgumentException>(() =>
            CalibrationMasterBuilder.BuildMedian([highBits, highBits, highBits]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            CalibrationMasterBuilder.BuildDefectMask([highBits, highBits, highBits]));

        var leftShifted = layout with { StoredCodeTransform = FrameStoredCodeTransform.LeftShiftedV1 };
        var invalidPadding = new CalibrationSourceFrame(leftShifted, Bytes([0x0401, 0x0400, 0x0400, 0x0400], 10));
        Assert.ThrowsExactly<ArgumentException>(() =>
            CalibrationMasterBuilder.BuildMedian([invalidPadding, invalidPadding, invalidPadding]));
    }

    private static FrameLayoutDescriptor CreateNative12Layout()
        => new(
            4,
            1,
            10,
            CameraPixelFormat.BayerRggb16,
            FrameByteOrder.LittleEndian,
            12,
            16,
            FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.Rggb,
            64,
            4095,
            10)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = new FrameReadoutDescriptor(
                4, 1, 0, 0, 4, 1, 1, 1, FrameBinningAlgorithm.IdentityV1, 0, 0)
        };

    private static byte[] Bytes(IReadOnlyList<ushort> values, int strideBytes)
    {
        var bytes = new byte[strideBytes];
        for (var index = 0; index < values.Count; index++)
        {
            bytes[index * 2] = (byte)values[index];
            bytes[index * 2 + 1] = (byte)(values[index] >> 8);
        }
        return bytes;
    }

    private static ushort[] Values(ReadOnlySpan<byte> bytes)
    {
        var values = new ushort[bytes.Length / 2];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (ushort)(bytes[index * 2] | bytes[index * 2 + 1] << 8);
        }
        return values;
    }

    private static ushort Normalize(uint value)
        => (ushort)(((value - 64) * ushort.MaxValue + (4095 - 64) / 2u) / (4095 - 64));
}
