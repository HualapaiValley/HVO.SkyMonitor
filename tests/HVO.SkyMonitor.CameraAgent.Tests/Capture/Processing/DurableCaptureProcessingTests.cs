using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
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
public sealed partial class DurableCaptureProcessingTests
{
    private const string Pre433CloudPlanSha256 = "C3937D26381FB9343A0C2439548D718B62CAA7778FE965B9E48D5E8C7BBD620E";
    private static readonly string[] ExpectedProductionLayerOrder =
        ["scene-constellations", "scene-image-circle", "scene-annotation", "scene-cardinals", "cloud-mask", "cloud-labels", "environment"];
    private static readonly JsonSerializerOptions WebEnumJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
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
    public async Task BlockedNodeWithStalePlanHash_IsRejectedWithoutMutatingCommittedNode()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
            var annotation = new PackedAnnotationProducingStep();
            var oldHash = new string('A', 64);
            var oldNode = new CaptureProcessingGraphNode("sky-annotation", annotation, [], true,
                annotation.RecipeName, annotation.OutputRole, annotation.OutputVariant, oldHash);
            var first = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, new CaptureProcessingGraph([oldNode]), persistence, telemetry, 1,
                NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, first.Outcome, first.Reason);
            var persisted = (await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                "sky-annotation", CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(oldHash, persisted.PlanSha256);

            var blocker = new OutcomeStep(ProcessingOutcome.TerminalFailure("test.blocker-terminal"));
            var incompatible = new PackedAnnotationProducingStep();
            var graph = new CaptureProcessingGraph([
                new CaptureProcessingGraphNode("blocker", blocker, [], false,
                    "blocker", FrameArtifactRole.Metadata, "blocker", new string('B', 64)),
                new CaptureProcessingGraphNode("sky-annotation", incompatible, ["blocker"], true,
                    incompatible.RecipeName, incompatible.OutputRole, incompatible.OutputVariant,
                    new string('N', 64))
            ]);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item, graph, persistence, telemetry, 2,
                    NullLogger.Instance, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(0, incompatible.ExecutionCount);
            Assert.IsNull(await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                "blocker", CancellationToken.None).ConfigureAwait(false));
            var unchanged = (await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                "sky-annotation", CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(oldHash, unchanged.PlanSha256);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, unchanged.Status);
            Assert.AreEqual(persisted.Attempt, unchanged.Attempt);
            CollectionAssert.AreEqual(
                persisted.Outputs.Select(static output => output.OutputIdentitySha256).ToArray(),
                unchanged.Outputs.Select(static output => output.OutputIdentitySha256).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ProductionLayeredPresentation_RestartRestoresExactFinalBytesIdentityAndSourceOrder()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            string beforeIdentity;
            byte[] beforePayload;
            Guid[] beforeSources;
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var graph = await CreateProductionLayeredGraphAsync(fixture, root).ConfigureAwait(false);
                var first = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item, graph, CreatePersistence(fixture.Options, store, storage, telemetry), telemetry, 1,
                    NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, first.Outcome, first.Reason);
                var before = await ReadProductAsync(store, "presentation-materializer").ConfigureAwait(false);
                beforeIdentity = before.OutputIdentitySha256;
                beforePayload = await File.ReadAllBytesAsync(Path.Combine(root, before.PayloadRelativePath)).ConfigureAwait(false);
                beforeSources = before.Artifact.SourceArtifactIds.ToArray();
                var manifestOutput = await ReadProductAsync(store, "overlay-manifest").ConfigureAwait(false);
                var manifest = LayeredPresentationJson.ParseManifest(
                    await File.ReadAllBytesAsync(Path.Combine(root, manifestOutput.PayloadRelativePath)).ConfigureAwait(false)).Document;
                Assert.IsNotNull(manifest);
                CollectionAssert.AreEqual(
                    ExpectedProductionLayerOrder,
                    manifest.Layers.Select(static layer => layer.LayerKind).ToArray());
                CollectionAssert.AreEqual(manifest.Layers.Select(static layer => layer.ZOrder).Order().ToArray(),
                    manifest.Layers.Select(static layer => layer.ZOrder).ToArray());
            }

            var restartedFixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var restartedTelemetry = new CaptureProcessingTelemetry();
            using var restartedStore = new SqliteCaptureProcessingStore(restartedFixture.Options);
            using var restartedStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var replayGraph = await CreateProductionLayeredGraphAsync(restartedFixture, root).ConfigureAwait(false);
            var replay = await FrameProcessingWorker.ProcessGraphItemAsync(
                restartedFixture.Item, replayGraph,
                CreatePersistence(restartedFixture.Options, restartedStore, restartedStorage, restartedTelemetry),
                restartedTelemetry, 2, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, replay.Outcome, replay.Reason);
            var after = await ReadProductAsync(restartedStore, "presentation-materializer").ConfigureAwait(false);
            var afterPayload = await File.ReadAllBytesAsync(Path.Combine(root, after.PayloadRelativePath)).ConfigureAwait(false);

            Assert.AreEqual(beforeIdentity, after.OutputIdentitySha256);
            CollectionAssert.AreEqual(beforePayload, afterPayload);
            CollectionAssert.AreEqual(beforeSources, after.Artifact.SourceArtifactIds.ToArray());
            Assert.HasCount(9, beforeSources);
            foreach (var node in replayGraph.Nodes)
            {
                Assert.AreEqual(1, (await restartedStore.ReadNodeAsync(
                    restartedFixture.Manifest.Descriptor.Capture.CaptureId, node.Id,
                    CancellationToken.None).ConfigureAwait(false))!.Attempt);
            }
            var continuation = new DependencyCountingStep();
            var continuationResult = await FrameProcessingWorker.ProcessGraphItemAsync(
                restartedFixture.Item,
                new CaptureProcessingGraph([
                    .. replayGraph.Nodes,
                    new CaptureProcessingGraphNode("continuation", continuation, ["overlay-manifest"], true,
                        null, null, null, new string('C', 64))
                ]),
                CreatePersistence(restartedFixture.Options, restartedStore, restartedStorage, restartedTelemetry),
                restartedTelemetry, 3, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, continuationResult.Outcome, continuationResult.Reason);
            Assert.AreEqual(1, continuation.ExecutionCount);
            Assert.AreEqual(OverlayManifestV1.CurrentSchemaVersion, continuation.DependencySchemaVersion);

            async Task<DurableProcessingOutput> ReadProductAsync(SqliteCaptureProcessingStore store, string nodeId)
            {
                var node = await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                    nodeId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(node);
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status);
                Assert.HasCount(1, node.Outputs);
                return node.Outputs[0];
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task OperatorMaterializationPersistsSelectedStackOnceWithExactAuditAndLineage()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var rawJournal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 5);
            await rawJournal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
            var graph = await CreateProductionLayeredGraphAsync(fixture, root).ConfigureAwait(false);
            var processed = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, graph, persistence, telemetry, 1, NullLogger.Instance, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);
            var manifestNode = await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, "overlay-manifest", CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(manifestNode);
            var manifestOutput = manifestNode.Outputs.Single();
            var manifest = LayeredPresentationJson.ParseManifest(
                await File.ReadAllBytesAsync(Path.Combine(root, manifestOutput.PayloadRelativePath)).ConfigureAwait(false))
                .Document!;
            var selected = new[] { manifest.Layers[0].LayerIdentitySha256 };
            await rawJournal.ReserveIdentityAsync(
                fixture.Manifest.Descriptor.Capture.AgentId,
                fixture.Manifest.Descriptor.Capture.CaptureId,
                fixture.Manifest.Descriptor.Artifact.ArtifactId,
                CancellationToken.None).ConfigureAwait(false);
            var rawManifestJson = CaptureContractJson.Serialize(fixture.Manifest);
            await rawJournal.CommitAsync(new RawIngressJournalEntry(
                fixture.Manifest.Descriptor.Capture.AgentId,
                fixture.Manifest.Descriptor.Capture.CaptureSequence,
                fixture.Manifest.Descriptor.Capture.CaptureId,
                fixture.Manifest.Descriptor.Artifact.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(fixture.Manifest.Descriptor),
                CaptureContractJson.ComputeManifestSha256(rawManifestJson),
                fixture.Manifest.Descriptor.Artifact.ChecksumSha256,
                fixture.Manifest.Descriptor.Layout.ByteLength,
                fixture.Manifest.RelativeArtifactPath,
                Path.ChangeExtension(fixture.Manifest.RelativeArtifactPath, ".json"),
                rawManifestJson,
                fixture.Manifest.Descriptor.Timing.ExposureStartedUtc,
                fixture.Manifest.Descriptor.Timing.DurableIngressUtc), CancellationToken.None).ConfigureAwait(false);
            var reconciliation = await new DerivedProductReconciler(root, store)
                .RunAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, reconciliation.Quarantined);
            Assert.IsGreaterThan(0, reconciliation.Available);
            using var artifactService = new CameraAgentArtifactService(
                fixture.Options, store, new CameraAgentPreviewEncoder());
            var openedManifest = await artifactService.OpenContentAsync(
                manifestOutput.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, openedManifest.Status);
            if (openedManifest.Content is not null)
            {
                await openedManifest.Content.DisposeAsync().ConfigureAwait(false);
            }
            var presentationService = new CameraAgentLayeredPresentationService(store, artifactService);
            var presentation = await presentationService.GetAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            var cachedPresentation = await presentationService.GetAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, presentation.Status, presentation.Reason);
            Assert.IsNotNull(presentation.Presentation);
            Assert.AreEqual(manifest.Layers.Count, presentation.Presentation.Layers.Count);
            Assert.IsGreaterThan(0, presentation.Presentation.Svg.Length);
            Assert.AreSame(presentation.Presentation, cachedPresentation.Presentation);

            var first = await persistence.MaterializePresentationAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId,
                manifestOutput.ArtifactId,
                selected,
                "operator-test",
                CancellationToken.None).ConfigureAwait(false);
            var replay = await persistence.MaterializePresentationAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId,
                manifestOutput.ArtifactId,
                selected,
                "operator-test",
                CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(first.Replayed);
            Assert.IsTrue(replay.Replayed);
            Assert.AreEqual(first.ArtifactId, replay.ArtifactId);
            Assert.AreEqual(first.OutputIdentitySha256, replay.OutputIdentitySha256);
            Assert.AreEqual(first.ChecksumSha256, replay.ChecksumSha256);
            Assert.AreEqual(first.ByteLength, replay.ByteLength);
            var output = await store.ReadOutputByArtifactIdAsync(first.ArtifactId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(output);
            CollectionAssert.AreEqual(
                new[] { manifest.BaseProduct.ArtifactId, manifestOutput.ArtifactId, manifest.Layers[0].SourceProduct.ArtifactId },
                output.Artifact.SourceArtifactIds.ToArray());
            var node = await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId,
                $"gallery-materialization-{first.OutputIdentitySha256[..16]}",
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(node);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status);
            Assert.HasCount(1, node.Outputs);
            Assert.IsNotNull(node.Inputs);
            var audit = node.Inputs.Single(static input => input.Kind == "CanonicalContext");
            Assert.AreEqual("operator", audit.Name);
            Assert.AreEqual(64, audit.IdentitySha256?.Length);
            Assert.AreNotEqual("operator-test", audit.IdentitySha256);
            var reconstruction = FrameReconstructor.TryReconstruct(
                output.Descriptor!,
                await File.ReadAllBytesAsync(Path.Combine(root, output.PayloadRelativePath)).ConfigureAwait(false),
                out _);
            Assert.IsTrue(reconstruction.IsValid, reconstruction.ReasonCode);

            var materializedPath = Path.Combine(root, output.PayloadRelativePath);
            var corruptPayload = await File.ReadAllBytesAsync(materializedPath).ConfigureAwait(false);
            corruptPayload[0] ^= 0xFF;
            await File.WriteAllBytesAsync(materializedPath, corruptPayload).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await persistence.MaterializePresentationAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId,
                    manifestOutput.ArtifactId,
                    selected,
                    "operator-test",
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            var layerOutput = await store.ReadOutputByArtifactIdAsync(
                manifest.Layers[0].SourceProduct.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(layerOutput);
            await store.TransitionOutputUnavailableAsync(
                layerOutput.OutputIdentitySha256, "Missing", "test-unavailable", null, CancellationToken.None)
                .ConfigureAwait(false);
            var staleAvailability = await presentationService.GetAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Unavailable, staleAvailability.Status);
            Assert.IsNull(staleAvailability.Presentation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PreTypedCloudPlan_IsRejectedWithoutMutation()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var productTemplate = CreateCloudAssessmentProduct(fixture.Item);
            var environmentInput = CameraAgentCloudEnvironment.CreateMissingInput(new CaptureProcessingContext(
                fixture.Item.Config, fixture.Item.Submission, fixture.Item.RawCapture));
            var environment = JsonSerializer.Deserialize<CloudAssessmentEnvironmentV1>(
                environmentInput.Payload.Span, WebEnumJsonOptions)!;
            environment = environment with
            {
                InputIdentitySha256 = environmentInput.IdentitySha256
            };
            var assessment = CloudAssessmentJson.Parse(productTemplate.Payload).Assessment! with
            {
                Environment = environment,
                RecipeIdentitySha256 = productTemplate.Recipe.IdentitySha256
            };
            var legacyPayload = CloudAssessmentJson.Serialize(assessment);
            var legacyProduct = productTemplate with
            {
                Payload = legacyPayload,
                ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(legacyPayload),
                SchemaVersion = null,
                ContentIdentitySha256 = null
            };
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var producer = new FixedMetadataProducingStep(legacyProduct);
                var committed = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([new CaptureProcessingGraphNode("cloud", producer, [], false,
                        producer.RecipeName, producer.OutputRole, producer.OutputVariant, Pre433CloudPlanSha256)]),
                    CreatePersistence(fixture.Options, store, storage, telemetry), telemetry, 1,
                    NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, committed.Outcome, committed.Reason);
            }

            var restartedFixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var restartedTelemetry = new CaptureProcessingTelemetry();
            using var restartedStore = new SqliteCaptureProcessingStore(restartedFixture.Options);
            using var restartedStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var cloud = CreateRealCloudStep(root);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    restartedFixture.Item,
                    new CaptureProcessingGraph([new CaptureProcessingGraphNode("cloud", cloud, [], false,
                        cloud.RecipeName, cloud.OutputRole, cloud.OutputVariant,
                        new string('N', 64))]),
                    CreatePersistence(restartedFixture.Options, restartedStore, restartedStorage, restartedTelemetry),
                    restartedTelemetry, 2, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
            var unchanged = await restartedStore.ReadNodeAsync(
                restartedFixture.Manifest.Descriptor.Capture.CaptureId, "cloud", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(unchanged);
            Assert.AreEqual(Pre433CloudPlanSha256, unchanged.PlanSha256);
            Assert.IsNull(unchanged.Outputs.Single().ProductSchemaVersion);
            Assert.IsNull(unchanged.Outputs.Single().ContentIdentitySha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static CloudAssessmentCaptureProcessingStep CreateRealCloudStep(string root) => new(
            new CaptureProcessingStepMetadata("cloud", "CloudAssessment", 60),
            new CloudAssessmentProcessingStepOptions { OutputVariant = "cloud-assessment-v1" },
            new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()),
            new CameraAgentClearReferenceLoader(Options.Create(new CameraAgentHostOptions { RawIngressRoot = root })));
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
            var outboxRecords = await outbox.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, outboxRecords);
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
                var firstStep = new ProducingStep(recipeVersion: "configured-artifact-v2");
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
            var restartedStep = new ProducingStep(recipeVersion: "configured-artifact-v2");
            var restartedNode = CreateNode(restartedStep);
            var inspector = new RestoredFrameInspectingStep(
                fixture.Manifest.Scene!, "configured-artifact-v2");
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
            Assert.AreEqual("configured-artifact-v2", output.FrameArtifactRecipeVersion);
            Assert.AreEqual("test-v1", output.Artifact.Recipe.ImplementationVersion);
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
    public async Task PredictedAndSyntheticImageRegisteredV3CoexistAcrossRestartWithIndependentLineage()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
                new CelestialCatalogObject("star", "Star", 0, 0, 1)
            ])).BuildAsync(new VisibleSceneRequest(
                fixture.Manifest.Descriptor.Timing.ExposureStartedUtc,
                new ObserverLocation(0, 0, 0),
                new EquidistantProjectionContext(1, 1, 1, 1, WidthPixels: 2, HeightPixels: 2),
                new CatalogQuery(6.5, 10),
                new CatalogMetadata("test", "1", new Uri("https://example.invalid"), new string('A', 64), "test", "1"),
                projectionVersion: "projection-v1"))
                .ConfigureAwait(false);
            var source = new ProjectedSceneSource(
                fixture.Manifest.Descriptor.Capture.CaptureId,
                fixture.Manifest.Descriptor.Artifact.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(fixture.Manifest.Descriptor));
            var predictedScene = ProjectedSceneJson.Create(
                ProjectedSceneKind.Predicted, visible, ProjectedSceneImageTransformV1.Identity(2, 2), source,
                "calibration-v1", "projection-v1");
            var registeredScene = ProjectedSceneJson.Create(
                ProjectedSceneKind.ImageRegistered, visible, ProjectedSceneImageTransformV1.Identity(2, 2), source,
                "calibration-v1", "projection-v1");
            var predicted = new FixedMetadataProducingStep(CreateProjectedSceneProduct(
                fixture.Item, predictedScene, "predicted-scene"));
            var registered = new FixedMetadataProducingStep(CreateProjectedSceneProduct(
                fixture.Item, registeredScene, "image-registered-scene"));
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([
                        new CaptureProcessingGraphNode(
                            "predicted", predicted, [], true, predicted.RecipeName, predicted.OutputRole,
                            predicted.OutputVariant, new string('A', 64)),
                        new CaptureProcessingGraphNode(
                            "registered", registered, [], true, registered.RecipeName, registered.OutputRole,
                            registered.OutputVariant, new string('B', 64))
                    ]),
                    CreatePersistence(fixture.Options, store, storage, telemetry), telemetry, 1,
                    NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            }

            using var restarted = new SqliteCaptureProcessingStore(fixture.Options);
            var products = await restarted.ReadCaptureProductsAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId,
                ProjectedSceneV1.CurrentSchemaVersion,
                10,
                CancellationToken.None).ConfigureAwait(false);

            Assert.HasCount(2, products);
            Assert.IsTrue(products.Select(static product => product.Variant).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["predicted-scene", "image-registered-scene"]));
            Assert.AreEqual(2, products.Select(static product => product.OutputIdentitySha256).Distinct().Count());
            Assert.AreEqual(2, products.Select(static product => product.ContentIdentitySha256).Distinct().Count());
            foreach (var product in products)
            {
                var sources = await restarted.ReadOutputSourcesAsync(
                    product.OutputIdentitySha256, 10, CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(1, sources);
                Assert.AreEqual(fixture.Manifest.Descriptor.Artifact.ArtifactId, sources[0].ArtifactId);
            }
            var holds = await restarted.ReadRetentionHoldsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(products.All(product => holds.Any(hold =>
                hold.ArtifactId == ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ApplyRetentionAsync_ProductionProjectedSceneDependencyChainHoldsThenExpiresAndConverges()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var datedDirectory = Path.Combine(root, "frames", "2020", "01", "01", "Raw");
            Directory.CreateDirectory(datedDirectory);
            var datedPayload = Path.Combine(datedDirectory, "raw.bin");
            var datedSidecar = Path.ChangeExtension(datedPayload, ".json");
            File.Move(Path.Combine(root, "raw.bin"), datedPayload);
            File.Move(Path.Combine(root, "raw.json"), datedSidecar);
            var datedRelativePayload = Path.GetRelativePath(root, datedPayload).Replace(Path.DirectorySeparatorChar, '/');
            var datedManifest = fixture.Manifest with { RelativeArtifactPath = datedRelativePayload };
            await File.WriteAllBytesAsync(datedSidecar, CaptureContractJson.Serialize(datedManifest)).ConfigureAwait(false);
            var datedReceipt = fixture.Item.RawCapture! with
            {
                Manifest = datedManifest,
                StoredFrame = fixture.Item.RawCapture.StoredFrame with
                {
                    RelativePath = datedRelativePayload,
                    AbsolutePath = datedPayload
                },
                CommittedManifestSha256 = CaptureContractJson.ComputeManifestSha256(datedManifest)
            };
            fixture = fixture with
            {
                Manifest = datedManifest,
                Item = fixture.Item with { RawCapture = datedReceipt }
            };
            var rawId = fixture.Manifest.Descriptor.Artifact.ArtifactId;
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await journal.ReserveIdentityAsync(
                fixture.Manifest.Descriptor.Capture.AgentId,
                fixture.Manifest.Descriptor.Capture.CaptureId,
                rawId,
                CancellationToken.None).ConfigureAwait(false);
            await journal.CommitAsync(new RawIngressJournalEntry(
                fixture.Manifest.Descriptor.Capture.AgentId,
                fixture.Manifest.Descriptor.Capture.CaptureSequence,
                fixture.Manifest.Descriptor.Capture.CaptureId,
                rawId,
                CaptureContractJson.ComputeDescriptorSha256(fixture.Manifest.Descriptor),
                CaptureContractJson.ComputeManifestSha256(fixture.Manifest),
                fixture.Manifest.Descriptor.Artifact.ChecksumSha256,
                fixture.Manifest.Descriptor.Layout.ByteLength,
                fixture.Manifest.RelativeArtifactPath,
                Path.ChangeExtension(fixture.Manifest.RelativeArtifactPath, ".json"),
                CaptureContractJson.Serialize(fixture.Manifest),
                fixture.Manifest.Descriptor.Timing.ExposureStartedUtc,
                fixture.Manifest.Descriptor.Timing.DurableIngressUtc), CancellationToken.None).ConfigureAwait(false);
            var predicted = CreateSyntheticMetadataProduct(fixture.Item, "predicted", [rawId]);
            var registered = CreateSyntheticMetadataProduct(fixture.Item, "registered", [rawId]);
            var dependent = CreateSyntheticMetadataProduct(
                fixture.Item, "dependent",
                [ProcessingIdentity.CreateArtifactId(predicted.OutputIdentitySha256),
                 ProcessingIdentity.CreateArtifactId(registered.OutputIdentitySha256)]);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item,
                new CaptureProcessingGraph([
                    new CaptureProcessingGraphNode("predicted", new FixedMetadataProducingStep(predicted), [], true,
                        predicted.Recipe.Descriptor.Name, predicted.Role, predicted.Variant, new string('A', 64)),
                    new CaptureProcessingGraphNode("registered", new FixedMetadataProducingStep(registered), [], true,
                        registered.Recipe.Descriptor.Name, registered.Role, registered.Variant, new string('B', 64)),
                    new CaptureProcessingGraphNode("dependent", new FixedMetadataProducingStep(dependent),
                        ["predicted", "registered"], true, dependent.Recipe.Descriptor.Name, dependent.Role,
                        dependent.Variant, new string('C', 64))
                ]),
                persistence, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);

            var expiredUnix = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var age = connection.CreateCommand();
                age.CommandText = "UPDATE processing_outputs SET committed_unix_ms = $expired;";
                age.Parameters.AddWithValue("$expired", expiredUnix);
                await age.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var outputPaths = new List<string>();
            foreach (var nodeId in new[] { "predicted", "registered", "dependent" })
            {
                var node = await store.ReadNodeAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId, nodeId, CancellationToken.None).ConfigureAwait(false);
                var output = node!.Outputs.Single();
                outputPaths.Add(Path.Combine(root, output.PayloadRelativePath));
                outputPaths.Add(Path.Combine(root, output.SidecarRelativePath));
            }
            var config = CreateRetentionConfig(root);
            var retention = CreateProductionRetentionService(root, persistence);

            await retention.ApplyRetentionAsync(config, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(outputPaths.All(File.Exists));
            Assert.IsTrue(File.Exists(datedPayload));
            Assert.IsTrue(File.Exists(datedSidecar));
            Assert.HasCount(3, await store.ReadCaptureProductsAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, null, 10, CancellationToken.None).ConfigureAwait(false));

            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var release = connection.CreateCommand();
                release.CommandText = """
                    UPDATE processing_outputs
                    SET availability_state = 'Missing', availability_reason = 'retention-released',
                        unavailable_unix_ms = $expired;
                    """;
                release.Parameters.AddWithValue("$expired", expiredUnix);
                await release.ExecuteNonQueryAsync().ConfigureAwait(false);
                using var releaseRaw = connection.CreateCommand();
                releaseRaw.CommandText = "DELETE FROM raw_captures WHERE capture_id = $capture;";
                releaseRaw.Parameters.AddWithValue(
                    "$capture", fixture.Manifest.Descriptor.Capture.CaptureId.ToString("N"));
                await releaseRaw.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await retention.ApplyRetentionAsync(config, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(outputPaths.All(path => !File.Exists(path)));
            Assert.IsFalse(File.Exists(datedPayload));
            Assert.IsFalse(File.Exists(datedSidecar));
            Assert.IsEmpty(await store.ReadCaptureProductsAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, null, 10, CancellationToken.None).ConfigureAwait(false));
            using var verify = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await verify.OpenAsync().ConfigureAwait(false);
            using var counts = verify.CreateCommand();
            counts.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM processing_outputs WHERE capture_id = $capture),
                    (SELECT COUNT(*) FROM processing_output_sources source
                     JOIN processing_outputs output ON output.output_identity_sha256 = source.output_identity_sha256
                     WHERE output.capture_id = $capture),
                    (SELECT COUNT(*) FROM processing_nodes WHERE capture_id = $capture);
                """;
            counts.Parameters.AddWithValue("$capture", fixture.Manifest.Descriptor.Capture.CaptureId.ToString("N"));
            using var reader = await counts.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(0L, reader.GetInt64(0));
            Assert.AreEqual(0L, reader.GetInt64(1));
            Assert.AreEqual(0L, reader.GetInt64(2));
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
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var store = new SqliteCaptureProcessingStore(options);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA foreign_keys=OFF;
                    INSERT INTO capture_lane_work(
                        work_id, raw_capture_row_id, lane_name, agent_id, capture_sequence,
                        required, ordered, state, attempt_count, available_unix_ms,
                        lease_token, lease_expires_unix_ms, created_unix_ms, updated_unix_ms)
                    VALUES (
                        1, 1, 'standard', 'agent', 1,
                        1, 1, 'leased', 1, 0,
                        'current-token', 4102444800000, 0, 0);
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
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var store = new SqliteCaptureProcessingStore(options);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
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
                        INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
                        VALUES ($capture, $artifact, $agent, $sequence);
                        INSERT INTO raw_captures(
                            capture_id, raw_artifact_id, agent_id, capture_sequence, descriptor_sha256,
                            manifest_sha256, payload_sha256, payload_length, payload_relative_path,
                            sidecar_relative_path, manifest_json, exposure_started_unix_ms,
                            durable_ingress_unix_ms, committed_unix_ms, state, retention_hold, evidence_origin)
                        VALUES (
                            $capture, $artifact, $agent, $sequence, $descriptor_sha,
                            $manifest_sha, $payload_sha, 8, $payload,
                            $sidecar, $manifest, $time,
                            $time, $time, 'committed', 1, 'DeveloperFixture');
                        """;
                    insert.Parameters.AddWithValue("$capture", descriptor.Capture.CaptureId.ToString("N"));
                    insert.Parameters.AddWithValue("$artifact", descriptor.Artifact.ArtifactId.ToString("N"));
                    insert.Parameters.AddWithValue("$agent", descriptor.Capture.AgentId);
                    insert.Parameters.AddWithValue("$sequence", sequence);
                    insert.Parameters.AddWithValue("$descriptor_sha", CaptureContractJson.ComputeDescriptorSha256(descriptor));
                    insert.Parameters.AddWithValue("$payload", manifest.RelativeArtifactPath);
                    insert.Parameters.AddWithValue("$sidecar", $"raw/{sequence}.json");
                    insert.Parameters.AddWithValue("$manifest", manifestJson);
                    insert.Parameters.AddWithValue("$manifest_sha", CaptureContractJson.ComputeManifestSha256(manifestJson));
                    insert.Parameters.AddWithValue("$payload_sha", descriptor.Artifact.ChecksumSha256);
                    insert.Parameters.AddWithValue("$time", descriptor.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds());
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
    [DataRow(1, 1, 101, 100)]
    [DataRow(128, 1, 513, 512)]
    [DataRow(128, 9, 513, 4608)]
    [TestCategory("Integration")]
    public async Task ProcessingOutputRetention_CoversConfiguredWindowCandidateScanAcrossNodes(
        int maximumWindowInputs,
        int nodeCount,
        int outputsPerNode,
        int expectedHoldCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                ProcessingGraphs = new ProcessingGraphExecutionOptions
                {
                    MaximumWindowInputs = maximumWindowInputs
                }
            });
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var store = new SqliteCaptureProcessingStore(options);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var connection = new SqliteConnection(
                       $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
                for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
                {
                    for (var sequence = 1; sequence <= outputsPerNode; sequence++)
                    {
                        var identityOrdinal = nodeIndex * outputsPerNode + sequence;
                        using var insert = connection.CreateCommand();
                        insert.Transaction = transaction;
                        insert.CommandText = """
                            INSERT INTO processing_outputs(
                                output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                                payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                                algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                                committed_unix_ms)
                            VALUES ($output, $capture, 'agent-a', $node, $artifact, 'Calibrated', 'retention-test',
                                $payload, $sidecar, X'7B7D', $recipe, X'5B5D', X'7B7D', 1, $sequence, $sequence);
                            """;
                        insert.Parameters.AddWithValue(
                            "$output", identityOrdinal.ToString("X64", CultureInfo.InvariantCulture));
                        insert.Parameters.AddWithValue(
                            "$capture", Guid.Parse($"60000000-0000-0000-0000-{identityOrdinal:D12}").ToString("N"));
                        insert.Parameters.AddWithValue("$node", $"producer-{nodeIndex}");
                        insert.Parameters.AddWithValue(
                            "$artifact", Guid.Parse($"70000000-0000-0000-0000-{identityOrdinal:D12}").ToString("N"));
                        insert.Parameters.AddWithValue("$payload", $"frames/{nodeIndex}/{sequence}.bin");
                        insert.Parameters.AddWithValue("$sidecar", $"frames/{nodeIndex}/{sequence}.json");
                        insert.Parameters.AddWithValue("$recipe", new string('B', 64));
                        insert.Parameters.AddWithValue("$sequence", sequence);
                        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
                await transaction.CommitAsync().ConfigureAwait(false);
            }

            var holds = await store.ReadRetentionHoldsAsync(CancellationToken.None).ConfigureAwait(false);
            var firstRetainedCapture = Guid.Parse("60000000-0000-0000-0000-000000000002");
            var firstPrunedCapture = Guid.Parse("60000000-0000-0000-0000-000000000001");
            var firstRetainedState = await store.ReadGalleryRetentionStatesAsync(
                firstRetainedCapture, CancellationToken.None).ConfigureAwait(false);
            var firstPrunedState = await store.ReadGalleryRetentionStatesAsync(
                firstPrunedCapture, CancellationToken.None).ConfigureAwait(false);

            Assert.HasCount(expectedHoldCount, holds);
            Assert.IsTrue(holds.Any(static hold => hold.PayloadRelativePath == "frames/0/2.bin"));
            Assert.IsFalse(holds.Any(static hold => hold.PayloadRelativePath == "frames/0/1.bin"));
            Assert.IsTrue(firstRetainedState[Guid.Parse("70000000-0000-0000-0000-000000000002")]);
            Assert.IsFalse(firstPrunedState[Guid.Parse("70000000-0000-0000-0000-000000000001")]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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

    private static ProcessingProduct CreateProjectedSceneProduct(
        FrameProcessingItem item,
        ProjectedSceneV1 scene,
        string variant)
    {
        var payload = ProjectedSceneJson.Serialize(scene);
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            BuiltInProcessingRecipes.ProjectedScene, "1.0.0", "synthetic-test-v1",
            JsonSerializer.SerializeToElement(new { kind = scene.Kind.ToString() })));
        var descriptor = item.RawCapture?.Manifest.Descriptor ??
            throw new InvalidOperationException("The projected-scene fixture requires raw capture evidence.");
        var sources = new[] { descriptor.Artifact.ArtifactId };
        return new ProcessingProduct(
            FrameArtifactRole.Metadata,
            variant,
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, variant, recipe.IdentitySha256, sources),
            "application/json",
            null,
            payload,
            ProcessingIdentity.ComputePayloadSha256(payload),
            recipe,
            [new ProcessingAlgorithmIdentity("synthetic-registration-fixture", "v1")],
            sources,
            TimeSpan.Zero,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor))
        {
            Kind = ProcessingProductKind.Metadata,
            SchemaVersion = ProjectedSceneV1.CurrentSchemaVersion,
            ContentIdentitySha256 = scene.SceneIdentitySha256
        };
    }

    private static ProcessingProduct CreateSyntheticMetadataProduct(
        FrameProcessingItem item,
        string variant,
        IReadOnlyList<Guid> sources)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "retention-chain-v1", variant });
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            "retention-chain", "1.0.0", "retention-chain-v1", JsonSerializer.SerializeToElement(new { variant })));
        return new ProcessingProduct(
            FrameArtifactRole.Metadata,
            variant,
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, variant, recipe.IdentitySha256, sources),
            "application/json",
            null,
            payload,
            ProcessingIdentity.ComputePayloadSha256(payload),
            recipe,
            [new ProcessingAlgorithmIdentity("retention-chain", "v1")],
            sources,
            TimeSpan.Zero,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(item.RawCapture!.Manifest.Descriptor))
        {
            Kind = ProcessingProductKind.Metadata,
            SchemaVersion = "retention-chain-v1",
            ContentIdentitySha256 = ProcessingIdentity.ComputePayloadSha256(payload)
        };
    }

    private static CameraModuleConfig CreateRetentionConfig(string root)
    {
        var storageOptions = new NoOpFileStorageProcessingStepOptions
        {
            StorageRoot = root,
            RetentionDays = 1
        };
        return CreateConfig() with
        {
            Pipeline = new CapturePipelineConfig(
            [
                new CaptureProcessingStepConfig(
                    "Storage", "storage", Options: JsonSerializer.SerializeToElement(storageOptions)),
                new CaptureProcessingStepConfig(
                    "ProjectedScene", "projected", Publication: new CaptureProcessingPublicationPolicy(
                        CaptureProcessingPersistenceMode.DurableLocal))
            ])
        };
    }

    private static RetentionBackgroundService CreateProductionRetentionService(
        string root,
        CaptureProcessingPersistence persistence)
        => new(
            new RetentionConfigurationAccessor(),
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }),
            new RetentionTimeProvider(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero)),
            new SqliteArtifactOutbox(),
            new RetentionCapacityProvider(),
            new StoragePressureState(),
            NullLogger<RetentionBackgroundService>.Instance,
            processingHolds: persistence);

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
    public async Task OptionalRetryableNode_InLiveExecutionDegradesWithoutRetryingLane()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var optional = new OutcomeStep(ProcessingOutcome.RetryableFailure("test.optional-retry"));
        var required = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("optional", optional, [], false, "optional", FrameArtifactRole.Metadata, "optional"),
            new CaptureProcessingGraphNode("required", required, [], true, null, null, null)
        ]);
        var item = CreateEphemeralItem() with
        {
            Execution = new ProcessingExecutionContext(
                Guid.NewGuid(),
                ProcessingGraphExecutionClass.Live,
                "basic@1",
                new string('A', 64),
                AllowAutomaticPublication: true,
                WorkId: 1,
                LeaseToken: "lease")
        };

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            item, graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
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
    public async Task OptionalProducerRetry_DefersOptionalAndRequiredConsumersAndRetriesLane()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var producer = new OutcomeStep(ProcessingOutcome.RetryableFailure("test.producer-retry"));
        var optionalConsumer = new CountingStep();
        var requiredConsumer = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("P", producer, [], false, null, null, null),
            new CaptureProcessingGraphNode("A", optionalConsumer, ["P"], true, null, null, null,
                OptionalDependencies: new HashSet<string>(["P"], StringComparer.OrdinalIgnoreCase)),
            new CaptureProcessingGraphNode("B", requiredConsumer, ["P"], true, null, null, null)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, result.Outcome);
        Assert.AreEqual("test.producer-retry", result.Reason);
        Assert.AreEqual(0, optionalConsumer.ExecutionCount);
        Assert.AreEqual(0, requiredConsumer.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OptionalProducerWait_DefersOptionalConsumerAndDefersLane()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var producer = new OutcomeStep(ProcessingOutcome.RetryableFailure(
            ProcessingReasonCodes.EnvironmentAssociationPending));
        var optionalConsumer = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("P", producer, [], false, null, null, null),
            new CaptureProcessingGraphNode("A", optionalConsumer, ["P"], true, null, null, null,
                OptionalDependencies: new HashSet<string>(["P"], StringComparer.OrdinalIgnoreCase))
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.Deferred, result.Outcome);
        Assert.AreEqual(ProcessingReasonCodes.EnvironmentAssociationPending, result.Reason);
        Assert.AreEqual(0, optionalConsumer.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OptionalProducerTerminal_LetsOptionalConsumerSucceedAndRequiredConsumerTerminates()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var producer = new OutcomeStep(ProcessingOutcome.TerminalFailure("test.producer-terminal"));
        var optionalConsumer = new CountingStep();
        var requiredConsumer = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("P", producer, [], false, null, null, null),
            new CaptureProcessingGraphNode("A", optionalConsumer, ["P"], true, null, null, null,
                OptionalDependencies: new HashSet<string>(["P"], StringComparer.OrdinalIgnoreCase)),
            new CaptureProcessingGraphNode("B", requiredConsumer, ["P"], true, null, null, null)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, result.Outcome);
        Assert.AreEqual("processing.dependency-unavailable", result.Reason);
        Assert.AreEqual(1, optionalConsumer.ExecutionCount);
        Assert.AreEqual(0, requiredConsumer.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task W6OptionalCloudRetry_DefersFinalManifestAndMaterializationThenRetriesLane()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var cloud = new OutcomeStep(ProcessingOutcome.RetryableFailure("cloud.reference-unavailable"));
        var manifest = new CountingStep();
        var materializer = new CountingStep();
        var graph = CreateW6OptionalCloudGraph(cloud, manifest, materializer);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, result.Outcome);
        Assert.AreEqual("cloud.reference-unavailable", result.Reason);
        Assert.AreEqual(0, manifest.ExecutionCount);
        Assert.AreEqual(0, materializer.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task W6OptionalCloudTerminal_StillBuildsFinalManifestAndMaterializationSuccessfully()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var cloud = new OutcomeStep(ProcessingOutcome.TerminalFailure("cloud.invalid-reference"));
        var manifest = new CountingStep();
        var materializer = new CountingStep();
        var graph = CreateW6OptionalCloudGraph(cloud, manifest, materializer);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        Assert.AreEqual(1, manifest.ExecutionCount);
        Assert.AreEqual(1, materializer.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task OptionalCloudRetry_RestartCommitsManifestAndMaterializerWithExactCloudLineage()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
            var retryGraph = CreateDurableOptionalCloudGraph(
                new OutcomeStep(ProcessingOutcome.RetryableFailure("cloud.retry")));

            var retry = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, retryGraph.Graph, persistence, telemetry, 1,
                NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, retry.Outcome);
            Assert.AreEqual(DurableProcessingNodeStatus.RetryableFailure, (await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, "cloud", CancellationToken.None).ConfigureAwait(false))!.Status);
            Assert.IsNull(await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                "cloud-presentation", CancellationToken.None).ConfigureAwait(false));
            Assert.IsNull(await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                "overlay-manifest", CancellationToken.None).ConfigureAwait(false));
            Assert.IsNull(await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                "presentation-materializer", CancellationToken.None).ConfigureAwait(false));

            var restarted = CreateDurableOptionalCloudGraph(new MetadataProducingStep(variant: "cloud"));
            var completed = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, restarted.Graph, persistence, telemetry, 2,
                NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, completed.Outcome, completed.Reason);
            var cloud = await RequiredOutputAsync("cloud").ConfigureAwait(false);
            var cloudPresentation = await RequiredOutputAsync("cloud-presentation").ConfigureAwait(false);
            var manifest = await RequiredOutputAsync("overlay-manifest").ConfigureAwait(false);
            var materializer = await RequiredOutputAsync("presentation-materializer").ConfigureAwait(false);
            CollectionAssert.AreEqual(new[] { cloud.ArtifactId },
                cloudPresentation.ProductManifest!.Artifact.SourceArtifactIds.ToArray());
            CollectionAssert.AreEqual(new[] { cloudPresentation.ArtifactId },
                manifest.ProductManifest!.Artifact.SourceArtifactIds.ToArray());
            CollectionAssert.AreEqual(new[] { manifest.ArtifactId, cloudPresentation.ArtifactId },
                materializer.ProductManifest!.Artifact.SourceArtifactIds.ToArray());

            async Task<DurableProcessingOutput> RequiredOutputAsync(string nodeId)
            {
                var node = await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                    nodeId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(node);
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status);
                Assert.HasCount(1, node.Outputs);
                return node.Outputs[0];
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task OptionalCloudTerminal_CommitsCloudlessManifestAndMaterializerSuccessfully()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var graph = CreateDurableOptionalCloudGraph(
                new OutcomeStep(ProcessingOutcome.TerminalFailure("cloud.terminal")));

            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, graph.Graph, CreatePersistence(fixture.Options, store, storage, telemetry), telemetry, 1,
                NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            Assert.AreEqual(DurableProcessingNodeStatus.TerminalFailure, (await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, "cloud", CancellationToken.None).ConfigureAwait(false))!.Status);
            Assert.AreEqual(DurableProcessingNodeStatus.Skipped, (await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId, "cloud-presentation",
                CancellationToken.None).ConfigureAwait(false))!.Status);
            var manifest = await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                "overlay-manifest", CancellationToken.None).ConfigureAwait(false);
            var materializer = await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                "presentation-materializer", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, manifest!.Status);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, materializer!.Status);
            CollectionAssert.AreEqual(new[] { fixture.Manifest.Descriptor.Artifact.ArtifactId },
                manifest.Outputs.Single().ProductManifest!.Artifact.SourceArtifactIds.ToArray());
            CollectionAssert.AreEqual(new[] { manifest.Outputs.Single().ArtifactId },
                materializer.Outputs.Single().ProductManifest!.Artifact.SourceArtifactIds.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (CaptureProcessingGraph Graph, DependencyMetadataProducingStep CloudPresentation,
        DependencyMetadataProducingStep Manifest, DependencyMetadataProducingStep Materializer)
        CreateDurableOptionalCloudGraph(ICaptureProcessingStep cloud)
    {
        var cloudPresentation = new DependencyMetadataProducingStep("cloud-presentation", "cloud-layer");
        var manifest = new DependencyMetadataProducingStep("overlay-manifest", "manifest");
        var materializer = new DependencyMetadataProducingStep("presentation-materializer", "materialized");
        var optionalCloud = new HashSet<string>(["cloud-presentation"], StringComparer.OrdinalIgnoreCase);
        return (new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("cloud", cloud, [], false, "cloud", FrameArtifactRole.Metadata,
                "cloud", new string('1', 64)),
            new CaptureProcessingGraphNode("cloud-presentation", cloudPresentation, ["cloud"], false,
                cloudPresentation.RecipeName, cloudPresentation.OutputRole, cloudPresentation.OutputVariant,
                new string('2', 64)),
            new CaptureProcessingGraphNode("overlay-manifest", manifest, ["cloud-presentation"], true,
                manifest.RecipeName, manifest.OutputRole, manifest.OutputVariant, new string('3', 64),
                OptionalDependencies: optionalCloud),
            new CaptureProcessingGraphNode("presentation-materializer", materializer,
                ["overlay-manifest", "cloud-presentation"], true, materializer.RecipeName,
                materializer.OutputRole, materializer.OutputVariant, new string('4', 64),
                OptionalDependencies: optionalCloud)
        ]), cloudPresentation, manifest, materializer);
    }

    private static CaptureProcessingGraph CreateW6OptionalCloudGraph(
        ICaptureProcessingStep cloud,
        ICaptureProcessingStep manifest,
        ICaptureProcessingStep materializer)
    {
        var optionalCloud = new HashSet<string>(["cloud-presentation"], StringComparer.OrdinalIgnoreCase);
        return new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("cloud-presentation", cloud, [], false, null, null, null),
            new CaptureProcessingGraphNode("overlay-manifest", manifest, ["cloud-presentation"], true,
                null, null, null, OptionalDependencies: optionalCloud),
            new CaptureProcessingGraphNode("presentation-materializer", materializer,
                ["overlay-manifest", "cloud-presentation"], true, null, null, null,
                OptionalDependencies: optionalCloud)
        ]);
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
        var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
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
        var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
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

    private static async Task<CaptureProcessingGraph> CreateProductionLayeredGraphAsync(Fixture fixture, string root)
    {
        var descriptor = fixture.Manifest.Descriptor;
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(
            new VisibleSceneRequest(descriptor.Timing.ExposureStartedUtc, new ObserverLocation(0, 0, 0),
                new EquidistantProjectionContext(1, 1, 1, 1, WidthPixels: 2, HeightPixels: 2),
                new CatalogQuery(6.5, 10),
                new CatalogMetadata("test", "1", new Uri("https://example.invalid"), new string('A', 64), "test", "1"),
                projectionVersion: "projection-v1")).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(2, 2),
            new ProjectedSceneSource(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(descriptor)), "calibration-v1", "projection-v1");
        var projected = new FixedMetadataProducingStep(CreateProjectedSceneProduct(fixture.Item, scene, "projected-scene-v1"));
        var cloud = new FixedMetadataProducingStep(CreateCloudAssessmentProduct(fixture.Item));
        var rollingProduct = CreatePackedProduct(
            fixture.Item, FrameArtifactRole.Combined, BuiltInProcessingRecipes.RollingMean, "rolling-mean", [0, 64, 128, 255]);
        var rolling = new FixedArtifactProducingStep(rollingProduct);
        var preview = new FixedArtifactProducingStep(CreatePackedProduct(
            fixture.Item, FrameArtifactRole.Preview, "combined-preview", "combined-preview", [0, 64, 128, 255],
            [CaptureProcessingContext.CreateArtifactId(rollingProduct.OutputIdentitySha256)]));
        var sceneLayer = new ScenePresentationLayerCaptureProcessingStep(
            new CaptureProcessingStepMetadata("scene-presentation", "ScenePresentationLayer", 70),
            new ScenePresentationLayerProcessingStepOptions
            {
                AnnotationOutputVariant = "scene-layer",
                CardinalOutputVariant = "cardinal-layer",
                ImageCircleOutputVariant = "image-circle-layer",
                ConstellationOutputVariant = "constellation-layer"
            });
        var cloudLayer = new CloudPresentationLayerCaptureProcessingStep(
            new CaptureProcessingStepMetadata("cloud-presentation", "CloudPresentationLayer", 71),
            new CloudPresentationLayerProcessingStepOptions
            {
                MaskOutputVariant = "cloud-mask",
                LabelOutputVariant = "cloud-label",
                WidthPixels = 2,
                HeightPixels = 2,
                DrawLabels = false
            });
        using var environmentStore = new SqliteEnvironmentalObservationOutbox();
        var hostOptions = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
        var associationService = new EnvironmentalAssociationService(
            environmentStore, environmentStore, hostOptions, TimeProvider.System);
        var environment = new EnvironmentPresentationLayerCaptureProcessingStep(
            new CaptureProcessingStepMetadata("environment-presentation", "EnvironmentPresentationLayer", 72),
            new EnvironmentPresentationLayerProcessingStepOptions
            {
                FactsOutputVariant = "environment-facts",
                OutputVariant = "environment-layer",
                StackPreviewVariant = "combined-preview",
                WidthPixels = 2,
                HeightPixels = 2,
                EnvironmentalKinds = []
            }, new PresentationMetadataFactsBuilder(associationService, environmentStore, hostOptions));
        var manifest = new OverlayManifestCaptureProcessingStep(
            new CaptureProcessingStepMetadata("overlay-manifest", "OverlayManifest", 80),
            new OverlayManifestProcessingStepOptions
            {
                OutputVariant = "overlay-manifest",
                BasePreviewVariant = "combined-preview",
                SceneAnnotationVariant = "scene-layer",
                SceneCardinalVariant = "cardinal-layer",
                SceneImageCircleVariant = "image-circle-layer",
                SceneConstellationVariant = "constellation-layer",
                CloudMaskVariant = "cloud-mask",
                CloudLabelVariant = "cloud-label",
                EnvironmentFactsVariant = "environment-facts",
                EnvironmentVariant = "environment-layer"
            });
        var materializer = new PresentationMaterializerCaptureProcessingStep(
            new CaptureProcessingStepMetadata("presentation-materializer", "PresentationMaterializer", 81),
            new PresentationMaterializerProcessingStepOptions
            {
                OutputVariant = "annotated-preview",
                BasePreviewVariant = "combined-preview",
                SceneAnnotationVariant = "scene-layer",
                SceneCardinalVariant = "cardinal-layer",
                SceneImageCircleVariant = "image-circle-layer",
                SceneConstellationVariant = "constellation-layer",
                CloudMaskVariant = "cloud-mask",
                CloudLabelVariant = "cloud-label",
                EnvironmentFactsVariant = "environment-facts",
                EnvironmentVariant = "environment-layer"
            });
        return new CaptureProcessingGraph([
            Node("projected-scene", projected, []),
            Node("cloud", cloud, []),
            Node("rolling", rolling, []),
            Node("combined-preview", preview, ["rolling"]),
            Node("scene-presentation", sceneLayer, ["projected-scene"]),
            Node("cloud-presentation", cloudLayer, ["cloud"]),
            Node("environment-presentation", environment, ["projected-scene", "rolling", "combined-preview"]),
            Node("overlay-manifest", manifest,
                ["combined-preview", "scene-presentation", "cloud-presentation", "environment-presentation"]),
            Node("presentation-materializer", materializer,
                ["combined-preview", "overlay-manifest", "scene-presentation", "cloud-presentation", "environment-presentation"])
        ]);

        static CaptureProcessingGraphNode Node(string id, ICaptureProcessingStep step, IReadOnlyList<string> dependencies)
        {
            var graphStep = (ICaptureProcessingGraphStep)step;
            return new(id, step, dependencies, true, graphStep.RecipeName, graphStep.OutputRole,
                graphStep.OutputVariant, CaptureContractJson.ComputeCanonicalJsonSha256(new { id }));
        }
    }

    private static ProcessingProduct CreateCloudAssessmentProduct(FrameProcessingItem item)
    {
        var descriptor = item.RawCapture!.Manifest.Descriptor;
        var source = new CloudAssessmentSourceV1(descriptor.Artifact.ArtifactId, FrameArtifactRole.Calibrated,
            "calibrated", new string('A', 64));
        var reference = source with { ArtifactId = Guid.Parse("90000000-0000-0000-0000-000000000001") };
        var assessment = new CloudAssessmentV1(CloudAssessmentV1.CurrentSchemaVersion,
            CloudAssessmentStatus.Quantified, CloudAssessmentQuality.Degraded,
            [CloudAssessmentReasonCodes.EnvironmentMissing], 0, 850_000,
            new CloudAssessmentGridV1(1, 1, 850_000, 1, 0, 4, 0),
            [new CloudAssessmentRegionV1(0, 0, 0, 0, 2, 2, 4, 4, 0, 1_000_000, false)],
            new CloudAssessmentMaskV1(CloudAssessmentMaskV1.RowMajorLsbFirst, 1, 1, new byte[] { 0 }), source, reference,
            new CloudAssessmentCalibrationV1(0, ushort.MaxValue, ushort.MaxValue,
                "calibration", "mask", "sensor", "processing"),
            new CloudAssessmentEnvironmentV1(CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
                CaptureSolarRegime.Night, EnvironmentalObservationMatchStatus.Missing, null, null, false),
            new string('B', 64), [new ProcessingAlgorithmIdentity("cloud", "v1")]);
        var payload = CloudAssessmentJson.Serialize(assessment);
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            BuiltInProcessingRecipes.CloudAssessment, "1.0.0", "test", JsonSerializer.SerializeToElement(new { })));
        return new ProcessingProduct(FrameArtifactRole.Metadata, "cloud-assessment-v1",
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, "cloud-assessment-v1",
                recipe.IdentitySha256, [descriptor.Artifact.ArtifactId]), "application/json", null, payload,
            ProcessingIdentity.ComputePayloadSha256(payload), recipe, assessment.Algorithms,
            [descriptor.Artifact.ArtifactId], TimeSpan.Zero,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor))
        {
            Kind = ProcessingProductKind.Metadata,
            SchemaVersion = CloudAssessmentV1.CurrentSchemaVersion,
            ContentIdentitySha256 = assessment.AssessmentIdentitySha256
        };
    }

    private static ProcessingProduct CreatePackedProduct(FrameProcessingItem item, FrameArtifactRole role,
        string recipeName, string variant, byte[] payload, IReadOnlyList<Guid>? sources = null)
    {
        var descriptor = item.RawCapture!.Manifest.Descriptor;
        sources ??= [descriptor.Artifact.ArtifactId];
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            recipeName, "1.0.0", "test", JsonSerializer.SerializeToElement(new { variant })));
        return new ProcessingProduct(role, variant,
            ProcessingIdentity.CreateOutputIdentity(role, variant, recipe.IdentitySha256, sources),
            "application/x-hvo-packed-image", descriptor.Layout with
            {
                PixelFormat = CameraPixelFormat.Mono8,
                StrideBytes = 2,
                ByteOrder = FrameByteOrder.NotApplicable,
                ContainerDepthBits = 8,
                SampleDepthBits = 8,
                Packing = FrameSamplePacking.ByteAligned,
                WhiteLevel = byte.MaxValue,
                ByteLength = payload.Length
            }, payload, ProcessingIdentity.ComputePayloadSha256(payload), recipe,
            [new ProcessingAlgorithmIdentity(recipeName, "v1")], sources,
            role == FrameArtifactRole.Combined ? TimeSpan.FromSeconds(5) : TimeSpan.Zero,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor));
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
            CapturePipelineConfig.Empty,
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

    private class ProducingStep(
        string name = "normalize",
        string outputVariant = "none",
        string recipeVersion = "test-v1") :
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
                recipeVersion,
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

    private sealed class MetadataProducingStep(
        bool typed = true,
        int sourceCount = 1,
        string schemaVersion = "test-metadata-v1",
        string variant = "test-metadata-v1",
        string? contentIdentity = null) : ICaptureProcessingStep, ICaptureProcessingGraphStep
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
        public string OutputVariant => variant;
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
                schemaVersion,
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
                SchemaVersion = typed ? schemaVersion : null,
                ContentIdentitySha256 = typed ? contentIdentity ?? ProcessingIdentity.ComputePayloadSha256(payload) : null
            };
            context.AddProcessingOutcome(ProcessingOutcome.Produced(Product));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedMetadataProducingStep(ProcessingProduct product) :
        ICaptureProcessingStep, ICaptureProcessingGraphStep
    {
        public bool Enabled => true;
        public string Name => product.Variant;
        public int Order => 0;
        public string RecipeName => product.Recipe.Descriptor.Name;
        public FrameArtifactRole OutputRole => product.Role;
        public string OutputVariant => product.Variant;
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
            new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Produced(product));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedArtifactProducingStep(ProcessingProduct product) :
        ICaptureProcessingStep, ICaptureProcessingGraphStep
    {
        public bool Enabled => true;
        public string Name => product.Variant;
        public int Order => 0;
        public string RecipeName => product.Recipe.Descriptor.Name;
        public FrameArtifactRole OutputRole => product.Role;
        public string OutputVariant => product.Variant;
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
            new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw, FrameArtifactRole.Combined };

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            var raw = context.Artifacts!.Raw.Frame;
            var frame = CameraAgentRecipeExecutionAdapter.CreateFrame(product, raw, Name);
            var artifact = context.AddDerivative(product.Role, frame, product.Recipe.IdentitySha256,
                product.SourceArtifactIds, CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
            context.AssociateProcessingProduct(artifact, product);
            context.AddProcessingOutcome(ProcessingOutcome.Produced(product));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DependencyMetadataProducingStep(string name, string variant) :
        ICaptureProcessingStep, ICaptureProcessingGraphStep
    {
        public bool Enabled => true;
        public string Name => name;
        public int Order => 0;
        public string RecipeName => $"test-{name}";
        public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
        public string OutputVariant => variant;
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
            new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata };

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            var dependencies = context.GetDependencyProducts();
            var sources = dependencies.Count == 0
                ? new[] { context.Artifacts!.Raw.ArtifactId }
                : dependencies.Select(product =>
                    CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256)).ToArray();
            var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                RecipeName, "1.0.0", "test-v1", JsonSerializer.SerializeToElement(new { variant })));
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                sources = dependencies.Select(static product => product.OutputIdentitySha256).ToArray()
            });
            var compatibility = dependencies.Count > 0 ? dependencies[0].Compatibility :
                new ProcessingCompatibilityIdentity(
                    "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile");
            var product = new ProcessingProduct(
                OutputRole, OutputVariant,
                ProcessingIdentity.CreateOutputIdentity(OutputRole, OutputVariant, recipe.IdentitySha256, sources),
                "application/json", null, payload, ProcessingIdentity.ComputePayloadSha256(payload), recipe,
                [new ProcessingAlgorithmIdentity("test", "v1")], sources, TimeSpan.Zero, compatibility)
            {
                Kind = ProcessingProductKind.Metadata,
                SchemaVersion = "test-dependency-v1",
                ContentIdentitySha256 = ProcessingIdentity.ComputePayloadSha256(payload)
            };
            context.AddProcessingOutcome(ProcessingOutcome.Produced(product));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DependencyCountingStep : ICaptureProcessingStep
    {
        public string Name => "continuation";
        public int Order => 100;
        public int ExecutionCount { get; private set; }
        public string? DependencySchemaVersion { get; private set; }

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            DependencySchemaVersion = context.GetDependencyProducts().Single().SchemaVersion;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RetentionConfigurationAccessor : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => false;
        public void SetConfiguration(CameraModuleConfig config) { }
        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<CameraModuleConfig>(new InvalidOperationException("Not used by direct retention tests."));
    }

    private sealed class RetentionTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RetentionCapacityProvider : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot) => new(1000, 500);
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
        public int ExecutionCount { get; private set; }
        public string Name => "annotation";
        public int Order => 70;
        public string RecipeName => "test-packed-annotation";
        public FrameArtifactRole OutputRole => FrameArtifactRole.AnnotatedPreview;
        public string OutputVariant => "production-like-annotated";
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
            new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
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

    private sealed class RestoredFrameInspectingStep(
        SceneProvenance expectedScene,
        string expectedRecipeVersion) : ICaptureProcessingStep
    {
        public string Name => "inspect";
        public int Order => 1;
        public bool SawExpectedScene { get; private set; }

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            var dependency = context.GetDependencyArtifacts().Single();
            Assert.AreEqual(expectedScene, dependency.Frame.Metadata.Scene);
            Assert.AreEqual(expectedRecipeVersion, dependency.RecipeVersion);
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
