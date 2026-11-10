using HVO.SkyMonitor.AgentCore;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow("normalization")]
    [DataRow("json")]
    [DataRow("nodes")]
    [DataRow("counts")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only fixed fault-injection SQL literals are selected; no test data is interpolated.")]
    public async Task RunManifestReportsCorruptFrozenPlanWithoutClaimingItExceedsBounds(string corruption)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-corrupt");
        try
        {
            using var provider = CreateProvider(root);
            var id = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = corruption switch
            {
                "normalization" => "UPDATE processing_executions SET frozen_plan_json = CAST(json_set(CAST(frozen_plan_json AS TEXT), '$.normalizationPlans', json('{}')) AS BLOB);",
                "counts" => "UPDATE processing_executions SET frozen_plan_json = CAST(json_set(CAST(frozen_plan_json AS TEXT), '$.committedOutputCounts', json('{\"preview\":-1}')) AS BLOB);",
                "nodes" => "UPDATE processing_executions SET frozen_plan_json = CAST(json_remove(CAST(frozen_plan_json AS TEXT), '$.nodes') AS BLOB);",
                _ => "UPDATE processing_executions SET frozen_plan_json = CAST('private-sentinel invalid JSON' AS BLOB);"
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            var body = await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            Assert.IsFalse(body.Contains("private-sentinel", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyRunManifestReadPreservesFrozenEvidenceAndDoesNotInventPrunedOutputHistory(bool pruned)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-upgrade");
        try
        {
            Guid id;
            byte[] originalPayload;
            byte[] frozenPlan;
            byte[] frozenGraph;
            using (var provider = CreateProvider(root))
            {
                id = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var detail = await operations.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(detail);
                originalPayload = CameraAgentRunManifest.Create(detail).Payload!;
                if (pruned)
                {
                    var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                    var output = detail.Nodes.Single().Outputs.Single();
                    var target = await store.ReadOutputByArtifactIdAsync(output.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(target);
                    var deletion = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", target.OutputIdentitySha256,
                        target.PayloadRelativePath, target.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}", "retention-expired", 0);
                    await store.PlanLifecycleOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                    await new DerivedProductReconciler(root, store).ResumeOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                }
            }
            SqliteConnection.ClearAllPools();
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var read = connection.CreateCommand();
                read.CommandText = "SELECT frozen_plan_json, (SELECT definition_json FROM processing_graph_revisions WHERE revision_id = graph_revision_id) FROM processing_executions WHERE execution_id = $id;";
                read.Parameters.AddWithValue("$id", id.ToString("N"));
                using (var reader = await read.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
                    frozenPlan = (byte[])reader[0];
                    frozenGraph = (byte[])reader[1];
                }
                using var legacy = connection.CreateCommand();
                legacy.CommandText = "UPDATE processing_executions SET frozen_plan_json = CAST(json_remove(CAST(frozen_plan_json AS TEXT), '$.committedOutputCounts') AS BLOB);";
                await legacy.ExecuteNonQueryAsync().ConfigureAwait(false);
                using var legacyReader = await read.ExecuteReaderAsync().ConfigureAwait(false);
                Assert.IsTrue(await legacyReader.ReadAsync().ConfigureAwait(false));
                frozenPlan = (byte[])legacyReader[0];
            }
            using (var restarted = CreateProvider(root))
            {
                var operations = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var detail = await operations.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(detail);
                Assert.HasCount(1, detail.Nodes.Single().Attempts);
                Assert.AreEqual(pruned ? null : (int?)1, detail.Nodes.Single().CommittedOutputCount);
                if (pruned)
                    await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.UnrecordedOutputReason).ConfigureAwait(false);
                else
                {
                    var body = await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
                    CollectionAssert.AreEqual(originalPayload, System.Text.Encoding.UTF8.GetBytes(body));
                }
            }
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var read = connection.CreateCommand();
                read.CommandText = "SELECT frozen_plan_json, (SELECT definition_json FROM processing_graph_revisions WHERE revision_id = graph_revision_id), (SELECT version FROM capture_processing_schema) FROM processing_executions WHERE execution_id = $id;";
                read.Parameters.AddWithValue("$id", id.ToString("N"));
                using var reader = await read.ExecuteReaderAsync().ConfigureAwait(false);
                Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
                CollectionAssert.AreEqual(frozenPlan, (byte[])reader[0]);
                CollectionAssert.AreEqual(frozenGraph, (byte[])reader[1]);
                Assert.AreEqual(8L, reader.GetInt64(2));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task RunManifestResolvesRetainedAuxiliaryProcessingSourceByImmutableIdentity()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-auxiliary");
        try
        {
            using var provider = CreateProvider(root);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var earlierId = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            var earlier = (await operations.ReadRunManifestAsync(earlierId, CancellationToken.None).ConfigureAwait(false))!;
            var reference = earlier.Nodes.Single().Outputs.Single();
            var currentId = await CompleteManifestPreviewAsync(provider, sequence: 1).ConfigureAwait(false);
            var current = (await operations.ReadRunManifestAsync(currentId, CancellationToken.None).ConfigureAwait(false))!;
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var lineage = connection.CreateCommand();
                // Emulate an auxiliary consumer's committed lineage without altering the frozen
                // graph or substituting a mutable cache. Both source and consumer are real outputs.
                lineage.CommandText = """
                    INSERT INTO processing_output_sources(output_identity_sha256, source_ordinal, source_artifact_id)
                    VALUES ($output, 1, $source);
                    UPDATE processing_outputs
                    SET descriptor_json = CAST(json_insert(CAST(descriptor_json AS TEXT), CASE WHEN json_type(CAST(descriptor_json AS TEXT), '$.descriptor.artifact') = 'object'
                        THEN '$.descriptor.artifact.sourceArtifactIds[#]' ELSE '$.artifact.sourceArtifactIds[#]' END, $sourceJson) AS BLOB)
                    WHERE output_identity_sha256 = $output;
                    """;
                lineage.Parameters.AddWithValue("$output", current.Nodes.Single().Outputs.Single().OutputIdentitySha256);
                lineage.Parameters.AddWithValue("$source", reference.ArtifactId.ToString("N"));
                lineage.Parameters.AddWithValue("$sourceJson", reference.ArtifactId.ToString("D"));
                await lineage.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var response = await RunManifestTestAssertions.ResponseAsync(operations, currentId).ConfigureAwait(false);
            StringAssert.Contains(response, reference.ArtifactId.ToString("D"), StringComparison.OrdinalIgnoreCase);
            using var manifest = JsonDocument.Parse(response);
            var auxiliary = manifest.RootElement.GetProperty("nodes")[0].GetProperty("inputs").EnumerateArray()
                .Single(input => input.GetProperty("artifactId").GetGuid() == reference.ArtifactId);
            Assert.AreEqual("AuxiliaryProcessingOutput", auxiliary.GetProperty("kind").GetString());
            Assert.AreEqual(JsonValueKind.Null, auxiliary.GetProperty("windowPosition").ValueKind);
            Assert.AreEqual(earlier.Execution.CaptureId, auxiliary.GetProperty("captureId").GetGuid());
            Assert.AreEqual(reference.OutputIdentitySha256, auxiliary.GetProperty("outputIdentitySha256").GetString());
            Assert.AreEqual(reference.PayloadSha256, auxiliary.GetProperty("payloadSha256").GetString());
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var target = (await store.ReadOutputByArtifactIdAsync(reference.ArtifactId, CancellationToken.None).ConfigureAwait(false))!;
            var deletion = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", target.OutputIdentitySha256,
                target.PayloadRelativePath, target.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}", "retention-expired", 0);
            await store.PlanLifecycleOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
            await new DerivedProductReconciler(root, store).ResumeOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
            await RunManifestTestAssertions.ResponseAsync(operations, currentId, CameraAgentRunManifest.ArtifactEvidenceUnavailableReason).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CommittedOutputCountsPreserveFrozenPlanMembersAndSchemaAcrossCompletion()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-counts");
        try
        {
            using var provider = CreateProvider(root);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = CreateConfiguration() with
            {
                Pipeline = new([new("Preview", "preview", DependsOn: ["$raw"]), new("Telemetry", "telemetry", DependsOn: ["preview"])],
                    CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var receipt = await ingress.AcceptAsync(config, CreateSubmission(), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
            var before = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
            await CompleteLiveAsync(provider, config).ConfigureAwait(false);
            var after = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
            using var original = JsonDocument.Parse(before);
            using var completed = JsonDocument.Parse(after);
            foreach (var member in original.RootElement.EnumerateObject())
                Assert.AreEqual(member.Value.GetRawText(), completed.RootElement.GetProperty(member.Name).GetRawText(), member.Name);
            var counts = completed.RootElement.GetProperty("committedOutputCounts");
            Assert.AreEqual(1, counts.GetProperty("preview").GetInt32());
            Assert.AreEqual(0, counts.GetProperty("telemetry").GetInt32());
            await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<Guid> CompleteManifestPreviewAsync(ServiceProvider provider, int sequence = 0)
    {
        var config = CreateConfiguration() with
        {
            Pipeline = new([new("Preview", "preview", DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
        var receipt = await ingress.AcceptAsync(config, CreateSubmission(sequence), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        await CompleteLiveAsync(provider, config).ConfigureAwait(false);
        return (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
    }
}
