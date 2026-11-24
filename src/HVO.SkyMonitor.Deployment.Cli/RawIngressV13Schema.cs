using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment;

// Frozen schema identity for the one supported in-place migration. This is deliberately independent of
// CameraAgent.Common: the deployment CLI must not load the runtime whose state it is evaluating.
internal static class RawIngressV13Schema
{
    internal const string MigrationContract = "canonical-v13-to-v14";
    internal const string RecoveryMigrationContract = "canonical-v13-v14-to-v15";
    internal const string EnvironmentalFenceMigrationContract = "canonical-v13-v14-v15-to-v16";
    internal const string CenteredExposureMigrationContract = "canonical-v13-v14-v15-v16-to-v17";
    internal const string V17Fingerprint = "feb4ed1f705a458d3915c14d460ab9ff00e51fbe9888e499d2adfad57d74745f";
    internal const string Fingerprint = "937cceed13de8f13bf9263d7ece48d21e26388836551c78ccbd27d86aea638ce";
    // Frozen from the candidate v14 journal, not derived from the database under inspection.
    internal const string V14Fingerprint = "66046ff0772ff0ad91509efee1a343a3a73d36fc1e4d196ac3e2db2e66e29216";
    // v15 changes durable reader semantics, not the canonical table definitions.
    internal const string V15Fingerprint = V14Fingerprint;
    // Raw 16 plus the 48 witnessed processing objects from the separately released astrometry beta.
    private const string V16WithLegacyAstrometryFingerprint = "194cba35fbe2eb39b8ecf193efa876b589f9c17f98f6a6cf5b24c4514a5d2f35";
    private static readonly HashSet<string> SharedObjects = new(StringComparer.Ordinal)
    {
        "capture_processing_schema", "processing_nodes", "processing_node_inputs", "processing_outputs",
        "processing_output_sources", "processing_lifecycle_operations", "processing_reconciliation_state",
        "processing_output_diagnostics", "processing_graph_revisions", "processing_graph_registry_state",
        "processing_graph_commands", "processing_graph_delivery_proposals", "processing_graph_delivery_facts",
        "processing_executions", "processing_execution_nodes", "processing_node_attempts",
        "processing_execution_inputs", "processing_execution_input_pins", "processing_execution_output_input_pins",
        "processing_execution_outputs", "processing_execution_manifest_counts", "processing_replay_work", "ix_processing_outputs_capture_node",
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
        "processing_centered_view_bases", "ix_processing_centered_view_live_bases",
        "ix_processing_centered_view_reference", "ix_processing_centered_view_single",
        "ix_processing_centered_view_capture", "processing_centered_view_members",
        "processing_centered_view_changes", "ix_processing_centered_view_change_stream",
        "processing_centered_view_revisions", "ix_processing_centered_view_revision_order",
        "processing_centered_view_intervals", "ix_processing_centered_view_current",
        "ix_processing_centered_view_history", "processing_centered_view_selections", "ix_processing_centered_view_current_stream",
        "ix_processing_centered_view_changes_base", "ix_processing_centered_view_members_base",
        "ix_processing_centered_view_members_order", "ix_processing_centered_view_intervals_end",
        "ix_processing_centered_view_bases_parent", "ix_processing_centered_view_selections_revision",
        "ix_processing_centered_view_archive_reference", "processing_centered_view_cursors",
        "processing_centered_view_retry", "ix_processing_centered_view_retry_due",
        "processing_centered_view_window_work", "ix_processing_centered_view_window_live_base",
        "processing_centered_view_trail_work", "ix_processing_centered_view_trail_due", "ix_processing_centered_view_trail_capture",
        "processing_centered_windows", "ix_processing_centered_windows_pending", "ix_processing_centered_windows_reference",
        "processing_centered_attempt_calendar", "processing_centered_product_clock", "processing_centered_window_holds",
        "ix_processing_centered_product_clock_pending",
        "processing_centered_output_availability", "ix_processing_centered_output_availability_cutoff",
        "ix_processing_centered_output_availability_pending",
        "transient_worker_frames", "ix_transient_worker_frames_ready", "transient_worker_candidates",
        "ix_transient_worker_candidates_pending"
    };

    internal static bool IsCanonical(SqliteConnection connection)
        => string.Equals(ComputeFingerprint(connection, ProcessingObjects.RejectAstrometryIndex), Fingerprint, StringComparison.Ordinal);

    internal static bool IsCanonicalV14(SqliteConnection connection)
        => string.Equals(ComputeFingerprint(connection, ProcessingObjects.RejectAstrometryIndex), V14Fingerprint, StringComparison.Ordinal);

    internal static bool IsCanonicalV16(SqliteConnection connection)
        => IsCanonicalV14(connection) ||
            (string.Equals(ComputeFingerprint(connection, ProcessingObjects.Include), V16WithLegacyAstrometryFingerprint, StringComparison.Ordinal) &&
             HasLegacyAstrometryMarker(connection));

    internal static bool IsCanonicalV17(SqliteConnection connection)
        => string.Equals(ComputeFingerprint(connection, ProcessingObjects.IgnoreWithAstrometryIndex), V17Fingerprint, StringComparison.Ordinal);

    internal static string ComputeFingerprint(SqliteConnection connection)
        => ComputeFingerprint(connection, ProcessingObjects.Ignore);

    private static bool HasLegacyAstrometryMarker(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM capture_processing_schema WHERE schema_key = 1;";
        return command.ExecuteScalar() is 10L;
    }

    private static string ComputeFingerprint(SqliteConnection connection, ProcessingObjects processing)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;";
        using var reader = command.ExecuteReader();
        var definition = new StringBuilder();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            // Include processing only when identifying the historical v16 migration source.
            if (SharedObjects.Contains(name) && (processing != ProcessingObjects.Include ||
                name.StartsWith("transient_", StringComparison.Ordinal) || name.StartsWith("ix_transient_", StringComparison.Ordinal)))
            {
                continue;
            }
            if (name == LegacyAstrometryJournalSchema.LookupIndexName &&
                (processing == ProcessingObjects.Ignore ||
                 (processing == ProcessingObjects.IgnoreWithAstrometryIndex && reader.GetString(0) == "index" &&
                  reader.GetString(2) == "processing_outputs" && reader.GetString(3) == LegacyAstrometryJournalSchema.LookupIndexDefinition)))
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

    private enum ProcessingObjects
    {
        Ignore,
        RejectAstrometryIndex,
        Include,
        IgnoreWithAstrometryIndex
    }
}
