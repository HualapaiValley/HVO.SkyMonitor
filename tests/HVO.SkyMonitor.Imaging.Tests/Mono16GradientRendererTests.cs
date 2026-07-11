using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
public sealed class Mono16GradientRendererTests
{
    [TestMethod]
    public void Render_WithSameInputs_ReturnsDeterministicLittleEndianPixels()
    {
        var layout = new ImageLayout(2, 2, CameraPixelFormat.Mono16, 4);
        var first = Mono16GradientRenderer.Render(layout, 1, 1);
        var second = Mono16GradientRenderer.Render(layout, 1, 1);

        CollectionAssert.AreEqual(first, second);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 128, 0, 128, 255, 255 }, first);
    }

    [TestMethod]
    public void Render_WithHigherExposure_IncreasesNonSaturatedSample()
    {
        var layout = new ImageLayout(3, 1, CameraPixelFormat.Mono16, 6);
        var low = Mono16GradientRenderer.Render(layout, 0.25, 1);
        var high = Mono16GradientRenderer.Render(layout, 0.5, 1);

        Assert.IsTrue(BitConverter.ToUInt16(high, 2) > BitConverter.ToUInt16(low, 2));
    }
}
