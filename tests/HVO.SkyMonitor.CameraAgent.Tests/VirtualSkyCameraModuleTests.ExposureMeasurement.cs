using System.Buffers.Binary;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, "None")]
    [DataRow(CameraPixelFormat.BayerRggb16, "None")]
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
            var file = camera with
            {
                Schedule = profile.Schedule,
                DeploymentLocation = DeploymentLocationSnapshot.Create("measurement-test", 1, "virtual",
                    null, DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 0, "America/Phoenix"),
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 1920, fixedSequenceStartUtc = now, illuminationMode = "ControlledNight",
                    asi178Sensor = new { enabled = format == CameraPixelFormat.BayerRggb16 }
                })),
                Pipeline = profile.Pipeline with
                {
                    Steps =
                    [
                        new("Calibration", "linear", DependsOn: ["$raw"], Publication: durable,
                            Options: JsonSerializer.SerializeToElement(new CalibrationProcessingStepOptions
                            { Strategy = calibration, OutputVariant = "qualified-linear" })),
                        new(ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.StepAlias,
                            DependsOn: ["$raw", "linear"], Publication: durable)
                    ]
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
                CollectionAssert.AreEquivalent(new[] { "$raw", "linear" }, node.Dependencies!.ToArray());

                await using var module = CreateModule(now);
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
                if (calibration == "None")
                {
                    Assert.AreEqual(ExposureCalibrationDisposition.NormalizationOnly, measurement.Input.Calibration);
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
            services.AddSingleton(new CaptureProcessingStepRegistration(ExposureMeasurementProducts.StepAlias,
                typeof(ExposureMeasurementCaptureProcessingStep), typeof(ExposureMeasurementStepOptions), 4, AutoInclude: false)));
}
