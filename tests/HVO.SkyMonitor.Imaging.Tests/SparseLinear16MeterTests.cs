using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class SparseLinear16MeterTests
{
    [TestMethod]
    public void Measure_Mono16UsesRoiStridesLevelsAndRejectsSaturation()
    {
        const int width = 7;
        const int height = 5;
        const int stride = 18;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Write(pixels, stride, x, y, (ushort)(y * 100 + x * 10));
            }
        }
        Array.Fill(pixels, (byte)0xff, width * 2, stride - width * 2);

        var result = SparseLinear16Meter.Measure(
            new ImageLayout(width, height, CameraPixelFormat.Mono16, stride),
            pixels,
            new SparseMeteringOptions(
                2, 2, blackLevel: 100, whiteLevel: 400, saturationLevel: 330,
                region: new MeteringRegion(1, 1, 5, 4)));

        Assert.AreEqual(0.25, result.NormalizedMean, 1e-12);
        Assert.AreEqual(6, result.ConsideredSampleCount);
        Assert.AreEqual(4, result.AcceptedSampleCount);
        Assert.AreEqual(2, result.SaturatedSampleCount);
        Assert.AreEqual(12, result.ScannedBytes);
        Assert.IsTrue(result.HasMeasurement);
    }

    [TestMethod]
    public void Measure_AppliesImageCircleAndCompactInclusionMaskBeforeScanning()
    {
        const int width = 5;
        const int height = 5;
        var pixels = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Write(pixels, width * 2, x, y, 200);
            }
        }
        var mask = new byte[] { 0xff, 0xff, 0xff, 0xff };
        var excludedPixel = 2 * width + 1;
        mask[excludedPixel >> 3] &= (byte)~(1 << (excludedPixel & 7));

        var result = SparseLinear16Meter.Measure(
            new ImageLayout(width, height, CameraPixelFormat.Mono16, width * 2),
            pixels,
            new SparseMeteringOptions(
                1, 1, whiteLevel: 400, imageCircle: new MeteringImageCircle(2, 2, 1.1)),
            mask);

        Assert.AreEqual(0.5, result.NormalizedMean, 1e-12);
        Assert.AreEqual(4, result.ConsideredSampleCount);
        Assert.AreEqual(4, result.AcceptedSampleCount);
        Assert.AreEqual(0, result.SaturatedSampleCount);
        Assert.AreEqual(8, result.ScannedBytes);
    }

    [TestMethod]
    public void Measure_BayerRggb16SamplesSelectedPhotositesBySparseCellsWithoutDemosaic()
    {
        const int width = 6;
        const int height = 4;
        const int stride = 16;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (y & 1, x & 1) switch
                {
                    (0, 0) => 1000,
                    (0, 1) => 2000,
                    (1, 0) => 3000,
                    _ => 4000
                };
                Write(pixels, stride, x, y, (ushort)value);
            }
        }

        var result = SparseLinear16Meter.Measure(
            new ImageLayout(width, height, CameraPixelFormat.BayerRggb16, stride),
            pixels,
            new SparseMeteringOptions(
                2, 2, whiteLevel: 5000, bayerPhotosites: BayerMeteringPhotosites.Green));

        Assert.AreEqual(0.5, result.NormalizedMean, 1e-12);
        Assert.AreEqual(12, result.ConsideredSampleCount);
        Assert.AreEqual(12, result.AcceptedSampleCount);
        Assert.AreEqual(24, result.ScannedBytes);
    }

    [TestMethod]
    public void Measure_WhenEverySampleIsSaturatedReturnsBoundedEmptyMeasurement()
    {
        var result = SparseLinear16Meter.Measure(
            new ImageLayout(2, 1, CameraPixelFormat.Mono16, 4),
            new byte[] { 100, 0, 200, 0 },
            new SparseMeteringOptions(1, 1, whiteLevel: 200, saturationLevel: 100));

        Assert.AreEqual(0d, result.NormalizedMean);
        Assert.AreEqual(2, result.ConsideredSampleCount);
        Assert.AreEqual(0, result.AcceptedSampleCount);
        Assert.AreEqual(2, result.SaturatedSampleCount);
        Assert.AreEqual(4, result.ScannedBytes);
        Assert.IsFalse(result.HasMeasurement);
    }

    [TestMethod]
    public void Measure_RejectsInvalidLayoutsOptionsBuffersAndMasks()
    {
        var mono16 = new ImageLayout(2, 2, CameraPixelFormat.Mono16, 4);
        var pixels = new byte[8];

        Assert.Throws<ArgumentOutOfRangeException>(() => SparseLinear16Meter.Measure(
            new ImageLayout(2, 2, CameraPixelFormat.Mono16, 3), pixels, new SparseMeteringOptions()));
        Assert.Throws<ArgumentException>(() => SparseLinear16Meter.Measure(
            new ImageLayout(2, 2, CameraPixelFormat.Mono8, 2), new byte[4], new SparseMeteringOptions()));
        Assert.Throws<ArgumentException>(() => SparseLinear16Meter.Measure(
            mono16, new byte[7], new SparseMeteringOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparseLinear16Meter.Measure(
            mono16, pixels, new SparseMeteringOptions(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparseLinear16Meter.Measure(
            mono16, pixels, new SparseMeteringOptions(1, 1, blackLevel: 100, whiteLevel: 100)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparseLinear16Meter.Measure(
            mono16, pixels, new SparseMeteringOptions(
                1, 1, region: new MeteringRegion(1, 1, 2, 2))));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparseLinear16Meter.Measure(
            mono16, pixels, new SparseMeteringOptions(
                1, 1, imageCircle: new MeteringImageCircle(1, 1, double.NaN))));
        Assert.Throws<ArgumentException>(() => SparseLinear16Meter.Measure(
            new ImageLayout(5, 2, CameraPixelFormat.Mono16, 10), new byte[20],
            new SparseMeteringOptions(), new byte[] { 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparseLinear16Meter.Measure(
            new ImageLayout(2, 2, CameraPixelFormat.BayerRggb16, 4), pixels,
            new SparseMeteringOptions(1, 1, bayerPhotosites: BayerMeteringPhotosites.None)));
    }

    [TestMethod]
    public void Measure_AfterWarmupDoesNotAllocatePerCall()
    {
        var pixels = new byte[32 * 32 * 2];
        var layout = new ImageLayout(32, 32, CameraPixelFormat.Mono16, 64);
        var options = new SparseMeteringOptions(4, 4);
        _ = SparseLinear16Meter.Measure(layout, pixels, options);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = SparseLinear16Meter.Measure(layout, pixels, options);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0, allocated);
    }

    private static void Write(byte[] pixels, int stride, int x, int y, ushort value)
    {
        var offset = y * stride + x * 2;
        pixels[offset] = (byte)value;
        pixels[offset + 1] = (byte)(value >> 8);
    }
}
