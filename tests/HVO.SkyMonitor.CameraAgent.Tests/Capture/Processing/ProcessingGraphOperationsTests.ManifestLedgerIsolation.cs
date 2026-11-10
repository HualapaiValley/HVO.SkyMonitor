using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow("deep", false)]
    [DataRow("deep", true)]
    [DataRow("negative", false)]
    [DataRow("negative", true)]
    [DataRow("unicode", false)]
    [DataRow("unicode", true)]
    public async Task CorruptInputCountsCannotEraseNewFailedOutputEvidenceAcrossRestart(string variant, bool replay)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-ledger-isolation");
        Guid id;
        try
        {
            using (var provider = CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:ReplayMaximumAttempts"] = "1",
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
            }, configureServices: services => services.AddSingleton(new CaptureProcessingStepRegistration(
                "FailureAfterProduced", typeof(FailureAfterProducedStep), typeof(FailureAfterProducedOptions), AutoInclude: false))))
            {
                var configuration = CreateConfiguration() with
                {
                    Pipeline = new([new("FailureAfterProduced", "preview", Required: false, DependsOn: ["$raw"])],
                        CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
                };
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var receipt = (await ingress.AcceptAsync(configuration, CreateSubmission(), CancellationToken.None).ConfigureAwait(false))!;
                id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
                if (replay)
                {
                    await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
                    id = (await operations.SubmitReplayAsync(new(receipt.Manifest.Descriptor.Capture.CaptureId,
                        registry.ActiveRevisionId, receipt.Manifest.Descriptor.Artifact.ArtifactId), "isolate-ledgers", "test", CancellationToken.None).ConfigureAwait(false)).Execution.ExecutionId;
                }
                await ReplaceManifestCounterAsync(root, id, "frozenOutputInputCounts", FrozenInputCorruption(variant)).ConfigureAwait(false);
                if (replay) await RunReplayToCompletionAsync(provider, id).ConfigureAwait(false);
                else await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
                var journal = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                Assert.AreEqual("TerminalFailure", journal.Nodes.Single().Status);
                Assert.AreEqual(ProcessingOutcomeStatus.Produced, journal.Nodes.Single().Attempts.Single().Outcome);
                Assert.IsEmpty(journal.Nodes.Single().Outputs);
                await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using var reopened = CreateProvider(root);
            await reopened.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recovered = reopened.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await RunManifestTestAssertions.ResponseAsync(recovered, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            foreach (var consumer in new[] { "detail", "api", "ui", "replay", "export" })
                await ReadManifestJournalConsumerAsync(root, recovered, id, consumer, true).ConfigureAwait(false);
            await ReplaceManifestCounterAsync(root, id, "frozenOutputInputCounts", null).ConfigureAwait(false);
            await RunManifestTestAssertions.ResponseAsync(recovered, id).ConfigureAwait(false);
            Assert.AreEqual(0, await ReadManifestCounterAsync(root, id, "committedOutputCounts", "preview").ConfigureAwait(false));
            foreach (var consumer in new[] { "detail", "api", "ui", "replay", "export" })
                await ReadManifestJournalConsumerAsync(root, recovered, id, consumer, false).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    internal static async Task ReplaceManifestCounterAsync(string root, Guid id, string member, string? replacement)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        if (replacement is null)
            command.CommandText = "UPDATE processing_executions SET frozen_plan_json=CAST(json_remove(CAST(frozen_plan_json AS TEXT), $path) AS BLOB) WHERE execution_id=$execution;";
        else
            command.CommandText = "UPDATE processing_executions SET frozen_plan_json=CAST(json_set(CAST(frozen_plan_json AS TEXT), $path, json($value)) AS BLOB) WHERE execution_id=$execution;";
        command.Parameters.AddWithValue("$path", "$." + member);
        command.Parameters.AddWithValue("$execution", id.ToString("N"));
        if (replacement is not null) command.Parameters.AddWithValue("$value", replacement);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    internal static async Task<int?> ReadManifestCounterAsync(string root, Guid id, string member, string nodeId)
    {
        var bytes = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
        Assert.IsLessThan(65536, bytes.Length, "This fixture must be far below the existing plan byte budget.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 1024 });
        return document.RootElement.TryGetProperty(member, out var counts) && counts.TryGetProperty(nodeId, out var count)
            ? count.GetInt32() : null;
    }
}
