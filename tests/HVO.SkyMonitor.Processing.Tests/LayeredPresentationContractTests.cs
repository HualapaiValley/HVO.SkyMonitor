using System.Text.Json;
using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class LayeredPresentationContractTests
{
    private static readonly PresentationCompatibilityDescriptor Compatibility = new(
        640, 480, new string('D', 64), new string('E', 64));
    private static readonly PresentationProductReference BaseProduct = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), new string('A', 64), "image/jpeg", Compatibility);
    private static readonly PresentationProductReference MetadataProduct = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('B', 64), "application/json", Compatibility);

    [TestMethod]
    [TestCategory("Unit")]
    public void LayerAndManifestHaveIndependentGoldenIdentitiesAndCanonicalOrdering()
    {
        var labels = Layer("labels", 20, """{"color":"white","size":12}""");
        var topology = Layer("constellations", 10, """{"width":1,"color":"blue"}""");
        var reorderedOptions = Layer("labels", 20, """{"size":12,"color":"white"}""");
        var manifest = LayeredPresentationJson.CreateManifest(BaseProduct, new string('C', 64), [labels, topology]);

        Assert.AreEqual(labels.LayerIdentitySha256, reorderedOptions.LayerIdentitySha256);
        Assert.AreEqual("FC5A8C28C807ACE34FF5A1604FC421A86051581D0FE2B941593DE4B56E33E583", labels.LayerIdentitySha256);
        Assert.AreEqual("2A2D1F88133092CAC24089905F90F07C1AA3E2AA4BE0F5ADDB65B7AB24726666", manifest.ManifestIdentitySha256);
        Assert.AreEqual(topology.LayerIdentitySha256, manifest.Layers[0].LayerIdentitySha256);
        Assert.AreEqual(labels.LayerIdentitySha256, manifest.Layers[1].LayerIdentitySha256);
        Assert.IsLessThan(LayeredPresentationJson.MaximumPayloadBytes, LayeredPresentationJson.Serialize(manifest).Length);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IdentitiesChangeForLayerOrderStyleOptionsEncoderAndLineage()
    {
        var first = Layer("labels", 10, """{"size":12}""");
        var changedStyle = first with { StyleVersion = "style-v2" };
        changedStyle = changedStyle with
        {
            LayerIdentitySha256 = LayeredPresentationJson.CreateLayer(
                changedStyle.LayerKind, changedStyle.SourceProduct, changedStyle.SceneIdentitySha256,
                changedStyle.CoordinateSpace, changedStyle.RendererVersion, changedStyle.StyleVersion,
                changedStyle.ZOrder, changedStyle.BlendMode, changedStyle.OpacityMillionths,
                changedStyle.EnabledByDefault, changedStyle.Options).LayerIdentitySha256
        };
        var second = Layer("grid", 20, """{"spacing":10}""");
        var manifest = LayeredPresentationJson.CreateManifest(BaseProduct, new string('C', 64), [second, first]);
        var request = Materialization(manifest, [first.LayerIdentitySha256], "png-v1", [BaseProduct.ArtifactId]);
        var changedEncoder = Materialization(manifest, [first.LayerIdentitySha256], "png-v2", [BaseProduct.ArtifactId]);
        var changedLayers = Materialization(manifest, [first.LayerIdentitySha256, second.LayerIdentitySha256], "png-v1", [BaseProduct.ArtifactId]);
        var changedLineage = Materialization(manifest, [first.LayerIdentitySha256], "png-v1",
            [BaseProduct.ArtifactId, Guid.Parse("33333333-3333-3333-3333-333333333333")]);

        Assert.AreNotEqual(first.LayerIdentitySha256, changedStyle.LayerIdentitySha256);
        Assert.AreNotEqual(request.MaterializationIdentitySha256, changedEncoder.MaterializationIdentitySha256);
        Assert.AreNotEqual(request.MaterializationIdentitySha256, changedLayers.MaterializationIdentitySha256);
        Assert.AreNotEqual(request.MaterializationIdentitySha256, changedLineage.MaterializationIdentitySha256);
        Assert.AreNotEqual(manifest.ManifestIdentitySha256, request.MaterializationIdentitySha256);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ValidationRejectsEmbeddedPayloadShapesUnknownLayersAndStructuralBounds()
    {
        var layer = Layer("labels", 1, "{}");
        var manifest = LayeredPresentationJson.CreateManifest(BaseProduct, new string('C', 64), [layer]);

        Assert.ThrowsExactly<ArgumentException>(() => LayeredPresentationJson.CreateLayer(
            "labels", MetadataProduct, null, PresentationCoordinateSpace.ScenePixels,
            "renderer-v1", "style-v1", 1, PresentationBlendMode.Normal, 1_000_001, true, Json("{}")));
        Assert.ThrowsExactly<ArgumentException>(() => LayeredPresentationJson.CreateLayer(
            "labels", MetadataProduct, null, PresentationCoordinateSpace.ScenePixels,
            "renderer-v1", "style-v1", 1, PresentationBlendMode.Normal, 1_000_000, true, Json("[]")));
        Assert.ThrowsExactly<ArgumentException>(() => LayeredPresentationJson.CreateMaterializationRequest(
            manifest, [new string('D', 64)], "compositor-v1", "png", "png-v1", Json("{}"), [BaseProduct.ArtifactId]));
        Assert.ThrowsExactly<ArgumentException>(() => LayeredPresentationJson.Serialize(
            manifest with { Layers = Enumerable.Repeat(layer, LayeredPresentationJson.MaximumLayerCount + 1).ToArray() }));

        Assert.IsFalse(typeof(PresentationLayerV1).GetProperties().Any(property =>
            property.PropertyType == typeof(ReadOnlyMemory<byte>) || property.Name.Contains("Svg", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ExistingAnnotationAndFlattenedArtifactContractsRemainAvailable()
    {
        var annotation = new ProcessingAnnotationInput([], [], new PreviewTransform(1, 1), null, new string('A', 64));

        Assert.AreEqual(FrameArtifactRole.AnnotatedPreview, Enum.Parse<FrameArtifactRole>("AnnotatedPreview"));
        Assert.IsEmpty(annotation.Objects);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TypedParsersRoundTripAndRejectMalformedWireDocuments()
    {
        var layer = Layer("labels", 10, "{}");
        var manifest = LayeredPresentationJson.CreateManifest(BaseProduct, new string('C', 64), [layer]);
        var request = Materialization(manifest, [layer.LayerIdentitySha256], "png-v1", []);
        var result = new PresentationMaterializationResultV1(
            PresentationMaterializationResultV1.CurrentSchemaVersion, request.MaterializationIdentitySha256,
            BaseProduct with { ArtifactId = Guid.Parse("44444444-4444-4444-4444-444444444444") }, new string('F', 64), 123);

        Assert.IsTrue(LayeredPresentationJson.ParseLayer(LayeredPresentationJson.Serialize(layer)).IsValid);
        Assert.IsTrue(LayeredPresentationJson.ParseManifest(LayeredPresentationJson.Serialize(manifest)).IsValid);
        Assert.IsTrue(LayeredPresentationJson.ParseMaterializationRequest(LayeredPresentationJson.Serialize(request)).IsValid);
        Assert.IsTrue(LayeredPresentationJson.ParseMaterializationResult(LayeredPresentationJson.Serialize(result)).IsValid);

        var json = System.Text.Encoding.UTF8.GetString(LayeredPresentationJson.Serialize(layer));
        var malformed = new[]
        {
            "null",
            json.Replace("\"sourceProduct\":{", "\"sourceProduct\":null,\"ignored\":{", StringComparison.Ordinal),
            json.Replace("\"enabledByDefault\":true,", string.Empty, StringComparison.Ordinal),
            json.Replace("\"zOrder\":10", "\"zOrder\":1e1", StringComparison.Ordinal),
            json.Replace("\"layerKind\":\"labels\"", "\"layerKind\":\"labels\",\"LAYERKIND\":\"labels\"", StringComparison.Ordinal),
            "{" + json[1..^1] + ",\"unknown\":true}"
        };
        foreach (var candidate in malformed)
        {
            Assert.IsFalse(LayeredPresentationJson.ParseLayer(System.Text.Encoding.UTF8.GetBytes(candidate)).IsValid, candidate);
        }
        Assert.IsFalse(LayeredPresentationJson.ParseLayer(new byte[LayeredPresentationJson.MaximumPayloadBytes + 1]).IsValid);

        AssertParserRejectsMalformed(
            LayeredPresentationJson.Serialize(manifest),
            value => LayeredPresentationJson.ParseManifest(value).IsValid);
        AssertParserRejectsMalformed(
            LayeredPresentationJson.Serialize(request),
            value => LayeredPresentationJson.ParseMaterializationRequest(value).IsValid);
        AssertParserRejectsMalformed(
            LayeredPresentationJson.Serialize(result),
            value => LayeredPresentationJson.ParseMaterializationResult(value).IsValid);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void OptionsCompatibilityHashNormalizationAndBoundedEnumerablesAreEnforced()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Layer("labels", 1, """{"nested":{"x":1,"X":2}}"""));
        Assert.ThrowsExactly<ArgumentException>(() => Layer("labels", 1, """{"value":1.0}"""));
        Assert.ThrowsExactly<ArgumentException>(() => LayeredPresentationJson.CreateLayer(
            "labels", MetadataProduct with { ProductIdentitySha256 = new string('\uFF21', 64) }, new string('C', 64),
            PresentationCoordinateSpace.ScenePixels, "renderer-v1", "style-v1", 1,
            PresentationBlendMode.Normal, 1_000_000, true, Json("{}")));

        var lowercase = MetadataProduct with
        {
            ProductIdentitySha256 = new string('b', 64),
            Compatibility = Compatibility with
            {
                LayoutIdentitySha256 = new string('d', 64),
                CoordinateIdentitySha256 = new string('e', 64)
            }
        };
        var normalized = LayeredPresentationJson.CreateLayer(
            "labels", lowercase, new string('c', 64), PresentationCoordinateSpace.ScenePixels,
            "renderer-v1", "style-v1", 1, PresentationBlendMode.Normal, 1_000_000, true, Json("{}"));
        Assert.AreEqual(new string('B', 64), normalized.SourceProduct.ProductIdentitySha256);
        Assert.AreEqual(new string('D', 64), normalized.SourceProduct.Compatibility.LayoutIdentitySha256);

        var incompatible = normalized with
        {
            SourceProduct = normalized.SourceProduct with
            {
                Compatibility = normalized.SourceProduct.Compatibility with { WidthPixels = 641 }
            }
        };
        Assert.ThrowsExactly<ArgumentException>(() => LayeredPresentationJson.CreateManifest(BaseProduct, new string('C', 64), [incompatible]));
        Assert.ThrowsExactly<ArgumentException>(() => LayeredPresentationJson.CreateManifest(
            BaseProduct, new string('C', 64), UnboundedLayers(normalized)));
        Assert.ThrowsExactly<ArgumentException>(() => LayeredPresentationJson.CreateLayer(
            "labels", MetadataProduct with
            {
                Compatibility = Compatibility with { WidthPixels = LayeredPresentationJson.MaximumDimensionPixels + 1 }
            }, new string('C', 64), PresentationCoordinateSpace.ScenePixels,
            "renderer-v1", "style-v1", 1, PresentationBlendMode.Normal, 1_000_000, true, Json("{}")));
        Assert.ThrowsExactly<ArgumentException>(() => LayeredPresentationJson.CreateLayer(
            "labels", MetadataProduct with
            {
                Compatibility = Compatibility with { WidthPixels = 65_536, HeightPixels = 65_536 }
            }, new string('C', 64), PresentationCoordinateSpace.ScenePixels,
            "renderer-v1", "style-v1", 1, PresentationBlendMode.Normal, 1_000_000, true, Json("{}")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CollectionsAreImmutableAndCompatibilityChangesEveryOwningIdentity()
    {
        var layer = Layer("labels", 10, "{}");
        var manifest = LayeredPresentationJson.CreateManifest(BaseProduct, new string('C', 64), [layer]);
        var request = Materialization(manifest, [layer.LayerIdentitySha256], "png-v1", []);
        var parsedManifest = LayeredPresentationJson.ParseManifest(LayeredPresentationJson.Serialize(manifest)).Document!;
        var parsedRequest = LayeredPresentationJson.ParseMaterializationRequest(LayeredPresentationJson.Serialize(request)).Document!;

        Assert.IsFalse(manifest.Layers is PresentationLayerV1[]);
        Assert.IsFalse(parsedManifest.Layers is PresentationLayerV1[]);
        Assert.IsFalse(parsedRequest.SourceArtifactIds is Guid[]);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<PresentationLayerV1>)manifest.Layers).Clear());

        var changedProduct = BaseProduct with { Compatibility = Compatibility with { CoordinateIdentitySha256 = new string('F', 64) } };
        var changedLayer = LayeredPresentationJson.CreateLayer(
            layer.LayerKind, changedProduct, layer.SceneIdentitySha256, layer.CoordinateSpace,
            layer.RendererVersion, layer.StyleVersion, layer.ZOrder, layer.BlendMode,
            layer.OpacityMillionths, layer.EnabledByDefault, layer.Options);
        var changedManifest = LayeredPresentationJson.CreateManifest(changedProduct, new string('C', 64), [changedLayer]);
        var changedRequest = Materialization(changedManifest, [changedLayer.LayerIdentitySha256], "png-v1", []);
        Assert.AreNotEqual(layer.LayerIdentitySha256, changedLayer.LayerIdentitySha256);
        Assert.AreNotEqual(manifest.ManifestIdentitySha256, changedManifest.ManifestIdentitySha256);
        Assert.AreNotEqual(request.MaterializationIdentitySha256, changedRequest.MaterializationIdentitySha256);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RepresentativeLayerStackHasBoundedSizeAndCost()
    {
        const int layerCount = 128;
        var layers = Enumerable.Range(0, layerCount)
            .Select(index => Layer($"layer-{index:D3}", index, $$"""{"index":{{index}},"enabled":true}"""))
            .ToArray();
        var manifest = LayeredPresentationJson.CreateManifest(BaseProduct, new string('C', 64), layers);
        _ = LayeredPresentationJson.Serialize(manifest);

        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var bytes = LayeredPresentationJson.Serialize(manifest);
        var parsed = LayeredPresentationJson.ParseManifest(bytes);
        stopwatch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        Assert.IsTrue(bytes.Length < LayeredPresentationJson.MaximumPayloadBytes);
        Console.WriteLine(
            $"layers={layerCount};jsonBytes={bytes.Length};serializeParseMs={stopwatch.Elapsed.TotalMilliseconds:F3};allocatedBytes={allocatedBytes}");
    }

    private static IEnumerable<PresentationLayerV1> UnboundedLayers(PresentationLayerV1 layer)
    {
        for (var index = 0; index <= LayeredPresentationJson.MaximumLayerCount; index++) yield return layer;
    }

    private static void AssertParserRejectsMalformed(byte[] canonical, Func<ReadOnlyMemory<byte>, bool> parse)
    {
        var json = System.Text.Encoding.UTF8.GetString(canonical);
        var candidates = new[]
        {
            "null",
            "{" + json[1..^1] + ",\"unknown\":true}",
            json.Replace("\"schemaVersion\":", "\"SCHEMAVERSION\":\"duplicate\",\"schemaVersion\":", StringComparison.Ordinal),
            json.Replace("\"schemaVersion\":", "\"removedSchemaVersion\":", StringComparison.Ordinal)
        };
        foreach (var candidate in candidates)
        {
            Assert.IsFalse(parse(System.Text.Encoding.UTF8.GetBytes(candidate)), candidate);
        }
    }

    private static PresentationLayerV1 Layer(string kind, int zOrder, string options) =>
        LayeredPresentationJson.CreateLayer(
            kind, MetadataProduct, new string('C', 64), PresentationCoordinateSpace.ScenePixels,
            "renderer-v1", "style-v1", zOrder, PresentationBlendMode.Normal, 750_000, true, Json(options));

    private static PresentationMaterializationRequestV1 Materialization(
        OverlayManifestV1 manifest,
        IReadOnlyList<string> layers,
        string encoderVersion,
        IReadOnlyList<Guid> lineage) =>
        LayeredPresentationJson.CreateMaterializationRequest(
            manifest, layers, "compositor-v1", "png", encoderVersion, Json("""{"compression":6}"""), lineage);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
