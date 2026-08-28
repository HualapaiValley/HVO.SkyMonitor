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
        var storage = new Mock<IFrameStorageService>(MockBehavior.Strict);
        var step = new NoOpFileStorageProcessingStep(
            new CaptureProcessingStepMetadata("storage", "Storage", 100),
            new NoOpFileStorageProcessingStepOptions { UpdateLatestFrame = true },
            latest.Object,
            storage.Object,
            Mock.Of<IArtifactOutbox>(),
            Options.Create(new CameraAgentHostOptions()),
            NullLogger<NoOpFileStorageProcessingStep>.Instance);

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
        var step = new NoOpFileStorageProcessingStep(
            new CaptureProcessingStepMetadata("storage", "Storage", 100),
            new NoOpFileStorageProcessingStepOptions { UpdateLatestFrame = false },
            Mock.Of<ILatestFrameAccessor>(),
            storage.Object,
            Mock.Of<IArtifactOutbox>(),
            Options.Create(new CameraAgentHostOptions()),
            NullLogger<NoOpFileStorageProcessingStep>.Instance);

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
        var step = new NoOpFileStorageProcessingStep(
            new CaptureProcessingStepMetadata("storage", "Storage", 100),
            new NoOpFileStorageProcessingStepOptions
            {
                UpdateLatestFrame = true,
                Policies = [new ArtifactStoragePolicyOptions { Role = FrameArtifactRole.Raw, RetentionDays = 60 }]
            },
            latest.Object,
            storage.Object,
            Mock.Of<IArtifactOutbox>(),
            Options.Create(new CameraAgentHostOptions()),
            NullLogger<NoOpFileStorageProcessingStep>.Instance);

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
            var step = new NoOpFileStorageProcessingStep(
                new CaptureProcessingStepMetadata("storage", "Storage", 100),
                new NoOpFileStorageProcessingStepOptions
                {
                    StorageRoot = "/tmp/camera",
                    UpdateLatestFrame = false,
                    Policies = policyStepId is null
                        ? []
                        : [new ArtifactStoragePolicyOptions { StepId = policyStepId }]
                },
                Mock.Of<ILatestFrameAccessor>(),
                storage.Object,
                Mock.Of<IArtifactOutbox>(),
                Options.Create(new CameraAgentHostOptions()),
                NullLogger<NoOpFileStorageProcessingStep>.Instance);

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
    public async Task GlobalFrameUploadPolicyDoesNotApplyToDurableLayoutlessMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "unsupported-metadata-storage", Guid.NewGuid().ToString("N"));
        var rawRoot = Path.Combine(root, "raw");
        var archiveRoot = Path.Combine(root, "archive");
        Directory.CreateDirectory(rawRoot);
        try
        {
            var payload = new byte[] { 0, 0, 0, 0 };
            var manifest = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono8, 2, 2, 2, payload);
            var receipt = new RawCaptureReceipt(
                RawIngressOutcome.Committed,
                manifest,
                new StoredFrameReference(
                    "raw.bin",
                    Path.Combine(rawRoot, "raw.bin"),
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
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = rawRoot,
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
            });
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(hostOptions);
            var persistence = new CaptureProcessingPersistence(
                hostOptions,
                store,
                storage.Object,
                telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance);
            var step = new NoOpFileStorageProcessingStep(
                new CaptureProcessingStepMetadata("storage", "Storage", 100),
                new NoOpFileStorageProcessingStepOptions
                {
                    StorageRoot = archiveRoot,
                    QueueForUpload = true,
                    UpdateLatestFrame = false
                },
                Mock.Of<ILatestFrameAccessor>(),
                storage.Object,
                outbox.Object,
                hostOptions,
                NullLogger<NoOpFileStorageProcessingStep>.Instance,
                persistence);

            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

            storage.VerifyNoOtherCalls();
            outbox.VerifyNoOtherCalls();
            Assert.HasCount(2, Directory.EnumerateFiles(archiveRoot, "*", SearchOption.AllDirectories));
            Assert.IsEmpty(context.ProcessingOutcomes);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TypedLayoutlessMetadataPersistsAndQueuesThroughDurableOutbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "structured-storage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rawPayload = new byte[] { 0, 0, 0, 0 };
            var rawManifest = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono8, 2, 2, 2, rawPayload);
            var receipt = new RawCaptureReceipt(
                RawIngressOutcome.Committed,
                rawManifest,
                new StoredFrameReference(
                    "raw.bin",
                    Path.Combine(root, "raw.bin"),
                    rawManifest.Descriptor.Timing.ExposureStartedUtc,
                    FrameArtifactRole.Raw),
                CaptureContractJson.ComputeManifestSha256(rawManifest));
            var context = CreateContext(receipt);
            var raw = context.Artifacts!.Raw;
            var processingRaw = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, "raw-source");
            var layerPayload = PresentationLayerPayloadJson.Create(
                processingRaw.ContentIdentitySha256 ?? processingRaw.DescriptorIdentitySha256 ??
                    ProcessingIdentity.ComputePayloadSha256(processingRaw.Payload), 2, 2);
            var product = PresentationProcessingProducts.CreateLayerProduct(
                layerPayload, "scene-layer", [processingRaw], "storage-test-v1");
            var artifactId = CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256);
            context.RestoreProduct("layer", artifactId, product);
            context.BeginNode("storage", ["layer"]);

            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
            });
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(hostOptions);
            var frameStorage = new Mock<IFrameStorageService>(MockBehavior.Strict);
            var persistence = new CaptureProcessingPersistence(
                hostOptions,
                store,
                frameStorage.Object,
                telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance);
            using (var outbox = new SqliteArtifactOutbox())
            {
                var step = new NoOpFileStorageProcessingStep(
                    new CaptureProcessingStepMetadata("storage", "Storage", 100),
                    new NoOpFileStorageProcessingStepOptions
                    {
                        StorageRoot = root,
                        QueueForUpload = true,
                        UpdateLatestFrame = false
                    },
                    Mock.Of<ILatestFrameAccessor>(),
                    frameStorage.Object,
                    outbox,
                    hostOptions,
                    NullLogger<NoOpFileStorageProcessingStep>.Instance,
                    persistence);

                await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            }

            using var restarted = new SqliteArtifactOutbox();
            var lease = await restarted.ClaimAsync(
                root, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            Assert.AreEqual(ArtifactOutboxManifestKind.StructuredProductV1, lease.Record.ManifestKind);
            var queued = lease.Record.ProductManifest!;
            Assert.AreEqual(product.OutputIdentitySha256, queued.Descriptor.OutputIdentitySha256);
            Assert.AreEqual(layerPayload.ContentIdentitySha256, queued.Descriptor.ContentIdentitySha256);
            Assert.IsTrue(File.Exists(Path.Combine(root,
                queued.RelativeArtifactPath.Replace('/', Path.DirectorySeparatorChar))));
            var hold = await restarted.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, hold);
            Assert.IsTrue(File.Exists(Path.Combine(root,
                hold[0].RelativeSidecarPath.Replace('/', Path.DirectorySeparatorChar))));
            frameStorage.VerifyNoOtherCalls();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
