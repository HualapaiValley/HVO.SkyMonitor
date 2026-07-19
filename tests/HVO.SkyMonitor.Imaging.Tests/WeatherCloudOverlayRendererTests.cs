using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class WeatherCloudOverlayRendererTests
{
    [TestMethod]
    public void RenderDrawsOnlyCloudyTileBordersWithoutMutatingInput()
    {
        var input = new byte[4 * 2];
        var result = WeatherCloudOverlayRenderer.Render(
            new ImageLayout(4, 2, CameraPixelFormat.Mono8, 4),
            input,
            2,
            1,
            new byte[] { 0b0000_0010 },
            [],
            new WeatherCloudOverlayRenderOptions(DrawLabels: false));

        CollectionAssert.AreEqual(new byte[8], input);
        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 },
            result.Pixels.ToArray());
        Assert.AreEqual(WeatherCloudOverlayRenderer.AlgorithmVersion, result.AlgorithmVersion);
    }
}
