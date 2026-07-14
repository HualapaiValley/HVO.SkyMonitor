using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class RollingMono16CombinerTests
{
    [TestMethod]
    public void Add_DuringWarmup_AveragesAvailableFramesAndRecordsProvenance()
    {
        var combiner = new RollingMono16Combiner(3);
        var first = CreateArtifact(100, TimeSpan.FromSeconds(1));
        var second = CreateArtifact(300, TimeSpan.FromSeconds(2));
        combiner.Add(first);

        var result = combiner.Add(second);

        Assert.AreEqual((ushort)200, BitConverter.ToUInt16(result.PixelData.Span));
        CollectionAssert.AreEqual(new[] { first.ArtifactId, second.ArtifactId }, result.SourceArtifactIds.ToArray());
        Assert.AreEqual(TimeSpan.FromSeconds(3), result.TotalIntegration);
    }

    [TestMethod]
    public void Add_WhenWindowIsFull_RemovesOldestFrame()
    {
        var combiner = new RollingMono16Combiner(2);
        combiner.Add(CreateArtifact(100, TimeSpan.FromSeconds(1)));
        var second = CreateArtifact(200, TimeSpan.FromSeconds(1));
        combiner.Add(second);
        var third = CreateArtifact(400, TimeSpan.FromSeconds(1));

        var result = combiner.Add(third);

        Assert.AreEqual((ushort)300, BitConverter.ToUInt16(result.PixelData.Span));
        CollectionAssert.AreEqual(new[] { second.ArtifactId, third.ArtifactId }, result.SourceArtifactIds.ToArray());
    }

    [TestMethod]
    public void Add_WhenDimensionsChange_ResetsWindow()
    {
        var combiner = new RollingMono16Combiner(2);
        combiner.Add(CreateArtifact(100, TimeSpan.FromSeconds(1)));
        var changed = new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw,
            new CameraFrame(DateTimeOffset.UnixEpoch, 2, 1, CameraPixelFormat.Mono16, new byte[] { 44, 1, 44, 1 }, new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0)));

        var result = combiner.Add(changed);

        Assert.AreEqual(1, result.SourceArtifactIds.Count);
        Assert.AreEqual((ushort)300, BitConverter.ToUInt16(result.PixelData.Span));
    }

    [TestMethod]
    public void Add_BayerRggb16_AveragesRawCfaSamplesAndPreservesFormat()
    {
        var combiner = new RollingMono16Combiner(2);
        combiner.Add(CreateArtifact(100, TimeSpan.FromSeconds(1), CameraPixelFormat.BayerRggb16));

        var result = combiner.Add(CreateArtifact(300, TimeSpan.FromSeconds(1), CameraPixelFormat.BayerRggb16));

        Assert.AreEqual(CameraPixelFormat.BayerRggb16, result.PixelFormat);
        Assert.AreEqual((ushort)200, BitConverter.ToUInt16(result.PixelData.Span));
    }

    [TestMethod]
    public void Add_WhenPixelFormatChanges_ResetsWindow()
    {
        var combiner = new RollingMono16Combiner(2);
        combiner.Add(CreateArtifact(100, TimeSpan.FromSeconds(1)));

        var result = combiner.Add(CreateArtifact(300, TimeSpan.FromSeconds(1), CameraPixelFormat.BayerRggb16));

        Assert.AreEqual(1, result.SourceArtifactIds.Count);
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, result.PixelFormat);
        Assert.AreEqual((ushort)300, BitConverter.ToUInt16(result.PixelData.Span));
    }

    [TestMethod]
    public async Task Add_IndependentSensorReadNoiseImprovesBySquareRootOfFrameCount()
    {
        const int width = 101;
        const int height = 101;
        var scene = await SceneTestFactory.CreateEmptyAsync(width, height, 50).ConfigureAwait(false);
        var combiner = new RollingMono16Combiner(16);
        ReadOnlyMemory<byte> firstPixels = default;
        RollingCombinationResult? combined = null;
        for (var frameIndex = 0; frameIndex < 16; frameIndex++)
        {
            var render = Mono16SceneRenderer.Render(scene,
                new ImageLayout(width, height, CameraPixelFormat.Mono16, width * 2),
                new Mono16SceneRenderOptions
                {
                    ExposureSeconds = 1,
                    Gain = 0,
                    Seed = 2025 + frameIndex * 104729,
                    SensorResponse = new MonoSensorResponse
                    {
                        AdcBitDepth = 12,
                        FullWellElectrons = 32_400,
                        ElectronsPerAdu = 1,
                        ReadNoiseElectrons = 20,
                        BlackLevelAdu = 1000
                    }
                });
            if (frameIndex == 0)
            {
                firstPixels = render.Pixels;
            }
            combined = combiner.Add(new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw,
                new CameraFrame(DateTimeOffset.UnixEpoch, width, height, CameraPixelFormat.Mono16,
                    render.Pixels, new FrameMetadata(TimeSpan.FromSeconds(1), 0, 0))));
        }

        var firstDeviation = StandardDeviation(firstPixels.Span, 1000);
        var combinedDeviation = StandardDeviation(combined!.PixelData.Span, 1000);
        Assert.IsLessThan(firstDeviation * 0.35, combinedDeviation);
        Assert.IsGreaterThan(firstDeviation * 0.15, combinedDeviation);
    }

    private static FrameArtifact CreateArtifact(
        ushort value,
        TimeSpan exposure,
        CameraPixelFormat pixelFormat = CameraPixelFormat.Mono16)
        => new(Guid.NewGuid(), FrameArtifactRole.Raw,
            new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, pixelFormat,
                new byte[] { (byte)value, (byte)(value >> 8) }, new FrameMetadata(exposure, 1, 0)));

    private static double StandardDeviation(ReadOnlySpan<byte> pixels, double expectedMean)
    {
        var squared = 0d;
        var count = 0;
        for (var offset = 0; offset < pixels.Length; offset += 2)
        {
            var value = (ushort)(pixels[offset] | pixels[offset + 1] << 8);
            if (value == 0)
            {
                continue;
            }
            squared += Math.Pow(value - expectedMean, 2);
            count++;
        }
        return Math.Sqrt(squared / count);
    }
}
