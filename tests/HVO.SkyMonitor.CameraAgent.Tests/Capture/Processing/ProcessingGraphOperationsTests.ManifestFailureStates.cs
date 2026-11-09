using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
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
    [DataRow("detail", 65)]
    [DataRow("api", 65)]
    [DataRow("ui", 65)]
    [DataRow("replay", 65)]
    [DataRow("export", 65)]
    [DataRow("detail", 1024)]
    [DataRow("api", 1024)]
    [DataRow("ui", 1024)]
    [DataRow("replay", 1024)]
    [DataRow("export", 1024)]
    [DataRow("detail", 16384)]
    [DataRow("api", 16384)]
    [DataRow("ui", 16384)]
    [DataRow("replay", 16384)]
    [DataRow("export", 16384)]
    public async Task DeepOptionalManifestCountsPreserveConsumersAcrossRestart(string consumer, int depth)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-depth");
        try
        {
            Guid id;
            string expected;
            byte[] original;
            using (var provider = CreateProvider(root))
            {
                id = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                if (consumer == "replay")
                {
                    var live = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                    id = (await ExecuteReplayAsync(provider, new(live.Execution.CaptureId,
                        live.Execution.GraphRevisionId, live.Execution.PrimaryArtifactId), "deep-count-replay").ConfigureAwait(false)).Execution.ExecutionId;
                }
                expected = await ReadManifestJournalConsumerAsync(root, operations, id, consumer, corrupt: false).ConfigureAwait(false);
                original = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
                await ReplaceManifestCountsAsync(root, id, original, new string('[', depth) + "0" + new string(']', depth)).ConfigureAwait(false);
                await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
                Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, operations, id, consumer, corrupt: true).ConfigureAwait(false));
            }
            SqliteConnection.ClearAllPools();
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recovered = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, recovered, id, consumer, corrupt: true).ConfigureAwait(false));
            await RunManifestTestAssertions.ResponseAsync(recovered, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await ReadManifestJournalConsumerAsync(
                root, recovered, id, consumer, corrupt: true, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
            await ReplaceManifestCountsAsync(root, id, original, "{\"preview\":1}").ConfigureAwait(false);
            Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, recovered, id, consumer, corrupt: false).ConfigureAwait(false));
            await RunManifestTestAssertions.ResponseAsync(recovered, id).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NewWorkerFailureAfterProducedKeepsDownloadableZeroOutputEvidence(bool replay)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-failed-produced");
        try
        {
            using var provider = CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:ReplayMaximumAttempts"] = "1",
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
            }, configureServices: services => services.AddSingleton(new CaptureProcessingStepRegistration(
                "FailureAfterProduced", typeof(FailureAfterProducedStep), typeof(FailureAfterProducedOptions), AutoInclude: false)));
            var config = CreateConfiguration() with
            {
                Pipeline = new([new("FailureAfterProduced", "preview", Required: false, DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var registry = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var receipt = await ingress.AcceptAsync(config, CreateSubmission(), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            await CompleteLiveAsync(provider, config).ConfigureAwait(false);
            var id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
            if (replay)
            {
                var submitted = await operations.SubmitReplayAsync(new(receipt.Manifest.Descriptor.Capture.CaptureId,
                    registry.ActiveRevisionId, receipt.Manifest.Descriptor.Artifact.ArtifactId), "produced-failure", "test", CancellationToken.None).ConfigureAwait(false);
                id = submitted.Execution.ExecutionId;
                await RunReplayToCompletionAsync(provider, id).ConfigureAwait(false);
            }
            var detail = (await operations.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false))!;
            var node = detail.Nodes.Single();
            Assert.AreEqual("TerminalFailure", node.Status);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, node.Attempts.Single().Outcome);
            Assert.IsEmpty(node.Outputs);
            Assert.AreEqual(0, node.CommittedOutputCount);
            await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
            var facts = new Dictionary<string, string>();
            foreach (var consumer in new[] { "detail", "api", "ui", "replay", "export" })
                facts.Add(consumer, await ReadManifestJournalConsumerAsync(root, operations, id, consumer, corrupt: false).ConfigureAwait(false));
            await provider.DisposeAsync().ConfigureAwait(false);
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recovered = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            Assert.AreEqual(0, (await recovered.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false))!.Nodes.Single().CommittedOutputCount);
            await RunManifestTestAssertions.ResponseAsync(recovered, id).ConfigureAwait(false);
            foreach (var (consumer, expected) in facts)
                Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, recovered, id, consumer, corrupt: false).ConfigureAwait(false));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("RetryableFailure", false)]
    [DataRow("TerminalFailure", false)]
    [DataRow("Skipped", true)]
    [DataRow("Skipped", false)]
    public async Task NodeCompletionRecordsFailedAttemptsWithoutDependingOnOptionalCounts(string status, bool deepCorruption)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-completion");
        try
        {
            using var provider = CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
            });
            var liveId = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var live = (await operations.ReadExecutionDetailAsync(liveId, CancellationToken.None).ConfigureAwait(false))!;
            var submitted = await operations.SubmitReplayAsync(new(live.Execution.CaptureId,
                live.Execution.GraphRevisionId, live.Execution.PrimaryArtifactId), "count-completion", "test", CancellationToken.None).ConfigureAwait(false);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var lease = (await store.ClaimReplayAsync("count-completion", CancellationToken.None).ConfigureAwait(false))!;
            var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateRetainedGraph(lease.Configuration);
            try
            {
                var node = graph.Nodes.Single();
                var context = new ProcessingExecutionContext(submitted.Execution.ExecutionId, ProcessingGraphExecutionClass.Replay,
                    lease.Execution.GraphRevisionId, lease.Execution.LocalPlanIdentitySha256, false,
                    lease.WorkId, lease.LeaseToken, lease.LeaseOwner, lease.Execution.DeadlineUtc, lease.ClaimCount);
                await store.BeginNodeAttemptAsync(context, node, lease.ClaimCount, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                var corrupt = status == "Skipped";
                if (corrupt)
                    await ReplaceManifestCountsAsync(root, context.ExecutionId, await ReadFrozenExecutionJsonAsync(root, context.ExecutionId).ConfigureAwait(false),
                        deepCorruption ? new string('[', 1024) + "0" + new string(']', 1024) : "{\"unrelated\":-1}").ConfigureAwait(false);
                await store.CompleteOutputlessExecutionNodeAsync(context, node, Enum.Parse<DurableProcessingNodeStatus>(status),
                    "failure-after-produced", lease.ClaimCount, DateTimeOffset.UtcNow, TimeSpan.Zero,
                    ProcessingOutcomeStatus.Produced, ProcessingNodeExecutionRoute.Unknown, CancellationToken.None).ConfigureAwait(false);
                var detail = (await operations.ReadExecutionDetailAsync(context.ExecutionId, CancellationToken.None).ConfigureAwait(false))!;
                Assert.AreEqual(status, detail.Nodes.Single().Status);
                await RunManifestTestAssertions.ResponseAsync(operations, context.ExecutionId, corrupt ? CameraAgentRunManifest.CorruptReason : null).ConfigureAwait(false);
                if (!corrupt)
                {
                    var manifest = (await operations.ReadRunManifestAsync(context.ExecutionId, CancellationToken.None).ConfigureAwait(false))!;
                    Assert.AreEqual(0, manifest.Nodes.Single().CommittedOutputCount);
                }
                if (status == "RetryableFailure")
                {
                    await store.CompleteReplayAsync(lease, CaptureLaneHandlerResult.Retry("transient"), CancellationToken.None).ConfigureAwait(false);
                    var recovered = await RunReplayToCompletionAsync(provider, context.ExecutionId).ConfigureAwait(false);
                    Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, recovered.Execution.Status);
                    await RunManifestTestAssertions.ResponseAsync(operations, context.ExecutionId).ConfigureAwait(false);
                    Assert.AreEqual(1, (await operations.ReadRunManifestAsync(context.ExecutionId, CancellationToken.None).ConfigureAwait(false))!.Nodes.Single().CommittedOutputCount);
                }
            }
            finally { graph.DisposeSteps(); }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static async Task ReplaceManifestCountsAsync(string root, Guid id, byte[] original, string countsJson)
    {
        var plan = JsonNode.Parse(original)!.AsObject();
        plan.Remove("committedOutputCounts");
        var primary = plan.ToJsonString();
        var json = Encoding.UTF8.GetBytes(primary[..^1] + ",\"committedOutputCounts\":" + countsJson + "}");
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE processing_executions SET frozen_plan_json = $json WHERE execution_id = $execution;";
        command.Parameters.AddWithValue("$execution", id.ToString("N"));
        command.Parameters.AddWithValue("$json", json);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Constructed by the configuration-driven test step factory.")]
    private sealed class FailureAfterProducedOptions;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Constructed by the configuration-driven test step factory.")]
    private sealed class FailureAfterProducedStep(CaptureProcessingStepMetadata metadata, FailureAfterProducedOptions options)
        : ConfigurableCaptureProcessingStep<FailureAfterProducedOptions>(metadata, options), ICaptureProcessingGraphStep
    {
        public bool Enabled => true;
        public string RecipeName => "produced-then-throw";
        public FrameArtifactRole OutputRole => FrameArtifactRole.Preview;
        public string OutputVariant => "display";
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };
        public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Produced());
            throw new IOException("Failure after registering Produced, before derivative commitment.");
        }
    }
}
