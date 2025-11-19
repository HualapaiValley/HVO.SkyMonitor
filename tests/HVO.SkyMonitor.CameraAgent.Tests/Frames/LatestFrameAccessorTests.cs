using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Frames;

namespace HVO.SkyMonitor.CameraAgent.Tests.Frames;

[TestClass]
public sealed class LatestFrameAccessorTests
{
    [TestMethod]
    public void Update_ReplacesSnapshotAndClonesPixelData()
    {
        var accessor = new LatestFrameAccessor();
        var pixels = new byte[] { 1, 2, 3, 4 };
        var expected = (byte[])pixels.Clone();
        var frame = new CameraFrame(
            DateTimeOffset.UtcNow,
            Width: 2,
            Height: 2,
            PixelFormat: CameraPixelFormat.Mono8,
            PixelData: pixels,
            Metadata: new FrameMetadata(TimeSpan.FromMilliseconds(10), 1, 20, "Random", null));

        accessor.Update(frame);
        var success = accessor.TryGetSnapshot(out var snapshot);

        Assert.IsTrue(success);
        Assert.IsNotNull(snapshot);
        CollectionAssert.AreEqual(expected, snapshot!.PixelData.ToArray());

        // Mutate original array to ensure snapshot holds its own copy
        pixels[0] = 255;
        Assert.AreEqual(expected[0], snapshot.PixelData.Span[0]);
    }
}
