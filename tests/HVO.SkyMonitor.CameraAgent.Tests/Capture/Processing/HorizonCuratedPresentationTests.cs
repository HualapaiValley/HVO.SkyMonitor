using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class HorizonCuratedPresentationTests
{
    [TestMethod]
    public async Task IndependentDeepSkyLayerKeepsStarPayloadAndArchivedGeometrySeparate()
    {
        var fixture = await FixtureAsync("clear").ConfigureAwait(false);
        using var services = Services(fixture);
        string? starIdentity = null;
        string? deepSkyIdentity = null;
        foreach (var replay in new[] { false, true })
        {
            var context = Context(fixture, replay);
            var step = new ScenePresentationLayerCaptureProcessingStep(new("layer", "ScenePresentationLayer", 1),
                new() { IncludeDeepSkyLayer = true, DeepSkyLabels = false, DeepSkyOutlines = false }, services);
            Assert.HasCount(5, step.Outputs);
            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            var products = context.ProcessingOutcomes.SelectMany(static outcome => outcome.Products).ToArray();
            var stars = products.Single(static product => product.Variant == "scene-annotation-layer-v1");
            var deepSky = products.Single(static product => product.Variant == "scene-deep-sky-v1");
            var starPayload = PresentationLayerPayloadJson.Parse(stars.Payload).Payload!;
            var payload = PresentationLayerPayloadJson.Parse(deepSky.Payload).Payload!;
            Assert.IsFalse(starPayload.TextBlocks.SelectMany(static block => block.Lines).Contains(PresentationDeepSkyLayerProducer.Legend));
            Assert.IsTrue(payload.TextBlocks.SelectMany(static block => block.Lines).Contains(PresentationDeepSkyLayerProducer.Legend));
            Assert.HasCount(1, payload.TextBlocks, "Labels are disabled, while the scientific explanation remains.");
            Assert.AreEqual(fixture.Scene.SceneIdentitySha256, payload.SourceIdentitySha256);
            if (starIdentity is not null) Assert.AreEqual(starIdentity, stars.OutputIdentitySha256);
            if (deepSkyIdentity is not null) Assert.AreEqual(deepSkyIdentity, deepSky.OutputIdentitySha256);
            starIdentity = stars.OutputIdentitySha256;
            deepSkyIdentity = deepSky.OutputIdentitySha256;
            Assert.IsTrue(context.Artifacts!.Raw.Frame.PixelData.Span.IndexOfAnyExcept((byte)0) < 0);

            var source = CameraAgentRecipeExecutionAdapter.CreateArtifact(context,
                context.GetDependencyProducts().Single(static product => ProjectedSceneV1.IsSupportedSchemaVersion(product.SchemaVersion)));
            context.BeginNode("environment", []);
            LayeredPresentationCaptureProcessing.Add(context, PresentationProcessingProducts.CreateLayerProduct(
                PresentationLayerPayloadJson.Create(fixture.Scene.SceneIdentitySha256, 484, 304), "environment-layer-v1", [source], "fixture-v1"));
            context.BeginNode("manifest", ["preview", "presentation", "environment"]);
            await new OverlayManifestCaptureProcessingStep(new("manifest", "OverlayManifest", 2),
                new() { BasePreviewVariant = "preview-v1", SceneDeepSkyVariant = "scene-deep-sky-v1" }).ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            var manifest = LayeredPresentationJson.ParseManifest(context.ProcessingProducts[^1].Payload).Document!;
            Assert.HasCount(6, manifest.Layers);
            Assert.AreEqual(CaptureProcessingContext.CreateArtifactId(deepSky.OutputIdentitySha256),
                manifest.Layers.Single(static layer => layer.LayerKind == "scene-deep-sky").SourceProduct.ArtifactId);
            foreach (var visible in new[] { true, false })
            {
                context.BeginNode(visible ? "visible" : "hidden", ["preview", "manifest", "presentation", "environment"]);
                await new PresentationMaterializerCaptureProcessingStep(new("materializer", "PresentationMaterializer", 3),
                    new()
                    {
                        BasePreviewVariant = "preview-v1",
                        OutputVariant = visible ? "deep-visible" : "deep-hidden",
                        EnabledLayerKinds = [visible ? "scene-deep-sky" : "environment"]
                    })
                    .ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(visible, context.ProcessingProducts[^1].Payload.Span.IndexOfAnyExcept((byte)0) >= 0,
                    "The retained manifest and real compositor must support the independent deep-sky selection.");
            }
            Assert.IsTrue(context.Artifacts.Raw.Frame.PixelData.Span.IndexOfAnyExcept((byte)0) < 0);
        }
    }

    [TestMethod]
    [DataRow("ScenePresentationLayer", "blocked")]
    [DataRow("ScenePresentationLayer", "unknown")]
    [DataRow("ScenePresentationLayer", "incompatible")]
    [DataRow("ScenePresentationLayer", "clear")]
    [DataRow("SceneStylePresentationLayerV1", "blocked")]
    [DataRow("SceneStylePresentationLayerV1", "unknown")]
    [DataRow("SceneStylePresentationLayerV1", "incompatible")]
    [DataRow("SceneStylePresentationLayerV1", "clear")]
    public async Task CuratedGlyphsFootprintsAndLabelsUseHorizonThroughBothAliasesAndArchivedExecution(string alias, string mode)
    {
        var fixture = await FixtureAsync(mode).ConfigureAwait(false);
        using var services = Services(fixture);
        var retained = ProjectedSceneJson.Serialize(fixture.Scene).ToArray();
        string? identity = null;
        foreach (var replay in new[] { false, true })
        {
            var context = Context(fixture, replay);
            ScenePresentationLayerCaptureProcessingStep step = alias == "ScenePresentationLayer"
                ? new ScenePresentationLayerCaptureProcessingStep(new("layer", alias, 1), new() { DrawMarkers = true, DrawConstellationLines = true }, services)
                : new SceneStylePresentationCaptureProcessingStep(new("layer", alias, 1), new() { DrawMarkers = true, DrawConstellationLines = true }, services);
            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            var product = context.ProcessingOutcomes.Single().Products[0];
            var payload = PresentationLayerPayloadJson.Parse(product.Payload).Payload!;
            Assert.AreEqual(mode == "clear", payload.Segments.Count > 0, "Both catalog footprints and glyph strokes are excluded.");
            Assert.AreEqual(mode == "clear", payload.TextBlocks.Count > 1, "Only the scientific caption remains when no object is eligible.");
            Assert.AreEqual(mode == "clear", payload.Markers.Count > 0, "Ordinary stars must survive the redacted lane only when eligible.");
            var constellation = PresentationLayerPayloadJson.Parse(context.ProcessingOutcomes.Single().Products[3].Payload).Payload!;
            Assert.AreEqual(mode == "clear", constellation.Segments.Count > 0, "Constellations use the same capture-time site.");
            Assert.AreEqual(fixture.Scene.SceneIdentitySha256, payload.SourceIdentitySha256);
            if (identity is not null) Assert.AreEqual(identity, product.OutputIdentitySha256);
            identity = product.OutputIdentitySha256;
            CollectionAssert.AreEqual(retained, ProjectedSceneJson.Serialize(fixture.Scene).ToArray());
            Assert.IsTrue(context.Artifacts!.Raw.Frame.PixelData.Span.IndexOfAnyExcept((byte)0) < 0);
        }
    }

    [TestMethod]
    [DataRow("blocked")]
    [DataRow("unknown")]
    [DataRow("incompatible")]
    [DataRow("clear")]
    public async Task ActualFlattenedExecutorFiltersCuratedCompositionInLiveAndArchivedExecution(string mode)
    {
        var fixture = await FixtureAsync(mode).ConfigureAwait(false);
        using var services = Services(fixture);
        var retained = ProjectedSceneJson.Serialize(fixture.Scene).ToArray();
        byte[]? first = null;
        foreach (var replay in new[] { false, true })
        {
            var context = Context(fixture, replay);
            var step = new AnnotationCaptureProcessingStep(new("annotation", "Annotation", 2), new()
            {
                RequireProjectedSceneDependency = true,
                DrawMetadataCorners = false,
                DrawImageCircle = false,
                DrawCardinalDirections = false,
                DrawConstellationLines = false,
                DrawLabels = true
            }, new ProjectedSceneStore(), new NeverGenerateScene(),
                new(new ProcessingRecipeExecutor()), services);
            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            var outcome = context.ProcessingOutcomes.Single();
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, $"{outcome.ReasonCode}: {outcome.Field}");
            var product = outcome.Products.Single();
            Assert.IsTrue(product.Algorithms.Any(static algorithm => algorithm.Name == "horizon-curated-selection"));
            var frame = context.Artifacts![FrameArtifactRole.AnnotatedPreview].Frame;
            // The mandatory scientific caption is above row 40. Curated objects are near the image centre.
            Assert.AreEqual(mode == "clear", frame.PixelData.Span[(40 * frame.Width)..].IndexOfAnyExcept((byte)0) >= 0,
                "The real executor must not composite excluded catalog objects after filtering the ordinary annotation.");
            if (first is not null) CollectionAssert.AreEqual(first, frame.PixelData.ToArray());
            first = frame.PixelData.ToArray();
            CollectionAssert.AreEqual(retained, ProjectedSceneJson.Serialize(fixture.Scene).ToArray());
            Assert.IsTrue(context.Artifacts.Raw.Frame.PixelData.Span.IndexOfAnyExcept((byte)0) < 0);
        }
    }

    private sealed record Fixture(CameraModuleConfig Config, ArtifactManifestV2 Manifest, ProjectedSceneV1 Scene);

    private static async Task<Fixture> FixtureAsync(string mode)
    {
        var manifest = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono8, 484, 304, 484, new byte[484 * 304]);
        var epoch = manifest.Descriptor.Timing.ExposureStartedUtc;
        var location = DeploymentLocationSnapshot.Create("retained-site", 4, "synthetic", null,
            epoch.AddDays(-1), null, 35, -114, 1200, "UTC");
        manifest = manifest with { Descriptor = manifest.Descriptor with { Location = location.ToProvenance() } };
        var rig = new CameraRigConfig(new("virtual", 484, 304, 2, SensorColorMode.Mono, CameraPixelFormat.Mono8),
            new("Perspective", 0, 100, 0, LensKind.Rectilinear, PrincipalPointX: 242, PrincipalPointY: 152,
                FocalLengthXPixels: 120, FocalLengthYPixels: 120, CalibrationVersion: "curated-v1"),
            new(60, 0, 0), new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 100));
        var profile = new HorizonObstructionProfile(HorizonObstructionProfile.CurrentSchemaVersion, "curated-horizon", 1,
            "virtual-installation", HorizonObstructionProfile.ComputeSiteIdentitySha256(35, -114, mode == "incompatible" ? 1300 : 1200, location.ToProvenance()),
            HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig), HorizonObstructionSource.Synthetic, 90,
            mode == "unknown" ? [] : Enumerable.Range(0, 4).Select(i => new HorizonObstructionSample(i * 90,
                mode == "blocked" ? 90 : 0)).ToArray());
        rig = rig with { HorizonObstruction = profile };
        var request = new VisibleSceneRequest(epoch, new(35, -114, 1200), RigProjectionContextFactory.Create(rig),
            new CatalogQuery(6, 10), new("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "1"),
            projectionVersion: RigProjectionContextFactory.AlgorithmVersion);
        EquatorialPoint Position(double altitude, double azimuth) => EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new(altitude, azimuth), epoch, 35, -114), epoch);
        var starA = Position(60, 0);
        var starB = Position(58, 5);
        var catalog = new InMemoryCelestialCatalog([
            new("star-a", "Star A", starA.RightAscensionHours, starA.DeclinationDegrees, 1, HipparcosId: "1"),
            new("star-b", "Star B", starB.RightAscensionHours, starB.DeclinationDegrees, 1, HipparcosId: "2")]);
        var topology = new InMemoryConstellationTopology([new("TST", "1", "2")],
            new("fixture", "1", new Uri("https://example.test/topology"), new string('D', 64), "test", "1"));
        request = new VisibleSceneRequest(epoch, request.Observer, request.Projection, request.CatalogQuery,
            request.CatalogMetadata, projectionVersion: request.ProjectionVersion, constellationIds: ["TST"]);
        var visible = await new VisibleSceneBuilder(catalog, topology).BuildAsync(request).ConfigureAwait(false);
        Assert.IsNotEmpty(visible.Segments);
        var a = Position(60, 0);
        var b = Position(50, 20);
        var c = Position(45, 340);
        DeepSkyObject[] objects =
        [
            new("NGC0224", "NGC 224", "M31", "G", a.RightAscensionHours, a.DeclinationDegrees, "And", 600, 300, 90,
                null, null, null, null, 31, null, null, null),
            new("M040", "M40", "M40", "**", b.RightAscensionHours, b.DeclinationDegrees, "UMa", null, null, null,
                null, null, null, null, 40, null, null, null),
            new("NGC7000", "NGC 7000", "NGC 7000", "Neb", c.RightAscensionHours, c.DeclinationDegrees, "Cyg", null, null, null,
                null, null, null, null, null, null, null, "North America")
        ];
        var semantics = new DeepSkySemantics("OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
            new Uri("https://github.com/mattiaverga/OpenNGC"), "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned", "J2000.0", "arcminute",
            "degrees-north-through-east-0-inclusive-to-180-exclusive", "1-widest-2-standard-3-narrowest",
            "b-mag-per-square-arcsecond-within-25-mag-isophote");
        var aliases = objects.Select(static item => new DeepSkyAlias(item.Designation, item.Id, DeepSkyAliasKinds.Designation))
            .Concat(objects.Where(static item => item.DisplayName != item.Designation)
                .Select(static item => new DeepSkyAlias(item.DisplayName, item.Id, DeepSkyAliasKinds.Messier)));
        visible = visible.WithCuratedDeepSky(new DeepSkyCatalog(semantics, objects, aliases, [], []), new(128, 32, 0, 1, .1));
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible, ProjectedSceneImageTransformV1.Identity(484, 304),
            new(manifest.Descriptor.Capture.CaptureId, manifest.Descriptor.Artifact.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor)), rig.Optics.CalibrationVersion, RigProjectionContextFactory.AlgorithmVersion);
        Assert.HasCount(3, scene.DeepSky!.Objects);
        Assert.IsNotEmpty(scene.ResolvedFootprints!);
        Assert.IsTrue(scene.DeepSky.Objects.Any(static item => item.Representation == DeepSkyRepresentation.UnknownExtentGlyph));
        return new(new(new(35, -114, 1200, "UTC"), new("VirtualSky"), rig, CapturePipelineConfig.Empty) { DeploymentLocation = location }, manifest, scene);
    }

    private static CaptureProcessingContext Context(Fixture fixture, bool replay)
    {
        var descriptor = fixture.Manifest.Descriptor;
        var raw = new CameraFrame(descriptor.Timing.ExposureStartedUtc, 484, 304, CameraPixelFormat.Mono8,
            new byte[484 * 304], new(TimeSpan.FromSeconds(1), 99.5, -9.5), 484);
        var artifacts = new FrameArtifactSet(new FrameArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, raw, []));
        var submission = new CaptureLoopSubmission(new(descriptor.Timing.RequestedStartUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            new(raw, new(TimeSpan.FromSeconds(1), 99.5, null, null), TimeSpan.Zero, CaptureMode.Still, false, Artifacts: artifacts),
            descriptor.Timing.RequestedStartUtc, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, fixture.Manifest,
            new StoredFrameReference("raw.bin", "/virtual/raw.bin", raw.TimestampUtc, FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(fixture.Manifest));
        var envelope = CaptureLaneEnvelopeSerializer.Serialize(fixture.Config, submission);
        var config = CaptureLaneEnvelopeSerializer.Deserialize(envelope.Json, envelope.Sha256).Configuration;
        Assert.IsTrue(config.DeploymentLocationRedacted);
        Assert.AreEqual(0, config.Observatory.LatitudeDegrees);
        Assert.IsNull(config.DeploymentLocation);
        var context = new CaptureProcessingContext(config, submission, receipt, null,
            replay ? new(Guid.NewGuid(), ProcessingGraphExecutionClass.Replay, "archived", new string('F', 64), false, 1, "lease") : null);
        context.BeginNode("preview", []);
        var previewRecipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(BuiltInProcessingRecipes.FixedPreview,
            "1", "synthetic-preview", JsonSerializer.SerializeToElement(new { })));
        var preview = context.AddDerivative(FrameArtifactRole.Preview, raw, "preview-v1", [descriptor.Artifact.ArtifactId],
            CaptureProcessingContext.CreateArtifactId(new string('D', 64)));
        context.AssociateProcessingProduct(preview, new(FrameArtifactRole.Preview, "preview-v1", new string('D', 64),
            "application/x-hvo-packed-image", descriptor.Layout, raw.PixelData, ProcessingIdentity.ComputePayloadSha256(raw.PixelData),
            previewRecipe, [], [descriptor.Artifact.ArtifactId], TimeSpan.FromSeconds(1),
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor)));
        var payload = ProjectedSceneJson.Serialize(fixture.Scene);
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(BuiltInProcessingRecipes.ProjectedScene,
            "1", "fixture", JsonSerializer.SerializeToElement(new { })));
        context.BeginNode("scene", []);
        context.RegisterProcessingProduct(new(FrameArtifactRole.Metadata, "scene", new string('B', 64),
            StructuredProcessingProductContracts.ProjectedSceneMediaType, null, payload, ProcessingIdentity.ComputePayloadSha256(payload),
            recipe, [], [descriptor.Artifact.ArtifactId], TimeSpan.Zero, CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor))
        { Kind = ProcessingProductKind.Metadata, SchemaVersion = fixture.Scene.SchemaVersion, ContentIdentitySha256 = fixture.Scene.SceneIdentitySha256 });
        context.BeginNode("presentation", ["scene", "preview"]);
        return context;
    }

    private static ServiceProvider Services(Fixture fixture)
    {
        var location = fixture.Config.DeploymentLocation!;
        var store = new Mock<IDeploymentLocationStore>(MockBehavior.Strict);
        store.Setup(s => s.Resolve(location.ToProvenance(), fixture.Manifest.Descriptor.Timing.ExposureStartedUtc))
            .Returns(location);
        return new ServiceCollection().AddSingleton(store.Object).BuildServiceProvider();
    }

    [TestMethod]
    public async Task RedactedCaptureWithoutHistoryRefusesRatherThanSubstitutingCurrentCoordinates()
    {
        var fixture = await FixtureAsync("clear").ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new ScenePresentationLayerCaptureProcessingStep(new("layer", "ScenePresentationLayer", 1), new())
                .ProcessAsync(Context(fixture, true), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private sealed class NeverGenerateScene : IAnnotationSceneProvider
    {
        public ValueTask<AnnotationSceneResult> BuildAsync(CameraModuleConfig config, ReconstructionDescriptor? descriptor,
            CameraFrame rawFrame, IReadOnlyList<string> constellationIds, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Archived curated geometry must come from its retained scene.");
    }
}
