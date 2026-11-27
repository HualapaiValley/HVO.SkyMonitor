using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false, "single-step")]
    [DataRow(true, "single-step")]
    [DataRow(false, "split-repair")]
    [DataRow(true, "split-repair")]
    [DataRow(false, "replace-rejection")]
    [DataRow(true, "replace-rejection")]
    public async Task RetainedAstrometry_PrivateAssessmentProgressAndPublication(bool reopen, string repairKind)
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var first = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                commit: static (journal, entry, _) => journal.CommitAsync(entry, CancellationToken.None)).ConfigureAwait(false);
            var probe = new AstrometryLookupBudget();
            using var provider = ProcessingGraphOperationsTests.CreateRemainingOptionsProvider(root);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var pipeline = ProcessingGraphOperationsTests.RemainingOptionsPipeline(root);
            var config = CreateConfig() with
            {
                Pipeline = pipeline with { Steps = pipeline.Steps.Where(step => step.Id is "scene" or "measured" or "astrometry" or "storage").ToArray() }
            };
            var registry = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            var revision = await operations.ReadRevisionSnapshotAsync(registry.ActiveRevisionId, token).ConfigureAwait(false);
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(definition => definition.Name == "standard");
            using var telemetry = new CaptureProcessingTelemetry();
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(first.Fixture.Options, store, telemetry);
            var oldest = await ProduceLatestReadFixtureAsync(first, store, persistence, telemetry, gate).ConfigureAwait(false);
            var lanes = provider.GetRequiredService<ICaptureLaneStore>();
            await lanes.InitializeLanesAsync(token).ConfigureAwait(false);
            var oldestLease = await lanes.ClaimAsync(standard, "public-fit", config, token).ConfigureAwait(false);
            Assert.IsNotNull(oldestLease);
            await lanes.CompleteAsync(oldestLease, token).ConfigureAwait(false);

            var artifacts = provider.GetRequiredService<CameraAgentArtifactService>();
            var splitRepair = repairKind == "split-repair";
            var replaceRejection = repairKind == "replace-rejection";
            var (near, _, _, _) = await StageAssessmentAsync(61, (1, 1), terminal: true).ConfigureAwait(false);
            var (far, _, measured, rejection) = await StageAssessmentAsync(121, (61, 2), terminal: true,
                privateMeasurement: splitRepair, publishRejection: replaceRejection).ConfigureAwait(false);

            using var observedStore = new SqliteCaptureProcessingStore(first.Fixture.Options, faultInjector: probe);
            using var observed = new CameraAgentRetainedAstrometryService(observedStore, artifacts);
            var service = CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(observed);
            var expectedOld = CameraAgentLatestAstrometry.FromVerified(
                (await service.GetRetainedAstrometryAsync(oldest.Capture.CaptureId, token).ConfigureAwait(false)).Value!);
            Assert.AreEqual(oldest.ArtifactId, expectedOld?.ArtifactId);

            using var restartedStore = new SqliteCaptureProcessingStore(first.Fixture.Options, faultInjector: probe);
            using var restarted = new CameraAgentRetainedAstrometryService(restartedStore, artifacts);
            var chosenStore = reopen ? restartedStore : observedStore;
            var selectedService = reopen ? CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(restarted) : service;
            var selectedReader = reopen ? restarted : observed;

            // Warm the snapshot on this store. The upcoming deadline controls apply to successful
            // SQLite probes, independently of asynchronous seed timing or host speed.
            var candidates = await chosenStore.ReadAstrometryCandidatesAsync(token).ConfigureAwait(false);
            Assert.IsTrue(candidates.Any(candidate => candidate.CaptureId == oldest.Capture.CaptureId));
            if (!replaceRejection)
            {
                var seen = new List<Guid>();
                for (var request = 0; request < 2; request++)
                {
                    using var deadline = new CancellationTokenSource();
                    probe.Arm(deadline);
                    try
                    {
                        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                            await selectedService.GetLatestAcceptedAstrometryAsync(deadline.Token).ConfigureAwait(false)).ConfigureAwait(false);
                        seen.Add(probe.LastCapture);
                    }
                    finally { probe.Disarm(); }
                }
                CollectionAssert.AreEqual(new[] { far.Capture.CaptureId, near.Capture.CaptureId }, seen,
                    "The next deadline must resume after the prior conclusively nonpublic capture.");
            }
            var fallback = await selectedService.GetLatestAcceptedAstrometryAsync(token).ConfigureAwait(false);
            Assert.IsTrue(fallback.IsSuccess);
            Assert.AreEqual(expectedOld, fallback.Value?.Summary);
            Assert.AreEqual(CameraAgentAstrometryPublicationState.Invalidated,
                await selectedReader.ReadPublicationStateAsync(Identity(far), token).ConfigureAwait(false));
            var visible = (await selectedService.GetRetainedAstrometryAsync(far.Capture.CaptureId, token).ConfigureAwait(false)).Value;
            if (rejection is not null)
            {
                Assert.AreNotEqual(far.ArtifactId, rejection.ArtifactId);
                Assert.AreEqual(rejection.ArtifactId, visible?.ArtifactId);
                Assert.AreEqual(AstrometricAssessmentStatus.Rejected, visible?.Solution?.Assessment.Status);
                await EvictAndRestoreFilesAsync(rejection, store).ConfigureAwait(false);
            }
            else Assert.IsNull(visible?.Solution);

            // Reinsert the exact assessment on the public persistence path from a different
            // store. Its notifications cannot invalidate the reader's candidate snapshot.
            await EvictAndRestoreFilesAsync(far, store).ConfigureAwait(false);
            var repair = AstrometryGraph(first, new(new ProcessingRecipeExecutor()), gate);
            await PublishAsync(far, "astrometry").ConfigureAwait(false);
            if (splitRepair)
            {
                await ExpectPublicArtifactAsync(far.ArtifactId, CameraAgentArtifactReadStatus.Found).ConfigureAwait(false);
                await ExpectPublicArtifactAsync(measured.ArtifactId, CameraAgentArtifactReadStatus.NotFound).ConfigureAwait(false);
                var partial = await selectedService.GetLatestAcceptedAstrometryAsync(token).ConfigureAwait(false);
                Assert.IsTrue(partial.IsSuccess);
                Assert.AreEqual(expectedOld, partial.Value?.Summary);
                Assert.AreEqual(CameraAgentAstrometryPublicationState.Invalidated,
                    await selectedReader.ReadPublicationStateAsync(Identity(far), token).ConfigureAwait(false));
                await EvictAndRestoreFilesAsync(measured, store).ConfigureAwait(false);
                await PublishAsync(measured, "measured").ConfigureAwait(false);
                await ExpectPublicArtifactAsync(measured.ArtifactId, CameraAgentArtifactReadStatus.Found).ConfigureAwait(false);
            }
            var repaired = await selectedService.GetLatestAcceptedAstrometryAsync(token).ConfigureAwait(false);
            var exact = await selectedService.GetRetainedAstrometryAsync(far.Capture.CaptureId, token).ConfigureAwait(false);
            Assert.AreEqual(far.ArtifactId, exact.Value?.ArtifactId);
            Assert.AreEqual(AstrometricAssessmentStatus.Accepted, exact.Value?.Solution?.Assessment.Status);
            Assert.AreSame(candidates, await chosenStore.ReadAstrometryCandidatesAsync(token).ConfigureAwait(false));
            Assert.AreEqual(CameraAgentAstrometryPublicationState.Available,
                await selectedReader.ReadPublicationStateAsync(Identity(far), token).ConfigureAwait(false));
            using var repairedStore = new SqliteCaptureProcessingStore(first.Fixture.Options);
            using var repairedReader = new CameraAgentRetainedAstrometryService(repairedStore, artifacts);
            var reopened = (await repairedReader.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary;
            Assert.AreEqual(far.ArtifactId, reopened?.ArtifactId);
            Assert.AreEqual(far.ContentIdentitySha256, reopened?.SolutionIdentitySha256);
            Assert.AreEqual(reopened, repaired.Value?.Summary,
                "External public repair must become visible to the reader that cached the skip.");
            if (repairKind != "single-step") return;

            var (staged, stagedLease, _, _) = await StageAssessmentAsync(181, (121, 3), terminal: false).ConfigureAwait(false);
            using var publicationReader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var pending = await publicationReader.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
            Assert.IsFalse(pending.Completed, "A staged live assessment can still become public.");
            var selectedPending = await publicationReader.GetAsync(staged.Capture.CaptureId, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentRetainedAstrometryService.TemporaryEvidenceUnavailable, selectedPending.Reason);
            Assert.IsFalse(selectedPending.HasMissingEvidence);
            Assert.AreEqual(CameraAgentAstrometryPublicationState.Indeterminate,
                await publicationReader.ReadPublicationStateAsync(Identity(staged), token).ConfigureAwait(false));
            await lanes.CompleteAsync(stagedLease, token).ConfigureAwait(false);
            Assert.AreEqual(staged.ArtifactId, (await publicationReader.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary?.ArtifactId);

            ValueTask PublishAsync(DurableProcessingOutput output, string nodeId)
                => store.WriteNodeAsync(output.Capture.CaptureId, repair.Nodes.Single(node => node.Id == nodeId),
                    DurableProcessingNodeStatus.Completed, null, 2, null, null, DateTimeOffset.UtcNow, null,
                    ProcessingOutcomeStatus.Produced, [], 0, null, [output], token);

            async ValueTask ExpectPublicArtifactAsync(Guid artifactId, CameraAgentArtifactReadStatus expected)
            {
                var opened = await artifacts.OpenContentAsync(artifactId, token).ConfigureAwait(false);
                if (opened.Content is { } content) await content.DisposeAsync().ConfigureAwait(false);
                Assert.AreEqual(expected, opened.Status);
            }

            async ValueTask<(DurableProcessingOutput Assessment, CaptureLaneLease Lease, DurableProcessingOutput Measured,
                DurableProcessingOutput? Rejection)> StageAssessmentAsync(int sequence, (long Sequence, int Count) prior, bool terminal,
                bool privateMeasurement = false, bool publishRejection = false)
            {
                var reference = await MeasuredAstrometryFixture.CreateAtAsync(sequence / 3).ConfigureAwait(false);
                var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                    captureSequence: sequence, reference: reference, prior: prior,
                    commit: (journal, entry, submission) =>
                    {
                        var envelope = CaptureLaneEnvelopeSerializer.Serialize(config, submission);
                        var seed = operations.PrepareBoundLiveExecution(config, entry.CaptureId, entry.ArtifactId,
                            DateTimeOffset.UtcNow, revision).Seed;
                        return journal.CommitAsync(entry, envelope.Json, envelope.Sha256, [standard], seed, token);
                    }).ConfigureAwait(false);
                var fit = await ProduceLatestReadFixtureAsync(fixture, store, persistence, telemetry, gate).ConfigureAwait(false);
                var captureId = fit.Capture.CaptureId;
                var graph = AstrometryGraph(fixture, new(new ProcessingRecipeExecutor()), gate);
                var nodes = graph.Nodes;
                var outputs = new Dictionary<string, IReadOnlyList<DurableProcessingOutput>>(StringComparer.Ordinal);
                foreach (var node in nodes)
                    outputs[node.Id] = (await store.ReadNodeAsync(captureId, node.Id, token).ConfigureAwait(false))!.Outputs;
                var measuredOutput = outputs["measured"].Single();
                DurableProcessingOutput? rejectedOutput = null;
                IReadOnlyList<DurableProcessingOutput>? rejectionOutputs = null;
                if (publishRejection)
                {
                    var older = MeasuredAstrometryJson.Parse(
                        await File.ReadAllBytesAsync(Path.Combine(root, oldest.PayloadRelativePath), token).ConfigureAwait(false));
                    var settings = fixture.Options.Recipe.Solver!;
                    var rejectedFixture = fixture with
                    {
                        Options = new()
                        {
                            Catalog = fixture.Options.Catalog,
                            Recipe = fixture.Options.Recipe with { Solver = settings with { MaximumWarmAgeSeconds = .001 } },
                            PreviousAccepted = older.Assessment
                        }
                    };
                    var rejectionGraph = AstrometryGraph(rejectedFixture, new(new ProcessingRecipeExecutor()), gate, persistence: persistence);
                    var rejectingNode = rejectionGraph.Nodes.Single(node => node.Id == "astrometry") with { Id = "astrometry-rejected" };
                    var alternate = new CaptureProcessingGraph([.. rejectionGraph.Nodes.Where(node => node.Id != "astrometry"), rejectingNode]);
                    var rejected = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Fixture.Item, alternate, persistence,
                        telemetry, 1, NullLogger.Instance, token).ConfigureAwait(false);
                    Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, rejected.Outcome, rejected.Reason);
                    rejectionOutputs = (await store.ReadNodeAsync(captureId, rejectingNode.Id, token).ConfigureAwait(false))!.Outputs;
                    rejectedOutput = rejectionOutputs.Single(output => output.ProductSchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
                    var rejectedSolution = MeasuredAstrometryJson.Parse(
                        await File.ReadAllBytesAsync(Path.Combine(root, rejectedOutput.PayloadRelativePath), token).ConfigureAwait(false));
                    Assert.AreEqual(AstrometricAssessmentStatus.Rejected, rejectedSolution.Assessment.Status);
                    foreach (var output in rejectionOutputs)
                        await EvictAndRestoreFilesAsync(output, store).ConfigureAwait(false);
                }
                if (privateMeasurement) await EvictAndRestoreFilesAsync(measuredOutput, store).ConfigureAwait(false);
                await EvictAndRestoreFilesAsync(fit, store).ConfigureAwait(false);
                var live = await lanes.ClaimAsync(standard, "stage-private-fit", config, token).ConfigureAwait(false);
                Assert.IsNotNull(live);
                Assert.AreEqual(captureId, live.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId);
                Assert.IsNotNull(live.Context.Execution);
                await WriteOutputsAsync(live.Context.Execution, rejectionOutputs).ConfigureAwait(false);
                if (!terminal) return (fit, live, measuredOutput, null);

                if (rejectionOutputs is not null)
                    await lanes.CompleteAsync(live, token).ConfigureAwait(false);
                else
                    Assert.AreEqual(CaptureLaneHandlerOutcome.Abandoned,
                        await lanes.FailAsync(live, CaptureLaneHandlerResult.Abandon("fixture-ended-before-publication"), token).ConfigureAwait(false));
                var submitted = await operations.SubmitReplayAsync(new(captureId, registry.ActiveRevisionId, fit.Artifact.SourceArtifactIds[0]),
                    $"private-assessment-{sequence}", "owner", token).ConfigureAwait(false);
                var replay = await store.ClaimReplayAsync("private-fit-replay", token).ConfigureAwait(false);
                Assert.IsNotNull(replay);
                Assert.AreEqual(submitted.Execution.ExecutionId, replay.Execution.ExecutionId);
                await WriteOutputsAsync(new(replay.Execution.ExecutionId, ProcessingGraphExecutionClass.Replay, replay.Execution.GraphRevisionId,
                    replay.Execution.LocalPlanIdentitySha256, false, replay.WorkId, replay.LeaseToken, replay.LeaseOwner,
                    replay.Execution.DeadlineUtc, replay.ClaimCount)).ConfigureAwait(false);
                var completed = await store.CompleteReplayAsync(replay, CaptureLaneHandlerResult.Success, token).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, completed.Status);
                Assert.AreEqual(CameraAgentArtifactReadStatus.NotFound, (await artifacts.OpenContentAsync(fit.ArtifactId, token).ConfigureAwait(false)).Status);
                var replayContent = await artifacts.OpenReplayOutputContentAsync(completed.ExecutionId, fit.ArtifactId, token).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Found, replayContent.Status);
                await replayContent.Content!.DisposeAsync().ConfigureAwait(false);
                return (fit, live, measuredOutput, rejectedOutput);

                async ValueTask WriteOutputsAsync(ProcessingExecutionContext execution, IReadOnlyList<DurableProcessingOutput>? alternate = null)
                {
                    foreach (var node in nodes)
                    {
                        var frozen = revision.Nodes.Single(item => item.NodeId == node.Id);
                        await store.WriteNodeAsync(captureId, node with { PlanSha256 = frozen.PlanSha256 },
                            DurableProcessingNodeStatus.Completed, null, 1, null, null, DateTimeOffset.UtcNow, null,
                            ProcessingOutcomeStatus.Produced, [], execution.WorkId, execution.LeaseToken,
                            node.Id == "astrometry" && alternate is not null ? alternate : outputs[node.Id],
                            execution, ProcessingNodeExecutionRoute.InProcess, token).ConfigureAwait(false);
                    }
                }
            }

            async ValueTask EvictAndRestoreFilesAsync(DurableProcessingOutput fit, SqliteCaptureProcessingStore writer)
            {
                var path = Path.Combine(root, fit.PayloadRelativePath);
                var sidecarPath = Path.Combine(root, fit.SidecarRelativePath);
                var payload = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                var sidecar = await File.ReadAllBytesAsync(sidecarPath, token).ConfigureAwait(false);
                var operation = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", fit.OutputIdentitySha256,
                    fit.PayloadRelativePath, fit.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}",
                    "retention-expired", payload.Length + sidecar.Length);
                await writer.PlanLifecycleOperationAsync(operation, token).ConfigureAwait(false);
                await new DerivedProductReconciler(root, writer).ResumeOperationAsync(operation, token).ConfigureAwait(false);
                Assert.IsNull(await writer.ReadOutputByArtifactIdAsync(fit.ArtifactId, token).ConfigureAwait(false));
                await File.WriteAllBytesAsync(path, payload, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(sidecarPath, sidecar, token).ConfigureAwait(false);
            }

            static CameraAgentAstrometryAssessmentIdentity Identity(DurableProcessingOutput fit)
                => new(fit.Capture.CaptureId, fit.ArtifactId, fit.ContentIdentitySha256!);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class AstrometryLookupBudget : ICaptureProcessingFaultInjector
    {
        private CancellationTokenSource? _deadline;
        public Guid LastCapture { get; private set; }

        public void Arm(CancellationTokenSource deadline) => _deadline = deadline;

        public void Disarm() => _deadline = null;

        public void Inject(CaptureProcessingFaultPoint point, string value)
        {
            if (point != CaptureProcessingFaultPoint.AfterAstrometryAssessmentLookup || _deadline is null) return;
            LastCapture = Guid.ParseExact(value, "N");
            _deadline.Cancel();
        }
    }
}
