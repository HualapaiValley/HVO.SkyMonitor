using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
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
    public async Task GlobalFrameUploadPolicyDoesNotApplyToDurableLayoutlessMetadata()
    {
        var payload = new byte[] { 0, 0, 0, 0 };
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono8, 2, 2, 2, payload);
        var receipt = new RawCaptureReceipt(
            RawIngressOutcome.Committed,
            manifest,
            new StoredFrameReference(
                "raw.bin",
                "/tmp/camera/raw.bin",
                manifest.Descriptor.Timing.ExposureStartedUtc,
                FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifest));
        var context = CreateContext(receipt);
        var raw = context.Artifacts!.Raw;
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            "image-quality", "1.0.0", "integer-image-statistics-v1",
            System.Text.Json.JsonSerializer.SerializeToElement(new { })));
        var sources = new[] { raw.ArtifactId };
        var metadataPayload = "{}"u8.ToArray();
        var product = new ProcessingProduct(
            FrameArtifactRole.Metadata,
            "image-quality-v1",
            ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.Metadata, "image-quality-v1", recipe.IdentitySha256, sources),
            "application/json",
            null,
            metadataPayload,
            ProcessingIdentity.ComputePayloadSha256(metadataPayload),
            recipe,
            [new ProcessingAlgorithmIdentity("image-statistics", "v1")],
            sources,
            raw.Frame.Metadata.Exposure,
            CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, "source").Compatibility);
        context.RestoreProduct(
            "quality",
            CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256),
            product);
        context.BeginNode("storage", ["quality"]);
        var storage = new Mock<IFrameStorageService>(MockBehavior.Strict);
        var outbox = new Mock<IArtifactOutbox>(MockBehavior.Strict);
        var step = new NoOpFileStorageProcessingStep(
            new CaptureProcessingStepMetadata("storage", "Storage", 100),
            new NoOpFileStorageProcessingStepOptions
            {
                StorageRoot = "/tmp/camera",
                QueueForUpload = true,
                UpdateLatestFrame = false
            },
            Mock.Of<ILatestFrameAccessor>(),
            storage.Object,
            outbox.Object,
            Options.Create(new CameraAgentHostOptions
            {
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
            }),
            NullLogger<NoOpFileStorageProcessingStep>.Instance);

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        storage.VerifyNoOtherCalls();
        outbox.VerifyNoOtherCalls();
        Assert.IsEmpty(context.ProcessingOutcomes);
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
