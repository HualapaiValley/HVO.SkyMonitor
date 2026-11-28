using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    private static readonly string[] MeasuredDurabilityNodeIds = ["scene", "measured", "storage"];

    [TestMethod]
    [DataRow("none")]
    [DataRow("payload")]
    [DataRow("sidecar")]
    [TestCategory("Integration")]
    public async Task ActualMeasuredAssociations_PublishAndRestoreExactLocalEvidenceOrRejectTampering(string fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        var root = CreateTestRoot();
        try
        {
            var (fixture, sceneInput) = await CreateMeasuredDurabilityFixtureAsync(root).ConfigureAwait(false);
            DurableProcessingOutput output;
            ProcessingProduct product;
            byte[] payload;
            byte[] sidecar;
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
                var adapter = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
                var observer = new MeasuredDurabilityObservationStep();
                var producerGraph = CreateMeasuredDurabilityGraph(sceneInput, adapter, persistence, fixture.Options, root);
                var graph = new CaptureProcessingGraph([
                    .. producerGraph.Nodes,
                    new CaptureProcessingGraphNode("observe-initial", observer, ["measured"], true,
                        null, null, null, new string('E', 64))
                ]);
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Item, graph, persistence,
                    telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                var node = await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                    "measured", CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(node);
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status);
                output = Assert.ContainsSingle(node.Outputs);
                Assert.IsNotNull(observer.Product);
                product = observer.Product;
                var associations = MeasuredStellarAssociationJson.Parse(product.Payload).Associations;
                Assert.IsNotNull(associations);
                var association = Assert.ContainsSingle(associations.Associations);
                Assert.AreEqual("star:durable", association.CatalogId);
                Assert.IsTrue(association.LabelEligible, association.LabelRejectionReason);
                Assert.AreEqual(fixture.Manifest.Descriptor.Capture.CaptureId, associations.Source.CaptureId);
                Assert.AreEqual(fixture.Manifest.Descriptor.Artifact.ArtifactId, associations.Source.ArtifactId);
                Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(fixture.Manifest.Descriptor),
                    associations.Source.DescriptorIdentitySha256);
                Assert.AreEqual(sceneInput.IdentitySha256!.ToUpperInvariant(), associations.Scene.SceneIdentitySha256);
                Assert.AreEqual(associations.AssociationIdentitySha256, product.ContentIdentitySha256);
                CollectionAssert.AreEqual(new[] { fixture.Manifest.Descriptor.Artifact.ArtifactId },
                    product.SourceArtifactIds.ToArray());
                Assert.IsInstanceOfType<DurableTypedMetadataProductManifestV3>(output.ProductManifest);
                payload = await File.ReadAllBytesAsync(Path.Combine(root, output.PayloadRelativePath)).ConfigureAwait(false);
                sidecar = await File.ReadAllBytesAsync(Path.Combine(root, output.SidecarRelativePath)).ConfigureAwait(false);
                CollectionAssert.AreEqual(product.Payload.ToArray(), payload);
                CollectionAssert.AreEqual(output.EvidenceJson, sidecar);
                Assert.AreEqual(product.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(payload));
            }

            if (fault != "none")
            {
                var path = Path.Combine(root, fault == "payload" ? output.PayloadRelativePath : output.SidecarRelativePath);
                var original = fault == "payload" ? payload : sidecar;
                var tampered = new byte[original.Length + 1];
                tampered[0] = (byte)' ';
                original.CopyTo(tampered, 1);
                await File.WriteAllBytesAsync(path, tampered).ConfigureAwait(false);
            }
            using var restartedTelemetry = new CaptureProcessingTelemetry();
            using var restartedStore = new SqliteCaptureProcessingStore(fixture.Options);
            using var restartedStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var restartedPersistence = CreatePersistence(fixture.Options, restartedStore, restartedStorage, restartedTelemetry);
            var noReexecution = new CameraAgentRecipeExecutionAdapter(new FixedOutcomeExecutor(
                ProcessingOutcome.TerminalFailure("fixture.unexpected-reexecution")));
            var inspector = new RestoredMetadataInspectingStep(product);
            var restartedGraph = CreateMeasuredDurabilityGraph(sceneInput, noReexecution, restartedPersistence, fixture.Options, root);
            var graphWithInspection = new CaptureProcessingGraph([
                .. restartedGraph.Nodes,
                new CaptureProcessingGraphNode("inspect-measured", inspector, ["measured"], true,
                    null, null, null, new string('D', 64))
            ]);
            if (fault == "none")
            {
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Item, graphWithInspection,
                    restartedPersistence, restartedTelemetry, 2, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                Assert.IsTrue(inspector.SawExactProduct);
                foreach (var id in MeasuredDurabilityNodeIds)
                {
                    var node = await restartedStore.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                        id, CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(node);
                    Assert.AreEqual(1, node.Attempt, "Restart must restore, never silently rerun a completed producer.");
                }
                CollectionAssert.AreEqual(payload,
                    await File.ReadAllBytesAsync(Path.Combine(root, output.PayloadRelativePath)).ConfigureAwait(false));
                CollectionAssert.AreEqual(sidecar,
                    await File.ReadAllBytesAsync(Path.Combine(root, output.SidecarRelativePath)).ConfigureAwait(false));
            }
            else
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Item, graphWithInspection,
                        restartedPersistence, restartedTelemetry, 2, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(false);
                Assert.IsFalse(inspector.SawExactProduct);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CaptureProcessingGraph CreateMeasuredDurabilityGraph(ProcessingAuxiliaryInput scene,
        CameraAgentRecipeExecutionAdapter adapter, CaptureProcessingPersistence persistence,
        IOptions<CameraAgentHostOptions> options, string root)
    {
        var sceneStep = new DurabilitySceneRecipeStep(scene, adapter);
        var measured = new MeasuredStellarAssociationCaptureProcessingStep(new("measured", "MeasuredStellarAssociations", 1),
            new MeasuredStellarAssociationProcessingStepOptions(), adapter);
        var storage = new FileStorageCaptureProcessingStep(new("storage", "Storage", 2),
            new FileStorageCaptureProcessingStepOptions { StorageRoot = root, UpdateLatestFrame = false },
            Mock.Of<ILatestFrameAccessor>(), Mock.Of<IFrameStorageService>(),
            NullLogger<FileStorageCaptureProcessingStep>.Instance, persistence);
        return new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("scene", sceneStep, [], true, sceneStep.RecipeName,
                sceneStep.OutputRole, sceneStep.OutputVariant, new string('A', 64)),
            new CaptureProcessingGraphNode("measured", measured, ["scene"], true, measured.RecipeName,
                measured.OutputRole, measured.OutputVariant, new string('B', 64)),
            new CaptureProcessingGraphNode("storage", storage, ["measured"], true, null, null, null, new string('C', 64))
        ]);
    }

    private static async Task<(Fixture Fixture, ProcessingAuxiliaryInput Scene)> CreateMeasuredDurabilityFixtureAsync(string root)
    {
        const int width = 96, height = 64;
        var bytes = new byte[width * height * 2];
        for (var index = 0; index < width * height; index++)
        {
            var dx = (index % width + .5 - width / 2d) / 1.2;
            var dy = (index / width + .5 - height / 2d) / 1.2;
            var sample = (ushort)Math.Round(1000 + MeasuredDurabilityNoise(index) + 4000 * Math.Exp(-.5 * (dx * dx + dy * dy)));
            bytes[index * 2] = (byte)sample;
            bytes[index * 2 + 1] = (byte)(sample >> 8);
        }
        var baseline = CreateConfig();
        var config = baseline with
        {
            Rig = baseline.Rig with { Sensor = baseline.Rig.Sensor with { WidthPixels = width, HeightPixels = height } }
        };
        var manifest = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, width, height, width * 2, bytes);
        manifest = manifest with
        {
            RelativeArtifactPath = "raw.bin",
            Descriptor = manifest.Descriptor with
            {
                Capture = manifest.Descriptor.Capture with { CaptureSequence = 1 },
                Profiles = manifest.Descriptor.Profiles with { Processing = RawCaptureDescriptorFactory.CreateProcessingProfile(config) }
            }
        };
        var descriptor = manifest.Descriptor;
        var utc = descriptor.Timing.ExposureStartedUtc;
        var position = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(90, 0), utc, 0, 0), utc);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("star:durable", "Durable", position.RightAscensionHours, position.DeclinationDegrees, 1)
        ])).BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, width / 2d, height / 2d, 50, 50, width, height,
                ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90), new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "perspective-v1")).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible, ProjectedSceneImageTransformV1.Identity(width, height),
            new ProjectedSceneSource(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(descriptor)), "calibration-v1", "perspective-v1");
        var sceneBytes = ProjectedSceneJson.Serialize(scene);
        var sceneInput = new ProcessingAuxiliaryInput("scene", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: scene.SchemaVersion, IdentitySha256: scene.SceneIdentitySha256, Payload: sceneBytes)
        { ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(sceneBytes) };
        var manifestBytes = CaptureContractJson.Serialize(manifest);
        await File.WriteAllBytesAsync(Path.Combine(root, "raw.bin"), bytes).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(root, "raw.json"), manifestBytes).ConfigureAwait(false);
        var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var reserved = await journal.ReserveIdentityAsync(descriptor.Capture.AgentId, descriptor.Capture.CaptureId,
            descriptor.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(descriptor.Capture.CaptureSequence, reserved.CaptureSequence);
        Assert.AreEqual(RawIngressOutcome.Committed, await journal.CommitAsync(new RawIngressJournalEntry(
            descriptor.Capture.AgentId, descriptor.Capture.CaptureSequence, descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
            CaptureContractJson.ComputeDescriptorSha256(descriptor), CaptureContractJson.ComputeManifestSha256(manifestBytes),
            descriptor.Artifact.ChecksumSha256, bytes.Length, "raw.bin", "raw.json", manifestBytes,
            descriptor.Timing.ExposureStartedUtc, descriptor.Timing.DurableIngressUtc), CancellationToken.None).ConfigureAwait(false));
        Assert.IsTrue(FrameReconstructor.TryReconstruct(descriptor, bytes, out var frame).IsValid);
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest,
            new StoredFrameReference("raw.bin", Path.Combine(root, "raw.bin"), utc, FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifestBytes));
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
        return (new Fixture(manifest, options, new FrameProcessingItem(config, CreateSubmission(frame!), receipt)), sceneInput);
    }

    // Same bounded synthetic noise as the real-producer conformance fixture; periodic ramps have zero adjacent-difference MAD.
    private static int MeasuredDurabilityNoise(int index)
    {
        var bits = (uint)index + 526u;
        bits ^= bits >> 16;
        bits = unchecked(bits * 0x7FEB352Du);
        bits ^= bits >> 15;
        bits = unchecked(bits * 0x846CA68Bu);
        bits ^= bits >> 16;
        return (int)(bits % 13) - 6;
    }

    private sealed class MeasuredDurabilityObservationStep : ICaptureProcessingStep
    {
        public string Name => "observe-initial";
        public int Order => 3;
        public ProcessingProduct? Product { get; private set; }

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            Product = context.GetDependencyProducts().Single();
            return ValueTask.CompletedTask;
        }
    }

    // Declared synthetic scene input traverses the real shared recipe; MSA is never synthesized by the fixture.
    private sealed class DurabilitySceneRecipeStep(ProcessingAuxiliaryInput scene, CameraAgentRecipeExecutionAdapter adapter)
        : ICaptureProcessingStep, ICaptureProcessingGraphStep
    {
        public string Name => "scene";
        public int Order => 0;
        public bool Enabled => true;
        public string RecipeName => BuiltInProcessingRecipes.ProjectedScene;
        public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
        public string OutputVariant => "projected-scene-v1";
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

        public async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            var descriptor = context.ReconstructionDescriptor!;
            var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, context.Artifacts!.Raw,
                descriptor.Artifact.Variant, reconstructionDescriptor: descriptor);
            var outcome = await adapter.ExecuteAsync(context, new ProcessingExecutionRequest(RecipeName,
                JsonSerializer.SerializeToElement(new { }), ProcessingInputSelector.Raw(raw.Variant), [raw], OutputVariant,
                AuxiliaryInputs: [scene], InputArtifactId: raw.ArtifactId), cancellationToken).ConfigureAwait(false);
            context.AddProcessingOutcome(outcome);
        }
    }
}
