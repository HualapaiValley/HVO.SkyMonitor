using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ManifestLedgerAdmissionAndCompletionDoNotRewriteTheFrozenPlan(bool replay)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-ledger-no-plan-write");
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig([new("Preview", "preview", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var receipt = (await ingress.AcceptAsync(configuration, CreateSubmission(), CancellationToken.None).ConfigureAwait(false))!;
            var id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
            if (replay)
            {
                await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
                id = (await operations.SubmitReplayAsync(new(receipt.Manifest.Descriptor.Capture.CaptureId,
                    registry.ActiveRevisionId, receipt.Manifest.Descriptor.Artifact.ArtifactId), "side-ledger", "test", CancellationToken.None).ConfigureAwait(false)).Execution.ExecutionId;
            }
            var before = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
            using (var frozen = JsonDocument.Parse(before))
            {
                Assert.IsFalse(frozen.RootElement.TryGetProperty("frozenOutputInputCounts", out _));
                Assert.IsFalse(frozen.RootElement.TryGetProperty("committedOutputCounts", out _));
            }
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var guard = connection.CreateCommand();
            guard.CommandText = """
                CREATE TRIGGER reject_frozen_plan_update BEFORE UPDATE OF frozen_plan_json ON processing_executions
                BEGIN SELECT RAISE(ABORT, 'Manifest count updates must not rewrite the frozen plan'); END;
                """;
            await guard.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (replay) await RunReplayToCompletionAsync(provider, id).ConfigureAwait(false);
            else await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
            var detail = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, detail.Execution.Status);
            CollectionAssert.AreEqual(before, await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false));
            await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
            guard.CommandText = "DROP TRIGGER reject_frozen_plan_update;";
            await guard.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The legacy marker statements come only from the store's constant test schema.")]
    public async Task Schema8UpgradePreservesTheCompleteRunManifestAcrossRestart(bool replay)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-ledger-upgrade-manifest");
        try
        {
            Guid id;
            byte[] before;
            byte[] primary;
            using (var provider = CreateProvider(root))
            {
                id = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                if (replay)
                {
                    var live = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                    id = (await ExecuteReplayAsync(provider, new(live.Execution.CaptureId, live.Execution.GraphRevisionId, live.Execution.PrimaryArtifactId), "legacy-ledger-upgrade").ConfigureAwait(false)).Execution.ExecutionId;
                }
                before = System.Text.Encoding.UTF8.GetBytes(await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false));
                primary = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var legacy = connection.CreateCommand();
                var marker = string.Join(";", SqliteCaptureProcessingStore.LegacySchema8SqlForTests.Split(';').Where(static statement =>
                    statement.TrimStart().StartsWith("CREATE TABLE capture_processing_schema(", StringComparison.Ordinal) ||
                    statement.TrimStart().StartsWith("INSERT INTO capture_processing_schema(", StringComparison.Ordinal)));
                legacy.CommandText = "DROP TABLE processing_execution_manifest_counts; DROP TABLE capture_processing_schema;" + marker +
                    "; UPDATE processing_executions SET frozen_plan_json=$plan WHERE execution_id=$execution;";
                legacy.Parameters.AddWithValue("$execution", id.ToString("N"));
                legacy.Parameters.AddWithValue("$plan", System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(primary)[..^1] +
                    ",\"frozenOutputInputCounts\":{\"preview\":0},\"committedOutputCounts\":{\"preview\":1}}"));
                await legacy.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            for (var restart = 0; restart < 2; restart++)
            {
                using var provider = CreateProvider(root);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var after = System.Text.Encoding.UTF8.GetBytes(await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false));
                CollectionAssert.AreEqual(before, after);
                CollectionAssert.AreEqual(primary, await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false));
                Assert.AreEqual(1, await ReadManifestCounterAsync(root, id, "committedOutputCounts", "preview").ConfigureAwait(false));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task SideLedgerCapacityIsIndependentAndDoesNotConcealOrdinaryDetail()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-ledger-capacity");
        try
        {
            using var provider = CreateProvider(root);
            var id = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            var before = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
#pragma warning disable CA1849 // SQLite test transaction follows the store's immediate transaction convention.
                using var transaction = connection.BeginTransaction();
#pragma warning restore CA1849
                await SqliteCaptureProcessingStore.RecordManifestCountAsync(connection, transaction, id,
                    SqliteCaptureProcessingStore.ManifestOutputLedger, "preview", 0, CancellationToken.None).ConfigureAwait(false);
                for (var index = 0; index < 64; index++)
                    await SqliteCaptureProcessingStore.RecordManifestCountAsync(connection, transaction, id,
                        SqliteCaptureProcessingStore.ManifestOutputLedger, "additional-" + index, 1, CancellationToken.None).ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
                using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM processing_execution_manifest_counts WHERE execution_id=$id AND ledger_kind='Outputs' AND node_id<>'';";
                count.Parameters.AddWithValue("$id", id.ToString("N"));
                Assert.AreEqual(64L, (long)(await count.ExecuteScalarAsync().ConfigureAwait(false))!);
            }
            CollectionAssert.AreEqual(before, await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false));
            Assert.AreEqual(1, await ReadManifestCounterAsync(root, id, "committedOutputCounts", "preview").ConfigureAwait(false));
            Assert.AreEqual(0, await ReadManifestCounterAsync(root, id, "frozenOutputInputCounts", "preview").ConfigureAwait(false));
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            Assert.IsNotNull(await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false));
            await RunManifestTestAssertions.ResponseAsync(operations, id, ProcessingRunManifestBounds.UnavailableReason).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("Inputs", -1)]
    [DataRow("Inputs", 513)]
    [DataRow("Outputs", 129)]
    public async Task ManifestLedgerSchemaRefusesInvalidCountRows(string kind, int invalid)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-ledger-count-constraint");
        try
        {
            using var provider = CreateProvider(root);
            var id = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE processing_execution_manifest_counts SET evidence_count=$invalid WHERE execution_id=$id AND ledger_kind=$kind AND node_id='preview';";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$invalid", invalid);
            var error = await Assert.ThrowsExactlyAsync<SqliteException>(async () => await command.ExecuteNonQueryAsync().ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(19, error.SqliteErrorCode);
            await RunManifestTestAssertions.ResponseAsync(provider.GetRequiredService<ProcessingGraphOperationsCoordinator>(), id).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task MissingLedgerHeaderRefusesTheManifestWithoutConcealingTheJournal()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-ledger-header-loss");
        try
        {
            using var provider = CreateProvider(root);
            var id = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM processing_execution_manifest_counts WHERE execution_id=$id AND ledger_kind='Inputs' AND node_id='';";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            Assert.IsNotNull(await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false));
            await RunManifestTestAssertions.ResponseAsync(operations, id, HVO.SkyMonitor.CameraAgent.Services.CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            command.CommandText = "PRAGMA foreign_keys=ON; DELETE FROM processing_executions WHERE execution_id=$id;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            command.CommandText = "SELECT COUNT(*) FROM processing_execution_manifest_counts WHERE execution_id=$id;";
            Assert.AreEqual(0L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
