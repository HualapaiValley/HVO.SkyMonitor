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

    [TestMethod]
    public void Validate_WithCurrentContract_Succeeds()
    {
        var manifest = CreateManifest();

        manifest.Validate();
    }

    [TestMethod]
    [DataRow("../payload.bin")]
    [DataRow("frames/../payload.bin")]
    [DataRow("/frames/payload.bin")]
    [DataRow("C:\\frames\\payload.bin")]
    public void Validate_WithUnsafeRelativePath_Throws(string path)
    {
        var manifest = CreateManifest() with { RelativeArtifactPath = path };

        Assert.ThrowsExactly<ArgumentException>(manifest.Validate);
    }

    private static ArtifactUploadManifest CreateManifest() => new(
        ArtifactUploadManifest.CurrentSchemaVersion,
        "agent-a",
        Guid.NewGuid(),
        Guid.NewGuid(),
        FrameArtifactRole.Raw,
        "application/octet-stream",
        4,
        new string('A', 64),
        DateTimeOffset.UnixEpoch,
        "raw-v1",
        "frames/payload.bin");
}
