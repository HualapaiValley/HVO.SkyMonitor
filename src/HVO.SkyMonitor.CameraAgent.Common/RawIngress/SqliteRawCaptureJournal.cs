using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed class SqliteRawCaptureJournal(
    string databasePath,
    int busyTimeoutSeconds,
    Action<TimeSpan>? lockWaitRecorder = null,
    IRawIngressFaultInjector? faultInjector = null,
    Action<string, bool>? transactionRecorder = null,
    Action<bool>? checkpointRecorder = null)
{
    internal const int CurrentSchemaVersion = 1;
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly int _busyTimeoutSeconds = busyTimeoutSeconds;
    private readonly Action<TimeSpan>? _lockWaitRecorder = lockWaitRecorder;
    private readonly IRawIngressFaultInjector _faultInjector = faultInjector ?? new NullRawIngressFaultInjector();
    private readonly Action<string, bool>? _transactionRecorder = transactionRecorder;
    private readonly Action<bool>? _checkpointRecorder = checkpointRecorder;

    internal string DatabasePath => _databasePath;

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        EnsureDatabaseFilesArePhysical();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        EnsureDatabaseFilesArePhysical();

        var version = await ExecuteScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Raw ingress schema {version} is newer than supported schema {CurrentSchemaVersion}.");
        }
        if (version == 0)
        {
            try
            {
                using var transaction = BeginImmediate(connection);
                _faultInjector.Inject(RawIngressFaultPoint.AfterMigrationTransactionBegan);
                await ExecuteNonQueryAsync(connection, transaction, SchemaSql, cancellationToken).ConfigureAwait(false);
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

        var integrity = await ExecuteScalarStringAsync(connection, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Raw ingress SQLite integrity check failed.");
        }
        var schemaObjectCount = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*) FROM sqlite_master
            WHERE name IN (
                'raw_capture_sequences', 'raw_capture_assignments', 'raw_captures', 'raw_ingress_reconciliation',
                'ix_raw_captures_discovery', 'ix_raw_captures_backlog', 'ix_raw_captures_retention');
            """, cancellationToken).ConfigureAwait(false);
        if (schemaObjectCount != 7)
        {
            throw new InvalidDataException("Raw ingress SQLite schema is incomplete or drifted.");
        }
        var captureColumnCount = await ExecuteScalarLongAsync(
            connection, "SELECT COUNT(*) FROM pragma_table_info('raw_captures');", cancellationToken).ConfigureAwait(false);
        if (captureColumnCount != 18)
        {
            throw new InvalidDataException("Raw ingress SQLite capture schema has unexpected columns.");
        }
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
        => TrackTransactionAsync("commit", () => CommitCoreAsync(entry, cancellationToken));

    private async Task<RawIngressOutcome> CommitCoreAsync(RawIngressJournalEntry entry, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        _faultInjector.Inject(RawIngressFaultPoint.AfterJournalTransactionBegan);
        var claims = await ReadClaimsAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        if (claims.Count > 0)
        {
            if (claims.Count == 1 && IsExact(claims[0], entry))
            {
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
                   exposure_started_unix_ms, durable_ingress_unix_ms, state
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

    internal Task<RawIngressOutcome> RecoverAsync(RawIngressJournalEntry entry, CancellationToken cancellationToken)
        => TrackTransactionAsync("recovery", () => RecoverCoreAsync(entry, cancellationToken));

    private async Task<RawIngressOutcome> RecoverCoreAsync(RawIngressJournalEntry entry, CancellationToken cancellationToken)
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
                   exposure_started_unix_ms, durable_ingress_unix_ms, state
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
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)), reader.GetString(13));

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
