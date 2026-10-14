using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

public sealed partial class HostRecipeContractConformanceTests
{
    [TestMethod]
    [DataRow(ProjectedSceneKind.Predicted)]
    [DataRow(ProjectedSceneKind.VirtualRenderAuthoritative)]
    public async Task CentralSceneMetadataCanDriveDeterministicToggleablePresentation(ProjectedSceneKind kind)
    {
        var source = Source(CameraPixelFormat.Mono8) with
        { CaptureId = Guid.NewGuid(), DescriptorIdentitySha256 = new string('A', 64) };
        var utc = ProcessingConformanceFixture.CapturedUtc;
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("zenith", "Zenith", AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15, 0, 1)
        ])).BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.EquidistantFisheye, 4, 4, 2, 2, 8, 8, ProjectionAperture.Circular,
                ImageCircleRadiusPixels: 3.5, BoresightAltitudeDegrees: 90), new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "equidistant-v1"));
        var scene = ProjectedSceneJson.Create(kind, visible, ProjectedSceneImageTransformV1.Identity(8, 8),
            new ProjectedSceneSource(source.CaptureId.Value, source.ArtifactId, source.DescriptorIdentitySha256),
            "calibration-v1", visible.Request.ProjectionVersion);
        var payload = ProjectedSceneJson.Serialize(scene);
        var auxiliary = new ProcessingAuxiliaryInput("scene", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: ProjectedSceneV1.CurrentSchemaVersion, IdentitySha256: scene.SceneIdentitySha256, Payload: payload)
        { ChecksumSha256 = PayloadChecksum.ComputeSha256(payload) };
        var recorder = new RecordingExecutor();
        async Task<ProcessingOutcome> Run(ProcessingArtifact input, ProcessingAuxiliaryInput? facts) => await new LogicHostRecipeExecutionAdapter(recorder)
            .ExecuteAsync([new LogicHostProcessingInput(null, input.Payload, Artifact: input)], BuiltInProcessingRecipes.ProjectedScene,
                JsonSerializer.SerializeToElement(new { }), ProcessingInputSelector.Raw("source"), "scene", auxiliaryInputs: facts is null ? [] : [facts]);
        var result = await Run(source, auxiliary);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status, result.ReasonCode);
        var product = Assert.ContainsSingle(result.Products);
        AssertContract(recorder.Request!, product);
        Assert.AreEqual(scene.SceneIdentitySha256, product.ContentIdentitySha256);
        Assert.IsNull(product.Layout);
        CollectionAssert.AreEqual(payload, product.Payload.ToArray());
        var sceneArtifact = AsTypedArtifact(product);
        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene,
            new PresentationAnnotationStyleV1(MarkerRadius: 1, CardinalScale: 1));
        var packed = await Execute(source, BuiltInProcessingRecipes.EncodedPreview, new EncodedPreviewOptions(OutputEncoding: "Packed"));
        var baseArtifact = AsTypedArtifact(packed);
        var reference = PresentationProcessingProducts.CreateReference(baseArtifact, source.DescriptorIdentitySha256);
        var layers = new[] { groups.StarAnnotations, groups.CardinalDirections, groups.ImageCircle, groups.Constellations }
            .Select((layerPayload, index) =>
            {
                var layerProduct = PresentationProcessingProducts.CreateLayerProduct(layerPayload, $"layer-{index}",
                    [sceneArtifact], PresentationLayerProducers.SceneProducerVersion);
                var artifact = AsTypedArtifact(layerProduct);
                var layer = LayeredPresentationJson.CreateLayer($"layer-{index}",
                    new PresentationProductReference(artifact.ArtifactId, layerPayload.ContentIdentitySha256, artifact.MediaType, reference.Compatibility),
                    null, PresentationCoordinateSpace.ScenePixels, PresentationLayerCompositor.AlgorithmVersion, "host-conformance-v1",
                    index, PresentationBlendMode.Normal, 1_000_000, true, JsonSerializer.SerializeToElement(new { }));
                return new PresentationLayerProductInput(layer, artifact);
            }).ToArray();
        var manifest = LayeredPresentationJson.CreateManifest(reference, null, layers.Select(l => l.Layer));
        var manifestArtifact = AsTypedArtifact(PresentationProcessingProducts.CreateManifestProduct(reference, null, layers, "manifest", baseArtifact));
        var enabled = layers.Select(l => l.Layer.LayerIdentitySha256).ToArray();
        var rendered = PresentationMaterializationExecutor.MaterializePacked(baseArtifact, manifestArtifact, manifest, layers, enabled, "rendered");
        var repeated = PresentationMaterializationExecutor.MaterializePacked(baseArtifact, manifestArtifact, manifest, layers, enabled, "rendered");
        Assert.AreEqual(rendered.OutputIdentitySha256, repeated.OutputIdentitySha256);
        CollectionAssert.AreEqual(rendered.Payload.ToArray(), repeated.Payload.ToArray());
        Assert.IsFalse(baseArtifact.Payload.Span.SequenceEqual(rendered.Payload.Span));
        var off = PresentationMaterializationExecutor.MaterializePacked(baseArtifact, manifestArtifact, manifest, layers, [], "rendered");
        CollectionAssert.AreEqual(baseArtifact.Payload.ToArray(), off.Payload.ToArray());
        Assert.AreNotEqual(off.OutputIdentitySha256, rendered.OutputIdentitySha256);
        CollectionAssert.AreEquivalent(new[] { baseArtifact.ArtifactId, manifestArtifact.ArtifactId }.Concat(layers.Select(l => l.Product.ArtifactId)).ToArray(), rendered.SourceArtifactIds.ToArray());

        foreach (var (input, facts, reason) in new (ProcessingArtifact, ProcessingAuxiliaryInput?, string)[]
        {
            (source, null, ProcessingReasonCodes.MissingProjectedScene),
            (source with { CaptureId = Guid.NewGuid() }, auxiliary, ProcessingReasonCodes.ProjectedSceneSourceMismatch),
            (source with { DescriptorIdentitySha256 = new string('B', 64) }, auxiliary, ProcessingReasonCodes.ProjectedSceneDescriptorMismatch),
            (source with { Layout = source.Layout! with { Width = 7 } }, auxiliary, ProcessingReasonCodes.ProjectedSceneDimensionMismatch),
            (source, auxiliary with { ChecksumSha256 = new string('F', 64) }, ProcessingReasonCodes.InvalidInput),
            (source, auxiliary with { IdentitySha256 = new string('F', 64) }, ProcessingReasonCodes.InvalidProjectedScene)
        })
        {
            var rejected = await Run(input, facts);
            Assert.AreEqual(reason, rejected.ReasonCode);
            Assert.HasCount(0, rejected.Products);
        }
    }

    private static ProcessingArtifact AsTypedArtifact(ProcessingProduct product) => AsArtifact(product) with
    { ProductKind = product.Kind, SchemaVersion = product.SchemaVersion, ContentIdentitySha256 = product.ContentIdentitySha256 };
}
