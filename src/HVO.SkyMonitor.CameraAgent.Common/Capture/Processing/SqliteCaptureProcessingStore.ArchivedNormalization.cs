using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    /// <summary>
    /// Mixed-gain replay copies the original node's plan and complete raw window, including excluded positions.
    /// Neither selection nor normalization is rerun against today's archive or active calibration family.
    /// </summary>
    private static async ValueTask<ArchivedNormalizationWindow?> ReadArchivedNormalizationWindowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProcessingReplaySource source,
        ProcessingExecutionNodeSeed node,
        CancellationToken cancellationToken)
    {
        if (node.WindowJson is null ||
            JsonSerializer.Deserialize<ProcessingGraphWindowRequirement>(node.WindowJson, ExecutionSerializerOptions)
                is not { } window || window.CompatibilityLabels.IsDefaultOrEmpty ||
            !window.CompatibilityLabels.Contains(GainNormalizationCompatibility.WindowLabel, StringComparer.Ordinal))
        {
            return null;
        }

        var executionId = string.Empty;
        FrozenNormalizationPlan? frozen = null;
        // A matching live node is the capture's original; centered originals are the earliest archived execution.
        // A missing/corrupt plan on that execution is an error, never a reason to use a later execution's plan.
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT execution.execution_id, execution.frozen_plan_json, node.plan_sha256
                FROM processing_executions execution
                JOIN processing_execution_nodes node ON node.execution_id = execution.execution_id
                WHERE execution.capture_id = $capture
                  AND execution.primary_artifact_id = $artifact AND node.node_id = $node
                  AND node.status = 'Completed'
                ORDER BY CASE execution.execution_class WHEN 'Live' THEN 0 ELSE 1 END,
                         execution.accepted_unix_ms, execution.execution_id;
                """;
            command.Parameters.AddWithValue("$capture", source.RawCapture.Manifest.Descriptor.Capture.CaptureId.ToString("N"));
            command.Parameters.AddWithValue("$artifact", source.RawCapture.Manifest.Descriptor.Artifact.ArtifactId.ToString("N"));
            command.Parameters.AddWithValue("$node", node.NodeId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var sawNode = false;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sawNode = true;
                if (!string.Equals(reader.GetString(2), node.PlanSha256, StringComparison.Ordinal))
                    continue;
                executionId = reader.GetString(0);
                try
                {
                    frozen = FindNormalizationPlan((byte[])reader.GetValue(1), node.NodeId)
                        ?? throw new NormalizationPlanException(ProcessingReasonCodes.MissingNormalizationPlan);
                }
                catch (InvalidDataException exception)
                {
                    throw new NormalizationPlanException(ProcessingReasonCodes.InvalidNormalizationPlan, exception);
                }
                break;
            }
            if (frozen is null)
            {
                throw new NormalizationPlanException(sawNode
                    ? ProcessingReasonCodes.NormalizationPlanInputMismatch
                    : ProcessingReasonCodes.MissingNormalizationPlan);
            }
        }

        var inputs = new List<ProcessingFrozenRawInput>(frozen.Plan.Inputs.Count);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT input.input_ordinal, input.window_position, input.capture_id, input.artifact_id,
                       input.descriptor_sha256, input.payload_sha256,
                       raw.raw_capture_row_id, raw.manifest_json, raw.payload_relative_path,
                       raw.manifest_sha256, raw.payload_sha256
                FROM processing_execution_inputs input
                JOIN raw_captures raw ON raw.capture_id = input.capture_id AND raw.raw_artifact_id = input.artifact_id
                WHERE input.execution_id = $execution AND input.node_id = $node
                  AND input.selected_flag = 1 AND raw.state = 'committed'
                ORDER BY input.input_ordinal;
                """;
            command.Parameters.AddWithValue("$execution", executionId);
            command.Parameters.AddWithValue("$node", node.NodeId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (inputs.Count >= frozen.Plan.Inputs.Count || !FrozenInputMatches(frozen.Plan.Inputs[inputs.Count], reader))
                {
                    throw new NormalizationPlanException(ProcessingReasonCodes.NormalizationPlanInputMismatch);
                }
                var manifestJson = (byte[])reader.GetValue(7);
                var parsed = CaptureContractJson.ParseManifest(manifestJson);
                if (!parsed.IsValid || parsed.Document?.Manifest.Descriptor is not { } descriptor ||
                    !string.Equals(CaptureContractJson.ComputeManifestSha256(manifestJson), reader.GetString(9), StringComparison.Ordinal) ||
                    !string.Equals(CaptureContractJson.ComputeDescriptorSha256(descriptor), reader.GetString(4), StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(10), reader.GetString(5), StringComparison.Ordinal))
                {
                    throw new NormalizationPlanException(ProcessingReasonCodes.NormalizationPlanInputMismatch);
                }
                inputs.Add(new ProcessingFrozenRawInput(
                    reader.GetInt64(6), reader.GetInt32(1), descriptor, reader.GetString(4), reader.GetString(5), reader.GetString(8)));
            }
        }
        if (inputs.Count != frozen.Plan.Inputs.Count)
        {
            throw new NormalizationPlanException(ProcessingReasonCodes.NormalizationPlanInputMismatch);
        }
        return new ArchivedNormalizationWindow(frozen, inputs);
    }

    private sealed record ArchivedNormalizationWindow(
        FrozenNormalizationPlan Plan,
        IReadOnlyList<ProcessingFrozenRawInput> Inputs);
}
