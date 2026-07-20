using System.Buffers.Binary;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class Linear16TemporalBackgroundTests
{
    [TestMethod]
    public void ComputesRoundedNormalizedMeanAndMarksUnsupportedPixels()
    {
        var firstMask = Mask(2, 1, 1);
        var secondMask = Mask(2, 1, 1);
        var result = Linear16TemporalBackground.Compute(
        [
            new Linear16TemporalFrame(Frame(110, 130), firstMask, 10, 2, 1),
            new Linear16TemporalFrame(Frame(210, 230), secondMask, 10, 1, 1)
        ], 20);

        CollectionAssert.AreEqual(U16(220, 20), result.PixelData.ToArray());
        Assert.AreEqual(2, result.IncludedSamples);
        Assert.AreEqual(2, result.MaskedSamples);
        Assert.IsFalse(Linear16MaskOperations.IsExcluded(result.NoSupportMask, 0, 0));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(result.NoSupportMask, 1, 0));
        Assert.AreEqual(Linear16TemporalBackground.AlgorithmVersion, result.AlgorithmVersion);
    }

    [TestMethod]
    public void UsesAvailableUnmaskedSupportWithoutAllocatingAccumulatorPlane()
    {
        var result = Linear16TemporalBackground.Compute(
        [
            new Linear16TemporalFrame(Frame(10, 20), Mask(2, 1, 0), 0),
            new Linear16TemporalFrame(Frame(12, 22), Mask(2, 1, 1), 0),
            new Linear16TemporalFrame(Frame(14, 24), Mask(2, 1), 0)
        ], 0);

        CollectionAssert.AreEqual(U16(13, 22), result.PixelData.ToArray());
        Assert.AreEqual(4, result.IncludedSamples);
        Assert.AreEqual(2, result.MaskedSamples);
    }

    [TestMethod]
    public void PinsNormalizationAndMeanTieRoundingAndRejectsOverflow()
    {
        var result = Linear16TemporalBackground.Compute(
        [
            new Linear16TemporalFrame(Frame(11, 9, ushort.MaxValue), Mask(3, 1), 10, 1, 2),
            new Linear16TemporalFrame(Frame(13, 10, ushort.MaxValue), Mask(3, 1), 10, 1, 2)
        ], 10);

        CollectionAssert.AreEqual(U16(12, 10, 32773), result.PixelData.ToArray());

        var overflow = Linear16TemporalBackground.Compute(
        [
            new Linear16TemporalFrame(Frame(ushort.MaxValue, 0), Mask(2, 1), 0, uint.MaxValue, 1)
        ], 0);
        CollectionAssert.AreEqual(U16(0, 0), overflow.PixelData.ToArray());
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(overflow.NoSupportMask, 0, 0));
        Assert.IsFalse(Linear16MaskOperations.IsExcluded(overflow.NoSupportMask, 1, 0));
    }

    [TestMethod]
    public void SaturationAndRggbReductionUseInclusiveAnyPhotositeSemantics()
    {
        var source = new Linear16Frame(2, 2, 4, CameraPixelFormat.BayerRggb16, U16(10, 20, 30, 4000));
        var saturation = Linear16MaskOperations.CreateSaturationMask(source, 4000);
        var detector = Linear16MaskOperations.ReduceRggb16ToDetector(saturation);

        Assert.IsTrue(Linear16MaskOperations.IsExcluded(saturation, 1, 1));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(detector, 0, 0));
        Assert.AreEqual(1, detector.Width);
        Assert.AreEqual(1, detector.Height);
    }

    [TestMethod]
    public void CombinesMasksAndRasterizesPersistentCircularSupportsDeterministically()
    {
        var stars = Linear16MaskOperations.CreateCircularSupportMask(
            8,
            4,
            [new Linear16CircularMaskRegion(2.5, 1.5, 1), new Linear16CircularMaskRegion(4.5, 1.5, 1)]);
        var obstruction = Mask(8, 4, 31);
        var combined = Linear16MaskOperations.Combine([stars, obstruction]);
        var repeated = Linear16MaskOperations.Combine([stars, obstruction]);

        CollectionAssert.AreEqual(combined.Bits.ToArray(), repeated.Bits.ToArray());
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(combined, 2, 1));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(combined, 4, 1));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(combined, 7, 3));
        Assert.AreEqual(4, combined.Bits.Length);
    }

    [TestMethod]
    public void EncodingSpansRowsWithoutRowPaddingAndClearsFinalPadding()
    {
        var mask = Mask(5, 2, 0, 4, 5, 7, 8, 9);

        CollectionAssert.AreEqual(new byte[] { 0b1011_0001, 0b0000_0011 }, mask.Bits.ToArray());
    }

    [TestMethod]
    public void RejectsInvalidMasksNormalizationAndCancellation()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Linear16MaskOperations.Combine([]));
        Assert.ThrowsExactly<ArgumentException>(() => Linear16MaskOperations.Combine(
            [new Linear16PixelMask(2, 2, new byte[] { 0b1111_0000 })]));
        Assert.ThrowsExactly<ArgumentException>(() => Linear16TemporalBackground.Compute(
            [new Linear16TemporalFrame(Frame(1), Mask(1, 1), 0, 0, 1)], 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => Linear16TemporalBackground.Compute(
            [new Linear16TemporalFrame(Frame(1), Mask(1, 1), 0)], 0, cancellation.Token));
    }

    private static Linear16Frame Frame(params ushort[] values)
        => new(values.Length, 1, values.Length * 2, CameraPixelFormat.Mono16, U16(values));

    private static Linear16PixelMask Mask(int width, int height, params int[] excluded)
    {
        var bits = new byte[Linear16MaskOperations.RequiredByteLength(width, height)];
        foreach (var index in excluded)
        {
            bits[index >> 3] |= (byte)(1 << (index & 7));
        }
        return new Linear16PixelMask(width, height, bits);
    }

    private static byte[] U16(params ushort[] values)
    {
        var output = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(index * 2), values[index]);
        }
        return output;
    }
}
