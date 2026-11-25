using System;
using System.Buffers.Binary;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Controllers.v1;
using HVO.SkyMonitor.Imaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Controllers;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Hosting")]
public class FramesControllerTests
{
    [TestMethod]
    public void GetLatest_WhenNoFrame_ReturnsNotFound()
    {
        var accessor = new Mock<ILatestFrameAccessor>(MockBehavior.Strict);
        LatestFrameSnapshot? snapshot = null;
        accessor.Setup(a => a.TryGetSnapshot(FrameArtifactRole.Preview, out snapshot)).Returns(false);

        var controller = CreateController(accessor.Object);

        var result = controller.GetLatest();

        Assert.IsInstanceOfType<NotFoundResult>(result);
    }

    [TestMethod]
    public void GetLatest_WhenRgbPixelFormatAvailable_ReturnsJpeg()
    {
        var accessor = new Mock<ILatestFrameAccessor>(MockBehavior.Strict);
        var snapshot = new LatestFrameSnapshot(
            DateTimeOffset.UtcNow,
            2,
            2,
            CameraPixelFormat.Rgb24,
            new byte[12]);
        accessor.Setup(a => a.TryGetSnapshot(FrameArtifactRole.Preview, out snapshot)).Returns(true);

        var controller = CreateController(accessor.Object);

        var result = controller.GetLatest();

        var fileResult = result as FileContentResult;
        Assert.IsNotNull(fileResult);
        Assert.AreEqual("image/jpeg", fileResult.ContentType);
        Assert.IsTrue(fileResult.FileContents.Length > 0);
    }

    [TestMethod]
    public void GetLatest_WhenFrameAvailable_ReturnsJpeg()
    {
        var accessor = new Mock<ILatestFrameAccessor>(MockBehavior.Strict);
        var snapshot = new LatestFrameSnapshot(
            DateTimeOffset.UtcNow,
            1,
            1,
            CameraPixelFormat.Mono8,
            new byte[] { 128 });
        accessor.Setup(a => a.TryGetSnapshot(FrameArtifactRole.Preview, out snapshot)).Returns(true);

        var controller = CreateController(accessor.Object);

        var result = controller.GetLatest();

        var fileResult = result as FileContentResult;
        Assert.IsNotNull(fileResult);
        Assert.AreEqual("image/jpeg", fileResult.ContentType);
        Assert.IsTrue(fileResult.FileContents.Length > 0);
        Assert.AreEqual("no-store, no-cache, must-revalidate", controller.Response.Headers.CacheControl.ToString());
        Assert.AreEqual("no-cache", controller.Response.Headers.Pragma.ToString());
        Assert.AreEqual("0", controller.Response.Headers.Expires.ToString());
    }

    [TestMethod]
    public void GetProcessed_WhenMono16FrameAvailable_ReturnsJpeg()
    {
        var accessor = new Mock<ILatestFrameAccessor>(MockBehavior.Strict);
        var snapshot = new LatestFrameSnapshot(
            DateTimeOffset.UtcNow,
            1,
            1,
            CameraPixelFormat.Mono16,
            new byte[] { 0, 128 });
        accessor.Setup(a => a.TryGetSnapshot(FrameArtifactRole.Combined, out snapshot)).Returns(true);

        var controller = CreateController(accessor.Object);

        var result = controller.GetProcessed();

        var fileResult = result as FileContentResult;
        Assert.IsNotNull(fileResult);
        Assert.AreEqual("image/jpeg", fileResult.ContentType);
        Assert.IsTrue(fileResult.FileContents.Length > 0);
    }

