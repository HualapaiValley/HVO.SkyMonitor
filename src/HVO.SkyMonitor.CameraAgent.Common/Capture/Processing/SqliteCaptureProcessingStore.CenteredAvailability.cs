using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private const string CenteredAvailabilityCutoffIndex = """
        CREATE INDEX ix_processing_centered_output_availability_cutoff
            ON processing_centered_output_availability(output_identity_sha256, change_id DESC, recorded_ticks, state);
        """;
    private const string CenteredAvailabilitySchemaSql = """
        CREATE TABLE processing_centered_output_availability(
            change_id INTEGER PRIMARY KEY,
            output_identity_sha256 TEXT NOT NULL CHECK(length(output_identity_sha256) = 64),
            state TEXT NOT NULL CHECK(state IN ('Available', 'Missing', 'Quarantined', 'Expired')),
            reason TEXT,
            recorded_ticks INTEGER NOT NULL,
            observed_ticks INTEGER CHECK(observed_ticks >= recorded_ticks)
        ) STRICT;
        """ + "\n" + CenteredAvailabilityCutoffIndex + "\n" + """
        CREATE INDEX ix_processing_centered_output_availability_pending
            ON processing_centered_output_availability(change_id) WHERE observed_ticks IS NULL;
        """;

    private async ValueTask<long?> RecordCenteredAvailabilityChangeAsync(SqliteTransaction transaction,
        string outputIdentity, string state, string? reason, CancellationToken cancellationToken)
    {
        if (state != "Available")
            await PreserveCenteredSceneHeaderAsync(transaction, outputIdentity, cancellationToken).ConfigureAwait(false);
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_centered_output_availability(output_identity_sha256, state, reason, recorded_ticks)
            SELECT $output, $state, $reason, $now
            WHERE EXISTS (
                SELECT 1 FROM processing_outputs output
                JOIN centered_exposure_products product ON product.capture_id = output.capture_id
                    AND product.output_identity = output.output_identity_sha256
                WHERE output.output_identity_sha256 = $output)
            AND ($state <> 'Available' OR EXISTS (
                SELECT 1 FROM processing_centered_output_availability WHERE output_identity_sha256 = $output))
            AND NOT EXISTS (
                SELECT 1 FROM (
                    SELECT state, reason FROM processing_centered_output_availability
                    WHERE output_identity_sha256 = $output ORDER BY change_id DESC LIMIT 1
                ) WHERE state = $state AND reason IS $reason)
            RETURNING change_id;
            """;
        command.Parameters.AddWithValue("$output", outputIdentity);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().UtcTicks);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    // A deterministic scene can be removed from the current output inventory before regeneration.
    // Keep its original payload-free header alongside the settlement, where closure already reads.
    private sealed record HistoricalCenteredScene(CenteredStackArtifactHeader Header, long TrustedFromTicks);

    private async ValueTask PreserveCenteredSceneHeaderAsync(SqliteTransaction transaction,
        string outputIdentity, CancellationToken cancellationToken)
    {
        var connection = transaction.Connection!;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT output.artifact_id,
                CASE WHEN loss.change_id IS NULL THEN 0 ELSE COALESCE((
                    SELECT observed_ticks FROM processing_centered_output_availability restore
                    WHERE restore.output_identity_sha256 = output.output_identity_sha256
                        AND restore.state = 'Available' AND restore.change_id > loss.change_id
                    ORDER BY restore.change_id DESC LIMIT 1), $now) END
            FROM processing_outputs output
            JOIN centered_exposure_products product ON product.capture_id = output.capture_id
                AND product.output_identity = output.output_identity_sha256 AND product.artifact_id = output.artifact_id
                AND product.schema_version = output.product_schema_version
            LEFT JOIN processing_centered_output_availability loss ON loss.change_id = (
                SELECT change_id FROM processing_centered_output_availability history
                WHERE history.output_identity_sha256 = output.output_identity_sha256 AND history.state <> 'Available'
                ORDER BY history.change_id DESC LIMIT 1)
            WHERE output.output_identity_sha256 = $output AND product.provenance_json IS NULL
                AND product.schema_version IN (SELECT value FROM json_each($scenes))
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$output", outputIdentity);
        command.Parameters.AddWithValue("$scenes", JsonSerializer.Serialize(ProjectedSceneV1.SupportedSchemaVersions));
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().UtcTicks);
        string artifactId;
        long trustedFrom;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return;
            artifactId = reader.GetString(0);
            trustedFrom = reader.GetInt64(1);
        }

        var output = await ReadOutputByArtifactIdAsync(connection, transaction, Guid.ParseExact(artifactId, "N"), cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The centered scene has no committed output header.");
        var header = CenteredStackArtifactHeader.FromArtifact(CenteredWindowArtifactHeader.FromOutput(output));
        command.CommandText = """
            UPDATE centered_exposure_products SET provenance_json = $header
            WHERE output_identity = $output AND artifact_id = $artifact AND capture_id = $capture
              AND schema_version = $schema AND payload_sha256 = $payload AND content_identity_sha256 IS $content
              AND provenance_json IS NULL;
            """;
        command.Parameters.AddWithValue("$artifact", artifactId);
        command.Parameters.AddWithValue("$capture", output.Capture.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$schema", output.ProductSchemaVersion!);
        command.Parameters.AddWithValue("$payload", output.Artifact.ChecksumSha256);
        command.Parameters.AddWithValue("$content", (object?)output.ContentIdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$header", JsonSerializer.SerializeToUtf8Bytes(new HistoricalCenteredScene(header, trustedFrom)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveCenteredAvailabilityChangeAsync(SqliteConnection connection, long? changeId)
    {
        if (changeId is null) return;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE processing_centered_output_availability SET observed_ticks = MAX(recorded_ticks, $observed)
                WHERE change_id = $change AND observed_ticks IS NULL;
                """;
            command.Parameters.AddWithValue("$change", changeId.Value);
            command.Parameters.AddWithValue("$observed", _timeProvider.GetUtcNow().UtcTicks);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // The lifecycle transaction is committed. Recovery can observe the transition from a later snapshot.
        }
        _centeredWindowWakeup?.NotifyCommitted();
    }
}
