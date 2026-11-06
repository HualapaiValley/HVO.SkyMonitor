using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class PresentationReferenceLineageTests
{
    private static readonly string SceneIdentity = new('A', 64);
    private static readonly string FactsIdentity = new('B', 64);
    private static readonly string LayerIdentity = new('C', 64);
    private static readonly string OtherIdentity = new('D', 64);

    [TestMethod]
    public void RichReferenceSeparatelyBindsFactsAndProjectedScene()
    {
        var (reference, layer, facts, scene) = RichLineage();
        Assert.IsTrue(ArtifactIngestService.MatchesPresentationReference(reference, layer));
        Assert.IsFalse(ArtifactIngestService.HasStructuredSourceIdentityMismatch(layer));
        facts.MediaType = facts.MediaType.ToUpperInvariant();
        scene.MediaType = scene.MediaType.ToUpperInvariant();
        Assert.IsTrue(ArtifactIngestService.MatchesPresentationReference(reference, layer));
    }

    [TestMethod]
    [DataRow("facts-identity")]
    [DataRow("facts-missing")]
    [DataRow("facts-schema")]
    [DataRow("facts-media")]
    [DataRow("facts-frame")]
    [DataRow("facts-device")]
    [DataRow("facts-unavailable")]
    [DataRow("scene-identity")]
    [DataRow("scene-missing")]
    [DataRow("scene-schema")]
    [DataRow("scene-order")]
    [DataRow("scene-frame")]
    [DataRow("scene-device")]
    [DataRow("scene-unavailable")]
    [DataRow("dimensions")]
    [DataRow("content")]
    public void RichReferenceRejectsMissingOrMismatchedBindings(string mismatch)
    {
        var (reference, layer, facts, scene) = RichLineage();
        switch (mismatch)
        {
            case "facts-identity": layer.StructuredProduct!.SourceIdentitySha256 = OtherIdentity; break;
            case "facts-missing": layer.Sources.Clear(); break;
            case "facts-schema": facts.StructuredProduct!.ProductSchemaVersion = PresentationMetadataFactsProductV1.CurrentSchemaVersion; break;
            case "facts-media": facts.MediaType = PresentationLayerPayloadJson.MediaType; break;
            case "facts-frame": facts.CentralFrameId = Guid.NewGuid(); break;
            case "facts-device": facts.DevicePublicId = Guid.NewGuid(); break;
            case "facts-unavailable": facts.ReconstructionState = CentralReconstructionState.PendingReference; break;
            case "scene-identity": reference.ExpectedCoordinateIdentitySha256 = OtherIdentity; break;
            case "scene-missing": facts.Sources.Clear(); break;
            case "scene-schema": scene.StructuredProduct!.ProductSchemaVersion = "projected-scene-v999"; break;
            case "scene-order": facts.Sources.Single().Ordinal = 1; break;
            case "scene-frame": scene.CentralFrameId = Guid.NewGuid(); break;
            case "scene-device": scene.DevicePublicId = Guid.NewGuid(); break;
            case "scene-unavailable": scene.ObjectState = CentralArtifactObjectState.Quarantined; break;
            case "dimensions": reference.ExpectedWidthPixels++; break;
            case "content": reference.ExpectedProductIdentitySha256 = OtherIdentity; break;
            default: Assert.Fail($"Unknown mismatch {mismatch}"); break;
        }
        Assert.IsFalse(ArtifactIngestService.MatchesPresentationReference(reference, layer), mismatch);
    }

    [TestMethod]
    [DataRow(PresentationLayerPayloadV1.PreviousSchemaVersion)]
    [DataRow(PresentationLayerPayloadV1.SemanticSchemaVersion)]
    [DataRow(PresentationLayerPayloadV1.CurrentSchemaVersion)]
    public void LegacyReferenceKeepsPinnedDirectCoordinateIdentityComparison(string schema)
    {
        var (reference, layer, _, _) = RichLineage();
        layer.StructuredProduct!.ProductSchemaVersion = schema;
        layer.Sources.Clear();
        layer.StructuredProduct.SourceIdentitySha256 = SceneIdentity;
        Assert.IsTrue(ArtifactIngestService.MatchesPresentationReference(reference, layer));
        layer.StructuredProduct.SourceIdentitySha256 = FactsIdentity;
        Assert.IsFalse(ArtifactIngestService.MatchesPresentationReference(reference, layer));
    }

    private static (CentralArtifactSource Reference, CentralArtifact Layer, CentralArtifact Facts, CentralArtifact Scene) RichLineage()
    {
        var frame = Guid.NewGuid();
        var device = Guid.NewGuid();
        CentralArtifact Artifact(string mediaType, string schema, string identity) => new()
        {
            ArtifactId = Guid.NewGuid(),
            CentralFrameId = frame,
            DevicePublicId = device,
            MediaType = mediaType,
            ReconstructionState = CentralReconstructionState.Complete,
            StructuredProduct = new() { ProductSchemaVersion = schema, ContentIdentitySha256 = identity }
        };
        var scene = Artifact(StructuredProcessingProductContracts.ProjectedSceneMediaType, ProjectedSceneV1.CurrentSchemaVersion, SceneIdentity);
        var facts = Artifact(PresentationMetadataFactsProductV1.MediaType, PresentationMetadataFactsProductV1.RichTextSchemaVersion, FactsIdentity);
        facts.Sources.Add(new() { SourceArtifactId = scene.ArtifactId, ResolvedCentralArtifactId = scene.Id, ResolvedArtifact = scene });
        var layer = Artifact(PresentationLayerPayloadJson.MediaType, PresentationLayerPayloadV1.RichTextSchemaVersion, LayerIdentity);
        layer.StructuredProduct!.SourceIdentitySha256 = FactsIdentity;
        layer.StructuredProduct.PresentationWidthPixels = 320;
        layer.StructuredProduct.PresentationHeightPixels = 240;
        layer.Sources.Add(new() { SourceArtifactId = facts.ArtifactId, ResolvedCentralArtifactId = facts.Id, ResolvedArtifact = facts });
        var reference = new CentralArtifactSource
        {
            Ordinal = 1,
            ExpectedMediaType = PresentationLayerPayloadJson.MediaType,
            ExpectedProductIdentitySha256 = LayerIdentity,
            ExpectedWidthPixels = 320,
            ExpectedHeightPixels = 240,
            ExpectedCoordinateIdentitySha256 = SceneIdentity
        };
        return (reference, layer, facts, scene);
    }
}
