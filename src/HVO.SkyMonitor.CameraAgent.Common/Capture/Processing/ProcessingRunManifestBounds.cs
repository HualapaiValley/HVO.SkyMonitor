using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Limits for the operator's single-run evidence export.</summary>
public static class ProcessingRunManifestBounds
{
    public const int MaximumNodes = 64;
    public const int MaximumRows = 4096;
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const string UnavailableReason = "The retained run evidence exceeds the manifest download limits.";
}

internal sealed partial class SqliteCaptureProcessingStore
{
    private static async Task CheckRunManifestBoundsAsync(
        SqliteConnection connection, Guid executionId, CancellationToken cancellationToken)
    {
        // Every scan uses the execution index and stops at limit + 1. Measure serialized columns
        // before deserialization, including descriptors, so a small row count cannot hide huge blobs.
        using var rows = connection.CreateCommand();
        rows.CommandText = """
            SELECT (SELECT COUNT(*) FROM (SELECT 1 FROM processing_execution_nodes WHERE execution_id = $execution LIMIT $nodes)) >= $nodes
                OR (SELECT COUNT(*) FROM (SELECT 1 FROM processing_node_attempts WHERE execution_id = $execution LIMIT $rows)) >= $rows
                OR (SELECT COUNT(*) FROM (SELECT 1 FROM processing_execution_inputs WHERE execution_id = $execution LIMIT $rows)) >= $rows
                OR (SELECT COUNT(*) FROM (SELECT 1 FROM processing_execution_output_input_pins WHERE execution_id = $execution LIMIT $rows)) >= $rows
                OR (SELECT COUNT(*) FROM (SELECT 1 FROM processing_execution_outputs WHERE execution_id = $execution LIMIT $rows)) >= $rows;
            """;
        rows.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        rows.Parameters.AddWithValue("$nodes", ProcessingRunManifestBounds.MaximumNodes + 1);
        rows.Parameters.AddWithValue("$rows", ProcessingRunManifestBounds.MaximumRows + 1);
        if ((long)(await rows.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 0)
            throw new InvalidDataException(ProcessingRunManifestBounds.UnavailableReason);
        using var bytes = connection.CreateCommand();
        bytes.CommandText = """
            SELECT COALESCE((SELECT length(frozen_plan_json) FROM processing_executions WHERE execution_id = $execution), 0)
              + COALESCE((SELECT SUM(length(CAST(dependencies_json AS BLOB)) + length(CAST(inputs_json AS BLOB)) + length(CAST(outputs_json AS BLOB)))
                  FROM processing_execution_nodes WHERE execution_id = $execution), 0)
              + COALESCE((SELECT SUM(length(o.descriptor_json) + length(o.algorithms_json) + length(o.compatibility_json))
                  FROM processing_execution_outputs a JOIN processing_outputs o ON o.output_identity_sha256 = a.output_identity_sha256
                  WHERE a.execution_id = $execution), 0)
              + COALESCE((SELECT SUM(length(o.descriptor_json))
                  FROM processing_execution_output_input_pins p JOIN processing_outputs o ON o.output_identity_sha256 = p.output_identity_sha256
                  WHERE p.execution_id = $execution), 0);
            """;
        bytes.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        if ((long)(await bytes.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > ProcessingRunManifestBounds.MaximumBytes)
            throw new InvalidDataException(ProcessingRunManifestBounds.UnavailableReason);
    }
}
