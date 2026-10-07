using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

// A retained projected-scene-v3 artifact reaches both hosts as the same bytes. The edge leg is the production path: the
// scene builder over the composed OpenNGC subset, the virtual camera's deep-sky selection, the staging store's stage,
// read and bind, and the ProjectedScene recipe through the edge adapter. It builds that recipe request as the edge
// step does, because the step's context type is not constructible from this lane. The central leg reads the retained
// bytes as CentralProjectedSceneResolver does, annotates them with its mapping, and composites the retained deep-sky
// layer payload over the decoded packed base as CentralPresentationMaterializer does; the central host has no deep-sky
// producer of its own.
public sealed partial class HostRecipeContractConformanceTests
{
    private const string DeepSkyCatalogChecksum = "42590e36f804b30e444d917ca188f35287684584d1b741838ed297446bd669c8";

    [TestMethod]
    [DataRow("centred-footprint")]
    [DataRow("centred-outline")]
    [DataRow("off-axis")]
    public async Task RetainedDeepSkySceneReconstructsIdenticallyOnBothHosts(string camera)
    {
        // At f=400 M31 (177.8') is a resolvable ellipse without an outline, and M45 (150') and M42 (90') are
        // resolvable outlines. The perspective boresights are their apparent centres at the fixture instant; the
        // zenith fisheye places all three well away from its principal point.
        (ProjectionContext Projection, string? Centred, DeepSkyRepresentation? Expected) row = camera switch
        {
            "centred-footprint" => (DeepSkyPerspective(9.914713980340611, 314.36123097036824), "deep-sky:NGC0224",
                DeepSkyRepresentation.Footprint),
            "centred-outline" => (DeepSkyPerspective(34.35054199845635, 277.0957768139783), "deep-sky:Mel022",
                DeepSkyRepresentation.Outline),
            "off-axis" => (new ProjectionContext(ProjectionModel.EquidistantFisheye, 650, 650, 400, 400, 1300, 1300,
                ProjectionAperture.Circular, 640, BoresightAltitudeDegrees: 90), null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(camera), camera, "Unknown camera row.")
        };
        var width = row.Projection.WidthPixels;
        var height = row.Projection.HeightPixels;
        var stageKey = new string('5', 64);
        var sceneId = new string('E', 64);
        var root = DeepSkyPhysicalPath(Directory.CreateTempSubdirectory("hvo-525-deep-sky-").FullName);
        try
        {
            var catalog = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v44-openngc-subset.sqlite"),
                DeepSkyCatalogChecksum, "4", "5"));
            var visible = (await new VisibleSceneBuilder(catalog, null, new AstronomyEnginePlanetEphemeris())
                    .BuildAsync(new VisibleSceneRequest(ProcessingConformanceFixture.CapturedUtc,
                        new ObserverLocation(35.347, -113.878, 0), row.Projection, new CatalogQuery(6, 10),
                        catalog.Metadata, projectionVersion: "deep-sky-conformance-v1")))
                .WithDeepSky(catalog.DeepSky!, new VirtualDeepSkyOptions { Enabled = true }.ToSelection());

            using var staging = new ProjectedSceneStagingStore(
                Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
            await staging.StageAsync(stageKey, sceneId, visible, CancellationToken.None);
            var staged = await staging.ReadAsync(stageKey, CancellationToken.None);
            Assert.IsNotNull(staged);

            var pixels = Enumerable.Range(0, width * height).Select(static i => (byte)(i % 251)).ToArray();
            var layout = new FrameLayoutDescriptor(width, height, width, CameraPixelFormat.Mono8,
                FrameByteOrder.NotApplicable, 8, 8, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0,
                byte.MaxValue, pixels.Length);
            var descriptor = ProcessingConformanceFixture.CreateDescriptor() with
            {
                Layout = layout,
                Artifact = ProcessingConformanceFixture.CreateDescriptor().Artifact with
                { ChecksumSha256 = PayloadChecksum.ComputeSha256(pixels) }
            };
            var descriptorSha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor);
            var bound = staged.Bind(
                new ProjectedSceneSource(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, descriptorSha256),
                staged.IntendedKind ?? ProjectedSceneKind.VirtualRenderAuthoritative);

