using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

// The restart regression clears process-wide pools to force fresh SQLite handles.
[DoNotParallelize]
public sealed partial class RollingCombinationWindowLineageTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task PinnedDescriptorCorruptionOnlyDisablesManifestAndPreservesJournalAndExportAcrossRestart()
    {
        var root = CreateRoot();
        try
        {
            Guid id;
            string identity;
            byte[] original;
            Dictionary<string, string> expected = [];
            using (var provider = CreateProvider(root))
            {
                var configuration = CreateConfiguration(root);
                var module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
                await using var moduleLifetime = module.ConfigureAwait(false);
                var (receipt, _) = await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
                var detail = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                var pinned = detail.Nodes.Single(node => node.NodeId == RollingNodeId).Inputs
                    .First(input => input.Kind == ProcessingGraphExecutionInputKind.ProcessingOutput && input.CaptureId != receipt.Manifest.Descriptor.Capture.CaptureId);
                identity = pinned.OutputIdentitySha256!;
                Assert.IsNull(pinned.PayloadSha256, "Ordinary evidence keeps the established null hash for historical output pins.");
                foreach (var consumer in new[] { "detail", "api", "ui", "replay", "export" })
                    expected.Add(consumer, await ProcessingGraphOperationsTests.ReadManifestJournalConsumerAsync(root, operations, id, consumer, corrupt: false).ConfigureAwait(false));
                await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
                using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT descriptor_json FROM processing_outputs WHERE output_identity_sha256 = $identity;";
                command.Parameters.AddWithValue("$identity", identity);
                original = (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
                command.CommandText = "UPDATE processing_outputs SET descriptor_json = CAST('{not-json' AS BLOB) WHERE output_identity_sha256 = $identity;";
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
                foreach (var (consumer, baseline) in expected)
                    Assert.AreEqual(baseline, await ProcessingGraphOperationsTests.ReadManifestJournalConsumerAsync(root, operations, id, consumer, corrupt: consumer == "ui").ConfigureAwait(false));
                await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recovered = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            foreach (var (consumer, baseline) in expected)
                Assert.AreEqual(baseline, await ProcessingGraphOperationsTests.ReadManifestJournalConsumerAsync(root, recovered, id, consumer, corrupt: consumer == "ui").ConfigureAwait(false));
            await RunManifestTestAssertions.ResponseAsync(recovered, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            using var restore = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await restore.OpenAsync().ConfigureAwait(false);
            using var update = restore.CreateCommand();
            update.CommandText = "UPDATE processing_outputs SET descriptor_json = $json WHERE output_identity_sha256 = $identity;";
            update.Parameters.AddWithValue("$identity", identity);
            update.Parameters.AddWithValue("$json", original);
            Assert.AreEqual(1, await update.ExecuteNonQueryAsync().ConfigureAwait(false));
            await RunManifestTestAssertions.ResponseAsync(recovered, id).ConfigureAwait(false);
        }
        finally { Cleanup(root); }
    }
}
