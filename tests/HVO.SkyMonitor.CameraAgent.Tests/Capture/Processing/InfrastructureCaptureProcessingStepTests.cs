using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class InfrastructureCaptureProcessingStepTests
{
    [TestMethod]
    public async Task ExplicitStorageConsumesOnlyDeclaredDependencyArtifacts()
    {
        var context = CreateContext();
        context.BeginNode("included", []);
        var included = context.AddDerivative(FrameArtifactRole.Preview, CreateFrame(1), "preview-v1");
        context.BeginNode("excluded", []);
        var excluded = context.AddDerivative(FrameArtifactRole.AnnotatedPreview, CreateFrame(2), "annotation-v1");
        context.BeginNode("storage", ["included"]);
        var storage = new Mock<IFrameStorageService>(MockBehavior.Strict);
        storage.Setup(service => service.SaveAsync(
                "/tmp/camera", included, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredFrameReference("included.bin", "/tmp/camera/included.bin",
                included.Frame.TimestampUtc, included.Role));
        var step = new NoOpFileStorageProcessingStep(
            new CaptureProcessingStepMetadata("storage", "Storage", 100),
            new NoOpFileStorageProcessingStepOptions { UpdateLatestFrame = false },
            Mock.Of<ILatestFrameAccessor>(),
            storage.Object,
            Mock.Of<IArtifactOutbox>(),
            Options.Create(new CameraAgentHostOptions()),
            NullLogger<NoOpFileStorageProcessingStep>.Instance);

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        storage.Verify(service => service.SaveAsync(
            "/tmp/camera", included, It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(service => service.SaveAsync(
            It.IsAny<string>(), excluded, It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsEmpty(context.ProcessingOutcomes);
        var evidence = context.GetCurrentInputEvidence();
        Assert.HasCount(1, evidence);
        Assert.AreEqual(included.ArtifactId, evidence[0].ArtifactId);
        Assert.IsTrue(evidence[0].Selected);
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

    private static CaptureProcessingContext CreateContext()
    {
        var frame = CreateFrame(0);
        var baseline = ProcessingConformanceFixture.CameraConfig;
        var config = baseline with
        {
            AgentId = "agent-test",
            ProcessingSteps = null,
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
                TimeSpan.Zero));
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
