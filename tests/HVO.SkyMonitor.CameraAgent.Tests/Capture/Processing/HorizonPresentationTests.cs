using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class HorizonPresentationTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Observer = new(35, -114, 1200);

    [TestMethod]
    public async Task IdenticalEmptyLayersRemainBoundToTheirDifferentHorizonRevisions()
    {
        var (config, scene) = await FixtureAsync("effective", ProjectedSceneKind.Predicted, "unknown").ConfigureAwait(false);
        var profile = config.Rig.HorizonObstruction!;
        var next = new HorizonObstructionProfile(profile.SchemaVersion, profile.ProfileId, 2, profile.InstallationId,
            profile.SiteIdentitySha256, profile.GeometryIdentitySha256, profile.Source, profile.MaximumInterpolationGapDegrees, profile.Samples);
        var products = new List<ProcessingProduct>();
        foreach (var current in new[] { config, config with { Rig = config.Rig with { HorizonObstruction = next } } })
        {
            var context = Context(current, scene, replay: true);
            await new ScenePresentationLayerCaptureProcessingStep(new("layer", "ScenePresentationLayer", 1), new())
                .ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            products.Add(context.ProcessingOutcomes.Single().Products[0]);
        }
        Assert.AreEqual(products[0].ChecksumSha256, products[1].ChecksumSha256);
        Assert.AreNotEqual(products[0].OutputIdentitySha256, products[1].OutputIdentitySha256);
        var eligibility = new HorizonObstructionSceneEligibility(new(profile, config.Rig, Observer), config.Rig, scene);
        Assert.ThrowsExactly<ArgumentException>(() => eligibility.ValidateScene(scene with { SceneIdentitySha256 = new string('E', 64) }));
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
    public async Task BothLayerAliasesFilterStarsAndConstellationsInLiveAndArchivedExecution(string alias, string mode)
    {
        var (config, scene) = await FixtureAsync("effective", ProjectedSceneKind.Predicted, mode).ConfigureAwait(false);
        var retained = ProjectedSceneJson.Serialize(scene).ToArray();
        string? firstIdentity = null;
        foreach (var replay in new[] { false, true })
        {
            var context = Context(config, scene, replay);
            ScenePresentationLayerCaptureProcessingStep step = alias == "ScenePresentationLayer"
                ? new ScenePresentationLayerCaptureProcessingStep(new("layer", alias, 1), new() { DrawMarkers = true, DrawConstellationLines = true, DrawLabels = false })
                : new SceneStylePresentationCaptureProcessingStep(new("layer", alias, 1), new() { DrawMarkers = true, DrawConstellationLines = true, DrawLabels = false });
            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            var products = context.ProcessingOutcomes.Single().Products;
            var stars = PresentationLayerPayloadJson.Parse(products[0].Payload).Payload!;
            var figures = PresentationLayerPayloadJson.Parse(products[3].Payload).Payload!;
            Assert.AreEqual(mode == "clear" ? 2 : 0, stars.Markers.Count);
            Assert.AreEqual(mode == "clear", figures.Segments.Count > 0);
            Assert.AreEqual(scene.SceneIdentitySha256, stars.SourceIdentitySha256);
            if (firstIdentity is not null) Assert.AreEqual(firstIdentity, products[0].OutputIdentitySha256);
            firstIdentity = products[0].OutputIdentitySha256;
            CollectionAssert.AreEqual(retained, ProjectedSceneJson.Serialize(scene).ToArray());
        }
    }

    [TestMethod]
    [DataRow("effective", ProjectedSceneKind.ImageRegistered)]
    [DataRow("native", ProjectedSceneKind.ImageRegistered)]
    [DataRow("native-roi", ProjectedSceneKind.ImageRegistered)]
    [DataRow("native", ProjectedSceneKind.Predicted)]
    [DataRow("native-refracted", ProjectedSceneKind.ImageRegistered)]
    public async Task CompatibleRegisteredAndNativeTransformedScenesRetainClearAnnotations(string coordinates, ProjectedSceneKind kind)
    {
        foreach (var rotation in Enum.GetValues<ProjectedSceneQuarterRotation>())
            foreach (var mirror in new[] { false, true })
            {
                var (config, scene) = await FixtureAsync(coordinates, kind, "partial", rotation, mirror).ConfigureAwait(false);
                var bytes = ProjectedSceneJson.Serialize(scene).ToArray();
                var eligibility = new HorizonObstructionSceneEligibility(new(config.Rig.HorizonObstruction!, config.Rig, Observer), config.Rig, scene);
                Assert.IsTrue(eligibility.IsAligned);
                var filtered = HorizonObstructionAnnotation.Filter(eligibility, ProjectedSceneAnnotation.CreateObjects(scene, 6),
                    scene.Segments.Select(s => new ProjectedAnnotationSegment(s.ConstellationId, s.FromPixel, s.ToPixel)).ToArray());
                Assert.HasCount(1, filtered.Objects);
                Assert.AreEqual("clear", filtered.Objects[0].Id);
                Assert.IsLessThan(scene.Segments.Count, filtered.Segments.Count);
                Assert.IsTrue(filtered.Segments.All(s => eligibility.IsSegmentClear(s.FromPixel, s.ToPixel)));
                // Exercise the flattened step itself in archived execution, not only the shared helper.
                var context = Context(config, scene, replay: true);
                var executor = new RecordingExecutor();
                await new AnnotationCaptureProcessingStep(new("annotation", "Annotation", 2), new()
                {
                    RequireProjectedSceneDependency = true,
                    DrawMetadataCorners = false,
                    DrawImageCircle = false,
                    DrawCardinalDirections = false,
                    DrawConstellationLines = true,
                    DrawLabels = false
                }, new ProjectedSceneStore(), new UnusedSceneProvider(), new(executor))
                    .ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(executor.Request?.Annotation);
                Assert.HasCount(1, executor.Request.Annotation.Objects);
                Assert.AreEqual("clear", executor.Request.Annotation.Objects[0].Id);
                Assert.AreEqual(filtered.Segments.Count, executor.Request.Annotation.Segments.Count);
                CollectionAssert.AreEqual(bytes, ProjectedSceneJson.Serialize(scene).ToArray());
            }
    }

    [TestMethod]
    [DataRow("calibration")]
    [DataRow("pose")]
    [DataRow("parity")]
    [DataRow("readout")]
    [DataRow("site")]
    public async Task IncompatibleRetainedAlignmentRejectsAnnotations(string mismatch)
    {
        var (config, scene) = await FixtureAsync("native", ProjectedSceneKind.ImageRegistered, "partial").ConfigureAwait(false);
        var rig = config.Rig;
        var changed = mismatch switch
        {
            "calibration" => rig with { Optics = rig.Optics with { CalibrationVersion = "other" } },
            "pose" => rig with { Orientation = rig.Orientation with { BoresightAzimuthDegrees = 5 } },
            "parity" => rig with { Optics = rig.Optics with { HorizontalFlip = !rig.Optics.HorizontalFlip } },
            "readout" => rig with { Readout = rig.Readout! with { Roi = new(14, 16, 96, 96) } },
            _ => rig
        };
        var observer = mismatch == "site" ? Observer with { ElevationMeters = 1300 } : Observer;
        var eligibility = new HorizonObstructionSceneEligibility(new(rig.HorizonObstruction!, changed, observer), changed, scene);
        Assert.IsFalse(eligibility.IsAligned);
        Assert.IsFalse(eligibility.IsClear(scene.Objects[0].Pixel));
        Assert.IsEmpty(HorizonObstructionAnnotation.Filter(eligibility, ProjectedSceneAnnotation.CreateObjects(scene, 6), []).Objects);
    }

    private static async Task<(CameraModuleConfig Config, ProjectedSceneV1 Scene)> FixtureAsync(string coordinates,
        ProjectedSceneKind kind, string mode, ProjectedSceneQuarterRotation rotation = ProjectedSceneQuarterRotation.Degrees0,
        bool mirror = false)
    {
        var refracted = coordinates == "native-refracted";
        if (refracted) coordinates = "native";
        var rig = new CameraRigConfig(new("virtual", 128, 128, 2, SensorColorMode.Mono, CameraPixelFormat.Mono8),
            new("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, ImageCircleRadiusPixels: 60,
                FocalLengthXPixels: 40, FocalLengthYPixels: 40, CalibrationVersion: "calibration-v1"),
            new(90, 0, 0), new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1),
            Readout: new(new(16, 16, 96, 96), 2, 2, FrameBinningAlgorithm.DigitalAverageV1, CameraPixelFormat.Mono8,
                8, 8, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.IdentityV1, FrameLevelCodeSpace.StoredContainer, 0, 255));
        var profile = new HorizonObstructionProfile(HorizonObstructionProfile.CurrentSchemaVersion, "horizon", 1, "virtual-installation",
            HorizonObstructionProfile.ComputeSiteIdentitySha256(35, -114, mode == "incompatible" ? 1300 : 1200),
            HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig), HorizonObstructionSource.Synthetic, 90,
            mode == "unknown" ? [] : Enumerable.Range(0, 4).Select(i => new HorizonObstructionSample(i * 90,
                mode == "blocked" ? 90 : mode == "partial" ? 60 : 0)).ToArray());
        rig = rig with { HorizonObstruction = profile };
        var projection = coordinates switch
        {
            "native" => RigProjectionContextFactory.CreateNative(rig),
            "native-roi" => RigProjectionContextFactory.CreateNativeRoi(rig),
            _ => RigProjectionContextFactory.Create(rig)
        };
        var transform = new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            projection.WidthPixels, projection.HeightPixels, coordinates == "native" ? 16 : 0, coordinates == "native" ? 16 : 0,
            coordinates == "effective" ? 48 : 96, coordinates == "effective" ? 48 : 96,
            coordinates == "effective" ? 1 : 2, coordinates == "effective" ? 1 : 2, mirror, !mirror, rotation, 48, 48);
        CelestialCatalogObject Star(string id, string name, double altitude, string hip)
        {
            var eq = EquatorialPrecession.PrecessToJ2000(CoordinateTransforms.HorizontalToEquatorial(new(altitude, 45), Epoch, 35, -114), Epoch);
            return new(id, name, eq.RightAscensionHours, eq.DeclinationDegrees, 1, HipparcosId: hip);
        }
        var topology = new InMemoryConstellationTopology([new("TST", "1", "2")],
            new("fixture", "1", new Uri("https://example.test/topology"), new string('D', 64), "test", "1"));
        var catalog = new InMemoryCelestialCatalog([Star("blocked", "Blocked star", 30, "1"), Star("clear", "Clear star", 80, "2")]);
        var visible = await new VisibleSceneBuilder(catalog, topology).BuildAsync(new(Epoch, Observer, projection, new CatalogQuery(6, 10),
            new("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "1"),
            projectionVersion: RigProjectionContextFactory.AlgorithmVersion, constellationIds: ["TST"], refraction: new(refracted))).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(kind, visible, transform, new(Guid.NewGuid(), Guid.NewGuid(), new string('A', 64)),
            rig.Optics.CalibrationVersion, RigProjectionContextFactory.AlgorithmVersion);
        Assert.HasCount(2, scene.Objects);
        Assert.IsNotEmpty(scene.Segments);
        return (new(new(35, -114, 1200, "UTC"), new("VirtualSky"), rig, CapturePipelineConfig.Empty), scene);
    }

    private static CaptureProcessingContext Context(CameraModuleConfig config, ProjectedSceneV1 scene, bool replay)
    {
        var raw = new CameraFrame(Epoch, 48, 48, CameraPixelFormat.Mono8, new byte[48 * 48], new(TimeSpan.FromSeconds(1), 1, 0), 48);
        var artifacts = new FrameArtifactSet(new FrameArtifact(scene.Source.ArtifactId, FrameArtifactRole.Raw, raw, []));
        var submission = new CaptureLoopSubmission(new(Epoch, TimeSpan.FromSeconds(1), CaptureMode.Still),
            new(raw, new(TimeSpan.FromSeconds(1), 1, null, null), TimeSpan.Zero, CaptureMode.Still, false, Artifacts: artifacts),
            Epoch, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        var context = new CaptureProcessingContext(config, submission, null, null,
            replay ? new(Guid.NewGuid(), ProcessingGraphExecutionClass.Replay, "archived", new string('F', 64), false, 1, "lease") : null);
        context.BeginNode("preview", []);
        context.AddDerivative(FrameArtifactRole.Preview, raw, "preview-v1", [scene.Source.ArtifactId]);
        var bytes = ProjectedSceneJson.Serialize(scene);
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(BuiltInProcessingRecipes.ProjectedScene,
            "1", "fixture", JsonSerializer.SerializeToElement(new { })));
        context.BeginNode("scene", []);
        context.RegisterProcessingProduct(new(FrameArtifactRole.Metadata, "scene", new string('B', 64),
            StructuredProcessingProductContracts.ProjectedSceneMediaType, null, bytes, ProcessingIdentity.ComputePayloadSha256(bytes),
            recipe, [], [scene.Source.ArtifactId], TimeSpan.Zero, new("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing"))
        { Kind = ProcessingProductKind.Metadata, SchemaVersion = scene.SchemaVersion, ContentIdentitySha256 = scene.SceneIdentitySha256 });
        context.BeginNode("presentation", ["scene", "preview"]);
        return context;
    }

    private sealed class RecordingExecutor : IProcessingRecipeExecutor
    {
        public ProcessingExecutionRequest? Request { get; private set; }
        public ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure("recorded"));
        }
    }

    private sealed class UnusedSceneProvider : IAnnotationSceneProvider
    {
        public ValueTask<AnnotationSceneResult> BuildAsync(CameraModuleConfig config, ReconstructionDescriptor? descriptor,
            CameraFrame rawFrame, IReadOnlyList<string> constellationIds, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Archived execution must use its retained scene.");
    }
}
