using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Imaging;
using HVO.SkyMonitor.CameraAgent.Controllers.v1;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Controllers;

[TestClass]
public sealed class FramesControllerTests
{
    [TestMethod]
    public void GetLatest_WithMonoFrame_ReturnsJpegFile()
    {
        var accessor = new LatestFrameAccessor();
        var frame = new CameraFrame(
            DateTimeOffset.UtcNow,
            Width: 2,
            Height: 2,
            PixelFormat: CameraPixelFormat.Mono8,
            PixelData: new byte[]
            {
                0, 64,
                128, 255
            },
            Metadata: new FrameMetadata(TimeSpan.FromMilliseconds(10), 1000, 2, "UnitTest", null));
        accessor.Update(frame);

        var controller = new FramesController(accessor, NullLogger<FramesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var actionResult = controller.GetLatest();
        var fileResult = actionResult as FileContentResult;

        Assert.IsNotNull(fileResult, "Expected FileContentResult from controller response");
        Assert.AreEqual("image/jpeg", fileResult.ContentType);
        Assert.IsTrue(fileResult.FileContents.Length > 0, "JPEG payload should not be empty");
    }
}
