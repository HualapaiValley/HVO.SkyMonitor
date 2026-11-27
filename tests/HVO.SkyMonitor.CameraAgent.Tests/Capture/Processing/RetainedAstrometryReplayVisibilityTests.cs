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
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RetainedAstrometry_PublicInputEvictionRespectsReplayVisibility(bool stageLive, bool reopen)
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var first = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                commit: static (journal, entry, _) => journal.CommitAsync(entry, CancellationToken.None)).ConfigureAwait(false);
            using var provider = ProcessingGraphOperationsTests.CreateRemainingOptionsProvider(root);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var pipeline = ProcessingGraphOperationsTests.RemainingOptionsPipeline(root);
            var config = CreateConfig() with { Pipeline = pipeline with { Steps = [.. pipeline.Steps.Take(2), pipeline.Steps[^1]] } };
            var registry = await operations.EnsureConfiguredBasicAsync(config, token).ConfigureAwait(false);
            var revision = await operations.ReadRevisionSnapshotAsync(registry.ActiveRevisionId, token).ConfigureAwait(false);
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(definition => definition.Name == "standard");
            using var telemetry = new CaptureProcessingTelemetry();
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(first.Fixture.Options, store, telemetry);
            var oldest = await ProduceLatestReadFixtureAsync(first, store, persistence, telemetry, gate).ConfigureAwait(false);
            var next = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true, captureSequence: 61,
                reference: await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false), prior: (1, 1),
                commit: (journal, entry, submission) =>
                {
                    var context = CaptureLaneEnvelopeSerializer.Serialize(config, submission);
                    var seed = operations.PrepareBoundLiveExecution(config, entry.CaptureId, entry.ArtifactId,
                        DateTimeOffset.UtcNow, revision).Seed;
                    return journal.CommitAsync(entry, context.Json, context.Sha256, [standard], seed, token);
                }).ConfigureAwait(false);
            var newest = await ProduceLatestReadFixtureAsync(next, store, persistence, telemetry, gate).ConfigureAwait(false);
            var capture = newest.Capture.CaptureId;
            var rawId = newest.Artifact.SourceArtifactIds[0];
            var measured = (await store.ReadNodeAsync(capture, "measured", token).ConfigureAwait(false))!.Outputs.Single();
            var scene = (await store.ReadNodeAsync(capture, "scene", token).ConfigureAwait(false))!.Outputs.Single();
            Assert.AreEqual(newest.Artifact.SourceArtifactIds[1], measured.ArtifactId);
            var graph = AstrometryGraph(next, new(new ProcessingRecipeExecutor()), gate);
            var measuredNode = graph.Nodes.Single(node => node.Id == "measured");
            var sceneNode = graph.Nodes.Single(node => node.Id == "scene");
            var payloadPath = Path.Combine(root, measured.PayloadRelativePath);
            var sidecarPath = Path.Combine(root, measured.SidecarRelativePath);
            var payload = await File.ReadAllBytesAsync(payloadPath, token).ConfigureAwait(false);
            var sidecar = await File.ReadAllBytesAsync(sidecarPath, token).ConfigureAwait(false);
            var reconciler = new DerivedProductReconciler(root, store);

            var lanes = provider.GetRequiredService<ICaptureLaneStore>();
            await lanes.InitializeLanesAsync(token).ConfigureAwait(false);
            var olderLease = await lanes.ClaimAsync(standard, "oldest", config, token).ConfigureAwait(false);
            Assert.IsNotNull(olderLease);
            Assert.AreEqual(oldest.Capture.CaptureId, olderLease.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId);
            await lanes.CompleteAsync(olderLease, token).ConfigureAwait(false);
            var newerLease = await lanes.ClaimAsync(standard, "newest", config, token).ConfigureAwait(false);
            Assert.IsNotNull(newerLease);
            Assert.AreEqual(capture, newerLease.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId);
            var liveExecution = newerLease.Context.Execution;
            Assert.IsNotNull(liveExecution);
            await WriteExecutionOutputAsync(sceneNode, scene, liveExecution).ConfigureAwait(false);
            if (!stageLive)
            {
                await WriteExecutionOutputAsync(measuredNode, measured, liveExecution).ConfigureAwait(false);
                await lanes.CompleteAsync(newerLease, token).ConfigureAwait(false);
            }

            var artifacts = provider.GetRequiredService<CameraAgentArtifactService>();
            using var warm = new CameraAgentRetainedAstrometryService(store, artifacts);
            var warmService = CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(warm);
            var olderFit = CameraAgentLatestAstrometry.FromVerified(
                (await warmService.GetRetainedAstrometryAsync(oldest.Capture.CaptureId, token).ConfigureAwait(false)).Value!);
            var newerFit = (await warmService.GetLatestAcceptedAstrometryAsync(token).ConfigureAwait(false)).Value?.Summary;
            Assert.IsNotNull(olderFit);
            Assert.IsNotNull(newerFit);
            Assert.AreEqual(newest.ArtifactId, newerFit.ArtifactId);

            var displaced = Path.Combine(root, "held-measured-payload");
            File.Move(payloadPath, displaced);
            Assert.AreEqual(1, (await reconciler.RunAsync(token).ConfigureAwait(false)).Missing);
            var missing = await warmService.GetLatestAcceptedAstrometryAsync(token, newerFit).ConfigureAwait(false);
            Assert.IsFalse(missing.Value!.Completed);
            Assert.IsFalse(missing.Value.PreviousSummaryInvalidated);
            File.Move(displaced, payloadPath);
            await reconciler.RunAsync(token).ConfigureAwait(false);

            await EvictMeasurementAsync().ConfigureAwait(false);
            var fallback = await warmService.GetLatestAcceptedAstrometryAsync(token, newerFit).ConfigureAwait(false);
            Assert.IsTrue(fallback.IsSuccess);
            Assert.IsTrue(fallback.Value!.PreviousSummaryEvicted);
            Assert.AreEqual(olderFit, fallback.Value.Summary);
            await ExpectPublicArtifactAsync(measured.ArtifactId, CameraAgentArtifactReadStatus.NotFound).ConfigureAwait(false);

            await RestoreFilesAsync().ConfigureAwait(false);
            if (stageLive)
            {
                await WriteExecutionOutputAsync(measuredNode, measured, liveExecution).ConfigureAwait(false);
            }
            else
            {
                var replay = await operations.SubmitReplayAsync(new(capture, registry.ActiveRevisionId, rawId),
                    "reinsert-private-fit-input", "owner", token).ConfigureAwait(false);
                var lease = await store.ClaimReplayAsync("private-fit-replay", token).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                Assert.AreEqual(replay.Execution.ExecutionId, lease.Execution.ExecutionId);
                var execution = new ProcessingExecutionContext(lease.Execution.ExecutionId, ProcessingGraphExecutionClass.Replay,
                    lease.Execution.GraphRevisionId, lease.Execution.LocalPlanIdentitySha256, false, lease.WorkId,
                    lease.LeaseToken, lease.LeaseOwner, lease.Execution.DeadlineUtc, lease.ClaimCount);
                await WriteExecutionOutputAsync(sceneNode, scene, execution).ConfigureAwait(false);
                await WriteExecutionOutputAsync(measuredNode, measured, execution).ConfigureAwait(false);
                var completed = await store.CompleteReplayAsync(lease, CaptureLaneHandlerResult.Success, token).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, completed.Status);
                var detail = await operations.ReadExecutionDetailAsync(completed.ExecutionId, token).ConfigureAwait(false);
                Assert.IsNotNull(detail);
                Assert.IsFalse(detail.Nodes.Single(node => node.NodeId == measuredNode.Id).Outputs.Single().Published);
                var replayArtifact = await artifacts.OpenReplayOutputContentAsync(completed.ExecutionId, measured.ArtifactId, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Found, replayArtifact.Status);
                await replayArtifact.Content!.DisposeAsync().ConfigureAwait(false);
            }
            await ExpectPublicArtifactAsync(measured.ArtifactId, CameraAgentArtifactReadStatus.NotFound).ConfigureAwait(false);
            await ExpectPublicArtifactAsync(rawId, CameraAgentArtifactReadStatus.Found).ConfigureAwait(false);

            using var reopenedStore = new SqliteCaptureProcessingStore(first.Fixture.Options);
            using var reopenedArtifacts = new CameraAgentArtifactService(first.Fixture.Options, reopenedStore, new CameraAgentPreviewEncoder());
            using var cold = new CameraAgentRetainedAstrometryService(reopenedStore, reopenedArtifacts);
            var publicReader = reopen ? CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(cold) : warmService;
            var afterReinsert = await publicReader.GetLatestAcceptedAstrometryAsync(token, newerFit).ConfigureAwait(false);
            if (stageLive)
            {
                Assert.IsFalse(afterReinsert.Value!.Completed);
                Assert.IsFalse(afterReinsert.Value.PreviousSummaryInvalidated);
                await lanes.CompleteAsync(newerLease, token).ConfigureAwait(false);
            }
            else
            {
                Assert.IsTrue(afterReinsert.Value!.Completed, "An exact input held only by completed private replay cannot block earlier public fits.");
                Assert.IsTrue(afterReinsert.Value.PreviousSummaryEvicted);
                Assert.AreEqual(olderFit, afterReinsert.Value.Summary);
                await EvictMeasurementAsync().ConfigureAwait(false);
                await RestoreFilesAsync().ConfigureAwait(false);
                var repairStore = reopen ? reopenedStore : store;
                await repairStore.WriteNodeAsync(capture, measuredNode, DurableProcessingNodeStatus.Completed,
                    null, 2, null, null, DateTimeOffset.UtcNow, null, ProcessingOutcomeStatus.Produced,
                    [], 0, null, [measured], token).ConfigureAwait(false);
            }

            await ExpectPublicArtifactAsync(measured.ArtifactId, CameraAgentArtifactReadStatus.Found).ConfigureAwait(false);
            var restored = await publicReader.GetLatestAcceptedAstrometryAsync(token, selectedAssessment:
                new(capture, newerFit.ArtifactId, newerFit.SolutionIdentitySha256)).ConfigureAwait(false);
            Assert.IsTrue(restored.IsSuccess);
            Assert.IsFalse(restored.Value!.SelectedAssessmentInvalidated);
            Assert.AreEqual(newerFit, restored.Value.Summary);

            async ValueTask EvictMeasurementAsync()
            {
                var operation = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", measured.OutputIdentitySha256,
                    measured.PayloadRelativePath, measured.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}",
                    "retention-expired", payload.Length + sidecar.Length);
                await store.PlanLifecycleOperationAsync(operation, token).ConfigureAwait(false);
                await reconciler.ResumeOperationAsync(operation, token).ConfigureAwait(false);
                Assert.IsNull(await store.ReadOutputByArtifactIdAsync(measured.ArtifactId, token).ConfigureAwait(false));
            }

            async ValueTask RestoreFilesAsync()
            {
                await File.WriteAllBytesAsync(payloadPath, payload, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(sidecarPath, sidecar, token).ConfigureAwait(false);
            }

            async ValueTask WriteExecutionOutputAsync(CaptureProcessingGraphNode node, DurableProcessingOutput output,
                ProcessingExecutionContext execution)
            {
                var plan = revision.Nodes.Single(item => item.NodeId == node.Id);
                await store.WriteNodeAsync(capture, node with { PlanSha256 = plan.PlanSha256 },
                    DurableProcessingNodeStatus.Completed, null, 1, null, null, DateTimeOffset.UtcNow, null,
                    ProcessingOutcomeStatus.Produced, [], execution.WorkId, execution.LeaseToken,
                    [output], execution, ProcessingNodeExecutionRoute.InProcess, token).ConfigureAwait(false);
            }

            async ValueTask ExpectPublicArtifactAsync(Guid artifactId, CameraAgentArtifactReadStatus expected)
            {
                var read = await artifacts.OpenContentAsync(artifactId, token).ConfigureAwait(false);
                if (read.Content is { } content) await content.DisposeAsync().ConfigureAwait(false);
                Assert.AreEqual(expected, read.Status);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
