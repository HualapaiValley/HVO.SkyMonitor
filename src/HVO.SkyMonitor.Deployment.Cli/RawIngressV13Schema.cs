using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment;

// Frozen schema identity for the one supported in-place migration. This is deliberately independent of
// CameraAgent.Common: the deployment CLI must not load the runtime whose state it is evaluating.
internal static class RawIngressV13Schema
{
    internal const string MigrationContract = "canonical-v13-to-v14";
    internal const string Fingerprint = "937cceed13de8f13bf9263d7ece48d21e26388836551c78ccbd27d86aea638ce";
    // Frozen from the candidate v14 journal, not derived from the database under inspection.
    internal const string V14Fingerprint = "66046ff0772ff0ad91509efee1a343a3a73d36fc1e4d196ac3e2db2e66e29216";
    private static readonly HashSet<string> SharedObjects = new(StringComparer.Ordinal)
    {
        "capture_processing_schema", "processing_nodes", "processing_node_inputs", "processing_outputs",
        "processing_output_sources", "processing_lifecycle_operations", "processing_reconciliation_state",
        "processing_output_diagnostics", "processing_graph_revisions", "processing_graph_registry_state",
        "processing_graph_commands", "processing_graph_delivery_proposals", "processing_graph_delivery_facts",
        "processing_executions", "processing_execution_nodes", "processing_node_attempts",
        "processing_execution_inputs", "processing_execution_input_pins", "processing_execution_output_input_pins",
        "processing_execution_outputs", "processing_replay_work", "ix_processing_outputs_capture_node",
        "ix_processing_outputs_window", "ix_processing_nodes_status", "ix_processing_nodes_recipe",
        "ix_processing_outputs_role", "ix_processing_outputs_recipe", "ix_processing_outputs_product",
        "ix_processing_outputs_retention_available", "ix_processing_outputs_retention_unavailable",
        "ix_processing_output_sources_artifact", "ix_processing_node_inputs_artifact",
        "ix_processing_graph_revisions_active", "ix_processing_graph_revisions_name",
        "ix_processing_graph_delivery_proposals_lookup", "ix_processing_graph_delivery_facts_pending",
        "ix_processing_graph_delivery_facts_lifecycle", "ix_processing_graph_delivery_facts_settlement",
        "ix_processing_executions_live_capture", "ix_processing_executions_status",
        "ix_processing_replay_work_claim", "ix_processing_node_attempts_history",
        "ix_processing_execution_inputs_window", "ix_processing_execution_pins_active",
        "ix_processing_execution_output_pins_active", "ix_processing_execution_outputs_publication",
        "transient_worker_frames", "ix_transient_worker_frames_ready", "transient_worker_candidates",
        "ix_transient_worker_candidates_pending"
    };

    internal static bool IsCanonical(SqliteConnection connection)
        => string.Equals(ComputeFingerprint(connection), Fingerprint, StringComparison.Ordinal);

    internal static bool IsCanonicalV14(SqliteConnection connection)
        => string.Equals(ComputeFingerprint(connection), V14Fingerprint, StringComparison.Ordinal);

    internal static string ComputeFingerprint(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;";
        using var reader = command.ExecuteReader();
        var definition = new StringBuilder();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            // These objects share the journal but are owned by capture processing or transient workers.
            if (SharedObjects.Contains(name))
            {
                continue;
            }
            foreach (var ordinal in new[] { 0, 1, 2, 3 })
            {
                var value = reader.GetString(ordinal);
                definition.Append(value.Length).Append(':').Append(value);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition.ToString())));
    }
}
