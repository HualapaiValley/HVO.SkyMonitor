using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Capture.Processing")]
public sealed class InfrastructureCaptureProcessingStepTests
{
    [TestMethod]
    public async Task OrdinaryUploadLane_QueuesExactlyOneRawManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "raw-upload");
        var payload = new byte[] { 0, 0, 0, 0 };
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono8,
            2,
            2,
            2,
            payload);
        var receipt = new RawCaptureReceipt(
            RawIngressOutcome.Committed,
            manifest,
            new StoredFrameReference(
                manifest.RelativeArtifactPath,
                Path.Combine(root, manifest.RelativeArtifactPath),
                manifest.Descriptor.Timing.ExposureStartedUtc,
                FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifest));
        var context = CreateContext(receipt);
        var outbox = new Mock<IArtifactOutbox>(MockBehavior.Strict);
        outbox.Setup(value => value.EnqueueAsync(root, manifest, It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        var handler = new UploadCaptureLaneHandler(
            outbox.Object,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));

        var result = await handler.HandleAsync(new CaptureLaneHandlerContext(
            "upload",
            1,
            context.Config,
            context.Submission,
            receipt), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
        outbox.Verify(value => value.EnqueueAsync(root, manifest, It.IsAny<CancellationToken>()), Times.Once);
        outbox.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExplicitPublicationOwnership_PreventsStorageWriteButKeepsPackedLatestFrame()
    {
        var baseline = ProcessingConformanceFixture.CameraConfig;
        var config = baseline with
        {
            AgentId = "agent-test",
            Pipeline = new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig(
                        "Annotation",
                        "annotation",
                        DependsOn: ["$raw"],
                        Publication: new CaptureProcessingPublicationPolicy(
                            CaptureProcessingPersistenceMode.MemoryOnly)),
                    new CaptureProcessingStepConfig("Storage", "storage", DependsOn: ["annotation"])
                ],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };
        var frame = CreateFrame(0);
        var context = new CaptureProcessingContext(
            config,
            new CaptureLoopSubmission(
                new CaptureRequest(frame.TimestampUtc, frame.Metadata.Exposure, CaptureMode.Still),
                new CaptureResult(
                    frame,
                    new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, null),
                    TimeSpan.Zero,
                    CaptureMode.Still,
                    false),
                frame.TimestampUtc,
                frame.Metadata.Exposure,
                TimeSpan.Zero));
        context.BeginNode("annotation", []);
        var annotation = context.AddDerivative(
            FrameArtifactRole.AnnotatedPreview,
            CreateFrame(2),
            "annotation-v1");
        context.BeginNode("storage", ["annotation"]);
        var latest = new Mock<ILatestFrameAccessor>(MockBehavior.Strict);
        latest.Setup(accessor => accessor.Update(annotation));
        var step = new SiloCaptureProcessingStep(
            new CaptureProcessingStepMetadata("storage", "Storage", 100),
            new SiloCaptureProcessingStepOptions(),
            latest.Object,
            processingPersistence: null!);
        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        latest.Verify(accessor => accessor.Update(annotation), Times.Once);
    }

    [TestMethod]
    public async Task ExplicitTelemetryReportsOnlyDeclaredDependencyOutcomes()
    {
        var context = CreateContext();
        context.AddStepTelemetry(new CaptureProcessingStepTelemetry("included", TimeSpan.FromMilliseconds(1), true, null));
        context.AddStepTelemetry(new CaptureProcessingStepTelemetry("excluded", TimeSpan.FromMilliseconds(2), false, "failed"));
        context.BeginNode("telemetry", ["included"]);
        var sink = new CaptureTelemetrySink();
        var step = new TelemetryCaptureProcessingStep(
            new CaptureProcessingStepMetadata("telemetry", "Telemetry", 200),
            new TelemetryProcessingStepOptions(),
            sink,
            new CaptureTelemetryMetricsRecorder(),
            NullLogger<TelemetryCaptureProcessingStep>.Instance);

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(1, sink.Latest!.ProcessingSteps);
        Assert.AreEqual("included", sink.Latest.ProcessingSteps[0].Name);
        Assert.IsEmpty(context.ProcessingOutcomes);
        var evidence = context.GetCurrentInputEvidence();
        Assert.HasCount(1, evidence);
        Assert.AreEqual("CanonicalContext", evidence[0].Kind);
        Assert.AreEqual("dependency-telemetry", evidence[0].Name);
        Assert.AreEqual("capture-processing-telemetry-input-v1", evidence[0].SchemaVersion);
        Assert.AreEqual(64, evidence[0].IdentitySha256!.Length);
    }

    private static CaptureProcessingContext CreateContext(RawCaptureReceipt? receipt = null)
    {
        var frame = CreateFrame(0);
        var baseline = ProcessingConformanceFixture.CameraConfig;
        var config = baseline with
        {
            AgentId = "agent-test",
            Pipeline = new CapturePipelineConfig(
                [],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };
        return new CaptureProcessingContext(
            config,
            new CaptureLoopSubmission(
                new CaptureRequest(frame.TimestampUtc, frame.Metadata.Exposure, CaptureMode.Still),
                new CaptureResult(
                    frame,
                    new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, null),
                    TimeSpan.Zero,
                    CaptureMode.Still,
                    false),
                frame.TimestampUtc,
                frame.Metadata.Exposure,
                TimeSpan.Zero),
            receipt);
    }

    private static CameraFrame CreateFrame(byte value)
        => new(
            new DateTimeOffset(2026, 1, 15, 8, 10, 0, TimeSpan.Zero),
            2,
            2,
            CameraPixelFormat.Mono8,
            new byte[] { value, value, value, value },
            new FrameMetadata(TimeSpan.FromSeconds(1), 10, 0, "infrastructure-test"),
            2);
}
