using HVO.SkyMonitor.CameraAgent.Common.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Imaging;

[TestClass]
public sealed class SkiaPreviewEncoderTests
{
    [TestMethod]
    public void EncodeMono8ToJpeg_WithValidFrame_ReturnsPayload()
    {
        var pixels = new byte[]
        {
            0, 64,
            128, 255
        };

        var result = SkiaPreviewEncoder.EncodeMono8ToJpeg(2, 2, pixels);

        Assert.IsTrue(result.IsSuccessful);
        Assert.IsNotNull(result.Value);
        Assert.IsTrue(result.Value.Length > 0);
    }

    [TestMethod]
    public void EncodeMono8ToJpeg_WithMismatchedBuffer_Fails()
    {
        var pixels = new byte[] { 0, 1, 2 };

        var result = SkiaPreviewEncoder.EncodeMono8ToJpeg(2, 2, pixels);

        Assert.IsTrue(result.IsFailure);
        Assert.IsNotNull(result.Error);
    }
}
