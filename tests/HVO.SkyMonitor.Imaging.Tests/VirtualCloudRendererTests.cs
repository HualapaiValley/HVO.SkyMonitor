using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualCloudRendererTests
{
    private static readonly DateTimeOffset Epoch = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Render_CloudDisabledAndExplicitClearRemainByteIdentical()
    {
        var scene = await SceneTestFactory.CreateCenteredAsync(21, 21).ConfigureAwait(false);
        var layout = new ImageLayout(21, 21, CameraPixelFormat.Mono16, 42);
        var baselineOptions = Options() with { BackgroundElectronsPerSecond = 100 };
        var clearOptions = baselineOptions with { Cloud = Context(coverage: 0, opacity: 0) };

        var baseline = Mono16SceneRenderer.Render(scene, layout, baselineOptions);
        var clear = Mono16SceneRenderer.Render(scene, layout, clearOptions);

        CollectionAssert.AreEqual(baseline.Pixels.ToArray(), clear.Pixels.ToArray());
        Assert.AreEqual(Mono16SceneRenderer.AlgorithmVersion, baseline.AlgorithmVersion);
        StringAssert.EndsWith(clear.AlgorithmVersion, Mono16SceneRenderer.CloudAlgorithmSuffix, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Render_AppliesCloudBeforeSensorNoiseClippingDefectsAndQuantization()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(9, 9, 4).ConfigureAwait(false);
        var layout = new ImageLayout(9, 9, CameraPixelFormat.Mono16, 18);
        var sensorOptions = Options() with
        {
            BackgroundElectronsPerSecond = 1000,
            ShotNoiseEnabled = true,
            DarkCurrentElectronsPerSecond = 10,
            Defects = [new SensorDefect(5, 4, FixedValue: 77)],
            SensorResponse = new MonoSensorResponse
            {
                AdcBitDepth = 16,
                FullWellElectrons = 50,
                ElectronsPerAdu = 1,
                ReadNoiseElectrons = 0,
                BlackLevelAdu = 25
            }
        };
        var baseline = Mono16SceneRenderer.Render(scene, layout, sensorOptions);
        var clouded = Mono16SceneRenderer.Render(
            scene,
            layout,
            sensorOptions with { Cloud = Context(coverage: 1, opacity: 1) });

        Assert.AreEqual((ushort)75, Read(baseline.Pixels, 9, 4, 4));
        Assert.AreEqual((ushort)35, Read(clouded.Pixels, 9, 4, 4));
        Assert.AreEqual((ushort)77, Read(clouded.Pixels, 9, 5, 4));
        AssertBoundedFinite(clouded.Statistics, ushort.MaxValue);
    }

    [TestMethod]
    public async Task Render_RgbAndBayerCloudPathsAreDeterministic()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(17, 17, 8).ConfigureAwait(false);
        var cloud = Context(coverage: 1, opacity: 1) with { IntegrationDuration = TimeSpan.FromSeconds(2) };
        var rgbLayout = new ImageLayout(17, 17, CameraPixelFormat.Rgb24, 51);
        var bayerLayout = new ImageLayout(17, 17, CameraPixelFormat.BayerRggb16, 34);
        var rgbOptions = new Rgb24CompatibilityRenderOptions
        {
            ExposureSeconds = 2,
            MagnitudeZeroElectronsPerSecond = 1000,
            BackgroundElectronsPerSecond = 100,
            Bias = 25,
            Cloud = cloud,
            Seed = 104
        };
        var bayerOptions = new BayerRggb16RenderOptions
        {
            ExposureSeconds = 2,
            MagnitudeZeroElectronsPerSecond = 1000,
            BackgroundElectronsPerSecond = 100,
            Cloud = cloud,
            Seed = 104,
            SensorResponse = new MonoSensorResponse
            {
                AdcBitDepth = 16,
                FullWellElectrons = 1000,
                ElectronsPerAdu = 1,
                ReadNoiseElectrons = 0,
                BlackLevelAdu = 25
            }
        };

        var firstRgb = Rgb24CompatibilityRenderer.Render(scene, rgbLayout, rgbOptions);
        var secondRgb = Rgb24CompatibilityRenderer.Render(scene, rgbLayout, rgbOptions);
        var firstBayer = BayerRggb16Renderer.Render(scene, bayerLayout, bayerOptions);
        var secondBayer = BayerRggb16Renderer.Render(scene, bayerLayout, bayerOptions);

        CollectionAssert.AreEqual(firstRgb.Pixels.ToArray(), secondRgb.Pixels.ToArray());
        CollectionAssert.AreEqual(firstBayer.Pixels.ToArray(), secondBayer.Pixels.ToArray());
        var rgbOffset = (8 * 17 + 8) * 3;
        CollectionAssert.AreEqual(
            new byte[] { 25, 25, 25 },
            firstRgb.Pixels.Slice(rgbOffset, 3).ToArray(),
            "Cloud attenuation must precede RGB response, bias, and byte quantization.");
        Assert.AreEqual((ushort)25, Read(firstBayer.Pixels, 17, 8, 8),
            "Cloud attenuation must precede CFA selection, sensor response, and RAW16 quantization.");
        AssertBoundedFinite(firstRgb.Statistics, byte.MaxValue);
        AssertBoundedFinite(firstBayer.Statistics, ushort.MaxValue);
        StringAssert.EndsWith(firstRgb.AlgorithmVersion, Mono16SceneRenderer.CloudAlgorithmSuffix, StringComparison.Ordinal);
        StringAssert.EndsWith(firstBayer.AlgorithmVersion, Mono16SceneRenderer.CloudAlgorithmSuffix, StringComparison.Ordinal);
    }

    private static Mono16SceneRenderOptions Options() => new()
    {
        ExposureSeconds = 1,
        Gain = 1,
        MagnitudeZeroElectronsPerSecond = 1000,
        PsfSigmaPixels = 1,
        PsfRadiusPixels = 4
    };

    private static VirtualCloudRenderContext Context(double coverage, double opacity)
    {
        var definition = new VirtualCloudScenarioDefinition
        {
            ScenarioId = "renderer-cloud",
            ScenarioVersion = "1",
            Seed = 104,
            EpochUtc = Epoch,
            SpatialFrequency = 2,
            Octaves = 2,
            EdgeSoftness = 1e-9,
            HorizonFadeDegrees = 0,
            TemporalSampleCount = 2,
            Keyframes = [new() { Coverage = coverage, MaximumOpacity = opacity }]
        };
        return new VirtualCloudRenderContext(new VirtualCloudField(definition), Epoch, TimeSpan.FromSeconds(1));
    }

    private static ushort Read(ReadOnlyMemory<byte> pixels, int width, int x, int y)
        => BitConverter.ToUInt16(pixels.Span.Slice((y * width + x) * 2, 2));

    private static void AssertBoundedFinite(RenderStatistics statistics, double maximum)
    {
        Assert.IsTrue(double.IsFinite(statistics.Minimum));
        Assert.IsTrue(double.IsFinite(statistics.Maximum));
        Assert.IsTrue(double.IsFinite(statistics.Mean));
        Assert.IsTrue(statistics.Minimum >= 0);
        Assert.IsTrue(statistics.Maximum <= maximum);
        Assert.IsTrue(statistics.Mean is >= 0 && statistics.Mean <= maximum);
    }
}
