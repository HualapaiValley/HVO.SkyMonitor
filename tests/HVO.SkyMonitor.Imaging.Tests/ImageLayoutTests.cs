using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
public sealed class ImageLayoutTests
{
    [TestMethod]
    public void Validate_RejectsStrideShorterThanPackedPixels()
    {
        var layout = new ImageLayout(2, 1, CameraPixelFormat.Mono16, 3);
        Assert.Throws<ArgumentOutOfRangeException>(layout.Validate);
    }

    [TestMethod]
    public void ImageBuffer_RejectsMismatchedLength()
    {
        var layout = new ImageLayout(2, 2, CameraPixelFormat.Mono8, 2);
        Assert.Throws<ArgumentException>(() => ImageBuffer.Validate(layout, new byte[3]));
    }
}
