using System;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Controllers.v1;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Controllers;

[TestClass]
[TestCategory("Unit")]
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
