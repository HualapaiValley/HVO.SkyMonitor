using System.Buffers.Binary;
using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
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
    [DataRow(CameraPixelFormat.Mono16, "None")]
    [DataRow(CameraPixelFormat.BayerRggb16, "None")]
    [DataRow(CameraPixelFormat.Mono16, "SyntheticReferences")]
    [DataRow(CameraPixelFormat.Mono16, "SyntheticReferences", false, 10)]
    [DataRow(CameraPixelFormat.Mono16, "None", false)]
    [DataRow(CameraPixelFormat.Mono16, "CalibrationLibrary")]
    public async Task ExposureMeasurement_OrdinaryCapturePersistsAndReopensThroughStandardLane(
        CameraPixelFormat format, string calibration, bool declareReadout = true, int referenceGain = 1)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-exposure-measurement");
        try
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var profile = StoragePolicyRawIngressIntegrationTests.Profile(root);
            var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
            var camera = CreateConfig(format, 224, 224);
            var references = calibration == "SyntheticReferences" ? new SyntheticCalibrationModelV1 { Gain = referenceGain } : null;
            var readout = format == CameraPixelFormat.Mono16 && declareReadout && calibration == "None"
                ? new SensorReadoutProfile(new SensorCrop(0, 0, 224, 224), 1, 1, FrameBinningAlgorithm.IdentityV1,
                    CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.RightAlignedV1,
                    FrameLevelCodeSpace.NativeSample, 0, 4095, 64)
                : null;
            var pipeline = profile.Pipeline with
            {
                Steps =
                [
                    new("Calibration", "linear", DependsOn: ["$raw"], Required: false, Publication: durable,
                        Options: JsonSerializer.SerializeToElement(new
                        { strategy = calibration, outputVariant = "qualified-linear", syntheticCalibration = references })),
                    new("ProjectedScene", "scene", DependsOn: ["$raw"], Required: false, Publication: durable),
                    new("Storage", "storage", DependsOn: ["$raw"], Options: JsonSerializer.SerializeToElement(
                        new FileStorageCaptureProcessingStepOptions
                        {
                            StorageRoot = Path.Combine(root, "storage"), QueueForUpload = false, UpdateLatestFrame = false
                        }))
                ]
            };
            var file = camera with
            {
                AgentId = profile.AgentId,
                Schedule = profile.Schedule,
                Rig = camera.Rig with { ControlPolicy = profile.Rig.ControlPolicy, Readout = readout },
                DeploymentLocation = DeploymentLocationSnapshot.Create("measurement-test", 1, "virtual",
                    null, DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 0, "America/Phoenix"),
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 1920, fixedSequenceStartUtc = now, illuminationMode = "ControlledNight",
                    asi178Sensor = new { enabled = format == CameraPixelFormat.BayerRggb16 },
                    syntheticCalibration = references
                })),
                CenteredExposure = new("fixture-camera", CenteredExposureProfile.CurrentExecutionVersion),
                Pipeline = pipeline
            };
            Guid executionId;
            Guid artifactId;
            string digest;
            using (var provider = MeasurementProvider(root))
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
                CollectionAssert.AreEqual(MeasurementDependencies, node.Dependencies!.ToArray());

                var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(now, -113.878) / 15d;
                var catalog = new InMemoryCelestialCatalog([
                    new CelestialCatalogObject("fixture-star", "Fixture Star", rightAscension, 35.347, 0, 0.65)
                ]);
                var module = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), catalog, new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var lifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(active, CancellationToken.None).ConfigureAwait(false);
                var request = new CaptureRequest(now, TimeSpan.FromSeconds(1), CaptureMode.Still,
                    new(TimeSpan.FromSeconds(1), 1, null, null));
                var capture = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(capture.Frame);
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                var receipt = await ingress.AcceptAsync(active, new(request, capture, now, request.TargetInterval,
                    request.TargetInterval), CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
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
                Assert.IsFalse(execution.Nodes.Any(item => item.NodeId == "MeasuredAstrometry"),
                    "The retained results exist when the optional fit was never scheduled.");
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
                var sum = Enumerable.Range(0, pixels.Length / sizeof(ushort))
                    .Sum(index => (long)BinaryPrimitives.ReadUInt16LittleEndian(pixels.Span[(index * sizeof(ushort))..]));
                Assert.AreEqual((ulong)sum, measurement.RawBrightness.Sum);
                if (calibration != "CalibrationLibrary" && referenceGain == 1)
                {
                    Assert.AreEqual(calibration == "None" ? ExposureCalibrationDisposition.NormalizationOnly :
                        ExposureCalibrationDisposition.ReferenceCalibrated, measurement.Input.Calibration,
                        measurement.Input.CalibrationReason);
                    Assert.IsNotNull(measurement.Input.Linear);
                    if (declareReadout)
                        Assert.AreNotEqual(ExposureMeasurementStatus.Unavailable, measurement.Sources.Status, measurement.Sources.Reason);
                    else
                    {
                        Assert.IsNull(measurement.RawBrightness.SensorClipping.Count);
                        Assert.AreEqual(FrameWhiteThreshold.UnavailableReason, measurement.Sources.Reason);
                    }
                }
                else
                {
                    var expected = referenceGain != 1 ? ExposureCalibrationDisposition.Incompatible : ExposureCalibrationDisposition.Missing;
                    Assert.AreEqual(expected, measurement.Input.Calibration, measurement.Input.CalibrationReason);
                    if (referenceGain != 1)
                        Assert.AreEqual(ProcessingReasonCodes.CalibrationReferenceConditionsMismatch, measurement.Input.CalibrationReason);
                    Assert.IsNull(measurement.Input.Linear);
                    Assert.AreEqual(ExposureMeasurementStatus.Unavailable, measurement.Sources.Status);
                }
                var output = Assert.ContainsSingle(execution.Nodes.Single(item => item.NodeId == ExposureMeasurementProducts.StepAlias).Outputs);
                artifactId = output.ArtifactId;
                digest = output.OutputIdentitySha256;
            }

            using var reopened = MeasurementProvider(root);
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

    private static ServiceProvider MeasurementProvider(string root)
        => ProcessingGraphOperationsTests.CreateProvider(root, configureServices: services =>
        {
            HVO.SkyMonitor.CameraAgent.Program.AddCameraModules(services);
            services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
        });

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