            // Edge: the ProjectedScene recipe request exactly as the edge step builds it.
            var payload = ProjectedSceneJson.Serialize(bound);
            var raw = new ProcessingArtifact(
                descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, descriptor.Artifact.Variant,
                ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                descriptor.Artifact.MediaType, descriptor.Layout, ReadOnlyMemory<byte>.Empty,
                descriptor.Artifact.CreatedUtc, descriptor.Controls.EffectiveExposure,
                CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor),
                CaptureSequence: descriptor.Capture.CaptureSequence,
                ObservationStartedUtc: descriptor.Timing.ExposureStartedUtc,
                ObservationEndedUtc: descriptor.Timing.ExposureEndedUtc)
            {
                CaptureId = descriptor.Capture.CaptureId,
                DescriptorIdentitySha256 = descriptorSha256
            };
            var auxiliary = new ProcessingAuxiliaryInput(
                "scene", ProcessingAuxiliaryInputKind.CanonicalJson, SchemaVersion: bound.SchemaVersion,
                IdentitySha256: bound.SceneIdentitySha256, Payload: payload)
            {
                ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(payload)
            };
            var request = new ProcessingExecutionRequest(
                BuiltInProcessingRecipes.ProjectedScene,
                JsonSerializer.SerializeToElement(new Dictionary<string, object>()),
                ProcessingInputSelector.Raw(descriptor.Artifact.Variant), [raw], "projected-scene-v1",
                AuxiliaryInputs: [auxiliary], InputArtifactId: raw.ArtifactId);
            var outcome = await new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor())
                .ExecuteAsync(request, CancellationToken.None);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
            var sceneProduct = Assert.ContainsSingle(outcome.Products);
            AssertContract(request, sceneProduct);
            var retained = sceneProduct.Payload.ToArray();
            CollectionAssert.AreEqual(payload, retained);
            Assert.AreEqual(bound.SceneIdentitySha256, sceneProduct.ContentIdentitySha256);
            var edgeScene = ProjectedSceneJson.Parse(sceneProduct.Payload).Scene!;

            // Central: the resolver's strict parse and source binding over the identical retained bytes.
            var parsed = ProjectedSceneJson.Parse(retained);
            Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
            var centralScene = parsed.Scene!;
            Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, centralScene.SchemaVersion);
            Assert.AreEqual(descriptor.Capture.CaptureId, centralScene.Source.CaptureId);
            Assert.AreEqual(raw.ArtifactId, centralScene.Source.ArtifactId);
            Assert.AreEqual(descriptorSha256, centralScene.Source.ArtifactIdentitySha256);
            Assert.AreEqual(sceneProduct.ContentIdentitySha256, centralScene.SceneIdentitySha256);
            Assert.AreEqual(width, centralScene.ImageTransform.OutputWidthPixels);
            Assert.AreEqual(height, centralScene.ImageTransform.OutputHeightPixels);

            // Non-vacuity: an outlined object and an ellipse footprint, each with geometry, before any comparison.
            var deepSky = centralScene.DeepSky!;
            var outlined = deepSky.Objects.Where(static item => item.Representation == DeepSkyRepresentation.Outline).ToArray();
            var ellipses = deepSky.Objects.Where(static item => item.Representation == DeepSkyRepresentation.Footprint).ToArray();
            Assert.IsNotEmpty(outlined, camera);
            Assert.IsNotEmpty(ellipses, camera);
            foreach (var item in outlined)
            {
                var outline = deepSky.Outlines.Single(candidate => candidate.Id == item.Id);
                Assert.IsNotEmpty(outline.Parts, item.Id);
                Assert.IsTrue(outline.Parts.All(static part => part.Points.Count > 0), item.Id);
            }
            var resolvedFootprints = centralScene.ResolvedFootprints!;
            foreach (var item in ellipses)
            {
                var footprint = resolvedFootprints.Single(candidate => candidate.Id == item.Id);
                Assert.AreEqual(ResolvedFootprintSourceKind.DeepSkyObject, footprint.SourceKind, item.Id);
                Assert.AreEqual(ResolvedFootprintShape.Ellipse, footprint.Extent.Shape, item.Id);
                Assert.IsNotEmpty(footprint.Parts, item.Id);
                Assert.IsTrue(footprint.Parts.All(static part => part.Points.Count > 0), item.Id);
            }
            if (row.Centred is { } centredId)
            {
                var centred = deepSky.Objects.Single(item => item.Id == centredId);
                Assert.AreEqual(row.Expected, centred.Representation);
                var pixel = centred.Pixel!.Value;
                Assert.IsLessThanOrEqualTo(0.01, Math.Abs(pixel.X - row.Projection.PrincipalPointX), centredId);
                Assert.IsLessThanOrEqualTo(0.01, Math.Abs(pixel.Y - row.Projection.PrincipalPointY), centredId);
            }
            else
            {
                foreach (var item in outlined.Concat(ellipses))
                {
                    var pixel = item.Pixel!.Value;
                    var offset = Math.Sqrt(Math.Pow(pixel.X - row.Projection.PrincipalPointX, 2) +
                        Math.Pow(pixel.Y - row.Projection.PrincipalPointY, 2));
                    Assert.IsGreaterThan(100d, offset, item.Id);
                }
            }

            // Geometry per object ID: the scene the edge bound, the scene the edge layer reads, and the scene the
            // central host reads.
            AssertSameDeepSkyGeometry(bound, centralScene);
            AssertSameDeepSkyGeometry(edgeScene, centralScene);

            // Annotation: the edge mapping and the central resolver's mapping agree and leave deep-sky objects to
            // their own layer.
            var edgeObjects = ProjectedSceneAnnotation.CreateObjects(
                edgeScene, new AnnotationProcessingStepOptions().MaximumLabelMagnitude);
            var centralObjects = CentralProjectedSceneResolver.CreateAnnotation(centralScene, sceneId).Objects;
            Assert.HasCount(edgeObjects.Count, centralObjects);
            Assert.IsFalse(centralObjects.Any(static item => item.Id.StartsWith(ProjectedDeepSkyObject.IdPrefix, StringComparison.Ordinal)));
            for (var index = 0; index < edgeObjects.Count; index++)
            {
                var edgeObject = edgeObjects[index];
                var centralObject = centralObjects[index];
                Assert.AreEqual(edgeObject.Id, centralObject.Id);
                Assert.AreEqual(edgeObject.Pixel, centralObject.Pixel, edgeObject.Id);
                Assert.AreEqual(edgeObject.DrawMark, centralObject.DrawMark, edgeObject.Id);
                Assert.AreEqual(edgeObject.DrawLabel, centralObject.DrawLabel, edgeObject.Id);
                Assert.AreEqual(edgeObject.FootprintParts is null, centralObject.FootprintParts is null, edgeObject.Id);
                if (edgeObject.FootprintParts is { } edgeParts)
                    AssertSameParts(edgeParts, centralObject.FootprintParts!, edgeObject.Id);
            }

            // Presentation: the deep-sky layer each host would draw from its scene, then the materialized frame.
            var sceneOptions = new ScenePresentationLayerProcessingStepOptions();
            var deepSkyOptions = new ScenePresentationDeepSkyOptions { OutputVariant = "deep-sky-layer" };
            var style = new PresentationDeepSkyStyleV1(deepSkyOptions.MaximumLabels, sceneOptions.MaximumLabelCharacters,
                sceneOptions.LabelScale, deepSkyOptions.CatalogLabels);
            var edgeLayer = PresentationDeepSkyLayerProducer.Create(edgeScene, style);
            var centralLayer = PresentationDeepSkyLayerProducer.Create(centralScene, style);
            Assert.IsTrue(edgeLayer.Objects.Any(static item => item.Drawing == PresentationDeepSkyDrawing.Outline), camera);
            Assert.IsTrue(edgeLayer.Objects.Any(static item => item.Drawing == PresentationDeepSkyDrawing.Footprint), camera);
            CollectionAssert.AreEqual(edgeLayer.Objects.ToArray(), centralLayer.Objects.ToArray());
            var layerProduct = PresentationProcessingProducts.CreateLayerProduct(edgeLayer.Payload,
                deepSkyOptions.OutputVariant, [AsTypedArtifact(sceneProduct)], PresentationDeepSkyLayerProducer.ProducerVersion);
            CollectionAssert.AreEqual(PresentationLayerPayloadJson.Serialize(centralLayer.Payload), layerProduct.Payload.ToArray());

            var baseArtifact = AsTypedArtifact(await Execute(raw with { Payload = pixels },
                BuiltInProcessingRecipes.EncodedPreview, new EncodedPreviewOptions(OutputEncoding: "Packed")));
            var reference = PresentationProcessingProducts.CreateReference(baseArtifact, edgeLayer.Payload.SourceIdentitySha256);
            var layerArtifact = AsTypedArtifact(layerProduct);
            var layer = new PresentationLayerProductInput(LayeredPresentationJson.CreateLayer(
                LayeredPresentationCaptureProcessing.DeepSkyLayerKind,
                new PresentationProductReference(layerArtifact.ArtifactId, edgeLayer.Payload.ContentIdentitySha256,
                    layerArtifact.MediaType, reference.Compatibility),
                edgeLayer.Payload.SourceIdentitySha256, PresentationCoordinateSpace.ScenePixels,
                PresentationLayerCompositor.AlgorithmVersion, layerProduct.Recipe.Descriptor.ImplementationVersion,
                12, PresentationBlendMode.Normal, 1_000_000, false,
                LayeredPresentationCaptureProcessing.DeepSkyLayerOptions(layerProduct)), layerArtifact);
            var manifest = LayeredPresentationJson.CreateManifest(reference, edgeLayer.Payload.SourceIdentitySha256, [layer.Layer]);
            var manifestArtifact = AsTypedArtifact(PresentationProcessingProducts.CreateManifestProduct(
                reference, edgeLayer.Payload.SourceIdentitySha256, [layer], "manifest", baseArtifact));
            var rendered = PresentationMaterializationExecutor.MaterializePacked(baseArtifact, manifestArtifact, manifest,
                [layer], [layer.Layer.LayerIdentitySha256], "rendered");
            Assert.IsFalse(baseArtifact.Payload.Span.SequenceEqual(rendered.Payload.Span), camera);

            var retainedLayer = PresentationLayerPayloadJson.Parse(layerProduct.Payload).Payload!;
            Assert.AreEqual(layer.Layer.SourceProduct.ProductIdentitySha256, retainedLayer.ContentIdentitySha256);
            var baseLayout = baseArtifact.Layout!;
            var composed = PresentationLayerCompositor.CompositeDisplay(
                new ImageLayout(baseLayout.Width, baseLayout.Height, baseLayout.PixelFormat, baseLayout.StrideBytes),
                baseArtifact.Payload, [new PresentationCompositorLayer(retainedLayer, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
            CollectionAssert.AreEqual(rendered.Payload.ToArray(), composed.Pixels);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectionContext DeepSkyPerspective(double altitude, double azimuth) => new(
        ProjectionModel.Perspective, 500, 500, 400, 400, 1000, 1000, ProjectionAperture.Rectangular,
        BoresightAltitudeDegrees: altitude, BoresightAzimuthDegrees: azimuth);

    // The staging store requires a root with no symbolic link in its path.
    private static string DeepSkyPhysicalPath(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return current;
    }

    private static void AssertSameDeepSkyGeometry(ProjectedSceneV1 expected, ProjectedSceneV1 actual)
    {
        var expectedObjects = expected.DeepSky!.Objects;
        var actualObjects = actual.DeepSky!.Objects;
        CollectionAssert.AreEqual(expectedObjects.Select(static item => item.Id).ToArray(),
            actualObjects.Select(static item => item.Id).ToArray());
        for (var index = 0; index < expectedObjects.Count; index++)
            Assert.AreEqual(expectedObjects[index], actualObjects[index], expectedObjects[index].Id);

        var actualOutlines = actual.DeepSky.Outlines.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        Assert.HasCount(expected.DeepSky.Outlines.Count, actualOutlines);
        foreach (var outline in expected.DeepSky.Outlines)
        {
            var other = actualOutlines[outline.Id];
            Assert.AreEqual(outline.Level, other.Level, outline.Id);
            Assert.AreEqual(outline.RingCount, other.RingCount, outline.Id);
            Assert.AreEqual(outline.Clipped, other.Clipped, outline.Id);
            Assert.AreEqual(outline.Bounds, other.Bounds, outline.Id);
            AssertSameParts(outline.Parts, other.Parts, outline.Id);
        }

        var expectedFootprints = expected.ResolvedFootprints!
            .Where(static item => item.SourceKind == ResolvedFootprintSourceKind.DeepSkyObject).ToArray();
        var actualFootprints = actual.ResolvedFootprints!
            .Where(static item => item.SourceKind == ResolvedFootprintSourceKind.DeepSkyObject)
            .ToDictionary(static item => item.Id, StringComparer.Ordinal);
        Assert.HasCount(expectedFootprints.Length, actualFootprints);
        foreach (var footprint in expectedFootprints)
        {
            var other = actualFootprints[footprint.Id];
            Assert.AreEqual(footprint.Extent, other.Extent, footprint.Id);
            Assert.AreEqual(footprint.CenterPixel, other.CenterPixel, footprint.Id);
            Assert.AreEqual(footprint.Clipped, other.Clipped, footprint.Id);
            Assert.AreEqual(footprint.Bounds, other.Bounds, footprint.Id);
            AssertSameParts(footprint.Parts, other.Parts, footprint.Id);
            Assert.AreEqual(ProjectedSceneAnnotation.FootprintAnchor(footprint, expected.ImageTransform),
                ProjectedSceneAnnotation.FootprintAnchor(other, actual.ImageTransform), footprint.Id);
        }
    }

    private static void AssertSameParts(IReadOnlyList<ResolvedFootprintPart> expected, IReadOnlyList<ResolvedFootprintPart> actual, string id)
    {
        Assert.HasCount(expected.Count, actual, id);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(expected[index].Closed, actual[index].Closed, id);
            CollectionAssert.AreEqual(expected[index].Points.ToArray(), actual[index].Points.ToArray(), id);
        }
    }
}
