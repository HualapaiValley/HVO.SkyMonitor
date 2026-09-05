using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record ProcessingFrozenOutputInput(
    string OutputIdentitySha256,
    int WindowPosition,
    string PayloadRelativePath);

internal static class ProcessingOutputWindowSelector
{
    internal static string? ReadFirstProducerId(string dependenciesJson)
    {
        using var document = JsonDocument.Parse(dependenciesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            return null;
        var dependency = document.RootElement[0];
        return dependency.TryGetProperty("producerId", out var producer) && producer.ValueKind == JsonValueKind.String
            ? producer.GetString()
            : null;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The selected query is one of two fixed internal statements and all values remain parameterized.")]
    internal static async ValueTask<IReadOnlyList<ProcessingFrozenOutputInput>> SelectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ReconstructionDescriptor current,
        string sourceNodeId,
        string graphRevisionId,
        string sourcePlanSha256,
        string inputsJson,
        ProcessingGraphWindowRequirement requirement,
        int maximumAllowedInputs,
        bool includeUnpublishedRevisionOutputs,
        CancellationToken cancellationToken)
    {
        var maximumHistory = Math.Max(0, Math.Min(requirement.MaximumInputCount, maximumAllowedInputs) - 1);
        var minimumHistory = Math.Max(0, requirement.MinimumInputCount - 1);
        if (maximumHistory == 0) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = requirement.Kind == ProcessingGraphWindowKind.Trailing
            ? """
              SELECT DISTINCT output.output_identity_sha256, output.artifact_id,
                     output.payload_relative_path, output.sidecar_relative_path, output.descriptor_json,
                     output.capture_id, output.agent_id, output.node_id, output.role, output.variant,
                     output.recipe_identity_sha256, output.algorithms_json, output.compatibility_json,
                     output.total_integration_ticks, output.capture_sequence, output.product_kind,
                     output.product_schema_version, output.content_identity_sha256,
                     output.availability_state, output.availability_reason, output.frame_artifact_recipe_version
              FROM processing_outputs output
              LEFT JOIN processing_execution_outputs association
                ON association.output_identity_sha256 = output.output_identity_sha256
              LEFT JOIN processing_executions execution ON execution.execution_id = association.execution_id
              LEFT JOIN processing_nodes legacy ON legacy.capture_id = output.capture_id
                                                   AND legacy.node_id = output.node_id
               WHERE output.agent_id = $agent
                 AND (association.node_id = $node
                      OR (output.node_id = $node
                          AND legacy.status = 'Completed' AND legacy.plan_sha256 = $source_plan))
                 AND output.capture_sequence < $sequence AND output.availability_state = 'Available'
                 AND (($include_unpublished = 1
                       AND execution.graph_revision_id = $revision AND execution.status = 'Completed')
                      OR (association.published_flag = 1
                          AND ((execution.graph_revision_id = $revision AND execution.status = 'Completed')
                               OR (legacy.status = 'Completed' AND legacy.plan_sha256 = $source_plan)))
                      OR (association.output_identity_sha256 IS NULL
                          AND legacy.status = 'Completed' AND legacy.plan_sha256 = $source_plan))
              ORDER BY output.capture_sequence DESC, output.output_identity_sha256 DESC
              LIMIT $candidates;
              """
            : """
              SELECT DISTINCT output.output_identity_sha256, output.artifact_id,
                     output.payload_relative_path, output.sidecar_relative_path, output.descriptor_json,
                     output.capture_id, output.agent_id, output.node_id, output.role, output.variant,
                     output.recipe_identity_sha256, output.algorithms_json, output.compatibility_json,
                     output.total_integration_ticks, output.capture_sequence, output.product_kind,
                     output.product_schema_version, output.content_identity_sha256,
                     output.availability_state, output.availability_reason, output.frame_artifact_recipe_version
              FROM processing_outputs output
              LEFT JOIN processing_execution_outputs association
                ON association.output_identity_sha256 = output.output_identity_sha256
              LEFT JOIN processing_executions execution ON execution.execution_id = association.execution_id
              LEFT JOIN processing_nodes legacy ON legacy.capture_id = output.capture_id
                                                   AND legacy.node_id = output.node_id
               WHERE output.agent_id = $agent
                 AND (association.node_id = $node
                      OR (output.node_id = $node
                          AND legacy.status = 'Completed' AND legacy.plan_sha256 = $source_plan))
                 AND output.capture_sequence != $sequence AND output.availability_state = 'Available'
                 AND (($include_unpublished = 1
                       AND execution.graph_revision_id = $revision AND execution.status = 'Completed')
                      OR (association.published_flag = 1
                          AND ((execution.graph_revision_id = $revision AND execution.status = 'Completed')
                               OR (legacy.status = 'Completed' AND legacy.plan_sha256 = $source_plan)))
                      OR (association.output_identity_sha256 IS NULL
                          AND legacy.status = 'Completed' AND legacy.plan_sha256 = $source_plan))
              ORDER BY ABS(output.capture_sequence - $sequence), output.capture_sequence
              LIMIT $candidates;
              """;
        command.Parameters.AddWithValue("$agent", current.Capture.AgentId);
        command.Parameters.AddWithValue("$node", sourceNodeId);
        command.Parameters.AddWithValue("$sequence", current.Capture.CaptureSequence);
        command.Parameters.AddWithValue("$revision", graphRevisionId);
        command.Parameters.AddWithValue("$source_plan", sourcePlanSha256);
        command.Parameters.AddWithValue("$include_unpublished", includeUnpublishedRevisionOutputs ? 1 : 0);
        command.Parameters.AddWithValue("$candidates", Math.Min(512, Math.Max(maximumHistory, maximumAllowedInputs * 4)));
        var contracts = JsonSerializer.Deserialize<ProcessingGraphInputContract[]>(inputsJson, SerializerOptions)
            ?? throw new InvalidDataException("The derived processing window input contract is invalid.");
        // A derived window is compared against this capture's own output from the same producer, not against
        // the raw capture: a producer such as calibration deliberately changes the calibration and mask axes,
        // so comparing a calibrated candidate with the raw identity would reject every earlier capture.
        var expectedCompatibility = await ReadProducerCompatibilityAsync(
                connection, transaction, current, sourceNodeId, contracts, cancellationToken).ConfigureAwait(false)
            ?? CameraAgentRecipeExecutionAdapter.CreateCompatibility(current);
        var selected = (await SqliteCaptureProcessingStore.ReadOutputRowsAsync(command, cancellationToken).ConfigureAwait(false))
            .Select(static row => row.Output)
            .Where(output => output.Compatibility == expectedCompatibility &&
                             contracts.Any(contract => Matches(contract, output)))
            .Take(maximumHistory)
            .Select(static output => (output.OutputIdentitySha256, output.CaptureSequence, output.PayloadRelativePath))
            .ToList();
        if (selected.Count < minimumHistory)
            throw new ProcessingGraphStoreConflictException("The archived derived processing window is incomplete.");
        selected.Sort(static (left, right) => left.CaptureSequence.CompareTo(right.CaptureSequence));
        var priorCount = selected.Count(value => value.CaptureSequence < current.Capture.CaptureSequence);
        var result = selected.Select((value, index) => new ProcessingFrozenOutputInput(
            value.OutputIdentitySha256,
            value.CaptureSequence < current.Capture.CaptureSequence ? index - priorCount : index - priorCount + 1,
            value.PayloadRelativePath)).ToArray();
        if (!requirement.RequiredPositions.IsDefaultOrEmpty && requirement.RequiredPositions
                .Where(static position => position != 0)
                .Any(position => result.All(input => input.WindowPosition != position)))
        {
            throw new ProcessingGraphStoreConflictException("The archived derived processing window is missing a required position.");
        }
        return result;
    }

    private static async ValueTask<ProcessingCompatibilityIdentity?> ReadProducerCompatibilityAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ReconstructionDescriptor current,
        string sourceNodeId,
        IReadOnlyList<ProcessingGraphInputContract> contracts,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT output.output_identity_sha256, output.artifact_id, output.payload_relative_path,
                   output.sidecar_relative_path, output.descriptor_json, output.capture_id,
                   output.agent_id, output.node_id, output.role, output.variant,
                   output.recipe_identity_sha256, output.algorithms_json, output.compatibility_json,
                   output.total_integration_ticks, output.capture_sequence, output.product_kind,
                   output.product_schema_version, output.content_identity_sha256,
                   output.availability_state, output.availability_reason,
                   output.frame_artifact_recipe_version
            FROM processing_outputs output
            WHERE output.capture_id = $capture AND output.node_id = $node
              AND output.availability_state = 'Available';
            """;
        command.Parameters.AddWithValue("$capture", current.Capture.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$node", sourceNodeId);
        return (await SqliteCaptureProcessingStore.ReadOutputRowsAsync(command, cancellationToken).ConfigureAwait(false))
            .Select(static row => row.Output)
            .FirstOrDefault(output => contracts.Any(contract => Matches(contract, output)))
            ?.Compatibility;
    }

    private static bool Matches(ProcessingGraphInputContract contract, DurableProcessingOutput output)
    {
        var productKind = output.ProductKind ??
            (output.Artifact.Role == FrameArtifactRole.Metadata ? ProcessingProductKind.Metadata : ProcessingProductKind.PixelData);
        return (contract.Roles.IsDefaultOrEmpty || contract.Roles.Contains(output.Artifact.Role)) &&
               (contract.ProductKinds.IsDefaultOrEmpty || contract.ProductKinds.Contains(productKind)) &&
               (contract.Variants.IsDefaultOrEmpty || contract.Variants.Contains(output.Artifact.Variant, StringComparer.Ordinal)) &&
               (contract.RecipeNames.IsDefaultOrEmpty || contract.RecipeNames.Contains(output.Artifact.Recipe.Name, StringComparer.Ordinal)) &&
               (contract.SchemaVersions.IsDefaultOrEmpty || output.ProductSchemaVersion is not null &&
                   contract.SchemaVersions.Contains(output.ProductSchemaVersion, StringComparer.Ordinal));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
