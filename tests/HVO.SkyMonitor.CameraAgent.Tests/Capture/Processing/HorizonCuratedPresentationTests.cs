using System.Text.Json;
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
        var retained = ProjectedSceneJson.Serialize(fixture.Scene).ToArray();
        string? identity = null;
        foreach (var replay in new[] { false, true })
        {
            var context = Context(fixture, replay);
            ScenePresentationLayerCaptureProcessingStep step = alias == "ScenePresentationLayer"
                ? new ScenePresentationLayerCaptureProcessingStep(new("layer", alias, 1), new())
                : new SceneStylePresentationCaptureProcessingStep(new("layer", alias, 1), new());
            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            var product = context.ProcessingOutcomes.Single().Products[0];
            var payload = PresentationLayerPayloadJson.Parse(product.Payload).Payload!;
            Assert.AreEqual(mode == "clear", payload.Segments.Count > 0, "Both catalog footprints and glyph strokes are excluded.");
            Assert.AreEqual(mode == "clear", payload.TextBlocks.Count > 1, "Only the scientific caption remains when no object is eligible.");
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
                new(new ProcessingRecipeExecutor()));
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
        var rig = new CameraRigConfig(new("virtual", 484, 304, 2, SensorColorMode.Mono, CameraPixelFormat.Mono8),
            new("Perspective", 0, 100, 0, LensKind.Rectilinear, PrincipalPointX: 242, PrincipalPointY: 152,
                FocalLengthXPixels: 120, FocalLengthYPixels: 120, CalibrationVersion: "curated-v1"),
            new(60, 0, 0), new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 100));
        var profile = new HorizonObstructionProfile(HorizonObstructionProfile.CurrentSchemaVersion, "curated-horizon", 1,
            "virtual-installation", HorizonObstructionProfile.ComputeSiteIdentitySha256(35, -114, mode == "incompatible" ? 1300 : 1200),
            HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig), HorizonObstructionSource.Synthetic, 90,
            mode == "unknown" ? [] : Enumerable.Range(0, 4).Select(i => new HorizonObstructionSample(i * 90,
                mode == "blocked" ? 90 : 0)).ToArray());
        rig = rig with { HorizonObstruction = profile };
        var request = new VisibleSceneRequest(epoch, new(35, -114, 1200), RigProjectionContextFactory.Create(rig),
            new CatalogQuery(6, 10), new("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "1"),
            projectionVersion: RigProjectionContextFactory.AlgorithmVersion);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(request).ConfigureAwait(false);
        EquatorialPoint Position(double altitude, double azimuth) => EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new(altitude, azimuth), epoch, 35, -114), epoch);
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
        return new(new(new(35, -114, 1200, "UTC"), new("VirtualSky"), rig, CapturePipelineConfig.Empty), manifest, scene);
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
        var context = new CaptureProcessingContext(fixture.Config, submission, receipt, null,
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

    private sealed class NeverGenerateScene : IAnnotationSceneProvider
    {
        public ValueTask<AnnotationSceneResult> BuildAsync(CameraModuleConfig config, ReconstructionDescriptor? descriptor,
            CameraFrame rawFrame, IReadOnlyList<string> constellationIds, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Archived curated geometry must come from its retained scene.");
    }
}
