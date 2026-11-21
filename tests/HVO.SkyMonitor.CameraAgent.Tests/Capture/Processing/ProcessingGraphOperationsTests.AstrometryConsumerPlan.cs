using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private static async Task AssertSupplementaryStoragePlanAsync(
        ProcessingGraphOperationsCoordinator operations, ProcessingGraphRevisionSnapshot revision,
        Guid captureId, CancellationToken token)
    {
        var executionId = await operations.ReadLiveExecutionIdAsync(captureId, token).ConfigureAwait(false);
        Assert.IsNotNull(executionId);
        var detail = await operations.ReadExecutionDetailAsync(executionId.Value, token).ConfigureAwait(false);
        Assert.IsNotNull(detail);
        Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, detail.Execution.Status);
        Assert.AreEqual(revision.State.RevisionId, detail.Execution.GraphRevisionId);
        var archive = detail.Nodes.Single(node => node.NodeId == "archive");
        var fit = detail.Nodes.Single(node => node.NodeId == "fit");
        Assert.AreEqual("Completed", archive.Status);
        Assert.AreEqual("astrometry.capture-cadence", fit.Reason);
        Assert.IsEmpty(fit.Outputs);
        var fitDependency = archive.Dependencies.Single(dependency => dependency.ProducerId == "fit");
        Console.WriteLine($"Sequence 2 reopened execution {executionId} archived with no fit, stored fit Required={fitDependency.Required}.");
        Assert.IsFalse(fitDependency.Required, "A completed Storage run without a fit must record it as supplementary.");
        Assert.IsTrue(archive.Dependencies.Single(dependency => dependency.ProducerId == "annotation").Required);
        Assert.IsTrue(archive.Dependencies.Single(dependency => dependency.ProducerId == "projected-scene").Required);
        var telemetry = detail.Nodes.SingleOrDefault(node => node.NodeId == "telemetry");
        if (telemetry is not null)
        {
            Assert.AreEqual("Completed", telemetry.Status);
            var outcome = telemetry.Dependencies.Single(dependency => dependency.ProducerId == "fit");
            Assert.AreEqual(ProcessingGraphDependencyKind.Outcome, outcome.Kind);
            Assert.IsFalse(outcome.Required);
            Assert.IsTrue(telemetry.Dependencies.Single(dependency => dependency.ProducerId == "archive").Required);
        }

        using var definition = JsonDocument.Parse(revision.DefinitionJson);
        var node = definition.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "archive");
        Assert.AreEqual(ProcessingGraphDependencyKind.Artifact, fitDependency.Kind);
        Assert.IsFalse(node.GetProperty("dependencies").EnumerateArray()
            .Single(item => item.GetProperty("producerId").GetString() == "fit").GetProperty("required").GetBoolean());
        var fitVariants = fit.OutputContracts.Select(output => output.Variant).ToHashSet(StringComparer.Ordinal);
        Assert.HasCount(2, fitVariants);
        var selectors = node.GetProperty("inputs").EnumerateArray().ToArray();
        Assert.HasCount(4, selectors);
        foreach (var selector in selectors)
        {
            var variant = selector.GetProperty("variants")[0].GetString()!;
            Assert.AreEqual(!fitVariants.Contains(variant), selector.GetProperty("required").GetBoolean(), variant);
        }
    }

    private static async Task AssertHistoricalAstrometryReplayAsync(
        ServiceProvider host, ProcessingGraphRevisionSnapshot expected, Guid captureId, Guid rawId,
        DurableProcessingOutput retainedFit, CancellationToken token)
    {
        var operations = host.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var store = host.GetRequiredService<SqliteCaptureProcessingStore>();
        var original = await store.ReadRevisionAsync(expected.State.RevisionId, token).ConfigureAwait(false);
        Assert.IsTrue(SqliteCaptureProcessingStore.HasSameRevisionContent(original, expected));

        var liveId = await operations.ReadLiveExecutionIdAsync(captureId, token).ConfigureAwait(false);
        Assert.IsNotNull(liveId);
        var historical = await operations.ReadExecutionDetailAsync(liveId.Value, token).ConfigureAwait(false);
        Assert.IsNotNull(historical);
        Assert.AreEqual(expected.State.RevisionId, historical.Execution.GraphRevisionId);
        Assert.AreEqual(expected.State.LocalPlanIdentitySha256, historical.Execution.LocalPlanIdentitySha256);
        Assert.IsTrue(historical.Nodes.Single(node => node.NodeId == "archive").Dependencies
            .Single(dependency => dependency.ProducerId == "fit").Required);
        Assert.IsTrue(historical.Nodes.Single(node => node.NodeId == "telemetry").Dependencies
            .Single(dependency => dependency.ProducerId == "fit").Required);

        var request = new ProcessingReplaySubmission(captureId, expected.State.RevisionId, rawId, Reason: "historical fit consumer");
        var replay = await ExecuteReplayAsync(host, request, "replay-historical-fit-consumer").ConfigureAwait(false);
        Assert.AreEqual(expected.State.RevisionId, replay.Execution.GraphRevisionId);
        Assert.AreEqual(expected.State.SharedPlanIdentitySha256, replay.Execution.SharedPlanIdentitySha256);
        Assert.AreEqual(expected.State.LocalPlanIdentitySha256, replay.Execution.LocalPlanIdentitySha256);
        var actualFit = replay.Nodes.Single(node => node.NodeId == "fit").Outputs
            .Single(output => output.Variant == retainedFit.Artifact.Variant);
        Assert.AreEqual(retainedFit.ArtifactId, actualFit.ArtifactId);
        Assert.AreEqual(retainedFit.OutputIdentitySha256, actualFit.OutputIdentitySha256);
        Assert.AreEqual(retainedFit.Artifact.ChecksumSha256, actualFit.PayloadSha256);
        var retry = await operations.SubmitReplayAsync(request, "replay-historical-fit-consumer", "equivalence-test", token)
            .ConfigureAwait(false);
        Assert.IsTrue(retry.Replayed);
        Assert.AreEqual(replay.Execution.ExecutionId, retry.Execution.ExecutionId);
        var afterReplay = await store.ReadRevisionAsync(expected.State.RevisionId, token).ConfigureAwait(false);
        Assert.IsTrue(SqliteCaptureProcessingStore.HasSameRevisionContent(afterReplay, expected));
        Console.WriteLine($"Historical revision {expected.State.RevisionId} retained all plan bytes and fit/replay identities.");
    }

    private static async Task UsePreviousAstrometryLookupSchemaAsync(string root, CancellationToken token)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        // The schema-10 layout added this index and advanced the processing marker.
        // Restore the predecessor layout in the disposable fixture without modifying any plan or evidence rows.
        command.CommandText = """
            BEGIN IMMEDIATE;
            DROP INDEX ix_processing_outputs_astrometry_lookup;
            DROP TABLE capture_processing_schema;
            CREATE TABLE capture_processing_schema(
                schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
                version INTEGER NOT NULL CHECK(version = 9)
            ) STRICT;
            INSERT INTO capture_processing_schema(schema_key, version) VALUES (1, 9);
            COMMIT;
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    // Feed the production journal an independently compiled predecessor definition.
    private sealed class HistoricalAstrometryPlanFactory(CaptureProcessingPipelineFactory inner) : ICaptureProcessingPipelineFactory
    {
        public IReadOnlyList<string> StableStepAliases => inner.StableStepAliases;

        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => FreezePriorContract(inner.CreateGraph(config));
        public CaptureProcessingGraph CreateActiveProfileGraph(CameraModuleConfig config) => FreezePriorContract(inner.CreateActiveProfileGraph(config));
        public CaptureProcessingGraph CreateRetainedGraph(CameraModuleConfig config) => FreezePriorContract(inner.CreateRetainedGraph(config));
        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config) => inner.PreviewPlan(config);

        private static CaptureProcessingGraph FreezePriorContract(CaptureProcessingGraph graph)
        {
            var definition = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(graph.SharedPlan!.CanonicalDefinition.GetRawText())).Definition!;
            var historical = definition with
            {
                Nodes = definition.Nodes.Select(node => node.Id is "archive" or "telemetry"
                    ? new ProcessingGraphNodeDefinition(node.Id, node.StepAlias, node.StepVersion, node.OperationKind,
                        node.Enabled, node.FailurePolicy, node.Order, node.EffectiveOptions,
                        node.Dependencies.Select(dependency => dependency with { Required = true }).ToImmutableArray(),
                        node.Inputs.Select(input => input with { Required = true }).ToImmutableArray(),
                        node.Outputs, node.Window, node.CapabilityLabels, node.HostApplicability)
                    : node).ToImmutableArray()
            };
            var previous = ProcessingGraphCompiler.Compile(historical, new(ProcessingGraphHosts.CameraAgent, []));
            Assert.IsTrue(previous.IsValid);
            var identities = previous.Plan!.Nodes.ToDictionary(node => node.Definition.Id, node => node.IdentitySha256, StringComparer.Ordinal);
            return new CaptureProcessingGraph(graph.Nodes.Select(node => node with
            {
                SharedPlanNodeIdentitySha256 = identities[node.Id]
            }).ToArray(), previous.Plan);
        }
    }
}
