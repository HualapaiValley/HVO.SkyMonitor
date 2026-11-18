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
#pragma warning disable CA1849 // SQLite test transaction follows the store's immediate transaction convention.
        using var transaction = connection.BeginTransaction();
#pragma warning restore CA1849
        var kind = member == "committedOutputCounts" ? SqliteCaptureProcessingStore.ManifestOutputLedger : SqliteCaptureProcessingStore.ManifestInputLedger;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM processing_execution_manifest_counts WHERE execution_id=$execution AND ledger_kind=$kind AND node_id<>''; UPDATE processing_execution_manifest_counts SET evidence_status='Recorded' WHERE execution_id=$execution AND ledger_kind=$kind AND node_id='';";
        command.Parameters.AddWithValue("$execution", id.ToString("N"));
        command.Parameters.AddWithValue("$kind", kind);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (replacement is not null)
        {
            // Fault payloads are only a fixture notation. The current durable schema
            // stores typed counts and an explicit corruption state, never this JSON.
            try
            {
                using var document = JsonDocument.Parse(replacement);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name.Length is 0 or > 128 || !seen.Add(property.Name) || !property.Value.TryGetInt32(out var count) ||
                        count < 0 || count > (kind == SqliteCaptureProcessingStore.ManifestInputLedger ? 512 : 128)) throw new InvalidDataException();
                    await SqliteCaptureProcessingStore.RecordManifestCountAsync(connection, transaction, id, kind, property.Name, count, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException)
            {
                command.CommandText = "UPDATE processing_execution_manifest_counts SET evidence_status='Corrupt' WHERE execution_id=$execution AND ledger_kind=$kind AND node_id='';";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    internal static async Task<int?> ReadManifestCounterAsync(string root, Guid id, string member, string nodeId)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT evidence_count FROM processing_execution_manifest_counts WHERE execution_id=$execution AND ledger_kind=$kind AND node_id=$node;";
        command.Parameters.AddWithValue("$execution", id.ToString("N"));
        command.Parameters.AddWithValue("$kind", member == "committedOutputCounts" ? SqliteCaptureProcessingStore.ManifestOutputLedger : SqliteCaptureProcessingStore.ManifestInputLedger);
        command.Parameters.AddWithValue("$node", nodeId);
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
