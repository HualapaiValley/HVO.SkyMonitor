using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Replay;
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
    private static readonly byte[] ReplayAuthenticationKey = Encoding.UTF8.GetBytes(
        "projected-scene-replay-key-00001");

    [TestMethod]
    public async Task PhysicalCaptureStagesOnceBeforeRawIdentityAndPreservesPayload()
    {
        var root = CreateRoot();
        try
        {
            using var staging = CreateStaging(root);
            var location = DeploymentLocationSnapshot.Create(
                "capture-location", 1, "test", null, DateTimeOffset.UnixEpoch, null, 35, -115, 1200, "UTC");
            var config = CreateConfig() with
            {
                Module = new CameraModuleDescriptor("PhysicalCamera"),
                DeploymentLocation = location,
                Pipeline = new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig("ProjectedScene", Options: JsonSerializer.SerializeToElement(
                        new ProjectedSceneCaptureProcessingStepOptions()), DependsOn: ["$raw"])
                ])
            };
            var catalog = new MetadataCatalog([new CelestialCatalogObject("star", "Star", 0, 0, 1)]);
            var stager = new CaptureProjectedSceneStager(staging, catalog);
            var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var layout = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, payload).Descriptor.Layout;
            var startedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var frame = new CameraFrame(
                startedUtc, 2, 2, CameraPixelFormat.Mono16, payload,
                new FrameMetadata(TimeSpan.FromSeconds(2), 1, 0, "PhysicalCamera"), 4)
            {
                Layout = layout
            };
            var result = new CaptureResult(
                frame, new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null),
                TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.AddSeconds(4), startedUtc.AddSeconds(4.1))
            };
            var submission = new CaptureLoopSubmission(
                new CaptureRequest(startedUtc, TimeSpan.FromSeconds(5), CaptureMode.Still),
                result, startedUtc, TimeSpan.FromSeconds(5), TimeSpan.Zero);

            var staged = await stager.StageAsync(config, submission, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, catalog.QueryCount);
            CollectionAssert.AreEqual(payload, staged.Result.Frame!.PixelData.ToArray());
            var provenance = staged.Result.Frame.Metadata.Scene;
            Assert.IsNotNull(provenance);
            Assert.AreEqual(StagedProjectedSceneDocument.CurrentSchemaVersion, provenance.ProjectedSceneStageSchemaVersion);
            Assert.HasCount(64, provenance.ProjectedSceneStageKey!);
            Assert.AreEqual(provenance.ProjectedSceneStageKey, provenance.SceneId);
            Assert.AreNotEqual(provenance.SceneId, provenance.ProjectedSceneStageIdentitySha256);
            var document = await staging.ReadAsync(provenance.ProjectedSceneStageKey!, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(document);
            Assert.AreEqual(startedUtc.AddSeconds(2), document.EffectiveUtc);
            Assert.AreEqual(CameraRigProfileIdentity.ComputeSha256(config.Rig), document.RigProfileSha256);
            Assert.AreEqual(layout, document.Layout);
            Assert.AreEqual(ProjectedSceneKind.Predicted, document.IntendedKind);
            Assert.AreEqual(provenance.ProjectedSceneStageIdentitySha256, document.StageSceneIdentitySha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PhysicalCaptureWithReversedAcquisitionTimingDoesNotStage()
    {
        var root = CreateRoot();
        try
        {
            using var staging = CreateStaging(root);
            var location = DeploymentLocationSnapshot.Create(
                "capture-location", 1, "test", null, DateTimeOffset.UnixEpoch, null, 35, -115, 1200, "UTC");
            var config = CreateConfig() with
            {
                Module = new CameraModuleDescriptor("PhysicalCamera"),
                DeploymentLocation = location,
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("ProjectedScene", DependsOn: ["$raw"])])
            };
            var catalog = new MetadataCatalog([new CelestialCatalogObject("star", "Star", 0, 0, 1)]);
            var stager = new CaptureProjectedSceneStager(staging, catalog);
            var startedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var layout = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor.Layout;
            var frame = new CameraFrame(
                startedUtc, 2, 2, CameraPixelFormat.Mono16, new byte[8],
                new FrameMetadata(TimeSpan.FromSeconds(2), 1, 0, "PhysicalCamera"), 4)
            {
                Layout = layout
            };
            var result = new CaptureResult(
                frame, new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null),
                TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.AddSeconds(-1), startedUtc.AddSeconds(1))
            };
            var submission = new CaptureLoopSubmission(
                new CaptureRequest(startedUtc, TimeSpan.FromSeconds(5), CaptureMode.Still),
                result, startedUtc, TimeSpan.FromSeconds(5), TimeSpan.Zero);

            var unstaged = await stager.StageAsync(config, submission, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(unstaged.Result.Frame!.Metadata.Scene);
            Assert.AreEqual(0, catalog.QueryCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
    public async Task DescriptorOnlyProjectedSceneExecutesThroughLocalRunner()
    {
        var root = CreateRoot();
        var socketPath = FileSystemTestPaths.CreateShortUnixSocketPath();
        try
        {
            using var staging = CreateStaging(root);
            var scene = await CreateSceneAsync().ConfigureAwait(false);
            var stageKey = new string('1', 64);
            var sceneId = new string('A', 64);
            await staging.StageAsync(stageKey, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            var execution = new ProcessingExecutionContext(
                Guid.NewGuid(),
                ProcessingGraphExecutionClass.Replay,
                "basic@1",
                new string('B', 64),
                false,
                1,
                "projected-scene-lease",
                "test-owner",
                DateTimeOffset.UtcNow.AddMinutes(5),
                1);
            var fixture = CreateContext(root, new SceneProvenance(
                sceneId, "rig-v1", "test", "1", new string('0', 64), "EquidistantFisheye",
                "projection-v1", "astronomy-v1", "sensor-v1",
                ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                ProjectedSceneStageKey: stageKey), execution);
            File.Delete(fixture.PayloadPath);
            var runnerOptions = new LocalReplayRunnerOptions
            {
                SocketPath = socketPath,
                PreSharedAuthKey = ReplayAuthenticationKey,
                HeartbeatInterval = TimeSpan.FromMilliseconds(100),
                HeartbeatTimeout = TimeSpan.FromSeconds(2)
            };
            using var stopping = new CancellationTokenSource();
            var server = new LocalReplayRunnerServer(runnerOptions);
            await using var serverLifetime = server.ConfigureAwait(false);
            var serverTask = server.RunAsync(stopping.Token);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (!Path.Exists(socketPath))
                {
                    await Task.Delay(10, timeout.Token).ConfigureAwait(false);
                }
            }
            var client = new LocalReplayRunnerClient(runnerOptions);
            await using var clientLifetime = client.ConfigureAwait(false);
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                ProcessingGraphs = new ProcessingGraphExecutionOptions
                {
                    ReplayProfile = ReplayExecutionProfile.LocalRunner,
                    LocalRunner = new LocalReplayRunnerHostOptions
                    {
                        AuthorizationKey = Encoding.UTF8.GetString(ReplayAuthenticationKey)
                    }
                }
            });
            var adapter = new CameraAgentRecipeExecutionAdapter(
                new ProcessingRecipeExecutor(),
                hostOptions,
                client);
            fixture.Context.BeginNode("projected-scene", [], ["$raw"]);
            // Replay consumes the pinned committed product; the still-present stage must not be consulted (#718).
            fixture.Context.SetFrozenAuxiliaryInputs([CreateFrozenSceneArtifact(fixture.Descriptor, scene).Artifact]);
            var reader = new CountingStagingReader(staging);
            var step = CreateStep(staging, adapter: adapter, stagingReader: reader);

            await step.ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(ProcessingOutcomeStatus.Produced, fixture.Context.ProcessingOutcomes.Single().Status);
            Assert.AreEqual(0, reader.ReadCount);
            Assert.IsNotNull(await staging.ReadAsync(stageKey, CancellationToken.None).ConfigureAwait(false));
            await stopping.CancelAsync().ConfigureAwait(false);
            await serverTask.ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
            var catalog = new MetadataCatalog([
                new CelestialCatalogObject("star", "Star", 0, 0, 1)
            ]);
            var effectiveUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 6, TimeSpan.Zero) + TimeSpan.FromSeconds(4);
            var scene = await new VisibleSceneBuilder(catalog).BuildAsync(new VisibleSceneRequest(
                effectiveUtc,
                new ObserverLocation(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters),
                RigProjectionContextFactory.Create(config.Rig),
                new CatalogQuery(6.5, 2000),
                catalog.Metadata,
                projectionVersion: config.Rig.Optics.CalibrationVersion,
                algorithmVersion: "visible-scene-iau1976-constellation-v2")).ConfigureAwait(false);
            var stageKey = new string('6', 64);
            var sceneId = new string('E', 64);
            await staging.StageAsync(stageKey, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            var changedConfig = config with
            {
                Observatory = new ObservatoryLocation(-45, 90, 0, "UTC"),
                Pipeline = CapturePipelineConfig.Empty
            };
            var fixture = CreatePhysicalContext(root, config, location, new SceneProvenance(
                sceneId, config.Rig.ProfileVersion, catalog.Metadata.Name, catalog.Metadata.Version,
                catalog.Metadata.Checksum, config.Rig.Optics.ProjectionModel,
                RigProjectionContextFactory.AlgorithmVersion, scene.Request.AlgorithmVersion,
                config.Rig.Sensor.SensorRecipeVersion,
                ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                ProjectedSceneStageKey: stageKey), changedConfig);
            File.Delete(fixture.PayloadPath);
            var step = CreateStep(staging);

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
            Assert.AreNotEqual(fixture.Context.RawCapture!.Manifest.Scene!.SceneId, parsed.Scene.SceneIdentitySha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PhysicalStageProjectedProductAndAnnotationCalculateSceneExactlyOnceWithExactLineage()
    {
        var root = CreateRoot();
        try
        {
            using var staging = CreateStaging(root);
            var location = DeploymentLocationSnapshot.Create(
                "capture-location", 1, "test", null, DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC");
            var config = CreateConfig() with
            {
                Module = new CameraModuleDescriptor("PhysicalCamera"),
                DeploymentLocation = location,
                Pipeline = new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig("ProjectedScene", Options: JsonSerializer.SerializeToElement(
                        new ProjectedSceneCaptureProcessingStepOptions()), DependsOn: ["$raw"])
                ])
            };
            var startedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var midpoint = startedUtc.AddSeconds(1);
            var rightAscensionHours = AstronomyTime.LocalMeanSiderealDegrees(midpoint, 0) / 15;
            var catalog = new MetadataCatalog([
                new CelestialCatalogObject("zenith", "Zenith", rightAscensionHours, 0, 0)
            ]);
            var layout = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor.Layout;
            var captureFrame = new CameraFrame(
                startedUtc, 2, 2, CameraPixelFormat.Mono16, new byte[8],
                new FrameMetadata(TimeSpan.FromSeconds(2), 1, 0, "PhysicalCamera"), 4)
            {
                Layout = layout
            };
            var captureResult = new CaptureResult(
                captureFrame, new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null),
                TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.AddSeconds(2), startedUtc.AddSeconds(2.1))
            };
            var stagedSubmission = await new CaptureProjectedSceneStager(staging, catalog).StageAsync(
                config,
                new CaptureLoopSubmission(
                    new CaptureRequest(startedUtc, TimeSpan.FromSeconds(5), CaptureMode.Still),
                    captureResult, startedUtc, TimeSpan.FromSeconds(5), TimeSpan.Zero),
                CancellationToken.None).ConfigureAwait(false);
            var fixture = CreatePhysicalContext(
                root, config, location, stagedSubmission.Result.Frame!.Metadata.Scene!);
            File.Delete(fixture.PayloadPath);
            fixture.Context.BeginNode("projected-scene", ["$raw"]);
            var projectedStep = CreateStep(staging);

            await projectedStep.ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);

            var sceneProduct = fixture.Context.ProcessingOutcomes.Single().Products.Single();
            fixture.Context.RegisterProcessingProduct(sceneProduct);
            fixture.Context.BeginNode("preview", ["$raw"]);
            var previewFrame = new CameraFrame(
                startedUtc, 2, 2, CameraPixelFormat.Mono8, new byte[4],
                new FrameMetadata(TimeSpan.FromSeconds(2), 1, 0), 2);
            var previewSources = new[] { fixture.Descriptor.Artifact.ArtifactId };
            var previewRecipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                "encoded-preview", "1.0.0", "encoded-preview-v1", JsonSerializer.SerializeToElement(new { })));
            var previewIdentity = ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.Preview, "preview-v1", previewRecipe.IdentitySha256, previewSources);
            var previewArtifact = fixture.Context.AddDerivative(
                FrameArtifactRole.Preview, previewFrame, "preview-v1",
                previewSources, CaptureProcessingContext.CreateArtifactId(previewIdentity));
            fixture.Context.AssociateProcessingProduct(previewArtifact, new ProcessingProduct(
                FrameArtifactRole.Preview,
                "preview-v1",
                previewIdentity,
                "application/x-hvo-packed-frame",
                new FrameLayoutDescriptor(
                    2, 2, 2, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable, 8, 8,
                    FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, byte.MaxValue, 4),
                previewFrame.PixelData,
                PayloadChecksum.ComputeSha256(previewFrame.PixelData.Span),
                previewRecipe,
                [new("encoded-preview", "encoded-preview-v1")],
                previewSources,
                previewFrame.Metadata.Exposure,
                sceneProduct.Compatibility));
            fixture.Context.BeginNode("annotation", ["preview", "projected-scene"]);
            var annotation = new AnnotationCaptureProcessingStep(
                new CaptureProcessingStepMetadata("annotation", "Annotation", 70),
                new AnnotationProcessingStepOptions
                {
                    RequireProjectedSceneDependency = true,
                    DrawLabels = false,
                    DrawConstellationLines = false,
                    MarkRadius = 0
                },
                new ProjectedSceneStore(), new FailingAnnotationSceneProvider(),
                new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()));

            await annotation.ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, catalog.QueryCount);
            var annotationProduct = fixture.Context.ProcessingProducts.Single(
                product => product.Role == FrameArtifactRole.AnnotatedPreview);
            CollectionAssert.AreEqual(
                new[]
                {
                    previewArtifact.ArtifactId,
                    CaptureProcessingContext.CreateArtifactId(sceneProduct.OutputIdentitySha256)
                },
                annotationProduct.SourceArtifactIds.ToArray());
            Assert.IsTrue(annotationProduct.Payload.Span.Contains((byte)144));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReplayConsumesFrozenProjectedSceneWithoutReadingStaging()
    {
        var root = CreateRoot();
        try
        {
            using var staging = CreateStaging(root);
            var reader = new CountingStagingReader(staging);
            var fixture = CreateContext(root, CreateStageProvenance(new string('5', 64), new string('E', 64)), CreateReplayExecution());
            var frozen = CreateFrozenSceneArtifact(fixture.Descriptor, await CreateSceneAsync().ConfigureAwait(false));
            fixture.Context.BeginNode("projected-scene", [], ["$raw"]);
            fixture.Context.SetFrozenAuxiliaryInputs([frozen.Artifact]);
            var step = CreateStep(staging, stagingReader: reader);

            await step.ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);

            var outcome = fixture.Context.ProcessingOutcomes.Single();
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
            Assert.AreEqual(0, reader.ReadCount, "replay must never consult the transient stage");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReplayWithoutUsableFrozenProjectedSceneFailsTerminallyWithoutReadingStaging()
    {
        var root = CreateRoot();
        try
        {
            using var staging = CreateStaging(root);
            var reader = new CountingStagingReader(staging);
            var stageKey = new string('6', 64);
            var sceneId = new string('F', 64);
            // Even a live-looking stage is ignored on replay.
            await staging.StageAsync(stageKey, sceneId, await CreateSceneAsync().ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
            var step = CreateStep(staging, stagingReader: reader);

            var none = CreateContext(root, CreateStageProvenance(stageKey, sceneId), CreateReplayExecution());
            none.Context.BeginNode("projected-scene", [], ["$raw"]);
            await step.ProcessAsync(none.Context, CancellationToken.None).ConfigureAwait(false);
            var missing = none.Context.ProcessingOutcomes.Single();
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, missing.Status);
            Assert.AreEqual(ProcessingReasonCodes.MissingProjectedScene, missing.ReasonCode);

            var altered = CreateContext(root, CreateStageProvenance(stageKey, sceneId), CreateReplayExecution());
            altered.Context.BeginNode("projected-scene", [], ["$raw"]);
            altered.Context.SetFrozenAuxiliaryInputFailure(FrozenAuxiliaryInputFailure.Altered);
            await step.ProcessAsync(altered.Context, CancellationToken.None).ConfigureAwait(false);
            var invalid = altered.Context.ProcessingOutcomes.Single();
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, invalid.Status);
            Assert.AreEqual(ProcessingReasonCodes.InvalidProjectedScene, invalid.ReasonCode);

            var foreign = CreateContext(root, CreateStageProvenance(stageKey, sceneId), CreateReplayExecution());
            var otherDescriptor = foreign.Descriptor with
            {
                Capture = foreign.Descriptor.Capture with { CaptureId = Guid.NewGuid() }
            };
            foreign.Context.BeginNode("projected-scene", [], ["$raw"]);
            foreign.Context.SetFrozenAuxiliaryInputs(
                [CreateFrozenSceneArtifact(otherDescriptor, await CreateSceneAsync().ConfigureAwait(false)).Artifact]);
            await step.ProcessAsync(foreign.Context, CancellationToken.None).ConfigureAwait(false);
            var mismatch = foreign.Context.ProcessingOutcomes.Single();
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, mismatch.Status);
            Assert.AreEqual(ProcessingReasonCodes.ProjectedSceneSourceMismatch, mismatch.ReasonCode);

            Assert.AreEqual(0, reader.ReadCount, "replay must never consult the transient stage");
            Assert.IsNotNull(await staging.ReadAsync(stageKey, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ProcessingExecutionContext CreateReplayExecution() => new(
        Guid.NewGuid(),
        ProcessingGraphExecutionClass.Replay,
        "basic@1",
        new string('B', 64),
        false,
        1,
        "projected-scene-lease",
        "test-owner",
        DateTimeOffset.UtcNow.AddMinutes(5),
        1);

    private static SceneProvenance CreateStageProvenance(string stageKey, string sceneId) => new(
        sceneId, "rig-v1", "test", "1", new string('0', 64), "EquidistantFisheye",
        "projection-v1", "astronomy-v1", "sensor-v1",
        ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
        ProjectedSceneStageKey: stageKey);

    private static (ProcessingArtifact Artifact, ProjectedSceneV1 Scene) CreateFrozenSceneArtifact(
        ReconstructionDescriptor descriptor, VisibleScene visible)
    {
        var source = new ProjectedSceneSource(
            descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor));
        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.VirtualRenderAuthoritative, visible, ProjectedSceneImageTransformV1.Identity(2, 2), source,
            "projection-v1", "projection-v1");
        var payload = ProjectedSceneJson.Serialize(scene);
        var artifact = new ProcessingArtifact(
            Guid.NewGuid(), FrameArtifactRole.Metadata, "projected-scene-v1", new string('c', 64),
            StructuredProcessingProductContracts.ProjectedSceneMediaType, null, payload, DateTimeOffset.UnixEpoch,
            TimeSpan.Zero, CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor))
        {
            ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = ProjectedSceneV1.CurrentSchemaVersion,
            ContentIdentitySha256 = scene.SceneIdentitySha256
        };
        return (artifact, scene);
    }

    private sealed class CountingStagingReader(IProjectedSceneStagingReader inner) : IProjectedSceneStagingReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<StagedProjectedSceneDocument?> ReadAsync(string stageKey, CancellationToken cancellationToken)
        {
            ReadCount++;
            return inner.ReadAsync(stageKey, cancellationToken);
        }
    }

    private static ProjectedSceneCaptureProcessingStep CreateStep(
        ProjectedSceneStagingStore staging,
        IProjectedSceneStagingStore? stagingStore = null,
        CameraAgentRecipeExecutionAdapter? adapter = null,
        IProjectedSceneStagingReader? stagingReader = null)
    {
        return new ProjectedSceneCaptureProcessingStep(
            new CaptureProcessingStepMetadata("projected-scene", "ProjectedScene", 0),
            new ProjectedSceneCaptureProcessingStepOptions(), stagingStore ?? staging, stagingReader ?? staging,
            adapter ?? new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()));
    }

    private static (CaptureProcessingContext Context, ReconstructionDescriptor Descriptor, string PayloadPath) CreateContext(
        string root,
        SceneProvenance provenance,
        ProcessingExecutionContext? execution = null)
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
        return (new CaptureProcessingContext(CreateConfig(), submission, receipt, null, execution), descriptor, payloadPath);
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
                TimeSpan.FromSeconds(1), 1, 1)),
        CapturePipelineConfig.Empty);

    private static (CaptureProcessingContext Context, ReconstructionDescriptor Descriptor, string PayloadPath)
        CreatePhysicalContext(
            string root,
            CameraModuleConfig config,
            DeploymentLocationSnapshot location,
            SceneProvenance provenance,
            CameraModuleConfig? contextConfig = null)
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
        var manifest = original with { Descriptor = descriptor, Scene = provenance };
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
        return (new CaptureProcessingContext(contextConfig ?? config, submission, receipt), descriptor, payloadPath);
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
        => FileSystemTestPaths.CreatePhysicalTemporaryDirectory("skymonitor-tests");

    private sealed class MetadataCatalog(IEnumerable<CelestialCatalogObject> objects) :
        ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        private readonly InMemoryCelestialCatalog _inner = new(objects);

        public CatalogMetadata Metadata { get; } = new(
            "test", "1", new Uri("https://example.invalid"), new string('0', 64), "test", "1");

        public int QueryCount { get; private set; }

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query)
        {
            QueryCount++;
            return _inner.Query(query);
        }

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default)
        {
            QueryCount++;
            return _inner.QueryCandidatesAsync(query, cancellationToken);
        }
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

    private sealed class FailingAnnotationSceneProvider : IAnnotationSceneProvider
    {
        public ValueTask<AnnotationSceneResult> BuildAsync(
            CameraModuleConfig config,
            ReconstructionDescriptor? descriptor,
            CameraFrame rawFrame,
            IReadOnlyList<string> constellationIds,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<AnnotationSceneResult>(new AssertFailedException(
                "Annotation must not recalculate a declared projected-scene dependency."));
    }
}
