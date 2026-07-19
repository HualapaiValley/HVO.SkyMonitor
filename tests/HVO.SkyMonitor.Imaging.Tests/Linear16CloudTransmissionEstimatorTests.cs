using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class Linear16CloudTransmissionEstimatorTests
{
    [TestMethod]
    public void Assess_Mono16ProducesDeterministicTileCoverageAndLsbFirstMask()
    {
        var reference = CreateFrame(4, 2, CameraPixelFormat.Mono16, (x, y) => (ushort)(1000 + x * 100 + y * 10));
        var current = CreateFrame(4, 2, CameraPixelFormat.Mono16, (x, y) =>
        {
            var clear = 1000 + x * 100 + y * 10;
            return (ushort)(x < 2 ? clear : clear / 2 + 100);
        });

        var result = Linear16CloudTransmissionEstimator.Assess(
            current,
            reference,
            new CloudTransmissionEstimatorOptions(
                gridColumns: 2,
                gridRows: 1,
                transmissionThresholdMillionths: 750_000,
                minimumReferenceSignal: 1,
                minimumSamplesPerTile: 1));

        Assert.AreEqual(500_000, result.CoverageMillionths);
        Assert.AreEqual(2, result.ValidTileCount);
        Assert.AreEqual(1, result.CloudyTileCount);
        Assert.HasCount(1, result.Mask!);
        Assert.AreEqual(0b0000_0010, result.Mask![0]);
        Assert.AreEqual(1_000_000, result.Regions[0].TransmissionMillionths);
        Assert.AreEqual(500_000, result.Regions[1].TransmissionMillionths);
        Assert.AreEqual(666_667, result.ConfidenceMillionths);
        Assert.IsFalse(result.Regions[0].IsCloudy);
        Assert.IsTrue(result.Regions[1].IsCloudy);
        Assert.AreEqual(32, result.ScannedBytes);
        Assert.AreEqual(Linear16CloudTransmissionEstimator.AlgorithmVersion, result.AlgorithmVersion);
    }

    [TestMethod]
    public void Assess_UsesIndependentStridesAndSetsFirstAndLastMaskBits()
    {
        var reference = CreateFrame(
            4, 4, CameraPixelFormat.Mono16, (x, y) => (ushort)(1000 + x * 100 + y * 10), strideBytes: 12);
        var current = CreateFrame(
            4,
            4,
            CameraPixelFormat.Mono16,
            (x, y) =>
            {
                var clear = 1000 + x * 100 + y * 10;
                return (ushort)(x < 2 && y < 2 || x >= 2 && y >= 2 ? clear / 2 + 100 : clear);
            },
            strideBytes: 10);

        var result = Linear16CloudTransmissionEstimator.Assess(
            current,
            reference,
            new CloudTransmissionEstimatorOptions(
                gridColumns: 2,
                gridRows: 2,
                transmissionThresholdMillionths: 750_000,
                minimumReferenceSignal: 1,
                minimumSamplesPerTile: 1));

        Assert.AreEqual(500_000, result.CoverageMillionths);
        Assert.AreEqual(0b0000_1001, result.Mask![0]);
        Assert.AreEqual(64, result.ScannedBytes);
    }

    [TestMethod]
    public void Assess_Rggb16ComparesSameCfaSitesWithoutDemosaic()
    {
        var reference = CreateFrame(4, 4, CameraPixelFormat.BayerRggb16, (x, y) => (ushort)(((y & 1, x & 1) switch
        {
            (0, 0) => 1000,
            (0, 1) => 2000,
            (1, 0) => 3000,
            _ => 4000
        }) + (x / 2 + y / 2) * 100));
        var current = CreateFrame(4, 4, CameraPixelFormat.BayerRggb16, (x, y) =>
        {
            var clear = ((y & 1, x & 1) switch
            {
                (0, 0) => 1000,
                (0, 1) => 2000,
                (1, 0) => 3000,
                _ => 4000
            }) + (x / 2 + y / 2) * 100;
            return (ushort)(clear / 2 + 50);
        });

        var result = Linear16CloudTransmissionEstimator.Assess(
            current,
            reference,
            new CloudTransmissionEstimatorOptions(
                gridColumns: 1,
                gridRows: 1,
                transmissionThresholdMillionths: 600_000,
                minimumReferenceSignal: 1,
                minimumSamplesPerTile: 4));

        Assert.AreEqual(1_000_000, result.CoverageMillionths);
        Assert.AreEqual(500_000, result.Regions.Single().TransmissionMillionths);
        Assert.AreEqual(0b0000_0001, result.Mask![0]);
    }

    [TestMethod]
    public void Assess_GeometryAndExclusionsLeaveUnsupportedTilesInvalidRatherThanClear()
    {
        var reference = CreateFrame(4, 2, CameraPixelFormat.Mono16, (x, y) => (ushort)(1000 + x * 100 + y * 10));
        var current = CreateFrame(4, 2, CameraPixelFormat.Mono16, (x, y) =>
            (ushort)((1000 + x * 100 + y * 10) / 2 + 100));
        var options = new CloudTransmissionEstimatorOptions(
            gridColumns: 2,
            gridRows: 1,
            transmissionThresholdMillionths: 750_000,
            minimumReferenceSignal: 1,
            minimumSamplesPerTile: 1,
            excludedRegions: [new NormalizedCloudRectangle(500_000, 0, 1_000_000, 1_000_000)]);

        var result = Linear16CloudTransmissionEstimator.Assess(current, reference, options);

        Assert.AreEqual(1_000_000, result.CoverageMillionths);
        Assert.AreEqual(1, result.ValidTileCount);
        Assert.IsNull(result.Regions[1].TransmissionMillionths);
        Assert.IsFalse(result.Regions[1].IsCloudy);
        Assert.AreEqual(0b0000_0001, result.Mask![0]);
    }

    [TestMethod]
    public void Assess_CoverageIsWeightedByValidSkySupportRatherThanTileCount()
    {
        var reference = CreateFrame(4, 2, CameraPixelFormat.Mono16, (x, y) => (ushort)(1000 + x * 100 + y * 10));
        var current = CreateFrame(4, 2, CameraPixelFormat.Mono16, (x, y) =>
        {
            var clear = 1000 + x * 100 + y * 10;
            return (ushort)(x < 2 ? clear / 2 + 100 : clear);
        });
        var result = Linear16CloudTransmissionEstimator.Assess(
            current,
            reference,
            new CloudTransmissionEstimatorOptions(
                gridColumns: 2,
                gridRows: 1,
                transmissionThresholdMillionths: 750_000,
                minimumReferenceSignal: 1,
                minimumSamplesPerTile: 2,
                excludedRegions: [new NormalizedCloudRectangle(750_000, 0, 1_000_000, 1_000_000)]));

        Assert.AreEqual(666_667, result.CoverageMillionths);
        Assert.AreEqual(6, result.ValidSampleCount);
        Assert.AreEqual(4, result.CloudySampleCount);
        Assert.AreEqual(2, result.ValidTileCount);
        Assert.AreEqual(1, result.CloudyTileCount);
    }

    [TestMethod]
    public void Assess_InsufficientReferenceAndSaturationReturnNoCoverage()
    {
        var darkReference = CreateFrame(2, 2, CameraPixelFormat.Mono16, (x, y) => (ushort)(100 + x + y));
        var current = CreateFrame(2, 2, CameraPixelFormat.Mono16, (_, _) => ushort.MaxValue);
        var result = Linear16CloudTransmissionEstimator.Assess(
            current,
            darkReference,
            new CloudTransmissionEstimatorOptions(
                gridColumns: 1,
                gridRows: 1,
                minimumReferenceSignal: 200,
                minimumSamplesPerTile: 1,
                maximumSaturatedFractionMillionths: 0));

        Assert.IsNull(result.CoverageMillionths);
        Assert.AreEqual(0, result.ValidTileCount);
        Assert.AreEqual(4, result.SaturatedSampleCount);
        Assert.IsTrue(result.IsSaturationContaminated);
        Assert.AreEqual(0, result.ConfidenceMillionths);
    }

    [TestMethod]
    public void Assess_RejectsIncompatibleInputsAndHonorsCancellation()
    {
        var mono = CreateFrame(2, 2, CameraPixelFormat.Mono16, (_, _) => 1000);
        var bayer = CreateFrame(2, 2, CameraPixelFormat.BayerRggb16, (_, _) => 1000);
        Assert.Throws<ArgumentException>(() => Linear16CloudTransmissionEstimator.Assess(mono, bayer));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            Linear16CloudTransmissionEstimator.Assess(mono, mono, cancellationToken: cancellation.Token));
    }

    private static Linear16CloudFrame CreateFrame(
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        Func<int, int, ushort> value,
        int? strideBytes = null)
    {
        var stride = strideBytes ?? width * 2;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sample = value(x, y);
                var offset = y * stride + x * 2;
                pixels[offset] = (byte)sample;
                pixels[offset + 1] = (byte)(sample >> 8);
            }
        }
        return new Linear16CloudFrame(
            new ImageLayout(width, height, pixelFormat, stride),
            pixels,
            blackLevel: 0,
            whiteLevel: ushort.MaxValue,
            saturationLevel: ushort.MaxValue);
    }
}
