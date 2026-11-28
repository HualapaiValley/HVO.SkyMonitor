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
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("astrometry.capture-cadence", "CadenceSkipped", "astrometry.capture-cadence")]
    [DataRow("processing.dependency-unavailable", "Unavailable", "astrometry.inputs-unavailable")]
    public async Task RetainedAstrometry_ReadsTheJournaledSkipReason(string reason, string availability, string assessmentReason)
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var store = SiloInfrastructureFixture.OpenProcessingStore(fixture.Options);
            using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(fixture.Options, store);
            using var service = new CameraAgentRetainedAstrometryService(store, artifacts);
            var captureId = fixture.Manifest.Descriptor.Capture.CaptureId;
            var node = new CaptureProcessingGraphNode("astrometry", new OutcomeStep(ProcessingOutcome.Skipped(reason)),
                ["$raw"], false, BuiltInProcessingRecipes.MeasuredAstrometry, FrameArtifactRole.Metadata, "assessment",
                new string('A', 64));
            await store.WriteNodeAsync(captureId, node, DurableProcessingNodeStatus.Skipped, reason, 1, null,
                null, fixture.Manifest.Descriptor.Timing.DurableIngressUtc, TimeSpan.Zero, ProcessingOutcomeStatus.Skipped,
                [], 0, null, [], CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(reason, await store.ReadAstrometrySkipReasonAsync(captureId, CancellationToken.None).ConfigureAwait(false));
            var assessment = await service.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(captureId, assessment.CaptureId);
            Assert.AreEqual(availability, assessment.Availability);
            Assert.AreEqual(assessmentReason, assessment.Reason);
            Assert.IsNull(assessment.Solution);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("accepted")]
    [DataRow("sparse")]
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
                scenario.StartsWith("tamper-", StringComparison.Ordinal) || scenario == "sparse" || interrupted ? "accepted" : scenario;
            var f = await CreateAstrometryDurabilityFixtureAsync(root, outcomeScenario).ConfigureAwait(false);
            Dictionary<Guid, byte[]> payloads;
            Dictionary<Guid, byte[]> manifests;
            string identity;
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = SiloInfrastructureFixture.OpenProcessingStore(f.Fixture.Options))
            using (var gate = new CaptureAstrometryExecutionGate())
            {
                var persistence = new CaptureProcessingPersistence(f.Fixture.Options, store, telemetry,
                    NullLogger<CaptureProcessingPersistence>.Instance, SiloInfrastructureFixture.ProductStorage(root),
                    interrupted ? new AstrometryCommitInterruption() : null);
                var graph = AstrometryGraph(f, new(new ProcessingRecipeExecutor()), gate, persistence: persistence);
                using var currentArtifacts = SiloInfrastructureFixture.OpenArtifactReader(f.Fixture.Options, store);
                using var currentReader = new CameraAgentRetainedAstrometryService(store, currentArtifacts);
                var empty = await currentReader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(empty.Completed);
                Assert.IsNull(empty.Summary);
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
                var solution = MeasuredAstrometryJson.Parse(await ReadProductPartAsync(root, solutionOutput).ConfigureAwait(false));
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
                    if (scenario == "accepted")
                        await CheckLatestPublicationAsync(root, store, currentReader, solutionOutput, identity).ConfigureAwait(false);
                }
                var holds = await store.ReadRetentionHoldsAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var id in solutionOutput.Artifact.SourceArtifactIds) Assert.IsTrue(holds.Any(h => h.ArtifactId == id));
                var all = new List<DurableProcessingOutput>();
                foreach (var id in new[] { "scene", "measured", "astrometry" })
                    all.AddRange((await store.ReadNodeAsync(f.Fixture.Manifest.Descriptor.Capture.CaptureId, id, CancellationToken.None).ConfigureAwait(false))!.Outputs);
                payloads = new(); manifests = new();
                foreach (var output in all)
                {
                    payloads.Add(output.ArtifactId, await ReadProductPartAsync(root, output).ConfigureAwait(false));
                    var m = output.ProductManifest!;
                    var upload = new StructuredProcessingProductManifestV1(StructuredProcessingProductManifestV1.CurrentSchemaVersion,
                        new(f.Fixture.Manifest.Descriptor, m.Artifact, m.OutputIdentitySha256, m.Algorithms, m.Compatibility,
                            m.TotalIntegrationTicks, m.ByteLength, m.Kind, m.ProductSchemaVersion!, m.ContentIdentitySha256!),
                        m.RelativeArtifactPath, null);
                    manifests.Add(output.ArtifactId, StructuredProcessingProductManifestJson.Serialize(upload));
                }
            }
            if (scenario == "sparse")
            {
                using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                await connection.OpenAsync().ConfigureAwait(false);
                var exposure = f.Fixture.Manifest.Descriptor.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds();
                await SeedRawAstrometryHistoryAsync(connection, 650_000, exposure + 1).ConfigureAwait(false);
            }
            var invalidNewerCapture = scenario == "accepted"
                ? await SeedUnverifiableNewerAstrometryAsync(root, f.Fixture.Manifest.Descriptor.Capture.CaptureId).ConfigureAwait(false)
                : (Guid?)null;
            using var restartedTelemetry = new CaptureProcessingTelemetry();
            using var restartedStore = SiloInfrastructureFixture.OpenProcessingStore(f.Fixture.Options);
            using var restartedGate = new CaptureAstrometryExecutionGate();
            var restartedPersistence = CreatePersistence(f.Fixture.Options, restartedStore, restartedTelemetry);
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
                var rawSource = f.Fixture.Manifest.Descriptor;
                var artifactId = scenario == "tamper-raw" ? rawSource.Artifact.ArtifactId
                    : manifests.Keys.Single(id => ImportAstrometryProduct(manifests[id], payloads[id]).SchemaVersion == schema);
                var revision = scenario == "tamper-raw" ? CaptureContractJson.ComputeDescriptorSha256(rawSource)
                    : (await restartedStore.ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false))!.OutputIdentitySha256;
                var silo = SiloInfrastructureFixture.ProductStore(root);
                var locator = await SiloInfrastructureFixture.ReadLocalLocatorAsync(silo, artifactId, revision, CancellationToken.None).ConfigureAwait(false);
                silo.GetRequiredService<SiloTestStore>().Damage(locator);
            }
            using var artifactReader = SiloInfrastructureFixture.OpenArtifactReader(f.Fixture.Options, restartedStore);
            using var astrometryReader = new CameraAgentRetainedAstrometryService(restartedStore, artifactReader);
            var view = await astrometryReader.GetAsync(f.Fixture.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            if (scenario.StartsWith("tamper-", StringComparison.Ordinal))
            {
                Assert.AreEqual("Unavailable", view.Availability);
                Assert.IsNull(view.Solution);
                Assert.IsNull(await astrometryReader.GetLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false));
                return;
            }
            Assert.AreEqual("Retained", view.Availability, view.Reason);
            Assert.AreEqual(identity, view.SolutionIdentitySha256);
            using var summaryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var latestRead = await astrometryReader.ReadLatestAcceptedAsync(summaryDeadline.Token).ConfigureAwait(false);
            Assert.IsTrue(latestRead.Completed);
            var latest = latestRead.Summary;
            if (outcomeScenario == "accepted")
            {
                Assert.IsNotNull(latest);
                Assert.AreEqual(identity, latest.SolutionIdentitySha256);
                Assert.AreEqual(view.CaptureId, latest.CaptureId);
                Assert.IsTrue(latest.LocalScale.XArcsecondsPerPixel > 0);
                var candidates = await restartedStore.ReadAstrometryCandidatesAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(invalidNewerCapture.HasValue ? 2 : 1, candidates);
                if (invalidNewerCapture is { } invalid) Assert.AreEqual(invalid, candidates[0].CaptureId);
                Assert.AreEqual(view.CaptureId, candidates[^1].CaptureId);
                if (scenario == "accepted")
                {
                    await CheckLatestReadCapacityAsync(restartedStore, artifactReader, view.CaptureId, identity).ConfigureAwait(false);
                    var output = await restartedStore.ReadOutputByArtifactIdAsync(view.ArtifactId!.Value, CancellationToken.None)
                        .ConfigureAwait(false);
                    Assert.IsNotNull(output);
                    await AssertPinnedAssessmentWithAmbiguousHistoryAsync(root, astrometryReader, output, identity).ConfigureAwait(false);
                }
            }
            else Assert.IsNull(latest);

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
            var request = RetainedAstrometryProducts.Request(raw, measured, scene, context,
                f.Options.Recipe with { UseScientificAssociations = true }, original.Variant);
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
            using var store = SiloInfrastructureFixture.OpenProcessingStore(f.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(f.Fixture.Options, store, telemetry);
            var processed = await FrameProcessingWorker.ProcessGraphItemAsync(f.Fixture.Item,
                AstrometryGraph(f, new(new ProcessingRecipeExecutor()), gate, persistence: persistence), persistence,
                telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);
            using var real = SiloInfrastructureFixture.OpenArtifactReader(f.Fixture.Options, store);
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
            using var reader = CreateUnpooledAstrometryReader(store, decorated.Object,
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

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetainedAstrometry_NewerAcceptedCropWinsOverCenteredFit()
    {
        var root = CreateTestRoot();
        try
        {
            var first = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = SiloInfrastructureFixture.OpenProcessingStore(first.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(first.Fixture.Options, store, telemetry);
            using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(first.Fixture.Options, store);
            using var reader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var centered = await ProduceAcceptedAsync(first).ConfigureAwait(false);
            Assert.AreEqual("measured boresight", centered.ScaleReference);

            var cropped = new ProjectionContext(ProjectionModel.EquidistantFisheye, 568, 240, 379.323, 379.323,
                480, 480, ProjectionAperture.Circular, 595.84, 72, 243, 17);
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(20, projectionOverride: cropped).ConfigureAwait(false);
            var next = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true, captureSequence: 61,
                reference: reference, prior: (1, 1)).ConfigureAwait(false);
            var latest = await ProduceAcceptedAsync(next).ConfigureAwait(false);
            Assert.IsTrue(latest.MidpointUtc > centered.MidpointUtc);
            Assert.AreNotEqual(centered.SolutionIdentitySha256, latest.SolutionIdentitySha256);
            Assert.AreEqual("source image center", latest.ScaleReference);
            Assert.AreEqual(new PixelPoint(239.5, 239.5), latest.ReferencePixel);
            Assert.IsTrue(latest.LocalScale.XArcsecondsPerPixel > 0);
            Assert.IsTrue(latest.LocalScale.YArcsecondsPerPixel > 0);
            Assert.AreEqual(centered.SolutionIdentitySha256,
                (await reader.GetAsync(centered.CaptureId, CancellationToken.None).ConfigureAwait(false)).SolutionIdentitySha256);

            async Task<CameraAgentLatestAstrometry> ProduceAcceptedAsync(AstrometryDurabilityFixture fixture)
            {
                var graph = AstrometryGraph(fixture, new(new ProcessingRecipeExecutor()), gate, persistence: persistence);
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Fixture.Item, graph, persistence,
                    telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                var id = fixture.Fixture.Manifest.Descriptor.Capture.CaptureId;
                var exact = await reader.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(AstrometricAssessmentStatus.Accepted, exact.Solution?.Assessment.Status, exact.Solution?.Assessment.Reason);
                var latest = (await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false)).Summary;
                Assert.IsNotNull(latest);
                Assert.AreEqual(id, latest.CaptureId);
                Assert.AreEqual(exact.SolutionIdentitySha256, latest.SolutionIdentitySha256);
                return latest;
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<Guid> SeedUnverifiableNewerAstrometryAsync(string root, Guid acceptedCapture)
    {
        var captureId = Guid.NewGuid();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raw_capture_assignments(capture_id,raw_artifact_id,agent_id,capture_sequence)
            VALUES ($new,$raw,'newer-fixture',1);
            INSERT INTO raw_captures(capture_id,raw_artifact_id,agent_id,capture_sequence,
                descriptor_sha256,manifest_sha256,payload_sha256,payload_length,payload_relative_path,
                sidecar_relative_path,manifest_json,exposure_started_unix_ms,durable_ingress_unix_ms,
                committed_unix_ms,state,retention_hold)
            SELECT $new,$raw,'newer-fixture',1,$descriptor_sha,manifest_sha256,payload_sha256,0,
                'absent-raw','absent-sidecar',X'7B7D',exposure_started_unix_ms + 1,
                durable_ingress_unix_ms + 1,committed_unix_ms + 1,'committed',0
            FROM raw_captures WHERE capture_id = $accepted;
            INSERT INTO processing_outputs(output_identity_sha256,capture_id,agent_id,node_id,artifact_id,role,variant,
                payload_relative_path,sidecar_relative_path,descriptor_json,recipe_identity_sha256,algorithms_json,
                compatibility_json,total_integration_ticks,capture_sequence,committed_unix_ms,product_kind,
                product_schema_version,content_identity_sha256)
            SELECT $output,$new,'newer-fixture',node_id,$artifact,role,variant,'absent-product','absent-manifest',
                descriptor_json,recipe_identity_sha256,algorithms_json,compatibility_json,total_integration_ticks,
                1,committed_unix_ms + 1,product_kind,product_schema_version,content_identity_sha256
            FROM processing_outputs WHERE capture_id = $accepted AND product_schema_version = $schema;
            """;
        command.Parameters.AddWithValue("$new", captureId.ToString("N"));
        command.Parameters.AddWithValue("$raw", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$descriptor_sha", new string('B', 64));
        command.Parameters.AddWithValue("$accepted", acceptedCapture.ToString("N"));
        command.Parameters.AddWithValue("$output", new string('A', 64));
        command.Parameters.AddWithValue("$artifact", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$schema", MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return captureId;
    }

    private static async Task CheckLatestPublicationAsync(string root, SqliteCaptureProcessingStore store,
        CameraAgentRetainedAstrometryService reader, DurableProcessingOutput output, string identity)
    {
        // Isolate publication visibility from the execution scheduler by installing the association in the fixture.
        // The accepted solution and its source files came through the actual producer and retained read path.
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Foreign Keys=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_execution_outputs(execution_id,node_id,output_ordinal,output_identity_sha256,published_flag)
            VALUES ($execution,'astrometry',0,$output,0);
            """;
        command.Parameters.AddWithValue("$execution", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$output", output.OutputIdentitySha256);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

        var pending = await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(pending.Completed);
        Assert.IsNull(pending.Summary);
        var warm = await store.ReadAstrometryCandidatesAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, warm, "The earlier empty snapshot must observe the committed solution output.");

        command.CommandText = "UPDATE processing_execution_outputs SET published_flag = 1 WHERE execution_id = $execution AND output_identity_sha256 = $output;";
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        var published = await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(published.Completed);
        Assert.AreEqual(identity, published.Summary?.SolutionIdentitySha256, "Publication must become readable without another output insert.");
        Assert.AreSame(warm, await store.ReadAstrometryCandidatesAsync(CancellationToken.None).ConfigureAwait(false));
        command.CommandText = "DELETE FROM processing_execution_outputs WHERE execution_id = $execution AND output_identity_sha256 = $output;";
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private static async Task AssertPinnedAssessmentWithAmbiguousHistoryAsync(string root,
        CameraAgentRetainedAstrometryService reader, DurableProcessingOutput output, string identity)
    {
        var captureId = output.Capture.CaptureId;
        var pin = new CenteredViewArtifact(output.ArtifactId, output.OutputIdentitySha256, output.Artifact.ChecksumSha256);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_outputs(output_identity_sha256,capture_id,agent_id,node_id,artifact_id,role,variant,
                payload_relative_path,sidecar_relative_path,descriptor_json,recipe_identity_sha256,algorithms_json,
                compatibility_json,total_integration_ticks,capture_sequence,committed_unix_ms,product_kind,
                product_schema_version,content_identity_sha256)
            SELECT $duplicate,capture_id,agent_id,node_id,$artifact,role,variant,'absent-product','absent-manifest',
                descriptor_json,recipe_identity_sha256,algorithms_json,compatibility_json,total_integration_ticks,
                capture_sequence,committed_unix_ms,product_kind,product_schema_version,content_identity_sha256
            FROM processing_outputs WHERE output_identity_sha256 = $output;
            """;
        command.Parameters.AddWithValue("$duplicate", new string('C', 64));
        command.Parameters.AddWithValue("$artifact", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$output", output.OutputIdentitySha256);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        try
        {
            var ambiguous = await reader.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("assessment-ambiguous", ambiguous.Reason);
            var exact = await reader.GetExactAsync(captureId, pin, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(identity, exact.SolutionIdentitySha256);
            Assert.AreEqual(pin.ArtifactId, exact.ArtifactId);
            Assert.IsNotNull(exact.Diagnostics);
            var wrongPin = await reader.GetExactAsync(captureId, pin with { PayloadSha256 = new string('D', 64) },
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(wrongPin.Solution);
        }
        finally
        {
            command.CommandText = "DELETE FROM processing_outputs WHERE output_identity_sha256 = $duplicate;";
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
    }

    private static async Task CheckLatestReadCapacityAsync(SqliteCaptureProcessingStore store,
        CameraAgentArtifactService artifacts, Guid captureId, string identity)
    {
        var entering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        blocked.Setup(service => service.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, CancellationToken token) =>
            {
                if (token == cancellation.Token)
                {
                    entering.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }
                return await artifacts.OpenContentAsync(id, token).ConfigureAwait(false);
            });

        using var reader = CreateUnpooledAstrometryReader(store, blocked.Object);
        var held = reader.GetAsync(captureId, cancellation.Token).AsTask();
        await entering.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        var busy = await reader.ReadLatestAcceptedAsync(deadline.Token).ConfigureAwait(false);
        Assert.IsFalse(busy.Completed);
        Assert.IsNull(busy.Summary);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => held).ConfigureAwait(false);

        var retried = await reader.ReadLatestAcceptedAsync(deadline.Token).ConfigureAwait(false);
        Assert.IsTrue(retried.Completed);
        Assert.AreEqual(identity, retried.Summary?.SolutionIdentitySha256);
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

    private static async Task<AstrometryDurabilityFixture> CreateAstrometryDurabilityFixtureAsync(string root,
        string scenario = "accepted", bool hostCompatible = false, long captureSequence = 1,
        MeasuredAstrometryFixture? reference = null, (long Sequence, int Count)? prior = null,
        Func<SqliteRawCaptureJournal, RawIngressJournalEntry, CaptureLoopSubmission, Task<RawIngressOutcome>>? commit = null,
        MeasuredAstrometryOptions? recipe = null,
        SqliteRawCaptureJournal? benchmarkJournal = null)
    {
        reference ??= await MeasuredAstrometryFixture.CreateAsync().ConfigureAwait(false);
        recipe ??= MeasuredAstrometryFixture.Options;
        var bytes = reference.Raw.Payload.ToArray();
        if (scenario == "rejected")
        {
            var pixels = MeasuredStellarAssociationFixtures.Field(512, 512);
            MeasuredStellarAssociationFixtures.AddNoise(pixels, 1205, 4);
            bytes = MeasuredStellarAssociationFixtures.RawArtifact(pixels, width: 512, height: 512).Payload.ToArray();
        }
        var config = CreateConfig();
        var layout = reference.Raw.Layout!;
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, layout.Width, layout.Height, layout.StrideBytes, bytes);
        if (prior is not null)
        {
            manifest = manifest with
            {
                Descriptor = manifest.Descriptor with
                {
                    Capture = manifest.Descriptor.Capture with { CaptureId = reference.Raw.CaptureId!.Value },
                    Artifact = manifest.Descriptor.Artifact with { ArtifactId = reference.Raw.ArtifactId }
                }
            };
        }
        var start = reference.Context.Frame.ExposureStartUtc;
        DateTimeOffset StoredTime(DateTimeOffset value) => hostCompatible
            ? DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds()) : value;
        var paths = RawIngressFileStore.GetPaths(root, StoredTime(start), manifest.Descriptor.Artifact.ArtifactId);
        var payloadRelativePath = hostCompatible ? paths.PayloadRelativePath : "raw.bin";
        var sidecarRelativePath = hostCompatible ? paths.SidecarRelativePath : "raw.json";
        manifest = manifest with
        {
            RelativeArtifactPath = payloadRelativePath,
            Descriptor = manifest.Descriptor with
            {
                Capture = manifest.Descriptor.Capture with { CaptureSequence = captureSequence },
                Layout = reference.Raw.Layout!,
                Timing = new(StoredTime(start), StoredTime(start), StoredTime(start.AddSeconds(1)), StoredTime(start.AddSeconds(1.1)), StoredTime(start.AddSeconds(1.2))),
                Artifact = manifest.Descriptor.Artifact with { CreatedUtc = StoredTime(start.AddSeconds(1.2)) },
                Profiles = manifest.Descriptor.Profiles with
                {
                    Processing = RawCaptureDescriptorFactory.CreateProcessingProfile(config),
                    Rig = commit is null ? manifest.Descriptor.Profiles.Rig : manifest.Descriptor.Profiles.Rig with
                    {
                        Sha256 = CameraRigProfileIdentity.ComputeSha256(config.Rig)
                    }
                }
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
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PayloadAbsolutePath)!);
        await File.WriteAllBytesAsync(paths.PayloadAbsolutePath, bytes).ConfigureAwait(false);
        await File.WriteAllBytesAsync(paths.SidecarAbsolutePath, manifestBytes).ConfigureAwait(false);
        var journal = benchmarkJournal ?? new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1,
            usesSilo: commit is not null);
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        for (var sequence = (prior?.Sequence ?? 0) + 1; sequence < captureSequence; sequence++)
            _ = await journal.ReserveIdentityAsync(descriptor.Capture.AgentId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        _ = await journal.ReserveIdentityAsync(descriptor.Capture.AgentId, descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        var entry = new RawIngressJournalEntry(
            descriptor.Capture.AgentId, captureSequence, descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
            CaptureContractJson.ComputeDescriptorSha256(descriptor), CaptureContractJson.ComputeManifestSha256(manifestBytes),
            descriptor.Artifact.ChecksumSha256, bytes.Length, payloadRelativePath, sidecarRelativePath, manifestBytes, descriptor.Timing.ExposureStartedUtc, descriptor.Timing.DurableIngressUtc);
        Assert.IsTrue(FrameReconstructor.TryReconstruct(descriptor, bytes, out var frame).IsValid);
        var submission = CreateSubmission(frame!);
        var outcome = commit is null
            ? await journal.CommitAsync(entry, CancellationToken.None).ConfigureAwait(false)
            : await commit(journal, entry, submission).ConfigureAwait(false);
        Assert.AreEqual(RawIngressOutcome.Committed, outcome);
        await SiloInfrastructureFixture.PublishRawFixtureAsync(
            SiloInfrastructureFixture.ProductStorage(root), entry, manifest, bytes, CancellationToken.None).ConfigureAwait(false);
        if (commit is not null)
            await journal.SettleSiloAdmissionAsync(entry.CaptureId, entry.ManifestSha256, siloVisible: true, CancellationToken.None)
                .ConfigureAwait(false);
        else if (hostCompatible && benchmarkJournal is null)
        {
            var recovered = await new RawIngressReconciler(root, journal)
                .RunAsync(_ => ValueTask.FromResult(default(CaptureMaintenanceEntry)), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual((prior?.Count ?? 0) + 1, recovered.Inspected);
            Assert.AreEqual(0, recovered.Quarantined);
            Assert.AreEqual(0, recovered.MissingEvidence);
        }
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest,
            new StoredFrameReference(payloadRelativePath, Path.Combine(root, payloadRelativePath), descriptor.Timing.ExposureStartedUtc, FrameArtifactRole.Raw), CaptureContractJson.ComputeManifestSha256(manifestBytes));
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
        return new(new(manifest, options, new(config, submission, receipt)), input,
            new()
            {
                Catalog = new(reference.Context.CatalogMetadata, reference.Context.CatalogStars, reference.Context.CatalogPackage,
                scenario != "unavailable", 7),
                Recipe = scenario == "budget" ? recipe with { MaximumExecutionMilliseconds = .000001 } : recipe
            });
    }
}
