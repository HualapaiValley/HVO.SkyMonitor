using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("accepted")]
    [DataRow("unavailable")]
    [DataRow("rejected")]
    [DataRow("budget")]
    [DataRow("tamper-solution")]
    [DataRow("tamper-context")]
    [DataRow("tamper-scene")]
    [DataRow("tamper-measured")]
    [DataRow("tamper-raw")]
    [DataRow("interrupted")]
    [DataRow("interrupted-budget")]
    public async Task RetainedAstrometry_RawAdmissionRestartReadExportAndReconstructionPreserveIdentity(string scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var root = CreateTestRoot();
        try
        {
            var interrupted = scenario.StartsWith("interrupted", StringComparison.Ordinal);
            var outcomeScenario = scenario == "interrupted-budget" ? "budget" :
                scenario.StartsWith("tamper-", StringComparison.Ordinal) || interrupted ? "accepted" : scenario;
            var f = await CreateAstrometryDurabilityFixtureAsync(root, outcomeScenario).ConfigureAwait(false);
            Dictionary<Guid, byte[]> payloads;
            Dictionary<Guid, byte[]> manifests;
            string identity;
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(f.Fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            using (var gate = new CaptureAstrometryExecutionGate())
            {
                var persistence = new CaptureProcessingPersistence(f.Fixture.Options, store, storage, telemetry,
                    NullLogger<CaptureProcessingPersistence>.Instance, interrupted ? new AstrometryCommitInterruption() : null);
                var graph = AstrometryGraph(f, new(new ProcessingRecipeExecutor()), gate, persistence: persistence);
                if (interrupted)
                {
                    await Assert.ThrowsExactlyAsync<IOException>(async () => await FrameProcessingWorker.ProcessGraphItemAsync(
                        f.Fixture.Item, graph, persistence, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                    graph = AstrometryGraph(f, new(new FixedOutcomeExecutor(ProcessingOutcome.TerminalFailure("unexpected-retry-fit"))),
                        gate, persistence: persistence);
                }
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(f.Fixture.Item, graph, persistence,
                    telemetry, interrupted ? 2 : 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                var node = await store.ReadNodeAsync(f.Fixture.Manifest.Descriptor.Capture.CaptureId, "astrometry", CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(node);
                Assert.HasCount(2, node.Outputs);
                var solutionOutput = node.Outputs.Single(p => p.ProductSchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
                var solution = MeasuredAstrometryJson.Parse(await File.ReadAllBytesAsync(Path.Combine(root, solutionOutput.PayloadRelativePath)).ConfigureAwait(false));
                identity = solution.SolutionIdentitySha256;
                Assert.AreEqual(outcomeScenario == "accepted" ? AstrometricAssessmentStatus.Accepted : outcomeScenario == "rejected" ? AstrometricAssessmentStatus.Rejected : outcomeScenario == "budget"
                    ? AstrometricAssessmentStatus.BudgetExceeded : AstrometricAssessmentStatus.Unavailable, solution.Assessment.Status, solution.Assessment.ReasonCode);
                Assert.AreEqual(4, solutionOutput.Artifact.SourceArtifactIds.Count);
                if (outcomeScenario == "accepted")
                {
                    Assert.IsTrue(solution.Assessment.Quality!.InlierCount >= 8);
                    Assert.IsTrue(solution.Residuals.VerificationRmsPixels < .5);
                    Assert.IsNotNull(solution.Uncertainty);
                    Console.WriteLine($"retained identity={identity} inliers={solution.Assessment.Quality.InlierCount} fitRms={solution.Residuals.FittingRmsPixels:R} verificationRms={solution.Residuals.VerificationRmsPixels:R}");
                }
                var holds = await store.ReadRetentionHoldsAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var id in solutionOutput.Artifact.SourceArtifactIds) Assert.IsTrue(holds.Any(h => h.ArtifactId == id));
                var all = new List<DurableProcessingOutput>();
                foreach (var id in new[] { "scene", "measured", "astrometry" })
                    all.AddRange((await store.ReadNodeAsync(f.Fixture.Manifest.Descriptor.Capture.CaptureId, id, CancellationToken.None).ConfigureAwait(false))!.Outputs);
                payloads = new(); manifests = new();
                foreach (var output in all)
                {
                    payloads.Add(output.ArtifactId, await File.ReadAllBytesAsync(Path.Combine(root, output.PayloadRelativePath)).ConfigureAwait(false));
                    var m = output.ProductManifest!;
                    var upload = new StructuredProcessingProductManifestV1(StructuredProcessingProductManifestV1.CurrentSchemaVersion,
                        new(f.Fixture.Manifest.Descriptor, m.Artifact, m.OutputIdentitySha256, m.Algorithms, m.Compatibility,
                            m.TotalIntegrationTicks, m.ByteLength, m.Kind, m.ProductSchemaVersion!, m.ContentIdentitySha256!),
                        m.RelativeArtifactPath, null);
                    manifests.Add(output.ArtifactId, StructuredProcessingProductManifestJson.Serialize(upload));
                }
            }
            using var restartedTelemetry = new CaptureProcessingTelemetry();
            using var restartedStore = new SqliteCaptureProcessingStore(f.Fixture.Options);
            using var restartedStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var restartedGate = new CaptureAstrometryExecutionGate();
            var restartedPersistence = CreatePersistence(f.Fixture.Options, restartedStore, restartedStorage, restartedTelemetry);
            var noExecution = new CameraAgentRecipeExecutionAdapter(new FixedOutcomeExecutor(ProcessingOutcome.TerminalFailure("unexpected-refit")));
            var resumed = await FrameProcessingWorker.ProcessGraphItemAsync(f.Fixture.Item, AstrometryGraph(f, noExecution, restartedGate, persistence: restartedPersistence),
                restartedPersistence, restartedTelemetry, 2, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, resumed.Outcome, resumed.Reason);
            if (scenario.StartsWith("tamper-", StringComparison.Ordinal))
            {
                var schema = scenario switch
                {
                    "tamper-solution" => MeasuredAstrometrySolutionV1.CurrentSchemaVersion,
                    "tamper-context" => MeasuredAstrometryContextV1.CurrentSchemaVersion,
                    "tamper-scene" => ProjectedSceneV1.CurrentSchemaVersion,
                    _ => MeasuredStellarAssociationsV1.CurrentSchemaVersion
                };
                var path = scenario == "tamper-raw" ? Path.Combine(root, "raw.bin") : Path.Combine(root,
                    (await restartedStore.ReadOutputByArtifactIdAsync(manifests.Keys.Single(id => ImportAstrometryProduct(manifests[id], payloads[id]).SchemaVersion == schema), CancellationToken.None).ConfigureAwait(false))!.PayloadRelativePath);
                await File.AppendAllTextAsync(path, " ").ConfigureAwait(false);
            }
            using var artifactReader = new CameraAgentArtifactService(f.Fixture.Options, restartedStore, new CameraAgentPreviewEncoder());
            using var astrometryReader = new CameraAgentRetainedAstrometryService(restartedStore, artifactReader);
            var view = await astrometryReader.GetAsync(f.Fixture.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            if (scenario.StartsWith("tamper-", StringComparison.Ordinal))
            {
                Assert.AreEqual("Unavailable", view.Availability);
                Assert.IsNull(view.Solution);
                return;
            }
            Assert.AreEqual("Retained", view.Availability, view.Reason);
            Assert.AreEqual(identity, view.SolutionIdentitySha256);
            Assert.AreEqual(outcomeScenario == "accepted" ? "MeasuredFit" : "Unavailable", view.GeometryBasis);

            // Import the exact wire descriptors and bytes with no CameraAgent state or current catalog/configuration.
            var artifacts = manifests.ToDictionary(p => p.Key, p => ImportAstrometryProduct(p.Value, payloads[p.Key]));
            var original = artifacts.Values.Single(p => p.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
            var rawDescriptor = f.Fixture.Manifest.Descriptor;
            var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(f.Fixture.Item.Config, new FrameArtifact(rawDescriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, f.Fixture.Item.Submission.Result.Frame!, []),
                rawDescriptor.Artifact.Variant, reconstructionDescriptor: rawDescriptor);
            var measured = artifacts[view.Solution!.MeasuredArtifactId];
            var scene = artifacts.Values.Single(p => ProjectedSceneV1.IsSupportedSchemaVersion(p.SchemaVersion));
            var context = artifacts.Values.Single(p => p.SchemaVersion == MeasuredAstrometryContextV1.CurrentSchemaVersion);
            var request = RetainedAstrometryProducts.Request(raw, measured, scene, context, f.Options.Recipe, original.Variant);
            var verified = RetainedAstrometryProducts.Verify(request, RetainedAstrometryProducts.Product(original));
            Assert.AreEqual(identity, verified.SolutionIdentitySha256);
            var replaySubmission = f.Fixture.Item.Submission with
            {
                Result = f.Fixture.Item.Submission.Result with
                {
                    Artifacts = new FrameArtifactSet(new FrameArtifact(rawDescriptor.Artifact.ArtifactId, FrameArtifactRole.Raw,
                    f.Fixture.Item.Submission.Result.Frame!, []))
                }
            };
            var replay = new CaptureProcessingContext(f.Fixture.Item.Config, replaySubmission, f.Fixture.Item.RawCapture,
                null, new(Guid.NewGuid(), ProcessingGraphExecutionClass.Replay, "frozen-revision", new string('F', 64), false, 1, "lease"));
            foreach (var (nodeId, a) in new[] { ("scene", scene), ("measured", measured) })
            {
                var m = StructuredProcessingProductManifestJson.Parse(manifests[a.ArtifactId]).Manifest!.Descriptor;
                var p = new ProcessingProduct(a.Role, a.Variant, m.OutputIdentitySha256, a.MediaType, null, a.Payload,
                    m.Artifact.ChecksumSha256, ProcessingIdentity.CreateRecipeIdentity(m.Artifact.Recipe), m.Algorithms,
                    m.Artifact.SourceArtifactIds, a.Integration, a.Compatibility)
                { Kind = a.ProductKind, SchemaVersion = a.SchemaVersion, ContentIdentitySha256 = a.ContentIdentitySha256 };
                replay.RestoreProduct(nodeId, a.ArtifactId, p);
            }
            replay.BeginNode("astrometry", ["scene", "measured"]);
            replay.SetFrozenAuxiliaryInputs([context, original]);
            var replayStep = new MeasuredAstrometryCaptureProcessingStep(new("astrometry", "MeasuredAstrometry", 2),
                new() { Catalog = f.Options.Catalog! with { Complete = !f.Options.Catalog.Complete }, Recipe = new(MaximumExecutionMilliseconds: .000001) }, noExecution, restartedGate);
            await replayStep.ProcessAsync(replay, CancellationToken.None).ConfigureAwait(false);
            var replaySolution = replay.ProcessingOutcomes.SelectMany(o => o.Products)
                .Single(p => p.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
            CollectionAssert.AreEqual(original.Payload.ToArray(), replaySolution.Payload.ToArray());
            Assert.AreEqual(identity, replaySolution.ContentIdentitySha256);
            if (outcomeScenario == "accepted")
            {
                var central = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);
                Assert.AreEqual(ProcessingOutcomeStatus.Produced, central.Status, central.ReasonCode);
                CollectionAssert.AreEqual(original.Payload.ToArray(), central.Products.Single().Payload.ToArray());
                Assert.AreEqual(original.ArtifactId, ProcessingIdentity.CreateArtifactId(central.Products.Single().OutputIdentitySha256));
            }
            var completed = await restartedStore.ReadNodeAsync(rawDescriptor.Capture.CaptureId, "astrometry", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(interrupted ? 2 : 1, completed!.Attempt, "Restart must restore the original accepted or failed assessment without refitting.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("raw-limit")]
    [DataRow("solution-limit")]
    [DataRow("total-limit")]
    [DataRow("raw-envelope")]
    [DataRow("product-envelope")]
    [DataRow("cancel")]
    public async Task RetainedAstrometry_InvalidReadEvidenceReturnsUnavailableAndCancellationPropagates(string fault)
    {
        var root = CreateTestRoot();
        try
        {
            var f = await CreateAstrometryDurabilityFixtureAsync(root, "unavailable").ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(f.Fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(f.Fixture.Options, store, storage, telemetry);
            var processed = await FrameProcessingWorker.ProcessGraphItemAsync(f.Fixture.Item,
                AstrometryGraph(f, new(new ProcessingRecipeExecutor()), gate, persistence: persistence), persistence,
                telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);
            using var real = new CameraAgentArtifactService(f.Fixture.Options, store, new CameraAgentPreviewEncoder());
            var decorated = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            using var cancellation = new CancellationTokenSource();
            decorated.Setup(a => a.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid id, CancellationToken token) =>
                {
                    if (fault == "cancel")
                    {
                        await cancellation.CancelAsync().ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                    }
                    var result = await real.OpenContentAsync(id, token).ConfigureAwait(false);
                    var content = result.Content!;
                    var raw = content.Role == FrameArtifactRole.Raw;
                    var bytes = content.ByteLength;
                    if (fault == "raw-limit" && raw) bytes = CameraAgentRetainedAstrometryService.MaximumRawBytes + 1L;
                    if (fault == "solution-limit" && !raw) bytes = MeasuredAstrometryJson.MaximumSolutionBytes + 1L;
                    var capture = fault == "raw-envelope" && raw || fault == "product-envelope" && !raw ? Guid.NewGuid() : content.CaptureId;
                    return result with
                    {
                        Content = new CameraAgentArtifactContentStream(content, content.ArtifactId, capture,
                        content.Role, content.MediaType, bytes, content.ChecksumSha256, content.FileName, content.Descriptor)
                        { ProductManifest = content.ProductManifest }
                    };
                });
            // Lower the aggregate budget to exercise accounting independently of individual limits.
            // At production limits the sum of the five individual caps is already below 44 MiB.
            using var reader = new CameraAgentRetainedAstrometryService(store, decorated.Object,
                fault == "total-limit" ? MeasuredAstrometryJson.MaximumSolutionBytes : CameraAgentRetainedAstrometryService.MaximumReadBytes);
            if (fault == "cancel")
            {
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                    await reader.GetAsync(f.Fixture.Manifest.Descriptor.Capture.CaptureId, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
                return;
            }
            var view = await reader.GetAsync(f.Fixture.Manifest.Descriptor.Capture.CaptureId, cancellation.Token).ConfigureAwait(false);
            Assert.AreEqual("Unavailable", view.Availability);
            Assert.IsNull(view.Solution);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ProcessingArtifact ImportAstrometryProduct(byte[] manifestBytes, byte[] bytes)
    {
        var parsed = StructuredProcessingProductManifestJson.Parse(manifestBytes);
        Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
        var d = parsed.Manifest!.Descriptor;
        Assert.AreEqual(d.Artifact.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(bytes));
        return new(d.Artifact.ArtifactId, d.Artifact.Role, d.Artifact.Variant!, ProcessingIdentity.CreateRecipeIdentity(d.Artifact.Recipe).IdentitySha256,
            d.Artifact.MediaType, null, bytes, d.Artifact.CreatedUtc, TimeSpan.FromTicks(d.TotalIntegrationTicks), d.Compatibility,
            SourceArtifactIds: d.Artifact.SourceArtifactIds)
        {
            CaptureId = d.SourceCapture.Capture.CaptureId,
            ProductKind = d.Kind,
            SchemaVersion = d.ProductSchemaVersion,
            ContentIdentitySha256 = d.ContentIdentitySha256,
            RecipeDescriptor = d.Artifact.Recipe
        };
    }

    private sealed record AstrometryDurabilityFixture(Fixture Fixture, ProcessingAuxiliaryInput Scene, MeasuredAstrometryCaptureOptions Options);

    private sealed class AstrometryCommitInterruption : ICaptureProcessingFaultInjector
    {
        private bool fired;
        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (!fired && point == CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit && nodeId == "astrometry")
            {
                fired = true;
                throw new IOException("Injected interruption after immutable astrometry publication.");
            }
        }
    }

    private static CaptureProcessingGraph AstrometryGraph(AstrometryDurabilityFixture f, CameraAgentRecipeExecutionAdapter adapter,
        CaptureAstrometryExecutionGate gate, bool enabled = true, CaptureProcessingPersistence? persistence = null)
    {
        var scene = new DurabilitySceneRecipeStep(f.Scene, adapter);
        var measured = new MeasuredStellarAssociationCaptureProcessingStep(new("measured", "MeasuredStellarAssociations", 1), new(), adapter);
        var astrometry = new MeasuredAstrometryCaptureProcessingStep(new("astrometry", MeasuredAstrometryCaptureProcessingStep.Alias, 2), f.Options, adapter, gate, persistence);
        return new([
            new("scene", scene, [], true, scene.RecipeName, scene.OutputRole, scene.OutputVariant, new string('A', 64)),
            new("measured", measured, ["scene"], true, measured.RecipeName, measured.OutputRole, measured.OutputVariant, new string('B', 64)),
            .. enabled ? new[] { new CaptureProcessingGraphNode("astrometry", astrometry, ["scene", "measured"], true,
                astrometry.RecipeName, astrometry.OutputRole, astrometry.OutputVariant, new string('C', 64)) } : []
        ]);
    }

    private static async Task<AstrometryDurabilityFixture> CreateAstrometryDurabilityFixtureAsync(string root, string scenario = "accepted")
    {
        var reference = await MeasuredAstrometryFixture.CreateAsync().ConfigureAwait(false);
        var bytes = reference.Raw.Payload.ToArray();
        if (scenario == "rejected")
        {
            var pixels = MeasuredStellarAssociationFixtures.Field(512, 512);
            MeasuredStellarAssociationFixtures.AddNoise(pixels, 1205, 4);
            bytes = MeasuredStellarAssociationFixtures.RawArtifact(pixels, width: 512, height: 512).Payload.ToArray();
        }
        var config = CreateConfig();
        var manifest = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, 512, 512, 1024, bytes);
        var start = reference.Context.Frame.ExposureStartUtc;
        manifest = manifest with
        {
            RelativeArtifactPath = "raw.bin",
            Descriptor = manifest.Descriptor with
            {
                Capture = manifest.Descriptor.Capture with { CaptureSequence = 1 },
                Layout = reference.Raw.Layout!,
                Timing = new(start, start, start.AddSeconds(1), start.AddSeconds(1.1), start.AddSeconds(1.2)),
                Artifact = manifest.Descriptor.Artifact with { CreatedUtc = start.AddSeconds(1.2) },
                Profiles = manifest.Descriptor.Profiles with { Processing = RawCaptureDescriptorFactory.CreateProcessingProfile(config) }
            }
        };
        var descriptor = manifest.Descriptor;
        var scene = reference.Scene with
        {
            Source = new(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
            CaptureContractJson.ComputeDescriptorSha256(descriptor))
        };
        scene = scene with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(scene) };
        var sceneBytes = ProjectedSceneJson.Serialize(scene);
        var input = new ProcessingAuxiliaryInput("scene", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: scene.SchemaVersion, IdentitySha256: scene.SceneIdentitySha256, Payload: sceneBytes)
        { ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(sceneBytes) };
        var manifestBytes = CaptureContractJson.Serialize(manifest);
        await File.WriteAllBytesAsync(Path.Combine(root, "raw.bin"), bytes).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(root, "raw.json"), manifestBytes).ConfigureAwait(false);
        var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await journal.ReserveIdentityAsync(descriptor.Capture.AgentId, descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(RawIngressOutcome.Committed, await journal.CommitAsync(new RawIngressJournalEntry(
            descriptor.Capture.AgentId, 1, descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
            CaptureContractJson.ComputeDescriptorSha256(descriptor), CaptureContractJson.ComputeManifestSha256(manifestBytes),
            descriptor.Artifact.ChecksumSha256, bytes.Length, "raw.bin", "raw.json", manifestBytes, start, descriptor.Timing.DurableIngressUtc), CancellationToken.None).ConfigureAwait(false));
        Assert.IsTrue(FrameReconstructor.TryReconstruct(descriptor, bytes, out var frame).IsValid);
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest,
            new StoredFrameReference("raw.bin", Path.Combine(root, "raw.bin"), start, FrameArtifactRole.Raw), CaptureContractJson.ComputeManifestSha256(manifestBytes));
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
        return new(new(manifest, options, new(config, CreateSubmission(frame!), receipt)), input,
            new()
            {
                Catalog = new(reference.Context.CatalogMetadata, reference.Context.CatalogStars, reference.Context.CatalogPackage,
                scenario != "unavailable", 7),
                Recipe = MeasuredAstrometryFixture.Options with
                { MaximumExecutionMilliseconds = scenario == "budget" ? .000001 : 20000 }
            });
    }
}
