using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
#if COMBINED_TESTS
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
#endif
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

public sealed partial class HostRecipeContractConformanceTests
{
    [TestMethod]
    [DataRow(ProjectedSceneKind.Predicted, false, null)]
    [DataRow(ProjectedSceneKind.Predicted, true, null)]
    [DataRow(ProjectedSceneKind.VirtualRenderAuthoritative, false, null)]
    [DataRow(ProjectedSceneKind.VirtualRenderAuthoritative, true, null)]
    [DataRow(ProjectedSceneKind.Predicted, true, 0)]
    [DataRow(ProjectedSceneKind.VirtualRenderAuthoritative, true, 255)]
    [DataRow(ProjectedSceneKind.Predicted, true, null, true)]
    [DataRow(ProjectedSceneKind.VirtualRenderAuthoritative, true, null, true)]
    public async Task CentralSceneMetadataCanDriveDeterministicToggleablePresentation(ProjectedSceneKind kind, bool edited, int? intensity, bool styled = false)
    {
        var source = Source(CameraPixelFormat.Mono8) with
        { CaptureId = Guid.NewGuid(), DescriptorIdentitySha256 = new string('A', 64) };
        var utc = ProcessingConformanceFixture.CapturedUtc;
        var descriptor = ProjectedSceneOptionDescriptors.Descriptor;
        var queryOptions = ProcessingNodeOptionsCodec.Encode(descriptor, JsonSerializer.SerializeToElement(new { }),
            edited ? new Dictionary<string, ProcessingOptionValue>
            {
                ["maximumMagnitude"] = ProcessingOptionValue.FromRealNumber(2),
                ["maximumResults"] = ProcessingOptionValue.FromWholeNumber(1),
                ["includeConstellationEndpointStars"] = ProcessingOptionValue.FromToggle(true)
            } : new Dictionary<string, ProcessingOptionValue>());
        var query = ProcessingNodeOptionsCodec.Decode(descriptor, queryOptions.Options!.Value);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("zenith", "Zenith", AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15, 0, 1),
            new CelestialCatalogObject("faint", "Faint", AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15, 10, 4)
        ])).BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.EquidistantFisheye, 4, 4, 2, 2, 8, 8, ProjectionAperture.Circular,
                ImageCircleRadiusPixels: 3.5, BoresightAltitudeDegrees: 90),
            new CatalogQuery(query.Get("maximumMagnitude").Value.AsRealNumber(), checked((int)query.Get("maximumResults").Value.AsWholeNumber())),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "equidistant-v1", includeConstellationEndpointStars: query.Get("includeConstellationEndpointStars").Value.AsToggle()));
        var scene = ProjectedSceneJson.Create(kind, visible, ProjectedSceneImageTransformV1.Identity(8, 8),
            new ProjectedSceneSource(source.CaptureId.Value, source.ArtifactId, source.DescriptorIdentitySha256),
            "calibration-v1", visible.Request.ProjectionVersion);
        var payload = ProjectedSceneJson.Serialize(scene);
        Assert.HasCount(edited ? 1 : 2, scene.Objects);
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
#if COMBINED_TESTS
        // Central reconstruction consumes retained scene metadata, never a new central catalog query.
        var edge = await new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor())
            .ExecuteAsync(recorder.Request!, CancellationToken.None);
        var edgeProduct = Assert.ContainsSingle(edge.Products);
        CollectionAssert.AreEqual(product.Payload.ToArray(), edgeProduct.Payload.ToArray());
        Assert.AreEqual(product.OutputIdentitySha256, edgeProduct.OutputIdentitySha256);
        CollectionAssert.AreEqual(product.SourceArtifactIds.ToArray(), edgeProduct.SourceArtifactIds.ToArray());
