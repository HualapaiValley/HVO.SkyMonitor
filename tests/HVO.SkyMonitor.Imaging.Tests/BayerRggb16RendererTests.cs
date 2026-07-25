using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class BayerRggb16RendererTests
{
    [TestMethod]
    public async Task Render_ProducesPackedIndependentRaw16FramesWithoutDemosaicing()
    {
        var scene = await SceneTestFactory.CreateCenteredAsync(9, 9).ConfigureAwait(false);
        var layout = new ImageLayout(9, 9, CameraPixelFormat.BayerRggb16, 18);
        var options = new BayerRggb16RenderOptions
        {
            ExposureSeconds = 1,
            Gain = 150,
            MagnitudeZeroElectronsPerSecond = 100,
            BackgroundElectronsPerSecond = 10,
            ShotNoiseEnabled = true,
            Seed = 42,
            SensorResponse = Asi178McSensorModel.Resolve(150),
            StoredCodeTransform = FrameStoredCodeTransform.FullRangeScaledV1
        };

        var first = BayerRggb16Renderer.Render(scene, layout, options);
        var repeated = BayerRggb16Renderer.Render(scene, layout, options);
        var changed = BayerRggb16Renderer.Render(scene, layout, options with { Seed = 43 });

        Assert.AreEqual(9 * 9 * 2, first.Pixels.Length);
        Assert.AreEqual($"{BayerRggb16Renderer.AlgorithmVersion}+full-range-scaled-v1", first.AlgorithmVersion);
        StringAssert.Contains(first.CompatibilityLabel, "RGGB", StringComparison.Ordinal);
        CollectionAssert.AreEqual(first.Pixels.ToArray(), repeated.Pixels.ToArray());
        CollectionAssert.AreNotEqual(first.Pixels.ToArray(), changed.Pixels.ToArray());
    }

    [TestMethod]
    public void Asi178McSensorModel_ResolvesNativeFourteenBitResponse()
    {
        var gainZero = Asi178McSensorModel.Resolve(0);
        var gainOneFifty = Asi178McSensorModel.Resolve(150);

        Assert.AreEqual(0.916, gainZero.ElectronsPerAdu, 1e-12);
        Assert.AreEqual(0.916 * Math.Pow(10, -150d / 200), gainOneFifty.ElectronsPerAdu, 1e-12);
        Assert.AreEqual(2.25, gainZero.ReadNoiseElectrons, 1e-12);
        Assert.AreEqual(1.57, gainOneFifty.ReadNoiseElectrons, 1e-12);
        Assert.AreEqual(14, gainZero.AdcBitDepth);
        Assert.AreEqual(16, gainZero.BlackLevelAdu);
    }

    [TestMethod]
    public void Validate_RejectsRightAlignedSampleWiderThanContainer()
    {
        var options = new BayerRggb16RenderOptions
        {
            SensorResponse = Asi178McSensorModel.Resolve(0),
            ContainerDepthBits = 8
        };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(options.Validate);
    }

    [TestMethod]
    public async Task Render_NativeRightAlignedOutputHasDistinctAlgorithmVersion()
    {
        var scene = await SceneTestFactory.CreateCenteredAsync(9, 9).ConfigureAwait(false);

        var result = BayerRggb16Renderer.Render(
            scene,
            new ImageLayout(9, 9, CameraPixelFormat.BayerRggb16, 18));

        Assert.AreEqual($"{BayerRggb16Renderer.AlgorithmVersion}+right-aligned-v1", result.AlgorithmVersion);
    }

    [TestMethod]
    public async Task Render_HonorsCancellationBeforeAllocatingChannelPlanes()
    {
        var scene = await SceneTestFactory.CreateCenteredAsync(9, 9).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        Assert.ThrowsExactly<OperationCanceledException>(() => BayerRggb16Renderer.Render(
            scene,
            new ImageLayout(9, 9, CameraPixelFormat.BayerRggb16, 18),
            cancellationToken: cancellation.Token));
    }
}
