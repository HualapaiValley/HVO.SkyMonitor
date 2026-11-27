using System.Text.Json.Nodes;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.AgentCore.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class SceneProvenanceCatalogIdentityTests
{
    [TestMethod]
    public void IndependentDeepSkyIdentityRoundTripsAndChangesTheFrameManifestDigest()
    {
        var identity = new CatalogSnapshotIdentity("openngc-fixture", "openngc-fixture-1", "5", "6", new string('a', 64));
        var source = CreateManifest(CreateScene());
        var selected = CreateManifest(CreateScene() with { DeepSkyCatalog = identity });
        var parsed = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(selected));
        Assert.IsTrue(parsed.IsValid);
        Assert.AreEqual(identity, parsed.Document!.Manifest.Scene!.DeepSkyCatalog);
        Assert.AreNotEqual(CaptureContractJson.ComputeManifestSha256(source), CaptureContractJson.ComputeManifestSha256(selected));
        Assert.AreEqual(source.Scene!.CatalogChecksumSha256, selected.Scene!.CatalogChecksumSha256);
    }

    [TestMethod]
    [DataRow("catalogId", null)]
    [DataRow("catalogChecksumSha256", null)]
    [DataRow("catalogId", "")]
    [DataRow("catalogChecksumSha256", "invalid")]
    public void PersistedSceneWithoutValidCatalogIdentityIsRefused(string field, string? invalidValue)
    {
        var manifest = JsonNode.Parse(CaptureContractJson.Serialize(CreateManifest(CreateScene())))!;
        var scene = manifest["scene"]!.AsObject();
        if (invalidValue is null)
            scene.Remove(field);
        else
            scene[field] = invalidValue;

        Assert.IsFalse(CaptureContractJson.ParseManifest(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest)).IsValid);
    }

    [TestMethod]
    public void SelectedCatalogIdentityRoundTripsAndIsBoundIntoTheManifestDigest()
    {
        var original = CreateManifest(CreateScene());
        var selected = CreateManifest(CreateScene() with
        {
            CatalogId = "hyg-v44-production",
            CatalogPackageVersion = "hyg-v4.4-p4-s3-r1"
        });

        var parsed = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(selected));

        Assert.IsTrue(parsed.IsValid);
        Assert.AreEqual("hyg-v44-production", parsed.Document!.Manifest.Scene!.CatalogId);
        Assert.AreEqual("hyg-v4.4-p4-s3-r1", parsed.Document.Manifest.Scene.CatalogPackageVersion);
        Assert.AreNotEqual(CaptureContractJson.ComputeManifestSha256(original), CaptureContractJson.ComputeManifestSha256(selected));
    }

    private static SceneProvenance CreateScene()
        => new("scene", "rig-v1", "HYG", "4.4", new string('A', 64),
            "EquidistantFisheye", "projection-v1", "astronomy-v1", "sensor-v1", CatalogId: "test-catalog");

    private static ArtifactManifestV2 CreateManifest(SceneProvenance scene)
        => ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]) with { Scene = scene };
}
