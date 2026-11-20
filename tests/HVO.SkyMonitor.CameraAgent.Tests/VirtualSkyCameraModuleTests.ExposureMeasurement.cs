using System.Buffers.Binary;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, "None")]
    [DataRow(CameraPixelFormat.BayerRggb16, "None")]
    [DataRow(CameraPixelFormat.Mono16, "SyntheticReferences")]
    [DataRow(CameraPixelFormat.Mono16, "CalibrationLibrary")]
    public async Task ExposureMeasurement_OrdinaryCapturePersistsAndReopensThroughStandardLane(
        CameraPixelFormat format, string calibration)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-exposure-measurement");
        try
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var profile = StoragePolicyRawIngressIntegrationTests.Profile(root);
            var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
            var camera = CreateConfig(format, 224, 224);
            var references = calibration == "SyntheticReferences" ? new SyntheticCalibrationModelV1() : null;
            var centeredPlan = ExposureMeasurementGraphPlan.Configure(profile.Pipeline with
            {
                Steps =
                [
                    new("Calibration", "linear", DependsOn: ["$raw"], Required: false, Publication: durable,
                        Options: JsonSerializer.SerializeToElement(new
                        { strategy = calibration, outputVariant = "qualified-linear", syntheticCalibration = references })),
                    new("Storage", "storage", DependsOn: ["$raw"], Options: JsonSerializer.SerializeToElement(
                        new FileStorageCaptureProcessingStepOptions
                        {
                            StorageRoot = Path.Combine(root, "storage"), QueueForUpload = false, UpdateLatestFrame = false
                        }))
                ]
            });
            var fixedStep = centeredPlan.Steps.Single(step => step.Id == ExposureMeasurementProducts.StepAlias);
            CollectionAssert.AreEqual(new List<string> { "$raw", "linear" }, fixedStep.DependsOn!.ToArray());
            var file = camera with
            {
                AgentId = profile.AgentId,
                Schedule = profile.Schedule,
                Rig = camera.Rig with { ControlPolicy = profile.Rig.ControlPolicy },
                DeploymentLocation = DeploymentLocationSnapshot.Create("measurement-test", 1, "virtual",
                    null, DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 0, "America/Phoenix"),
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 1920, fixedSequenceStartUtc = now, illuminationMode = "ControlledNight",
                    asi178Sensor = new { enabled = format == CameraPixelFormat.BayerRggb16 },
                    syntheticCalibration = references
                })),
                Pipeline = centeredPlan with
                {
                    // The Common fixture uses the base graph compiler and the worker's admitted primary raw.
                    Steps = centeredPlan.Steps.Select(step => step == fixedStep ? step with { DependsOn = ["linear"] } : step).ToArray()
                }
            };
            Guid executionId;
            Guid artifactId;
            string digest;
            using (var provider = MeasurementProvider(root))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, file).ConfigureAwait(false);
                var plan = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().PreviewPlan(active);
                var node = plan.EffectiveNodes.Single(n => n.Id == ExposureMeasurementProducts.StepAlias);
                Assert.IsTrue(node.Enabled);
                Assert.IsFalse(node.Required);
                Assert.AreEqual("linear", Assert.ContainsSingle(node.Dependencies!));

                var module = CreateModule(now);
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

                var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
                await laneStore.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
                var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
                var lease = await laneStore.ClaimAsync(lane, "exposure-measurement-test", active, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                var outcome = await provider.GetRequiredService<StandardCaptureLaneHandler>()
                    .HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, outcome.Outcome, outcome.Reason);
                await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);

                var operations = provider.GetRequiredService<IProcessingGraphOperations>();
                var id = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.IsNotNull(id);
                executionId = id.Value;
                var execution = await operations.ReadExecutionDetailAsync(executionId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(execution);
                Assert.IsFalse(execution.Nodes.Any(item => item.NodeId == "MeasuredAstrometry"),
                    "The retained results exist when the optional fit was never scheduled.");
                var run = await provider.GetRequiredService<IExposureMeasurementRunReader>().ReadAsync(execution, CancellationToken.None)
                    .ConfigureAwait(false);
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
                if (calibration != "CalibrationLibrary")
                {
                    Assert.AreEqual(calibration == "None" ? ExposureCalibrationDisposition.NormalizationOnly :
                        ExposureCalibrationDisposition.ReferenceCalibrated, measurement.Input.Calibration);
                    Assert.IsNotNull(measurement.Input.Linear);
                    Assert.AreNotEqual(ExposureMeasurementStatus.Unavailable, measurement.Sources.Status, measurement.Sources.Reason);
                }
                else
                {
                    Assert.AreEqual(ExposureCalibrationDisposition.Missing, measurement.Input.Calibration, measurement.Input.CalibrationReason);
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
            services.AddSingleton(new CaptureProcessingStepRegistration(ExposureMeasurementProducts.StepAlias,
                typeof(ExposureMeasurementCaptureProcessingStep), typeof(ExposureMeasurementStepOptions), 4, AutoInclude: false));
        });
}
