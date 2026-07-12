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

    [TestMethod]
    public async Task SharedSceneFixture_ConformsAcrossEveryProjectionModel()
    {
        var results = await ProjectionConformanceFixture.BuildReferenceScenesAsync().ConfigureAwait(false);

        Assert.HasCount(5, results);
        foreach (var result in results)
        {
            Assert.HasCount(3, result.Objects);
            var center = result.Objects.Single(item => item.Id == "center").Pixel;
            Assert.IsTrue(Math.Sqrt(Math.Pow(center.X - 100, 2) + Math.Pow(center.Y - 100, 2)) < 1);
            Assert.IsTrue(result.Objects.Any(item => item.Id == "solar-system:Jupiter"));
            Assert.IsGreaterThan(0, result.Segments.Count);
        }
    }
}
