using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class ArtifactUploadManifestTests
{
    [TestMethod]
    public void IdempotencyKey_IsStableForSameArtifactIdentity()
    {
        var frameId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var first = ArtifactUploadManifest.ComputeIdempotencyKey("agent-a", frameId, FrameArtifactRole.Raw, "raw-v1");
        var second = ArtifactUploadManifest.ComputeIdempotencyKey("agent-a", frameId, FrameArtifactRole.Raw, "raw-v1");
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void IdempotencyKey_ChangesWhenRecipeChanges()
    {
        var frameId = Guid.NewGuid();
        var first = ArtifactUploadManifest.ComputeIdempotencyKey("agent-a", frameId, FrameArtifactRole.Preview, "preview-v1");
        var second = ArtifactUploadManifest.ComputeIdempotencyKey("agent-a", frameId, FrameArtifactRole.Preview, "preview-v2");
        Assert.AreNotEqual(first, second);
    }
}
