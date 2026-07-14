using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class Rgb24GradientRendererTests
{
    [TestMethod]
    public void Render_UsesDocumentedRgbChannelOrder()
    {
        var pixels = Rgb24GradientRenderer.Render(new ImageLayout(2, 1, CameraPixelFormat.Rgb24, 6), 1, 1);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255, 127, 0 }, pixels);
    }
}
