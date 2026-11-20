using System.Buffers.Binary;
using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    private static readonly string[] MeasurementDependencies = ["$raw", "linear"];

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, true)]
    [DataRow(CameraPixelFormat.BayerRggb16, "None", true, 1, false, true)]
    [DataRow(CameraPixelFormat.Mono16, "SyntheticReferences")]
    [DataRow(CameraPixelFormat.Mono16, "SyntheticReferences", false, 10)]
    [DataRow(CameraPixelFormat.Mono16, "None", false)]
    [DataRow(CameraPixelFormat.Mono16, "CalibrationLibrary")]
    [DataRow(CameraPixelFormat.Mono16, "Omitted")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, false, false, "blank")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, false, false, "clipped")]
    public async Task ExposureMeasurement_OrdinaryCapturePersistsAndReopensThroughStandardLane(
        CameraPixelFormat format, string calibration, bool declareReadout = true, int referenceGain = 1, bool fit = false,
        bool interrupt = false, string signal = "star")
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-exposure-measurement");
        try
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var profile = StoragePolicyRawIngressIntegrationTests.Profile(root);
            var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
            var camera = CreateConfig(format, 224, 224);
            var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(now, -113.878) / 15d;
            var predictionStar = new CelestialCatalogObject("independent-star", "Independent Star", rightAscension, 35.347, 0, 0.65);
            var predictions = new MeasurementPredictionCatalog(predictionStar);
            var location = DeploymentLocationSnapshot.Create("measurement-test", 1, "virtual", null,
                DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 0, "America/Phoenix");
            var references = calibration == "SyntheticReferences" ? new SyntheticCalibrationModelV1 { Gain = referenceGain } : null;
            var readout = format == CameraPixelFormat.Mono16 && declareReadout && calibration != "SyntheticReferences"
                ? new SensorReadoutProfile(new SensorCrop(0, 0, 224, 224), 1, 1, FrameBinningAlgorithm.IdentityV1,
                    CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.RightAlignedV1,
                    FrameLevelCodeSpace.NativeSample, 64, 4095)
                : null;
            var steps = new List<CaptureProcessingStepConfig>
            {
                new("ProjectedScene", "scene", DependsOn: ["$raw"], Required: false, Publication: durable),
                new("Storage", "storage", DependsOn: ["$raw"], Options: JsonSerializer.SerializeToElement(
                    new FileStorageCaptureProcessingStepOptions
                    {
                        StorageRoot = Path.Combine(root, "storage"), QueueForUpload = false, UpdateLatestFrame = false
                    }))
            };
            if (calibration != "Omitted")
            {
                steps.Insert(0, new("Calibration", "linear", DependsOn: ["$raw"],
                    Publication: new(CaptureProcessingPersistenceMode.MemoryOnly), Options: JsonSerializer.SerializeToElement(new
                    { strategy = calibration, outputVariant = "qualified-linear", syntheticCalibration = references })));
            }
            if (fit)
            {
                var snapshot = new AstrometryCatalogSnapshotV1(predictions.Metadata, [predictionStar],
                    new("fixture", "1", "fixture", "unspecified"), true, 7);
                steps.Add(new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false, Publication: durable));
                steps.Add(new("MeasuredAstrometry", "fit", DependsOn: ["scene", "associated"], Required: false,
                    Publication: durable, Options: JsonSerializer.SerializeToElement(new MeasuredAstrometryCaptureOptions { Catalog = snapshot })));
            }
            var file = camera with
            {
                AgentId = profile.AgentId,
                Schedule = profile.Schedule,
                Rig = camera.Rig with { ControlPolicy = profile.Rig.ControlPolicy, Readout = readout },
                DeploymentLocation = location,
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 1920, fixedSequenceStartUtc = now, illuminationMode = "ControlledNight",
                    asi178Sensor = new { enabled = format == CameraPixelFormat.BayerRggb16 },
                    syntheticCalibration = references,
                    backgroundElectronsPerSecond = signal == "clipped" ? 1_000_000d : (double?)null
                })),
                CenteredExposure = new("fixture-camera", CenteredExposureProfile.CurrentExecutionVersion),
                Pipeline = profile.Pipeline with { Steps = steps }
            };
            Guid executionId;
            Guid artifactId;
            string digest;
            using var cancellation = new CancellationTokenSource();
            using (var provider = MeasurementProvider(root, location, predictions,
                interrupt ? new MeasurementCommitInterruption(cancellation) : null))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, file).ConfigureAwait(false);
                var operations = provider.GetRequiredService<IProcessingGraphOperations>();
                var ui = MeasurementOperations(provider, operations);
                var configured = await ui.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(configured.IsSuccess, configured.Message);
                var revision = await ui.GetRevisionDetailAsync(configured.Value!.ConfiguredBasicRevisionId, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.IsTrue(revision.IsSuccess, revision.Message);
                var plan = revision.Value!.Plan;
                Assert.IsNotNull(plan);
                var node = plan.EffectiveNodes.Single(n => n.Id == ExposureMeasurementProducts.StepAlias);
                Assert.IsTrue(node.Enabled);
                Assert.IsFalse(node.Required);
                CollectionAssert.AreEqual(calibration == "Omitted" ? ["$raw"] : MeasurementDependencies, node.Dependencies!.ToArray());
                if (calibration != "Omitted")
                {
                    var linearNode = plan.EffectiveNodes.Single(n => n.Id == "linear");
                    Assert.IsFalse(linearNode.Required);
                    Assert.AreEqual(durable, linearNode.Publication);
                }
                else
                {
                    await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                        await operations.CreateRevisionAsync("measurement-bypass", "no-step-four",
                            active.Pipeline, "no-step-four", "measurement-test", null, CancellationToken.None)
                            .ConfigureAwait(false)).ConfigureAwait(false);
                }

                var catalog = new InMemoryCelestialCatalog(signal == "star" ? [
                    new CelestialCatalogObject("fixture-star", "Fixture Star", rightAscension, 35.347,
                        calibration == "SyntheticReferences" ? 2.5 : 0, 0.65)
                ] : []);
                var module = new VirtualSkyCameraModule(new ExposureTestTimeProvider(now, autoAdvance: true), catalog, new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var lifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(active, CancellationToken.None).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
                var reservation = await ingress.ReserveCenteredAsync(active, resources, null, CancellationToken.None).ConfigureAwait(false);
                var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
                await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "fixture", CancellationToken.None).ConfigureAwait(false);
                var request = new CaptureRequest(now, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
                {
                    BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                    BoundGraph = reservation.Revision.Pipeline
                };
                var capture = await module.CaptureBoundAsync(reservation.Pending.Key, request, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(capture.Frame);
                var receipt = await ingress.AcceptCenteredAsync(active, new(request, capture, now, request.TargetInterval,
                    request.TargetInterval), reservation, CancellationToken.None).ConfigureAwait(false);
                var exposure = CaptureExposureMeasurementReader.Key(receipt.Manifest);
                if (references is not null)
                {
                    var rawCalibration = receipt.Manifest.Descriptor.Profiles.Calibration;
                    Assert.IsTrue(CalibrationCaptureProcessingStep.MatchesSyntheticCalibrationModel(receipt.Manifest.Descriptor, references),
                        $"Raw calibration profile {rawCalibration.Name}/{rawCalibration.Version}/{rawCalibration.Sha256}");
                }

                var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
                await laneStore.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
                var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
                var lease = await laneStore.ClaimAsync(lane, "exposure-measurement-test", active, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                var reader = provider.GetRequiredService<IExposureProductReader>();
                var slot = new ExposureProductSlot(lease.Context.Execution!.ExecutionId, ExposureMeasurementProducts.StepAlias,
                    ExposureMeasurementProducts.SchemaVersion, ExposureMeasurementProducts.Variant);
                if (interrupt)
                {
                    await Assert.ThrowsAsync<OperationCanceledException>(async () => await provider.GetRequiredService<StandardCaptureLaneHandler>()
                        .HandleAsync(lease.Context, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
                    Assert.IsNull(await reader.ReadProductAsync(exposure, slot, CancellationToken.None).ConfigureAwait(false),
                        "Files alone cannot advertise an uncommitted measurement.");
                    await laneStore.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
                    lease = await laneStore.ClaimAsync(lane, "exposure-measurement-retry", active, CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(lease);
                    Assert.AreEqual(slot.ExecutionId, lease.Context.Execution!.ExecutionId);
                }
                var outcome = await provider.GetRequiredService<StandardCaptureLaneHandler>()
                    .HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, outcome.Outcome, outcome.Reason);
                await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);

                var id = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.IsNotNull(id);
                executionId = id.Value;
                var execution = await operations.ReadExecutionDetailAsync(executionId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(execution);
                if (!fit)
                    Assert.IsFalse(execution.Nodes.Any(item => item.NodeId == "fit"));
                var detail = await ui.GetExecutionDetailAsync(executionId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(detail.IsSuccess, detail.Message);
                var run = detail.Value!.ExposureMeasurement;
                Assert.IsNotNull(run?.Measurement, run?.UnavailableReason);
                var measurement = run.Measurement;
                Assert.AreEqual(receipt.Manifest.Descriptor.Layout, measurement.Input.Raw.Layout);
                Assert.AreEqual(receipt.Manifest.Descriptor.Capture.CaptureId, measurement.Input.Raw.CaptureId);
                Assert.AreEqual(format, measurement.Input.Raw.Layout.PixelFormat);
                Assert.AreEqual(224L * 224, measurement.RawBrightness!.SampleCount);
                var pixels = capture.Frame.PixelData;
                var codes = Enumerable.Range(0, pixels.Length / sizeof(ushort))
                    .Select(index => BinaryPrimitives.ReadUInt16LittleEndian(pixels.Span[(index * sizeof(ushort))..])).ToArray();
                Assert.AreEqual((ulong)codes.Sum(value => (long)value), measurement.RawBrightness.Sum);
                Assert.AreEqual(codes.Min(), measurement.RawBrightness.Minimum);
                Assert.AreEqual(codes.Max(), measurement.RawBrightness.Maximum);
                Assert.AreEqual(codes.LongCount(value => value == 0), measurement.RawBrightness.ZeroCount);
                if (measurement.RawBrightness.SensorClipping.StoredWhite is { } white)
                    Assert.AreEqual(codes.LongCount(value => value >= white), measurement.RawBrightness.SensorClipping.Count);
                if (calibration is not ("CalibrationLibrary" or "Omitted") && referenceGain == 1 && declareReadout)
                {
                    Assert.AreEqual(calibration == "None" ? ExposureCalibrationDisposition.NormalizationOnly :
                        ExposureCalibrationDisposition.ReferenceCalibrated, measurement.Input.Calibration,
                        measurement.Input.CalibrationReason);
                    Assert.IsNotNull(measurement.Input.Linear);
                    if (signal == "star")
                    {
                        Assert.AreEqual(ExposureMeasurementStatus.Usable, measurement.Sources.Status, measurement.Sources.Reason +
                            ": " + string.Join(", ", measurement.Sources.Exclusions.Select(item => item.ReasonCode)));
                        Assert.IsTrue(measurement.Sources.Detections.Any(detection =>
                            Math.Abs(detection.Pixel.X - 112) < 2 && Math.Abs(detection.Pixel.Y - 112) < 2),
                            "The independently placed zenith star must be measured near the optical center.");
                        Assert.IsGreaterThan(0, measurement.Hfd.ValidCount ?? 0, measurement.Hfd.Reason);
                    }
                    else if (signal == "blank")
                    {
                        Assert.AreEqual(ExposureMeasurementStatus.Empty, measurement.Sources.Status, measurement.Sources.Reason);
                        Assert.AreEqual(0, measurement.Hfd.ValidCount);
                        Assert.IsNull(measurement.Hfd.MeanPixels);
                    }
                    else
                    {
                        Assert.IsGreaterThan(0L, measurement.RawBrightness.SensorClipping.Count ?? 0);
                        Assert.AreEqual(ExposureMeasurementStatus.Unsuitable, measurement.Sources.Status, measurement.Sources.Reason);
                        Assert.IsNull(measurement.Hfd.MeanPixels);
                    }
                }
                else
                {
                    var expected = referenceGain != 1 || !declareReadout
                        ? ExposureCalibrationDisposition.Incompatible : ExposureCalibrationDisposition.Missing;
                    Assert.AreEqual(expected, measurement.Input.Calibration, measurement.Input.CalibrationReason);
                    if (referenceGain != 1)
                        Assert.AreEqual(ProcessingReasonCodes.CalibrationReferenceConditionsMismatch, measurement.Input.CalibrationReason);
                    else if (calibration == "Omitted")
                        Assert.AreEqual("linear.no-configured-step", measurement.Input.CalibrationReason);
                    else if (!declareReadout)
                    {
                        Assert.IsNull(measurement.RawBrightness.SensorClipping.Count);
                        Assert.AreEqual(FrameWhiteThreshold.UnavailableReason, measurement.Sources.Reason);
                    }
                    Assert.IsNull(measurement.Input.Linear);
                    Assert.AreEqual(ExposureMeasurementStatus.Unavailable, measurement.Sources.Status);
                }
                var output = Assert.ContainsSingle(execution.Nodes.Single(item => item.NodeId == ExposureMeasurementProducts.StepAlias).Outputs);
                artifactId = output.ArtifactId;
                digest = output.OutputIdentitySha256;
                var published = await reader.ReadProductAsync(exposure, slot, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(published);
                Assert.AreEqual(ExposureProductAvailability.Available, published.Availability);
                Assert.IsNotNull(published.Product);
                Assert.AreEqual(artifactId, published.Product?.ArtifactId);
                Assert.AreEqual(digest, published.Product?.OutputIdentitySha256);
                var notifications = await reader.ReadProductsAfterAsync(exposure.Camera, 0, 128, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.AreEqual(published, Assert.ContainsSingle(notifications.Where(product => product.Slot == slot)));
                if (fit)
                {
                    var association = execution.Nodes.Single(item => item.NodeId == "associated");
                    Assert.AreEqual("Completed", association.Status, association.Reason);
                    var associationOutput = Assert.ContainsSingle(association.Outputs);
                    Assert.Contains(artifactId, associationOutput.SourceArtifactIds);
                    var fitting = execution.Nodes.Single(item => item.NodeId == "fit");
                    Assert.AreEqual("Completed", fitting.Status, fitting.Reason);
                    var solutionOutput = fitting.Outputs.Single(item => item.Variant == "measured-astrometry-v1");
                    var stored = await provider.GetRequiredService<SqliteCaptureProcessingStore>().ReadOutputByArtifactIdAsync(
                        solutionOutput.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(stored);
                    var solution = MeasuredAstrometryJson.Parse(await File.ReadAllBytesAsync(Path.Combine(root, stored.PayloadRelativePath))
                        .ConfigureAwait(false));
                    Assert.AreEqual(AstrometricAssessmentStatus.Rejected, solution.Assessment.Status, solution.Assessment.Reason);
                    Assert.AreEqual(associationOutput.ArtifactId, solution.MeasuredArtifactId);
                }
            }

            using var reopened = MeasurementProvider(root, location, predictions);
            var restartedIngress = reopened.GetRequiredService<RawCaptureIngress>();
            await restartedIngress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await restartedIngress.ReconcileCenteredControlAsync(file with { CenteredExposure = null }, CancellationToken.None)
                .ConfigureAwait(false);
            var reopenedExecution = await reopened.GetRequiredService<IProcessingGraphOperations>()
                .ReadExecutionDetailAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(reopenedExecution);
            var reopenedOutput = Assert.ContainsSingle(reopenedExecution.Nodes.Single(
                item => item.NodeId == ExposureMeasurementProducts.StepAlias).Outputs);
            Assert.AreEqual(artifactId, reopenedOutput.ArtifactId);
            Assert.AreEqual(digest, reopenedOutput.OutputIdentitySha256);
            var restored = await reopened.GetRequiredService<IExposureMeasurementRunReader>()
                .ReadAsync(reopenedExecution, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(restored?.Measurement, restored?.UnavailableReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ServiceProvider MeasurementProvider(string root, DeploymentLocationSnapshot location, ICelestialCatalog catalog,
        ICaptureProcessingFaultInjector? fault = null)
        => ProcessingGraphOperationsTests.CreateProvider(root, configureServices: services =>
        {
            HVO.SkyMonitor.CameraAgent.Program.AddCameraModules(services);
            services.AddSingleton(catalog);
            var locations = new Mock<IDeploymentLocationStore>();
            locations.Setup(store => store.Resolve(location.ToProvenance(), It.IsAny<DateTimeOffset?>())).Returns(location);
            services.AddSingleton(locations.Object);
            if (fault is not null)
                services.AddSingleton(fault);
        });

    private sealed class MeasurementCommitInterruption(CancellationTokenSource cancellation) : ICaptureProcessingFaultInjector
    {
        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit &&
                nodeId == ExposureMeasurementProducts.StepAlias && !cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        }
    }

    private sealed class MeasurementPredictionCatalog(CelestialCatalogObject star) : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        private readonly InMemoryCelestialCatalog _catalog = new([star]);

        public CatalogMetadata Metadata { get; } = new("measurement-prediction", "fixture", new Uri("https://example.invalid/catalog"),
            new string('A', 64), "fixture", "2");

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _catalog.Query(query);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(CatalogCandidateQuery query,
            CancellationToken cancellationToken = default) => _catalog.QueryCandidatesAsync(query, cancellationToken);
    }

    private static CameraAgentProcessingGraphUiService MeasurementOperations(ServiceProvider provider, IProcessingGraphOperations operations)
    {
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(service => service.GetAuthenticationStateAsync()).ReturnsAsync(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity("test"))));
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(service => service.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Success());
        return new(authentication.Object, authorization.Object, operations,
            provider.GetRequiredService<ICaptureProcessingPipelineFactory>(),
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
            provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>(), TimeProvider.System,
            NullLogger<CameraAgentProcessingGraphUiService>.Instance, provider.GetRequiredService<IExposureMeasurementRunReader>());
    }
}
