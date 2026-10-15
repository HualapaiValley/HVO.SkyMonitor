using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using SkiaSharp;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProjectedSceneAnnotationTests
{
    private static readonly DateTimeOffset CuratedUtc = new(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CaptureId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly ProcessingCompatibilityIdentity Compatibility = new(
        "rig-v1", "north-up", "none-v1", "full-v1", "sensor-v1", "night-v1", "pipeline-v1");
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LayerShowsExpectedLegendAndBasicGeometryWithBoundedLabels()
    {
        var scene = await SceneAsync().ConfigureAwait(false);
        var layer = PresentationDeepSkyLayerProducer.Create(scene, new(MaximumLabels: 1));
        Assert.IsTrue(layer.Payload.TextBlocks.SelectMany(static block => block.Lines)
            .Contains(PresentationDeepSkyLayerProducer.Legend, StringComparer.Ordinal));
        Assert.IsTrue(layer.Objects.Count(static item => item.Label is not null) <= 1);
        Assert.IsTrue(layer.Objects.Any(static item => item.Drawing == PresentationDeepSkyDrawing.Footprint));
        Assert.IsTrue(layer.Objects.Any(static item => item.Representation == DeepSkyRepresentation.UnknownExtentGlyph));
        Assert.IsTrue(layer.Payload.Segments.All(static item => item.Stroke is not null));
        Assert.IsFalse(ProjectedSceneAnnotation.CreateObjects(scene, 6).Any(static item => item.Id.StartsWith("deep-sky:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task LayerRevalidatesIdentityAfterNestedCallerMutation()
    {
        var scene = await SceneAsync().ConfigureAwait(false);
        var objects = scene.DeepSky!.Objects.ToArray();
        var borrowed = scene with { DeepSky = scene.DeepSky with { Objects = objects } };
        var layer = PresentationDeepSkyLayerProducer.Create(borrowed).Payload;
        objects[0] = objects[0] with { DisplayName = "Changed catalog label" };
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(borrowed));
        Assert.ThrowsExactly<ArgumentException>(() => PresentationDeepSkyLayerProducer.Create(borrowed));
        var texts = layer.TextBlocks.ToArray();
        texts[0] = texts[0] with { Lines = ["Changed presentation legend"] };
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerPayloadJson.Validate(layer with { TextBlocks = texts }));
    }

    [TestMethod]
    public async Task PreviewScaleMapsGeometryOnceAndRetainedReconstructionIsIdentical()
    {
        var scene = await SceneAsync().ConfigureAwait(false);
        var retained = ProjectedSceneJson.Parse(ProjectedSceneJson.Serialize(scene)).Scene!;
        var full = PresentationDeepSkyLayerProducer.Create(scene).Payload;
        var small = PresentationDeepSkyLayerProducer.Create(scene, previewTransform: new(.5, .5),
            outputWidth: 242, outputHeight: 152).Payload;
        // Physical outlines scale with the image. Symbolic glyphs deliberately retain their
        // minimum output-pixel radius and are not assertions about physical angular size.
        var physicalSegments = scene.ResolvedFootprints!.Sum(static footprint => footprint.Parts.Sum(
            static part => part.Closed ? part.Points.Count : part.Points.Count - 1));
        // Each presentation independently rounds vertices to 0.001 px. Half of the
        // full-size rounding error plus the preview's rounding error is <=0.00075 px.
        const double QuantizationBound = 0.000750001;
        for (var i = 0; i < physicalSegments; i++)
        {
            Assert.AreEqual(full.Segments[i].From.X / 2, small.Segments[i].From.X, QuantizationBound);
            Assert.AreEqual(full.Segments[i].From.Y / 2, small.Segments[i].From.Y, QuantizationBound);
        }
        var recreated = PresentationDeepSkyLayerProducer.Create(retained).Payload;
        CollectionAssert.AreEqual(PresentationLayerPayloadJson.Serialize(full), PresentationLayerPayloadJson.Serialize(recreated));
    }

    [TestMethod]
    [DataRow("Packed")]
    [DataRow("Jpeg")]
    public async Task ActualAnnotationRecipeDrawsLayerAndKeepsBorrowedRawPreviewBytes(string encoding)
    {
        var scene = await SceneAsync().ConfigureAwait(false);
        var (primary, sceneArtifact) = Artifacts(scene);
        var original = primary.Payload.ToArray();
        var request = Request(primary, sceneArtifact, encoding);
        var executor = new ProcessingRecipeExecutor();
        var result = await executor.ExecuteAsync(request).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status);
        var product = result.Products.Single();
        Assert.IsFalse(product.Payload.Span.SequenceEqual(original));
        CollectionAssert.AreEqual(original, primary.Payload.ToArray());
        Assert.IsTrue(product.Algorithms.Any(static item => item.Name == "curated-dso-overlay"));
        Assert.AreEqual(2, product.SourceArtifactIds.Count);
        ProcessingRecipeTests.AssertProductMatchesContract(request, product);
        if (encoding == "Jpeg")
        {
            Assert.AreEqual("image/jpeg", product.MediaType);
            using var bitmap = SKBitmap.Decode(product.Payload.ToArray());
            Assert.IsNotNull(bitmap);
            Assert.AreEqual(484, bitmap.Width);
            Assert.AreEqual(304, bitmap.Height);
            var directory = string.IsNullOrEmpty(TestContext.TestRunResultsDirectory)
                ? Path.GetTempPath() : TestContext.TestRunResultsDirectory;
            var path = Path.Combine(directory, $"curated-annotated-fixture-{Guid.NewGuid():N}.jpg");
            var image = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var imageLifetime = image.ConfigureAwait(false);
            await image.WriteAsync(product.Payload).ConfigureAwait(false);
            TestContext.AddResultFile(path);
        }
    }

    [TestMethod]
    public async Task WrongCaptureSceneFailsClosedInActualRecipe()
    {
        var scene = await SceneAsync().ConfigureAwait(false);
        var (primary, artifact) = Artifacts(scene);
        var result = await new ProcessingRecipeExecutor().ExecuteAsync(
            Request(primary with { CaptureId = Guid.NewGuid() }, artifact)).ConfigureAwait(false);
        Assert.AreNotEqual(ProcessingOutcomeStatus.Produced, result.Status);
        Assert.IsEmpty(result.Products);
    }

    [TestMethod]
    public async Task ActualCaptionPreservesExistingCornerMetadataAndLabelsHonorReservations()
    {
        var scene = await SceneAsync().ConfigureAwait(false);
        var (primary, artifact) = Artifacts(scene);
        var request = Request(primary, artifact);
        request = request with
        {
            Annotation = request.Annotation! with
            { MetadataOverlay = new MetadataCornerOverlay(["CAPTURE SAME", "EXPOSURE 1S"], [], [], []) }
        };
        var executor = new ProcessingRecipeExecutor();
        var before = await executor.ExecuteAsync(request with { Inputs = [primary], AuxiliaryInputs = [] }).ConfigureAwait(false);
        var after = await executor.ExecuteAsync(request).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, before.Status);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, after.Status);
        // The legacy two-line corner occupies rows 4..19. Its bytes remain intact under the new caption.
        CollectionAssert.AreEqual(before.Products.Single().Payload.Span[..(24 * 484)].ToArray(),
            after.Products.Single().Payload.Span[..(24 * 484)].ToArray());
        var reserved = PresentationDeepSkyLayerProducer.Create(scene,
            reservedLabelBounds: [new(0, 0, 484, 304)]);
        Assert.IsTrue(reserved.Objects.All(static item => item.Label is null));
        Assert.IsTrue(reserved.Objects.Any(static item => item.Drawing != PresentationDeepSkyDrawing.Omitted));
    }

    private static ProcessingExecutionRequest Request(ProcessingArtifact primary, ProcessingArtifact scene, string encoding = "Packed") => new(
        BuiltInProcessingRecipes.Annotation, JsonSerializer.SerializeToElement(new AnnotationRecipeOptions(OutputEncoding: encoding)),
        ProcessingInputSelector.RecipeResult(primary.Role, primary.Variant, primary.RecipeIdentitySha256),
        [primary, scene], "curated-annotated",
        new ProcessingAnnotationInput([], [], new PreviewTransform(1, 1), null, new string('A', 64)),
        AuxiliaryInputs: [new("projected-scene", ProcessingAuxiliaryInputKind.Artifact,
            ProcessingInputSelector.RecipeResult(scene.Role, scene.Variant, scene.RecipeIdentitySha256), ArtifactId: scene.ArtifactId)],
        InputArtifactId: primary.ArtifactId);

    [TestMethod]
    public async Task CrowdedCombinedGroupRetainsCaptionWithoutExceedingSharedTextBudget()
    {
        var scene = await SceneAsync(crowded: true).ConfigureAwait(false);
        var grouped = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeImageCircle: false,
            includeCardinalDirections: false, includeConstellations: false).StarAnnotations;
        Assert.AreEqual(PresentationLayerPayloadV1.MaximumTextBlocks, grouped.TextBlocks.Count,
            $"The fixture must actually reach the 64-block boundary; scene objects={scene.Objects.Count}.");
        Assert.IsTrue(grouped.TextBlocks.Count <= PresentationLayerPayloadV1.MaximumTextBlocks);
        Assert.IsTrue(grouped.TextBlocks.SelectMany(static block => block.Lines)
            .Contains(PresentationDeepSkyLayerProducer.Legend, StringComparer.Ordinal));
        Assert.IsTrue(PresentationLayerPayloadJson.Parse(PresentationLayerPayloadJson.Serialize(grouped)).IsValid);
    }

    private static (ProcessingArtifact Primary, ProcessingArtifact Scene) Artifacts(ProjectedSceneV1 scene)
    {
        var primary = new ProcessingArtifact(Guid.NewGuid(), FrameArtifactRole.Preview, "preview", new string('A', 64),
            "application/x-hvo-packed-image", new(484, 304, 484, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable,
                8, 8, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, null, 255, 484 * 304),
            new byte[484 * 304], CuratedUtc, TimeSpan.FromSeconds(1), Compatibility)
        { CaptureId = CaptureId };
        var artifact = primary with
        {
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Metadata,
            Variant = "projected-scene-v1",
            RecipeIdentitySha256 = new string('B', 64),
            MediaType = StructuredProcessingProductContracts.ProjectedSceneMediaType,
            Layout = null,
            Payload = ProjectedSceneJson.Serialize(scene),
            SchemaVersion = scene.SchemaVersion,
            ContentIdentitySha256 = scene.SceneIdentitySha256
        };
        return (primary, artifact);
    }

    private static async Task<ProjectedSceneV1> SceneAsync(bool crowded = false)
    {
        var request = new VisibleSceneRequest(CuratedUtc, new(30, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, 242, 152, 100, 100, 484, 304, ProjectionAperture.Rectangular),
            new CatalogQuery(6, 128), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64)),
            projectionVersion: "cal-v1");
        var stars = new List<CelestialCatalogObject>();
        if (crowded)
        {
            var projector = ProjectorFactory.Create(request.Projection);
            for (var index = 0; index < 120; index++)
            {
                var direction = projector.Unproject(new(15 + index % 12 * 40, 22 + index / 12 * 26))!.Value;
                var equatorial = CoordinateTransforms.HorizontalToEquatorial(direction, CuratedUtc, 30, 0);
                stars.Add(new($"S{index:D2}", $"N{index:D2}", equatorial.RightAscensionHours, equatorial.DeclinationDegrees, 1));
            }
        }
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(stars)).BuildAsync(request).ConfigureAwait(false);
        var ra = AstronomyTime.LocalMeanSiderealDegrees(CuratedUtc, 0) / 15;
        DeepSkyObject[] objects =
        [
            new("NGC0224", "NGC 224", "M31", "G", ra, 30, "And", 120, 60, 90, null, null, null, null, 31, null, null, null),
            new("M045", "M45", "M45", "OCl", ra + .1, 30, "Tau", 150, 150, null, null, null, null, null, 45, null, null, null),
            new("M040", "M40", "M40", "**", ra - .1, 30, "UMa", null, null, null, null, null, null, null, 40, null, null, null),
            new("NGC7000", "NGC 7000", "NGC 7000", "Neb", ra + .2, 30, "Cyg", null, null, null, null, null, null, null, null, null, null, null)
        ];
        var semantics = new DeepSkySemantics("OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
            new Uri("https://github.com/mattiaverga/OpenNGC"), "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned", "J2000.0", "arcminute",
            "degrees-north-through-east-0-inclusive-to-180-exclusive", "1-widest-2-standard-3-narrowest",
            "b-mag-per-square-arcsecond-within-25-mag-isophote");
        var aliases = objects.Select(static item => new DeepSkyAlias(item.Designation, item.Id, DeepSkyAliasKinds.Designation))
            .Concat(objects.Where(static item => item.MessierNumber is not null && item.DisplayName != item.Designation)
                .Select(static item => new DeepSkyAlias(item.DisplayName, item.Id, DeepSkyAliasKinds.Messier)));
        visible = visible.WithCuratedDeepSky(new DeepSkyCatalog(semantics, objects, aliases, [], []), new(128, 32, 0, 1, .1));
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(484, 304), new(CaptureId, Guid.NewGuid(), new string('D', 64)), "rig-v1", "cal-v1");
    }
}
