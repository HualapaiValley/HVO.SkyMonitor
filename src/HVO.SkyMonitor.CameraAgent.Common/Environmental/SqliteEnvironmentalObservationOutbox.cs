using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

/// <summary>Bounded SQLite WAL delivery state for fully enriched environmental observations.</summary>
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Microsoft.Data.Sqlite field getters read buffered values after asynchronous row reads.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Dynamic SQL inputs are private settlement constants or validated numeric configuration; record values remain parameterized.")]
public sealed class SqliteEnvironmentalObservationOutbox(
    TimeProvider? timeProvider = null,
    int busyTimeoutSeconds = 5,
    int maximumRecords = 10_000,
    long maximumBytes = 64L * 1024 * 1024,
    IEnvironmentalObservationOutboxFaultInjector? faultInjector = null) : IEnvironmentalObservationOutbox, IDisposable
{
    private const int MaximumAuditRecords = 10_000;
    internal const int MaximumOperationReceipts = 10_000;
    internal const int OperationReceiptRetentionDays = 30;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IEnvironmentalObservationOutboxFaultInjector _faultInjector =
        faultInjector ?? NullEnvironmentalObservationOutboxFaultInjector.Instance;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initializationGates = new(PathComparer);
    private readonly HashSet<string> _initializedRoots = new(PathComparer);
    private readonly object _initializedLock = new();

    public async ValueTask<EnvironmentalObservationEnqueueDisposition> EnqueueAsync(
        string root,
        EnvironmentalObservationV1 observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var validation = EnvironmentalObservationJson.Validate(observation);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Environmental observation is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(observation));
        }
        if (observation.Target.AgentId is null)
        {
            throw new ArgumentException("Environmental delivery requires a device-scoped target.", nameof(observation));
        }
        var canonical = EnvironmentalObservationJson.Parse(EnvironmentalObservationJson.Serialize(observation)).Observation
            ?? throw new InvalidDataException("Canonical environmental observation could not be parsed.");
        var targetAgentId = canonical.Target.AgentId
            ?? throw new InvalidDataException("Canonical environmental observation lost its device target.");
        var payload = EnvironmentalObservationJson.Serialize(canonical);
        var sourceIdentity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(canonical);
        var contentIdentity = EnvironmentalObservationJson.ComputeContentSha256(canonical);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT record_id, content_sha256 FROM environmental_observation_outbox WHERE source_identity_sha256 = $source AND observation_id = $observation;";
            existing.Parameters.AddWithValue("$source", sourceIdentity);
            existing.Parameters.AddWithValue("$observation", canonical.ObservationId.ToString("D"));
            long? existingRecordId = null;
            string? existingHash = null;
            using (var existingReader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await existingReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    existingRecordId = existingReader.GetInt64(0);
                    existingHash = existingReader.GetString(1);
                }
            }
            if (existingRecordId is { } recordId)
            {
                if (!string.Equals(Convert.ToString(existingHash, System.Globalization.CultureInfo.InvariantCulture), contentIdentity, StringComparison.Ordinal))
                {
                    throw new EnvironmentalObservationIdentityConflictException(
                        "A different environmental payload already uses this local source and observation identity.");
                }
                _ = await ReadRecordAsync(connection, transaction, recordId, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return EnvironmentalObservationEnqueueDisposition.Duplicate;
            }
        }

        long storedCount;
        long storedBytes;
        using (var capacity = connection.CreateCommand())
        {
            capacity.Transaction = transaction;
            capacity.CommandText = "SELECT stored_count, stored_bytes FROM environmental_observation_metadata WHERE metadata_key = 1;";
            using var reader = await capacity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            storedCount = reader.GetInt64(0);
            storedBytes = reader.GetInt64(1);
        }
        if (storedCount >= maximumRecords || storedBytes > maximumBytes - payload.Length)
        {
            using var overflow = connection.CreateCommand();
            overflow.Transaction = transaction;
            overflow.CommandText = "UPDATE environmental_observation_metadata SET overflow_count = overflow_count + 1 WHERE metadata_key = 1;";
            await overflow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new EnvironmentalObservationOutboxCapacityException(
                "The environmental observation outbox reached its configured count or byte capacity.");
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO environmental_observation_outbox(
                    source_identity_sha256, observation_id, content_sha256, target_site_id, target_agent_id,
                    payload, payload_bytes, status, attempt_count, next_attempt_unix_ms, created_unix_ms, updated_unix_ms)
                VALUES($source, $observation, $content, $site, $agent, $payload, $bytes,
                    'pending', 0, $now, $now, $now);
                """;
            insert.Parameters.AddWithValue("$source", sourceIdentity);
            insert.Parameters.AddWithValue("$observation", canonical.ObservationId.ToString("D"));
            insert.Parameters.AddWithValue("$content", contentIdentity);
            insert.Parameters.AddWithValue("$site", canonical.Target.SiteId.ToString("D"));
            insert.Parameters.AddWithValue("$agent", targetAgentId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$bytes", payload.Length);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE environmental_observation_metadata SET stored_count = stored_count + 1, stored_bytes = stored_bytes + $bytes WHERE metadata_key = 1;";
            metadata.Parameters.AddWithValue("$bytes", payload.Length);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await _faultInjector.BeforeEnqueueCommitAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _faultInjector.AfterEnqueueCommitAsync(cancellationToken).ConfigureAwait(false);
        return EnvironmentalObservationEnqueueDisposition.Enqueued;
    }

    public async ValueTask<EnvironmentalObservationOutboxLease?> ClaimAsync(
        string root,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        for (var malformedRecords = 0; malformedRecords < 100; malformedRecords++)
        {
            var now = _timeProvider.GetUtcNow();
            var expires = now + leaseDuration;
            var token = Guid.NewGuid().ToString("N");
            using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            long? recordId;
            using (var candidate = connection.CreateCommand())
            {
                candidate.Transaction = transaction;
                candidate.CommandText = """
                    SELECT record_id FROM (
                        SELECT * FROM (
                            SELECT record_id, next_attempt_unix_ms, created_unix_ms
                            FROM environmental_observation_outbox
                            WHERE status = 'pending'
                            ORDER BY next_attempt_unix_ms, created_unix_ms, record_id
                            LIMIT 1)
                        UNION ALL
                        SELECT * FROM (
                            SELECT record_id, next_attempt_unix_ms, created_unix_ms
                            FROM environmental_observation_outbox
                            WHERE status = 'retry' AND next_attempt_unix_ms <= $now
                            ORDER BY next_attempt_unix_ms, created_unix_ms, record_id
                            LIMIT 1)
                        UNION ALL
                        SELECT * FROM (
                            SELECT record_id, lease_expires_unix_ms, created_unix_ms
                            FROM environmental_observation_outbox
                            WHERE status = 'leased' AND lease_expires_unix_ms <= $now
                            ORDER BY lease_expires_unix_ms, created_unix_ms, record_id
                            LIMIT 1))
                    ORDER BY next_attempt_unix_ms, created_unix_ms, record_id
                    LIMIT 1;
                    """;
                candidate.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                var value = await candidate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                recordId = value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (recordId is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE environmental_observation_outbox
                    SET status = 'leased', attempt_count = attempt_count + 1, lease_owner = $owner,
                        lease_token = $token, lease_expires_unix_ms = $expires, updated_unix_ms = $now
                    WHERE record_id = $id AND (status = 'pending'
                        OR (status = 'retry' AND next_attempt_unix_ms <= $now)
                        OR (status = 'leased' AND lease_expires_unix_ms <= $now));
                    """;
                update.Parameters.AddWithValue("$owner", Bound(owner, 128));
                update.Parameters.AddWithValue("$token", token);
                update.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$id", recordId.Value);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new EnvironmentalObservationLeaseLostException("Environmental observation claim changed before commit.");
                }
            }
            try
            {
                var record = await ReadRecordAsync(connection, transaction, recordId.Value, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new EnvironmentalObservationOutboxLease(record, Bound(owner, 128), token, expires);
            }
            catch (InvalidDataException)
            {
                using var quarantine = connection.CreateCommand();
                quarantine.Transaction = transaction;
                quarantine.CommandText = """
                    UPDATE environmental_observation_outbox
                    SET status = 'quarantined', last_reason = 'malformed-committed-record',
                        lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL, updated_unix_ms = $now
                    WHERE record_id = $id;
                    """;
                quarantine.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                quarantine.Parameters.AddWithValue("$id", recordId.Value);
                await quarantine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return null;
    }

    public ValueTask AcknowledgeAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        EnvironmentalObservationAcknowledgement acknowledgement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (!EnvironmentalObservationDeliveryJson.Matches(acknowledgement, lease.Record.Observation))
        {
            throw new InvalidDataException("Environmental acknowledgement does not match the leased observation.");
        }
        return SettleAsync(
            root,
            lease,
            "DELETE FROM environmental_observation_outbox WHERE record_id = $id AND status = 'leased' AND lease_owner = $owner AND lease_token = $token AND lease_expires_unix_ms > $now;",
            null,
            null,
            cancellationToken,
            removesRecord: true);
    }

    public ValueTask RetryAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        DateTimeOffset retryAtUtc,
        string reason,
        CancellationToken cancellationToken)
        => SettleAsync(root, lease, """
            UPDATE environmental_observation_outbox
            SET status = 'retry', next_attempt_unix_ms = $next, last_reason = $reason,
                lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL, updated_unix_ms = $now
            WHERE record_id = $id AND status = 'leased' AND lease_owner = $owner AND lease_token = $token
                AND lease_expires_unix_ms > $now;
            """, retryAtUtc, reason, cancellationToken);

    public ValueTask QuarantineAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        string reason,
        CancellationToken cancellationToken)
        => TerminalSettleAsync(root, lease, "quarantined", reason, cancellationToken);

    public ValueTask TerminalAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        string reason,
        CancellationToken cancellationToken)
        => TerminalSettleAsync(root, lease, "terminal", reason, cancellationToken);

    public async ValueTask<EnvironmentalObservationOutboxSnapshot> GetSnapshotAsync(
        string root,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT stored_count FROM environmental_observation_metadata WHERE metadata_key = 1),
                (SELECT stored_bytes FROM environmental_observation_metadata WHERE metadata_key = 1),
                COALESCE(SUM(CASE WHEN status IN ('pending','retry','leased') THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status IN ('pending','retry','leased') THEN payload_bytes ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'leased' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'retry' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'quarantined' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'terminal' THEN 1 ELSE 0 END), 0),
                (SELECT overflow_count FROM environmental_observation_metadata WHERE metadata_key = 1),
                MIN(CASE WHEN status IN ('pending','retry','leased') THEN created_unix_ms END)
            FROM environmental_observation_outbox;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new EnvironmentalObservationOutboxSnapshot(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
            reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
            _timeProvider.GetUtcNow());
    }

    public async ValueTask<IReadOnlyList<EnvironmentalObservationDeadLetter>> ReadDeadLettersAsync(
        string root,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 1000);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT record_id, status, COALESCE(last_reason, 'unspecified'), payload_bytes, attempt_count,
                created_unix_ms, updated_unix_ms
            FROM environmental_observation_outbox
            WHERE status IN ('quarantined','terminal')
            ORDER BY updated_unix_ms, record_id
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$maximum", maximumResults);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<EnvironmentalObservationDeadLetter>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new(
                reader.GetInt64(0),
                reader.GetString(1) == "quarantined"
                    ? EnvironmentalObservationDeadLetterStatus.Quarantined
                    : EnvironmentalObservationDeadLetterStatus.Terminal,
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
        }
        return records;
    }

    public ValueTask ReplayAsync(
        string root,
        long recordId,
        string actor,
        string reason,
        CancellationToken cancellationToken)
        => ResolveDeadLetterAsync(root, recordId, actor, reason, replay: true, cancellationToken);

    public ValueTask AbandonAsync(
        string root,
        long recordId,
        string actor,
        string reason,
        CancellationToken cancellationToken)
        => ResolveDeadLetterAsync(root, recordId, actor, reason, replay: false, cancellationToken);

    public async ValueTask<EnvironmentalOutboxOperationsPage> ReadOperationsPageAsync(
        string root,
        int pageSize,
        EnvironmentalOutboxOperationsCursor? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = cursor is null
            ? string.Concat(OperationsSelectColumns, " WHERE status IN ('quarantined','terminal') ORDER BY record_id DESC LIMIT $limit;")
            : string.Concat(OperationsSelectColumns, " WHERE status IN ('quarantined','terminal') AND record_id < $record_id ORDER BY record_id DESC LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$record_id", cursor.RecordId);
        }
        var records = new List<EnvironmentalOutboxOperationsRecord>(pageSize + 1);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadOperationsRecord(reader));
        }
        var hasMore = records.Count > pageSize;
        if (hasMore)
        {
            records.RemoveAt(records.Count - 1);
        }
        return new EnvironmentalOutboxOperationsPage(
            records,
            hasMore && records.Count > 0 ? records[^1].Cursor : null);
    }

    public async ValueTask<EnvironmentalOutboxOperationsRecord?> ReadOperationsDetailAsync(
        string root,
        long recordId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordId, 1);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = string.Concat(
            OperationsSelectColumns,
            " WHERE record_id = $record_id AND status IN ('quarantined','terminal');");
        command.Parameters.AddWithValue("$record_id", recordId);
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
        ArgumentOutOfRangeException.ThrowIfLessThan(recordId, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = cursor is null
            ? "SELECT audit_id, action, actor, reason, occurred_unix_ms FROM environmental_observation_outbox_audit WHERE record_id = $record_id ORDER BY audit_id DESC LIMIT $limit;"
            : "SELECT audit_id, action, actor, reason, occurred_unix_ms FROM environmental_observation_outbox_audit WHERE record_id = $record_id AND audit_id < $sequence ORDER BY audit_id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$record_id", recordId);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$sequence", cursor.Sequence);
        }
        var entries = new List<OutboxOperationsAuditRecord>(pageSize + 1);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new OutboxOperationsAuditRecord(
                reader.GetInt64(0),
                Bound(reader.GetString(1), 32),
                string.Equals(reader.GetString(2), "owner", StringComparison.Ordinal) ? "owner" : "system",
                OutboxOperationsReasonCodes.Sanitize(reader.GetString(3)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
        }
        var hasMore = entries.Count > pageSize;
        if (hasMore)
        {
            entries.RemoveAt(entries.Count - 1);
        }
        return new OutboxOperationsAuditPage(
            entries,
            hasMore && entries.Count > 0 ? new OutboxOperationsAuditCursor(entries[^1].Sequence) : null);
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
        ArgumentOutOfRangeException.ThrowIfLessThan(recordId, 1);
        ValidateOperation(operationKey, actorKind, reasonCode);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        var existing = await ReadOperationAsync(root, operationKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return MatchOperation(existing, recordId, action, actorKind, reasonCode);
        }

        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        existing = await ReadOperationAsync(connection, transaction, operationKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var disposition = MatchOperation(existing, recordId, action, actorKind, reasonCode);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return disposition;
        }
        string status;
        int payloadBytes;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT status, payload_bytes FROM environmental_observation_outbox WHERE record_id = $id AND status IN ('quarantined','terminal');";
            read.Parameters.AddWithValue("$id", recordId);
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Environmental observation dead-letter resolution requires one terminal or quarantined record.");
            }
            status = reader.GetString(0);
            payloadBytes = reader.GetInt32(1);
        }
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = action == OutboxOperationAction.Replay
                ? "UPDATE environmental_observation_outbox SET status = 'pending', attempt_count = 0, next_attempt_unix_ms = $now, last_reason = NULL, lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL, updated_unix_ms = $now WHERE record_id = $id AND status IN ('quarantined','terminal');"
                : "DELETE FROM environmental_observation_outbox WHERE record_id = $id AND status IN ('quarantined','terminal');";
            update.Parameters.AddWithValue("$id", recordId);
            update.Parameters.AddWithValue("$now", now);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Environmental observation dead-letter resolution requires one terminal or quarantined record.");
            }
        }
        if (action == OutboxOperationAction.Abandon)
        {
            using var metadata = connection.CreateCommand();
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE environmental_observation_metadata SET stored_count = stored_count - 1, stored_bytes = stored_bytes - $bytes WHERE metadata_key = 1;";
            metadata.Parameters.AddWithValue("$bytes", payloadBytes);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var operation = connection.CreateCommand())
        {
            operation.Transaction = transaction;
            operation.CommandText = "INSERT INTO environmental_observation_outbox_operations(operation_key, record_id, action, actor_kind, reason, occurred_unix_ms) VALUES($operation, $id, $action, $actor, $reason, $now);";
            operation.Parameters.AddWithValue("$operation", operationKey);
            operation.Parameters.AddWithValue("$id", recordId);
            operation.Parameters.AddWithValue("$action", FormatAction(action));
            operation.Parameters.AddWithValue("$actor", actorKind);
            operation.Parameters.AddWithValue("$reason", reasonCode);
            operation.Parameters.AddWithValue("$now", now);
            await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO environmental_observation_outbox_audit(record_id, previous_status, action, actor, reason, occurred_unix_ms, operation_key) VALUES($id, $status, $action, $actor, $reason, $now, $operation);";
            audit.Parameters.AddWithValue("$id", recordId);
            audit.Parameters.AddWithValue("$status", status);
            audit.Parameters.AddWithValue("$action", FormatAction(action));
            audit.Parameters.AddWithValue("$actor", actorKind);
            audit.Parameters.AddWithValue("$reason", reasonCode);
            audit.Parameters.AddWithValue("$now", now);
            audit.Parameters.AddWithValue("$operation", operationKey);
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await PruneOperationReceiptsAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return OutboxOperationDisposition.Applied;
    }

    private async ValueTask InitializeAsync(string root, CancellationToken cancellationToken)
    {
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
            Directory.CreateDirectory(DatabaseDirectory(root));
            EnsureDatabaseFilesArePhysical(root);
            using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
            EnsureDatabaseFilesArePhysical(root);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (version > 2)
            {
                throw new InvalidOperationException($"Environmental observation outbox schema {version} is newer than supported schema 2.");
            }
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (version < 2)
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                command.Transaction = transaction;
                command.CommandText = MigrationV2Sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                command.Transaction = null;
            }
            command.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (version != 2)
            {
                throw new InvalidOperationException($"Environmental observation outbox schema {version} is not supported.");
            }
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE (type = 'table' AND name IN ('environmental_observation_metadata','environmental_observation_outbox','environmental_observation_outbox_audit') AND sql LIKE '%STRICT%') OR (type = 'index' AND name IN ('ux_environment_identity','ix_environment_claim','ix_environment_lease'));";
            var schemaObjects = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (schemaObjects != 6)
            {
                throw new InvalidDataException("Environmental observation outbox schema check failed.");
            }
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'environmental_observation_outbox';";
            var tableSql = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            string[] requiredConstraints =
            [
                "payload_bytes = length(payload)",
                "status = 'leased' AND lease_owner IS NOT NULL AND lease_token IS NOT NULL",
                "status != 'leased' AND lease_owner IS NULL AND lease_token IS NULL"
            ];
            if (requiredConstraints.Any(fragment => !tableSql.Contains(fragment, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Environmental observation outbox constraints are not supported.");
            }
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'environmental_observation_metadata';";
            var metadataSql = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (!metadataSql.Contains("stored_count", StringComparison.Ordinal) ||
                !metadataSql.Contains("stored_bytes", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Environmental observation outbox metadata schema is not supported.");
            }
            await VerifyIndexAsync(
                connection,
                "ux_environment_identity",
                ["source_identity_sha256", "observation_id"],
                unique: true,
                cancellationToken).ConfigureAwait(false);
            await VerifyIndexAsync(
                connection,
                "ix_environment_claim",
                ["status", "next_attempt_unix_ms", "created_unix_ms", "record_id"],
                unique: false,
                cancellationToken).ConfigureAwait(false);
            await VerifyIndexAsync(
                connection,
                "ix_environment_lease",
                ["status", "lease_expires_unix_ms", "created_unix_ms", "record_id"],
                unique: false,
                cancellationToken).ConfigureAwait(false);
            command.CommandText = "PRAGMA integrity_check;";
            var integrity = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Environmental observation outbox SQLite integrity check failed.");
            }
            command.CommandText = """
                SELECT stored_count = (SELECT COUNT(*) FROM environmental_observation_outbox)
                    AND stored_bytes = (SELECT COALESCE(SUM(payload_bytes), 0) FROM environmental_observation_outbox)
                FROM environmental_observation_metadata WHERE metadata_key = 1;
                """;
            var metadataMatches = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (metadataMatches != 1)
            {
                throw new InvalidDataException("Environmental observation outbox metadata counters are inconsistent.");
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

    private async ValueTask ResolveDeadLetterAsync(
        string root,
        long recordId,
        string actor,
        string reason,
        bool replay,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        actor = actor.Trim();
        reason = reason.Trim();
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        string status;
        int payloadBytes;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT status, payload_bytes FROM environmental_observation_outbox WHERE record_id = $id AND status IN ('quarantined','terminal');";
            read.Parameters.AddWithValue("$id", recordId);
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Environmental observation dead-letter resolution requires one terminal or quarantined record.");
            }
            status = reader.GetString(0);
            payloadBytes = reader.GetInt32(1);
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = replay
            ? """
              UPDATE environmental_observation_outbox
              SET status = 'pending', attempt_count = 0, next_attempt_unix_ms = $now, last_reason = NULL,
                  lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL, updated_unix_ms = $now
              WHERE record_id = $id AND status IN ('quarantined','terminal');
              """
            : "DELETE FROM environmental_observation_outbox WHERE record_id = $id AND status IN ('quarantined','terminal');";
        command.Parameters.AddWithValue("$id", recordId);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Environmental observation dead-letter resolution requires one terminal or quarantined record.");
        }
        if (!replay)
        {
            using var metadata = connection.CreateCommand();
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE environmental_observation_metadata SET stored_count = stored_count - 1, stored_bytes = stored_bytes - $bytes WHERE metadata_key = 1;";
            metadata.Parameters.AddWithValue("$bytes", payloadBytes);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO environmental_observation_outbox_audit(
                    record_id, previous_status, action, actor, reason, occurred_unix_ms)
                VALUES($id, $status, $action, $actor, $reason, $now);
                """;
            audit.Parameters.AddWithValue("$id", recordId);
            audit.Parameters.AddWithValue("$status", status);
            audit.Parameters.AddWithValue("$action", replay ? "replay" : "abandon");
            audit.Parameters.AddWithValue("$actor", Bound(actor, 128));
            audit.Parameters.AddWithValue("$reason", Bound(reason, 512));
            audit.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var retention = connection.CreateCommand())
        {
            retention.Transaction = transaction;
            retention.CommandText = """
                DELETE FROM environmental_observation_outbox_audit
                WHERE audit_id <= COALESCE(
                    (SELECT audit_id FROM environmental_observation_outbox_audit
                     ORDER BY audit_id DESC LIMIT 1 OFFSET $maximum), 0);
                """;
            retention.Parameters.AddWithValue("$maximum", MaximumAuditRecords);
            await retention.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PruneOperationReceiptsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long nowUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM environmental_observation_outbox_audit
            WHERE operation_key IN (
                SELECT operation_key FROM environmental_observation_outbox_operations
                WHERE occurred_unix_ms < $cutoff
                   OR operation_key IN (
                       SELECT operation_key FROM environmental_observation_outbox_operations
                       ORDER BY occurred_unix_ms DESC, operation_key DESC
                       LIMIT -1 OFFSET $maximum));
            DELETE FROM environmental_observation_outbox_operations
            WHERE occurred_unix_ms < $cutoff
               OR operation_key IN (
                   SELECT operation_key FROM environmental_observation_outbox_operations
                   ORDER BY occurred_unix_ms DESC, operation_key DESC
                   LIMIT -1 OFFSET $maximum);
            DELETE FROM environmental_observation_outbox_audit
            WHERE audit_id IN (
                SELECT audit_id FROM environmental_observation_outbox_audit
                ORDER BY audit_id DESC LIMIT -1 OFFSET $maximum);
            """;
        command.Parameters.AddWithValue(
            "$cutoff",
            DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMilliseconds)
                .AddDays(-OperationReceiptRetentionDays)
                .ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$maximum", MaximumOperationReceipts);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommittedOperation?> ReadOperationAsync(
        string root,
        string operationKey,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        return await ReadOperationAsync(connection, transaction: null, operationKey, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<CommittedOperation?> ReadOperationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string operationKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT record_id, action, actor_kind, reason FROM environmental_observation_outbox_operations WHERE operation_key = $operation;";
        command.Parameters.AddWithValue("$operation", operationKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CommittedOperation(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private static OutboxOperationDisposition MatchOperation(
        CommittedOperation existing,
        long recordId,
        OutboxOperationAction action,
        string actorKind,
        string reasonCode)
    {
        if (existing.RecordId != recordId ||
            !string.Equals(existing.Action, FormatAction(action), StringComparison.Ordinal) ||
            !string.Equals(existing.ActorKind, actorKind, StringComparison.Ordinal) ||
            !string.Equals(existing.ReasonCode, reasonCode, StringComparison.Ordinal))
        {
            throw new OutboxOperationCollisionException("The operation key is already bound to a different request.");
        }
        return OutboxOperationDisposition.Duplicate;
    }

    private static void ValidateOperation(string operationKey, string actorKind, string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        if (operationKey.Length > 128 || operationKey.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException("Operation key is invalid.", nameof(operationKey));
        }
        if (actorKind is not ("owner" or "system"))
        {
            throw new ArgumentException("Actor kind is invalid.", nameof(actorKind));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if (reasonCode.Length > 64)
        {
            throw new ArgumentException("Reason code is invalid.", nameof(reasonCode));
        }
    }

    private static EnvironmentalOutboxOperationsRecord ReadOperationsRecord(SqliteDataReader reader)
    {
        var updatedUnixMilliseconds = reader.GetInt64(6);
        return new EnvironmentalOutboxOperationsRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
            DateTimeOffset.FromUnixTimeMilliseconds(updatedUnixMilliseconds),
            reader.IsDBNull(4) ? null : OutboxOperationsReasonCodes.Sanitize(reader.GetString(4)),
            CanReplay: true,
            CanAbandon: true,
            new EnvironmentalOutboxOperationsCursor(reader.GetInt64(0)));
    }

    private static string FormatAction(OutboxOperationAction action) => action switch
    {
        OutboxOperationAction.Replay => "replay",
        OutboxOperationAction.Abandon => "abandon",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private ValueTask TerminalSettleAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        string status,
        string reason,
        CancellationToken cancellationToken)
        => SettleAsync(root, lease, $"""
            UPDATE environmental_observation_outbox
            SET status = '{status}', last_reason = $reason, lease_owner = NULL, lease_token = NULL,
                lease_expires_unix_ms = NULL, updated_unix_ms = $now
            WHERE record_id = $id AND status = 'leased' AND lease_owner = $owner AND lease_token = $token
                AND lease_expires_unix_ms > $now;
            """, null, reason, cancellationToken);

    private async ValueTask SettleAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        string sql,
        DateTimeOffset? retryAtUtc,
        string? reason,
        CancellationToken cancellationToken,
        bool removesRecord = false)
    {
        ArgumentNullException.ThrowIfNull(lease);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", lease.Record.RecordId);
        command.Parameters.AddWithValue("$owner", lease.Owner);
        command.Parameters.AddWithValue("$token", lease.Token);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        if (retryAtUtc is { } next)
        {
            command.Parameters.AddWithValue("$next", next.ToUnixTimeMilliseconds());
        }
        if (reason is not null)
        {
            command.Parameters.AddWithValue("$reason", Bound(reason, 128));
        }
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new EnvironmentalObservationLeaseLostException(
                "Environmental observation lease was lost before settlement.");
        }
        if (removesRecord)
        {
            using var metadata = connection.CreateCommand();
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE environmental_observation_metadata SET stored_count = stored_count - 1, stored_bytes = stored_bytes - $bytes WHERE metadata_key = 1;";
            metadata.Parameters.AddWithValue("$bytes", lease.Record.PayloadBytes);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<EnvironmentalObservationOutboxRecord> ReadRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long recordId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload, payload_bytes, source_identity_sha256, content_sha256, observation_id, attempt_count FROM environmental_observation_outbox WHERE record_id = $id;";
        command.Parameters.AddWithValue("$id", recordId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Environmental observation disappeared during claim.");
        }
        var payload = (byte[])reader.GetValue(0);
        var parsed = EnvironmentalObservationJson.Parse(payload);
        var observation = parsed.Observation;
        if (observation is null || payload.Length != reader.GetInt32(1) ||
            !Guid.TryParse(reader.GetString(4), out var storedObservationId) ||
            observation.ObservationId != storedObservationId ||
            !FixedHash(EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation), reader.GetString(2)) ||
            !FixedHash(EnvironmentalObservationJson.ComputeContentSha256(observation), reader.GetString(3)))
        {
            throw new InvalidDataException("Environmental observation committed payload failed validation.");
        }
        return new EnvironmentalObservationOutboxRecord(
            recordId, observation, reader.GetString(2), reader.GetString(3), payload.Length, reader.GetInt32(5));
    }

    private static async ValueTask VerifyIndexAsync(
        SqliteConnection connection,
        string indexName,
        IReadOnlyList<string> expectedColumns,
        bool unique,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info('{indexName}');";
        var columns = new List<string>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(2));
            }
        }
        if (!columns.SequenceEqual(expectedColumns, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Environmental observation outbox index '{indexName}' is not supported.");
        }
        command.CommandText = "PRAGMA index_list('environmental_observation_outbox');";
        using var indexReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var uniquenessMatches = false;
        while (await indexReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(indexReader.GetString(1), indexName, StringComparison.Ordinal))
            {
                uniquenessMatches = (indexReader.GetInt64(2) != 0) == unique;
                break;
            }
        }
        if (!uniquenessMatches)
        {
            throw new InvalidDataException($"Environmental observation outbox index '{indexName}' uniqueness is not supported.");
        }
    }

    private async ValueTask<SqliteConnection> OpenAsync(string root, CancellationToken cancellationToken)
    {
        EnsureDatabaseFilesArePhysical(root);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath(root),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = busyTimeoutSeconds
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        EnsureDatabaseFilesArePhysical(root);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout={busyTimeoutSeconds * 1000};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static bool FixedHash(string expected, string actual)
        => actual.Length == expected.Length && actual.All(Uri.IsHexDigit) && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected), Convert.FromHexString(actual));

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.GetFullPath(root);
    }

    private static string DatabaseDirectory(string root) => Path.Combine(root, ".environment");
    private static string DatabasePath(string root) => Path.Combine(DatabaseDirectory(root), "environmental-observation-outbox.db");
    private static void EnsureDatabaseFilesArePhysical(string root)
    {
        var databasePath = DatabasePath(root);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, databasePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-wal"));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-shm"));
    }
    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public void Dispose()
    {
        foreach (var gate in _initializationGates.Values)
        {
            gate.Dispose();
        }
    }

    private sealed record CommittedOperation(
        long RecordId,
        string Action,
        string ActorKind,
        string ReasonCode);

    private const string OperationsSelectColumns = """
        SELECT record_id, status, attempt_count, payload_bytes, last_reason, created_unix_ms, updated_unix_ms
        FROM environmental_observation_outbox
        """;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS environmental_observation_metadata(
            metadata_key INTEGER NOT NULL PRIMARY KEY CHECK(metadata_key = 1),
            stored_count INTEGER NOT NULL DEFAULT 0 CHECK(stored_count >= 0),
            stored_bytes INTEGER NOT NULL DEFAULT 0 CHECK(stored_bytes >= 0),
            overflow_count INTEGER NOT NULL DEFAULT 0 CHECK(overflow_count >= 0)) STRICT;
        INSERT OR IGNORE INTO environmental_observation_metadata(metadata_key, stored_count, stored_bytes, overflow_count)
            VALUES(1, 0, 0, 0);
        CREATE TABLE IF NOT EXISTS environmental_observation_outbox_audit(
            audit_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            record_id INTEGER NOT NULL,
            previous_status TEXT NOT NULL CHECK(previous_status IN ('quarantined','terminal')),
            action TEXT NOT NULL CHECK(action IN ('replay','abandon')),
            actor TEXT NOT NULL CHECK(length(actor) BETWEEN 1 AND 128),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 512),
            occurred_unix_ms INTEGER NOT NULL) STRICT;
        CREATE TABLE IF NOT EXISTS environmental_observation_outbox(
            record_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            source_identity_sha256 TEXT NOT NULL CHECK(length(source_identity_sha256) = 64),
            observation_id TEXT NOT NULL,
            content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
            target_site_id TEXT NOT NULL,
            target_agent_id TEXT NOT NULL,
            payload BLOB NOT NULL,
            payload_bytes INTEGER NOT NULL CHECK(payload_bytes > 0),
            status TEXT NOT NULL CHECK(status IN ('pending','leased','retry','quarantined','terminal')),
            attempt_count INTEGER NOT NULL CHECK(attempt_count >= 0),
            next_attempt_unix_ms INTEGER NOT NULL,
            lease_owner TEXT NULL,
            lease_token TEXT NULL,
            lease_expires_unix_ms INTEGER NULL,
            last_reason TEXT NULL,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            CHECK(payload_bytes = length(payload)),
            CHECK((status = 'leased' AND lease_owner IS NOT NULL AND lease_token IS NOT NULL AND lease_expires_unix_ms IS NOT NULL)
                OR (status != 'leased' AND lease_owner IS NULL AND lease_token IS NULL AND lease_expires_unix_ms IS NULL))) STRICT;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_environment_identity
            ON environmental_observation_outbox(source_identity_sha256, observation_id);
        CREATE INDEX IF NOT EXISTS ix_environment_operations
            ON environmental_observation_outbox(status, record_id DESC);
        CREATE INDEX IF NOT EXISTS ix_environment_claim
            ON environmental_observation_outbox(status, next_attempt_unix_ms, created_unix_ms, record_id);
        CREATE INDEX IF NOT EXISTS ix_environment_lease
            ON environmental_observation_outbox(status, lease_expires_unix_ms, created_unix_ms, record_id);
        """;

    private const string MigrationV2Sql = """
        ALTER TABLE environmental_observation_outbox_audit ADD COLUMN operation_key TEXT NULL;
        CREATE UNIQUE INDEX ux_environment_audit_operation
            ON environmental_observation_outbox_audit(operation_key) WHERE operation_key IS NOT NULL;
        CREATE TABLE environmental_observation_outbox_operations(
            operation_key TEXT NOT NULL PRIMARY KEY CHECK(length(operation_key) BETWEEN 1 AND 128),
            record_id INTEGER NOT NULL,
            action TEXT NOT NULL CHECK(action IN ('replay','abandon')),
            actor_kind TEXT NOT NULL CHECK(actor_kind IN ('owner','system')),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 64),
            occurred_unix_ms INTEGER NOT NULL
        ) STRICT;
        PRAGMA user_version=2;
        """;
}
