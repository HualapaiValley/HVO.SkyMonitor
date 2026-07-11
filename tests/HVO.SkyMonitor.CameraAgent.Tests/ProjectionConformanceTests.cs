using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class ProjectionConformanceTests
{
    [TestMethod]
    public void SharedProjectionFixture_ReturnsExpectedPixel()
    {
        var pixel = ProjectionConformanceFixture.ProjectReferenceDirection();
        Assert.AreEqual(157.73502691896257d, pixel.X, 1e-10);
        Assert.AreEqual(100d, pixel.Y, 1e-10);
    }
}
