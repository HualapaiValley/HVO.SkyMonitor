using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task RunManifestPreservesFrozenEvidenceChecksumsAndLineageAcrossRestart()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-run-manifest");
        try
        {
            Guid executionId;
            byte[] before;
            using (var provider = CreateProvider(root))
            {
                var configuration = CreateConfiguration() with
                {
                    Pipeline = new CapturePipelineConfig([new("Preview", "preview", DependsOn: ["$raw"])],
                        CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
                };
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var receipt = await ingress.AcceptAsync(configuration, CreateSubmission(), CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
                executionId = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
                var detail = await operations.ReadRunManifestAsync(executionId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(detail);
                Assert.IsTrue(detail.Nodes.SelectMany(node => node.Outputs).Any());
                foreach (var output in detail.Nodes.SelectMany(node => node.Outputs))
                {
                    Assert.AreEqual(64, output.PayloadSha256?.Length);
                    Assert.IsNotEmpty(output.SourceArtifactIds);
                }
                Assert.IsTrue(detail.Nodes.SelectMany(node => node.Inputs).All(input => input.PayloadSha256?.Length == 64));
                var result = CameraAgentRunManifest.Create(detail);
                Assert.IsNull(result.Reason);
                before = result.Payload!;
                // Retiring payload availability must not erase retained checksum/lineage evidence.
                using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE processing_outputs SET availability_state = 'Missing', availability_reason = 'private-sentinel';";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using (var restarted = CreateProvider(root))
            {
                var detail = await restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>()
                    .ReadRunManifestAsync(executionId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(detail);
                var after = CameraAgentRunManifest.Create(detail);
                Assert.IsNull(after.Reason);
                CollectionAssert.AreEqual(before, after.Payload);
                using var json = JsonDocument.Parse(after.Payload!);
                Assert.AreEqual(executionId, json.RootElement.GetProperty("executionId").GetGuid());
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All three fault-injection queries are fixed literals; the test parameter selects one without entering SQL.")]
    [DataRow("bytes")]
    [DataRow("nodes")]
    [DataRow("attempts")]
    public async Task RunManifestRejectsOversizedSnapshotBeforeReadingDescriptors(string bound)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-run-manifest-bounds");
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig([new("Preview", "preview", DependsOn: ["$raw"])],
                        CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var receipt = await ingress.AcceptAsync(configuration, CreateSubmission(), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
            var executionId = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = bound switch
            {
                "nodes" => """
                    WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<65)
                    INSERT INTO processing_execution_nodes(execution_id,node_id,required,plan_sha256,shared_plan_node_identity_sha256,dependencies_json,inputs_json,outputs_json,status)
                    SELECT e.execution_id,'extra-' || n.i,1,e.local_plan_identity_sha256,e.shared_plan_identity_sha256,'[]','[]','[]','Pending'
                    FROM processing_executions e CROSS JOIN n;
                    """,
                "attempts" => """
                    WITH RECURSIVE n(i) AS (VALUES(100) UNION ALL SELECT i+1 FROM n WHERE i<4196)
                    INSERT INTO processing_node_attempts(execution_id,node_id,attempt_number,lease_owner,lease_token,started_unix_ms,status)
                    SELECT e.execution_id,e.node_id,n.i,'test','test',0,'Running'
                    FROM (SELECT execution_id,node_id FROM processing_execution_nodes LIMIT 1) e CROSS JOIN n;
                    """,
                _ => "UPDATE processing_outputs SET descriptor_json = zeroblob(2097153);"
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await operations.ReadRunManifestAsync(executionId, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(ProcessingRunManifestBounds.UnavailableReason, exception.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
