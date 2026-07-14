using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DeterministicStarFieldRendererTests
{
    [TestMethod]
    public void RenderMono16_WithSameInputs_IsStable()
    {
        var layout = new ImageLayout(16, 8, CameraPixelFormat.Mono16, 32);
        var first = DeterministicStarFieldRenderer.RenderMono16(layout, DateTimeOffset.UnixEpoch, 42, 10, 1, 1);
        var second = DeterministicStarFieldRenderer.RenderMono16(layout, DateTimeOffset.UnixEpoch, 42, 10, 1, 1);
        CollectionAssert.AreEqual(first, second);
    }

    [TestMethod]
    public void RenderMono16_WhenMinuteChanges_MovesStars()
    {
        var layout = new ImageLayout(16, 8, CameraPixelFormat.Mono16, 32);
        var first = DeterministicStarFieldRenderer.RenderMono16(layout, DateTimeOffset.UnixEpoch, 42, 10, 1, 1);
        var later = DeterministicStarFieldRenderer.RenderMono16(layout, DateTimeOffset.UnixEpoch.AddMinutes(1), 42, 10, 1, 1);
        CollectionAssert.AreNotEqual(first, later);
    }
}