#endif
        var sceneArtifact = AsTypedArtifact(product);
        var sceneOptions = ProcessingNodeOptionsDescriptors.ScenePresentationLayer;
        var originalOptions = JsonSerializer.Deserialize<JsonElement>("{ \"future\":[1e+02] }");
        var intensityKeys = new[] { "markerValue", "labelValue", "imageCircleValue", "cardinalValue" };
        var intensityOptions = ProcessingNodeOptionsCodec.Encode(sceneOptions, originalOptions,
            intensityKeys.ToDictionary(static key => key, _ => ProcessingOptionValue.FromNullableWholeNumber(intensity)));
        Assert.IsTrue(intensityOptions.IsValid);
        PresentationAnnotationStyleV1 Style(JsonElement options)
        {
            var decoded = ProcessingNodeOptionsCodec.Decode(sceneOptions, options);
            PresentationColor? Color(string key) => decoded.Get(key).Value.AsNullableWholeNumber() is { } value
                ? new PresentationColor((byte)value, (byte)value, (byte)value) : null;
            return new(MarkerRadius: 1, CardinalScale: 1, MarkerColor: Color("markerValue"), LabelColor: Color("labelValue"),
                ImageCircleColor: Color("imageCircleValue"), CardinalColor: Color("cardinalValue"));
        }
        var groups = styled ? SceneStylePresentation.Create(scene,
            new(Style(intensityOptions.Options!.Value) with { MarkerColor = new(240, 100, 25) }, "PlexRegular", "MonoRegular", "Crosshair"))
            : PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, Style(intensityOptions.Options!.Value));
        if (styled)
        {
            Assert.IsTrue(groups.StarAnnotations.Markers.All(marker => marker.Crosshair));
            Assert.AreEqual(PresentationLayerPayloadV1.SceneStyleSchemaVersion, groups.StarAnnotations.SchemaVersion);
            Assert.IsNotNull(groups.StarAnnotations.SceneStyleIdentitySha256);
        }
        var clearOptions = ProcessingNodeOptionsCodec.Encode(sceneOptions, intensityOptions.Options.Value,
            intensityKeys.ToDictionary(static key => key, _ => ProcessingOptionValue.FromNullableWholeNumber(null)));
        var restoredGroups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, Style(clearOptions.Options!.Value));
        var originalGroups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, Style(originalOptions));
        CollectionAssert.AreEqual(PresentationLayerPayloadJson.Serialize(originalGroups.StarAnnotations), PresentationLayerPayloadJson.Serialize(restoredGroups.StarAnnotations));
        var environmentDescriptor = RetainedLayerOptionDescriptors.Environment;
        var environmentOptions = ProcessingNodeOptionsCodec.Encode(environmentDescriptor, originalOptions,
            new Dictionary<string, ProcessingOptionValue> { ["value"] = ProcessingOptionValue.FromNullableWholeNumber(intensity) });
        var gray = ProcessingNodeOptionsCodec.Decode(environmentDescriptor, environmentOptions.Options!.Value).Get("value").Value.AsNullableWholeNumber();
        var environment = PresentationLayerProducers.FromMetadataFacts(new PresentationMetadataFactsV1(scene.SceneIdentitySha256,
            ["Temperature unavailable"], ["Humidity stale"], [], []), 8, 8,
            gray is { } value ? new PresentationColor((byte)value, (byte)value, (byte)value) : null);
        var packed = await Execute(source, BuiltInProcessingRecipes.EncodedPreview, new EncodedPreviewOptions(OutputEncoding: "Packed"));
        var baseArtifact = AsTypedArtifact(packed);
        var reference = PresentationProcessingProducts.CreateReference(baseArtifact, source.DescriptorIdentitySha256);
        var layers = new[] { groups.StarAnnotations, groups.CardinalDirections, groups.ImageCircle, groups.Constellations, environment }
            .Select((layerPayload, index) =>
            {
                var layerProduct = PresentationProcessingProducts.CreateLayerProduct(layerPayload, $"layer-{index}",
                    [sceneArtifact], index == 4 ? PresentationLayerProducers.MetadataProducerVersion : styled ? SceneStylePresentation.ProducerVersion : PresentationLayerProducers.SceneProducerVersion);
                var artifact = AsTypedArtifact(layerProduct);
                var layer = LayeredPresentationJson.CreateLayer($"layer-{index}",
                    new PresentationProductReference(artifact.ArtifactId, layerPayload.ContentIdentitySha256, artifact.MediaType, reference.Compatibility),
                    null, PresentationCoordinateSpace.ScenePixels, styled && index != 4 ? PresentationLayerCompositor.SceneStyleAlgorithmVersion : PresentationLayerCompositor.AlgorithmVersion, styled && index != 4 ? SceneStylePresentation.ProducerVersion : "host-conformance-v1",
                    index, PresentationBlendMode.Normal, 1_000_000, true, JsonSerializer.SerializeToElement(new { }));
                return new PresentationLayerProductInput(layer, artifact);
            }).ToArray();
        var manifest = LayeredPresentationJson.CreateManifest(reference, null, layers.Select(l => l.Layer));
        var manifestArtifact = AsTypedArtifact(PresentationProcessingProducts.CreateManifestProduct(reference, null, layers, "manifest", baseArtifact));
        var enabled = layers.Select(l => l.Layer.LayerIdentitySha256).ToArray();
        var rendered = PresentationMaterializationExecutor.MaterializePacked(baseArtifact, manifestArtifact, manifest, layers, enabled, "rendered");
        if (styled)
        {
            var svg = GroupedSvgPresentationRenderer.Render(manifest,
                layers.Select(layer => PresentationLayerPayloadJson.Parse(layer.Product.Payload).Payload!).ToArray(), ProcessingIdentity.ComputePayloadSha256(baseArtifact.Payload));
            Assert.IsTrue(svg.Svg.Length > 0);
            var wrong = layers[0] with { Layer = layers[0].Layer with { RendererVersion = PresentationLayerCompositor.AlgorithmVersion } };
            Assert.ThrowsExactly<ArgumentException>(() => PresentationProcessingProducts.CreateManifestProduct(reference, null,
                [wrong, .. layers.Skip(1)], "invalid", baseArtifact));
        }
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
