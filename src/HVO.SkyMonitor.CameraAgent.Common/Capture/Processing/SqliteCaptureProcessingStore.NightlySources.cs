using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    /// <summary>
    /// Selects by the raw exposure-time index and joins outputs by capture, so a window costs its own rows rather than
    /// a scan of the journal. Left to itself the planner drives from <c>ix_processing_outputs_role</c>, which visits
    /// every output of the role, so the indexes and the join order (<c>CROSS JOIN</c>) are pinned.
    /// </summary>
    internal const string PublishedOutputsByExposureSql = """
        WITH candidates AS (
            SELECT output.output_identity_sha256, output.artifact_id, output.payload_relative_path,
                   output.sidecar_relative_path, output.descriptor_json, output.capture_id, output.agent_id,
                   output.node_id, output.role, output.variant, output.recipe_identity_sha256,
                   output.algorithms_json, output.compatibility_json, output.total_integration_ticks,
                   output.capture_sequence, output.product_kind, output.product_schema_version,
                   output.content_identity_sha256, output.availability_state, output.availability_reason,
                   output.frame_artifact_recipe_version,
                   raw.exposure_started_unix_ms AS exposure_started_unix_ms,
                   ROW_NUMBER() OVER (
                       PARTITION BY output.capture_id
                       ORDER BY output.committed_unix_ms DESC, output.output_identity_sha256 DESC) AS rank
            FROM raw_captures AS raw INDEXED BY ix_raw_captures_gallery_time
            CROSS JOIN processing_outputs AS output INDEXED BY ix_processing_outputs_capture_node
                ON output.capture_id = raw.capture_id AND output.node_id = $node_id
            WHERE raw.state = 'committed'
              AND raw.exposure_started_unix_ms >= $start_unix_ms
              AND raw.exposure_started_unix_ms < $end_unix_ms
              AND output.role = $role
              AND output.availability_state = 'Available'
              AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                               WHERE association.output_identity_sha256 = output.output_identity_sha256)
                   OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                              WHERE association.output_identity_sha256 = output.output_identity_sha256
                                AND association.published_flag = 1))
        )
        SELECT output_identity_sha256, artifact_id, payload_relative_path, sidecar_relative_path,
               descriptor_json, capture_id, agent_id, node_id, role, variant,
               recipe_identity_sha256, algorithms_json, compatibility_json, total_integration_ticks,
               capture_sequence, product_kind, product_schema_version, content_identity_sha256,
               availability_state, availability_reason, frame_artifact_recipe_version
        FROM candidates
        WHERE rank = 1
        ORDER BY exposure_started_unix_ms, capture_sequence, output_identity_sha256
        LIMIT $maximum_count_plus_one;
        """;

    /// <summary>
    /// Reads the published, available outputs of one node and role whose raw capture began exposing inside
    /// [<paramref name="startUtc"/>, <paramref name="endUtc"/>), ordered by exposure start. The window is selected by
    /// the raw capture's own exposure clock, never by processing or commit time. A capture with several outputs on the
    /// node contributes only its most recently committed one. At most <paramref name="maximumCount"/> + 1 rows are
    /// returned so the caller can reject an over-bound window instead of truncating it.
    /// </summary>
    internal async ValueTask<IReadOnlyList<DurableProcessingOutput>> ReadPublishedOutputsByExposureAsync(
        string nodeId,
        FrameArtifactRole role,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = PublishedOutputsByExposureSql;
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$role", role.ToString());
        command.Parameters.AddWithValue("$start_unix_ms", startUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end_unix_ms", endUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$maximum_count_plus_one", (long)maximumCount + 1);
        return (await ReadOutputRowsAsync(command, cancellationToken).ConfigureAwait(false))
            .Select(static row => row.Output)
            .ToList();
    }
}
