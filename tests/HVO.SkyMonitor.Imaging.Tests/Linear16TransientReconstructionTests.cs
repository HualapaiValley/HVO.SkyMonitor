using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class Linear16TransientReconstructionTests
{
    [TestMethod]
    public void OneObservation_AddsOnlyPositiveBoundedUnmaskedResiduals()
    {
        var result = Linear16TransientReconstruction.Reconstruct([
            Observation(
                target: [5, 30, 40, 50, 60, 70],
                background: [10, 10, 10, 10, 10, 10],
                width: 6,
                bounds: new(1, 0, 4, 1),
                hardMaskedPixels: [2])
        ]);

        CollectionAssert.AreEqual(new ushort[] { 10, 30, 10, 50, 60, 10 }, Pixels(result.Reconstruction));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(result.EventMask, 1, 0));
        Assert.IsFalse(Linear16MaskOperations.IsExcluded(result.EventMask, 2, 0));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(result.EventMask, 3, 0));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(result.EventMask, 4, 0));
        Assert.AreEqual(3, result.EventPixelCount);
        Assert.AreEqual(Linear16TransientReconstruction.AlgorithmVersion, result.AlgorithmVersion);
    }

    [TestMethod]
    public void TwoObservations_AverageBackgroundsAndSaturateSummedResiduals()
    {
        var result = Linear16TransientReconstruction.Reconstruct([
            Observation([ushort.MaxValue, 30], [10, 20], 2, new(0, 0, 2, 1)),
            Observation([ushort.MaxValue, 35], [20, 30], 2, new(0, 0, 2, 1))
        ]);

        CollectionAssert.AreEqual(new ushort[] { ushort.MaxValue, 40 }, Pixels(result.Reconstruction));
        Assert.AreEqual(2, result.ObservationCount);
        Assert.AreEqual(2, result.EventPixelCount);
        Assert.AreEqual(1, result.SaturatedOutputPixelCount);
    }

    [TestMethod]
    public void RejectsUnsupportedCountsLayoutsBoundsAndCancellation()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Linear16TransientReconstruction.Reconstruct([]));
        Assert.ThrowsExactly<ArgumentException>(() => Linear16TransientReconstruction.Reconstruct([
            Observation([1], [0], 1, new(0, 0, 1, 1)),
            Observation([1], [0], 1, new(0, 0, 1, 1)),
            Observation([1], [0], 1, new(0, 0, 1, 1))
        ]));
        Assert.ThrowsExactly<ArgumentException>(() => Linear16TransientReconstruction.Reconstruct([
            Observation([1], [0], 1, new(0, 0, 0, 1))
        ]));
        Assert.ThrowsExactly<ArgumentException>(() => Linear16TransientReconstruction.Reconstruct([
            Observation([1], [0], 1, new(0, 0, 1, 1)),
            Observation([1, 1], [0, 0], 2, new(0, 0, 2, 1))
        ]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => Linear16TransientReconstruction.Reconstruct([
            Observation([1], [0], 1, new(0, 0, 1, 1))
        ], cancellation.Token));
    }

    [TestMethod]
    public void DerivativeProducts_UseFrozenRawMaskAndJpegContract()
    {
        var values = Enumerable.Repeat((ushort)1_000, 32).ToArray();
        values[18] = 50_000;
        var background = Enumerable.Repeat((ushort)1_000, 32).ToArray();
        var reconstruction = Linear16TransientReconstruction.Reconstruct([
            new Linear16TransientReconstructionObservation(
                Frame(values, 8),
                Frame(background, 8),
                new Linear16PixelMask(8, 4, new byte[4]),
                new(2, 1, 3, 2))
        ]);
        var geometry = new Linear16TransientDerivativeGeometry(
            new(2, 1, 3, 2),
            [new PixelPoint(2, 1), new PixelPoint(5, 3)]);

        var first = Linear16TransientDerivativeProductFactory.Create(
            reconstruction, [geometry], new(CropPaddingPixels: 1, JpegQuality: 90));
        var second = Linear16TransientDerivativeProductFactory.Create(
            reconstruction, [geometry], new(CropPaddingPixels: 1, JpegQuality: 90));

        CollectionAssert.AreEqual(reconstruction.Reconstruction.PixelData.ToArray(), first.Reconstruction.ToArray());
        CollectionAssert.AreEqual(reconstruction.EventMask.Bits.ToArray(), first.Mask.ToArray());
        CollectionAssert.AreEqual(first.Preview.ToArray(), second.Preview.ToArray());
        CollectionAssert.AreEqual(first.Crop.ToArray(), second.Crop.ToArray());
        CollectionAssert.AreEqual(first.Overlay.ToArray(), second.Overlay.ToArray());
        Assert.AreEqual((1, 0, 5, 4), (first.CropX, first.CropY, first.CropWidth, first.CropHeight));
        var preview = JpegImageCodec.DecodeJpeg(first.Preview);
        var crop = JpegImageCodec.DecodeJpeg(first.Crop);
        var overlay = JpegImageCodec.DecodeJpeg(first.Overlay);
        Assert.AreEqual((8, 4), (preview.Width, preview.Height));
        Assert.AreEqual((5, 4), (crop.Width, crop.Height));
        Assert.AreEqual((8, 4, CameraPixelFormat.Rgb24),
            (overlay.Width, overlay.Height, overlay.PixelFormat));
        Assert.AreEqual(Linear16TransientDerivativeProductFactory.AlgorithmVersion, first.AlgorithmVersion);
    }

    [TestMethod]
    public void DerivativeProducts_RejectInvalidGeometryOptionsAndCancellation()
    {
        var reconstruction = Linear16TransientReconstruction.Reconstruct([
            Observation([1, 2], [0, 0], 2, new(0, 0, 2, 1))
        ]);
        var geometry = new Linear16TransientDerivativeGeometry(new(0, 0, 2, 1), []);

        Assert.ThrowsExactly<ArgumentException>(() => Linear16TransientDerivativeProductFactory.Create(
            reconstruction, [], new()));
        Assert.ThrowsExactly<ArgumentException>(() => Linear16TransientDerivativeProductFactory.Create(
            reconstruction, [geometry], new(CropPaddingPixels: -1)));
        Assert.ThrowsExactly<ArgumentException>(() => Linear16TransientDerivativeProductFactory.Create(
            reconstruction, [geometry with { Bounds = new(0, 0, 3, 1) }], new()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => Linear16TransientDerivativeProductFactory.Create(
            reconstruction, [geometry], cancellationToken: cancellation.Token));
    }

    private static Linear16TransientReconstructionObservation Observation(
        ushort[] target,
        ushort[] background,
        int width,
        Linear16TransientReconstructionBounds bounds,
        int[]? hardMaskedPixels = null)
        => new(
            Frame(target, width),
            Frame(background, width),
            Mask(width, hardMaskedPixels ?? []),
            bounds);

    private static Linear16Frame Frame(ushort[] values, int width)
    {
        var bytes = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            bytes[index * 2] = (byte)values[index];
            bytes[index * 2 + 1] = (byte)(values[index] >> 8);
        }
        return new(width, values.Length / width, width * 2, CameraPixelFormat.Mono16, bytes);
    }

    private static Linear16PixelMask Mask(int width, params int[] pixels)
    {
        var bits = new byte[Linear16MaskOperations.RequiredByteLength(width, 1)];
        foreach (var pixel in pixels)
        {
            bits[pixel >> 3] |= (byte)(1 << (pixel & 7));
        }
        return new(width, 1, bits);
    }

    private static ushort[] Pixels(Linear16Frame frame)
    {
        var values = new ushort[frame.Width * frame.Height];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (ushort)(frame.PixelData.Span[index * 2] | frame.PixelData.Span[index * 2 + 1] << 8);
        }
        return values;
    }
}
