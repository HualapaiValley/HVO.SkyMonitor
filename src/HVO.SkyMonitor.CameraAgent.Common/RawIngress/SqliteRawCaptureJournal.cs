using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed class SqliteRawCaptureJournal(
    string databasePath,
    int busyTimeoutSeconds,
    Action<TimeSpan>? lockWaitRecorder = null,
    IRawIngressFaultInjector? faultInjector = null,
    Action<string, bool>? transactionRecorder = null,
    Action<bool>? checkpointRecorder = null,
    CaptureDistributionOptions? distributionOptions = null,
    ICaptureLaneFaultInjector? laneFaultInjector = null,
    Func<DateTimeOffset>? utcNow = null)
{
    internal const int CurrentSchemaVersion = 2;
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly int _busyTimeoutSeconds = busyTimeoutSeconds;
    private readonly Action<TimeSpan>? _lockWaitRecorder = lockWaitRecorder;
    private readonly IRawIngressFaultInjector _faultInjector = faultInjector ?? new NullRawIngressFaultInjector();
    private readonly Action<string, bool>? _transactionRecorder = transactionRecorder;
    private readonly Action<bool>? _checkpointRecorder = checkpointRecorder;
    private readonly CaptureDistributionOptions _distributionOptions = distributionOptions ?? new CaptureDistributionOptions();
    private readonly ICaptureLaneFaultInjector _laneFaultInjector = laneFaultInjector ?? new NullCaptureLaneFaultInjector();
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? SystemUtcNow;

    internal string DatabasePath => _databasePath;

    private static DateTimeOffset SystemUtcNow() => DateTimeOffset.UtcNow;

    internal Task InitializeAsync(CancellationToken cancellationToken)
        => InitializeAsync(DefaultLaneDefinitions, cancellationToken);

    internal async Task InitializeAsync(
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(laneDefinitions);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        EnsureDatabaseFilesArePhysical();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        EnsureDatabaseFilesArePhysical();

        var version = await ExecuteScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Raw ingress schema {version} is newer than supported schema {CurrentSchemaVersion}.");
        }
        if (version < CurrentSchemaVersion)
        {
            try
            {
                using var transaction = BeginImmediate(connection);
                _faultInjector.Inject(RawIngressFaultPoint.AfterMigrationTransactionBegan);
                if (version == 0)
                {
                    await ExecuteNonQueryAsync(connection, transaction, SchemaSql, cancellationToken).ConfigureAwait(false);
                }
                else if (version == 1)
                {
                    await ExecuteNonQueryAsync(connection, transaction, LaneSchemaSql, cancellationToken).ConfigureAwait(false);
                    await UpsertLaneDefinitionsAsync(connection, transaction, laneDefinitions, cancellationToken).ConfigureAwait(false);
                    await BackfillLaneWorkAsync(
                        connection, transaction, laneDefinitions, _distributionOptions, cancellationToken).ConfigureAwait(false);
                }
                await ExecuteNonQueryAsync(connection, transaction, $"PRAGMA user_version = {CurrentSchemaVersion};", cancellationToken).ConfigureAwait(false);
                _faultInjector.Inject(RawIngressFaultPoint.BeforeMigrationCommit);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                _transactionRecorder?.Invoke("migration", true);
            }
            catch
            {
                _transactionRecorder?.Invoke("migration", false);
                throw;
            }
        }

        await SynchronizeLaneDefinitionsAsync(connection, laneDefinitions, cancellationToken).ConfigureAwait(false);

        var integrity = await ExecuteScalarStringAsync(connection, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Raw ingress SQLite integrity check failed.");
        }
        var schemaObjectCount = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*) FROM sqlite_master
            WHERE name IN (
                'raw_capture_sequences', 'raw_capture_assignments', 'raw_captures', 'raw_ingress_reconciliation',
                'ix_raw_captures_discovery', 'ix_raw_captures_backlog', 'ix_raw_captures_retention',
                'capture_lane_definitions', 'capture_lane_contexts', 'capture_lane_work',
                'ix_capture_lane_work_claim', 'ix_capture_lane_work_lease',
                'ix_capture_lane_work_backlog', 'ix_capture_lane_work_raw', 'ix_capture_lane_work_ordered');
            """, cancellationToken).ConfigureAwait(false);
        if (schemaObjectCount != 15)
        {
            throw new InvalidDataException("Raw ingress SQLite schema is incomplete or drifted.");
        }
        await VerifyColumnsAsync(connection, "raw_captures", RawCaptureColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_lane_definitions", LaneDefinitionColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_lane_contexts", LaneContextColumns, cancellationToken).ConfigureAwait(false);
        await VerifyColumnsAsync(connection, "capture_lane_work", LaneWorkColumns, cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_lane_work_claim", "lane_name,state,available_unix_ms,work_id", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_lane_work_lease", "state,lease_expires_unix_ms", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_lane_work_backlog", "lane_name,state,created_unix_ms", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_lane_work_raw", "raw_capture_row_id,required,state", cancellationToken).ConfigureAwait(false);
        await VerifyIndexAsync(connection, "ix_capture_lane_work_ordered", "lane_name,agent_id,capture_sequence", cancellationToken).ConfigureAwait(false);
        await VerifyConnectionSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    internal Task<RawCaptureIdentity> ReserveIdentityAsync(
        string agentId,
        Guid captureId,
        Guid artifactId,
        CancellationToken cancellationToken)
        => TrackTransactionAsync(
            "identity",
            () => ReserveIdentityCoreAsync(agentId, captureId, artifactId, cancellationToken));

    internal async Task<(bool Exists, bool EvidenceRetained)> ReadCommittedCaptureStateAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_relative_path, sidecar_relative_path FROM raw_captures WHERE capture_id = $capture_id;";
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (false, false);
        }
        var root = Path.GetDirectoryName(Path.GetDirectoryName(_databasePath)!)!;
        var payload = Path.GetFullPath(Path.Combine(
            root, reader.GetString(0).Replace('/', Path.DirectorySeparatorChar)));
        var sidecar = Path.GetFullPath(Path.Combine(
            root, reader.GetString(1).Replace('/', Path.DirectorySeparatorChar)));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, payload);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, sidecar);
        return (true, File.Exists(payload) && File.Exists(sidecar));
    }

    private async Task<RawCaptureIdentity> ReserveIdentityCoreAsync(
        string agentId,
        Guid captureId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT agent_id, capture_sequence, raw_artifact_id FROM raw_capture_assignments WHERE capture_id = $capture_id;";
            existing.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
            using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var existingAgent = reader.GetString(0);
                var sequence = reader.GetInt64(1);
                var existingArtifact = Guid.ParseExact(reader.GetString(2), "N");
                if (!string.Equals(existingAgent, agentId, StringComparison.Ordinal) || existingArtifact != artifactId)
                {
                    throw new RawIngressConflictException("Capture identity is already assigned to different immutable facts.");
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new RawCaptureIdentity(agentId, sequence, captureId, artifactId);
            }
        }

        long allocatedSequence;
        using (var allocate = connection.CreateCommand())
        {
            allocate.Transaction = transaction;
            allocate.CommandText = """
                INSERT INTO raw_capture_sequences(agent_id, last_sequence)
                VALUES ($agent_id, 1)
                ON CONFLICT(agent_id) DO UPDATE SET last_sequence = last_sequence + 1
                RETURNING last_sequence;
                """;
            allocate.Parameters.AddWithValue("$agent_id", agentId);
            allocatedSequence = Convert.ToInt64(await allocate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
                VALUES ($capture_id, $artifact_id, $agent_id, $capture_sequence);
                """;
            insert.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
            insert.Parameters.AddWithValue("$artifact_id", artifactId.ToString("N"));
            insert.Parameters.AddWithValue("$agent_id", agentId);
            insert.Parameters.AddWithValue("$capture_sequence", allocatedSequence);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RawCaptureIdentity(agentId, allocatedSequence, captureId, artifactId);
    }

    internal Task<RawIngressOutcome> CommitAsync(RawIngressJournalEntry entry, CancellationToken cancellationToken)
        => CommitAsync(entry, null, null, DefaultLaneDefinitions, cancellationToken);

    internal Task<RawIngressOutcome> CommitAsync(
        RawIngressJournalEntry entry,
        byte[]? contextJson,
        string? contextSha256,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
        => TrackTransactionAsync(
            "commit",
            () => CommitCoreAsync(entry, contextJson, contextSha256, laneDefinitions, cancellationToken));

    private async Task<RawIngressOutcome> CommitCoreAsync(
        RawIngressJournalEntry entry,
        byte[]? contextJson,
        string? contextSha256,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        _faultInjector.Inject(RawIngressFaultPoint.AfterJournalTransactionBegan);
        var claims = await ReadClaimsAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        if (claims.Count > 0)
        {
            if (claims.Count == 1 && IsExact(claims[0], entry))
            {
                var existingRowId = await ReadRawRowIdAsync(connection, transaction, entry.CaptureId, cancellationToken).ConfigureAwait(false);
                await InsertContextAsync(
                    connection, transaction, existingRowId, contextJson, contextSha256, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RawIngressOutcome.Existing;
            }
            throw new RawIngressConflictException("Raw capture identity or path conflicts with committed immutable evidence.");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertCaptureSql;
        AddEntryParameters(command, entry);
        command.Parameters.AddWithValue("$committed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var rawRowId = await ReadLastInsertRowIdAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await InsertContextAsync(
            connection, transaction, rawRowId, contextJson, contextSha256, cancellationToken).ConfigureAwait(false);
        await InsertLaneWorkAsync(
            connection, transaction, rawRowId, entry, laneDefinitions, cancellationToken).ConfigureAwait(false);
        _laneFaultInjector.Inject(CaptureLaneFaultPoint.AfterWorkRowsInserted);
        _faultInjector.Inject(RawIngressFaultPoint.AfterJournalRowInserted);
        _faultInjector.Inject(RawIngressFaultPoint.BeforeJournalTransactionCommit);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RawIngressOutcome.Committed;
    }

    internal async Task<(long Count, long Bytes, DateTimeOffset? Oldest)> ReadHeldTotalsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(payload_length), 0), MIN(durable_ingress_unix_ms) FROM raw_captures WHERE retention_hold = 1;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset? oldest = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        return (
            reader.GetInt64(0),
            reader.GetInt64(1),
            oldest);
    }

    internal async Task<IReadOnlyList<RawIngressJournalEntry>> ReadAllAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT agent_id, capture_sequence, capture_id, raw_artifact_id,
                   descriptor_sha256, manifest_sha256, payload_sha256, payload_length,
                   payload_relative_path, sidecar_relative_path, manifest_json,
                   exposure_started_unix_ms, durable_ingress_unix_ms, state, retention_hold
            FROM raw_captures
            ORDER BY agent_id, capture_sequence;
            """;
        var entries = new List<RawIngressJournalEntry>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadEntry(reader));
        }
        return entries;
    }

    internal async Task<IReadOnlyList<RawIngressRetentionHold>> ReadRetentionHoldsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw_artifact_id, payload_relative_path, sidecar_relative_path
            FROM raw_captures
            WHERE retention_hold = 1
            ORDER BY agent_id, capture_sequence;
            """;
        var holds = new List<RawIngressRetentionHold>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            holds.Add(new RawIngressRetentionHold(
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetString(1),
                reader.GetString(2)));
        }
        return holds;
    }

    private async Task SynchronizeLaneDefinitionsAsync(
        SqliteConnection connection,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        try
        {
            using var transaction = BeginImmediate(connection);
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "UPDATE capture_lane_definitions SET enabled = 0, updated_unix_ms = unixepoch('subsec') * 1000;",
                cancellationToken).ConfigureAwait(false);
            await UpsertLaneDefinitionsAsync(connection, transaction, laneDefinitions, cancellationToken).ConfigureAwait(false);
            using (var orphaned = connection.CreateCommand())
            {
                orphaned.Transaction = transaction;
                orphaned.CommandText = """
                    SELECT w.lane_name
                    FROM capture_lane_work w
                    JOIN capture_lane_definitions d ON d.lane_name = w.lane_name
                    WHERE w.required = 1 AND w.state != 'completed' AND d.enabled = 0
                    LIMIT 1;
                    """;
                var orphanedLane = Convert.ToString(
                    await orphaned.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(orphanedLane))
                {
                    throw new InvalidOperationException(
                        $"Required capture lane '{orphanedLane}' has unfinished durable work and cannot be removed.");
                }
            }
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                UPDATE capture_lane_work
                SET state = 'abandoned', failure_reason = 'disabled', updated_unix_ms = unixepoch('subsec') * 1000,
                    lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL
                WHERE required = 0 AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')
                  AND lane_name IN (SELECT lane_name FROM capture_lane_definitions WHERE enabled = 0);
                """,
                cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                UPDATE raw_captures
                SET retention_hold = CASE WHEN EXISTS (
                    SELECT 1 FROM capture_lane_work
                    WHERE capture_lane_work.raw_capture_row_id = raw_captures.raw_capture_row_id
                      AND ((required = 1 AND state != 'completed') OR state = 'leased')
                ) THEN 1 ELSE 0 END;
                """,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _transactionRecorder?.Invoke("lane-policy", true);
        }
        catch
        {
            _transactionRecorder?.Invoke("lane-policy", false);
            throw;
        }
    }

    private static async Task UpsertLaneDefinitionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        foreach (var lane in laneDefinitions)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO capture_lane_definitions(
                    lane_name, enabled, required, ordered, policy_sha256, created_unix_ms, updated_unix_ms)
                VALUES ($lane, $enabled, $required, $ordered, $policy, $now, $now)
                ON CONFLICT(lane_name) DO UPDATE SET
                    enabled = excluded.enabled,
                    required = excluded.required,
                    ordered = excluded.ordered,
                    policy_sha256 = excluded.policy_sha256,
                    updated_unix_ms = excluded.updated_unix_ms;
                """;
            command.Parameters.AddWithValue("$lane", lane.Name);
            command.Parameters.AddWithValue("$enabled", lane.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$required", lane.Required ? 1 : 0);
            command.Parameters.AddWithValue("$ordered", lane.Ordered ? 1 : 0);
            command.Parameters.AddWithValue("$policy", lane.PolicySha256);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task BackfillLaneWorkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CaptureDistributionOptions options,
        CancellationToken cancellationToken)
    {
        foreach (var lane in laneDefinitions)
        {
            if (!lane.Enabled)
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO capture_lane_work(
                    raw_capture_row_id, lane_name, agent_id, capture_sequence, required, ordered, state,
                    attempt_count, available_unix_ms, created_unix_ms, updated_unix_ms)
                SELECT raw_capture_row_id, $lane, agent_id, capture_sequence, $required, $ordered, 'pending',
                       0, committed_unix_ms, $now, $now
                FROM raw_captures
                WHERE state = 'committed' AND retention_hold = 1
                ON CONFLICT(raw_capture_row_id, lane_name) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$lane", lane.Name);
            command.Parameters.AddWithValue("$required", lane.Required ? 1 : 0);
            command.Parameters.AddWithValue("$ordered", lane.Ordered ? 1 : 0);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (!lane.Required)
            {
                await ApplyOptionalBackfillPressureAsync(
                    connection, transaction, lane, options, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ApplyOptionalBackfillPressureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureLaneDefinition lane,
        CaptureDistributionOptions options,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH ranked AS (
                SELECT w.work_id,
                       ROW_NUMBER() OVER (ORDER BY w.agent_id, w.capture_sequence) AS pending_position,
                       SUM(r.payload_length) OVER (
                           ORDER BY w.agent_id, w.capture_sequence ROWS UNBOUNDED PRECEDING) AS cumulative_bytes,
                       r.durable_ingress_unix_ms
                FROM capture_lane_work w
                JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
                WHERE w.lane_name = $lane AND w.state = 'pending'
                  AND r.durable_ingress_unix_ms > $oldest_allowed
            )
            UPDATE capture_lane_work
            SET state = 'abandoned', failure_reason = 'optional-pressure', updated_unix_ms = $now
            WHERE (lane_name = $lane AND state = 'pending' AND EXISTS (
                       SELECT 1 FROM raw_captures r
                       WHERE r.raw_capture_row_id = capture_lane_work.raw_capture_row_id
                         AND r.durable_ingress_unix_ms <= $oldest_allowed
                   ))
               OR work_id IN (
                   SELECT work_id FROM ranked
                   WHERE pending_position > $maximum_count
                      OR cumulative_bytes > $maximum_bytes
               );
            """;
        command.Parameters.AddWithValue("$lane", lane.Name);
        command.Parameters.AddWithValue("$maximum_count", options.OptionalMaximumPendingCount);
        command.Parameters.AddWithValue("$maximum_bytes", options.OptionalMaximumPendingBytes);
        command.Parameters.AddWithValue(
            "$oldest_allowed",
            DateTimeOffset.UtcNow.AddMinutes(-options.OptionalMaximumOldestAgeMinutes).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        using var pressure = connection.CreateCommand();
        pressure.Transaction = transaction;
        pressure.CommandText = """
            UPDATE capture_lane_definitions
            SET pressure_state = (
                SELECT CASE
                    WHEN COUNT(*) >= $maximum_count
                      OR COALESCE(SUM(r.payload_length), 0) >= $maximum_bytes THEN 2
                    WHEN COUNT(*) * 100.0 >= $maximum_count * $recovery_percent
                      OR COALESCE(SUM(r.payload_length), 0) * 100.0 >= $maximum_bytes * $recovery_percent THEN 1
                    ELSE 0
                END
                FROM capture_lane_work w
                JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
                WHERE w.lane_name = $lane AND w.state IN ('pending', 'leased', 'retry_wait', 'quarantined')
            )
            WHERE lane_name = $lane;
            """;
        pressure.Parameters.AddWithValue("$lane", lane.Name);
        pressure.Parameters.AddWithValue("$maximum_count", options.OptionalMaximumPendingCount);
        pressure.Parameters.AddWithValue("$maximum_bytes", options.OptionalMaximumPendingBytes);
        pressure.Parameters.AddWithValue("$recovery_percent", options.PressureRecoveryPercent);
        await pressure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadRawRowIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $capture_id;";
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadLastInsertRowIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task InsertContextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRowId,
        byte[]? contextJson,
        string? contextSha256,
        CancellationToken cancellationToken)
    {
        if (contextJson is null || contextSha256 is null)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO capture_lane_contexts(raw_capture_row_id, context_json, context_sha256, context_source)
            VALUES ($raw, $json, $sha, 'capture')
            ON CONFLICT(raw_capture_row_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$raw", rawRowId);
        command.Parameters.AddWithValue("$json", contextJson);
        command.Parameters.AddWithValue("$sha", contextSha256);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = "SELECT context_sha256 FROM capture_lane_contexts WHERE raw_capture_row_id = $raw;";
        verify.Parameters.AddWithValue("$raw", rawRowId);
        var existing = Convert.ToString(
            await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(existing, contextSha256, StringComparison.Ordinal))
        {
            throw new RawIngressConflictException("Capture lane context differs from the committed retry context.");
        }
    }

    private async Task InsertLaneWorkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRowId,
        RawIngressJournalEntry entry,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        foreach (var lane in laneDefinitions)
        {
            if (!lane.Enabled)
            {
                continue;
            }

            var abandon = !lane.Required && await IsOptionalAtHardLimitAsync(
                connection, transaction, lane.Name, entry.PayloadLength, entry.DurableIngressUtc, cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO capture_lane_work(
                    raw_capture_row_id, lane_name, agent_id, capture_sequence, required, ordered, state,
                    attempt_count, available_unix_ms, failure_reason, created_unix_ms, updated_unix_ms)
                VALUES ($raw, $lane, $agent_id, $capture_sequence, $required, $ordered, $state,
                        0, $available, $reason, $now, $now)
                ON CONFLICT(raw_capture_row_id, lane_name) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$raw", rawRowId);
            command.Parameters.AddWithValue("$lane", lane.Name);
            command.Parameters.AddWithValue("$agent_id", entry.AgentId);
            command.Parameters.AddWithValue("$capture_sequence", entry.CaptureSequence);
            command.Parameters.AddWithValue("$required", lane.Required ? 1 : 0);
            command.Parameters.AddWithValue("$ordered", lane.Ordered ? 1 : 0);
            command.Parameters.AddWithValue("$state", abandon ? "abandoned" : "pending");
            command.Parameters.AddWithValue("$available", entry.DurableIngressUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$reason", abandon ? "optional-pressure" : DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<IReadOnlyList<(string Lane, bool Required, string State)>> ReadLaneOutcomesAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.lane_name, w.required, w.state
            FROM capture_lane_work w
            JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
            WHERE r.capture_id = $capture_id
            ORDER BY w.lane_name;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        var outcomes = new List<(string Lane, bool Required, string State)>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            outcomes.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetString(2)));
        }
        return outcomes;
    }

    private async Task<bool> IsOptionalAtHardLimitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string lane,
        long payloadLength,
        DateTimeOffset durableIngressUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(r.payload_length), 0), MIN(r.durable_ingress_unix_ms)
            FROM capture_lane_work w
            JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
            WHERE w.lane_name = $lane AND w.state IN ('pending', 'leased', 'retry_wait', 'quarantined');
            """;
        command.Parameters.AddWithValue("$lane", lane);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var count = reader.GetInt64(0);
        var bytes = reader.GetInt64(1);
        var oldest = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? durableIngressUtc
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        var age = _utcNow() - oldest;
        var hard = count + 1 > _distributionOptions.OptionalMaximumPendingCount ||
                   bytes + payloadLength > _distributionOptions.OptionalMaximumPendingBytes ||
                   age >= TimeSpan.FromMinutes(_distributionOptions.OptionalMaximumOldestAgeMinutes);
        using var readPressure = connection.CreateCommand();
        readPressure.Transaction = transaction;
        readPressure.CommandText = "SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = $lane;";
        readPressure.Parameters.AddWithValue("$lane", lane);
        var prior = Convert.ToInt32(
            await readPressure.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        var recovered = count * 100 < _distributionOptions.OptionalMaximumPendingCount * _distributionOptions.PressureRecoveryPercent &&
                        bytes * 100 < _distributionOptions.OptionalMaximumPendingBytes * _distributionOptions.PressureRecoveryPercent &&
                        age.TotalMinutes * 100 < _distributionOptions.OptionalMaximumOldestAgeMinutes * _distributionOptions.PressureRecoveryPercent;
        var warning = count * 100 >= _distributionOptions.OptionalMaximumPendingCount * _distributionOptions.PressureRecoveryPercent ||
                      bytes * 100 >= _distributionOptions.OptionalMaximumPendingBytes * _distributionOptions.PressureRecoveryPercent;
        var next = hard || prior == 2 && !recovered ? 2 : warning ? 1 : 0;
        using var updatePressure = connection.CreateCommand();
        updatePressure.Transaction = transaction;
        updatePressure.CommandText = "UPDATE capture_lane_definitions SET pressure_state = $pressure WHERE lane_name = $lane;";
        updatePressure.Parameters.AddWithValue("$pressure", next);
        updatePressure.Parameters.AddWithValue("$lane", lane);
        await updatePressure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return next == 2;
    }

    internal Task<RawIngressOutcome> RecoverAsync(RawIngressJournalEntry entry, CancellationToken cancellationToken)
        => RecoverAsync(entry, DefaultLaneDefinitions, cancellationToken);

    internal Task<RawIngressOutcome> RecoverAsync(
        RawIngressJournalEntry entry,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
        => TrackTransactionAsync("recovery", () => RecoverCoreAsync(entry, laneDefinitions, cancellationToken));

    private async Task<RawIngressOutcome> RecoverCoreAsync(
        RawIngressJournalEntry entry,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var claims = await ReadClaimsAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        if (claims.Count > 0)
        {
            if (claims.Count == 1 && IsExact(claims[0], entry))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RawIngressOutcome.Existing;
            }
            throw new RawIngressConflictException("Recovered raw evidence conflicts with committed immutable identity.");
        }

        using (var existingAssignment = connection.CreateCommand())
        {
            existingAssignment.Transaction = transaction;
            existingAssignment.CommandText = """
                SELECT capture_id, raw_artifact_id, agent_id, capture_sequence
                FROM raw_capture_assignments
                WHERE capture_id = $capture_id OR raw_artifact_id = $artifact_id
                   OR (agent_id = $agent_id AND capture_sequence = $capture_sequence);
                """;
            existingAssignment.Parameters.AddWithValue("$capture_id", entry.CaptureId.ToString("N"));
            existingAssignment.Parameters.AddWithValue("$artifact_id", entry.ArtifactId.ToString("N"));
            existingAssignment.Parameters.AddWithValue("$agent_id", entry.AgentId);
            existingAssignment.Parameters.AddWithValue("$capture_sequence", entry.CaptureSequence);
            using var reader = await existingAssignment.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                (Guid.ParseExact(reader.GetString(0), "N") != entry.CaptureId ||
                 Guid.ParseExact(reader.GetString(1), "N") != entry.ArtifactId ||
                 !string.Equals(reader.GetString(2), entry.AgentId, StringComparison.Ordinal) ||
                 reader.GetInt64(3) != entry.CaptureSequence))
            {
                throw new RawIngressConflictException("Recovered raw evidence conflicts with its reserved identity assignment.");
            }
        }

        using (var sequence = connection.CreateCommand())
        {
            sequence.Transaction = transaction;
            sequence.CommandText = """
                INSERT INTO raw_capture_sequences(agent_id, last_sequence)
                VALUES ($agent_id, $capture_sequence)
                ON CONFLICT(agent_id) DO UPDATE SET last_sequence = MAX(last_sequence, excluded.last_sequence);
                """;
            sequence.Parameters.AddWithValue("$agent_id", entry.AgentId);
            sequence.Parameters.AddWithValue("$capture_sequence", entry.CaptureSequence);
            await sequence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var assignment = connection.CreateCommand())
        {
            assignment.Transaction = transaction;
            assignment.CommandText = """
                INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
                VALUES ($capture_id, $artifact_id, $agent_id, $capture_sequence)
                ON CONFLICT(capture_id) DO NOTHING;
                """;
            assignment.Parameters.AddWithValue("$capture_id", entry.CaptureId.ToString("N"));
            assignment.Parameters.AddWithValue("$artifact_id", entry.ArtifactId.ToString("N"));
            assignment.Parameters.AddWithValue("$agent_id", entry.AgentId);
            assignment.Parameters.AddWithValue("$capture_sequence", entry.CaptureSequence);
            await assignment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = InsertCaptureSql;
            AddEntryParameters(insert, entry);
            insert.Parameters.AddWithValue("$committed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var recoveredRowId = await ReadLastInsertRowIdAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await InsertLaneWorkAsync(
            connection, transaction, recoveredRowId, entry, laneDefinitions, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RawIngressOutcome.Committed;
    }

    internal Task MarkEvidenceFailureAsync(
        Guid captureId,
        string state,
        string reason,
        CancellationToken cancellationToken)
        => TrackWriteAsync(
            "evidence-state",
            () => MarkEvidenceFailureCoreAsync(captureId, state, reason, cancellationToken));

    private async Task MarkEvidenceFailureCoreAsync(
        Guid captureId,
        string state,
        string reason,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE raw_captures
            SET state = $state, failure_reason = $reason, retention_hold = 1
            WHERE capture_id = $capture_id;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Raw ingress evidence failure did not identify exactly one capture.");
        }
    }

    internal Task MarkCommittedAsync(Guid captureId, CancellationToken cancellationToken)
        => TrackWriteAsync("evidence-repair", () => MarkCommittedCoreAsync(captureId, cancellationToken));

    private async Task MarkCommittedCoreAsync(Guid captureId, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE raw_captures
            SET state = 'committed', failure_reason = NULL
            WHERE capture_id = $capture_id;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Raw ingress repair did not identify exactly one capture.");
        }
    }

    internal async Task<(long QuarantineCount, long QuarantineBytes, long FailureCount)> ReadHealthTotalsAsync(
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM raw_ingress_reconciliation WHERE outcome = 'quarantined'),
                (SELECT COALESCE(SUM(observed_bytes), 0) FROM raw_ingress_reconciliation WHERE outcome = 'quarantined'),
                (SELECT COUNT(*) FROM raw_captures WHERE state != 'committed');
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    internal Task PlanQuarantineAsync(
        RawIngressPlannedQuarantine operation,
        CancellationToken cancellationToken)
        => TrackWriteAsync("reconciliation-plan", () => PlanQuarantineCoreAsync(operation, cancellationToken));

    private async Task PlanQuarantineCoreAsync(
        RawIngressPlannedQuarantine operation,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raw_ingress_reconciliation(
                evidence_key, source_relative_path, companion_relative_path, quarantine_relative_path,
                outcome, reason, operation_state, observed_bytes, observed_unix_ms, completed_unix_ms)
            VALUES ($key, $source, $companion, $quarantine, 'quarantined', $reason, 'planned', $bytes, $observed, NULL);
            """;
        command.Parameters.AddWithValue("$key", operation.EvidenceKey);
        command.Parameters.AddWithValue("$source", operation.SourceRelativePath);
        command.Parameters.AddWithValue("$companion", (object?)operation.CompanionRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$quarantine", operation.QuarantineRelativePath);
        command.Parameters.AddWithValue("$reason", operation.Reason);
        command.Parameters.AddWithValue("$bytes", operation.ObservedBytes);
        command.Parameters.AddWithValue("$observed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task PlanCleanupAsync(
        string evidenceKey,
        string sourceRelativePath,
        long observedBytes,
        CancellationToken cancellationToken)
        => TrackWriteAsync(
            "reconciliation-plan",
            () => PlanCleanupCoreAsync(evidenceKey, sourceRelativePath, observedBytes, cancellationToken));

    private async Task PlanCleanupCoreAsync(
        string evidenceKey,
        string sourceRelativePath,
        long observedBytes,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raw_ingress_reconciliation(
                evidence_key, source_relative_path, companion_relative_path, quarantine_relative_path,
                outcome, reason, operation_state, observed_bytes, observed_unix_ms, completed_unix_ms)
            VALUES ($key, $source, NULL, NULL, 'cleaned', 'stale-temporary', 'planned', $bytes, $observed, NULL);
            """;
        command.Parameters.AddWithValue("$key", evidenceKey);
        command.Parameters.AddWithValue("$source", sourceRelativePath);
        command.Parameters.AddWithValue("$bytes", observedBytes);
        command.Parameters.AddWithValue("$observed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<KeyValuePair<string, string>>> ReadPlannedCleanupsAsync(
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence_key, source_relative_path
            FROM raw_ingress_reconciliation
            WHERE outcome = 'cleaned' AND operation_state = 'planned'
            ORDER BY reconciliation_id;
            """;
        var operations = new List<KeyValuePair<string, string>>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            operations.Add(new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));
        }
        return operations;
    }

    internal async Task<IReadOnlyList<RawIngressPlannedQuarantine>> ReadPlannedQuarantinesAsync(
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence_key, source_relative_path, companion_relative_path,
                   quarantine_relative_path, reason, observed_bytes
            FROM raw_ingress_reconciliation
            WHERE outcome = 'quarantined' AND operation_state = 'planned'
            ORDER BY reconciliation_id;
            """;
        var operations = new List<RawIngressPlannedQuarantine>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            operations.Add(new RawIngressPlannedQuarantine(
                reader.GetString(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5)));
        }
        return operations;
    }

    internal Task CompleteReconciliationAsync(string evidenceKey, CancellationToken cancellationToken)
        => TrackWriteAsync("reconciliation-complete", () => CompleteReconciliationCoreAsync(evidenceKey, cancellationToken));

    private async Task CompleteReconciliationCoreAsync(string evidenceKey, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE raw_ingress_reconciliation
            SET operation_state = 'completed', completed_unix_ms = $completed
            WHERE evidence_key = $key AND operation_state = 'planned';
            """;
        command.Parameters.AddWithValue("$key", evidenceKey);
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Planned raw ingress reconciliation operation was not completed exactly once.");
        }
    }

    internal async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 0)
            {
                throw new IOException("Raw ingress WAL checkpoint could not complete without a busy writer.");
            }
            _checkpointRecorder?.Invoke(true);
        }
        catch
        {
            _checkpointRecorder?.Invoke(false);
            throw;
        }
    }

    private async Task<T> TrackTransactionAsync<T>(string operation, Func<Task<T>> transaction)
    {
        try
        {
            var result = await transaction().ConfigureAwait(false);
            _transactionRecorder?.Invoke(operation, true);
            return result;
        }
        catch
        {
            _transactionRecorder?.Invoke(operation, false);
            throw;
        }
    }

    private async Task TrackWriteAsync(string operation, Func<Task> transaction)
    {
        try
        {
            await transaction().ConfigureAwait(false);
            _transactionRecorder?.Invoke(operation, true);
        }
        catch
        {
            _transactionRecorder?.Invoke(operation, false);
            throw;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        EnsureDatabaseFilesArePhysical();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = _busyTimeoutSeconds
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        EnsureDatabaseFilesArePhysical();
        await ExecuteNonQueryAsync(connection, null, $"PRAGMA busy_timeout = {_busyTimeoutSeconds * 1000};", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        var journalMode = await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Raw ingress SQLite journal could not enter WAL mode.");
        }
        await ExecuteNonQueryAsync(connection, null, "PRAGMA synchronous = FULL;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA wal_autocheckpoint = 1000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void EnsureDatabaseFilesArePhysical()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(_databasePath)!)!;
        RawIngressFileStore.EnsureNoSymbolicLinks(root, _databasePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(_databasePath, "-wal"));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(_databasePath, "-shm"));
    }

    private SqliteTransaction BeginImmediate(SqliteConnection connection)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
            return connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        }
        finally
        {
            _lockWaitRecorder?.Invoke(System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task VerifyConnectionSettingsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var synchronous = await ExecuteScalarLongAsync(connection, "PRAGMA synchronous;", cancellationToken).ConfigureAwait(false);
        var foreignKeys = await ExecuteScalarLongAsync(connection, "PRAGMA foreign_keys;", cancellationToken).ConfigureAwait(false);
        var busyTimeout = await ExecuteScalarLongAsync(connection, "PRAGMA busy_timeout;", cancellationToken).ConfigureAwait(false);
        var autoCheckpoint = await ExecuteScalarLongAsync(connection, "PRAGMA wal_autocheckpoint;", cancellationToken).ConfigureAwait(false);
        if (synchronous != 2 || foreignKeys != 1 || busyTimeout != _busyTimeoutSeconds * 1000L || autoCheckpoint != 1000)
        {
            throw new InvalidDataException("Raw ingress SQLite connection settings do not match the durability policy.");
        }
    }

    private static async Task<List<RawIngressJournalEntry>> ReadClaimsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RawIngressJournalEntry entry,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT agent_id, capture_sequence, capture_id, raw_artifact_id,
                   descriptor_sha256, manifest_sha256, payload_sha256, payload_length,
                   payload_relative_path, sidecar_relative_path, manifest_json,
                   exposure_started_unix_ms, durable_ingress_unix_ms, state, retention_hold
            FROM raw_captures
            WHERE capture_id = $capture_id
               OR raw_artifact_id = $artifact_id
               OR descriptor_sha256 = $descriptor_sha256
               OR payload_relative_path = $payload_path
               OR sidecar_relative_path = $sidecar_path;
            """;
        AddEntryParameters(command, entry);
        var claims = new List<RawIngressJournalEntry>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claims.Add(ReadEntry(reader));
        }
        return claims;
    }

    private static RawIngressJournalEntry ReadEntry(SqliteDataReader reader)
        => new(
            reader.GetString(0), reader.GetInt64(1), Guid.ParseExact(reader.GetString(2), "N"),
            Guid.ParseExact(reader.GetString(3), "N"), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetInt64(7), reader.GetString(8), reader.GetString(9),
            (byte[])reader[10], DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)), reader.GetString(13), reader.GetBoolean(14));

    private static bool IsExact(RawIngressJournalEntry left, RawIngressJournalEntry right)
        => left == right ||
           left.AgentId == right.AgentId && left.CaptureSequence == right.CaptureSequence &&
           left.CaptureId == right.CaptureId && left.ArtifactId == right.ArtifactId &&
           left.DescriptorSha256 == right.DescriptorSha256 && left.ManifestSha256 == right.ManifestSha256 &&
           left.PayloadSha256 == right.PayloadSha256 && left.PayloadLength == right.PayloadLength &&
           left.PayloadRelativePath == right.PayloadRelativePath && left.SidecarRelativePath == right.SidecarRelativePath &&
           left.ManifestJson.AsSpan().SequenceEqual(right.ManifestJson) &&
           left.ExposureStartedUtc == right.ExposureStartedUtc && left.DurableIngressUtc == right.DurableIngressUtc &&
           left.State == right.State;

    private static void AddEntryParameters(SqliteCommand command, RawIngressJournalEntry entry)
    {
        command.Parameters.AddWithValue("$capture_id", entry.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$artifact_id", entry.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$agent_id", entry.AgentId);
        command.Parameters.AddWithValue("$capture_sequence", entry.CaptureSequence);
        command.Parameters.AddWithValue("$descriptor_sha256", entry.DescriptorSha256);
        command.Parameters.AddWithValue("$manifest_sha256", entry.ManifestSha256);
        command.Parameters.AddWithValue("$payload_sha256", entry.PayloadSha256);
        command.Parameters.AddWithValue("$payload_length", entry.PayloadLength);
        command.Parameters.AddWithValue("$payload_path", entry.PayloadRelativePath);
        command.Parameters.AddWithValue("$sidecar_path", entry.SidecarRelativePath);
        command.Parameters.AddWithValue("$manifest_json", entry.ManifestJson);
        command.Parameters.AddWithValue("$exposure_started", entry.ExposureStartedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$durable_ingress", entry.DurableIngressUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$state", entry.State);
    }

    private static async Task<long> ExecuteScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
        => Convert.ToInt64(await ExecuteScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> ExecuteScalarStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
        => Convert.ToString(await ExecuteScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only internal constant schema and PRAGMA statements are passed to this helper.")]
    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyColumnsAsync(
        SqliteConnection connection,
        string table,
        string expected,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(name, ',') FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);
        var actual = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Raw ingress SQLite table '{table}' has unexpected columns.");
        }
    }

    private static async Task VerifyIndexAsync(
        SqliteConnection connection,
        string index,
        string expected,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(name, ',') FROM pragma_index_info($index);";
        command.Parameters.AddWithValue("$index", index);
        var actual = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Raw ingress SQLite index '{index}' has unexpected columns.");
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only internal constant schema and PRAGMA statements are passed to this helper.")]
    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static readonly IReadOnlyList<CaptureLaneDefinition> DefaultLaneDefinitions =
    [
        new CaptureLaneDefinition(
            "standard",
            Enabled: true,
            Required: true,
            Ordered: true,
            PolicySha256: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("v1\nstandard\nTrue\nTrue\nTrue"))))
    ];

    private const string RawCaptureColumns =
        "raw_capture_row_id,capture_id,raw_artifact_id,agent_id,capture_sequence,descriptor_sha256,manifest_sha256,payload_sha256,payload_length,payload_relative_path,sidecar_relative_path,manifest_json,exposure_started_unix_ms,durable_ingress_unix_ms,committed_unix_ms,state,retention_hold,failure_reason";
    private const string LaneDefinitionColumns =
        "lane_name,enabled,required,ordered,policy_sha256,pressure_state,created_unix_ms,updated_unix_ms";
    private const string LaneContextColumns =
        "raw_capture_row_id,context_json,context_sha256,context_source";
    private const string LaneWorkColumns =
        "work_id,raw_capture_row_id,lane_name,agent_id,capture_sequence,required,ordered,state,attempt_count,available_unix_ms,lease_token,lease_owner,lease_expires_unix_ms,completion_token,completed_unix_ms,failure_reason,created_unix_ms,updated_unix_ms";

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS raw_capture_sequences (
            agent_id TEXT PRIMARY KEY,
            last_sequence INTEGER NOT NULL CHECK (last_sequence >= 0)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS raw_capture_assignments (
            capture_id TEXT PRIMARY KEY,
            raw_artifact_id TEXT NOT NULL UNIQUE,
            agent_id TEXT NOT NULL,
            capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
            UNIQUE (agent_id, capture_sequence)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS raw_captures (
            raw_capture_row_id INTEGER PRIMARY KEY,
            capture_id TEXT NOT NULL UNIQUE,
            raw_artifact_id TEXT NOT NULL UNIQUE,
            agent_id TEXT NOT NULL,
            capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
            descriptor_sha256 TEXT NOT NULL UNIQUE CHECK (length(descriptor_sha256) = 64),
            manifest_sha256 TEXT NOT NULL CHECK (length(manifest_sha256) = 64),
            payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
            payload_length INTEGER NOT NULL CHECK (payload_length >= 0),
            payload_relative_path TEXT NOT NULL UNIQUE,
            sidecar_relative_path TEXT NOT NULL UNIQUE,
            manifest_json BLOB NOT NULL,
            exposure_started_unix_ms INTEGER NOT NULL,
            durable_ingress_unix_ms INTEGER NOT NULL,
            committed_unix_ms INTEGER NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('committed', 'missing_evidence', 'quarantined')),
            retention_hold INTEGER NOT NULL DEFAULT 1 CHECK (retention_hold IN (0, 1)),
            failure_reason TEXT,
            UNIQUE (agent_id, capture_sequence),
            FOREIGN KEY (capture_id) REFERENCES raw_capture_assignments(capture_id)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_raw_captures_discovery ON raw_captures(state, agent_id, capture_sequence);
        CREATE INDEX IF NOT EXISTS ix_raw_captures_backlog ON raw_captures(state, durable_ingress_unix_ms);
        CREATE INDEX IF NOT EXISTS ix_raw_captures_retention ON raw_captures(retention_hold, exposure_started_unix_ms);
        CREATE TABLE IF NOT EXISTS raw_ingress_reconciliation (
            reconciliation_id INTEGER PRIMARY KEY,
            evidence_key TEXT NOT NULL UNIQUE,
            source_relative_path TEXT NOT NULL,
            companion_relative_path TEXT,
            quarantine_relative_path TEXT,
            outcome TEXT NOT NULL CHECK (outcome IN ('cleaned', 'quarantined')),
            reason TEXT NOT NULL,
            operation_state TEXT NOT NULL CHECK (operation_state IN ('planned', 'completed')),
            observed_bytes INTEGER NOT NULL DEFAULT 0,
            observed_unix_ms INTEGER NOT NULL,
            completed_unix_ms INTEGER
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_lane_definitions (
            lane_name TEXT PRIMARY KEY,
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            ordered INTEGER NOT NULL CHECK (ordered IN (0, 1)),
            policy_sha256 TEXT NOT NULL CHECK (length(policy_sha256) = 64),
            pressure_state INTEGER NOT NULL DEFAULT 0 CHECK (pressure_state IN (0, 1, 2)),
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_lane_contexts (
            raw_capture_row_id INTEGER PRIMARY KEY,
            context_json BLOB NOT NULL,
            context_sha256 TEXT NOT NULL CHECK (length(context_sha256) = 64),
            context_source TEXT NOT NULL CHECK (context_source IN ('capture', 'manifest-fallback')),
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_lane_work (
            work_id INTEGER PRIMARY KEY,
             raw_capture_row_id INTEGER NOT NULL,
             lane_name TEXT NOT NULL,
             agent_id TEXT NOT NULL,
             capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            ordered INTEGER NOT NULL CHECK (ordered IN (0, 1)),
            state TEXT NOT NULL CHECK (state IN ('pending', 'leased', 'retry_wait', 'completed', 'quarantined', 'abandoned')),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            available_unix_ms INTEGER NOT NULL,
            lease_token TEXT,
            lease_owner TEXT,
            lease_expires_unix_ms INTEGER,
            completion_token TEXT,
            completed_unix_ms INTEGER,
            failure_reason TEXT,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            UNIQUE (raw_capture_row_id, lane_name),
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE,
            FOREIGN KEY (lane_name) REFERENCES capture_lane_definitions(lane_name)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_claim
            ON capture_lane_work(lane_name, state, available_unix_ms, work_id);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_lease
            ON capture_lane_work(state, lease_expires_unix_ms);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_backlog
            ON capture_lane_work(lane_name, state, created_unix_ms);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_raw
            ON capture_lane_work(raw_capture_row_id, required, state);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_ordered
            ON capture_lane_work(lane_name, agent_id, capture_sequence)
            WHERE state NOT IN ('completed', 'abandoned');
        """;

    private const string LaneSchemaSql = """
        CREATE TABLE IF NOT EXISTS capture_lane_definitions (
            lane_name TEXT PRIMARY KEY,
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            ordered INTEGER NOT NULL CHECK (ordered IN (0, 1)),
            policy_sha256 TEXT NOT NULL CHECK (length(policy_sha256) = 64),
            pressure_state INTEGER NOT NULL DEFAULT 0 CHECK (pressure_state IN (0, 1, 2)),
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_lane_contexts (
            raw_capture_row_id INTEGER PRIMARY KEY,
            context_json BLOB NOT NULL,
            context_sha256 TEXT NOT NULL CHECK (length(context_sha256) = 64),
            context_source TEXT NOT NULL CHECK (context_source IN ('capture', 'manifest-fallback')),
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE
        ) STRICT;
        CREATE TABLE IF NOT EXISTS capture_lane_work (
            work_id INTEGER PRIMARY KEY,
             raw_capture_row_id INTEGER NOT NULL,
             lane_name TEXT NOT NULL,
             agent_id TEXT NOT NULL,
             capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
            required INTEGER NOT NULL CHECK (required IN (0, 1)),
            ordered INTEGER NOT NULL CHECK (ordered IN (0, 1)),
            state TEXT NOT NULL CHECK (state IN ('pending', 'leased', 'retry_wait', 'completed', 'quarantined', 'abandoned')),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            available_unix_ms INTEGER NOT NULL,
            lease_token TEXT,
            lease_owner TEXT,
            lease_expires_unix_ms INTEGER,
            completion_token TEXT,
            completed_unix_ms INTEGER,
            failure_reason TEXT,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            UNIQUE (raw_capture_row_id, lane_name),
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE,
            FOREIGN KEY (lane_name) REFERENCES capture_lane_definitions(lane_name)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_claim
            ON capture_lane_work(lane_name, state, available_unix_ms, work_id);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_lease
            ON capture_lane_work(state, lease_expires_unix_ms);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_backlog
            ON capture_lane_work(lane_name, state, created_unix_ms);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_raw
            ON capture_lane_work(raw_capture_row_id, required, state);
        CREATE INDEX IF NOT EXISTS ix_capture_lane_work_ordered
            ON capture_lane_work(lane_name, agent_id, capture_sequence)
            WHERE state NOT IN ('completed', 'abandoned');
        """;

    private const string InsertCaptureSql = """
        INSERT INTO raw_captures(
            capture_id, raw_artifact_id, agent_id, capture_sequence,
            descriptor_sha256, manifest_sha256, payload_sha256, payload_length,
            payload_relative_path, sidecar_relative_path, manifest_json,
            exposure_started_unix_ms, durable_ingress_unix_ms, committed_unix_ms,
            state, retention_hold)
        VALUES (
            $capture_id, $artifact_id, $agent_id, $capture_sequence,
            $descriptor_sha256, $manifest_sha256, $payload_sha256, $payload_length,
            $payload_path, $sidecar_path, $manifest_json,
            $exposure_started, $durable_ingress, $committed,
            $state, 1);
        """;
}

[Serializable]
internal sealed class RawIngressConflictException : InvalidOperationException
{
    internal RawIngressConflictException()
    {
    }

    internal RawIngressConflictException(string message)
        : base(message)
    {
    }

    internal RawIngressConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
