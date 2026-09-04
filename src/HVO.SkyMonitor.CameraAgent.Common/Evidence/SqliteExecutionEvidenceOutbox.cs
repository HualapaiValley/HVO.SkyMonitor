using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Evidence;

/// <summary>
/// SQLite WAL outbox for immutable graph-execution evidence, stored in its own database file beside the raw-ingress
/// journal rather than inside it. Keeping the file separate means a rollback to a baseline image that predates this
/// lane still opens every store it knows about: the baseline simply never opens this one, and no schema this
/// milestone already shipped changes shape.
/// </summary>
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Microsoft.Data.Sqlite field getters are in-memory accessors after an asynchronous row read.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Dynamically selected query fragments are internal constants; all values remain parameterized.")]
public sealed class SqliteExecutionEvidenceOutbox(
    TimeProvider? timeProvider = null,
    int busyTimeoutSeconds = 5) : IExecutionEvidenceOutbox, IDisposable
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumOperationReceipts = 10_000;
    internal const int MaximumRetainedConflicts = 1_000;
    internal const int MaximumRetainedRejections = 1_000;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly int _busyTimeoutSeconds = busyTimeoutSeconds;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initializationGates = new(PathComparer);
    private readonly HashSet<string> _initializedRoots = new(PathComparer);
    private readonly object _initializedLock = new();

    public async ValueTask InitializeAsync(string root, CancellationToken cancellationToken)
    {
        using var activity = ExecutionEvidenceExportTelemetry.ActivitySource.StartActivity("evidence-export.initialize");
        root = NormalizeRoot(root);
        lock (_initializedLock)
        {
            if (_initializedRoots.Contains(root))
            {
                return;
            }
        }

        var gate = _initializationGates.GetOrAdd(root, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_initializedLock)
            {
                if (_initializedRoots.Contains(root))
                {
                    return;
                }
            }

            Directory.CreateDirectory(EvidenceDirectory(root));
            EnsureDatabaseFilesArePhysical(root);
            using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
            EnsureDatabaseFilesArePhysical(root);
            // Opening the connection materialises a valid but schemaless database file, so the file's existence is
            // not evidence that the schema was ever committed. A crash between the two would otherwise leave an
            // empty file that every later start rejects as drifted, bricking the lane permanently. The decision is
            // therefore taken from sqlite_master inside the write transaction.
            using (var transaction = BeginImmediate(connection))
            {
                if (await CountSchemaObjectsAsync(connection, transaction, cancellationToken).ConfigureAwait(false) == 0)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = SchemaSql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    await EnsureCanonicalSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
                }
            }

            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                var result = Convert.ToString(
                    await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (!string.Equals(result, "ok", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Execution evidence outbox SQLite integrity check failed.");
                }
            }

            lock (_initializedLock)
            {
                _initializedRoots.Add(root);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<ExecutionEvidenceOriginRecord> EnsureOriginAsync(
        string root,
        ExecutionEvidenceOriginV1 origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!IsSha256(origin.IdentitySha256) ||
            !string.Equals(origin.IdentitySha256, GraphExecutionEvidenceJson.ComputeOriginIdentitySha256(origin), StringComparison.Ordinal))
        {
            throw new ArgumentException("The origin identity is not the canonical hash of its own members.", nameof(origin));
        }

        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var stored = await ReadOriginAsync(connection, transaction, origin.IdentitySha256, cancellationToken)
            .ConfigureAwait(false);
        if (stored is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return stored;
        }

        var now = _timeProvider.GetUtcNow();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO execution_evidence_origins(
                    origin_identity_sha256, origin_installation_id, agent_instance_id, boot_session_id,
                    software_version, observatory_id, logical_camera_installation_id, installation_public_id,
                    next_sequence, acknowledged_through_sequence, created_unix_ms)
                VALUES ($identity, $installation, $agent, $boot, $software, $observatory, $logical, $public, 1, 0, $created);
                """;
            insert.Parameters.AddWithValue("$identity", origin.IdentitySha256);
            insert.Parameters.AddWithValue("$installation", Compact(origin.OriginInstallationId));
            insert.Parameters.AddWithValue("$agent", Compact(origin.AgentInstanceId));
            insert.Parameters.AddWithValue("$boot", Compact(origin.BootSessionId));
            insert.Parameters.AddWithValue("$software", Bound(origin.SoftwareVersion, 64));
            insert.Parameters.AddWithValue("$observatory", Compact(origin.ObservatoryId));
            insert.Parameters.AddWithValue("$logical", Compact(origin.LogicalCameraInstallationId));
            insert.Parameters.AddWithValue("$public", Compact(origin.InstallationPublicId));
            insert.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            origin.IdentitySha256,
            origin.OriginInstallationId,
            origin.AgentInstanceId,
            origin.BootSessionId,
            origin.SoftwareVersion,
            origin.ObservatoryId,
            origin.LogicalCameraInstallationId,
            origin.InstallationPublicId,
            1,
            0,
            now);
    }

    public async ValueTask<ExecutionEvidenceDiscoveryCursor> ReadDiscoveryCursorAsync(
        string root,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        return await ReadCursorAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ExecutionEvidenceEnlistmentResult> EnlistAsync(
        string root,
        string originIdentitySha256,
        Guid executionId,
        IReadOnlyList<ExecutionEvidenceEnlistmentUnit> units,
        ExecutionEvidenceDiscoveryCursor cursor,
        ExecutionEvidenceEnlistmentLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(limits);
        EnsureSha256(originIdentitySha256, nameof(originIdentitySha256));
        using var activity = ExecutionEvidenceExportTelemetry.ActivitySource.StartActivity("evidence-export.enlist");
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);

        var stored = await ReadCursorAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (cursor.TerminalUnixMs < stored.TerminalUnixMs ||
            (cursor.TerminalUnixMs == stored.TerminalUnixMs &&
                string.CompareOrdinal(cursor.ExecutionId, stored.ExecutionId) < 0))
        {
            throw new InvalidOperationException("The evidence discovery cursor may not move backwards.");
        }

        var backlog = await ReadBacklogAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var storageBytes = DatabaseBytes(root);
        if (backlog.PendingCount + backlog.RetryCount + units.Count > limits.MaximumPendingUnits ||
            storageBytes > limits.MaximumStorageBytes)
        {
            // Refuse at the enlistment boundary. The source store still holds every immutable fact, the cursor does
            // not advance, and nothing already enlisted is discarded, so overflow defers work instead of losing it.
            return new(
                ExecutionEvidenceEnlistmentDisposition.Saturated,
                0,
                backlog.HighestSequence,
                storageBytes > limits.MaximumStorageBytes
                    ? ExecutionEvidenceExportReasonCodes.StorageSaturated
                    : ExecutionEvidenceExportReasonCodes.BacklogSaturated);
        }

        var nextSequence = await ReadNextSequenceAsync(connection, transaction, originIdentitySha256, cancellationToken)
            .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var pendingBytes = backlog.PendingBytes;
        var highest = nextSequence - 1;

        // Every unit is sealed and checked before anything is written, so a refusal in the middle of a batch can
        // never leave a partially enlisted batch behind or strand the sequences it had already consumed.
        var prepared = new List<(
            string UnitKey,
            ExecutionEvidenceBodyKind Kind,
            long Sequence,
            ExecutionEvidenceSealedUnit Sealed,
            byte[] Payload)>(units.Count);
        foreach (var unit in units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The key is bounded once and reused, so the existence check and the insert can never disagree about
            // which key this unit owns.
            string unitKey;
            try
            {
                unitKey = Bound(unit.UnitKey, 128);
            }
            catch (ArgumentException)
            {
                return await RejectAsync(
                    connection, transaction, cursor, executionId, backlog.HighestSequence,
                    GraphExecutionEvidenceReasonCodes.InvalidBody, cancellationToken).ConfigureAwait(false);
            }
            var storedUnit = await ReadUnitKeyAsync(
                connection, transaction, originIdentitySha256, unitKey, cancellationToken).ConfigureAwait(false);
            var sequence = storedUnit?.Sequence ?? nextSequence++;
            ExecutionEvidenceSealedUnit sealedUnit;
            try
            {
                sealedUnit = unit.Seal(sequence);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException)
            {
                // A durable row that cannot be sealed into a valid unit of this contract version can never be
                // exported. Refusing it here keeps the failure inside the export lane: the cursor still advances,
                // the loss is counted durably, and the host is never faulted by one unexportable execution.
                return await RejectAsync(
                    connection, transaction, cursor, executionId, backlog.HighestSequence,
                    GraphExecutionEvidenceReasonCodes.InvalidBody, cancellationToken).ConfigureAwait(false);
            }
            var payload = sealedUnit.Payload.ToArray();
            if (!IsSha256(sealedUnit.PayloadSha256))
            {
                return await RejectAsync(
                    connection, transaction, cursor, executionId, backlog.HighestSequence,
                    GraphExecutionEvidenceReasonCodes.InvalidHash, cancellationToken).ConfigureAwait(false);
            }
            if (payload.Length > limits.MaximumUnitBytes)
            {
                return await RejectAsync(
                    connection, transaction, cursor, executionId, backlog.HighestSequence,
                    GraphExecutionEvidenceReasonCodes.PayloadTooLarge, cancellationToken).ConfigureAwait(false);
            }

            if (storedUnit is { } existing)
            {
                // The key is already durable. Identical canonical bytes are idempotent; different bytes mean the
                // projection now produces something else for a fact that was already sealed, and the stored unit
                // stays authoritative while the disagreement is recorded for an operator.
                if (!string.Equals(existing.PayloadSha256, sealedUnit.PayloadSha256, StringComparison.Ordinal))
                {
                    await InsertConflictAsync(
                        connection, transaction, originIdentitySha256, sequence, existing.PayloadSha256,
                        sealedUnit.PayloadSha256, GraphExecutionEvidenceReasonCodes.SequenceConflict,
                        now, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            pendingBytes += payload.Length;
            if (pendingBytes > limits.MaximumPendingBytes)
            {
                return new(
                    ExecutionEvidenceEnlistmentDisposition.Saturated,
                    0,
                    backlog.HighestSequence,
                    ExecutionEvidenceExportReasonCodes.BacklogSaturated);
            }

            prepared.Add((unitKey, unit.Kind, sequence, sealedUnit, payload));
            highest = sequence;
        }

        foreach (var (unitKey, kind, sequence, sealedUnit, payload) in prepared)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO execution_evidence_units(
                    origin_identity_sha256, origin_sequence, evidence_id, body_kind, unit_key,
                    payload, payload_sha256, payload_bytes, status, attempt_count,
                    next_attempt_unix_ms, created_unix_ms, updated_unix_ms)
                VALUES ($origin, $sequence, $evidence, $kind, $key,
                    $payload, $hash, $bytes, 'pending', 0, $now, $now, $now);
                """;
            insert.Parameters.AddWithValue("$origin", originIdentitySha256);
            insert.Parameters.AddWithValue("$sequence", sequence);
            insert.Parameters.AddWithValue("$evidence", Compact(sealedUnit.EvidenceId));
            insert.Parameters.AddWithValue("$kind", kind.ToString());
            insert.Parameters.AddWithValue("$key", unitKey);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$hash", sealedUnit.PayloadSha256);
            insert.Parameters.AddWithValue("$bytes", payload.Length);
            insert.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var enlisted = prepared.Count;
        if (enlisted > 0)
        {
            using var advance = connection.CreateCommand();
            advance.Transaction = transaction;
            advance.CommandText =
                "UPDATE execution_evidence_origins SET next_sequence = $next WHERE origin_identity_sha256 = $origin;";
            advance.Parameters.AddWithValue("$next", nextSequence);
            advance.Parameters.AddWithValue("$origin", originIdentitySha256);
            await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteCursorAsync(connection, transaction, cursor, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            enlisted == 0
                ? ExecutionEvidenceEnlistmentDisposition.Duplicate
                : ExecutionEvidenceEnlistmentDisposition.Enlisted,
            enlisted,
            highest);
    }

    /// <summary>
    /// Commits a durable rejection: the sweep cursor advances past the unexportable execution and the count is
    /// recorded, so nothing partially written survives and the loss stays visible in local status.
    /// </summary>
    private async ValueTask<ExecutionEvidenceEnlistmentResult> RejectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExecutionEvidenceDiscoveryCursor cursor,
        Guid executionId,
        long highestSequence,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        int inserted;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // Keyed by execution, so a repeated sweep of the same unexportable row counts one loss rather than one
            // per cycle, while leaving the row eligible again if a later contract version can express it.
            command.CommandText = """
                INSERT INTO execution_evidence_rejections(execution_id, reason, observed_unix_ms)
                VALUES ($execution, $reason, $now)
                ON CONFLICT(execution_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$execution", Compact(executionId));
            command.Parameters.AddWithValue("$reason", Bound(reasonCode, 128));
            command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (inserted > 0)
        {
            // The sample table is bounded, so the count of retained rows saturates. The reported loss is a
            // monotonic counter instead, and the table stays as the operator-inspectable newest sample.
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = """
                UPDATE execution_evidence_state
                SET projection_rejected_events = projection_rejected_events + 1 WHERE state_key = 1;
                """;
            await count.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = """
                DELETE FROM execution_evidence_rejections
                WHERE execution_id NOT IN (
                    SELECT execution_id FROM execution_evidence_rejections
                    ORDER BY observed_unix_ms DESC, execution_id DESC LIMIT $keep);
                """;
            prune.Parameters.AddWithValue("$keep", MaximumRetainedRejections);
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await WriteCursorAsync(connection, transaction, cursor, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(ExecutionEvidenceEnlistmentDisposition.Rejected, 0, highestSequence, reasonCode);
    }

    public async ValueTask RecordProjectionRejectedAsync(
        string root,
        ExecutionEvidenceDiscoveryCursor cursor,
        Guid executionId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        await RejectAsync(connection, transaction, cursor, executionId, 0, reasonCode, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RecordSourcePrunedAsync(
        string root,
        ExecutionEvidenceDiscoveryCursor cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE execution_evidence_state
                SET discovery_terminal_unix_ms = $ms,
                    discovery_execution_id = $execution,
                    deferred_terminal_unix_ms = 0,
                    source_pruned_events = source_pruned_events + 1
                WHERE state_key = 1;
                """;
            command.Parameters.AddWithValue("$ms", cursor.TerminalUnixMs);
            command.Parameters.AddWithValue("$execution", cursor.ExecutionId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ExecutionEvidenceOriginRecord>> ReadOriginsWithWorkAsync(
        string root,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = OriginSelectSql + """
             WHERE EXISTS (
                SELECT 1 FROM execution_evidence_units unit
                WHERE unit.origin_identity_sha256 = origin.origin_identity_sha256
                  AND unit.status IN ('pending', 'retry'))
            ORDER BY origin.created_unix_ms, origin.origin_identity_sha256;
            """;
        var results = new List<ExecutionEvidenceOriginRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadOriginRecord(reader));
        }
        return results;
    }

    public async ValueTask<IReadOnlyList<ExecutionEvidenceUnit>> ReadPendingAsync(
        string root,
        string originIdentitySha256,
        int maximumUnits,
        long maximumBytes,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        EnsureSha256(originIdentitySha256, nameof(originIdentitySha256));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUnits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumUnits, GraphExecutionEvidenceLimits.MaximumResyncUnits);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        EnsureUtc(nowUtc, nameof(nowUtc));
        using var activity = ExecutionEvidenceExportTelemetry.ActivitySource.StartActivity("evidence-export.claim");
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UnitSelectSql + """
             WHERE origin_identity_sha256 = $origin
               AND (status = 'pending' OR (status = 'retry' AND next_attempt_unix_ms <= $now))
            ORDER BY origin_sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$origin", originIdentitySha256);
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", maximumUnits);
        var results = new List<ExecutionEvidenceUnit>(maximumUnits);
        long bytes = 0;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var unit = ReadUnit(reader);
                if (results.Count > 0 && bytes + unit.Payload.Length > maximumBytes)
                {
                    break;
                }
                bytes += unit.Payload.Length;
                results.Add(unit);
            }
        }

        // The attempt is counted before the request leaves, so a crash mid-send still shows the attempt durably and
        // the bounded attempt budget cannot be defeated by repeated interruption.
        for (var index = 0; index < results.Count; index++)
        {
            using var attempt = connection.CreateCommand();
            attempt.Transaction = transaction;
            attempt.CommandText = """
                UPDATE execution_evidence_units
                SET attempt_count = attempt_count + 1, updated_unix_ms = $now
                WHERE record_id = $record;
                """;
            attempt.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeMilliseconds());
            attempt.Parameters.AddWithValue("$record", results[index].RecordId);
            await attempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            results[index] = results[index] with { AttemptCount = results[index].AttemptCount + 1 };
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async ValueTask<IReadOnlyList<ExecutionEvidenceUnit>> ReadRangeAsync(
        string root,
        string originIdentitySha256,
        IReadOnlyList<ExecutionEvidenceSequenceRangeV1> ranges,
        int maximumUnits,
        long maximumBytes,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        EnsureSha256(originIdentitySha256, nameof(originIdentitySha256));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUnits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumUnits, GraphExecutionEvidenceLimits.MaximumResyncUnits);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        EnsureUtc(nowUtc, nameof(nowUtc));
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        // One transaction, so a multi-range replay is a snapshot and its attempts are counted exactly like a normal
        // send. A resynchronized unit must not escape the byte bound, the attempt budget, or the backoff.
        using var transaction = BeginImmediate(connection);
        var results = new List<ExecutionEvidenceUnit>(maximumUnits);
        long bytes = 0;
        foreach (var range in ranges.Take(GraphExecutionEvidenceLimits.MaximumResyncRanges))
        {
            if (results.Count >= maximumUnits || bytes >= maximumBytes)
            {
                break;
            }
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UnitSelectSql + """
                 WHERE origin_identity_sha256 = $origin
                   AND origin_sequence BETWEEN $from AND $to
                   AND status IN ('pending', 'retry')
                ORDER BY origin_sequence
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$origin", originIdentitySha256);
            command.Parameters.AddWithValue("$from", range.FromSequence);
            command.Parameters.AddWithValue("$to", range.ToSequence);
            command.Parameters.AddWithValue("$limit", maximumUnits - results.Count);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var unit = ReadUnit(reader);
                if (results.Count > 0 && bytes + unit.Payload.Length > maximumBytes)
                {
                    break;
                }
                bytes += unit.Payload.Length;
                results.Add(unit);
            }
        }
        for (var index = 0; index < results.Count; index++)
        {
            using var attempt = connection.CreateCommand();
            attempt.Transaction = transaction;
            attempt.CommandText = """
                UPDATE execution_evidence_units
                SET attempt_count = attempt_count + 1, updated_unix_ms = $now
                WHERE record_id = $record;
                """;
            attempt.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeMilliseconds());
            attempt.Parameters.AddWithValue("$record", results[index].RecordId);
            await attempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            results[index] = results[index] with { AttemptCount = results[index].AttemptCount + 1 };
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async ValueTask<ExecutionEvidenceAcknowledgementDisposition> AcknowledgeAsync(
        string root,
        string originIdentitySha256,
        long originSequence,
        string payloadSha256,
        DateTimeOffset acknowledgedUtc,
        CancellationToken cancellationToken)
    {
        EnsureSha256(originIdentitySha256, nameof(originIdentitySha256));
        EnsureSha256(payloadSha256, nameof(payloadSha256));
        EnsureUtc(acknowledgedUtc, nameof(acknowledgedUtc));
        using var activity = ExecutionEvidenceExportTelemetry.ActivitySource.StartActivity("evidence-export.acknowledge");
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        ExecutionEvidenceAcknowledgementDisposition disposition;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE execution_evidence_units
                SET status = 'acknowledged', acknowledged_unix_ms = $now, updated_unix_ms = $now, last_reason = NULL
                WHERE origin_identity_sha256 = $origin AND origin_sequence = $sequence
                  AND payload_sha256 = $hash AND status IN ('pending', 'retry');
                """;
            command.Parameters.AddWithValue("$now", acknowledgedUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$origin", originIdentitySha256);
            command.Parameters.AddWithValue("$sequence", originSequence);
            command.Parameters.AddWithValue("$hash", payloadSha256);
            disposition = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
                ? ExecutionEvidenceAcknowledgementDisposition.Settled
                : await ClassifyAcknowledgementAsync(
                    connection, transaction, originIdentitySha256, originSequence, payloadSha256, cancellationToken)
                    .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return disposition;
    }

    public ValueTask RetryAsync(
        string root,
        string originIdentitySha256,
        long originSequence,
        DateTimeOffset nextAttemptUtc,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        EnsureUtc(nextAttemptUtc, nameof(nextAttemptUtc));
        return SettleAsync(
            root, originIdentitySha256, originSequence, "retry", nextAttemptUtc, reasonCode, cancellationToken);
    }

    public ValueTask QuarantineAsync(
        string root,
        string originIdentitySha256,
        long originSequence,
        string reasonCode,
        CancellationToken cancellationToken)
        => SettleAsync(
            root, originIdentitySha256, originSequence, "quarantined", null, reasonCode, cancellationToken);

    public async ValueTask RecordAcknowledgedThroughAsync(
        string root,
        string originIdentitySha256,
        long acknowledgedThroughSequence,
        CancellationToken cancellationToken)
    {
        EnsureSha256(originIdentitySha256, nameof(originIdentitySha256));
        ArgumentOutOfRangeException.ThrowIfNegative(acknowledgedThroughSequence);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE execution_evidence_origins
                SET acknowledged_through_sequence = $sequence
                WHERE origin_identity_sha256 = $origin AND acknowledged_through_sequence < $sequence;
                """;
            command.Parameters.AddWithValue("$sequence", acknowledgedThroughSequence);
            command.Parameters.AddWithValue("$origin", originIdentitySha256);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records one receiver sequence conflict for operator review, bounded to the newest entries.</summary>
    public async ValueTask RecordConflictAsync(
        string root,
        string originIdentitySha256,
        long originSequence,
        string localPayloadSha256,
        string? receiverPayloadSha256,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        EnsureSha256(originIdentitySha256, nameof(originIdentitySha256));
        EnsureSha256(localPayloadSha256, nameof(localPayloadSha256));
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO execution_evidence_conflicts(
                    origin_identity_sha256, origin_sequence, local_payload_sha256, receiver_payload_sha256,
                    reason, observed_unix_ms)
                VALUES ($origin, $sequence, $local, $receiver, $reason, $now);
                """;
            command.Parameters.AddWithValue("$origin", originIdentitySha256);
            command.Parameters.AddWithValue("$sequence", originSequence);
            command.Parameters.AddWithValue("$local", localPayloadSha256);
            command.Parameters.AddWithValue("$receiver",
                IsSha256(receiverPayloadSha256) ? receiverPayloadSha256! : (object)DBNull.Value);
            command.Parameters.AddWithValue("$reason", Bound(reasonCode, 128));
            command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await PruneConflictsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ExecutionEvidenceBacklog> ReadBacklogAsync(string root, CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        var backlog = await ReadBacklogAsync(connection, null, cancellationToken).ConfigureAwait(false);
        return backlog with { DatabaseBytes = DatabaseBytes(root) };
    }

    public async ValueTask<int> RetainAsync(
        string root,
        TimeSpan acknowledgementRetention,
        int maximumRetainedAcknowledgements,
        string? retainedOriginIdentitySha256,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(acknowledgementRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedAcknowledgements);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var threshold = (_timeProvider.GetUtcNow() - acknowledgementRetention).ToUnixTimeMilliseconds();
        int removed;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // Only acknowledged units are eligible. Pending, retry, quarantined and abandoned units are retained
            // until the receiver acknowledges them or an operator disposes of them explicitly.
            command.CommandText = """
                DELETE FROM execution_evidence_units
                WHERE status = 'acknowledged'
                  AND (acknowledged_unix_ms < $threshold
                       OR record_id NOT IN (
                            SELECT record_id FROM execution_evidence_units
                            WHERE status = 'acknowledged'
                            ORDER BY acknowledged_unix_ms DESC, record_id DESC
                            LIMIT $keep));
                """;
            command.Parameters.AddWithValue("$threshold", threshold);
            command.Parameters.AddWithValue("$keep", maximumRetainedAcknowledgements);
            removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var origins = connection.CreateCommand())
        {
            origins.Transaction = transaction;
            origins.CommandText = """
                DELETE FROM execution_evidence_origins
                WHERE ($retained IS NULL OR origin_identity_sha256 <> $retained)
                  AND NOT EXISTS (
                    SELECT 1 FROM execution_evidence_units unit
                    WHERE unit.origin_identity_sha256 = execution_evidence_origins.origin_identity_sha256);
                """;
            origins.Parameters.AddWithValue(
                "$retained",
                IsSha256(retainedOriginIdentitySha256) ? retainedOriginIdentitySha256! : (object)DBNull.Value);
            await origins.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        // The audit trail and the operator receipts deliberately outlive the units they describe. Cascading them
        // away with retention would erase the disposition history an operator is told to inspect and would turn a
        // retried replay into a conflict instead of a detected duplicate; both are pruned by their own bound.
        using (var receipts = connection.CreateCommand())
        {
            receipts.Transaction = transaction;
            receipts.CommandText = """
                DELETE FROM execution_evidence_operations
                WHERE operation_key NOT IN (
                    SELECT operation_key FROM execution_evidence_operations
                    ORDER BY occurred_unix_ms DESC, operation_key DESC LIMIT $keep);
                """;
            receipts.Parameters.AddWithValue("$keep", MaximumOperationReceipts);
            await receipts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                DELETE FROM execution_evidence_audit
                WHERE audit_id NOT IN (
                    SELECT audit_id FROM execution_evidence_audit
                    ORDER BY audit_id DESC LIMIT $keep);
                """;
            audit.Parameters.AddWithValue("$keep", MaximumOperationReceipts);
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public async ValueTask<ExecutionEvidenceOutboxOperationsPage> ReadOperationsPageAsync(
        string root,
        int pageSize,
        ExecutionEvidenceOutboxOperationsCursor? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = OperationsSelectSql + """
             WHERE ($cursor IS NULL OR record_id < $cursor)
            ORDER BY record_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$cursor", cursor is null ? DBNull.Value : cursor.RecordId);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var items = new List<ExecutionEvidenceOutboxOperationsRecord>(pageSize);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadOperationsRecord(reader));
        }
        if (items.Count > pageSize)
        {
            items.RemoveAt(items.Count - 1);
            return new(items, new(items[^1].RecordId));
        }
        return new(items, null);
    }

    public async ValueTask<ExecutionEvidenceOutboxOperationsRecord?> ReadOperationsDetailAsync(
        string root,
        long recordId,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = OperationsSelectSql + " WHERE record_id = $record;";
        command.Parameters.AddWithValue("$record", recordId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadOperationsRecord(reader) : null;
    }

    public async ValueTask<OutboxOperationsAuditPage> ReadOperationsAuditAsync(
        string root,
        long recordId,
        int pageSize,
        OutboxOperationsAuditCursor? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT audit_id, action, actor_kind, reason, occurred_unix_ms
            FROM execution_evidence_audit
            WHERE record_id = $record AND ($cursor IS NULL OR audit_id < $cursor)
            ORDER BY audit_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$record", recordId);
        command.Parameters.AddWithValue("$cursor", cursor is null ? DBNull.Value : cursor.Sequence);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var items = new List<OutboxOperationsAuditRecord>(pageSize);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
        }
        if (items.Count > pageSize)
        {
            items.RemoveAt(items.Count - 1);
            return new(items, new(items[^1].Sequence));
        }
        return new(items, null);
    }

    public async ValueTask<OutboxOperationDisposition> ResolveOperationsAsync(
        string root,
        long recordId,
        OutboxOperationAction action,
        string operationKey,
        string actorKind,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationKey.Length, 128);
        if (!OutboxOperationsReasonCodes.IsAllowed(action, reasonCode))
        {
            throw new ArgumentException("The operation reason code is not allowed for this action.", nameof(reasonCode));
        }
        if (actorKind is not ("owner" or "system"))
        {
            throw new ArgumentException("The operation actor kind is invalid.", nameof(actorKind));
        }

        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var actionName = action == OutboxOperationAction.Replay ? "replay" : "abandon";
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT record_id, action, actor_kind, reason FROM execution_evidence_operations
                WHERE operation_key = $key;
                """;
            existing.Parameters.AddWithValue("$key", operationKey);
            using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt64(0) != recordId ||
                    !string.Equals(reader.GetString(1), actionName, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), actorKind, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(3), reasonCode, StringComparison.Ordinal))
                {
                    throw new OutboxOperationCollisionException(
                        "The evidence export operation key was reused with different intent.");
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return OutboxOperationDisposition.Duplicate;
            }
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = action == OutboxOperationAction.Replay
                ? """
                    UPDATE execution_evidence_units
                    SET status = 'pending', next_attempt_unix_ms = $now, updated_unix_ms = $now, last_reason = NULL
                    WHERE record_id = $record AND status = 'quarantined';
                    """
                : """
                    UPDATE execution_evidence_units
                    SET status = 'abandoned', updated_unix_ms = $now,
                        terminal_actor = $actor, terminal_reason = $reason, terminal_unix_ms = $now
                    WHERE record_id = $record AND status = 'quarantined';
                    """;
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$record", recordId);
            if (action == OutboxOperationAction.Abandon)
            {
                update.Parameters.AddWithValue("$actor", actorKind);
                update.Parameters.AddWithValue("$reason", reasonCode);
            }
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "The evidence export unit is not quarantined and cannot be resolved.");
            }
        }

        await WriteAuditAsync(connection, transaction, recordId, actionName, actorKind, reasonCode, now, cancellationToken)
            .ConfigureAwait(false);
        using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = """
                INSERT INTO execution_evidence_operations(
                    operation_key, record_id, action, actor_kind, reason, occurred_unix_ms)
                VALUES ($key, $record, $action, $actor, $reason, $now);
                """;
            receipt.Parameters.AddWithValue("$key", operationKey);
            receipt.Parameters.AddWithValue("$record", recordId);
            receipt.Parameters.AddWithValue("$action", actionName);
            receipt.Parameters.AddWithValue("$actor", actorKind);
            receipt.Parameters.AddWithValue("$reason", reasonCode);
            receipt.Parameters.AddWithValue("$now", now);
            await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return OutboxOperationDisposition.Applied;
    }

    private async ValueTask SettleAsync(
        string root,
        string originIdentitySha256,
        long originSequence,
        string status,
        DateTimeOffset? nextAttemptUtc,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        EnsureSha256(originIdentitySha256, nameof(originIdentitySha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        long recordId;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE execution_evidence_units
                SET status = $status, next_attempt_unix_ms = $next, updated_unix_ms = $now, last_reason = $reason
                WHERE origin_identity_sha256 = $origin AND origin_sequence = $sequence
                  AND status IN ('pending', 'retry')
                RETURNING record_id;
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$next", (nextAttemptUtc ?? _timeProvider.GetUtcNow()).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$reason", Bound(reasonCode, 128));
            command.Parameters.AddWithValue("$origin", originIdentitySha256);
            command.Parameters.AddWithValue("$sequence", originSequence);
            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is null or DBNull)
            {
                // The unit already settled terminally (acknowledged, abandoned, or quarantined by an earlier pass).
                // Terminal evidence is never rewritten, so this is a no-op rather than a failure.
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            recordId = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }
        if (string.Equals(status, "quarantined", StringComparison.Ordinal))
        {
            await WriteAuditAsync(
                connection, transaction, recordId, "quarantine", "system",
                OutboxOperationsReasonCodes.Sanitize(reasonCode), now, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long recordId,
        string action,
        string actorKind,
        string reasonCode,
        long occurredUnixMs,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO execution_evidence_audit(record_id, action, actor_kind, reason, occurred_unix_ms)
            VALUES ($record, $action, $actor, $reason, $now);
            """;
        command.Parameters.AddWithValue("$record", recordId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$actor", actorKind);
        command.Parameters.AddWithValue("$reason", Bound(reasonCode, 128));
        command.Parameters.AddWithValue("$now", occurredUnixMs);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ExecutionEvidenceAcknowledgementDisposition> ClassifyAcknowledgementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string originIdentitySha256,
        long originSequence,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status, payload_sha256 FROM execution_evidence_units
            WHERE origin_identity_sha256 = $origin AND origin_sequence = $sequence;
            """;
        command.Parameters.AddWithValue("$origin", originIdentitySha256);
        command.Parameters.AddWithValue("$sequence", originSequence);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ExecutionEvidenceAcknowledgementDisposition.Unknown;
        }
        var status = reader.GetString(0);
        var storedHash = reader.GetString(1);
        return !string.Equals(storedHash, payloadSha256, StringComparison.Ordinal)
            ? ExecutionEvidenceAcknowledgementDisposition.HashMismatch
            : string.Equals(status, "acknowledged", StringComparison.Ordinal)
                ? ExecutionEvidenceAcknowledgementDisposition.Duplicate
                : ExecutionEvidenceAcknowledgementDisposition.AlreadyTerminal;
    }

    private static async ValueTask<ExecutionEvidenceDiscoveryCursor> ReadCursorAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT discovery_terminal_unix_ms, discovery_execution_id,
                   deferred_terminal_unix_ms, source_pruned_events, projection_rejected_events
            FROM execution_evidence_state WHERE state_key = 1;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The execution evidence outbox state row is missing.");
        }
        return new(
            reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    private static async ValueTask WriteCursorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExecutionEvidenceDiscoveryCursor cursor,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE execution_evidence_state
            SET discovery_terminal_unix_ms = $ms, discovery_execution_id = $execution,
                deferred_terminal_unix_ms = $deferred
            WHERE state_key = 1;
            """;
        command.Parameters.AddWithValue("$ms", cursor.TerminalUnixMs);
        command.Parameters.AddWithValue("$execution", cursor.ExecutionId);
        command.Parameters.AddWithValue("$deferred", cursor.DeferredTerminalUnixMs);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> ReadNextSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string originIdentitySha256,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT next_sequence FROM execution_evidence_origins WHERE origin_identity_sha256 = $origin;";
        command.Parameters.AddWithValue("$origin", originIdentitySha256);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? throw new InvalidOperationException("The evidence export origin is not registered.")
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<(long Sequence, string PayloadSha256)?> ReadUnitKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string originIdentitySha256,
        string unitKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT origin_sequence, payload_sha256 FROM execution_evidence_units
            WHERE origin_identity_sha256 = $origin AND unit_key = $key;
            """;
        command.Parameters.AddWithValue("$origin", originIdentitySha256);
        command.Parameters.AddWithValue("$key", unitKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private static async ValueTask InsertConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string originIdentitySha256,
        long originSequence,
        string localPayloadSha256,
        string observedPayloadSha256,
        string reasonCode,
        DateTimeOffset occurredUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO execution_evidence_conflicts(
                origin_identity_sha256, origin_sequence, local_payload_sha256, receiver_payload_sha256,
                reason, observed_unix_ms)
            VALUES ($origin, $sequence, $local, $observed, $reason, $now);
            """;
        command.Parameters.AddWithValue("$origin", originIdentitySha256);
        command.Parameters.AddWithValue("$sequence", originSequence);
        command.Parameters.AddWithValue("$local", localPayloadSha256);
        command.Parameters.AddWithValue("$observed", observedPayloadSha256);
        command.Parameters.AddWithValue("$reason", Bound(reasonCode, 128));
        command.Parameters.AddWithValue("$now", occurredUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await PruneConflictsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PruneConflictsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM execution_evidence_conflicts
            WHERE conflict_id NOT IN (
                SELECT conflict_id FROM execution_evidence_conflicts
                ORDER BY conflict_id DESC LIMIT $keep);
            """;
        command.Parameters.AddWithValue("$keep", MaximumRetainedConflicts);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ExecutionEvidenceOriginRecord?> ReadOriginAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string originIdentitySha256,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = OriginSelectSql + " WHERE origin.origin_identity_sha256 = $origin;";
        command.Parameters.AddWithValue("$origin", originIdentitySha256);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadOriginRecord(reader) : null;
    }

    private static async ValueTask<ExecutionEvidenceBacklog> ReadBacklogAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN status = 'pending' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status IN ('pending', 'retry') THEN payload_bytes ELSE 0 END), 0),
                MIN(CASE WHEN status IN ('pending', 'retry') THEN created_unix_ms END),
                COALESCE(SUM(CASE WHEN status = 'retry' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'quarantined' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'abandoned' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'acknowledged' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(attempt_count), 0),
                COALESCE(MAX(origin_sequence), 0)
            FROM execution_evidence_units;
            """;
        long pending, pendingBytes, retry, quarantined, abandoned, acknowledged, attempts, highest;
        DateTimeOffset? oldest;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            pending = reader.GetInt64(0);
            pendingBytes = reader.GetInt64(1);
            oldest = reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
            retry = reader.GetInt64(3);
            quarantined = reader.GetInt64(4);
            abandoned = reader.GetInt64(5);
            acknowledged = reader.GetInt64(6);
            attempts = reader.GetInt64(7);
            highest = reader.GetInt64(8);
        }

        long conflicts;
        using (var conflictCommand = connection.CreateCommand())
        {
            conflictCommand.Transaction = transaction;
            conflictCommand.CommandText = "SELECT COUNT(*) FROM execution_evidence_conflicts;";
            conflicts = Convert.ToInt64(
                await conflictCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        long pruned, acknowledgedThrough, rejected;
        using (var stateCommand = connection.CreateCommand())
        {
            stateCommand.Transaction = transaction;
            stateCommand.CommandText = """
                SELECT (SELECT source_pruned_events FROM execution_evidence_state WHERE state_key = 1),
                       COALESCE((SELECT MIN(acknowledged_through_sequence) FROM execution_evidence_origins), 0),
                       (SELECT projection_rejected_events FROM execution_evidence_state WHERE state_key = 1);
                """;
            using var reader = await stateCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            pruned = reader.GetInt64(0);
            acknowledgedThrough = reader.GetInt64(1);
            rejected = reader.GetInt64(2);
        }

        return new(
            pending,
            pendingBytes,
            oldest,
            retry,
            quarantined,
            abandoned,
            acknowledged,
            attempts,
            conflicts,
            pruned,
            0,
            highest,
            acknowledgedThrough,
            rejected);
    }

    private static ExecutionEvidenceOriginRecord ReadOriginRecord(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            ParseGuid(reader.GetString(1)),
            ParseGuid(reader.GetString(2)),
            ParseGuid(reader.GetString(3)),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : ParseGuid(reader.GetString(5)),
            reader.IsDBNull(6) ? null : ParseGuid(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseGuid(reader.GetString(7)),
            reader.GetInt64(8),
            reader.GetInt64(9),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)));

    private static ExecutionEvidenceUnit ReadUnit(SqliteDataReader reader)
    {
        var payload = (byte[])reader.GetValue(5);
        return new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2),
            ParseGuid(reader.GetString(3)),
            ParseBodyKind(reader.GetString(4)),
            reader.GetString(10),
            payload,
            reader.GetString(6),
            ParseStatus(reader.GetString(7)),
            reader.GetInt32(8),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
            reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    private static ExecutionEvidenceOutboxOperationsRecord ReadOperationsRecord(SqliteDataReader reader)
    {
        var status = ParseStatus(reader.GetString(3));
        return new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2),
            status.ToString(),
            reader.GetInt32(4),
            reader.GetInt64(5),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            reader.IsDBNull(9) ? null : OutboxOperationsReasonCodes.Sanitize(reader.GetString(9)),
            status == ExecutionEvidenceUnitStatus.Quarantined,
            status == ExecutionEvidenceUnitStatus.Quarantined,
            new(reader.GetInt64(0)));
    }

    private static async ValueTask EnsureCanonicalSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        long version;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT version FROM execution_evidence_schema WHERE schema_key = 1;";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            version = value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        if (version != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Execution evidence outbox schema {version} is not the canonical schema {CurrentSchemaVersion}.");
        }

        var observed = await ReadSchemaObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        var canonical = CanonicalSchemaObjects.Value;
        if (observed.Count != canonical.Count ||
            canonical.Any(entry => !observed.TryGetValue(entry.Key, out var sql) ||
                !string.Equals(sql, entry.Value, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The execution evidence outbox SQLite schema is not the canonical schema definition.");
        }
    }

    private static async ValueTask<long> CountSchemaObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async ValueTask<Dictionary<string, string>> ReadSchemaObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SchemaObjectSql;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add($"{reader.GetString(0)}:{reader.GetString(1)}", NormalizeSchemaSql(reader.GetString(2)));
        }
        return result;
    }

    private static Dictionary<string, string> CreateCanonicalSchemaObjects()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SchemaSql;
            command.ExecuteNonQuery();
        }
        using var schema = connection.CreateCommand();
        schema.CommandText = SchemaObjectSql;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = schema.ExecuteReader();
        while (reader.Read())
        {
            result.Add($"{reader.GetString(0)}:{reader.GetString(1)}", NormalizeSchemaSql(reader.GetString(2)));
        }
        return result;
    }

    private static string NormalizeSchemaSql(string sql)
    {
        var result = new List<char>(sql.Length);
        var inLiteral = false;
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (character == '\'')
            {
                result.Add(character);
                if (inLiteral && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    result.Add(sql[++index]);
                    continue;
                }
                inLiteral = !inLiteral;
                continue;
            }
            if (inLiteral || !char.IsWhiteSpace(character))
            {
                result.Add(character);
            }
        }
        return new string([.. result]);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The interpolated value is a constructor-validated integer used only for SQLite PRAGMA configuration.")]
    private async ValueTask<SqliteConnection> OpenAsync(string root, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath(root),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout={checked(_busyTimeoutSeconds * 1000)};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static SqliteTransaction BeginImmediate(SqliteConnection connection)
    {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        return connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
    }

    internal static string EvidenceDirectory(string root) => Path.Combine(root, "evidence");

    internal static string DatabasePath(string root)
        => Path.Combine(EvidenceDirectory(root), "execution-evidence-outbox.db");

    private static long DatabaseBytes(string root)
    {
        long total = 0;
        var databasePath = DatabasePath(root);
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                total += info.Length;
            }
        }
        return total;
    }

    private static void EnsureDatabaseFilesArePhysical(string root)
    {
        var databasePath = DatabasePath(root);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, databasePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-wal"));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-shm"));
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("UTC time is required.", parameterName);
        }
    }

    private static void EnsureSha256(string value, string parameterName)
    {
        if (!IsSha256(value))
        {
            throw new ArgumentException("An uppercase 64-character SHA-256 value is required.", parameterName);
        }
    }

    private static bool IsSha256([NotNullWhen(true)] string? value)
        => value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string Bound(string value, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static string Compact(Guid value) => value.ToString("N");

    private static object Compact(Guid? value) => value is { } present ? present.ToString("N") : DBNull.Value;

    private static Guid ParseGuid(string value) => Guid.ParseExact(value, "N");

    private static ExecutionEvidenceUnitStatus ParseStatus(string value) => value switch
    {
        "pending" => ExecutionEvidenceUnitStatus.Pending,
        "retry" => ExecutionEvidenceUnitStatus.Retry,
        "acknowledged" => ExecutionEvidenceUnitStatus.Acknowledged,
        "quarantined" => ExecutionEvidenceUnitStatus.Quarantined,
        "abandoned" => ExecutionEvidenceUnitStatus.Abandoned,
        _ => throw new InvalidDataException("The evidence export unit status is invalid.")
    };

    private static ExecutionEvidenceBodyKind ParseBodyKind(string value) => value switch
    {
        "GraphRevision" => ExecutionEvidenceBodyKind.GraphRevision,
        "GraphExecution" => ExecutionEvidenceBodyKind.GraphExecution,
        "ArtifactAvailability" => ExecutionEvidenceBodyKind.ArtifactAvailability,
        _ => throw new InvalidDataException("The evidence export body kind is invalid.")
    };

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly Lazy<Dictionary<string, string>> CanonicalSchemaObjects =
        new(CreateCanonicalSchemaObjects, LazyThreadSafetyMode.ExecutionAndPublication);

    private const string SchemaObjectSql =
        "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type, name;";

    private const string OriginSelectSql = """
        SELECT origin.origin_identity_sha256, origin.origin_installation_id, origin.agent_instance_id,
               origin.boot_session_id, origin.software_version, origin.observatory_id,
               origin.logical_camera_installation_id, origin.installation_public_id,
               origin.next_sequence, origin.acknowledged_through_sequence, origin.created_unix_ms
        FROM execution_evidence_origins origin
        """;

    private const string UnitSelectSql = """
        SELECT record_id, origin_identity_sha256, origin_sequence, evidence_id, body_kind,
               payload, payload_sha256, status, attempt_count, next_attempt_unix_ms,
               unit_key, created_unix_ms, updated_unix_ms, last_reason
        FROM execution_evidence_units
        """;

    private const string OperationsSelectSql = """
        SELECT record_id, body_kind, origin_sequence, status, attempt_count, payload_bytes,
               created_unix_ms, updated_unix_ms, next_attempt_unix_ms, last_reason
        FROM execution_evidence_units
        """;

    private const string SchemaSql = """
        CREATE TABLE execution_evidence_schema(
            schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
            version INTEGER NOT NULL CHECK(version = 1)
        ) STRICT;
        INSERT INTO execution_evidence_schema(schema_key, version) VALUES (1, 1);
        CREATE TABLE execution_evidence_state(
            state_key INTEGER PRIMARY KEY CHECK(state_key = 1),
            discovery_terminal_unix_ms INTEGER NOT NULL CHECK(discovery_terminal_unix_ms >= 0),
            discovery_execution_id TEXT NOT NULL CHECK(length(discovery_execution_id) IN (0, 32)),
            deferred_terminal_unix_ms INTEGER NOT NULL CHECK(deferred_terminal_unix_ms >= 0),
            source_pruned_events INTEGER NOT NULL CHECK(source_pruned_events >= 0),
            projection_rejected_events INTEGER NOT NULL CHECK(projection_rejected_events >= 0)
        ) STRICT;
        INSERT INTO execution_evidence_state(
            state_key, discovery_terminal_unix_ms, discovery_execution_id,
            deferred_terminal_unix_ms, source_pruned_events, projection_rejected_events)
            VALUES (1, 0, '', 0, 0, 0);
        CREATE TABLE execution_evidence_rejections(
            execution_id TEXT PRIMARY KEY CHECK(length(execution_id) = 32),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
            observed_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE execution_evidence_origins(
            origin_identity_sha256 TEXT PRIMARY KEY CHECK(length(origin_identity_sha256) = 64),
            origin_installation_id TEXT NOT NULL CHECK(length(origin_installation_id) = 32),
            agent_instance_id TEXT NOT NULL CHECK(length(agent_instance_id) = 32),
            boot_session_id TEXT NOT NULL CHECK(length(boot_session_id) = 32),
            software_version TEXT NOT NULL CHECK(length(software_version) BETWEEN 1 AND 64),
            observatory_id TEXT NULL CHECK(observatory_id IS NULL OR length(observatory_id) = 32),
            logical_camera_installation_id TEXT NULL
                CHECK(logical_camera_installation_id IS NULL OR length(logical_camera_installation_id) = 32),
            installation_public_id TEXT NULL
                CHECK(installation_public_id IS NULL OR length(installation_public_id) = 32),
            next_sequence INTEGER NOT NULL CHECK(next_sequence >= 1),
            acknowledged_through_sequence INTEGER NOT NULL CHECK(acknowledged_through_sequence >= 0),
            created_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE execution_evidence_units(
            record_id INTEGER PRIMARY KEY AUTOINCREMENT,
            origin_identity_sha256 TEXT NOT NULL CHECK(length(origin_identity_sha256) = 64),
            origin_sequence INTEGER NOT NULL CHECK(origin_sequence >= 1),
            evidence_id TEXT NOT NULL CHECK(length(evidence_id) = 32),
            body_kind TEXT NOT NULL
                CHECK(body_kind IN ('GraphRevision', 'GraphExecution', 'ArtifactAvailability')),
            unit_key TEXT NOT NULL CHECK(length(unit_key) BETWEEN 1 AND 128),
            payload BLOB NOT NULL CHECK(length(payload) BETWEEN 2 AND 8388608),
            payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
            payload_bytes INTEGER NOT NULL CHECK(payload_bytes > 0),
            status TEXT NOT NULL
                CHECK(status IN ('pending', 'retry', 'acknowledged', 'quarantined', 'abandoned')),
            attempt_count INTEGER NOT NULL CHECK(attempt_count >= 0),
            next_attempt_unix_ms INTEGER NOT NULL,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            acknowledged_unix_ms INTEGER NULL,
            last_reason TEXT NULL CHECK(last_reason IS NULL OR length(last_reason) BETWEEN 1 AND 128),
            terminal_actor TEXT NULL CHECK(terminal_actor IS NULL OR terminal_actor IN ('owner', 'system')),
            terminal_reason TEXT NULL CHECK(terminal_reason IS NULL OR length(terminal_reason) BETWEEN 1 AND 128),
            terminal_unix_ms INTEGER NULL,
            CHECK((status = 'acknowledged') = (acknowledged_unix_ms IS NOT NULL)),
            CHECK((status = 'abandoned') = (terminal_unix_ms IS NOT NULL)),
            CHECK((terminal_unix_ms IS NULL) = (terminal_actor IS NULL)),
            CHECK((terminal_unix_ms IS NULL) = (terminal_reason IS NULL)),
            UNIQUE(origin_identity_sha256, origin_sequence),
            UNIQUE(origin_identity_sha256, unit_key),
            FOREIGN KEY(origin_identity_sha256)
                REFERENCES execution_evidence_origins(origin_identity_sha256)
        ) STRICT;
        CREATE INDEX ix_execution_evidence_units_send
            ON execution_evidence_units(origin_identity_sha256, status, next_attempt_unix_ms, origin_sequence);
        CREATE INDEX ix_execution_evidence_units_status
            ON execution_evidence_units(status, created_unix_ms, record_id);
        CREATE INDEX ix_execution_evidence_units_operations
            ON execution_evidence_units(status, record_id DESC);
        CREATE INDEX ix_execution_evidence_units_acknowledged
            ON execution_evidence_units(status, acknowledged_unix_ms, record_id);
        CREATE INDEX ix_execution_evidence_units_totals
            ON execution_evidence_units(status, payload_bytes, attempt_count, created_unix_ms, origin_sequence);
        CREATE TABLE execution_evidence_audit(
            audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
            record_id INTEGER NOT NULL,
            action TEXT NOT NULL CHECK(length(action) BETWEEN 1 AND 32),
            actor_kind TEXT NOT NULL CHECK(actor_kind IN ('owner', 'system')),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
            occurred_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE INDEX ix_execution_evidence_audit_record
            ON execution_evidence_audit(record_id, audit_id DESC);
        CREATE TABLE execution_evidence_operations(
            operation_key TEXT PRIMARY KEY CHECK(length(operation_key) BETWEEN 1 AND 128),
            record_id INTEGER NOT NULL,
            action TEXT NOT NULL CHECK(action IN ('replay', 'abandon')),
            actor_kind TEXT NOT NULL CHECK(actor_kind IN ('owner', 'system')),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
            occurred_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE execution_evidence_conflicts(
            conflict_id INTEGER PRIMARY KEY AUTOINCREMENT,
            origin_identity_sha256 TEXT NOT NULL CHECK(length(origin_identity_sha256) = 64),
            origin_sequence INTEGER NOT NULL CHECK(origin_sequence >= 1),
            local_payload_sha256 TEXT NOT NULL CHECK(length(local_payload_sha256) = 64),
            receiver_payload_sha256 TEXT NULL
                CHECK(receiver_payload_sha256 IS NULL OR length(receiver_payload_sha256) = 64),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
            observed_unix_ms INTEGER NOT NULL
        ) STRICT;
        """;

    public void Dispose()
    {
        foreach (var gate in _initializationGates.Values)
        {
            gate.Dispose();
        }
        _initializationGates.Clear();
    }
}
