using System.Text;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.AgentCore.Tests;

/// <summary>
/// Issue #521 adds the selected catalog ID and package version to scene provenance as trailing optional members.
/// Frames recorded before the change must keep their exact bytes, digest, and meaning.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SceneProvenanceCatalogIdentityTests
{
    [TestMethod]
    public void PersistedSceneWithoutCatalogIdentityKeepsItsBytesAndDeserializes()
    {
        var manifest = CreateManifest(CreateScene());
        var bytes = CaptureContractJson.Serialize(manifest);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("catalogId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("catalogPackageVersion", text, StringComparison.OrdinalIgnoreCase);
        var parsed = CaptureContractJson.ParseManifest(bytes);
        Assert.IsTrue(parsed.IsValid);
        var scene = parsed.Document!.Manifest.Scene!;
        Assert.IsNull(scene.CatalogId);
        Assert.IsNull(scene.CatalogPackageVersion);
        Assert.AreEqual("HYG", scene.CatalogName);
        Assert.AreEqual("4.2", scene.CatalogVersion);
        CollectionAssert.AreEqual(bytes, CaptureContractJson.Serialize(parsed.Document.Manifest));
        Assert.AreEqual(CaptureContractJson.ComputeManifestSha256(manifest),
            CaptureContractJson.ComputeManifestSha256(parsed.Document.Manifest));
    }

    [TestMethod]
    public void SelectedCatalogIdentityRoundTripsAndIsBoundIntoTheManifestDigest()
    {
        var legacy = CreateManifest(CreateScene());
        var selected = CreateManifest(CreateScene() with
        {
            CatalogId = "hyg-v44-production",
            CatalogPackageVersion = "hyg-v4.4-p4-s3-r1"
        });

        var parsed = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(selected));

        Assert.IsTrue(parsed.IsValid);
        Assert.AreEqual("hyg-v44-production", parsed.Document!.Manifest.Scene!.CatalogId);
        Assert.AreEqual("hyg-v4.4-p4-s3-r1", parsed.Document.Manifest.Scene.CatalogPackageVersion);
        Assert.AreNotEqual(CaptureContractJson.ComputeManifestSha256(legacy), CaptureContractJson.ComputeManifestSha256(selected));
    }

    private static SceneProvenance CreateScene()
        => new("scene", "rig-v1", "HYG", "4.2", new string('A', 64),
            "EquidistantFisheye", "projection-v1", "astronomy-v1", "sensor-v1");

    private static ArtifactManifestV2 CreateManifest(SceneProvenance scene)
        => ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]) with { Scene = scene };
}