    [TestMethod]
    [DataRow(0, 0, BayerPattern.Rggb)]
    [DataRow(1, 0, BayerPattern.Grbg)]
    [DataRow(0, 1, BayerPattern.Gbrg)]
    [DataRow(1, 1, BayerPattern.Bggr)]
    public void GetRaw_WhenBayerOddOriginFrameArrives_PreservesDeliveredCfaPhase(
        int originX, int originY, BayerPattern expectedPattern)
    {
        const int width = 64;
        const int height = 48;
        var raw = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var channel = ((y + originY) & 1, (x + originX) & 1) switch
                {
                    (0, 0) => 0,
                    (1, 1) => 2,
                    _ => 1
                };
                ushort code = 120;
                if (x is >= 8 and <= 20 && y is >= 14 and <= 34)
                {
                    code = channel == 0 ? (ushort)3600 : (ushort)120;
                }
                else if (x is >= 26 and <= 38 && y is >= 14 and <= 34)
                {
                    code = channel == 1 ? (ushort)3600 : (ushort)120;
                }
                else if (x is >= 44 and <= 56 && y is >= 14 and <= 34)
                {
                    code = channel == 2 ? (ushort)3600 : (ushort)120;
                }
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan((y * width + x) * 2), code);
            }
        }

        var layout = new FrameLayoutDescriptor(
            width, height, width * 2, CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian, 12, 16,
            FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb, 64, 4095, raw.Length)
        {
            Readout = new FrameReadoutDescriptor(
                width + originX * 2, height + originY * 2, originX, originY, width, height, 1, 1,
                FrameBinningAlgorithm.IdentityV1, originX, originY)
        };

        var accessor = new LatestFrameAccessor();
        accessor.Update(FrameArtifactRole.Raw, new CameraFrame(
            DateTimeOffset.UtcNow, width, height, CameraPixelFormat.BayerRggb16, raw,
            new FrameMetadata(TimeSpan.FromMilliseconds(250), 10, -10), width * 2)
        {
            Layout = layout
        });
        Assert.IsTrue(accessor.TryGetSnapshot(FrameArtifactRole.Raw, out var snapshot));
        Assert.AreEqual(expectedPattern, snapshot.BayerPattern);

        var controller = CreateController(accessor);
        var result = controller.GetRaw() as FileContentResult;
        Assert.IsNotNull(result);
        Assert.AreEqual("image/jpeg", result.ContentType);

        var decoded = JpegImageCodec.DecodeJpeg(result.FileContents);
        Assert.AreEqual(width, decoded.Width);
        Assert.AreEqual(height, decoded.Height);
        Assert.AreEqual(CameraPixelFormat.Rgb24, decoded.PixelFormat);

        var red = SampleRgb(decoded, 14, 24);
        var green = SampleRgb(decoded, 32, 24);
        var blue = SampleRgb(decoded, 50, 24);
        Assert.IsGreaterThan(red.Blue * 2.0, red.Red, $"{expectedPattern} red target: {red}");
        Assert.IsGreaterThan(red.Green + 30, red.Red, $"{expectedPattern} red target: {red}");
        Assert.IsGreaterThan(green.Red + 30, green.Green, $"{expectedPattern} green target: {green}");
        Assert.IsGreaterThan(green.Blue + 30, green.Green, $"{expectedPattern} green target: {green}");
        Assert.IsGreaterThan(blue.Red * 2.0, blue.Blue, $"{expectedPattern} blue target: {blue}");
        Assert.IsGreaterThan(blue.Green + 30, blue.Blue, $"{expectedPattern} blue target: {blue}");

        static (double Red, double Green, double Blue) SampleRgb(DecodedImage image, int cx, int cy)
        {
            double r = 0, g = 0, b = 0;
            var count = 0;
            var span = image.PixelData.Span;
            for (var y = cy - 2; y <= cy + 2; y++)
            {
                for (var x = cx - 2; x <= cx + 2; x++)
                {
                    var o = y * image.StrideBytes + x * 3;
                    r += span[o];
                    g += span[o + 1];
                    b += span[o + 2];
                    count++;
                }
            }
            return (r / count, g / count, b / count);
        }
    }

    private static FramesController CreateController(ILatestFrameAccessor accessor)
    {
        var logger = Mock.Of<ILogger<FramesController>>();
        var controller = new FramesController(accessor, logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return controller;
    }
}
