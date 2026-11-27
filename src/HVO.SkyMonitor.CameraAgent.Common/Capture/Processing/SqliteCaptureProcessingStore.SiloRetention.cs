using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record SiloSceneLineage(
    string ArtifactId, string? RawArtifactId, bool HasRecordedHistory, IReadOnlyList<string>? ConsumerIds = null);

internal sealed partial class SqliteCaptureProcessingStore
{
    internal async ValueTask<IReadOnlyList<SiloRetentionHold>> ReadSavedSiloViewHoldsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT base_id, revision_id FROM processing_centered_view_selections;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloRetentionHold>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(SiloHoldKind.ViewManifest, reader.GetString(0), reader.GetString(1), IdentityKind: SiloIdentityKind.Manifest));
        return result;
    }

    internal async ValueTask<IReadOnlyList<SiloSceneLineage>> ReadSiloSceneLineageAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT scene.artifact_id, raw.raw_artifact_id, EXISTS (
                SELECT 1 FROM processing_executions execution
                JOIN processing_graph_revisions revision ON revision.revision_id = execution.graph_revision_id
                WHERE execution.execution_class IN ('Live', 'Replay') AND execution.capture_id = scene.capture_id
            ), (
                SELECT json_group_array(DISTINCT consumer.artifact_id)
                FROM processing_outputs consumer
                WHERE consumer.capture_id = scene.capture_id AND consumer.artifact_id <> scene.artifact_id
                  AND COALESCE(consumer.product_schema_version, '') NOT IN ('projected-scene-v1', 'projected-scene-v2', 'projected-scene-v3')
            )
            FROM processing_outputs scene LEFT JOIN raw_captures raw ON raw.capture_id = scene.capture_id
            WHERE scene.product_schema_version IN ('projected-scene-v1', 'projected-scene-v2', 'projected-scene-v3');
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SiloSceneLineage>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var raw = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                ? null : Guid.ParseExact(reader.GetString(1), "N").ToString("D");
            var consumers = JsonSerializer.Deserialize<string[]>(reader.GetString(3))
                ?? throw new InvalidDataException("The scene consumer inventory is invalid.");
            result.Add(new(Guid.ParseExact(reader.GetString(0), "N").ToString("D"), raw, reader.GetBoolean(2),
                consumers.Select(static id => Guid.ParseExact(id, "N").ToString("D")).ToArray()));
        }
        return result;
    }
}
