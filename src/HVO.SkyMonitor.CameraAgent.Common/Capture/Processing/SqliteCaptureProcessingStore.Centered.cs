using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
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
               product.artifact_id, product.payload_sha256, product.content_identity_sha256,
               output.availability_state, output.availability_reason,
               raw.raw_artifact_id, raw.descriptor_sha256, raw.payload_sha256
        FROM centered_exposure_products product
        JOIN centered_exposure_attempts attempt ON attempt.capture_id = product.capture_id
        JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
        JOIN raw_captures raw ON raw.capture_id = product.capture_id
        LEFT JOIN processing_outputs output ON output.output_identity_sha256 = product.output_identity
        """;

    internal const string CenteredProductsAfterSql = CenteredProductProjection + """
         WHERE product.agent_id = $agent AND product.camera_id = $camera AND product.revision > $revision
         ORDER BY product.revision LIMIT $limit;
        """;

    internal const string NoLinearStepProducer = "$step3";
    internal const string NoLinearStepVariant = "none";

    // Old snapshots could freeze the uncompiled default alias. An exact compiled ID always wins.
    private const string CenteredLinearNodeJoin = """
        LEFT JOIN processing_execution_nodes node
          ON node.execution_id = execution.execution_id
         AND node.node_id = CASE
            WHEN run.linear_node_id = 'Calibration' COLLATE NOCASE AND NOT EXISTS (
                SELECT 1 FROM processing_execution_nodes exact
                WHERE exact.execution_id = execution.execution_id AND exact.node_id = run.linear_node_id)
            THEN 'Calibration' ELSE run.linear_node_id END
        """;

    private const string CommittedArchivePublicationsSql = """
        SELECT input.variant, input.identity_sha256
        FROM processing_executions execution
        JOIN raw_captures raw ON raw.capture_id = execution.capture_id
                             AND raw.raw_artifact_id = execution.primary_artifact_id
        JOIN centered_exposure_attempts exposure ON exposure.capture_id = raw.capture_id
        JOIN processing_execution_nodes bound
          ON bound.execution_id = execution.execution_id AND bound.node_id = $node
        JOIN processing_nodes node
          ON node.capture_id = raw.capture_id AND node.node_id = bound.node_id
          AND node.plan_sha256 = bound.plan_sha256 AND node.attempt = bound.attempt_count
          AND node.completed_unix_ms = bound.completed_unix_ms
        JOIN processing_node_inputs input ON input.capture_id = node.capture_id AND input.node_id = node.node_id
        WHERE execution.execution_id = $execution AND execution.execution_class = 'Live'
          AND execution.trigger_kind = 'capture' AND execution.allow_automatic_publication = 1
          AND bound.status = 'Completed' AND node.status = 'Completed'
          AND input.kind = 'CanonicalContext' AND input.name = 'centered-archive' AND input.schema_version = $schema
        """;

    private static SqliteCommand CreateCommittedArchivePublicationsQuery(
        SqliteConnection connection, Guid executionId, string nodeId)
    {
        var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$node", nodeId);
        command.Parameters.AddWithValue("$schema", Storage.FileSystemFrameStorageService.ArchiveEvidenceVersion);
        return command;
    }

    internal static bool IsCommittedArchivePublication(
        SqliteConnection connection, Guid executionId, Guid captureId, string nodeId, string path, string identity)
    {
        using var command = CreateCommittedArchivePublicationsQuery(connection, executionId, nodeId);
        // The exact copy input was written by Storage after saving, then committed under the live lease.
        // Merely selecting a derivative can result in a successful Storage node with no exported copy.
        command.CommandText = CommittedArchivePublicationsSql + """

             AND raw.capture_id = $capture AND input.variant = $path AND input.identity_sha256 = $identity
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$identity", identity);
        return command.ExecuteScalar() is string;
    }

    internal async ValueTask<IReadOnlyList<(string RelativePath, string Identity)>> ReadCommittedArchivePublicationsAsync(
        Guid executionId, string nodeId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommittedArchivePublicationsQuery(connection, executionId, nodeId);
        command.CommandText = CommittedArchivePublicationsSql + """

            ORDER BY input.input_ordinal;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<(string, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    internal static async Task<bool> PublishMissingCenteredLinearAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid executionId, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $$"""
            INSERT INTO centered_exposure_products(agent_id, camera_id, capture_id, execution_id, producer, schema_version,
                variant, disposition, reason)
            SELECT run.agent_id, run.camera_id, execution.capture_id, execution.execution_id,
                   COALESCE(node.node_id, run.linear_node_id, $no_step), $schema, COALESCE(run.linear_variant, $no_variant),
                   CASE WHEN run.linear_node_id IS NULL THEN 'Unavailable'
                        WHEN execution.status = 'Expired' THEN 'Expired'
                        WHEN execution.status = 'Failed' OR node.status = 'TerminalFailure' THEN 'Failed'
                        ELSE 'Unavailable' END,
                   CASE WHEN run.linear_node_id IS NULL THEN 'linear.no-configured-step'
                        WHEN execution.status = 'Expired' THEN COALESCE(execution.failure_reason, 'linear.execution-expired')
                        ELSE COALESCE(node.reason, execution.failure_reason, 'linear.plan-did-not-produce') END
            FROM processing_executions execution
            JOIN centered_exposure_attempts attempt ON attempt.capture_id = execution.capture_id
            JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
            {{CenteredLinearNodeJoin}}
            WHERE execution.execution_id = $execution AND execution.execution_class = 'Live'
              AND execution.trigger_kind = 'capture' AND execution.allow_automatic_publication = 1
              AND (run.linear_node_id IS NULL OR execution.status IN ('Completed','Expired','Failed','Cancelled'))
            ON CONFLICT(capture_id, execution_id, producer, schema_version, variant) DO NOTHING
            RETURNING revision;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$schema", CenteredExposureProductReader.LinearSchemaVersion);
        command.Parameters.AddWithValue("$no_step", NoLinearStepProducer);
        command.Parameters.AddWithValue("$no_variant", NoLinearStepVariant);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long revision) return false;
        await TrackCenteredProductClockAsync(command, revision, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<List<long>?> PublishCenteredProductsAsync(SqliteConnection connection,
        SqliteTransaction transaction, ProcessingExecutionContext execution, IReadOnlyList<CenteredProductSettlement>? settlements,
        CancellationToken token)
    {
        if (execution is not { ExecutionClass: ProcessingGraphExecutionClass.Live, AllowAutomaticPublication: true } || settlements is null)
            return null;

        List<long>? revisions = null;
        foreach (var settlement in settlements)
        {
            if (await PublishCenteredProductAsync(connection, transaction, execution.ExecutionId, settlement, token).ConfigureAwait(false) is { } revision)
                (revisions ??= []).Add(revision);
        }
        return revisions;
    }

    private async ValueTask ObserveCommittedCenteredProductsAsync(SqliteConnection connection, List<long> revisions)
    {
        _centeredProductChanges?.NotifyCommitted();
        try
        {
            // A stalled node commit cannot backdate product availability.
            await ObserveCenteredProductsAsync(connection, revisions).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // The coordinator recovers any missing observation at recovery time.
        }
        _centeredWindowWakeup?.NotifyCommitted();
    }

    private static async Task<long?> PublishCenteredProductAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId,
        CenteredProductSettlement settlement,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $$"""
            INSERT INTO centered_exposure_products(agent_id, camera_id, capture_id, execution_id, producer, schema_version, variant,
                output_identity, artifact_id, payload_sha256, content_identity_sha256, disposition, reason, provenance_json)
            SELECT run.agent_id, run.camera_id, $capture, $execution, $producer, $schema, $variant, $output, $artifact,
                $payload, $content, $disposition, $reason, $provenance
            FROM centered_exposure_attempts attempt
            JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
            JOIN processing_executions execution ON execution.execution_id = $execution
            {{CenteredLinearNodeJoin}}
            WHERE attempt.capture_id = $capture AND run.run_id = $run AND run.agent_id = $agent AND run.camera_id = $camera
              AND ($schema <> $linear_schema OR (node.node_id = $producer AND run.linear_variant = $variant))
            ON CONFLICT(capture_id, execution_id, producer, schema_version, variant) DO NOTHING
            RETURNING revision;
            """;
        SqliteRawCaptureJournal.AddExposureParameters(command, settlement.Exposure);
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$producer", settlement.Producer);
        command.Parameters.AddWithValue("$schema", settlement.SchemaVersion);
        command.Parameters.AddWithValue("$linear_schema", CenteredExposureProductReader.LinearSchemaVersion);
        command.Parameters.AddWithValue("$variant", settlement.Variant);
        command.Parameters.AddWithValue("$output", (object?)settlement.OutputIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$artifact", settlement.ArtifactId is { } artifact ? artifact.ToString("N") : DBNull.Value);
        command.Parameters.AddWithValue("$payload", (object?)settlement.PayloadSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", (object?)settlement.ContentIdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$disposition", settlement.Availability.ToString());
        command.Parameters.AddWithValue("$reason", (object?)settlement.Reason ?? DBNull.Value);
        object provenanceValue = settlement.Linear is not null
            ? JsonSerializer.SerializeToUtf8Bytes(settlement.Linear)
            : settlement.Semantics is not null
                ? settlement.Semantics.Serialize()
                : DBNull.Value;
        command.Parameters.AddWithValue("$provenance", provenanceValue);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
        {
            await TrackCenteredProductClockAsync(command, revision, cancellationToken).ConfigureAwait(false);
            return revision;
        }

        command.CommandText = """
            SELECT provenance_json FROM centered_exposure_products
            WHERE capture_id = $capture AND execution_id = $execution AND producer = $producer AND schema_version = $schema
              AND variant = $variant AND output_identity IS $output AND artifact_id IS $artifact
              AND payload_sha256 IS $payload AND content_identity_sha256 IS $content AND disposition = $disposition
              AND agent_id = $agent AND camera_id = $camera
              AND reason IS $reason AND (provenance_json IS $provenance OR $scene = 1);
            """;
        var isScene = ProjectedSceneV1.IsSupportedSchemaVersion(settlement.SchemaVersion);
        command.Parameters.AddWithValue("$scene", isScene ? 1 : 0);
        var committedProvenance = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (committedProvenance is null)
            throw new InvalidDataException("The centered product conflicts with an already settled capture output.");
        if (isScene && committedProvenance is byte[] historical)
        {
            if (settlement.ArtifactId is not { } artifactId || settlement.OutputIdentity is not { } outputIdentity ||
                settlement.PayloadSha256 is not { } payload)
                throw new InvalidDataException("The regenerated centered scene has an incomplete product identity.");
            var reference = new ExposureProductRef(settlement.Exposure,
                new(executionId, settlement.Producer, settlement.SchemaVersion, settlement.Variant),
                artifactId, outputIdentity, payload, settlement.ContentIdentitySha256);
            var restored = await ReadCenteredProductHeaderAsync(transaction, reference, cancellationToken).ConfigureAwait(false);
            var saved = ReadHistoricalCenteredSceneHeader(historical, reference);
            if (CenteredStackArtifactPin.FromHeader(restored, payload) != CenteredStackArtifactPin.FromHeader(saved, payload))
                throw new InvalidDataException("The regenerated centered scene differs from its original header.");
        }
        return null;
    }

    private static Task<int> TrackCenteredProductClockAsync(SqliteCommand command, long revision, CancellationToken cancellationToken)
    {
        command.CommandText = "INSERT INTO processing_centered_product_clock(revision) VALUES ($revision);";
        command.Parameters.AddWithValue("$revision", revision);
        return command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ObserveCenteredProductsAsync(SqliteConnection connection, IReadOnlyList<long> revisions)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE processing_centered_product_clock SET settled_ticks = $observed
            WHERE settled_ticks IS NULL AND revision IN (SELECT value FROM json_each($revisions));
            """;
        command.Parameters.AddWithValue("$revisions", JsonSerializer.Serialize(revisions));
        command.Parameters.AddWithValue("$observed", _timeProvider.GetUtcNow().UtcTicks);
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal async ValueTask<bool> RecoverUnobservedCenteredEvidenceAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournalMetadataWrite();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // An immediate transaction establishes the observation snapshot before sampling its clock.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        var now = _timeProvider.GetUtcNow();
        var holdsRecovered = await CenteredWindowRuntimeJournal.RetryOpenHoldsAsync(transaction, now, cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE processing_centered_attempt_calendar
            SET dispatched_ticks = CASE WHEN dispatched_ticks = $unobserved THEN MIN($observed, COALESCE(admitted_ticks, $observed))
                                        ELSE dispatched_ticks END,
                expired_ticks = CASE WHEN expired_ticks = $unobserved THEN $observed ELSE expired_ticks END
            WHERE attempt_id IN (
                SELECT DISTINCT calendar.attempt_id
                FROM processing_centered_windows window
                JOIN processing_centered_attempt_calendar center ON center.attempt_id = REPLACE(CASE WHEN json_valid(window.checkpoint_json)
                    THEN json_extract(window.checkpoint_json, '$.referenceAttempt.attemptId') END, '-', '')
                JOIN processing_centered_attempt_calendar calendar ON calendar.run_id = center.run_id
                    AND calendar.slot BETWEEN center.slot - 2 AND center.slot + 2
                WHERE window.terminal = 0 AND window.closure_json IS NULL
                    AND (calendar.dispatched_ticks = $unobserved OR calendar.expired_ticks = $unobserved)
                LIMIT 128);
            UPDATE processing_centered_attempt_calendar SET admitted_ticks = $observed
            WHERE attempt_id IN (
                SELECT DISTINCT calendar.attempt_id
                FROM processing_centered_windows window
                JOIN processing_centered_window_holds owner ON owner.window_sha256 = window.window_sha256
                JOIN centered_exposure_holds hold ON hold.owner_id = owner.owner_id
                JOIN centered_exposure_attempts attempt ON attempt.capture_id = hold.capture_id
                JOIN raw_captures raw ON raw.capture_id = hold.capture_id AND raw.state = 'committed'
                JOIN processing_centered_attempt_calendar calendar ON calendar.attempt_id = attempt.attempt_id
                WHERE window.terminal = 0 AND window.closure_json IS NULL AND calendar.admitted_ticks IS NULL
                LIMIT 128);
            UPDATE processing_centered_product_clock SET settled_ticks = $observed
            WHERE settled_ticks IS NULL AND revision IN (
                SELECT revision FROM processing_centered_product_clock WHERE settled_ticks IS NULL
                ORDER BY revision LIMIT 128);
            UPDATE processing_centered_output_availability SET observed_ticks = MAX(recorded_ticks, $observed)
            WHERE observed_ticks IS NULL AND change_id IN (
                SELECT change_id FROM processing_centered_output_availability WHERE observed_ticks IS NULL
                ORDER BY change_id LIMIT 128);
            """;
        command.Parameters.AddWithValue("$observed", now.UtcTicks);
        command.Parameters.AddWithValue("$unobserved", CenteredWindowRuntimeJournal.UnobservedCalendarTicks);
        var observed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return observed || holdsRecovered;
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
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCenteredLookup(reader, out _) : null;
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
        command.CommandText = CenteredProductsAfterSql;
        command.Parameters.AddWithValue("$agent", camera.AgentId);
        command.Parameters.AddWithValue("$camera", camera.CameraId);
        command.Parameters.AddWithValue("$revision", afterRevision);
        command.Parameters.AddWithValue("$limit", maximumCount);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var products = new List<ExposureProductLookup>(maximumCount);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            products.Add(ReadCenteredLookup(reader, out _));
        return products;
    }

    internal async Task<(ExposureProductLookup? Product, LinearInputProvenanceV1? Provenance)>
        ReadCenteredLinearAsync(ExposureKey key, CenteredRawEvidence raw, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = CenteredProductProjection + $$"""
             JOIN processing_executions execution ON execution.execution_id = product.execution_id
             {{CenteredLinearNodeJoin}}
             WHERE product.capture_id = $capture AND run.run_id = $run AND run.agent_id = $agent AND run.camera_id = $camera
               AND execution.execution_class = 'Live' AND execution.allow_automatic_publication = 1
               AND execution.trigger_kind = 'capture' AND execution.capture_id = $capture
               AND execution.execution_id = $execution
               AND execution.primary_artifact_id = $raw AND execution.graph_revision_id = $graph
               AND product.schema_version = $schema
               AND product.producer = COALESCE(node.node_id, run.linear_node_id, $no_step) AND product.variant = $variant;
            """;
        SqliteRawCaptureJournal.AddExposureParameters(command, key);
        command.Parameters.AddWithValue("$schema", CenteredExposureProductReader.LinearSchemaVersion);
        command.Parameters.AddWithValue("$raw", raw.Manifest.Descriptor.Artifact.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$graph", raw.Run.GraphRevisionId);
        command.Parameters.AddWithValue("$execution",
            ProcessingGraphOperationsCoordinator.GetLiveExecutionId(key.CaptureId, raw.Run.GraphRevisionId).ToString("N"));
        command.Parameters.AddWithValue("$no_step", NoLinearStepProducer);
        command.Parameters.AddWithValue("$variant", raw.Run.LinearVariant ?? NoLinearStepVariant);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return (null, null);
        var product = ReadCenteredLookup(reader, out var provenance);
        return (product, provenance);
    }

    internal const long CenteredWindowMetadataReserveBytes = 4 * 1024 * 1024;

    // Every owner counts toward the same row limit. Each capture's raw file and each exact
    // leased output count once, including where the window already reserves their storage.
    // A window reserves space for future products as well as the products that already exist.
    internal const string CenteredInputHoldBudgetSql = """
        WITH owners AS MATERIALIZED (
            SELECT capture_id, output_identity, bytes
            FROM centered_exposure_holds WHERE expires_unix_ms > $now
            UNION ALL
            SELECT $capture, $output, $bytes
        ), backed AS (
            SELECT capture_id, output_identity, MAX(bytes) AS bytes
            FROM owners GROUP BY capture_id, output_identity
        ), capture_sizes AS (
            SELECT COALESCE(raw.payload_length, 0) AS raw_bytes,
                MAX(CASE WHEN backed.output_identity GLOB 'raw:*' THEN backed.bytes END) AS window_bytes,
                SUM(CASE WHEN backed.output_identity GLOB 'raw:*' THEN 0
                         ELSE MAX(backed.bytes - COALESCE(raw.payload_length, 0), 0) END) AS linear_bytes
            FROM backed LEFT JOIN raw_captures raw ON raw.capture_id = backed.capture_id
            GROUP BY backed.capture_id, raw.payload_length
        )
        INSERT INTO centered_exposure_holds(owner_id, capture_id, output_identity, bytes, expires_unix_ms)
        SELECT $owner, $capture, $output, $bytes, $expires
        WHERE $expires > $now
          AND (SELECT COUNT(*) FROM owners) <= 64
          AND (SELECT SUM(CASE WHEN window_bytes IS NULL THEN raw_bytes + linear_bytes
                               ELSE MAX(window_bytes, raw_bytes + linear_bytes + $metadata) END)
               FROM capture_sizes) <= 536870912
        """;

    internal async Task<bool> TryHoldCenteredInputAsync(
        Guid owner, ExposureProductRef product, long bytes, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        if (bytes <= 0) return false;
        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournalMetadataWrite();
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
        command.CommandText = CenteredInputHoldBudgetSql + """

              AND NOT EXISTS (SELECT 1 FROM processing_lifecycle_operations operation
                              WHERE operation.output_identity_sha256 = $output AND operation.kind IN ('delete', 'quarantine'))
              AND EXISTS (SELECT 1 FROM centered_exposure_products product
                          JOIN processing_outputs output ON output.output_identity_sha256 = product.output_identity
                          WHERE product.capture_id = $capture AND product.execution_id = $execution
                            AND product.producer = $producer AND product.schema_version = $schema AND product.variant = $variant
                            AND product.artifact_id = $artifact AND product.payload_sha256 = $payload
                            AND product.output_identity = $output AND product.disposition = 'Available'
                            AND output.availability_state = 'Available');
            """;
        command.Parameters.AddWithValue("$owner", owner.ToString("N"));
        command.Parameters.AddWithValue("$capture", product.Exposure.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$output", product.OutputIdentitySha256);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$metadata", CenteredWindowMetadataReserveBytes);
        command.Parameters.AddWithValue("$expires", expiresUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$execution", product.Slot.ExecutionId.ToString("N"));
        command.Parameters.AddWithValue("$producer", product.Slot.Producer);
        command.Parameters.AddWithValue("$schema", product.Slot.SchemaVersion);
        command.Parameters.AddWithValue("$variant", product.Slot.Variant);
        command.Parameters.AddWithValue("$artifact", product.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$payload", product.PayloadSha256);
        var added = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    internal async Task ReleaseCenteredInputHoldAsync(Guid owner)
    {
        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournalMetadataWrite();
        using var connection = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM centered_exposure_holds WHERE owner_id = $owner;";
        command.Parameters.AddWithValue("$owner", owner.ToString("N"));
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static ExposureProductLookup ReadCenteredLookup(SqliteDataReader reader, out LinearInputProvenanceV1? provenance)
    {
        var exposure = new ExposureKey(new(reader.GetString(3), reader.GetString(4)),
            Guid.ParseExact(reader.GetString(2), "N"), Guid.ParseExact(reader.GetString(1), "N"));
        var slot = new ExposureProductSlot(Guid.ParseExact(reader.GetString(5), "N"),
            reader.GetString(6), reader.GetString(7), reader.GetString(8));
        if (!Enum.TryParse<ExposureProductAvailability>(reader.GetString(10), out var availability) || !Enum.IsDefined(availability))
            throw new InvalidDataException("The centered product has an invalid durable disposition.");
        var reason = reader.IsDBNull(11) ? null : reader.GetString(11);
        provenance = null;
        ExposureProductRef? output = null;
        var hasArtifact = !reader.IsDBNull(13);
        if ((availability == ExposureProductAvailability.Available ||
             availability == ExposureProductAvailability.Expired && hasArtifact) &&
            (reader.IsDBNull(9) || !hasArtifact || reader.IsDBNull(14)))
            return new(exposure, slot, ExposureProductAvailability.MissingEvidence, reader.GetInt64(0), null,
                "product.settlement-identity-missing");
        if (availability is ExposureProductAvailability.Available or ExposureProductAvailability.Expired && hasArtifact)
            output = new(exposure, slot, Guid.ParseExact(reader.GetString(13), "N"), reader.GetString(9),
                reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15));
        if (availability == ExposureProductAvailability.Available)
        {
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

        if (slot.SchemaVersion == CenteredExposureProductReader.LinearSchemaVersion &&
            (availability == ExposureProductAvailability.Available ||
             availability == ExposureProductAvailability.Expired && !reader.IsDBNull(9)))
        {
            provenance = ReadOriginalLinearProvenance(reader);
            if (provenance is null)
            {
                availability = ExposureProductAvailability.MissingEvidence;
                reason = "linear.original-provenance-mismatch";
                output = null;
            }
            else
            {
                output ??= new(exposure, slot, provenance.LinearArtifactId, provenance.OutputIdentitySha256,
                    provenance.LinearPayloadSha256, reader.IsDBNull(15) ? null : reader.GetString(15));
            }
        }

        return new(exposure, slot, availability, reader.GetInt64(0), output, reason);
    }

    private static LinearInputProvenanceV1? ReadOriginalLinearProvenance(SqliteDataReader reader)
    {
        if (reader.IsDBNull(9) || reader.IsDBNull(12) || reader.IsDBNull(14)) return null;
        LinearInputProvenanceV1? saved;
        try
        {
            saved = JsonSerializer.Deserialize<LinearInputProvenanceV1>(reader.GetFieldValue<byte[]>(12));
        }
        catch (JsonException)
        {
            return null;
        }
        if (saved is not
            {
                Disposition: LinearExposureDisposition.Calibrated or LinearExposureDisposition.QualifiedNormalizationOnly,
                Recipe.Descriptor: not null, RawWhiteThreshold: not null, LinearWhiteThreshold: not null
            } || saved.CalibrationArtifactIds.IsDefault ||
            saved.RawArtifactId.ToString("N") != reader.GetString(18) ||
            saved.RawDescriptorSha256 != reader.GetString(19) || saved.RawPayloadSha256 != reader.GetString(20) ||
            saved.OutputIdentitySha256 != reader.GetString(9) || saved.LinearPayloadSha256 != reader.GetString(14) ||
            saved.LinearArtifactId == Guid.Empty ||
            !reader.IsDBNull(13) && saved.LinearArtifactId.ToString("N") != reader.GetString(13))
            return null;
        return saved;
    }
}
