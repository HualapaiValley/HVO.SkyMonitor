using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[DoNotParallelize]
public sealed class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task NonGraphWorker_ValidRawDoesNotInvalidateEvidence()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var recovery = new RecordingRecoveryControl();

            await RunWorkerAsync(fixture.Item, new RawObservingStep(), recovery, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsFalse(recovery.Invalidated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NonGraphWorker_MissingRawInvalidatesEvidence()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            File.Delete(fixture.Item.RawCapture!.StoredFrame.AbsolutePath);
            var recovery = new RecordingRecoveryControl();

            await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () =>
                await RunWorkerAsync(fixture.Item, new RawObservingStep(), recovery, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            Assert.IsTrue(recovery.Invalidated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NonGraphWorker_CorruptRawInvalidatesEvidence()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            await File.WriteAllBytesAsync(fixture.Item.RawCapture!.StoredFrame.AbsolutePath, [1]).ConfigureAwait(false);
            var recovery = new RecordingRecoveryControl();

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await RunWorkerAsync(fixture.Item, new RawObservingStep(), recovery, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            Assert.IsTrue(recovery.Invalidated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NonGraphWorker_CancellationPropagatesWithoutInvalidatingEvidence()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var recovery = new RecordingRecoveryControl();
            using var cancellation = new CancellationTokenSource();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await RunWorkerAsync(fixture.Item, new CancelingStep(cancellation), recovery, cancellation.Token)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            Assert.IsFalse(recovery.Invalidated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task DescriptorOnlyStep_DoesNotOpenOrDecodeRawContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            File.Delete(fixture.Item.RawCapture!.StoredFrame.AbsolutePath);
            var step = new DescriptorOnlyStep();
            using var telemetry = new CaptureProcessingTelemetry();

            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item,
                new CaptureProcessingGraph([CreateNode(step)]),
                null,
                telemetry,
                1,
                NullLogger.Instance,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            Assert.AreEqual(fixture.Manifest.Descriptor.Artifact.ArtifactId, step.ArtifactId);
            Assert.IsFalse(step.SawRawFrame);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PixelStepThenDescriptorStep_DescriptorApiExposesNoPixels()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var descriptor = new DescriptorOnlyStep();
            using var telemetry = new CaptureProcessingTelemetry();
            var pixel = new ProducingStep();

            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item,
                new CaptureProcessingGraph([
                    CreateNode(pixel),
                    CreateNode(descriptor) with { Dependencies = ["normalize"] }
                ]),
                null,
                telemetry,
                1,
                NullLogger.Instance,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            Assert.AreEqual(1, pixel.ExecutionCount);
            Assert.IsFalse(descriptor.SawRawFrame);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DescriptorOnlyContext_PublicApiIsTransitivelyPathAndPayloadFree()
    {
        var forbidden = new HashSet<Type>
        {
            typeof(RawCaptureReceipt),
            typeof(StoredFrameReference),
            typeof(CameraFrame),
            typeof(FileInfo),
            typeof(DirectoryInfo),
            typeof(Stream),
            typeof(ReadOnlyMemory<byte>),
            typeof(Memory<byte>),
            typeof(byte[])
        };
        var visited = new HashSet<Type>();
        var pending = new Queue<Type>([typeof(CaptureDescriptorProcessingContext)]);
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(type))
            {
                continue;
            }
            Assert.IsFalse(forbidden.Any(candidate => candidate.IsAssignableFrom(type)), type.FullName);
            if (type.Assembly != typeof(CaptureDescriptorProcessingContext).Assembly || type.IsEnum || type == typeof(string))
            {
                continue;
            }
            foreach (var memberType in type.GetProperties().Select(static property => property.PropertyType)
                         .Concat(type.GetMethods().Where(static method => !method.IsSpecialName)
                             .SelectMany(static method => method.GetParameters().Select(parameter => parameter.ParameterType)
                                 .Append(method.ReturnType))))
            {
                foreach (var expanded in Expand(memberType))
                {
                    pending.Enqueue(expanded);
                }
            }
        }

        static IEnumerable<Type> Expand(Type type)
        {
            yield return type;
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments().SelectMany(Expand))
                {
                    yield return argument;
                }
            }
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PixelBearingStep_ReconstructsRawOnFirstDemand()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                fixture.Item.RawCapture!.StoredFrame.AbsolutePath,
                [1]).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([CreateNode(new ProducingStep())]),
                    null,
                    telemetry,
                    1,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MemoryOnlyCompletedNode_IsNotCommittedAndReexecutesAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var policy = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.MemoryOnly);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var step = new ProducingStep();
                var node = CreateNode(step) with { Publication = policy };
                using var telemetry = new CaptureProcessingTelemetry();
                using var store = new SqliteCaptureProcessingStore(fixture.Options);
                using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);

                var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([node]),
                    CreatePersistence(fixture.Options, store, storage, telemetry),
                    telemetry,
                    attempt,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                Assert.AreEqual(1, step.ExecutionCount);
                Assert.IsNull(await store.ReadNodeAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId,
                    node.Id,
                    CancellationToken.None).ConfigureAwait(false));
            }
            Assert.AreEqual(1, Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PostCommitCleanupFailure_DoesNotChangeCommittedResultOrBlockDependentNode()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var producer = new PostCommitFailingProducingStep();
            var dependent = new ProducingStep("dependent", "dependent");
            var first = new CaptureProcessingGraphNode(
                "first", producer, [], true, producer.RecipeName, producer.OutputRole, producer.OutputVariant,
                new string('1', 64));
            var second = new CaptureProcessingGraphNode(
                "second", dependent, ["first"], true, dependent.RecipeName, dependent.OutputRole,
                dependent.OutputVariant, new string('2', 64));
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);

            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, new CaptureProcessingGraph([first, second]),
                CreatePersistence(fixture.Options, store, storage, telemetry), telemetry, 1,
                NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            Assert.AreEqual(1, dependent.ExecutionCount);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, (await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, "first", CancellationToken.None)
                .ConfigureAwait(false))!.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task MemoryOnlySuccessfulNode_InvokesCompletionCallback()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var step = new RecordingPostCommitProducingStep();
            var node = new CaptureProcessingGraphNode(
                "memory", step, [], true, step.RecipeName, step.OutputRole, step.OutputVariant,
                new string('3', 64), Publication: new CaptureProcessingPublicationPolicy(
                    CaptureProcessingPersistenceMode.MemoryOnly));
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);

            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, new CaptureProcessingGraph([node]),
                CreatePersistence(fixture.Options, store, storage, telemetry), telemetry, 1,
                NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            Assert.AreEqual(1, step.CallbackCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DurableJpegProducts_RestoreExactlyWithMediaCorrectPathsAndNoIntermediatePayloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var policy = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
            var variants = new[] { "annotated-final-jpeg", "annotated-thumbnail-1024-jpeg", "annotated-thumbnail-320-jpeg" };
            var firstSteps = variants.Select(variant => new JpegProducingStep(variant)).ToArray();
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var graph = new CaptureProcessingGraph(firstSteps.Select(step => new CaptureProcessingGraphNode(
                    step.Name,
                    step,
                    [],
                    true,
                    step.RecipeName,
                    step.OutputRole,
                    step.OutputVariant,
                    new string(step.OutputVariant[0], 64),
                    Publication: policy)).ToArray());
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item, graph, CreatePersistence(fixture.Options, store, storage, telemetry), telemetry,
                    1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            }

            var restartedSteps = variants.Select(variant => new JpegProducingStep(variant)).ToArray();
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var nodes = restartedSteps.Select(step => new CaptureProcessingGraphNode(
                    step.Name,
                    step,
                    [],
                    true,
                    step.RecipeName,
                    step.OutputRole,
                    step.OutputVariant,
                    new string(step.OutputVariant[0], 64),
                    Publication: policy)).ToArray();
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph(nodes),
                    CreatePersistence(fixture.Options, store, storage, telemetry),
                    telemetry,
                    2,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                foreach (var node in nodes)
                {
                    var durable = await store.ReadNodeAsync(
                        fixture.Manifest.Descriptor.Capture.CaptureId,
                        node.Id,
                        CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(durable);
                    Assert.HasCount(1, durable.Outputs);
                    StringAssert.EndsWith(
                        durable.Outputs[0].PayloadRelativePath,
                        ".jpg",
                        StringComparison.Ordinal);
                    var manifest = Assert.IsInstanceOfType<DurableEncodedProductManifestV2>(
                        durable.Outputs[0].ProductManifest);
                    Assert.AreEqual(DurableEncodedProductManifestV2.CurrentSchemaVersion, manifest.SchemaVersion);
                    Assert.AreEqual(node.Id, manifest.ProducerStepId);
                    var decoded = JpegImageCodec.DecodeJpeg(await File.ReadAllBytesAsync(
                        Path.Combine(root, durable.Outputs[0].PayloadRelativePath)).ConfigureAwait(false));
                    Assert.AreEqual(decoded.Width, manifest.EncodedWidth);
                    Assert.AreEqual(decoded.Height, manifest.EncodedHeight);
                    Assert.AreEqual(decoded.PixelFormat, manifest.EncodedPixelFormat);
                }
            }

            Assert.IsTrue(restartedSteps.All(static step => step.ExecutionCount == 0));
            Assert.AreEqual(1, Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Count());
            Assert.AreEqual(3, Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories).Count());
            Assert.IsFalse(Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                .Any(path => path.Contains("derived", StringComparison.Ordinal) &&
                    !path.EndsWith(".manifest.json", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DeploymentShapedFlow_PersistsOnlyRawFinalAndTwoThumbnailsAndQueuesOnlyRaw()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var memory = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.MemoryOnly);
            var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
            var configuredSteps = new[]
            {
                new CaptureProcessingStepConfig("Test", "annotation", DependsOn: ["$raw"], Publication: memory),
                new CaptureProcessingStepConfig("Test", "quality", DependsOn: ["$raw"], Required: false, Publication: memory),
                new CaptureProcessingStepConfig("JpegEncoding", "final-jpeg", DependsOn: ["annotation"], Publication: durable),
                new CaptureProcessingStepConfig("JpegEncoding", "thumbnail-large", DependsOn: ["annotation"], Publication: durable),
                new CaptureProcessingStepConfig("JpegEncoding", "thumbnail-small", DependsOn: ["annotation"], Publication: durable),
                new CaptureProcessingStepConfig("Storage", "storage", DependsOn:
                    ["annotation", "quality", "final-jpeg", "thumbnail-large", "thumbnail-small"])
            };
            var config = fixture.Item.Config with
            {
                ProcessingSteps = null,
                Pipeline = new CapturePipelineConfig(
                    configuredSteps,
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var item = fixture.Item with { Config = config };
            var adapter = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
            var annotation = new PackedAnnotationProducingStep();
            var quality = new MetadataProducingStep();
            var jpegSteps = new[]
            {
                new JpegEncodingCaptureProcessingStep(
                    new CaptureProcessingStepMetadata("final-jpeg", "JpegEncoding", 80),
                    new JpegEncodingProcessingStepOptions
                    {
                        OutputVariant = "annotated-final-jpeg",
                        JpegQuality = 90
                    },
                    adapter),
                new JpegEncodingCaptureProcessingStep(
                    new CaptureProcessingStepMetadata("thumbnail-large", "JpegEncoding", 81),
                    new JpegEncodingProcessingStepOptions
                    {
                        OutputVariant = "annotated-thumbnail-1024-jpeg",
                        JpegQuality = 85,
                        MaximumDimension = 1024
                    },
                    adapter),
                new JpegEncodingCaptureProcessingStep(
                    new CaptureProcessingStepMetadata("thumbnail-small", "JpegEncoding", 82),
                    new JpegEncodingProcessingStepOptions
                    {
                        OutputVariant = "annotated-thumbnail-320-jpeg",
                        JpegQuality = 80,
                        MaximumDimension = 320
                    },
                    adapter)
            };
            var frameStorage = new Mock<IFrameStorageService>(MockBehavior.Strict);
            using var outbox = new SqliteArtifactOutbox();
            var latest = new LatestFrameAccessor();
            var storageStep = new NoOpFileStorageProcessingStep(
                new CaptureProcessingStepMetadata("storage", "Storage", 90),
                new NoOpFileStorageProcessingStepOptions
                {
                    StorageRoot = root,
                    RetentionDays = 1,
                    UpdateLatestFrame = true,
                    QueueForUpload = false
                },
                latest,
                frameStorage.Object,
                outbox,
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = root,
                    CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
                }),
                NullLogger<NoOpFileStorageProcessingStep>.Instance);
            var graph = new CaptureProcessingGraph([
                new CaptureProcessingGraphNode(
                    "annotation", annotation, [], true, annotation.RecipeName,
                    annotation.OutputRole, annotation.OutputVariant, new string('A', 64), Publication: memory),
                new CaptureProcessingGraphNode(
                    "quality", quality, [], false, quality.RecipeName,
                    quality.OutputRole, quality.OutputVariant, new string('Q', 64), Publication: memory),
                .. jpegSteps.Select(step => new CaptureProcessingGraphNode(
                    step.Name, step, ["annotation"], true, step.RecipeName,
                    step.OutputRole, step.OutputVariant, new string(step.Name[0], 64), Publication: durable)),
                new CaptureProcessingGraphNode(
                    "storage", storageStep,
                    ["annotation", "quality", "final-jpeg", "thumbnail-large", "thumbnail-small"],
                    true, null, null, null, new string('S', 64))
            ]);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var durableStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);

            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                item,
                graph,
                CreatePersistence(fixture.Options, store, durableStorage, telemetry),
                telemetry,
                1,
                NullLogger.Instance,
                CancellationToken.None).ConfigureAwait(false);
            var upload = new UploadCaptureLaneHandler(
                outbox,
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
            var uploadResult = await upload.HandleAsync(new CaptureLaneHandlerContext(
                "upload", 1, config, item.Submission, fixture.Item.RawCapture!), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, uploadResult.Outcome);
            Assert.AreEqual(1, Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Count());
            Assert.AreEqual(3, Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories).Count());
            Assert.IsFalse(Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                .Any(path => path.Contains("derived", StringComparison.Ordinal) &&
                    !path.EndsWith(".manifest.json", StringComparison.Ordinal)));
            Assert.IsTrue(latest.TryGetSnapshot(out var latestFrame));
            Assert.AreEqual(CameraPixelFormat.Mono8, latestFrame!.PixelFormat);
            Assert.AreEqual(annotation.OutputVariant, latestFrame.RecipeVersion);
            frameStorage.VerifyNoOtherCalls();
            var outboxRecords = outbox.List(root, 10);
            Assert.HasCount(1, outboxRecords);
            Assert.AreEqual(FrameArtifactRole.Raw, outboxRecords[0].Role);
            Assert.AreEqual(fixture.Manifest.Descriptor.Artifact.ArtifactId, outboxRecords[0].ArtifactId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MemoryOnlyRetryThenSuccess_ClearsStaleStateAndReexecutesAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var policy = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.MemoryOnly);
            var step = new RetryThenCompleteStep();
            CaptureProcessingGraphNode Node(ICaptureProcessingStep value) => new(
                "memory", value, [], true, null, null, null, new string('Y', 64), Publication: policy);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);

            var retry = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, new CaptureProcessingGraph([Node(step)]), persistence, telemetry,
                1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, retry.Outcome);
            Assert.AreEqual(DurableProcessingNodeStatus.RetryableFailure, (await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, "memory", CancellationToken.None).ConfigureAwait(false))!.Status);

            var success = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, new CaptureProcessingGraph([Node(step)]), persistence, telemetry,
                2, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, success.Outcome);
            Assert.IsNull(await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, "memory", CancellationToken.None).ConfigureAwait(false));

            var restarted = new CountingStep();
            var replay = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, new CaptureProcessingGraph([Node(restarted)]), persistence, telemetry,
                3, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, replay.Outcome);
            Assert.AreEqual(1, restarted.ExecutionCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("Skipped")]
    [DataRow("TerminalFailure")]
    [DataRow("RetryableFailure")]
    [TestCategory("Integration")]
    public async Task MemoryOnlyNode_IgnoresAndClearsEveryOutputlessStaleStatus(
        string staleStatusName)
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var staleStatus = Enum.Parse<DurableProcessingNodeStatus>(staleStatusName);
            var step = new CountingStep();
            var node = new CaptureProcessingGraphNode(
                "memory",
                step,
                [],
                true,
                null,
                null,
                null,
                new string('Z', 64),
                Publication: new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.MemoryOnly));
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            await store.WriteNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId,
                node,
                staleStatus,
                "stale",
                1,
                fixture.Manifest.Descriptor.Profiles.Processing.Sha256,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                TimeSpan.Zero,
                staleStatus switch
                {
                    DurableProcessingNodeStatus.Skipped => ProcessingOutcomeStatus.Skipped,
                    DurableProcessingNodeStatus.TerminalFailure => ProcessingOutcomeStatus.TerminalFailure,
                    _ => ProcessingOutcomeStatus.RetryableFailure
                },
                [],
                0,
                null,
                [],
                CancellationToken.None).ConfigureAwait(false);

            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item,
                new CaptureProcessingGraph([node]),
                CreatePersistence(fixture.Options, store, storage, telemetry),
                telemetry,
                2,
                NullLogger.Instance,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            Assert.AreEqual(1, step.ExecutionCount);
            Assert.IsNull(await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId,
                node.Id,
                CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MalformedJpeg_IsRejectedBeforeCommitAndDuringRestore()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var policy = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
            CaptureProcessingGraphNode Node(JpegProducingStep step) => new(
                step.Name, step, [], true, step.RecipeName, step.OutputRole, step.OutputVariant,
                new string('J', 64), Publication: policy);

            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await FrameProcessingWorker.ProcessGraphItemAsync(
                        fixture.Item,
                        new CaptureProcessingGraph([Node(new JpegProducingStep("malformed", malformed: true))]),
                        CreatePersistence(fixture.Options, store, storage, telemetry),
                        telemetry,
                        1,
                        NullLogger.Instance,
                        CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            }
            Assert.IsFalse(Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories).Any());

            DurableProcessingOutput output;
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var step = new JpegProducingStep("restore-malformed");
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item, new CaptureProcessingGraph([Node(step)]),
                    CreatePersistence(fixture.Options, store, storage, telemetry), telemetry,
                    1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                output = (await store.ReadNodeAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId,
                    step.Name,
                    CancellationToken.None).ConfigureAwait(false))!.Outputs.Single();
            }

            var malformed = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
            var encodedManifest = Assert.IsInstanceOfType<DurableEncodedProductManifestV2>(output.ProductManifest);
            var falseV1 = new DurableProcessingProductManifestV1(
                DurableProcessingProductManifestV1.CurrentSchemaVersion,
                encodedManifest.Capture,
                encodedManifest.Artifact,
                encodedManifest.OutputIdentitySha256,
                encodedManifest.Algorithms,
                encodedManifest.Compatibility,
                encodedManifest.TotalIntegrationTicks,
                encodedManifest.ByteLength,
                encodedManifest.RelativeArtifactPath,
                encodedManifest.Layout);
            Assert.ThrowsExactly<InvalidDataException>(() =>
                DurableProcessingProductManifestJson.Serialize(falseV1));

            async Task CommitEvidenceAsync(DurableEncodedProductManifestV2 manifest, byte[] payload)
            {
                var evidence = DurableProcessingProductManifestJson.Serialize(manifest);
                await File.WriteAllBytesAsync(Path.Combine(root, output.PayloadRelativePath), payload).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(root, output.SidecarRelativePath), evidence).ConfigureAwait(false);
                using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE processing_outputs SET descriptor_json = $evidence WHERE output_identity_sha256 = $identity;";
                command.Parameters.AddWithValue("$evidence", evidence);
                command.Parameters.AddWithValue("$identity", output.OutputIdentitySha256);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            async Task AssertRestoreRejectedAsync(int attempt)
            {
                using var restartedTelemetry = new CaptureProcessingTelemetry();
                using var restartedStore = new SqliteCaptureProcessingStore(fixture.Options);
                using var restartedStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await FrameProcessingWorker.ProcessGraphItemAsync(
                        fixture.Item,
                        new CaptureProcessingGraph([Node(new JpegProducingStep("restore-malformed"))]),
                        CreatePersistence(fixture.Options, restartedStore, restartedStorage, restartedTelemetry),
                        restartedTelemetry,
                        attempt,
                        NullLogger.Instance,
                        CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            }

            var validPayload = await File.ReadAllBytesAsync(Path.Combine(root, output.PayloadRelativePath)).ConfigureAwait(false);
            await CommitEvidenceAsync(
                encodedManifest with { EncodedWidth = encodedManifest.EncodedWidth + 1 },
                validPayload).ConfigureAwait(false);
            await AssertRestoreRejectedAsync(2).ConfigureAwait(false);

            var malformedManifest = encodedManifest with
            {
                Artifact = encodedManifest.Artifact with
                {
                    ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(malformed)
                },
                ByteLength = malformed.Length
            };
            await CommitEvidenceAsync(malformedManifest, malformed).ConfigureAwait(false);
            await AssertRestoreRejectedAsync(3).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task CompletedNode_RestartRestoresExactOutputWithoutReexecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(
                root,
                includeCycleEvidence: true,
                includeSceneEvidence: true).ConfigureAwait(false);
            CaptureLaneHandlerResult first;
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
                var firstStep = new ProducingStep();
                var firstNode = CreateNode(firstStep);
                first = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([firstNode]),
                    persistence,
                    telemetry,
                    1,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(1, firstStep.ExecutionCount);
            }

            CaptureLaneHandlerResult second;
            DurableProcessingNode? durable;
            var restartedStep = new ProducingStep();
            var restartedNode = CreateNode(restartedStep);
            var inspector = new RestoredFrameInspectingStep(fixture.Manifest.Scene!);
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                second = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([
                        restartedNode,
                        new CaptureProcessingGraphNode(
                            "inspect", inspector, [restartedNode.Id], true, null, null, null, new string('I', 64))
                    ]),
                    CreatePersistence(fixture.Options, store, storage, telemetry),
                    telemetry,
                    2,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
                durable = await store.ReadNodeAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId,
                    restartedNode.Id,
                    CancellationToken.None).ConfigureAwait(false);
            }

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, first.Outcome);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, second.Outcome);
            Assert.AreEqual(0, restartedStep.ExecutionCount);
            Assert.IsTrue(inspector.SawExpectedScene);
            Assert.IsNotNull(durable);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, durable.Status);
            Assert.AreEqual(
                fixture.Manifest.Descriptor.Profiles.Processing.Sha256,
                durable.ProcessingProfileIdentitySha256);
            Assert.IsNotNull(durable.StartedUtc);
            Assert.IsTrue(durable.Duration >= TimeSpan.Zero);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, durable.Outcome);
            Assert.IsNotNull(durable.Inputs);
            Assert.IsEmpty(durable.Inputs);
            Assert.HasCount(1, durable.Outputs);
            var output = durable.Outputs[0];
            Assert.AreEqual(fixture.Manifest.Descriptor.CycleEvidence, output.Descriptor!.CycleEvidence);
            Assert.AreEqual(fixture.Manifest.Descriptor.Artifact.ArtifactId, output.Descriptor.Artifact.SourceArtifactIds.Single());
            Assert.IsTrue(File.Exists(Path.Combine(root, output.PayloadRelativePath)));
            Assert.IsTrue(File.Exists(Path.Combine(root, output.SidecarRelativePath)));
            var sidecar = CaptureContractJson.ParseManifest(
                await File.ReadAllBytesAsync(Path.Combine(root, output.SidecarRelativePath)).ConfigureAwait(false));
            Assert.IsTrue(sidecar.IsValid, sidecar.Validation.ReasonCode);
            CollectionAssert.AreEqual(
                output.EvidenceJson,
                await File.ReadAllBytesAsync(Path.Combine(root, output.SidecarRelativePath)).ConfigureAwait(false));
            Assert.AreEqual(
                fixture.Manifest.Descriptor.CycleEvidence,
                sidecar.Document!.Manifest!.Descriptor.CycleEvidence);
            Assert.AreEqual(fixture.Manifest.Scene, sidecar.Document.Manifest.Scene);
            Assert.AreEqual(1, Directory.EnumerateFiles(
                Path.Combine(root, "frames"), "*.bin", SearchOption.AllDirectories).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task CompletedMetadataNode_RestartRestoresExactProductWithoutFabricatingFrame()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var firstStep = new MetadataProducingStep();
            var firstInspector = new RestoredMetadataInspectingStep();
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var first = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([
                        CreateMetadataNode(firstStep),
                        new CaptureProcessingGraphNode(
                            "inspect-first", firstInspector, ["metadata"], true, null, null, null, new string('F', 64))
                    ]),
                    CreatePersistence(fixture.Options, store, storage, telemetry),
                    telemetry,
                    1,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, first.Outcome);
                Assert.AreEqual(1, firstStep.ExecutionCount);
                Assert.IsNotNull(firstStep.Product);
                Assert.IsTrue(firstInspector.SawExactProduct);
            }

            var restartedStep = new MetadataProducingStep();
            var inspector = new RestoredMetadataInspectingStep(firstStep.Product!);
            DurableProcessingNode? durable;
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var second = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([
                        CreateMetadataNode(restartedStep),
                        new CaptureProcessingGraphNode(
                            "inspect", inspector, ["metadata"], true, null, null, null, new string('I', 64))
                    ]),
                    CreatePersistence(fixture.Options, store, storage, telemetry),
                    telemetry,
                    2,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, second.Outcome);
                durable = await store.ReadNodeAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId,
                    "metadata",
                    CancellationToken.None).ConfigureAwait(false);
            }

            Assert.AreEqual(0, restartedStep.ExecutionCount);
            Assert.IsTrue(inspector.SawExactProduct);
            Assert.IsNotNull(durable);
            Assert.AreEqual(durable.Duration, inspector.RestoredDependencyDuration);
            Assert.HasCount(1, durable.Outputs);
            var output = durable.Outputs[0];
            Assert.IsNull(output.Descriptor);
            var manifest = Assert.IsInstanceOfType<DurableTypedMetadataProductManifestV3>(output.ProductManifest);
            Assert.AreEqual(DurableTypedMetadataProductManifestV3.CurrentSchemaVersion, manifest.SchemaVersion);
            Assert.IsTrue(output.PayloadRelativePath.StartsWith("derived/", StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(Path.Combine(root, output.PayloadRelativePath)));
            Assert.IsTrue(File.Exists(Path.Combine(root, output.SidecarRelativePath)));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "frames")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UntypedMetadataPersistsAsLegacyV1()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var step = new MetadataProducingStep(typed: false);

            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item,
                new CaptureProcessingGraph([CreateMetadataNode(step)]),
                CreatePersistence(fixture.Options, store, storage, telemetry),
                telemetry,
                1,
                NullLogger.Instance,
                CancellationToken.None).ConfigureAwait(false);
            var durable = await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, "metadata", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
            Assert.IsInstanceOfType<DurableProcessingProductManifestV1>(durable!.Outputs.Single().ProductManifest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task TypedMetadataFactsAndOrderedSourcesAreQueryableWithoutPayloadParsingAfterRestart()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([CreateMetadataNode(new MetadataProducingStep())]),
                    CreatePersistence(fixture.Options, store, storage, telemetry), telemetry, 1,
                    NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            }

            using var restarted = new SqliteCaptureProcessingStore(fixture.Options);
            var products = await restarted.ReadCaptureProductsAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, "test-metadata-v1", 10,
                CancellationToken.None).ConfigureAwait(false);
            var product = products.Single();
            Assert.AreEqual(ProcessingProductKind.Metadata, product.ProductKind);
            Assert.AreEqual("test-metadata-v1", product.ProductSchemaVersion);
            Assert.IsNotNull(product.ContentIdentitySha256);
            var sources = await restarted.ReadOutputSourcesAsync(
                product.OutputIdentitySha256, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, sources);
            Assert.AreEqual(0, sources[0].Ordinal);
            Assert.AreEqual(fixture.Manifest.Descriptor.Artifact.ArtifactId, sources[0].ArtifactId);
            Assert.IsEmpty(await restarted.ReadCaptureProductsAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, "other-schema-v1", 10,
                CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
                await restarted.ReadCaptureProductsAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId, null,
                    SqliteCaptureProcessingStore.MaximumProductQueryCount + 1,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var plan = connection.CreateCommand();
            plan.CommandText = """
                EXPLAIN QUERY PLAN SELECT output_identity_sha256
                FROM processing_outputs
                WHERE capture_id = $capture AND product_schema_version = $schema
                ORDER BY output_identity_sha256 LIMIT 10;
                """;
            plan.Parameters.AddWithValue("$capture", fixture.Manifest.Descriptor.Capture.CaptureId.ToString("N"));
            plan.Parameters.AddWithValue("$schema", "test-metadata-v1");
            var details = new List<string>();
            using var reader = await plan.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) details.Add(reader.GetString(3));
            await reader.DisposeAsync().ConfigureAwait(false);
            Assert.IsTrue(details.Any(static detail => detail.Contains("ix_processing_outputs_product", StringComparison.Ordinal)),
                string.Join(Environment.NewLine, details));

            plan.CommandText = """
                EXPLAIN QUERY PLAN SELECT output_identity_sha256
                FROM processing_outputs INDEXED BY ix_processing_outputs_retention_available
                WHERE committed_unix_ms < $cutoff AND availability_state = 'Available'
                ORDER BY committed_unix_ms, output_identity_sha256 LIMIT 10;
                """;
            plan.Parameters.Clear();
            plan.Parameters.AddWithValue("$cutoff", long.MaxValue);
            details.Clear();
            using var retentionReader = await plan.ExecuteReaderAsync().ConfigureAwait(false);
            while (await retentionReader.ReadAsync().ConfigureAwait(false)) details.Add(retentionReader.GetString(3));
            Assert.IsTrue(details.Any(static detail => detail.Contains("ix_processing_outputs_retention_available", StringComparison.Ordinal)),
                string.Join(Environment.NewLine, details));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DurableLineageAcceptsAndQueriesFiveHundredTwelveOrderedSources()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var step = new MetadataProducingStep(sourceCount: LayeredPresentationJson.MaximumSourceArtifactCount);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);

            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, new CaptureProcessingGraph([CreateMetadataNode(step)]),
                CreatePersistence(fixture.Options, store, storage, telemetry), telemetry, 1,
                NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            var output = (await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, "metadata", CancellationToken.None)
                .ConfigureAwait(false))!.Outputs.Single();
            var sources = await store.ReadOutputSourcesAsync(
                output.OutputIdentitySha256, LayeredPresentationJson.MaximumSourceArtifactCount,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            Assert.HasCount(LayeredPresentationJson.MaximumSourceArtifactCount, sources);
            CollectionAssert.AreEqual(step.Product!.SourceArtifactIds.ToArray(),
                sources.Select(static source => source.ArtifactId).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MoreThanFiveHundredTwelveSourcesFailsBeforeDerivedFilesPublish()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var step = new MetadataProducingStep(sourceCount: LayeredPresentationJson.MaximumSourceArtifactCount + 1);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item, new CaptureProcessingGraph([CreateMetadataNode(step)]),
                    CreatePersistence(fixture.Options, store, storage, telemetry), telemetry, 1,
                    NullLogger.Instance, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.IsFalse(Directory.Exists(Path.Combine(root, "derived")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TypedMetadataV3_RejectsMissingNonCanonicalAndDuplicateTypedFacts()
    {
        var product = CreateTypedMetadataProduct();
        var manifest = CreateTypedMetadataManifest(product);
        var valid = DurableProcessingProductManifestJson.Serialize(manifest);

        var parsed = Assert.IsInstanceOfType<DurableTypedMetadataProductManifestV3>(
            DurableProcessingProductManifestJson.Parse(valid));
        Assert.AreEqual(manifest.Kind, parsed.Kind);
        Assert.AreEqual(manifest.ProductSchemaVersion, parsed.ProductSchemaVersion);
        Assert.AreEqual(manifest.ContentIdentitySha256, parsed.ContentIdentitySha256);
        Assert.ThrowsExactly<InvalidDataException>(() => DurableProcessingProductManifestJson.Serialize(
            manifest with { ProductSchemaVersion = null! }));
        Assert.ThrowsExactly<InvalidDataException>(() => DurableProcessingProductManifestJson.Serialize(
            manifest with { ContentIdentitySha256 = null! }));
        Assert.ThrowsExactly<InvalidDataException>(() => DurableProcessingProductManifestJson.Serialize(
            manifest with { ProductSchemaVersion = " " }));
        Assert.ThrowsExactly<InvalidDataException>(() => DurableProcessingProductManifestJson.Serialize(
            manifest with { ProductSchemaVersion = new string('x', 129) }));
        Assert.ThrowsExactly<InvalidDataException>(() => DurableProcessingProductManifestJson.Serialize(
            manifest with { ContentIdentitySha256 = new string('a', 64) }));
        Assert.ThrowsExactly<InvalidDataException>(() => DurableProcessingProductManifestJson.Serialize(
            manifest with { ContentIdentitySha256 = new string('\u00C9', 64) }));
        Assert.ThrowsExactly<InvalidDataException>(() => DurableProcessingProductManifestJson.Serialize(
            manifest with { Kind = ProcessingProductKind.PixelData }));

        var json = System.Text.Encoding.UTF8.GetString(valid);
        var duplicate = json.Replace(
            "\"productSchemaVersion\":",
            "\"PRODUCTSCHEMAVERSION\":\"duplicate\",\"productSchemaVersion\":",
            StringComparison.Ordinal);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            DurableProcessingProductManifestJson.Parse(System.Text.Encoding.UTF8.GetBytes(duplicate)));
    }

    [TestMethod]
    [DataRow("payload-path")]
    [DataRow("sidecar-path")]
    [DataRow("artifact-id")]
    [DataRow("output-identity")]
    [DataRow("recipe-identity")]
    [TestCategory("Integration")]
    public async Task LayoutlessReplay_RejectsSqliteManifestFactMismatch(string mismatch)
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([CreateMetadataNode(new MetadataProducingStep())]),
                    CreatePersistence(fixture.Options, store, storage, telemetry),
                    telemetry,
                    1,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
            }

            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE processing_outputs SET
                        payload_relative_path = CASE WHEN $case = 'payload-path' THEN $payload ELSE payload_relative_path END,
                        sidecar_relative_path = CASE WHEN $case = 'sidecar-path' THEN $sidecar ELSE sidecar_relative_path END,
                        artifact_id = CASE WHEN $case = 'artifact-id' THEN $artifact ELSE artifact_id END,
                        output_identity_sha256 = CASE WHEN $case = 'output-identity' THEN $output ELSE output_identity_sha256 END,
                        recipe_identity_sha256 = CASE WHEN $case = 'recipe-identity' THEN $recipe ELSE recipe_identity_sha256 END;
                    """;
                command.Parameters.AddWithValue("$case", mismatch);
                command.Parameters.AddWithValue("$payload", "derived/wrong.json");
                command.Parameters.AddWithValue("$sidecar", "derived/wrong.manifest.json");
                command.Parameters.AddWithValue("$artifact", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                command.Parameters.AddWithValue("$output", new string('B', 64));
                command.Parameters.AddWithValue("$recipe", new string('C', 64));
                if (mismatch == "output-identity")
                {
                    await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
                        await command.ExecuteNonQueryAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    return;
                }
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using var restartedTelemetry = new CaptureProcessingTelemetry();
            using var restartedStore = new SqliteCaptureProcessingStore(fixture.Options);
            using var restartedStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([CreateMetadataNode(new MetadataProducingStep())]),
                    CreatePersistence(fixture.Options, restartedStore, restartedStorage, restartedTelemetry),
                    restartedTelemetry,
                    2,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task FallbackProfileMismatchAndInfrastructureOutcomeRemainUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var fallbackConfig = fixture.Item.Config with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("Changed", Enabled: false)],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var item = fixture.Item with { Config = fallbackConfig };
            var step = new CountingStep();
            var node = new CaptureProcessingGraphNode(
                "infrastructure", step, [], true, null, null, null, new string('I', 64));
            DurableProcessingNode? durable;
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                    item,
                    new CaptureProcessingGraph([node]),
                    CreatePersistence(fixture.Options, store, storage, telemetry),
                    telemetry,
                    1,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
                durable = await store.ReadNodeAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId,
                    node.Id,
                    CancellationToken.None).ConfigureAwait(false);
            }

            Assert.AreEqual(1, step.ExecutionCount);
            Assert.IsNotNull(durable);
            Assert.IsNull(durable.ProcessingProfileIdentitySha256);
            Assert.IsNull(durable.Outcome);
            Assert.IsNotNull(durable.Inputs);
            Assert.IsEmpty(durable.Inputs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Integration")]
    public async Task CommittedMetadataEvidenceTampering_IsRejectedOnRestart(bool tamperPayload)
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            DurableProcessingOutput output;
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var step = new MetadataProducingStep();
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([CreateMetadataNode(step)]),
                    CreatePersistence(fixture.Options, store, storage, telemetry),
                    telemetry,
                    1,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
                var durable = await store.ReadNodeAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId,
                    "metadata",
                    CancellationToken.None).ConfigureAwait(false);
                output = durable!.Outputs.Single();
            }

            var evidencePath = Path.Combine(
                root,
                tamperPayload ? output.PayloadRelativePath : output.SidecarRelativePath);
            var committed = await File.ReadAllBytesAsync(evidencePath).ConfigureAwait(false);
            var tampered = tamperPayload
                ? committed.Select(static value => (byte)(value ^ 0x01)).ToArray()
                : [.. "\n"u8, .. committed];
            await File.WriteAllBytesAsync(evidencePath, tampered).ConfigureAwait(false);

            using var restartedTelemetry = new CaptureProcessingTelemetry();
            using var restartedStore = new SqliteCaptureProcessingStore(fixture.Options);
            using var restartedStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([CreateMetadataNode(new MetadataProducingStep())]),
                    CreatePersistence(fixture.Options, restartedStore, restartedStorage, restartedTelemetry),
                    restartedTelemetry,
                    2,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PublishedMetadataEvidenceWithoutSqliteCommit_ReplayConvergesExactEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            DurableProcessingOutput output;
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([CreateMetadataNode(new MetadataProducingStep())]),
                    CreatePersistence(fixture.Options, store, storage, telemetry),
                    telemetry,
                    1,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
                output = (await store.ReadNodeAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId,
                    "metadata",
                    CancellationToken.None).ConfigureAwait(false))!.Outputs.Single();
            }

            var payloadPath = Path.Combine(root, output.PayloadRelativePath);
            var sidecarPath = Path.Combine(root, output.SidecarRelativePath);
            var payloadBytes = await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false);
            var sidecarBytes = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
            await DeleteProcessingCommitAsync(root).ConfigureAwait(false);

            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var replay = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([CreateMetadataNode(new MetadataProducingStep())]),
                    CreatePersistence(fixture.Options, store, storage, telemetry),
                    telemetry,
                    2,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, replay.Outcome, replay.Reason);
                Assert.HasCount(1, (await store.ReadNodeAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId,
                    "metadata",
                    CancellationToken.None).ConfigureAwait(false))!.Outputs);
            }

            CollectionAssert.AreEqual(payloadBytes, await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false));
            CollectionAssert.AreEqual(sidecarBytes, await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false));
            Assert.IsFalse(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PublishedOutputWithoutSqliteCommit_ReplayConvergesExactEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var step = new ProducingStep();
            var graph = new CaptureProcessingGraph([CreateNode(step)]);

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    graph,
                    CreatePersistence(fixture.Options, store, new ThrowAfterSaveStorage(storage), telemetry),
                    telemetry,
                    1,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            var legacySidecarPath = Directory.EnumerateFiles(
                Path.Combine(root, "frames"), "*.json", SearchOption.AllDirectories).Single();
            var legacySidecar = System.Text.Json.Nodes.JsonNode.Parse(
                await File.ReadAllBytesAsync(legacySidecarPath).ConfigureAwait(false))!.AsObject();
            Assert.IsTrue(legacySidecar.Remove("producerStepId"));
            await File.WriteAllTextAsync(legacySidecarPath, legacySidecar.ToJsonString()).ConfigureAwait(false);
            var legacyParsed = CaptureContractJson.ParseManifest(
                await File.ReadAllBytesAsync(legacySidecarPath).ConfigureAwait(false));
            Assert.IsTrue(legacyParsed.IsValid, legacyParsed.Validation.ReasonCode);
            Assert.IsNull(legacyParsed.Document!.Manifest!.ProducerStepId);

            var replay = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item,
                graph,
                CreatePersistence(fixture.Options, store, storage, telemetry),
                telemetry,
                2,
                NullLogger.Instance,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, replay.Outcome);
            Assert.HasCount(1, Directory.EnumerateFiles(
                Path.Combine(root, "frames"), "*.bin", SearchOption.AllDirectories));
            var durable = await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId,
                "normalize",
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(durable);
            Assert.HasCount(1, durable.Outputs);
            Assert.IsNull(durable.Outputs[0].Descriptor!.CycleEvidence);
            var sidecar = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(
                Path.Combine(root, durable.Outputs[0].SidecarRelativePath)).ConfigureAwait(false));
            Assert.IsTrue(sidecar.IsValid, sidecar.Validation.ReasonCode);
            Assert.AreEqual("normalize", sidecar.Document!.Manifest!.ProducerStepId);
            Assert.IsNull(sidecar.Document!.Manifest!.Descriptor.CycleEvidence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task CommittedRawSidecarTampering_IsRejectedBeforeProcessing()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var sidecarPath = Path.ChangeExtension(fixture.Item.RawCapture!.StoredFrame.AbsolutePath, ".json");
            var committed = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
            var tampered = System.Text.Encoding.UTF8.GetBytes($"\n{System.Text.Encoding.UTF8.GetString(committed)}");
            Assert.IsTrue(CaptureContractJson.ParseManifest(tampered).IsValid);
            await File.WriteAllBytesAsync(
                sidecarPath,
                tampered).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([CreateNode(new ProducingStep())]),
                    null,
                    telemetry,
                    1,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task StaleLaneLease_CannotCommitProcessingNode()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
            using var store = new SqliteCaptureProcessingStore(options);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE capture_lane_work(
                        work_id INTEGER PRIMARY KEY,
                        state TEXT NOT NULL,
                        lease_token TEXT NULL,
                        lease_expires_unix_ms INTEGER NULL);
                    INSERT INTO capture_lane_work(work_id, state, lease_token, lease_expires_unix_ms)
                    VALUES (1, 'leased', 'current-token', 4102444800000);
                    """;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var step = new CountingStep();
            var node = new CaptureProcessingGraphNode(
                "node", step, [], true, null, null, null, new string('E', 64));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await store.WriteNodeAsync(
                    Guid.NewGuid(), node, DurableProcessingNodeStatus.Completed, null, 1,
                    new string('A', 64), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                    TimeSpan.Zero, ProcessingOutcomeStatus.Produced, [],
                    1, "stale-token", [], CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE capture_lane_work SET lease_expires_unix_ms = 0 WHERE work_id = 1;";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await store.WriteNodeAsync(
                    Guid.NewGuid(), node, DurableProcessingNodeStatus.Completed, null, 1,
                    new string('A', 64), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                    TimeSpan.Zero, ProcessingOutcomeStatus.Produced, [],
                    1, "current-token", [], CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RawWindowHistory_IsAgentBoundedAndRetainsLatestHundredInputs()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
            using var store = new SqliteCaptureProcessingStore(options);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE raw_captures(
                        raw_capture_row_id INTEGER PRIMARY KEY,
                        capture_id TEXT NOT NULL,
                        raw_artifact_id TEXT NOT NULL,
                        agent_id TEXT NOT NULL,
                        capture_sequence INTEGER NOT NULL,
                        payload_relative_path TEXT NOT NULL,
                        sidecar_relative_path TEXT NOT NULL,
                        manifest_json BLOB NOT NULL,
                        manifest_sha256 TEXT NOT NULL,
                        state TEXT NOT NULL);
                    CREATE TABLE capture_lane_work(
                        raw_capture_row_id INTEGER NOT NULL,
                        lane_name TEXT NOT NULL,
                        state TEXT NOT NULL);
                    """;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                for (var sequence = 1; sequence <= 101; sequence++)
                {
                    var template = ReconstructableCaptureContractTests.CreateManifest(
                        CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
                    var descriptor = template.Descriptor with
                    {
                        Capture = template.Descriptor.Capture with
                        {
                            AgentId = "agent-a",
                            CaptureSequence = sequence,
                            CaptureId = Guid.Parse($"40000000-0000-0000-0000-{sequence:D12}")
                        },
                        Artifact = template.Descriptor.Artifact with
                        {
                            ArtifactId = Guid.Parse($"50000000-0000-0000-0000-{sequence:D12}")
                        }
                    };
                    var manifest = new ArtifactManifestV2(
                        ArtifactManifestV2.CurrentSchemaVersion, descriptor, $"raw/{sequence}.bin");
                    var manifestJson = CaptureContractJson.Serialize(manifest);
                    using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO raw_captures(
                            raw_capture_row_id, capture_id, raw_artifact_id, agent_id, capture_sequence,
                            payload_relative_path, sidecar_relative_path, manifest_json, manifest_sha256, state)
                        VALUES ($row, $capture, $artifact, $agent, $sequence, $payload, $sidecar, $manifest, $manifest_sha, 'committed');
                        """;
                    insert.Parameters.AddWithValue("$row", sequence);
                    insert.Parameters.AddWithValue("$capture", descriptor.Capture.CaptureId.ToString("N"));
                    insert.Parameters.AddWithValue("$artifact", descriptor.Artifact.ArtifactId.ToString("N"));
                    insert.Parameters.AddWithValue("$agent", descriptor.Capture.AgentId);
                    insert.Parameters.AddWithValue("$sequence", sequence);
                    insert.Parameters.AddWithValue("$payload", manifest.RelativeArtifactPath);
                    insert.Parameters.AddWithValue("$sidecar", $"raw/{sequence}.json");
                    insert.Parameters.AddWithValue("$manifest", manifestJson);
                    insert.Parameters.AddWithValue("$manifest_sha", CaptureContractJson.ComputeManifestSha256(manifestJson));
                    await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            var history = await store.ReadRecentRawInputsAsync(
                "agent-a", 100, 5, CancellationToken.None).ConfigureAwait(false);
            var holds = await store.ReadRetentionHoldsAsync(CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new long[] { 100, 99, 98, 97, 96 },
                history.Select(static entry => entry.Descriptor.Capture.CaptureSequence).ToArray());
            Assert.HasCount(100, holds);
            Assert.IsFalse(holds.Any(static hold => hold.PayloadRelativePath == "raw/1.bin"));
            Assert.IsTrue(holds.Any(static hold => hold.PayloadRelativePath == "raw/101.bin"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RollingWindow_RestartContinuesFromDurableCompatibleHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = await CreateFixtureAsync(
                root, 1, "first", new byte[] { 100, 0, 100, 0, 100, 0, 100, 0 }).ConfigureAwait(false);
            await ProcessCanonicalGraphAsync(first).ConfigureAwait(false);

            var second = await CreateFixtureAsync(
                root, 2, "second", new byte[] { 44, 1, 44, 1, 44, 1, 44, 1 }).ConfigureAwait(false);
            await ProcessCanonicalGraphAsync(second).ConfigureAwait(false);

            using var store = new SqliteCaptureProcessingStore(second.Options);
            var rolling = await store.ReadNodeAsync(
                second.Manifest.Descriptor.Capture.CaptureId,
                "rolling",
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(rolling);
            Assert.HasCount(1, rolling.Outputs);
            Assert.HasCount(2, rolling.Outputs[0].Descriptor!.Artifact.SourceArtifactIds);
            using var telemetry = new CaptureProcessingTelemetry();
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var restoredHistory = await CreatePersistence(second.Options, store, storage, telemetry).ReadRecentInputsAsync(
                second.Manifest.Descriptor,
                "calibration",
                FrameArtifactRole.Calibrated,
                2,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotEmpty(restoredHistory);
            Assert.IsTrue(restoredHistory.All(input => input.ObservationStartedUtc is not null));
            Assert.IsTrue(restoredHistory.All(input => input.ObservationEndedUtc is not null));
            Assert.IsTrue(restoredHistory.All(input =>
                input.ObservationEndedUtc == input.ObservationStartedUtc!.Value.Add(input.Integration)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CaptureProcessingPersistence CreatePersistence(
        IOptions<CameraAgentHostOptions> options,
        SqliteCaptureProcessingStore store,
        IFrameStorageService storage,
        CaptureProcessingTelemetry telemetry)
        => new(options, store, storage, telemetry, NullLogger<CaptureProcessingPersistence>.Instance);

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task RunWorkerAsync(
        FrameProcessingItem item,
        ICaptureProcessingStep step,
        IRawIngressRecoveryControl recovery,
        CancellationToken cancellationToken)
    {
        var channel = new FrameProcessingChannel(2);
        await channel.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
        channel.Complete();
        await new FrameProcessingWorker(channel, [step], NullLogger.Instance, recovery)
            .RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CaptureProcessingGraphNode CreateNode(ProducingStep step)
        => new("normalize", step, [], true, step.RecipeName, step.OutputRole, step.OutputVariant, new string('D', 64));

    private static CaptureProcessingGraphNode CreateNode(DescriptorOnlyStep step)
        => new("descriptor", step, [], true, step.RecipeName, step.OutputRole, step.OutputVariant, new string('E', 64));

    private static CaptureProcessingGraphNode CreateMetadataNode(MetadataProducingStep step)
        => new("metadata", step, [], true, step.RecipeName, step.OutputRole, step.OutputVariant, new string('M', 64));

    private static ProcessingProduct CreateTypedMetadataProduct()
    {
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            "typed-test", "1.0.0", "typed-test-v1", JsonSerializer.SerializeToElement(new { })));
        var sources = new[] { Guid.Parse("10000000-0000-0000-0000-000000000001") };
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "typed-test-v1" });
        return new ProcessingProduct(
            FrameArtifactRole.Metadata,
            "typed-test",
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, "typed-test", recipe.IdentitySha256, sources),
            "application/json",
            null,
            payload,
            ProcessingIdentity.ComputePayloadSha256(payload),
            recipe,
            [],
            sources,
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"))
        {
            Kind = ProcessingProductKind.Metadata,
            SchemaVersion = "typed-test-v1",
            ContentIdentitySha256 = new string('A', 64)
        };
    }

    private static DurableTypedMetadataProductManifestV3 CreateTypedMetadataManifest(ProcessingProduct product)
    {
        var capture = new CaptureIdentityDescriptor(
            "agent", "rig", 1, Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var artifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
        using var nullDocument = JsonDocument.Parse("null");
        return new DurableTypedMetadataProductManifestV3(
            DurableTypedMetadataProductManifestV3.CurrentSchemaVersion,
            capture,
            new ArtifactDescriptor(
                artifactId, product.Role, "typed-test", product.Variant, DateTimeOffset.UnixEpoch,
                product.SourceArtifactIds, product.Recipe.Descriptor, product.MediaType, product.ChecksumSha256),
            product.OutputIdentitySha256,
            product.Algorithms,
            product.Compatibility,
            product.TotalIntegration.Ticks,
            product.Payload.Length,
            "derived/typed-test.json",
            nullDocument.RootElement.Clone(),
            product.Kind,
            product.SchemaVersion!,
            product.ContentIdentitySha256!);
    }

    private static async Task DeleteProcessingCommitAsync(string root)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM processing_outputs; DELETE FROM processing_nodes;";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OptionalTerminalNode_DoesNotBlockIndependentRequiredNode()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var optional = new OutcomeStep(ProcessingOutcome.TerminalFailure("test.optional-terminal"));
        var required = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("optional", optional, [], false, "optional", FrameArtifactRole.Metadata, "optional"),
            new CaptureProcessingGraphNode("required", required, [], true, null, null, null)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        Assert.AreEqual(1, required.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OptionalRetryableNode_CommitsIndependentWorkAndRetriesLane()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var optional = new OutcomeStep(ProcessingOutcome.RetryableFailure("test.optional-retry"));
        var required = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("optional", optional, [], false, "optional", FrameArtifactRole.Metadata, "optional"),
            new CaptureProcessingGraphNode("required", required, [], true, null, null, null)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, result.Outcome);
        Assert.AreEqual("test.optional-retry", result.Reason);
        Assert.AreEqual(1, required.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OptionalRetryableNode_OnLastAttemptDoesNotQuarantineRequiredWork()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var optional = new OutcomeStep(ProcessingOutcome.RetryableFailure("test.optional-retry"));
        var required = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("optional", optional, [], false, "optional", FrameArtifactRole.Metadata, "optional"),
            new CaptureProcessingGraphNode("required", required, [], true, null, null, null)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 5, NullLogger.Instance, CancellationToken.None, 5).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        Assert.AreEqual(1, required.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RequiredRetryableNode_ReturnsRetryWithoutExecutingDependentNode()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var retry = new OutcomeStep(ProcessingOutcome.RetryableFailure("test.retry"));
        var dependent = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("retry", retry, [], true, "retry", FrameArtifactRole.Metadata, "retry"),
            new CaptureProcessingGraphNode("dependent", dependent, ["retry"], true, null, null, null)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, result.Outcome);
        Assert.AreEqual("test.retry", result.Reason);
        Assert.AreEqual(0, dependent.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OptionalRetryableDependency_RunsDependentAfterRetrySucceeds()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var retryOnce = new RetryOnceStep();
        var dependent = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("optional", retryOnce, [], false, null, null, null),
            new CaptureProcessingGraphNode("dependent", dependent, ["optional"], true, null, null, null)
        ]);

        var first = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
        var second = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 2, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, first.Outcome);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, second.Outcome);
        Assert.AreEqual(1, dependent.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CanonicalTerminalOutcome_RemainsTerminal()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var step = new CalibrationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("calibration", "calibration", 0),
            new CalibrationProcessingStepOptions(),
            new CameraAgentRecipeExecutionAdapter(new FixedOutcomeExecutor(
                ProcessingOutcome.TerminalFailure("test.canonical-terminal"))));
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode(
                "calibration", step, [], true, step.RecipeName, step.OutputRole, step.OutputVariant)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, result.Outcome);
        Assert.AreEqual("test.canonical-terminal", result.Reason);
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string root,
        bool includeCycleEvidence = false,
        bool includeSceneEvidence = false)
    {
        var payload = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var config = CreateConfig();
        manifest = manifest with
        {
            RelativeArtifactPath = "raw.bin",
            Descriptor = manifest.Descriptor with
            {
                Profiles = manifest.Descriptor.Profiles with
                {
                    Processing = RawCaptureDescriptorFactory.CreateProcessingProfile(config)
                }
            }
        };
        if (includeCycleEvidence)
        {
            manifest = manifest with
            {
                Descriptor = manifest.Descriptor with
                {
                    CycleEvidence = CreateCycleEvidence(manifest.Descriptor)
                }
            };
        }
        SceneProvenance? scene = null;
        if (includeSceneEvidence)
        {
            scene = new SceneProvenance(
                "durable-processing-scene",
                "rig-v1",
                "fixture",
                "1",
                new string('A', 64),
                "EquidistantFisheye",
                "projection-v1",
                "astronomy-v1",
                "sensor-v1");
            manifest = manifest with { Scene = scene };
        }
        var payloadPath = Path.Combine(root, "raw.bin");
        await File.WriteAllBytesAsync(payloadPath, payload).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.ChangeExtension(payloadPath, ".json"),
            CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);
        var stored = new StoredFrameReference("raw.bin", payloadPath, manifest.Descriptor.Timing.ExposureStartedUtc, FrameArtifactRole.Raw);
        var receipt = new RawCaptureReceipt(
            RawIngressOutcome.Committed,
            manifest,
            stored,
            CaptureContractJson.ComputeManifestSha256(manifest));
        var reconstruction = FrameReconstructor.TryReconstruct(manifest.Descriptor, payload, out var frame);
        Assert.IsTrue(reconstruction.IsValid);
        if (scene is not null)
        {
            frame = frame! with { Metadata = frame.Metadata with { Scene = scene } };
        }
        var submission = CreateSubmission(frame!) with
        {
            CycleEvidence = manifest.Descriptor.CycleEvidence
        };
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressReserveBytes = 0
        });
        return new Fixture(
            manifest,
            options,
            new FrameProcessingItem(config, submission, receipt));
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string root,
        long sequence,
        string suffix,
        byte[] payload)
    {
        var template = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var captureId = Guid.Parse($"20000000-0000-0000-0000-{sequence:D12}");
        var artifactId = Guid.Parse($"30000000-0000-0000-0000-{sequence:D12}");
        var descriptor = template.Descriptor with
        {
            Capture = template.Descriptor.Capture with
            {
                CaptureSequence = sequence,
                CaptureId = captureId
            },
            Timing = template.Descriptor.Timing with
            {
                ExposureEndedUtc = template.Descriptor.Timing.ExposureStartedUtc,
                ReadoutCompletedUtc = template.Descriptor.Timing.ExposureStartedUtc
            },
            Artifact = template.Descriptor.Artifact with { ArtifactId = artifactId }
        };
        var manifest = new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion,
            descriptor,
            $"{suffix}.bin");
        var payloadPath = Path.Combine(root, $"{suffix}.bin");
        await File.WriteAllBytesAsync(payloadPath, payload).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.ChangeExtension(payloadPath, ".json"),
            CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);
        var reconstruction = FrameReconstructor.TryReconstruct(descriptor, payload, out var frame);
        Assert.IsTrue(reconstruction.IsValid);
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
        return new Fixture(
            manifest,
            options,
            new FrameProcessingItem(
                CreateConfig(),
                CreateSubmission(frame!),
                new RawCaptureReceipt(
                    RawIngressOutcome.Committed,
                    manifest,
                    new StoredFrameReference($"{suffix}.bin", payloadPath, descriptor.Timing.ExposureStartedUtc, FrameArtifactRole.Raw),
                    CaptureContractJson.ComputeManifestSha256(manifest))));
    }

    private static async Task ProcessCanonicalGraphAsync(Fixture fixture)
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        var adapter = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var calibration = new CalibrationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("calibration", "calibration", 0),
            new CalibrationProcessingStepOptions(),
            adapter);
        var rolling = new RollingCombinationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("rolling", "rolling", 1),
            new RollingCombinationProcessingStepOptions { WindowSize = 2 },
            adapter);
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode(
                "calibration", calibration, [], true, calibration.RecipeName,
                calibration.OutputRole, calibration.OutputVariant, new string('C', 64)),
            new CaptureProcessingGraphNode(
                "rolling", rolling, ["calibration"], true, rolling.RecipeName,
                rolling.OutputRole, rolling.OutputVariant, new string('R', 64))
        ]);
        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            fixture.Item,
            graph,
            CreatePersistence(fixture.Options, store, storage, telemetry),
            telemetry,
            1,
            NullLogger.Instance,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
    }

    private static FrameProcessingItem CreateEphemeralItem()
    {
        var frame = new CameraFrame(
            DateTimeOffset.UnixEpoch,
            2,
            2,
            CameraPixelFormat.Mono8,
            new byte[4],
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
        return new FrameProcessingItem(CreateConfig(), CreateSubmission(frame));
    }

    private static CameraModuleConfig CreateConfig()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Test", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            AgentId: "agent-test");

    private static CaptureLoopSubmission CreateSubmission(CameraFrame frame)
        => new(
            new CaptureRequest(frame.TimestampUtc, frame.Metadata.Exposure, CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, null), TimeSpan.Zero, CaptureMode.Still, false),
            frame.TimestampUtc,
            frame.Metadata.Exposure,
            TimeSpan.Zero);

    private static CaptureCycleEvidence CreateCycleEvidence(ReconstructionDescriptor descriptor)
    {
        var decisionStartedUtc = descriptor.Timing.ReadoutCompletedUtc.AddMilliseconds(100);
        return new CaptureCycleEvidence(
            CaptureCadenceMode.MinimumStartInterval,
            CaptureStartReason.DeadlineReached,
            AutomaticControlOwnership.Disabled,
            AutomaticControlOwnership.Disabled,
            null,
            descriptor.Timing.RequestedStartUtc.AddMilliseconds(500),
            TimeSpan.FromSeconds(1),
            null,
            new CaptureControlDecisionEvidence(
                decisionStartedUtc,
                decisionStartedUtc.AddMilliseconds(100),
                descriptor.Controls.EffectiveExposure,
                descriptor.Controls.EffectiveGain,
                descriptor.Controls.EffectiveExposure,
                descriptor.Controls.EffectiveGain,
                CaptureControlDecisionReason.Disabled),
            decisionStartedUtc.AddMilliseconds(200));
    }

    private class ProducingStep(string name = "normalize", string outputVariant = "none") :
        ICaptureProcessingStep, ICaptureProcessingGraphStep
    {
        public bool Enabled => true;
        public int ExecutionCount { get; private set; }
        public string Name => name;
        public int Order => 0;
        public string RecipeName => "test-normalization";
        public FrameArtifactRole OutputRole => FrameArtifactRole.Calibrated;
        public string OutputVariant => outputVariant;
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            var raw = context.Artifacts!.Raw;
            var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                RecipeName, "1.0.0", "test-v1", JsonSerializer.SerializeToElement(new { mode = "none" })));
            var sources = new[] { raw.ArtifactId };
            var payload = raw.Frame.PixelData.ToArray();
            var product = new ProcessingProduct(
                OutputRole,
                OutputVariant,
                ProcessingIdentity.CreateOutputIdentity(OutputRole, OutputVariant, recipe.IdentitySha256, sources),
                "application/x-hvo-linear-frame",
                context.RawCapture!.Manifest.Descriptor.Layout,
                payload,
                ProcessingIdentity.ComputePayloadSha256(payload),
                recipe,
                [new ProcessingAlgorithmIdentity("test", "v1")],
                sources,
                raw.Frame.Metadata.Exposure,
                CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, "source").Compatibility);
            var artifact = context.AddDerivative(
                OutputRole,
                raw.Frame with { Metadata = raw.Frame.Metadata with { SourceId = "normalize" } },
                "test-v1",
                sources,
                CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
            context.AssociateProcessingProduct(artifact, product);
            context.AddProcessingOutcome(ProcessingOutcome.Produced(product));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PostCommitFailingProducingStep : ProducingStep, IDurableCaptureProcessingPostCommit
    {
        public ValueTask OnCommittedAsync(
            CaptureDescriptorProcessingContext context,
            CancellationToken cancellationToken) => ValueTask.FromException(new IOException("cleanup-failure"));
    }

    private sealed class RecordingPostCommitProducingStep : ProducingStep, IDurableCaptureProcessingPostCommit
    {
        public int CallbackCount { get; private set; }

        public ValueTask OnCommittedAsync(
            CaptureDescriptorProcessingContext context,
            CancellationToken cancellationToken)
        {
            CallbackCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RawObservingStep : ICaptureProcessingStep
    {
        public string Name => "raw-observer";
        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            Assert.IsNotNull(context.Artifacts?.Raw.Frame);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelingStep(CancellationTokenSource cancellation) : ICaptureProcessingStep
    {
        public string Name => "cancel";
        public int Order => 0;

        public async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class RecordingRecoveryControl : IRawIngressRecoveryControl
    {
        public bool Invalidated { get; private set; }

        public void InvalidateEvidence() => Invalidated = true;
    }

    private sealed class DescriptorOnlyStep : IDescriptorOnlyCaptureProcessingStep, ICaptureProcessingGraphStep
    {
        public bool Enabled => true;
        public string Name => "descriptor";
        public int Order => 0;
        public string RecipeName => "test-descriptor";
        public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
        public string OutputVariant => "descriptor";
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
            new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };
        public Guid? ArtifactId { get; private set; }
        public bool SawRawFrame { get; private set; }

        public ValueTask ProcessAsync(CaptureDescriptorProcessingContext context, CancellationToken cancellationToken)
        {
            ArtifactId = context.ReconstructionDescriptor?.Artifact.ArtifactId;
            SawRawFrame = typeof(CaptureDescriptorProcessingContext).GetProperties().Any(property =>
                property.PropertyType == typeof(CameraFrame) ||
                property.PropertyType == typeof(CaptureLoopSubmission) ||
                property.PropertyType == typeof(ProcessingProduct) ||
                property.Name.Contains("Artifact", StringComparison.Ordinal));
            context.AddProcessingOutcome(ProcessingOutcome.Produced());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MetadataProducingStep(bool typed = true, int sourceCount = 1) : ICaptureProcessingStep, ICaptureProcessingGraphStep
    {
        private static readonly IReadOnlySet<FrameArtifactRole> InputRoles =
            new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };
        private static readonly int[] MetadataValues = [1, 2, 3, 4];

        public bool Enabled => true;
        public int ExecutionCount { get; private set; }
        public string Name => "metadata";
        public int Order => 0;
        public string RecipeName => "test-metadata";
        public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
        public string OutputVariant => "test-metadata-v1";
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles => InputRoles;
        public ProcessingProduct? Product { get; private set; }

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            var raw = context.Artifacts!.Raw;
            var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                RecipeName, "1.0.0", "test-v1", JsonSerializer.SerializeToElement(new { gridColumns = 2, gridRows = 2 })));
            var sources = Enumerable.Range(0, sourceCount)
                .Select(index => index == 0 ? raw.ArtifactId : Guid.Parse($"60000000-0000-0000-0000-{index:D12}"))
                .ToArray();
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "test-metadata-v1",
                values = MetadataValues
            });
            Product = new ProcessingProduct(
                OutputRole,
                OutputVariant,
                ProcessingIdentity.CreateOutputIdentity(OutputRole, OutputVariant, recipe.IdentitySha256, sources),
                "application/json",
                null,
                payload,
                ProcessingIdentity.ComputePayloadSha256(payload),
                recipe,
                [new ProcessingAlgorithmIdentity("test-metadata", "v1")],
                sources,
                raw.Frame.Metadata.Exposure,
                CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, "source").Compatibility)
            {
                Kind = ProcessingProductKind.Metadata,
                SchemaVersion = typed ? "test-metadata-v1" : null,
                ContentIdentitySha256 = typed ? ProcessingIdentity.ComputePayloadSha256(payload) : null
            };
            context.AddProcessingOutcome(ProcessingOutcome.Produced(Product));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class JpegProducingStep(string variant, bool malformed = false) : ICaptureProcessingStep, ICaptureProcessingGraphStep
    {
        public bool Enabled => true;
        public int ExecutionCount { get; private set; }
        public string Name => variant;
        public int Order => 0;
        public string RecipeName => "test-jpeg";
        public FrameArtifactRole OutputRole => FrameArtifactRole.AnnotatedPreview;
        public string OutputVariant => variant;
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
            new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            var raw = context.Artifacts!.Raw;
            var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                RecipeName,
                "1.0.0",
                "test-jpeg-v1",
                JsonSerializer.SerializeToElement(new { variant })));
            var sources = new[] { raw.ArtifactId };
            var payload = malformed
                ? new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }
                : JpegImageCodec.EncodeMono8ToJpeg(
                    raw.Frame.Width,
                    raw.Frame.Height,
                    new byte[] { 0, 64, 128, 255 },
                    cancellationToken: cancellationToken);
            var product = new ProcessingProduct(
                OutputRole,
                OutputVariant,
                ProcessingIdentity.CreateOutputIdentity(OutputRole, OutputVariant, recipe.IdentitySha256, sources),
                JpegImageCodec.MediaType,
                null,
                payload,
                ProcessingIdentity.ComputePayloadSha256(payload),
                recipe,
                [new ProcessingAlgorithmIdentity("jpeg", JpegImageCodec.AlgorithmVersion)],
                sources,
                raw.Frame.Metadata.Exposure,
                CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, "source").Compatibility);
            context.AddProcessingOutcome(ProcessingOutcome.Produced(product));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PackedAnnotationProducingStep : ICaptureProcessingStep, ICaptureProcessingGraphStep
    {
        public bool Enabled => true;
        public string Name => "annotation";
        public int Order => 70;
        public string RecipeName => "test-packed-annotation";
        public FrameArtifactRole OutputRole => FrameArtifactRole.AnnotatedPreview;
        public string OutputVariant => "production-like-annotated";
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
            new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            var raw = context.Artifacts!.Raw;
            var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                RecipeName,
                "1.0.0",
                "test-packed-annotation-v1",
                JsonSerializer.SerializeToElement(new { outputEncoding = "Packed" })));
            var sources = new[] { raw.ArtifactId };
            var payload = new byte[] { 0, 64, 128, 255 };
            var layout = new FrameLayoutDescriptor(
                raw.Frame.Width,
                raw.Frame.Height,
                raw.Frame.Width,
                CameraPixelFormat.Mono8,
                FrameByteOrder.NotApplicable,
                8,
                8,
                FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.None,
                0,
                byte.MaxValue,
                payload.Length);
            var product = new ProcessingProduct(
                OutputRole,
                OutputVariant,
                ProcessingIdentity.CreateOutputIdentity(OutputRole, OutputVariant, recipe.IdentitySha256, sources),
                "application/x-hvo-packed-image",
                layout,
                payload,
                ProcessingIdentity.ComputePayloadSha256(payload),
                recipe,
                [new ProcessingAlgorithmIdentity("annotation", "test-v1")],
                sources,
                raw.Frame.Metadata.Exposure,
                CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, "source").Compatibility);
            var frame = new CameraFrame(
                raw.Frame.TimestampUtc,
                raw.Frame.Width,
                raw.Frame.Height,
                CameraPixelFormat.Mono8,
                payload,
                raw.Frame.Metadata with { SourceId = Name },
                raw.Frame.Width)
            {
                Layout = layout
            };
            var artifact = context.AddDerivative(
                OutputRole,
                frame,
                OutputVariant,
                sources,
                CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
            context.AssociateProcessingProduct(artifact, product);
            context.AddProcessingOutcome(ProcessingOutcome.Produced(product));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RetryThenCompleteStep : ICaptureProcessingStep
    {
        private int _attempt;

        public string Name => "memory";
        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempt) == 1)
            {
                context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure("test.retry"));
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RestoredFrameInspectingStep(SceneProvenance expectedScene) : ICaptureProcessingStep
    {
        public string Name => "inspect";
        public int Order => 1;
        public bool SawExpectedScene { get; private set; }

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            Assert.AreEqual(expectedScene, context.GetDependencyArtifacts().Single().Frame.Metadata.Scene);
            Assert.AreEqual(FrameArtifactRole.Raw, context.Artifacts!.Raw.Role);
            Assert.AreEqual(FrameArtifactRole.Calibrated, context.Artifacts[FrameArtifactRole.Calibrated].Role);
            Assert.HasCount(2, context.AllArtifacts);
            SawExpectedScene = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RestoredMetadataInspectingStep : ICaptureProcessingStep
    {
        private readonly ProcessingProduct? _expected;

        public RestoredMetadataInspectingStep()
        {
        }

        public RestoredMetadataInspectingStep(ProcessingProduct expected)
        {
            _expected = expected;
        }

        public string Name => "inspect";
        public int Order => 1;
        public bool SawExactProduct { get; private set; }
        public TimeSpan? RestoredDependencyDuration { get; private set; }

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            var actual = context.GetDependencyProducts().Single();
            var expected = _expected ?? actual;
            Assert.AreEqual(expected.Role, actual.Role);
            Assert.AreEqual(expected.Variant, actual.Variant);
            Assert.AreEqual(expected.OutputIdentitySha256, actual.OutputIdentitySha256);
            Assert.AreEqual(expected.MediaType, actual.MediaType);
            Assert.AreEqual(expected.ChecksumSha256, actual.ChecksumSha256);
            Assert.AreEqual(expected.Recipe.IdentitySha256, actual.Recipe.IdentitySha256);
            Assert.AreEqual(expected.Recipe.Descriptor.Name, actual.Recipe.Descriptor.Name);
            Assert.AreEqual(expected.Recipe.Descriptor.SemanticVersion, actual.Recipe.Descriptor.SemanticVersion);
            Assert.AreEqual(expected.Recipe.Descriptor.ImplementationVersion, actual.Recipe.Descriptor.ImplementationVersion);
            Assert.AreEqual(expected.Recipe.Descriptor.OptionsSha256, actual.Recipe.Descriptor.OptionsSha256);
            CollectionAssert.AreEqual(expected.Algorithms.ToArray(), actual.Algorithms.ToArray());
            CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), actual.SourceArtifactIds.ToArray());
            Assert.AreEqual(expected.TotalIntegration, actual.TotalIntegration);
            Assert.AreEqual(expected.Compatibility, actual.Compatibility);
            Assert.AreEqual(expected.Kind, actual.Kind);
            Assert.AreEqual(expected.SchemaVersion, actual.SchemaVersion);
            Assert.AreEqual(expected.ContentIdentitySha256, actual.ContentIdentitySha256);
            CollectionAssert.AreEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
            Assert.IsNull(actual.Layout);
            Assert.HasCount(1, context.AllArtifacts);
            Assert.AreEqual(FrameArtifactRole.Raw, context.AllArtifacts[0].Role);
            RestoredDependencyDuration = context.GetDependencyStepTelemetry().Single().Duration;
            SawExactProduct = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OutcomeStep(ProcessingOutcome outcome) : ICaptureProcessingStep
    {
        public string Name => "outcome";
        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            context.AddProcessingOutcome(outcome);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingStep : ICaptureProcessingStep
    {
        public int ExecutionCount { get; private set; }
        public string Name => "counting";
        public int Order => 1;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RetryOnceStep : ICaptureProcessingStep
    {
        private int _attempt;

        public string Name => "retry-once";
        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempt) == 1)
            {
                context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure("test.retry-once"));
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedOutcomeExecutor(ProcessingOutcome outcome) : IProcessingRecipeExecutor
    {
        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(outcome);
    }

    private sealed class ThrowAfterSaveStorage(IFrameStorageService inner) : IFrameStorageService
    {
        public ValueTask<StoredFrameReference> SaveAsync(
            string storageRoot,
            FrameArtifact artifact,
            CancellationToken cancellationToken) => inner.SaveAsync(storageRoot, artifact, cancellationToken);

        public async ValueTask<StoredFrameReference> SaveAsync(
            string storageRoot,
            FrameArtifact artifact,
            ReconstructionDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            _ = await inner.SaveAsync(storageRoot, artifact, descriptor, cancellationToken).ConfigureAwait(false);
            throw new IOException("Injected failure after immutable publication.");
        }

        public async ValueTask<StoredFrameReference> SaveAsync(
            string storageRoot,
            FrameArtifact artifact,
            ReconstructionDescriptor descriptor,
            string producerStepId,
            CancellationToken cancellationToken)
        {
            _ = await inner.SaveAsync(
                storageRoot, artifact, descriptor, producerStepId, cancellationToken).ConfigureAwait(false);
            throw new IOException("Injected failure after immutable publication.");
        }

        public ValueTask RemoveAsync(
            string storageRoot,
            StoredFrameReference storedFrame,
            Guid artifactId,
            CancellationToken cancellationToken) =>
            inner.RemoveAsync(storageRoot, storedFrame, artifactId, cancellationToken);

        public IReadOnlyList<StoredFrameReference> List(
            string storageRoot,
            DateOnly utcDate,
            FrameArtifactRole? role,
            int maximumResults) => inner.List(storageRoot, utcDate, role, maximumResults);
    }

    private sealed record Fixture(
        ArtifactManifestV2 Manifest,
        IOptions<CameraAgentHostOptions> Options,
        FrameProcessingItem Item);
}
