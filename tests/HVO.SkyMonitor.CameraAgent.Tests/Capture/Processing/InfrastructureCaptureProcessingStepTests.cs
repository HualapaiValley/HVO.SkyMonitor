using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
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
        var storage = new Mock<IFrameStorageService>(MockBehavior.Strict);
        var step = new FileStorageCaptureProcessingStep(
            new CaptureProcessingStepMetadata("storage", "Storage", 100),
            new FileStorageCaptureProcessingStepOptions { UpdateLatestFrame = true },
            latest.Object,
            storage.Object,
            NullLogger<FileStorageCaptureProcessingStep>.Instance);

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        storage.VerifyNoOtherCalls();
        latest.Verify(accessor => accessor.Update(annotation), Times.Once);
    }

    [TestMethod]
    public async Task ExplicitStorageRejectsSelectedDerivativeWithoutReconstructionEvidence()
    {
        var context = CreateContext();
        context.BeginNode("included", []);
        var included = context.AddDerivative(FrameArtifactRole.Preview, CreateFrame(1), "preview-v1");
        context.BeginNode("excluded", []);
        var excluded = context.AddDerivative(FrameArtifactRole.AnnotatedPreview, CreateFrame(2), "annotation-v1");
        context.BeginNode("storage", ["included"]);
        var storage = new Mock<IFrameStorageService>(MockBehavior.Strict);
        var step = new FileStorageCaptureProcessingStep(
            new CaptureProcessingStepMetadata("storage", "Storage", 100),
            new FileStorageCaptureProcessingStepOptions { UpdateLatestFrame = false },
            Mock.Of<ILatestFrameAccessor>(),
            storage.Object,
            NullLogger<FileStorageCaptureProcessingStep>.Instance);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        storage.VerifyNoOtherCalls();
        Assert.IsEmpty(context.ProcessingOutcomes);
        var evidence = context.GetCurrentInputEvidence();
        Assert.HasCount(1, evidence);
        Assert.AreEqual(included.ArtifactId, evidence[0].ArtifactId);
        Assert.IsTrue(evidence[0].Selected);
    }

    [TestMethod]
    public async Task ExplicitStorageRejectsRawDependencyWithoutIngressManifest()
    {
        var context = CreateContext();
        var raw = context.Artifacts!.Raw;
        context.BeginNode("excluded", []);
        _ = context.AddDerivative(FrameArtifactRole.Preview, CreateFrame(1), "preview-v1");
        context.BeginNode("storage", [], ["$raw"]);
        var storage = new Mock<IFrameStorageService>(MockBehavior.Strict);
        var latest = new Mock<ILatestFrameAccessor>(MockBehavior.Strict);
        var step = new FileStorageCaptureProcessingStep(
            new CaptureProcessingStepMetadata("storage", "Storage", 100),
            new FileStorageCaptureProcessingStepOptions
            {
                UpdateLatestFrame = true,
                Policies = [new ArtifactStoragePolicyOptions { Role = FrameArtifactRole.Raw, RetentionDays = 60 }]
            },
            latest.Object,
            storage.Object,
            NullLogger<FileStorageCaptureProcessingStep>.Instance);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        storage.VerifyNoOtherCalls();
        latest.VerifyNoOtherCalls();
        Assert.HasCount(1, context.GetCurrentInputEvidence());
        Assert.AreEqual(raw.ArtifactId, context.GetCurrentInputEvidence()[0].ArtifactId);
    }

    [TestMethod]
    public async Task StorageStepIdPolicy_DoesNotChangeDescriptorIdentityOrSemanticSource()
    {
        var payload = new byte[] { 0, 0, 0, 0 };
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono8, 2, 2, 2, payload);
        var receipt = new RawCaptureReceipt(
            RawIngressOutcome.Committed,
            manifest,
            new StoredFrameReference(
                manifest.RelativeArtifactPath,
                Path.Combine("/tmp/camera", manifest.RelativeArtifactPath),
                manifest.Descriptor.Timing.ExposureStartedUtc,
                FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifest));

        async Task<ReconstructionDescriptor> StoreAsync(string? policyStepId)
        {
            var context = CreateContext(receipt);
            context.BeginNode("producer", ["$raw"]);
            var raw = context.Artifacts!.Raw;
            var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                "preview", "1.0.0", "preview-v1", System.Text.Json.JsonSerializer.SerializeToElement(new { })));
            var sources = new[] { manifest.Descriptor.Artifact.ArtifactId };
            var product = new ProcessingProduct(
                FrameArtifactRole.Preview,
                "preview-v1",
                ProcessingIdentity.CreateOutputIdentity(
                    FrameArtifactRole.Preview, "preview-v1", recipe.IdentitySha256, sources),
                "application/x-hvo-linear-frame",
                manifest.Descriptor.Layout,
                payload,
                ProcessingIdentity.ComputePayloadSha256(payload),
                recipe,
                [new ProcessingAlgorithmIdentity("preview", "v1")],
                sources,
                raw.Frame.Metadata.Exposure,
                CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, "source").Compatibility);
            var artifact = context.AddDerivative(
                FrameArtifactRole.Preview,
                raw.Frame with { Metadata = raw.Frame.Metadata with { SourceId = "semantic-source" } },
                "preview-v1",
                sources,
                CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
            context.AssociateProcessingProduct(artifact, product);
            context.BeginNode("storage", ["producer"]);
            ReconstructionDescriptor? captured = null;
            var storage = new Mock<IFrameStorageService>(MockBehavior.Strict);
            storage.Setup(service => service.SaveAsync(
                    "/tmp/camera", artifact, It.IsAny<ReconstructionDescriptor>(), It.IsAny<CancellationToken>()))
                .Callback((string _, FrameArtifact _, ReconstructionDescriptor descriptor, CancellationToken _) => captured = descriptor)
                .ReturnsAsync(new StoredFrameReference(
                    "preview.bin", "/tmp/camera/preview.bin", artifact.Frame.TimestampUtc, artifact.Role));
            storage.Setup(service => service.SaveAsync(
                    "/tmp/camera", artifact, It.IsAny<ReconstructionDescriptor>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback((string _, FrameArtifact _, ReconstructionDescriptor descriptor, string _, CancellationToken _) => captured = descriptor)
                .ReturnsAsync(new StoredFrameReference(
                    "preview.bin", "/tmp/camera/preview.bin", artifact.Frame.TimestampUtc, artifact.Role));
            var step = new FileStorageCaptureProcessingStep(
                new CaptureProcessingStepMetadata("storage", "Storage", 100),
                new FileStorageCaptureProcessingStepOptions
                {
                    StorageRoot = "/tmp/camera",
                    UpdateLatestFrame = false,
                    Policies = policyStepId is null
                        ? []
                        : [new ArtifactStoragePolicyOptions { StepId = policyStepId }]
                },
                Mock.Of<ILatestFrameAccessor>(),
                storage.Object,
                NullLogger<FileStorageCaptureProcessingStep>.Instance);

            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            return captured!;
        }

        var withoutPolicy = await StoreAsync(null).ConfigureAwait(false);
        var withRecasedPolicy = await StoreAsync("PrOdUcEr").ConfigureAwait(false);

        Assert.AreEqual("semantic-source", withoutPolicy.Artifact.SourceId);
        Assert.AreEqual(withoutPolicy.Artifact.SourceId, withRecasedPolicy.Artifact.SourceId);
        Assert.AreEqual(
            CaptureContractJson.ComputeDescriptorSha256(withoutPolicy),
            CaptureContractJson.ComputeDescriptorSha256(withRecasedPolicy));
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
