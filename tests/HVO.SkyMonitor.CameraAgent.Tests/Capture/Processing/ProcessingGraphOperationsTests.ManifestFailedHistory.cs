using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Testing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow(true, "RetryableFailure")]
    [DataRow(true, "untyped")]
    [DataRow(true, "Produced")]
    [DataRow(false, "RetryableFailure")]
    [DataRow(false, "untyped")]
    [DataRow(false, "Produced")]
    public async Task FailedRetryHistoryDownloadsOnlyWhileItsOriginalOutputsAreProvenRetained(bool legacy, string firstOutcome)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-failed-history");
        Guid id;
        Guid artifactId;
        string retained;
        var unavailable = legacy ? CameraAgentRunManifest.UnrecordedOutputReason : CameraAgentRunManifest.ArtifactEvidenceUnavailableReason;
        try
        {
            using (var provider = CreateProvider(root))
            {
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var liveId = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
                var live = (await operations.ReadExecutionDetailAsync(liveId, CancellationToken.None).ConfigureAwait(false))!;
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                artifactId = live.Nodes.Single().Outputs.Single().ArtifactId;
                var output = (await store.ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false))!;
                var replay = await operations.SubmitReplayAsync(new(live.Execution.CaptureId, live.Execution.GraphRevisionId,
                    live.Execution.PrimaryArtifactId), "failed-history", "test", CancellationToken.None).ConfigureAwait(false);
                id = replay.Execution.ExecutionId;
                var lease = (await store.ClaimReplayAsync("failed-history", CancellationToken.None).ConfigureAwait(false))!;
                var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateRetainedGraph(lease.Configuration);
                try
                {
                    var node = graph.Nodes.Single();
                    var context = new ProcessingExecutionContext(id, ProcessingGraphExecutionClass.Replay,
                        lease.Execution.GraphRevisionId, lease.Execution.LocalPlanIdentitySha256, false,
                        lease.WorkId, lease.LeaseToken, lease.LeaseOwner, lease.Execution.DeadlineUtc, lease.ClaimCount);
                    var now = DateTimeOffset.UtcNow;
                    await store.BeginNodeAttemptAsync(context, node, lease.ClaimCount, now, CancellationToken.None).ConfigureAwait(false);
                    await store.WriteNodeAsync(live.Execution.CaptureId, node, DurableProcessingNodeStatus.RetryableFailure,
                        "partial-before-failure", lease.ClaimCount, null, now, now, TimeSpan.Zero,
                        firstOutcome == "untyped" ? null : Enum.Parse<ProcessingOutcomeStatus>(firstOutcome), [], lease.WorkId, lease.LeaseToken,
                        [output], context, ProcessingNodeExecutionRoute.Unknown, CancellationToken.None).ConfigureAwait(false);
                    await store.CompleteReplayAsync(lease, CaptureLaneHandlerResult.Retry("retry-before-failure"), CancellationToken.None).ConfigureAwait(false);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var retry = await store.ClaimReplayAsync("failed-history", deadline.Token).ConfigureAwait(false);
                    while (retry is null)
                    {
                        await Task.Delay(50, deadline.Token).ConfigureAwait(false);
                        retry = await store.ClaimReplayAsync("failed-history", deadline.Token).ConfigureAwait(false);
                    }
                    Assert.AreEqual(2, retry.ClaimCount);
                    context = context with { WorkId = retry.WorkId, LeaseToken = retry.LeaseToken, LeaseOwner = retry.LeaseOwner, DurableAttempt = retry.ClaimCount };
                    await store.BeginNodeAttemptAsync(context, node, retry.ClaimCount, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    await store.CompleteOutputlessExecutionNodeAsync(context, node, DurableProcessingNodeStatus.TerminalFailure,
                        "second-attempt-failed", retry.ClaimCount, DateTimeOffset.UtcNow, TimeSpan.Zero,
                        ProcessingOutcomeStatus.TerminalFailure, ProcessingNodeExecutionRoute.Unknown, CancellationToken.None).ConfigureAwait(false);
                    await store.CompleteReplayAsync(retry, CaptureLaneHandlerResult.Terminal("second-attempt-failed"), CancellationToken.None).ConfigureAwait(false);
                    retained = await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
                    var detail = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                    Assert.AreEqual("TerminalFailure", detail.Nodes.Single().Status);
                    Assert.HasCount(2, detail.Nodes.Single().Attempts);
                    if (legacy)
                    {
                        await ReplaceManifestCounterAsync(root, id, "committedOutputCounts", null).ConfigureAwait(false);
                    }
                    Assert.AreEqual(retained, await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false));
                }
                finally { graph.DisposeSteps(); }
            }
            SqliteConnection.ClearAllPools();
            using (var restarted = CreateProvider(root))
            {
                await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                Assert.AreEqual(retained, await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false));
                var store = restarted.GetRequiredService<SqliteCaptureProcessingStore>();
                var output = (await store.ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false))!;
                var deletion = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", output.OutputIdentitySha256,
                    output.PayloadRelativePath, output.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}", "retention-expired", 0);
                await store.PlanLifecycleOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                await new DerivedProductReconciler(root, store).ResumeOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNull(await store.ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false));
                await RunManifestTestAssertions.ResponseAsync(operations, id, unavailable).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using var reopened = CreateProvider(root);
            await reopened.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recovered = reopened.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await RunManifestTestAssertions.ResponseAsync(recovered, id, unavailable).ConfigureAwait(false);
            var journal = (await recovered.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual("TerminalFailure", journal.Nodes.Single().Status);
            Assert.HasCount(2, journal.Nodes.Single().Attempts);
            Assert.IsEmpty(journal.Nodes.Single().Outputs);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task AuxiliaryDescriptorCorruptionOnlyDisablesManifestAndRecoversAcrossRestart()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-aux-corrupt");
        try
        {
            using var provider = CreateProvider(root);
            await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            var id = await CompleteManifestPreviewAsync(provider, sequence: 1).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var detail = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
            var auxiliary = await AuxiliaryRunManifestEvidenceFixture.CreateAsync(root,
                detail.Nodes.Single().Outputs.Single().ArtifactId).ConfigureAwait(false);
            await using var auxiliaryLifetime = auxiliary.ConfigureAwait(false);
            var retained = await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
            StringAssert.Contains(retained, auxiliary.ReferenceArtifactId.ToString("D"), StringComparison.OrdinalIgnoreCase);
            StringAssert.Contains(retained, "AuxiliaryProcessingOutput", StringComparison.Ordinal);
            var facts = new Dictionary<string, string>();
            foreach (var consumer in new[] { "detail", "api", "ui", "replay", "export" })
                facts.Add(consumer, await ReadManifestJournalConsumerAsync(root, operations, id, consumer, false).ConfigureAwait(false));
            await auxiliary.CorruptAsync().ConfigureAwait(false);
            await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            foreach (var (consumer, expected) in facts)
                Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, operations, id, consumer, consumer == "ui").ConfigureAwait(false));
            await provider.DisposeAsync().ConfigureAwait(false);
            SqliteConnection.ClearAllPools();
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recovered = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await RunManifestTestAssertions.ResponseAsync(recovered, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            foreach (var (consumer, expected) in facts)
                Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, recovered, id, consumer, consumer == "ui").ConfigureAwait(false));
            await auxiliary.RestoreReferenceAsync().ConfigureAwait(false);
            Assert.AreEqual(retained, await RunManifestTestAssertions.ResponseAsync(recovered, id).ConfigureAwait(false));
            foreach (var (consumer, expected) in facts)
                Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, recovered, id, consumer, false).ConfigureAwait(false));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
