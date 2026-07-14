using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ImageLayoutTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono8, 1)]
    [DataRow(CameraPixelFormat.Mono16, 2)]
    [DataRow(CameraPixelFormat.Rgb24, 3)]
    [DataRow(CameraPixelFormat.BayerRggb16, 2)]
    public void Validate_AcceptsSupportedFormatsWithPadding(CameraPixelFormat format, int bytesPerPixel)
    {
        var layout = new ImageLayout(3, 2, format, 3 * bytesPerPixel + 2);

        layout.Validate();

        Assert.AreEqual(bytesPerPixel, ImageLayout.BytesPerPixel(format));
        Assert.AreEqual(3 * bytesPerPixel, layout.MinimumStrideBytes);
        Assert.AreEqual((3 * bytesPerPixel + 2) * 2, layout.RequiredByteLength);
        ImageBuffer.Validate(layout, new byte[layout.RequiredByteLength]);
    }

    [TestMethod]
    [DataRow(0, 1, 1)]
    [DataRow(-1, 1, 1)]
    [DataRow(1, 0, 1)]
    [DataRow(1, -1, 1)]
    [DataRow(1, 1, 0)]
    [DataRow(1, 1, -1)]
    public void Validate_RejectsNonPositiveDimensionsAndStride(int width, int height, int stride)
    {
        var layout = new ImageLayout(width, height, CameraPixelFormat.Mono8, stride);
        Assert.Throws<ArgumentOutOfRangeException>(layout.Validate);
    }

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

    [TestMethod]
    public void Validate_RejectsOverflowingDimensions()
    {
        var layout = new ImageLayout(int.MaxValue, 2, CameraPixelFormat.Mono16, int.MaxValue);
        Assert.Throws<ArgumentOutOfRangeException>(layout.Validate);
    }

    [TestMethod]
    public void Properties_ExposeCheckedOverflow()
    {
        var strideOverflow = new ImageLayout(int.MaxValue, 1, CameraPixelFormat.Rgb24, int.MaxValue);
        var lengthOverflow = new ImageLayout(1, 2, CameraPixelFormat.Mono8, int.MaxValue);

        Assert.Throws<OverflowException>(() => _ = strideOverflow.MinimumStrideBytes);
        Assert.Throws<OverflowException>(() => _ = lengthOverflow.RequiredByteLength);
        Assert.Throws<ArgumentOutOfRangeException>(lengthOverflow.Validate);
    }

    [TestMethod]
    public void BytesPerPixel_RejectsUnknownFormat()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ImageLayout.BytesPerPixel((CameraPixelFormat)999));

    [TestMethod]
    public void ImageBuffer_ValidatesLayoutBeforeBufferLength()
    {
        var invalid = new ImageLayout(0, 1, CameraPixelFormat.Mono8, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageBuffer.Validate(invalid, ReadOnlyMemory<byte>.Empty));
    }
}
