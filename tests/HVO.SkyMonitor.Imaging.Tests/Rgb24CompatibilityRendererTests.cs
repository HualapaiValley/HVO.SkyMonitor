using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
public sealed class Rgb24CompatibilityRendererTests
{
    [TestMethod]
    public async Task Render_UsesPackedRgbResponseAndExplicitCompatibilityLabel()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(3, 3, 2).ConfigureAwait(false);
        var result = Rgb24CompatibilityRenderer.Render(scene, new ImageLayout(3, 3, CameraPixelFormat.Rgb24, 9), new()
        {
            BackgroundElectronsPerSecond = 100,
            ChannelResponse = new RgbChannelSettings(1, 0.5, 0.25)
        });
        var offset = (1 * 3 + 1) * 3;

        CollectionAssert.AreEqual(new byte[] { 100, 50, 25 }, result.Pixels.Slice(offset, 3).ToArray());
        StringAssert.Contains(result.CompatibilityLabel, "non-Bayer", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Render_FallbackColorIsStableAndGeometryMatchesMono()
    {
        var scene = await SceneTestFactory.CreateCenteredAsync(21, 21, colorIndex: null).ConfigureAwait(false);
        var layout = new ImageLayout(21, 21, CameraPixelFormat.Rgb24, 63);
        var options = new Rgb24CompatibilityRenderOptions { MagnitudeZeroElectronsPerSecond = 100, FallbackColorIndex = 0.5 };

        var first = Rgb24CompatibilityRenderer.Render(scene, layout, options);
        var second = Rgb24CompatibilityRenderer.Render(scene, layout, options);
        var mono = Mono16SceneRenderer.Render(scene, new ImageLayout(21, 21, CameraPixelFormat.Mono16, 42), new()
        {
            MagnitudeZeroElectronsPerSecond = 100
        });

        CollectionAssert.AreEqual(first.Pixels.ToArray(), second.Pixels.ToArray());
        Assert.AreEqual(mono.Objects[0].SourcePixel, first.Objects[0].SourcePixel);
        Assert.AreEqual(mono.Objects[0].DepositedCentroid.X, first.Objects[0].DepositedCentroid.X, 1e-9);
        Assert.AreEqual(mono.Objects[0].DepositedCentroid.Y, first.Objects[0].DepositedCentroid.Y, 1e-9);
    }

    [TestMethod]
    public async Task Render_RejectsInvalidRgbOptionsAndLayout()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(3, 3, 2).ConfigureAwait(false);
        Rgb24CompatibilityRenderOptions[] invalid =
        [
            new() { FallbackColorIndex = double.NaN },
            new() { ChannelResponse = new RgbChannelSettings(-1, 1, 1) },
            new() { ChannelResponse = new RgbChannelSettings(double.NaN, 1, 1) },
            new() { ChannelResponse = new RgbChannelSettings(1, -1, 1) },
            new() { ChannelResponse = new RgbChannelSettings(1, double.PositiveInfinity, 1) },
            new() { ChannelResponse = new RgbChannelSettings(1, 1, -1) },
            new() { ChannelResponse = new RgbChannelSettings(1, 1, double.NaN) },
            new() { WhiteBalance = new RgbChannelSettings(-1, 1, 1) },
            new() { WhiteBalance = new RgbChannelSettings(1, -1, 1) },
            new() { WhiteBalance = new RgbChannelSettings(1, 1, -1) }
        ];

        foreach (var options in invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Rgb24CompatibilityRenderer.Render(scene, Layout(3, 3), options));
        }

        Assert.Throws<ArgumentException>(() => Rgb24CompatibilityRenderer.Render(scene,
            new ImageLayout(3, 3, CameraPixelFormat.Mono16, 6)));
        Assert.AreEqual(27, Rgb24CompatibilityRenderer.Render(scene, Layout(3, 3)).Pixels.Length);
    }

    [TestMethod]
    public async Task Render_ClampsColorIndicesUsesFallbackAndHonorsResponseWhiteBalanceAndPadding()
    {
        var blueLimit = await SceneTestFactory.CreateCenteredAsync(5, 5, colorIndex: -10).ConfigureAwait(false);
        var redLimit = await SceneTestFactory.CreateCenteredAsync(5, 5, colorIndex: 10).ConfigureAwait(false);
        var fallback = await SceneTestFactory.CreateCenteredAsync(5, 5, colorIndex: null).ConfigureAwait(false);
        var layout = new ImageLayout(5, 5, CameraPixelFormat.Rgb24, 17);
        var options = new Rgb24CompatibilityRenderOptions
        {
            MagnitudeZeroElectronsPerSecond = 200,
            PsfRadiusPixels = 1,
            FallbackColorIndex = 10,
            ChannelResponse = new RgbChannelSettings(2, 1, 0.5),
            WhiteBalance = new RgbChannelSettings(0.5, 1, 2)
        };

        var blue = Rgb24CompatibilityRenderer.Render(blueLimit, layout, options);
        var red = Rgb24CompatibilityRenderer.Render(redLimit, layout, options);
        var fallbackResult = Rgb24CompatibilityRenderer.Render(fallback, layout, options);
        var center = 2 * layout.StrideBytes + 2 * 3;

        Assert.IsTrue(blue.Pixels.Span[center + 2] > blue.Pixels.Span[center]);
        Assert.IsTrue(red.Pixels.Span[center] > red.Pixels.Span[center + 2]);
        CollectionAssert.AreEqual(red.Pixels.ToArray(), fallbackResult.Pixels.ToArray());
        Assert.AreEqual((byte)0, red.Pixels.Span[15]);
        Assert.AreEqual((byte)0, red.Pixels.Span[16]);
    }

    [TestMethod]
    public async Task Render_ReportsRgbHighAndLowClippingAndMasksCircleEdge()
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(3, 3, 1).ConfigureAwait(false);
        var result = Rgb24CompatibilityRenderer.Render(scene, Layout(3, 3), new()
        {
            Bias = 300,
            Defects = [new SensorDefect(1, 1, FixedValue: -5)]
        });

        Assert.IsTrue(result.Statistics.ClippedHigh > 0);
        Assert.AreEqual(3, result.Statistics.ClippedLow);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0 }, result.Pixels.Slice(12, 3).ToArray());
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0 }, result.Pixels.Slice(0, 3).ToArray());
    }

    private static ImageLayout Layout(int width, int height)
        => new(width, height, CameraPixelFormat.Rgb24, width * 3);
}
