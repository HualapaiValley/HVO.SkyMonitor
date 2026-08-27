using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Unit")]
public sealed class RealCameraAnnotationPipelineTests
{
    private static readonly DateTimeOffset Utc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] ExpectedConstellationIds = ["TST"];

    [TestMethod]
    public async Task ConfiguredPipeline_AnnotatesOnlyRealFrameDerivativeWithClippedConstellationGeometry()
    {
        var catalog = CreateCatalog();
        var topology = new InMemoryConstellationTopology([
            new ConstellationSegment("TST", "1", "2"),
            new ConstellationSegment("TST", "1", "3")
        ], new ConstellationTopologyMetadata(
            "test topology", "1", new Uri("https://example.test/topology"),
            new string('A', 64), "CC0", "fixture-v1"));
        var config = CreateConfig();
        using var provider = CreateServices(catalog, topology);
        var pipeline = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(config).Nodes
            .Select(static node => node.Step).ToArray();
        var rawBytes = new byte[checked(200 * 200 * 2)];
        var expectedRawBytes = rawBytes.ToArray();
        var raw = new CameraFrame(
            Utc, 200, 200, CameraPixelFormat.Mono16, rawBytes,
            new FrameMetadata(TimeSpan.FromSeconds(20), 150, -10, "PhysicalTest"), 400);

        var first = await RunPipelineAsync(config, pipeline, raw).ConfigureAwait(false);
        var second = await RunPipelineAsync(config, pipeline, raw).ConfigureAwait(false);

        Assert.AreSame(raw, first.Artifacts!.Raw.Frame);
        CollectionAssert.AreEqual(expectedRawBytes, first.Artifacts.Raw.Frame.PixelData.ToArray());
        Assert.IsNotEmpty(first.ProcessingOutcomes);
        Assert.AreEqual(HVO.SkyMonitor.Processing.ProcessingOutcomeStatus.Produced,
            first.ProcessingOutcomes[0].Status,
            $"{first.ProcessingOutcomes[0].ReasonCode}: {first.ProcessingOutcomes[0].Field}");
        var preview = first.Artifacts[FrameArtifactRole.Preview];
        var annotated = first.Artifacts[FrameArtifactRole.AnnotatedPreview];
        Assert.IsTrue(preview.Frame.PixelData.Span.IndexOfAnyExcept((byte)0) < 0);
        Assert.IsTrue(annotated.Frame.PixelData.Span.IndexOf((byte)200) >= 0);
        CollectionAssert.AreEqual(new[] { preview.ArtifactId }, annotated.SourceArtifactIds!.ToArray());
        Assert.AreEqual("real-constellation-test-v1", annotated.RecipeVersion);
        Assert.AreEqual("default",
            first.ProcessingProducts.Single(product => product.Role == FrameArtifactRole.AnnotatedPreview).Variant);
        Assert.AreEqual("AnnotatedPreview", annotated.Frame.Metadata.SourceId);

        var provenance = annotated.Frame.Metadata.Scene;
        Assert.IsNotNull(provenance);
        Assert.IsFalse(provenance.IncludeConstellationEndpointStars);
        Assert.IsNotNull(provenance.Objects);
        Assert.IsEmpty(provenance.Objects);
        Assert.IsNotNull(provenance.Segments);
        Assert.IsNotEmpty(provenance.Segments);
        Assert.IsTrue(provenance.Segments.All(segment =>
            segment.FromObjectId == "from" && segment.ToObjectId == "to"));
        CollectionAssert.AreEqual(ExpectedConstellationIds, provenance.ConstellationIds!.ToArray());
        Assert.AreEqual("test-catalog", provenance.CatalogName);
        Assert.AreEqual(topology.Metadata.Version, provenance.ConstellationTopologyVersion);
        Assert.AreEqual(topology.Metadata.SourceSha256, provenance.ConstellationTopologySha256);
        Assert.AreEqual(RigProjectionContextFactory.AlgorithmVersion, provenance.ProjectionAlgorithmVersion);
        Assert.AreEqual(config.Rig.Optics.CalibrationVersion, provenance.ProjectionCalibrationVersion);
        Assert.AreEqual(config.Rig.ProfileVersion, provenance.RigProfileVersion);
        Assert.AreEqual(RigProjectionContextFactory.CreateProfileHashSha256(config.Rig), provenance.RigProfileHashSha256);
        Assert.AreEqual(config.Rig.Sensor.SensorRecipeVersion, provenance.SensorRecipeVersion);

        var secondAnnotated = second.Artifacts![FrameArtifactRole.AnnotatedPreview].Frame;
        CollectionAssert.AreEqual(annotated.Frame.PixelData.ToArray(), secondAnnotated.PixelData.ToArray());
        Assert.AreEqual(provenance.SceneId, secondAnnotated.Metadata.Scene!.SceneId);
    }

    [TestMethod]
    public async Task ConfiguredPipeline_WithoutRequestedRealConstellationsLeavesAnnotationAbsent()
    {
        var catalog = CreateCatalog();
        var topology = new InMemoryConstellationTopology([]);
        var config = CreateConfig(constellationIds: []);
        using var provider = CreateServices(catalog, topology);
        var pipeline = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(config).Nodes
            .Select(static node => node.Step).ToArray();
        var raw = new CameraFrame(
            Utc, 200, 200, CameraPixelFormat.Mono16, new byte[200 * 200 * 2],
            new FrameMetadata(TimeSpan.FromSeconds(20), 150, -10), 400);

        var context = await RunPipelineAsync(config, pipeline, raw).ConfigureAwait(false);

        Assert.IsFalse(context.Artifacts!.Artifacts.ContainsKey(FrameArtifactRole.AnnotatedPreview));
        Assert.AreSame(raw, context.Artifacts.Raw.Frame);
    }

    [TestMethod]
    public async Task ConfiguredPipeline_AnnotatesBayerPreviewWithoutChangingRawPhotosites()
    {
        var catalog = CreateCatalog();
        var topology = new InMemoryConstellationTopology([
            new ConstellationSegment("TST", "1", "2")
        ]);
        var config = CreateConfig(pixelFormat: CameraPixelFormat.BayerRggb16);
        using var provider = CreateServices(catalog, topology);
        var pipeline = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(config).Nodes
            .Select(static node => node.Step).ToArray();
        var rawBytes = new byte[200 * 200 * 2];
        var expectedRawBytes = rawBytes.ToArray();
        var raw = new CameraFrame(
            Utc, 200, 200, CameraPixelFormat.BayerRggb16, rawBytes,
            new FrameMetadata(TimeSpan.FromSeconds(20), 150, -10), 400);

        var context = await RunPipelineAsync(config, pipeline, raw).ConfigureAwait(false);

        CollectionAssert.AreEqual(expectedRawBytes, context.Artifacts!.Raw.Frame.PixelData.ToArray());
        var preview = context.Artifacts[FrameArtifactRole.Preview].Frame;
        var annotated = context.Artifacts[FrameArtifactRole.AnnotatedPreview].Frame;
        Assert.AreEqual(CameraPixelFormat.Rgb24, preview.PixelFormat);
        Assert.AreEqual(CameraPixelFormat.Rgb24, annotated.PixelFormat);
        Assert.IsTrue(preview.PixelData.Span.IndexOfAnyExcept((byte)0) < 0);
        Assert.IsTrue(annotated.PixelData.Span.IndexOfAnyExcept((byte)0) >= 0);
        Assert.IsEmpty(annotated.Metadata.Scene!.Objects!);
        Assert.IsFalse(annotated.Metadata.Scene.IncludeConstellationEndpointStars);
    }

    [TestMethod]
    public async Task AnnotationSceneProvider_LegacyDescriptorDoesNotInferCurrentLocation()
    {
        var catalog = CreateCatalog();
        var topology = new InMemoryConstellationTopology([]);
        var provider = new AnnotationSceneProvider(() => catalog, topology, () => null);
        var config = CreateConfig();
        var raw = new CameraFrame(
            Utc, 200, 200, CameraPixelFormat.Mono16, new byte[200 * 200 * 2],
            new FrameMetadata(TimeSpan.FromSeconds(20), 150, -10), 400);
        var legacyDescriptor = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 200, 200, 400, raw.PixelData.ToArray()).Descriptor;

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await provider.BuildAsync(
                config, legacyDescriptor, raw, ExpectedConstellationIds, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Message, "not inferred", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AnnotationSceneProvider_UsesDescriptorLocationInsteadOfCurrentConfiguration()
    {
        var catalog = CreateCatalog();
        var topology = new InMemoryConstellationTopology([]);
        var captureLocation = DeploymentLocationSnapshot.Create(
            "capture-location", 1, "test", null, DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC");
        var currentLocation = DeploymentLocationSnapshot.Create(
            "current-location", 1, "test", null, DateTimeOffset.UnixEpoch, null, 45, 90, 100, "UTC");
        var locationStore = new StaticLocationStore(captureLocation);
        var provider = new AnnotationSceneProvider(() => catalog, topology, () => locationStore);
        var config = CreateConfig() with { DeploymentLocation = currentLocation };
        var raw = new CameraFrame(
            Utc, 200, 200, CameraPixelFormat.Mono16, new byte[200 * 200 * 2],
            new FrameMetadata(TimeSpan.FromSeconds(20), 150, -10), 400);
        var descriptor = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 200, 200, 400, raw.PixelData.ToArray()).Descriptor with
        {
            Location = captureLocation.ToProvenance()
        };

        var scene = await provider.BuildAsync(
            config, descriptor, raw, ExpectedConstellationIds, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(0, scene.Scene.Request.Observer.LatitudeDegrees, 1e-12);
        Assert.AreEqual(0, scene.Scene.Request.Observer.LongitudeDegrees, 1e-12);
        Assert.AreEqual(captureLocation.ToProvenance(), locationStore.Resolved);
    }

    private static ServiceProvider CreateServices(
        ICelestialCatalog catalog,
        IConstellationTopology topology)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(catalog);
        services.AddSingleton<ICelestialCatalog>(catalog);
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().Build());
        services.AddSingleton(topology);
        services.AddSingleton<IConstellationTopology>(topology);
        return services.BuildServiceProvider();
    }

    private static async Task<CaptureProcessingContext> RunPipelineAsync(
        CameraModuleConfig config,
        IReadOnlyList<ICaptureProcessingStep> pipeline,
        CameraFrame raw)
    {
        var request = new CaptureRequest(Utc, TimeSpan.FromSeconds(20), CaptureMode.Still);
        var result = new CaptureResult(
            raw, new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null),
            TimeSpan.Zero, CaptureMode.Still, false);
        var context = new CaptureProcessingContext(
            config, new CaptureLoopSubmission(request, result, Utc, request.TargetInterval, TimeSpan.Zero));
        foreach (var step in pipeline)
        {
            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        }
        return context;
    }

    private static CameraModuleConfig CreateConfig(
        IReadOnlyList<string>? constellationIds = null,
        CameraPixelFormat pixelFormat = CameraPixelFormat.Mono16)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("PhysicalTest"),
            new CameraRigConfig(
                new SensorProfile(
                    "Physical Test", 200, 200, 5,
                    pixelFormat == CameraPixelFormat.BayerRggb16 ? SensorColorMode.Color : SensorColorMode.Mono,
                    pixelFormat,
                    pixelFormat == CameraPixelFormat.BayerRggb16
                        ? SensorResponseMode.BayerRaw
                        : SensorResponseMode.Monochrome,
                    400, SensorRecipeVersion: "physical-test-v1"),
                new OpticsProfile(
                    "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    PrincipalPointX: 100, PrincipalPointY: 100, ImageCircleRadiusPixels: 50,
                    FocalLengthXPixels: 100, FocalLengthYPixels: 100,
                    CalibrationVersion: "physical-calibration-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), 0, 150),
                ProfileVersion: "physical-test-rig-v1"),
            Pipeline: new CapturePipelineConfig(
            [
                new CaptureProcessingStepConfig(
                    "Preview", "Preview", 50,
                    JsonSerializer.SerializeToElement(new { }),
                    ["$raw"]),
                new CaptureProcessingStepConfig(
                    "Annotation", "Annotation", 75,
                    JsonSerializer.SerializeToElement(new
                    {
                        drawLabels = false,
                        drawImageCircle = false,
                        drawCardinalDirections = false,
                        drawConstellationLines = true,
                        constellationIds = constellationIds ?? ExpectedConstellationIds,
                        constellationLineValue = 200,
                        constellationLineThickness = 1,
                        constellationLineOpacity = 1,
                        recipeVersion = "real-constellation-test-v1"
                    }),
                    ["Preview"])
            ]))
        {
            DeploymentLocation = DeploymentLocationSnapshot.Create(
                "physical-test-location", 1, "synthetic test", null,
                DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC")
        };

    private static TestCatalog CreateCatalog()
    {
        CelestialCatalogObject Create(string id, string hip, AltAzPoint horizontal)
        {
            var ofDate = CoordinateTransforms.HorizontalToEquatorial(horizontal, Utc, 0, 0);
            var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, Utc);
            return new CelestialCatalogObject(
                id, id, j2000.RightAscensionHours, j2000.DeclinationDegrees, 8, HipparcosId: hip);
        }

        return new TestCatalog([
            Create("from", "1", new AltAzPoint(60, 90)),
            Create("to", "2", new AltAzPoint(60, 270))
        ]);
    }

    private sealed class TestCatalog : ICelestialCatalog, IHipparcosCatalog, ICelestialCatalogMetadataSource
    {
        private readonly InMemoryCelestialCatalog _inner;

        public TestCatalog(IEnumerable<CelestialCatalogObject> objects)
        {
            _inner = new InMemoryCelestialCatalog(objects);
        }

        public CatalogMetadata Metadata { get; } = new(
            "test-catalog", "1", new Uri("https://example.test/catalog"), new string('B', 64), "CC0", "1");

        public string PreprocessingVersion => "fixture-v1";

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _inner.Query(query);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default)
            => _inner.QueryCandidatesAsync(query, cancellationToken);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> GetByHipparcosIdsAsync(
            IReadOnlyCollection<string> hipparcosIds,
            CancellationToken cancellationToken = default)
            => _inner.GetByHipparcosIdsAsync(hipparcosIds, cancellationToken);
    }

    private sealed class StaticLocationStore(DeploymentLocationSnapshot snapshot) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => snapshot;

        public CaptureLocationProvenance? Resolved { get; private set; }

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken) => ValueTask.FromResult(snapshot);

        public DeploymentLocationSnapshot Resolve(
            CaptureLocationProvenance provenance,
            DateTimeOffset? effectiveUtc = null)
        {
            Resolved = provenance;
            return snapshot;
        }
    }
}
