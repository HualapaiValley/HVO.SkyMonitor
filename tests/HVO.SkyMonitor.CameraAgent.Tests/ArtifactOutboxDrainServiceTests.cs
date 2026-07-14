using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ArtifactOutboxDrainServiceTests
{
    [TestMethod]
    public void ShouldRemoveUploadedArtifact_WhenRawArtifactIsHeldByIngress_ReturnsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "raw-ingress");

        Assert.IsFalse(ArtifactOutboxDrainService.ShouldRemoveUploadedArtifact(root, root, TestManifest(FrameArtifactRole.Raw)));
        Assert.IsTrue(ArtifactOutboxDrainService.ShouldRemoveUploadedArtifact(root, root, TestManifest(FrameArtifactRole.Preview)));
    }

    [TestMethod]
    public void ResolveStorageRoot_WhenOptionalStorageIsNotConfigured_ReturnsNull()
    {
        var config = new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)),
            AgentId: "agent");

        Assert.IsNull(ArtifactOutboxDrainService.ResolveStorageRoot(config));
    }

    [TestMethod]
    [DataRow(1, 10)]
    [DataRow(2, 20)]
    [DataRow(3, 40)]
    [DataRow(4, 60)]
    [DataRow(30, 60)]
    public void CalculateRetryDelay_UsesBoundedExponentialBackoff(int attempt, int expectedSeconds)
    {
        var delay = ArtifactOutboxDrainService.CalculateRetryDelay(
            attempt,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(60));

        Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    private static ArtifactUploadManifest TestManifest(FrameArtifactRole role)
        => new(
            ArtifactUploadManifest.CurrentSchemaVersion,
            "agent",
            Guid.NewGuid(),
            Guid.NewGuid(),
            role,
            "application/octet-stream",
            1,
            new string('A', 64),
            DateTimeOffset.UnixEpoch,
            "recipe-v1",
            "frames/2026-07-14/raw.bin");
}
