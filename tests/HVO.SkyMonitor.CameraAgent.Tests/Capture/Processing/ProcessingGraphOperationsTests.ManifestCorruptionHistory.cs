using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow("high")]
    [DataRow("low")]
    [DataRow("valid-escape")]
    public async Task MalformedCountNamesPreserveJournalAndRefuseOnlyCorruptManifestsAcrossRestart(string variant)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-unicode");
        try
        {
            var originals = new Dictionary<Guid, byte[]>();
            var facts = new Dictionary<(Guid Id, string Consumer), string>();
            var corrupt = variant != "valid-escape";
            var counts = variant switch
            {
                "high" => "{\"\\uD800\":1}",
                "low" => "{\"\\uDC00\":1}",
                _ => "{\"\\u0070review\":1}"
            };
            using (var provider = CreateProvider(root))
            {
                var liveId = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var live = (await operations.ReadExecutionDetailAsync(liveId, CancellationToken.None).ConfigureAwait(false))!;
                var replay = await ExecuteReplayAsync(provider, new(live.Execution.CaptureId,
                    live.Execution.GraphRevisionId, live.Execution.PrimaryArtifactId), "unicode-replay").ConfigureAwait(false);
                foreach (var id in new[] { liveId, replay.Execution.ExecutionId })
                {
                    originals[id] = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
                    foreach (var consumer in new[] { "detail", "api", "ui", "replay", "export" })
                        facts[(id, consumer)] = await ReadManifestJournalConsumerAsync(root, operations, id, consumer, false).ConfigureAwait(false);
                    await ReplaceManifestCountsAsync(root, id, originals[id], counts).ConfigureAwait(false);
                    await RunManifestTestAssertions.ResponseAsync(operations, id, corrupt ? CameraAgentRunManifest.CorruptReason : null).ConfigureAwait(false);
                    foreach (var ((run, consumer), expected) in facts.Where(pair => pair.Key.Id == id))
                        Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, operations, run, consumer, corrupt).ConfigureAwait(false));
                }
            }
            SqliteConnection.ClearAllPools();
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recovered = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            foreach (var ((id, consumer), expected) in facts)
                Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, recovered, id, consumer, corrupt).ConfigureAwait(false));
            foreach (var (id, original) in originals)
            {
                await RunManifestTestAssertions.ResponseAsync(recovered, id, corrupt ? CameraAgentRunManifest.CorruptReason : null).ConfigureAwait(false);
                await ReplaceFrozenEvidenceAsync(root, id, original).ConfigureAwait(false);
                await RunManifestTestAssertions.ResponseAsync(recovered, id).ConfigureAwait(false);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("high")]
    [DataRow("low")]
    [DataRow("syntax")]
    [DataRow("scalar")]
    public async Task CorruptExportBookkeepingDoesNotRollBackNodeCompletion(string variant)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-completion-corrupt");
        try
        {
            using var provider = CreateProvider(root);
            var liveId = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var live = (await operations.ReadExecutionDetailAsync(liveId, CancellationToken.None).ConfigureAwait(false))!;
            var replay = await operations.SubmitReplayAsync(new(live.Execution.CaptureId, live.Execution.GraphRevisionId,
                live.Execution.PrimaryArtifactId), "corrupt-completion", "test", CancellationToken.None).ConfigureAwait(false);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var lease = (await store.ClaimReplayAsync("corrupt-completion", CancellationToken.None).ConfigureAwait(false))!;
            var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateRetainedGraph(lease.Configuration);
            try
            {
                var node = graph.Nodes.Single();
                var context = new ProcessingExecutionContext(replay.Execution.ExecutionId, ProcessingGraphExecutionClass.Replay,
                    lease.Execution.GraphRevisionId, lease.Execution.LocalPlanIdentitySha256, false,
                    lease.WorkId, lease.LeaseToken, lease.LeaseOwner, lease.Execution.DeadlineUtc, lease.ClaimCount);
                await store.BeginNodeAttemptAsync(context, node, lease.ClaimCount, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                var original = await ReadFrozenExecutionJsonAsync(root, context.ExecutionId).ConfigureAwait(false);
                if (variant is "high" or "low")
                    await ReplaceManifestCountsAsync(root, context.ExecutionId, original,
                        variant == "high" ? "{\"\\uD800\":1}" : "{\"\\uDC00\":1}").ConfigureAwait(false);
                else
                    await ReplaceFrozenEvidenceAsync(root, context.ExecutionId, Encoding.UTF8.GetBytes(variant == "syntax" ? "{not-json" : "null")).ConfigureAwait(false);
                var damaged = await ReadFrozenExecutionJsonAsync(root, context.ExecutionId).ConfigureAwait(false);
                await store.CompleteOutputlessExecutionNodeAsync(context, node, DurableProcessingNodeStatus.Skipped,
                    "legitimate-skip", lease.ClaimCount, DateTimeOffset.UtcNow, TimeSpan.Zero,
                    ProcessingOutcomeStatus.Skipped, ProcessingNodeExecutionRoute.Unknown, CancellationToken.None).ConfigureAwait(false);
                CollectionAssert.AreEqual(damaged, await ReadFrozenExecutionJsonAsync(root, context.ExecutionId).ConfigureAwait(false),
                    "Corrupt export evidence must be preserved, not silently repaired.");
                using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
                {
                    await db.OpenAsync().ConfigureAwait(false);
                    using var read = db.CreateCommand();
                    read.CommandText = "SELECT status FROM processing_execution_nodes WHERE execution_id=$id;";
                    read.Parameters.AddWithValue("$id", context.ExecutionId.ToString("N"));
                    Assert.AreEqual("Skipped", await read.ExecuteScalarAsync().ConfigureAwait(false));
                }
                await RunManifestTestAssertions.ResponseAsync(operations, context.ExecutionId, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
                await ReplaceFrozenEvidenceAsync(root, context.ExecutionId, original).ConfigureAwait(false);
                var detail = (await operations.ReadExecutionDetailAsync(context.ExecutionId, CancellationToken.None).ConfigureAwait(false))!;
                Assert.AreEqual("Skipped", detail.Nodes.Single().Status);
                Assert.AreEqual("Skipped", detail.Nodes.Single().Attempts.Single().Status);
                await RunManifestTestAssertions.ResponseAsync(operations, context.ExecutionId).ConfigureAwait(false);
            }
            finally { graph.DisposeSteps(); }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public async Task RetryThenSkipCannotHidePrunedOutputsInLegacyHistory(bool legacy, bool typedOutcome)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-legacy-retry");
        Guid id;
        try
        {
            using (var provider = CreateProvider(root))
            {
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var liveId = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
                var live = (await operations.ReadExecutionDetailAsync(liveId, CancellationToken.None).ConfigureAwait(false))!;
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var output = (await store.ReadOutputByArtifactIdAsync(live.Nodes.Single().Outputs.Single().ArtifactId, CancellationToken.None).ConfigureAwait(false))!;
                var replay = await operations.SubmitReplayAsync(new(live.Execution.CaptureId, live.Execution.GraphRevisionId,
                    live.Execution.PrimaryArtifactId), "legacy-retry-skip", "test", CancellationToken.None).ConfigureAwait(false);
                id = replay.Execution.ExecutionId;
                var lease = (await store.ClaimReplayAsync("legacy-retry-skip", CancellationToken.None).ConfigureAwait(false))!;
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
                        "partial-before-retry", lease.ClaimCount, null, now, now, TimeSpan.Zero,
                        typedOutcome ? ProcessingOutcomeStatus.Produced : null, [], lease.WorkId, lease.LeaseToken,
                        [output], context, ProcessingNodeExecutionRoute.Unknown, CancellationToken.None).ConfigureAwait(false);
                    await store.CompleteReplayAsync(lease, CaptureLaneHandlerResult.Retry("retry-skip"), CancellationToken.None).ConfigureAwait(false);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var retry = await store.ClaimReplayAsync("legacy-retry-skip", deadline.Token).ConfigureAwait(false);
                    while (retry is null)
                    {
                        await Task.Delay(50, deadline.Token).ConfigureAwait(false);
                        retry = await store.ClaimReplayAsync("legacy-retry-skip", deadline.Token).ConfigureAwait(false);
                    }
                    Assert.AreEqual(2, retry.ClaimCount);
                    context = context with { WorkId = retry.WorkId, LeaseToken = retry.LeaseToken, LeaseOwner = retry.LeaseOwner, DurableAttempt = retry.ClaimCount };
                    await store.BeginNodeAttemptAsync(context, node, retry.ClaimCount, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    await store.CompleteOutputlessExecutionNodeAsync(context, node, DurableProcessingNodeStatus.Skipped,
                        "second-attempt-skipped", retry.ClaimCount, DateTimeOffset.UtcNow, TimeSpan.Zero,
                        ProcessingOutcomeStatus.Skipped, ProcessingNodeExecutionRoute.Unknown, CancellationToken.None).ConfigureAwait(false);
                    await store.CompleteReplayAsync(retry, CaptureLaneHandlerResult.Success, CancellationToken.None).ConfigureAwait(false);
                    await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
                    var original = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
                    if (legacy)
                    {
                        await ReplaceManifestCounterAsync(root, id, "committedOutputCounts", null).ConfigureAwait(false);
                        await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
                    }
                    var deletion = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", output.OutputIdentitySha256,
                        output.PayloadRelativePath, output.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}", "retention-expired", 0);
                    await store.PlanLifecycleOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                    await new DerivedProductReconciler(root, store).ResumeOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNull(await store.ReadOutputByArtifactIdAsync(live.Nodes.Single().Outputs.Single().ArtifactId, CancellationToken.None).ConfigureAwait(false));
                    await RunManifestTestAssertions.ResponseAsync(operations, id,
                        legacy ? CameraAgentRunManifest.UnrecordedOutputReason : CameraAgentRunManifest.ArtifactEvidenceUnavailableReason).ConfigureAwait(false);
                }
                finally { graph.DisposeSteps(); }
            }
            SqliteConnection.ClearAllPools();
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await RunManifestTestAssertions.ResponseAsync(restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>(), id,
                legacy ? CameraAgentRunManifest.UnrecordedOutputReason : CameraAgentRunManifest.ArtifactEvidenceUnavailableReason).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static async Task ReplaceFrozenEvidenceAsync(string root, Guid id, byte[] bytes)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await db.OpenAsync().ConfigureAwait(false);
        using var update = db.CreateCommand();
        update.CommandText = "UPDATE processing_executions SET frozen_plan_json=$json WHERE execution_id=$id;";
        update.Parameters.AddWithValue("$json", bytes);
        update.Parameters.AddWithValue("$id", id.ToString("N"));
        Assert.AreEqual(1, await update.ExecuteNonQueryAsync().ConfigureAwait(false));
    }
}
