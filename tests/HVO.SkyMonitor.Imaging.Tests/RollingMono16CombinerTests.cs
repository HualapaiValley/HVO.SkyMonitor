using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
public sealed class RollingMono16CombinerTests
{
    [TestMethod]
    public void Add_DuringWarmup_AveragesAvailableFramesAndRecordsProvenance()
    {
        var combiner = new RollingMono16Combiner(3);
        var first = CreateArtifact(100, TimeSpan.FromSeconds(1));
        var second = CreateArtifact(300, TimeSpan.FromSeconds(2));
        combiner.Add(first);

        var result = combiner.Add(second);

        Assert.AreEqual((ushort)200, BitConverter.ToUInt16(result.PixelData.Span));
        CollectionAssert.AreEqual(new[] { first.ArtifactId, second.ArtifactId }, result.SourceArtifactIds.ToArray());
        Assert.AreEqual(TimeSpan.FromSeconds(3), result.TotalIntegration);
    }

    [TestMethod]
    public void Add_WhenWindowIsFull_RemovesOldestFrame()
    {
        var combiner = new RollingMono16Combiner(2);
        combiner.Add(CreateArtifact(100, TimeSpan.FromSeconds(1)));
        var second = CreateArtifact(200, TimeSpan.FromSeconds(1));
        combiner.Add(second);
        var third = CreateArtifact(400, TimeSpan.FromSeconds(1));

        var result = combiner.Add(third);

        Assert.AreEqual((ushort)300, BitConverter.ToUInt16(result.PixelData.Span));
        CollectionAssert.AreEqual(new[] { second.ArtifactId, third.ArtifactId }, result.SourceArtifactIds.ToArray());
    }

    [TestMethod]
    public void Add_WhenDimensionsChange_ResetsWindow()
    {
        var combiner = new RollingMono16Combiner(2);
        combiner.Add(CreateArtifact(100, TimeSpan.FromSeconds(1)));
        var changed = new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw,
            new CameraFrame(DateTimeOffset.UnixEpoch, 2, 1, CameraPixelFormat.Mono16, new byte[] { 44, 1, 44, 1 }, new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0)));

        var result = combiner.Add(changed);

        Assert.AreEqual(1, result.SourceArtifactIds.Count);
        Assert.AreEqual((ushort)300, BitConverter.ToUInt16(result.PixelData.Span));
    }

    private static FrameArtifact CreateArtifact(ushort value, TimeSpan exposure)
        => new(Guid.NewGuid(), FrameArtifactRole.Raw,
            new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono16,
                new byte[] { (byte)value, (byte)(value >> 8) }, new FrameMetadata(exposure, 1, 0)));
}
