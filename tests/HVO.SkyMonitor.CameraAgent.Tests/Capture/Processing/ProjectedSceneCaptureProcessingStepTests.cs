using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ProjectedSceneCaptureProcessingStepTests
{
    [TestMethod]
    public async Task VirtualStageProducesAuthoritativeSourceBoundMetadataWithoutRawRead()
    {
        var root = CreateRoot();
        try
        {
            using var staging = CreateStaging(root);
            var scene = await CreateSceneAsync().ConfigureAwait(false);
            var stageKey = new string('1', 64);
            var sceneId = new string('A', 64);
            await staging.StageAsync(stageKey, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            var fixture = CreateContext(root, new SceneProvenance(
                sceneId, "rig-v1", "test", "1", new string('0', 64), "EquidistantFisheye",
                "projection-v1", "astronomy-v1", "sensor-v1",
                ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                ProjectedSceneStageKey: stageKey));
            File.Delete(fixture.PayloadPath);
            var step = CreateStep(staging);

            await step.ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(ProcessingOutcomeStatus.Produced, fixture.Context.ProcessingOutcomes.Single().Status,
                $"{fixture.Context.ProcessingOutcomes.Single().ReasonCode}: {fixture.Context.ProcessingOutcomes.Single().Field}");
            var product = fixture.Context.ProcessingProducts.Single();
            var parsed = ProjectedSceneJson.Parse(product.Payload);
            Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
            Assert.AreEqual(ProjectedSceneKind.VirtualRenderAuthoritative, parsed.Scene!.Kind);
            Assert.AreEqual(fixture.Descriptor.Capture.CaptureId, parsed.Scene.Source.CaptureId);
            Assert.AreEqual(fixture.Descriptor.Artifact.ArtifactId, parsed.Scene.Source.ArtifactId);
            Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(fixture.Descriptor),
                parsed.Scene.Source.ArtifactIdentitySha256);
            CollectionAssert.AreEqual(
                scene.Objects.Select(static item => item.Pixel).ToArray(),
                parsed.Scene.Objects.Select(static item => item.Pixel).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LegacyVirtualEvidenceSkipsUnavailableWithoutReadingSemanticSceneIdAsStageKey()
    {
        var root = CreateRoot();
        try
        {
            using var staging = CreateStaging(root);
            var sceneId = new string('B', 64);
            var fixture = CreateContext(root, new SceneProvenance(
                sceneId, "rig-v1", "test", "1", new string('0', 64), "EquidistantFisheye",
                "projection-v1", "astronomy-v1", "sensor-v1"));
            var step = CreateStep(staging);

            await step.ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(ProcessingOutcomeStatus.Skipped, fixture.Context.ProcessingOutcomes.Single().Status);
            Assert.HasCount(0, fixture.Context.ProcessingProducts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PostCommitDeletesOnlyCaptureOwnedStageAndIsIdempotentAfterRestore()
    {
        var root = CreateRoot();
        try
        {
            using var staging = CreateStaging(root);
            var scene = await CreateSceneAsync().ConfigureAwait(false);
            var firstKey = new string('3', 64);
            var secondKey = new string('4', 64);
            var sceneId = new string('C', 64);
            await staging.StageAsync(firstKey, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            await staging.StageAsync(secondKey, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            var fixture = CreateContext(root, new SceneProvenance(
                sceneId, "rig-v1", "test", "1", new string('0', 64), "EquidistantFisheye",
                "projection-v1", "astronomy-v1", "sensor-v1",
                ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                ProjectedSceneStageKey: firstKey));
            var step = CreateStep(staging);
            var descriptorContext = new CaptureDescriptorProcessingContext(fixture.Context);

            await step.OnCommittedAsync(descriptorContext, CancellationToken.None).ConfigureAwait(false);
            await step.OnCommittedAsync(descriptorContext, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(await staging.ReadAsync(firstKey, CancellationToken.None).ConfigureAwait(false));
            Assert.IsNotNull(await staging.ReadAsync(secondKey, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CompletedDeleteFault_LeavesMarkerThatReconciliationDeletesDespiteRawOwnership()
    {
        var root = CreateRoot();
        try
        {
            using var staging = CreateStaging(root);
            var stageKey = new string('5', 64);
            var sceneId = new string('D', 64);
            var scene = await CreateSceneAsync().ConfigureAwait(false);
            await staging.StageAsync(stageKey, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            var fixture = CreateContext(root, new SceneProvenance(
                sceneId, "rig-v1", "test", "1", new string('0', 64), "EquidistantFisheye",
                "projection-v1", "astronomy-v1", "sensor-v1",
                ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                ProjectedSceneStageKey: stageKey));
            var step = CreateStep(staging, stagingStore: new DeleteCompletedFailingStore(staging));

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await step.OnCommittedAsync(
                    new CaptureDescriptorProcessingContext(fixture.Context), CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);

            var completed = Path.Combine(root, "staging", "projected-scenes", $"{stageKey}.completed.json");
            Assert.IsTrue(File.Exists(completed));
            await staging.ReconcileAsync(new HashSet<string>([stageKey], StringComparer.Ordinal), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsFalse(File.Exists(completed));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PhysicalPredictionUsesDescriptorMidpointCaptureRigAndProtectedLocationWithoutRawRead()
    {
        var root = CreateRoot();
        try
        {
            using var staging = CreateStaging(root);
            var location = DeploymentLocationSnapshot.Create(
                "capture-location", 7, "test", 1, DateTimeOffset.UnixEpoch.AddDays(-1), null,
                35, -115, 1200, "UTC");
            var config = CreateConfig() with
            {
                DeploymentLocationRedacted = true,
                Observatory = new ObservatoryLocation(0, 0, 0, "UTC")
            };
            var fixture = CreatePhysicalContext(root, config, location);
            File.Delete(fixture.PayloadPath);
            var catalog = new MetadataCatalog([
                new CelestialCatalogObject("star", "Star", 0, 0, 1)
            ]);
            var services = new ServiceCollection()
                .AddSingleton<IDeploymentLocationStore>(new FixedLocationStore(location))
                .BuildServiceProvider();
            var step = CreateStep(staging, services, catalog);

            await step.ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);

            var outcome = fixture.Context.ProcessingOutcomes.Single();
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, $"{outcome.ReasonCode}: {outcome.Field}");
            var parsed = ProjectedSceneJson.Parse(outcome.Products.Single().Payload);
            Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
            Assert.AreEqual(ProjectedSceneKind.Predicted, parsed.Scene!.Kind);
            Assert.AreEqual(
                fixture.Descriptor.Timing.ExposureStartedUtc +
                    TimeSpan.FromTicks(fixture.Descriptor.Controls.EffectiveExposure.Ticks / 2),
                parsed.Scene.EffectiveUtc);
            Assert.AreEqual(location.LatitudeDegrees, parsed.Scene.Observer.LatitudeDegrees);
            Assert.AreEqual(location.LongitudeDegrees, parsed.Scene.Observer.LongitudeDegrees);
            Assert.AreEqual(config.Rig.Optics.CalibrationVersion, parsed.Scene.Projection.CalibrationVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectedSceneCaptureProcessingStep CreateStep(
        ProjectedSceneStagingStore staging,
        IServiceProvider? services = null,
        ICelestialCatalog? catalog = null,
        IProjectedSceneStagingStore? stagingStore = null)
    {
        return new ProjectedSceneCaptureProcessingStep(
            new CaptureProcessingStepMetadata("projected-scene", "ProjectedScene", 0),
            new ProjectedSceneCaptureProcessingStepOptions(), stagingStore ?? staging, staging,
            new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()),
            services ?? new ServiceCollection().BuildServiceProvider(), catalog);
    }

    private static (CaptureProcessingContext Context, ReconstructionDescriptor Descriptor, string PayloadPath) CreateContext(
        string root,
        SceneProvenance provenance)
    {
        var payload = new byte[8];
        var original = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var descriptor = original.Descriptor with
        {
            Artifact = original.Descriptor.Artifact with { SourceId = "VirtualSky" }
        };
        var manifest = original with { Descriptor = descriptor, Scene = provenance };
        var payloadPath = Path.Combine(root, "raw.bin");
        File.WriteAllBytes(payloadPath, payload);
        var receipt = new RawCaptureReceipt(
            RawIngressOutcome.Committed, manifest,
            new StoredFrameReference("raw.bin", payloadPath, descriptor.Timing.ExposureStartedUtc, FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifest));
        var request = new CaptureRequest(descriptor.Timing.RequestedStartUtc, TimeSpan.FromSeconds(1), CaptureMode.Still);
        var submission = new CaptureLoopSubmission(
            request,
            new CaptureResult(null, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null),
                TimeSpan.Zero, CaptureMode.Still, false),
            descriptor.Timing.RequestedStartUtc, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        return (new CaptureProcessingContext(CreateConfig(), submission, receipt), descriptor, payloadPath);
    }

    private static CameraModuleConfig CreateConfig() => new(
        new ObservatoryLocation(0, 0, 0, "UTC"), new CameraModuleDescriptor("VirtualSky"),
        new CameraRigConfig(
            new SensorProfile("test", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
            new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                PrincipalPointX: 1, PrincipalPointY: 1, ImageCircleRadiusPixels: 1,
                CalibrationVersion: "projection-v1"),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1), 1, 1)));

    private static (CaptureProcessingContext Context, ReconstructionDescriptor Descriptor, string PayloadPath)
        CreatePhysicalContext(string root, CameraModuleConfig config, DeploymentLocationSnapshot location)
    {
        var payload = new byte[8];
        var original = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var descriptor = original.Descriptor with
        {
            Controls = original.Descriptor.Controls with { EffectiveExposure = TimeSpan.FromSeconds(8) },
            Profiles = original.Descriptor.Profiles with
            {
                Rig = original.Descriptor.Profiles.Rig with { Sha256 = CameraRigProfileIdentity.ComputeSha256(config.Rig) }
            },
            Artifact = original.Descriptor.Artifact with { SourceId = "PhysicalCamera" },
            Location = location.ToProvenance()
        };
        var manifest = original with { Descriptor = descriptor, Scene = null };
        var payloadPath = Path.Combine(root, "physical-raw.bin");
        File.WriteAllBytes(payloadPath, payload);
        var receipt = new RawCaptureReceipt(
            RawIngressOutcome.Committed, manifest,
            new StoredFrameReference("physical-raw.bin", payloadPath, descriptor.Timing.ExposureStartedUtc,
                FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifest));
        var request = new CaptureRequest(descriptor.Timing.RequestedStartUtc, TimeSpan.FromSeconds(1), CaptureMode.Still);
        var submission = new CaptureLoopSubmission(
            request, new CaptureResult(null, new CaptureSetpoint(TimeSpan.FromSeconds(8), 1, null, null),
                TimeSpan.Zero, CaptureMode.Still, false), descriptor.Timing.RequestedStartUtc,
            TimeSpan.FromSeconds(1), TimeSpan.Zero);
        return (new CaptureProcessingContext(config, submission, receipt), descriptor, payloadPath);
    }

    private static ValueTask<VisibleScene> CreateSceneAsync() =>
        new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("star", "Star", 0, 0, 1)
        ])).BuildAsync(new VisibleSceneRequest(
            DateTimeOffset.UnixEpoch, new ObserverLocation(0, 0, 0),
            new EquidistantProjectionContext(1, 1, 1, 1, WidthPixels: 2, HeightPixels: 2),
            new CatalogQuery(6.5, 10),
            new CatalogMetadata("test", "1", new Uri("https://example.invalid"), new string('0', 64), "test", "1"),
            projectionVersion: "projection-v1", algorithmVersion: "astronomy-v1"));

    private static ProjectedSceneStagingStore CreateStaging(string root) => new(
        Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class MetadataCatalog(IEnumerable<CelestialCatalogObject> objects) :
        ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        private readonly InMemoryCelestialCatalog _inner = new(objects);

        public CatalogMetadata Metadata { get; } = new(
            "test", "1", new Uri("https://example.invalid"), new string('0', 64), "test", "1");

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _inner.Query(query);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default) => _inner.QueryCandidatesAsync(query, cancellationToken);
    }

    private sealed class FixedLocationStore(DeploymentLocationSnapshot location) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => location;

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken) => ValueTask.FromResult(location);

        public DeploymentLocationSnapshot Resolve(
            CaptureLocationProvenance provenance,
            DateTimeOffset? effectiveUtc = null) => location;
    }

    private sealed class DeleteCompletedFailingStore(ProjectedSceneStagingStore inner) : IProjectedSceneStagingStore
    {
        public ValueTask StageAsync(string stageKey, string sceneId, VisibleScene scene,
            CancellationToken cancellationToken) => inner.StageAsync(stageKey, sceneId, scene, cancellationToken);

        public ValueTask DeleteAsync(string stageKey, CancellationToken cancellationToken)
            => inner.DeleteAsync(stageKey, cancellationToken);

        public ValueTask MarkCompletedAsync(string stageKey, CancellationToken cancellationToken)
            => inner.MarkCompletedAsync(stageKey, cancellationToken);

        public ValueTask DeleteCompletedAsync(string stageKey, CancellationToken cancellationToken)
            => ValueTask.FromException(new IOException("delete-completed-failure"));
    }
}
