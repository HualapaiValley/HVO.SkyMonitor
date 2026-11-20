using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private const string CenteredProductProjection = """
        SELECT product.revision, product.capture_id, run.run_id, run.agent_id, run.camera_id,
               product.execution_id, product.producer, product.schema_version, product.variant,
               product.output_identity, product.disposition, product.reason, product.provenance_json,
               output.artifact_id, product.payload_sha256, output.content_identity_sha256,
               output.availability_state, output.availability_reason
        FROM centered_exposure_products product
        JOIN centered_exposure_attempts attempt ON attempt.capture_id = product.capture_id
        JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
        LEFT JOIN processing_outputs output ON output.output_identity_sha256 = product.output_identity
        """;

    private static async Task<bool> PublishCenteredProductAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId,
        CenteredProductSettlement settlement,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO centered_exposure_products(capture_id, execution_id, producer, schema_version, variant,
                output_identity, payload_sha256, disposition, reason, provenance_json)
            VALUES ($capture, $execution, $producer, $schema, $variant, $output, $payload, $disposition, $reason, $provenance)
            ON CONFLICT(capture_id, execution_id, producer, schema_version, variant) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$capture", settlement.Exposure.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$producer", settlement.Producer);
        command.Parameters.AddWithValue("$schema", settlement.SchemaVersion);
        command.Parameters.AddWithValue("$variant", settlement.Variant);
        command.Parameters.AddWithValue("$output", (object?)settlement.OutputIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", (object?)settlement.PayloadSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$disposition", settlement.Availability.ToString());
        command.Parameters.AddWithValue("$reason", (object?)settlement.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$provenance",
            settlement.Linear is null ? DBNull.Value : JsonSerializer.SerializeToUtf8Bytes(settlement.Linear));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) return true;

        command.CommandText = """
            SELECT COUNT(*) FROM centered_exposure_products
            WHERE capture_id = $capture AND execution_id = $execution AND producer = $producer AND schema_version = $schema
              AND variant = $variant AND output_identity IS $output AND payload_sha256 IS $payload AND disposition = $disposition
              AND reason IS $reason AND provenance_json IS $provenance;
            """;
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 1L)
            throw new InvalidDataException("The centered product conflicts with an already settled capture output.");
        return false;
    }

    internal async Task<ExposureProductLookup?> ReadCenteredProductAsync(
        ExposureKey key, ExposureProductSlot slot, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = CenteredProductProjection + """
             WHERE product.capture_id = $capture AND run.run_id = $run AND run.agent_id = $agent AND run.camera_id = $camera
               AND product.execution_id = $execution AND product.producer = $producer
               AND product.schema_version = $schema AND product.variant = $variant;
            """;
        SqliteRawCaptureJournal.AddExposureParameters(command, key);
        command.Parameters.AddWithValue("$execution", slot.ExecutionId.ToString("N"));
        command.Parameters.AddWithValue("$producer", slot.Producer);
        command.Parameters.AddWithValue("$schema", slot.SchemaVersion);
        command.Parameters.AddWithValue("$variant", slot.Variant);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCenteredLookup(reader) : null;
    }

    internal async Task<IReadOnlyList<ExposureProductLookup>> ReadCenteredProductsAfterAsync(
        CameraKey camera, long afterRevision, int maximumCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterRevision);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 128);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = CenteredProductProjection + """
             WHERE run.agent_id = $agent AND run.camera_id = $camera AND product.revision > $revision
             ORDER BY product.revision LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$agent", camera.AgentId);
        command.Parameters.AddWithValue("$camera", camera.CameraId);
        command.Parameters.AddWithValue("$revision", afterRevision);
        command.Parameters.AddWithValue("$limit", maximumCount);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var products = new List<ExposureProductLookup>(maximumCount);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            products.Add(ReadCenteredLookup(reader));
        return products;
    }

    internal async Task<(ExposureProductLookup? Product, LinearInputProvenanceV1? Provenance, string? UnavailableReason)>
        ReadCenteredLinearAsync(ExposureKey key, CenteredRawEvidence raw, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = CenteredProductProjection + """
             JOIN processing_executions execution ON execution.execution_id = product.execution_id
             JOIN processing_execution_nodes node ON node.execution_id = execution.execution_id
                                                   AND node.node_id = product.producer
             WHERE product.capture_id = $capture AND run.run_id = $run AND run.agent_id = $agent AND run.camera_id = $camera
               AND execution.execution_class = 'Live' AND execution.allow_automatic_publication = 1
               AND execution.trigger_kind = 'capture' AND execution.capture_id = $capture
               AND execution.execution_id = $execution
               AND execution.primary_artifact_id = $raw AND execution.graph_revision_id = $graph
               AND product.schema_version = $schema AND product.producer = $node AND product.variant = $variant;
            """;
        SqliteRawCaptureJournal.AddExposureParameters(command, key);
        command.Parameters.AddWithValue("$schema", CenteredExposureProductReader.LinearSchemaVersion);
        command.Parameters.AddWithValue("$raw", raw.Manifest.Descriptor.Artifact.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$graph", raw.Run.GraphRevisionId);
        command.Parameters.AddWithValue("$execution",
            ProcessingGraphOperationsCoordinator.GetLiveExecutionId(key.CaptureId, raw.Run.GraphRevisionId).ToString("N"));
        command.Parameters.AddWithValue("$node", raw.Run.LinearNodeId!);
        command.Parameters.AddWithValue("$variant", raw.Run.LinearVariant!);
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return (ReadCenteredLookup(reader), await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false) ? null :
                    JsonSerializer.Deserialize<LinearInputProvenanceV1>(
                        await reader.GetFieldValueAsync<byte[]>(12, cancellationToken).ConfigureAwait(false)), null);
        }

        command.CommandText = """
            SELECT CASE WHEN node.status IN ('Skipped','TerminalFailure')
                           THEN COALESCE(node.reason, 'linear.no-output')
                        WHEN execution.status IN ('Completed','Expired','Failed','Cancelled')
                           THEN COALESCE(execution.failure_reason, 'linear.plan-did-not-produce')
                        ELSE NULL END
            FROM processing_executions execution
            LEFT JOIN processing_execution_nodes node
              ON node.execution_id = execution.execution_id AND node.node_id = $node
            WHERE execution.capture_id = $capture AND execution.execution_class = 'Live'
              AND execution.allow_automatic_publication = 1 AND execution.trigger_kind = 'capture'
              AND execution.execution_id = $execution
              AND execution.primary_artifact_id = $raw AND execution.graph_revision_id = $graph;
            """;
        return (null, null, await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string);
    }

    internal async Task<bool> TryHoldCenteredInputAsync(
        Guid owner, ExposureProductRef product, long bytes, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        if (bytes <= 0) return false;
        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournal(_databasePath);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // The immediate transaction overload is synchronous in Microsoft.Data.Sqlite.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM centered_exposure_holds WHERE expires_unix_ms <= $now;
            """;
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = """
            INSERT INTO centered_exposure_holds(owner_id, capture_id, output_identity, bytes, expires_unix_ms)
            SELECT $owner, $capture, $output, $bytes, $expires
            WHERE $expires > $now
              AND (SELECT COUNT(*) FROM centered_exposure_holds) < 64
              AND (SELECT COALESCE(SUM(bytes), 0) FROM centered_exposure_holds) + $bytes <= 536870912;
            """;
        command.Parameters.AddWithValue("$owner", owner.ToString("N"));
        command.Parameters.AddWithValue("$capture", product.Exposure.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$output", product.OutputIdentitySha256);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$expires", expiresUtc.ToUnixTimeMilliseconds());
        var added = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    internal async Task ReleaseCenteredInputHoldAsync(Guid owner)
    {
        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournal(_databasePath);
        using var connection = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM centered_exposure_holds WHERE owner_id = $owner;";
        command.Parameters.AddWithValue("$owner", owner.ToString("N"));
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static ExposureProductLookup ReadCenteredLookup(SqliteDataReader reader)
    {
        var exposure = new ExposureKey(new(reader.GetString(3), reader.GetString(4)),
            Guid.ParseExact(reader.GetString(2), "N"), Guid.ParseExact(reader.GetString(1), "N"));
        var slot = new ExposureProductSlot(Guid.ParseExact(reader.GetString(5), "N"),
            reader.GetString(6), reader.GetString(7), reader.GetString(8));
        var availability = Enum.Parse<ExposureProductAvailability>(reader.GetString(10));
        var reason = reader.IsDBNull(11) ? null : reader.GetString(11);
        ExposureProductRef? output = null;
        if (availability == ExposureProductAvailability.Available)
        {
            if (!reader.IsDBNull(13))
                output = new(exposure, slot, Guid.ParseExact(reader.GetString(13), "N"), reader.GetString(9),
                    reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15));
            switch (reader.IsDBNull(16) ? null : reader.GetString(16))
            {
                case "Available":
                    break;
                case "Expired":
                    availability = ExposureProductAvailability.Expired;
                    break;
                default:
                    availability = ExposureProductAvailability.MissingEvidence;
                    break;
            }
            if (availability != ExposureProductAvailability.Available)
                reason = reader.IsDBNull(17) ? "product.retained-output-unavailable" : reader.GetString(17);
        }

        return new(exposure, slot, availability, reader.GetInt64(0), output, reason);
    }
}
