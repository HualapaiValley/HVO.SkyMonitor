using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PresentationProcessingProductTests
{
    private static readonly ProcessingCompatibilityIdentity Compatibility = new("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile");

    [TestMethod]
    public void LayerAndManifestProductsAreTypedAndCarryCompleteImmediateLineage()
    {
        var fact = Artifact(Guid.Parse("10000000-0000-0000-0000-000000000001"), FrameArtifactRole.Metadata,
            "scene", null, [1, 2, 3]) with
        { DescriptorIdentitySha256 = new string('A', 64) };
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 8, 8,
            markers: [new(new(3, 3), 1, new(255, 255, 255))]);
        var product = PresentationProcessingProducts.CreateLayerProduct(payload, "stars", [fact], "scene-producer-v1");
        var persisted = ToArtifact(product);
        var baseArtifact = Artifact(Guid.Parse("20000000-0000-0000-0000-000000000002"), FrameArtifactRole.Preview,
            "base", Layout(), new byte[64]);
        var compatibility = new PresentationCompatibilityDescriptor(8, 8, new string('D', 64), new string('E', 64));
        var baseReference = PresentationProcessingProducts.CreateReference(baseArtifact, new string('E', 64));
        compatibility = baseReference.Compatibility;
        var layerReference = new PresentationProductReference(persisted.ArtifactId, payload.ContentIdentitySha256, product.MediaType, compatibility);
        var layer = LayeredPresentationJson.CreateLayer("stars", layerReference, null,
            PresentationCoordinateSpace.ScenePixels, PresentationLayerCompositor.AlgorithmVersion, "style-v1", 10,
            PresentationBlendMode.Normal, 1_000_000, true, System.Text.Json.JsonSerializer.SerializeToElement(new { }));

        var manifestProduct = PresentationProcessingProducts.CreateManifestProduct(baseReference, null,
            [new(layer, persisted)], "overlay", baseArtifact);

        Assert.AreEqual(ProcessingProductKind.Metadata, product.Kind);
        Assert.AreEqual(PresentationLayerPayloadV1.CurrentSchemaVersion, product.SchemaVersion);
        Assert.AreEqual(payload.ContentIdentitySha256, product.ContentIdentitySha256);
        CollectionAssert.AreEqual(new[] { fact.ArtifactId }, product.SourceArtifactIds.ToArray());
        Assert.AreEqual(OverlayManifestV1.CurrentSchemaVersion, manifestProduct.SchemaVersion);
        CollectionAssert.AreEqual(new[] { baseArtifact.ArtifactId, persisted.ArtifactId }, manifestProduct.SourceArtifactIds.ToArray());
    }

    [TestMethod]
    public void MaterializationBindsToggleOrderCompositorEncoderAndLineage()
    {
        var baseArtifact = Artifact(Guid.Parse("30000000-0000-0000-0000-000000000003"), FrameArtifactRole.Preview,
            "base", Layout(), new byte[64]);
        var compatibility = new PresentationCompatibilityDescriptor(8, 8, new string('D', 64), new string('E', 64));
        var baseReference = PresentationProcessingProducts.CreateReference(baseArtifact, new string('E', 64));
        compatibility = baseReference.Compatibility;
        var first = LayerInput("first", 10, 2, 2, new(200, 200, 200), compatibility);
        var second = LayerInput("second", 20, 2, 2, new(50, 50, 50), compatibility);
        var manifest = LayeredPresentationJson.CreateManifest(baseReference, null, [first.Layer, second.Layer]);
        var manifestProduct = PresentationProcessingProducts.CreateManifestProduct(baseReference, null, [first, second], "manifest", baseArtifact);
        var manifestArtifact = ToArtifact(manifestProduct);

        var all = PresentationMaterializationExecutor.MaterializePacked(baseArtifact, manifestArtifact, manifest,
            [first, second], [first.Layer.LayerIdentitySha256, second.Layer.LayerIdentitySha256], "flat",
            new(PresentationMaterializationExecutor.PackedEncoderName, PresentationMaterializationExecutor.PackedEncoderVersion));
        var one = PresentationMaterializationExecutor.MaterializePacked(baseArtifact, manifestArtifact, manifest,
            [first, second], [first.Layer.LayerIdentitySha256], "flat");

        Assert.AreNotEqual(all.ChecksumSha256, one.ChecksumSha256);
        Assert.IsTrue(all.Algorithms.Any(algorithm => algorithm.Name == PresentationMaterializationExecutor.PackedEncoderName &&
            algorithm.Version == PresentationMaterializationExecutor.PackedEncoderVersion));
        Assert.ThrowsExactly<ArgumentException>(() => PresentationMaterializationExecutor.MaterializePacked(
            baseArtifact, manifestArtifact, manifest, [first, second], [first.Layer.LayerIdentitySha256], "flat",
            new("packed", "arbitrary")));
        CollectionAssert.AreEquivalent(new[] { baseArtifact.ArtifactId, manifestArtifact.ArtifactId,
            first.Product.ArtifactId, second.Product.ArtifactId }, all.SourceArtifactIds.ToArray());
        Assert.AreEqual(FrameArtifactRole.AnnotatedPreview, all.Role);
        Assert.AreEqual(ProcessingProductKind.PixelData, all.Kind);
    }

    [TestMethod]
    public void ConfiguredReorderedSubsetIsDeterministicAndRetainsCanonicalSourceOrder()
    {
        var baseArtifact = Artifact(Guid.Parse("31000000-0000-0000-0000-000000000003"), FrameArtifactRole.Preview,
            "combined-preview", Layout(), new byte[64]);
        var baseReference = PresentationProcessingProducts.CreateReference(baseArtifact, new string('E', 64));
        var first = LayerInput("first", 30, 2, 2, new(200, 200, 200), baseReference.Compatibility);
        var second = LayerInput("second", 10, 3, 3, new(50, 50, 50), baseReference.Compatibility);
        var third = LayerInput("third", 20, 4, 4, new(100, 100, 100), baseReference.Compatibility);
        var inputs = new[] { first, second, third };
        var manifest = LayeredPresentationJson.CreateManifest(
            baseReference, null, inputs.Select(static input => input.Layer));
        var manifestArtifact = ToArtifact(PresentationProcessingProducts.CreateManifestProduct(
            baseReference, null, inputs, "manifest", baseArtifact));
        var enabled = new[] { second.Layer.LayerIdentitySha256, first.Layer.LayerIdentitySha256 };

        var firstRun = PresentationMaterializationExecutor.MaterializePacked(
            baseArtifact, manifestArtifact, manifest, inputs, enabled, "subset");
        var restartRun = PresentationMaterializationExecutor.MaterializePacked(
            baseArtifact, manifestArtifact, manifest, inputs, enabled, "subset");

        Assert.AreEqual(firstRun.OutputIdentitySha256, restartRun.OutputIdentitySha256);
        CollectionAssert.AreEqual(firstRun.Payload.ToArray(), restartRun.Payload.ToArray());
        CollectionAssert.AreEqual(
            new[] { baseArtifact.ArtifactId, manifestArtifact.ArtifactId, second.Product.ArtifactId, first.Product.ArtifactId },
            firstRun.SourceArtifactIds.ToArray());
    }

    [TestMethod]
    public void FullResolutionW6RepresentativeOldAndTypedPresentationAreNumericallyEquivalent()
    {
        const int width = 3552;
        const int height = 3552;
        var layout = new ImageLayout(width, height, CameraPixelFormat.Mono8, width);
        var basePixels = new byte[checked(width * height)];
        var objects = new[]
        {
            new ProjectedAnnotationObject("a", "", new PixelPoint(1776, 1776), DrawLabel: false),
            new ProjectedAnnotationObject("b", "", new PixelPoint(900, 1200), DrawLabel: false)
        };
        var segments = new[]
        {
            new ProjectedAnnotationSegment("ORI", new PixelPoint(900, 1200), new PixelPoint(1776, 1776))
        };
        var annotationOptions = new AnnotationOptions { MarkRadius = 6, DrawLabels = false, MarkerValue = 144 };
        var oldAnnotation = AnnotationRenderer.AnnotateMono8WithSegments(
            basePixels, width, height, objects, segments, new PreviewTransform(1, 1), annotationOptions).Pixels;
        var cloudMask = new byte[32];
        cloudMask[0] = 1;
        cloudMask[^1] = 0x80;
        var oldFinal = WeatherCloudOverlayRenderer.Render(
            layout, oldAnnotation, 16, 16, cloudMask, [],
            new WeatherCloudOverlayRenderOptions(DrawLabels: false)).Pixels.ToArray();

        var constellations = PresentationLayerPayloadJson.Create(new string('A', 64), width, height,
            segments: segments.Select(item => new PresentationSegmentV1(item.FromPixel, item.ToPixel, 1,
                new PresentationColor(annotationOptions.ConstellationLineValue,
                    annotationOptions.ConstellationLineValue, annotationOptions.ConstellationLineValue))).ToArray());
        var scene = PresentationLayerPayloadJson.Create(new string('A', 64), width, height,
            markers: objects.Select(item => new PresentationMarkerV1(
                item.Pixel, annotationOptions.MarkRadius,
                new PresentationColor(annotationOptions.MarkerValue, annotationOptions.MarkerValue,
                    annotationOptions.MarkerValue))).ToArray());
        var cloud = PresentationLayerPayloadJson.Create(new string('B', 64), width, height,
            tileMask: new PresentationTileMaskV1(16, 16, PresentationTileMaskV1.RowMajorLsbFirst,
                cloudMask, 1, new PresentationColor(255, 255, 255)));
        var typedFinal = PresentationLayerCompositor.Composite(layout, basePixels,
        [
            new(constellations, true, PresentationRasterBlendMode.Normal, 800_000),
            new(scene, true, PresentationRasterBlendMode.Normal, 1_000_000),
            new(cloud, true, PresentationRasterBlendMode.Normal, 1_000_000)
        ]);

        CollectionAssert.AreEqual(oldFinal, typedFinal);
        Assert.AreEqual(3, new[] { constellations, scene, cloud }.Length);
    }

    [TestMethod]
    public void PackedMaterializationRejectsEnabledNormalizedImageLayer()
    {
        var baseArtifact = Artifact(Guid.NewGuid(), FrameArtifactRole.Preview, "base", Layout(), new byte[64]);
        var baseReference = PresentationProcessingProducts.CreateReference(baseArtifact, new string('E', 64));
        var normalized = LayerInput("normalized", 10, 2, 2, new(200, 200, 200), baseReference.Compatibility);
        normalized = normalized with
        {
            Layer = LayeredPresentationJson.CreateLayer(normalized.Layer.LayerKind, normalized.Layer.SourceProduct, null,
                PresentationCoordinateSpace.NormalizedImage, normalized.Layer.RendererVersion, normalized.Layer.StyleVersion,
                normalized.Layer.ZOrder, normalized.Layer.BlendMode, normalized.Layer.OpacityMillionths, true,
                normalized.Layer.Options)
        };
        var manifest = LayeredPresentationJson.CreateManifest(baseReference, null, [normalized.Layer]);
        var manifestArtifact = ToArtifact(PresentationProcessingProducts.CreateManifestProduct(
            baseReference, null, [normalized], "manifest", baseArtifact));

        Assert.ThrowsExactly<ArgumentException>(() => PresentationMaterializationExecutor.MaterializePacked(
            baseArtifact, manifestArtifact, manifest, [normalized], [normalized.Layer.LayerIdentitySha256], "flat"));
        var disabled = PresentationMaterializationExecutor.MaterializePacked(
            baseArtifact, manifestArtifact, manifest, [normalized], [], "flat");
        CollectionAssert.AreEqual(baseArtifact.Payload.ToArray(), disabled.Payload.ToArray());
    }

    [TestMethod]
    public void ManifestProductLineageUsesCanonicalLayerOrderIndependentOfCallerOrder()
    {
        var baseArtifact = Artifact(Guid.NewGuid(), FrameArtifactRole.Preview, "base", Layout(), new byte[64]);
        var baseReference = PresentationProcessingProducts.CreateReference(baseArtifact, new string('E', 64));
        var first = LayerInput("first", 20, 1, 1, new(100, 100, 100), baseReference.Compatibility);
        var second = LayerInput("second", 10, 2, 2, new(200, 200, 200), baseReference.Compatibility);

        var forward = PresentationProcessingProducts.CreateManifestProduct(
            baseReference, null, [first, second], "manifest", baseArtifact);
        var reverse = PresentationProcessingProducts.CreateManifestProduct(
            baseReference, null, [second, first], "manifest", baseArtifact);

        CollectionAssert.AreEqual(forward.Payload.ToArray(), reverse.Payload.ToArray());
        Assert.AreEqual(forward.Recipe.IdentitySha256, reverse.Recipe.IdentitySha256);
        Assert.AreEqual(forward.OutputIdentitySha256, reverse.OutputIdentitySha256);
        CollectionAssert.AreEqual(forward.SourceArtifactIds.ToArray(), reverse.SourceArtifactIds.ToArray());
        CollectionAssert.AreEqual(new[] { baseArtifact.ArtifactId, second.Product.ArtifactId, first.Product.ArtifactId },
            forward.SourceArtifactIds.ToArray());

        Assert.ThrowsExactly<ArgumentException>(() => PresentationProcessingProducts.CreateManifestProduct(
            baseReference, null, [first, first], "manifest", baseArtifact));
        Assert.ThrowsExactly<ArgumentException>(() => PresentationProcessingProducts.CreateManifestProduct(
            baseReference, null, [first, first with { Product = second.Product }], "manifest", baseArtifact));
    }

    [TestMethod]
    public void TypedSourceIdentityAndEveryReferenceCompatibilityAxisAreValidated()
    {
        var sourcePayload = PresentationLayerPayloadJson.Create(new string('A', 64), 8, 8);
        var source = Artifact(Guid.NewGuid(), FrameArtifactRole.Metadata, "scene", null,
            PresentationLayerPayloadJson.Serialize(sourcePayload), PresentationLayerPayloadJson.MediaType) with
        {
            ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = PresentationLayerPayloadV1.CurrentSchemaVersion,
            ContentIdentitySha256 = sourcePayload.ContentIdentitySha256,
            DescriptorIdentitySha256 = new string('F', 64)
        };
        var derived = PresentationLayerPayloadJson.Create(sourcePayload.ContentIdentitySha256, 8, 8);
        var product = PresentationProcessingProducts.CreateLayerProduct(derived, "derived", [source], "producer-v1");
        Assert.AreEqual(derived.ContentIdentitySha256, product.ContentIdentitySha256);

        var baseArtifact = Artifact(Guid.NewGuid(), FrameArtifactRole.Preview, "base", Layout(), new byte[64]);
        var reference = PresentationProcessingProducts.CreateReference(baseArtifact, new string('E', 64));
        var mutations = new[]
        {
            reference with { ArtifactId = Guid.NewGuid() },
            reference with { MediaType = "image/jpeg" },
            reference with { ProductIdentitySha256 = new string('C', 64) },
            reference with { Compatibility = reference.Compatibility with { WidthPixels = 9 } },
            reference with { Compatibility = reference.Compatibility with { HeightPixels = 9 } },
            reference with { Compatibility = reference.Compatibility with { LayoutIdentitySha256 = new string('D', 64) } }
        };
        foreach (var mutation in mutations)
        {
            Assert.ThrowsExactly<ArgumentException>(() => PresentationProcessingProducts.CreateManifestProduct(
                mutation, null, [], "manifest", baseArtifact));
        }


        var layerPayload = PresentationLayerPayloadJson.Create(new string('A', 64), 8, 8);
        var layerArtifact = Artifact(Guid.NewGuid(), FrameArtifactRole.Metadata, "layer", null,
            PresentationLayerPayloadJson.Serialize(layerPayload), PresentationLayerPayloadJson.MediaType) with
        {
            ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = PresentationLayerPayloadV1.CurrentSchemaVersion,
            ContentIdentitySha256 = layerPayload.ContentIdentitySha256
        };
        var layerReference = new PresentationProductReference(layerArtifact.ArtifactId, layerPayload.ContentIdentitySha256,
            layerArtifact.MediaType, reference.Compatibility with { CoordinateIdentitySha256 = new string('D', 64) });
        var layer = LayeredPresentationJson.CreateLayer("layer", layerReference, null,
            PresentationCoordinateSpace.ScenePixels, "renderer-v1", "style-v1", 1, PresentationBlendMode.Lighten,
            1_000_000, true, System.Text.Json.JsonSerializer.SerializeToElement(new { }));
        Assert.ThrowsExactly<ArgumentException>(() => PresentationProcessingProducts.CreateManifestProduct(
            reference, null, [new(layer, layerArtifact)], "manifest", baseArtifact));
    }

    [TestMethod]
    public void ProcessingArtifactPositionalAbiIsPreservedWithAdditiveTypedFacts()
    {
        var artifact = Artifact(Guid.NewGuid(), FrameArtifactRole.Metadata, "typed", null, [1]) with
        {
            ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = "schema-v1",
            ContentIdentitySha256 = new string('A', 64)
        };
        var (id, role, variant, recipe, media, layout, payload, created, integration, compatibility,
            sequence, sources, started, ended, conditions) = artifact;

        Assert.AreEqual(artifact.ArtifactId, id);
        Assert.AreEqual(ProcessingProductKind.Metadata, artifact.ProductKind);
        Assert.AreEqual("schema-v1", artifact.SchemaVersion);
        Assert.AreEqual(15, typeof(ProcessingArtifact).GetConstructors().Single().GetParameters().Length);
        Assert.AreEqual(1, payload.Length);
        _ = (role, variant, recipe, media, layout, created, integration, compatibility, sequence, sources, started, ended, conditions);
    }

    private static PresentationLayerProductInput LayerInput(string kind, int order, int x, int y,
        PresentationColor color, PresentationCompatibilityDescriptor compatibility)
    {
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 8, 8,
            markers: [new(new(x, y), 0, color)]);
        var artifact = Artifact(Guid.NewGuid(), FrameArtifactRole.Metadata, kind, null,
            PresentationLayerPayloadJson.Serialize(payload), PresentationLayerPayloadJson.MediaType) with
        {
            ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = PresentationLayerPayloadV1.CurrentSchemaVersion,
            ContentIdentitySha256 = payload.ContentIdentitySha256
        };
        var reference = new PresentationProductReference(artifact.ArtifactId, payload.ContentIdentitySha256,
            artifact.MediaType, compatibility);
        var layer = LayeredPresentationJson.CreateLayer(kind, reference, null, PresentationCoordinateSpace.ScenePixels,
            PresentationLayerCompositor.AlgorithmVersion, "style-v1", order, PresentationBlendMode.Normal, 1_000_000,
            true, System.Text.Json.JsonSerializer.SerializeToElement(new { }));
        return new(layer, artifact);
    }

    private static ProcessingArtifact ToArtifact(ProcessingProduct product) => new(
        ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.Role, product.Variant,
        product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload,
        DateTimeOffset.Parse("2026-08-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        product.TotalIntegration, product.Compatibility, SourceArtifactIds: product.SourceArtifactIds)
    {
        ProductKind = product.Kind,
        SchemaVersion = product.SchemaVersion,
        ContentIdentitySha256 = product.ContentIdentitySha256
    };

    private static ProcessingArtifact Artifact(Guid id, FrameArtifactRole role, string variant,
        FrameLayoutDescriptor? layout, byte[] bytes, string mediaType = "application/x-hvo-packed-image") => new(
            id, role, variant, new string('0', 64), mediaType, layout, bytes,
            DateTimeOffset.Parse("2026-08-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(1), Compatibility);

    private static FrameLayoutDescriptor Layout() => new(8, 8, 8, CameraPixelFormat.Mono8,
        FrameByteOrder.NotApplicable, 8, 8, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None,
        null, byte.MaxValue, 64);
}
