using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

/// <summary>Per-storage-root SQLite WAL outbox for immutable artifact manifests.</summary>
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Microsoft.Data.Sqlite field getters are in-memory accessors after an asynchronous row read.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Dynamically selected query fragments are internal constants; all values remain parameterized.")]
public sealed class SqliteArtifactOutbox(
    TimeProvider? timeProvider = null,
    int busyTimeoutSeconds = 5) : IArtifactOutbox, IDisposable
{
    internal const int MaximumOperationReceipts = 10_000;
    internal const int OperationReceiptRetentionDays = 30;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly int _busyTimeoutSeconds = busyTimeoutSeconds;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initializationGates = new(PathComparer);
    private readonly HashSet<string> _initializedRoots = new(PathComparer);
    private readonly object _initializedLock = new();

    public async ValueTask InitializeAsync(string root, CancellationToken cancellationToken)
    {
        using var activity = ArtifactOutboxTelemetry.ActivitySource.StartActivity("outbox.initialize");
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

            var directory = OutboxDirectory(root);
            var existingState = await InspectExistingStateAsync(root, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(directory);
            EnsureDatabaseFilesArePhysical(root);
            using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
            EnsureDatabaseFilesArePhysical(root);
            if (!existingState)
            {
                using var transaction = BeginImmediate(connection);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = SchemaSql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                var result = Convert.ToString(
                    await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(result, "ok", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Artifact outbox SQLite integrity check failed.");
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

    public async ValueTask EnqueueAsync(
        string root,
        ArtifactManifestV2 manifest,
        CancellationToken cancellationToken)
    {
        using var activity = ArtifactOutboxTelemetry.ActivitySource.StartActivity("outbox.enqueue");
        ArgumentNullException.ThrowIfNull(manifest);
        var validation = manifest.Validate();
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Artifact manifest is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(manifest));
        }

        root = NormalizeRoot(root);
        await ValidatePayloadAsync(root, manifest, cancellationToken).ConfigureAwait(false);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        var bytes = CaptureContractJson.Serialize(manifest);
        var descriptor = manifest.Descriptor;
        await InsertAsync(
            root,
            manifest.IdempotencyKey,
            ArtifactOutboxManifestKind.ManifestV2,
            bytes,
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            manifest.RelativeArtifactPath,
            descriptor.Artifact.ChecksumSha256.ToUpperInvariant(),
            descriptor.Layout.ByteLength,
            descriptor.Artifact.MediaType,
            descriptor.Artifact.CreatedUtc,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask EnqueueAsync(
        string root,
        StructuredProcessingProductManifestV1 manifest,
        CancellationToken cancellationToken)
    {
        using var activity = ArtifactOutboxTelemetry.ActivitySource.StartActivity("outbox.enqueue");
        ArgumentNullException.ThrowIfNull(manifest);
        var validation = manifest.Validate();
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Structured product manifest is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(manifest));
        }

        root = NormalizeRoot(root);
        await ValidatePayloadAsync(root, manifest, cancellationToken).ConfigureAwait(false);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        var descriptor = manifest.Descriptor;
        await InsertAsync(
            root,
            manifest.IdempotencyKey,
            ArtifactOutboxManifestKind.StructuredProductV1,
            StructuredProcessingProductManifestJson.Serialize(manifest),
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            manifest.RelativeArtifactPath,
            descriptor.Artifact.ChecksumSha256.ToUpperInvariant(),
            descriptor.ByteLength,
            descriptor.Artifact.MediaType,
            descriptor.Artifact.CreatedUtc,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ArtifactOutboxLease?> ClaimAsync(
        string root,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var expires = now + leaseDuration;
        var token = Guid.NewGuid().ToString("N");
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        long? recordId;
        using (var candidate = connection.CreateCommand())
        {
            candidate.Transaction = transaction;
            candidate.CommandText = """
                SELECT record_id FROM (
                    SELECT * FROM (
                        SELECT record_id, next_attempt_unix_ms, created_unix_ms, idempotency_key
                        FROM artifact_outbox_records
                        WHERE status = 'pending'
                        ORDER BY next_attempt_unix_ms, created_unix_ms, idempotency_key
                        LIMIT 1)
                    UNION ALL
                    SELECT * FROM (
                        SELECT record_id, next_attempt_unix_ms, created_unix_ms, idempotency_key
                        FROM artifact_outbox_records
                        WHERE status = 'retry' AND next_attempt_unix_ms <= $now
                        ORDER BY next_attempt_unix_ms, created_unix_ms, idempotency_key
                        LIMIT 1)
                    UNION ALL
                    SELECT * FROM (
                        SELECT record_id, next_attempt_unix_ms, created_unix_ms, idempotency_key
                        FROM artifact_outbox_records
                        WHERE status = 'leased' AND lease_expires_unix_ms <= $now
                        ORDER BY lease_expires_unix_ms, created_unix_ms, idempotency_key
                        LIMIT 1)
                )
                ORDER BY next_attempt_unix_ms, created_unix_ms, idempotency_key
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
                UPDATE artifact_outbox_records
                SET status = 'leased', attempt_count = attempt_count + 1,
                    lease_owner = $owner, lease_token = $token, lease_expires_unix_ms = $expires,
                    updated_unix_ms = $now
                WHERE record_id = $record_id AND (
                    status = 'pending'
                    OR (status = 'retry' AND next_attempt_unix_ms <= $now)
                    OR (status = 'leased' AND lease_expires_unix_ms <= $now));
                """;
            update.Parameters.AddWithValue("$owner", Bound(owner, 128));
            update.Parameters.AddWithValue("$token", token);
            update.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$record_id", recordId.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ArtifactOutboxLeaseLostException("Artifact outbox candidate changed before its lease was committed.");
            }
        }
        ArtifactOutboxRecord record;
        try
        {
            record = await ReadByIdAsync(connection, transaction, recordId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            string idempotencyKey;
            using (var keyCommand = connection.CreateCommand())
            {
                keyCommand.Transaction = transaction;
                keyCommand.CommandText = "SELECT idempotency_key FROM artifact_outbox_records WHERE record_id = $record_id;";
                keyCommand.Parameters.AddWithValue("$record_id", recordId.Value);
                idempotencyKey = Convert.ToString(
                    await keyCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture)
                    ?? throw new InvalidDataException("Malformed outbox record has no identity.");
            }
            using (var quarantine = connection.CreateCommand())
            {
                quarantine.Transaction = transaction;
                quarantine.CommandText = """
                    UPDATE artifact_outbox_records
                    SET status = 'quarantined', last_reason = 'malformed-committed-record',
                        lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL,
                        updated_unix_ms = $now
                    WHERE record_id = $record_id;
                    """;
                quarantine.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                quarantine.Parameters.AddWithValue("$record_id", recordId.Value);
                await quarantine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await InsertAuditAsync(
                connection, transaction, idempotencyKey, "quarantine", "claim", "malformed-committed-record", cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ArtifactOutboxLease(record, record.LeaseOwner!, token, expires);
    }

    public async ValueTask RetryAsync(
        string root,
        ArtifactOutboxLease lease,
        DateTimeOffset nextAttemptUtc,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        EnsureUtc(nextAttemptUtc, nameof(nextAttemptUtc));
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        await EnsureOwnedAsync(connection, transaction, lease, cancellationToken).ConfigureAwait(false);
        await TransitionLeaseAsync(
            connection, transaction, lease, "retry", reason, nextAttemptUtc, acknowledgement: null, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RenewAsync(
        string root,
        ArtifactOutboxLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var now = _timeProvider.GetUtcNow();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE artifact_outbox_records
            SET lease_expires_unix_ms = $expires, updated_unix_ms = $now
            WHERE idempotency_key = $key AND status = 'leased'
              AND lease_owner = $owner AND lease_token = $token AND lease_expires_unix_ms > $now;
            """;
        command.Parameters.AddWithValue("$expires", (now + leaseDuration).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$key", lease.Record.IdempotencyKey);
        command.Parameters.AddWithValue("$owner", lease.Owner);
        command.Parameters.AddWithValue("$token", lease.Token);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ArtifactOutboxLeaseLostException("Artifact outbox lease ownership was lost.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AcknowledgeAsync(
        string root,
        ArtifactOutboxLease lease,
        ArtifactUploadAcknowledgement acknowledgement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        acknowledgement.Validate();
        var delivery = ArtifactUploadClient.ResolveDelivery(lease.Record);
        if (!string.Equals(acknowledgement.IdempotencyKey, delivery.IdempotencyKey, StringComparison.OrdinalIgnoreCase)
            || acknowledgement.ArtifactId != delivery.ArtifactId
            || !string.Equals(acknowledgement.ChecksumSha256, delivery.ChecksumSha256, StringComparison.OrdinalIgnoreCase)
            || acknowledgement.ByteLength != delivery.ByteLength
            || !string.Equals(acknowledgement.AcceptedManifestSchemaVersion, delivery.SchemaVersion, StringComparison.Ordinal))
        {
            throw new ArtifactOutboxConflictException("Artifact upload acknowledgement does not match the leased delivery.");
        }
        var acknowledgementBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(acknowledgement);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var ownership = await ReadOwnershipAsync(connection, transaction, lease.Record.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (ownership.Status == "acknowledged" &&
            string.Equals(ownership.CompletionToken, lease.Token, StringComparison.Ordinal))
        {
            if (ownership.Acknowledgement is null || !ownership.Acknowledgement.AsSpan().SequenceEqual(acknowledgementBytes))
            {
                throw new ArtifactOutboxConflictException("Duplicate acknowledgement evidence conflicts with the committed acknowledgement.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        EnsureOwned(lease, ownership, _timeProvider.GetUtcNow());
        await TransitionLeaseAsync(
            connection,
            transaction,
            lease,
            "acknowledged",
            reason: null,
            _timeProvider.GetUtcNow(),
            acknowledgementBytes,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask QuarantineAsync(
        string root,
        ArtifactOutboxLease lease,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        await EnsureOwnedAsync(connection, transaction, lease, cancellationToken).ConfigureAwait(false);
        await TransitionLeaseAsync(
            connection, transaction, lease, "quarantined", reason, _timeProvider.GetUtcNow(), acknowledgement: null, cancellationToken)
            .ConfigureAwait(false);
        await InsertAuditAsync(
            connection, transaction, lease.Record.IdempotencyKey, "quarantine", lease.Owner, reason, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ReplayAsync(
        string root,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken)
        => ResolveQuarantineAsync(root, idempotencyKey, actor, reason, abandon: false, cancellationToken);

    public ValueTask AbandonAsync(
        string root,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken)
        => ResolveQuarantineAsync(root, idempotencyKey, actor, reason, abandon: true, cancellationToken);

    public async ValueTask<ArtifactOutboxRecord?> ReadAsync(
        string root,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = string.Concat(SelectColumns, " WHERE idempotency_key = $key;");
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? await ReadRecordAsync(reader, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async ValueTask<IReadOnlyList<ArtifactOutboxAuditEntry>> ReadAuditAsync(
        string root,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT audit_id, action, actor, reason, occurred_unix_ms
            FROM artifact_outbox_audit
            WHERE idempotency_key = $key
            ORDER BY audit_id;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        var entries = new List<ArtifactOutboxAuditEntry>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new ArtifactOutboxAuditEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
        }
        return entries;
    }

    public async ValueTask<ArtifactOutboxOperationsPage> ReadOperationsPageAsync(
        string root,
        int pageSize,
        ArtifactOutboxOperationsCursor? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = string.Concat(
            OperationsSelectColumns,
            cursor is null
                ? " WHERE status = 'quarantined' ORDER BY record_id DESC LIMIT $limit;"
                : " WHERE status = 'quarantined' AND record_id < $record_id ORDER BY record_id DESC LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$record_id", cursor.RecordId);
        }
        var records = new List<ArtifactOutboxOperationsRecord>(pageSize + 1);
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
        return new ArtifactOutboxOperationsPage(
            records,
            hasMore && records.Count > 0 ? records[^1].Cursor : null);
    }

    public async ValueTask<ArtifactOutboxOperationsRecord?> ReadOperationsDetailAsync(
        string root,
        string recordKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = string.Concat(
            OperationsSelectColumns,
            " WHERE idempotency_key = $key AND status = 'quarantined';");
        command.Parameters.AddWithValue("$key", recordKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadOperationsRecord(reader) : null;
    }

    public async ValueTask<OutboxOperationsAuditPage> ReadOperationsAuditAsync(
        string root,
        string recordKey,
        int pageSize,
        OutboxOperationsAuditCursor? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = cursor is null
            ? "SELECT audit_id, action, actor, reason, occurred_unix_ms FROM artifact_outbox_audit WHERE idempotency_key = $key ORDER BY audit_id DESC LIMIT $limit;"
            : "SELECT audit_id, action, actor, reason, occurred_unix_ms FROM artifact_outbox_audit WHERE idempotency_key = $key AND audit_id < $sequence ORDER BY audit_id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$key", recordKey);
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
                BoundOutput(reader.GetString(1), 32),
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
        string recordKey,
        OutboxOperationAction action,
        string operationKey,
        string actorKind,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        ValidateOperation(operationKey, actorKind, reasonCode);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        var existing = await ReadOperationAsync(root, operationKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return MatchOperation(existing, recordKey, action, actorKind, reasonCode);
        }
        if (action == OutboxOperationAction.Replay)
        {
            var record = await ReadAsync(root, recordKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Artifact outbox resolution requires one quarantined record.");
            await ValidateReplayEvidenceAsync(root, record, cancellationToken).ConfigureAwait(false);
        }

        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        existing = await ReadOperationAsync(connection, transaction, operationKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var disposition = MatchOperation(existing, recordKey, action, actorKind, reasonCode);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return disposition;
        }

        var now = _timeProvider.GetUtcNow();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = action == OutboxOperationAction.Abandon
                ? "UPDATE artifact_outbox_records SET status = 'abandoned', terminal_actor = $actor, terminal_reason = $reason, terminal_unix_ms = $now, updated_unix_ms = $now WHERE idempotency_key = $key AND status = 'quarantined';"
                : "UPDATE artifact_outbox_records SET status = 'pending', next_attempt_unix_ms = $now, last_reason = NULL, lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL, updated_unix_ms = $now WHERE idempotency_key = $key AND status = 'quarantined' AND manifest_kind != 'malformed-legacy';";
            update.Parameters.AddWithValue("$actor", actorKind);
            update.Parameters.AddWithValue("$reason", reasonCode);
            update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$key", recordKey);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Artifact outbox resolution requires one quarantined record.");
            }
        }
        using (var operation = connection.CreateCommand())
        {
            operation.Transaction = transaction;
            operation.CommandText = "INSERT INTO artifact_outbox_operations(operation_key, idempotency_key, action, actor_kind, reason, occurred_unix_ms) VALUES($operation, $key, $action, $actor, $reason, $now);";
            operation.Parameters.AddWithValue("$operation", operationKey);
            operation.Parameters.AddWithValue("$key", recordKey);
            operation.Parameters.AddWithValue("$action", FormatAction(action));
            operation.Parameters.AddWithValue("$actor", actorKind);
            operation.Parameters.AddWithValue("$reason", reasonCode);
            operation.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertOperationsAuditAsync(
            connection, transaction, recordKey, operationKey, FormatAction(action), actorKind, reasonCode, cancellationToken)
            .ConfigureAwait(false);
        await PruneOperationReceiptsAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return OutboxOperationDisposition.Applied;
    }

    public async ValueTask<IReadOnlyList<ArtifactOutboxRetentionHold>> GetRetentionHoldsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id, relative_artifact_path, status, manifest_bytes, media_type
            FROM artifact_outbox_records
            WHERE status IN ('pending', 'leased', 'retry', 'quarantined')
              AND artifact_id IS NOT NULL AND relative_artifact_path IS NOT NULL
            ORDER BY created_unix_ms, idempotency_key;
            """;
        var holds = new List<ArtifactOutboxRetentionHold>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var relativeArtifactPath = reader.GetString(1);
            var manifestBytes = await reader.GetFieldValueAsync<byte[]>(3, cancellationToken).ConfigureAwait(false);
            var structured = !reader.IsDBNull(4) && StructuredProcessingProductContracts.IsSupportedMediaType(reader.GetString(4)) ||
                StructuredProcessingProductManifestJson.Parse(manifestBytes).IsValid;
            holds.Add(new ArtifactOutboxRetentionHold(
                Guid.ParseExact(reader.GetString(0), "N"),
                relativeArtifactPath,
                Path.ChangeExtension(relativeArtifactPath, structured ? ".manifest.json" : ".json"),
                ParseStatus(reader.GetString(2))));
        }
        return holds;
    }

    public async ValueTask<IReadOnlyList<Guid>> GetAcknowledgedArtifactIdsAsync(
        string root,
        IReadOnlySet<Guid> artifactIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifactIds);
        if (artifactIds.Count == 0)
        {
            return [];
        }
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        var parameterNames = artifactIds.Select((artifactId, index) =>
        {
            var name = $"$artifact{index}";
            command.Parameters.AddWithValue(name, artifactId.ToString("N"));
            return name;
        }).ToArray();
        command.CommandText = $"""
            SELECT DISTINCT artifact_id
            FROM artifact_outbox_records
            WHERE status = 'acknowledged'
              AND artifact_id IN ({string.Join(", ", parameterNames)});
            """;
        var acknowledged = new List<Guid>(artifactIds.Count);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            acknowledged.Add(Guid.ParseExact(reader.GetString(0), "N"));
        }
        return acknowledged;
    }

    public async ValueTask<ArtifactOutboxSnapshot> GetSnapshotAsync(
        string root,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN status IN ('pending', 'leased', 'retry', 'quarantined') THEN 1 ELSE 0 END),
                SUM(CASE WHEN status IN ('pending', 'leased', 'retry', 'quarantined') THEN COALESCE(payload_length, 0) ELSE 0 END),
                MIN(CASE WHEN status IN ('pending', 'leased', 'retry', 'quarantined') THEN created_unix_ms END),
                SUM(CASE WHEN status = 'pending' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'leased' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'retry' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'acknowledged' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'quarantined' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'abandoned' THEN 1 ELSE 0 END)
            FROM artifact_outbox_records;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        static long ReadCount(SqliteDataReader value, int ordinal) => value.IsDBNull(ordinal) ? 0 : value.GetInt64(ordinal);
        return new ArtifactOutboxSnapshot(
            ReadCount(reader, 0),
            ReadCount(reader, 1),
            reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            ReadCount(reader, 3), ReadCount(reader, 4), ReadCount(reader, 5),
            ReadCount(reader, 6), ReadCount(reader, 7), ReadCount(reader, 8), _timeProvider.GetUtcNow());
    }

    private async ValueTask ResolveQuarantineAsync(
        string root,
        string idempotencyKey,
        string actor,
        string reason,
        bool abandon,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        if (!abandon)
        {
            var record = await ReadAsync(root, idempotencyKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Artifact outbox resolution requires one quarantined record.");
            await ValidateReplayEvidenceAsync(root, record, cancellationToken).ConfigureAwait(false);
        }
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var now = _timeProvider.GetUtcNow();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = abandon
                ? """
                  UPDATE artifact_outbox_records
                  SET status = 'abandoned', terminal_actor = $actor, terminal_reason = $reason,
                      terminal_unix_ms = $now, updated_unix_ms = $now
                  WHERE idempotency_key = $key AND status = 'quarantined';
                  """
                : """
                  UPDATE artifact_outbox_records
                  SET status = 'pending', next_attempt_unix_ms = $now, last_reason = NULL,
                      lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL,
                      updated_unix_ms = $now
                    WHERE idempotency_key = $key AND status = 'quarantined';
                  """;
            command.Parameters.AddWithValue("$actor", Bound(actor, 128));
            command.Parameters.AddWithValue("$reason", Bound(reason, 512));
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$key", idempotencyKey);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Artifact outbox resolution requires one quarantined record.");
            }
        }
        await InsertAuditAsync(
            connection, transaction, idempotencyKey, abandon ? "abandon" : "replay", actor, reason, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask InsertAsync(
        string root,
        string idempotencyKey,
        ArtifactOutboxManifestKind manifestKind,
        byte[] manifestBytes,
        Guid artifactId,
        FrameArtifactRole role,
        string relativeArtifactPath,
        string payloadSha256,
        long payloadLength,
        string mediaType,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO artifact_outbox_records(
                    idempotency_key, manifest_kind, manifest_bytes, artifact_id, role,
                    relative_artifact_path, payload_sha256, payload_length, media_type,
                    status, attempt_count, next_attempt_unix_ms, created_unix_ms, updated_unix_ms)
                VALUES ($key, $kind, $bytes, $artifact, $role, $path, $sha, $length, $media,
                        'pending', 0, $next, $created, $now)
                ON CONFLICT(idempotency_key) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$key", idempotencyKey);
            insert.Parameters.AddWithValue("$kind", FormatKind(manifestKind));
            insert.Parameters.AddWithValue("$bytes", manifestBytes);
            insert.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
            insert.Parameters.AddWithValue("$role", role.ToString());
            insert.Parameters.AddWithValue("$path", relativeArtifactPath);
            insert.Parameters.AddWithValue("$sha", payloadSha256);
            insert.Parameters.AddWithValue("$length", payloadLength);
            insert.Parameters.AddWithValue("$media", mediaType);
            insert.Parameters.AddWithValue("$next", now.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$created", createdUtc.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        byte[] existing;
        using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "SELECT manifest_bytes FROM artifact_outbox_records WHERE idempotency_key = $key;";
            verify.Parameters.AddWithValue("$key", idempotencyKey);
            existing = (byte[])(await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Artifact outbox enqueue did not commit a record."));
        }
        if (!existing.AsSpan().SequenceEqual(manifestBytes))
        {
            await InsertConflictAsync(
                connection, transaction, idempotencyKey, manifestBytes, "canonical-manifest-conflict", cancellationToken)
                .ConfigureAwait(false);
            if (await QuarantineByKeyAsync(
                    connection, transaction, idempotencyKey, "canonical-manifest-conflict", cancellationToken)
                .ConfigureAwait(false))
            {
                await InsertAuditAsync(
                    connection,
                    transaction,
                    idempotencyKey,
                    "quarantine",
                    "enqueue",
                    "canonical-manifest-conflict",
                    cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new ArtifactOutboxConflictException("Artifact outbox idempotency key conflicts with committed canonical evidence.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ArtifactOutboxRecord> ReadByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long recordId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Concat(SelectColumns, " WHERE record_id = $record_id;");
        command.Parameters.AddWithValue("$record_id", recordId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Leased artifact outbox record disappeared.");
        }
        return await ReadRecordAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ArtifactOutboxRecord> ReadRecordAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadRecordCoreAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Committed artifact outbox record is malformed.", exception);
        }
    }

    private static async ValueTask<ArtifactOutboxRecord> ReadRecordCoreAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var kind = ParseKind(reader.GetString(2));
        var bytes = await reader.GetFieldValueAsync<byte[]>(3, cancellationToken).ConfigureAwait(false);
        var status = ParseStatus(reader.GetString(10));
        ArtifactManifestDocument? manifest = null;
        StructuredProcessingProductManifestV1? productManifest = null;
        if (kind == ArtifactOutboxManifestKind.ManifestV2)
        {
            var parsed = CaptureContractJson.ParseManifest(bytes);
            if (parsed.IsValid && parsed.Document is not null)
            {
                manifest = parsed.Document;
            }
            else
            {
                var productParsed = StructuredProcessingProductManifestJson.Parse(bytes);
                if (!productParsed.IsValid || productParsed.Manifest is null)
                {
                    if (status != ArtifactOutboxStatus.Quarantined)
                    {
                        throw new InvalidDataException("Committed current artifact outbox manifest bytes are invalid.");
                    }
                }
                else
                {
                    kind = ArtifactOutboxManifestKind.StructuredProductV1;
                    productManifest = productParsed.Manifest;
                }
            }
        }
        return new ArtifactOutboxRecord(
            reader.GetString(1), kind, bytes, manifest,
            reader.IsDBNull(4) ? null : Guid.ParseExact(reader.GetString(4), "N"),
            reader.IsDBNull(5) ? null : Enum.Parse<FrameArtifactRole>(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            status,
            reader.GetInt32(11),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(13)),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(16)),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : await reader.GetFieldValueAsync<byte[]>(18, cancellationToken).ConfigureAwait(false))
        {
            ProductManifest = productManifest
        };
    }

    private async ValueTask EnsureOwnedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArtifactOutboxLease lease,
        CancellationToken cancellationToken)
    {
        var ownership = await ReadOwnershipAsync(
            connection, transaction, lease.Record.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        EnsureOwned(lease, ownership, _timeProvider.GetUtcNow());
    }

    private static void EnsureOwned(ArtifactOutboxLease lease, Ownership ownership, DateTimeOffset now)
    {
        if (ownership.Status != "leased" ||
            !string.Equals(ownership.LeaseOwner, lease.Owner, StringComparison.Ordinal) ||
            !string.Equals(ownership.LeaseToken, lease.Token, StringComparison.Ordinal) ||
            ownership.LeaseExpiresUtc is null || ownership.LeaseExpiresUtc <= now)
        {
            throw new ArtifactOutboxLeaseLostException("Artifact outbox lease ownership was lost.");
        }
    }

    private static async ValueTask<Ownership> ReadOwnershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status, lease_owner, lease_token, lease_expires_unix_ms, completion_token, acknowledgement
            FROM artifact_outbox_records WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ArtifactOutboxLeaseLostException("Artifact outbox lease record no longer exists.");
        }
        return new Ownership(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : await reader.GetFieldValueAsync<byte[]>(5, cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask TransitionLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArtifactOutboxLease lease,
        string status,
        string? reason,
        DateTimeOffset nextAttemptUtc,
        byte[]? acknowledgement,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE artifact_outbox_records
            SET status = $status, next_attempt_unix_ms = $next,
                lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL,
                completion_token = CASE WHEN $status = 'acknowledged' THEN $token ELSE completion_token END,
                acknowledgement = CASE WHEN $status = 'acknowledged' THEN $ack ELSE acknowledgement END,
                acknowledged_unix_ms = CASE WHEN $status = 'acknowledged' THEN $now ELSE acknowledged_unix_ms END,
                last_reason = $reason, updated_unix_ms = $now
            WHERE idempotency_key = $key AND status = 'leased'
              AND lease_owner = $owner AND lease_token = $token AND lease_expires_unix_ms > $now;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$next", nextAttemptUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$token", lease.Token);
        command.Parameters.AddWithValue("$ack", (object?)acknowledgement ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", reason is null ? DBNull.Value : Bound(reason, 512));
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$key", lease.Record.IdempotencyKey);
        command.Parameters.AddWithValue("$owner", lease.Owner);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ArtifactOutboxLeaseLostException("Artifact outbox lease ownership was lost.");
        }
    }

    private async ValueTask InsertAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string action,
        string actor,
        string reason,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO artifact_outbox_audit(idempotency_key, action, actor, reason, occurred_unix_ms)
            VALUES ($key, $action, $actor, $reason, $now);
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$actor", Bound(actor, 128));
        command.Parameters.AddWithValue("$reason", Bound(reason, 512));
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask InsertOperationsAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string recordKey,
        string operationKey,
        string action,
        string actorKind,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO artifact_outbox_audit(idempotency_key, action, actor, reason, occurred_unix_ms, operation_key) VALUES($key, $action, $actor, $reason, $now, $operation);";
        command.Parameters.AddWithValue("$key", recordKey);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$actor", actorKind);
        command.Parameters.AddWithValue("$reason", reasonCode);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$operation", operationKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PruneOperationReceiptsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM artifact_outbox_audit
            WHERE operation_key IN (
                SELECT operation_key FROM artifact_outbox_operations
                WHERE occurred_unix_ms < $cutoff
                   OR operation_key IN (
                       SELECT operation_key FROM artifact_outbox_operations
                       ORDER BY occurred_unix_ms DESC, operation_key DESC
                       LIMIT -1 OFFSET $maximum));
            DELETE FROM artifact_outbox_operations
            WHERE occurred_unix_ms < $cutoff
               OR operation_key IN (
                   SELECT operation_key FROM artifact_outbox_operations
                   ORDER BY occurred_unix_ms DESC, operation_key DESC
                   LIMIT -1 OFFSET $maximum);
            DELETE FROM artifact_outbox_audit
            WHERE audit_id IN (
                SELECT audit_id FROM artifact_outbox_audit
                ORDER BY audit_id DESC LIMIT -1 OFFSET $maximum);
            """;
        command.Parameters.AddWithValue("$cutoff", now.AddDays(-OperationReceiptRetentionDays).ToUnixTimeMilliseconds());
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
        command.CommandText = "SELECT idempotency_key, action, actor_kind, reason FROM artifact_outbox_operations WHERE operation_key = $operation;";
        command.Parameters.AddWithValue("$operation", operationKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CommittedOperation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private static OutboxOperationDisposition MatchOperation(
        CommittedOperation existing,
        string recordKey,
        OutboxOperationAction action,
        string actorKind,
        string reasonCode)
    {
        if (!string.Equals(existing.RecordKey, recordKey, StringComparison.Ordinal) ||
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

    private static ArtifactOutboxOperationsRecord ReadOperationsRecord(SqliteDataReader reader)
    {
        var kind = ParseKind(reader.GetString(2));
        var status = ParseStatus(reader.GetString(6));
        var updatedUnixMilliseconds = reader.GetInt64(10);
        FrameArtifactRole? role = null;
        if (!reader.IsDBNull(3) && Enum.TryParse<FrameArtifactRole>(reader.GetString(3), out var parsedRole))
        {
            role = parsedRole;
        }
        var isQuarantined = status == ArtifactOutboxStatus.Quarantined;
        return new ArtifactOutboxOperationsRecord(
            reader.GetString(1),
            kind,
            status,
            reader.GetInt32(7),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : BoundOutput(reader.GetString(5), 128),
            role,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
            DateTimeOffset.FromUnixTimeMilliseconds(updatedUnixMilliseconds),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            reader.IsDBNull(11) ? null : OutboxOperationsReasonCodes.Sanitize(reader.GetString(11)),
            isQuarantined,
            isQuarantined,
            new ArtifactOutboxOperationsCursor(reader.GetInt64(0)));
    }

    private static string FormatAction(OutboxOperationAction action) => action switch
    {
        OutboxOperationAction.Replay => "replay",
        OutboxOperationAction.Abandon => "abandon",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static string BoundOutput(string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength];

    private async ValueTask InsertConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        byte[] conflictingBytes,
        string reason,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO artifact_outbox_conflicts(
                idempotency_key, conflicting_manifest_bytes, reason, observed_unix_ms)
            VALUES ($key, $bytes, $reason, $now);
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$bytes", conflictingBytes);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> QuarantineByKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE artifact_outbox_records
            SET status = 'quarantined', last_reason = $reason,
                lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL,
                updated_unix_ms = $now
            WHERE idempotency_key = $key AND status != 'acknowledged' AND status != 'abandoned';
            """;
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$key", idempotencyKey);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async ValueTask<byte[]> ReadManifestBytesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT manifest_bytes FROM artifact_outbox_records WHERE idempotency_key = $key;";
        command.Parameters.AddWithValue("$key", idempotencyKey);
        return (byte[])(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Imported artifact outbox evidence was not committed."));
    }

    private static async ValueTask ValidatePayloadAsync(
        string root,
        ArtifactManifestV2 manifest,
        CancellationToken cancellationToken)
    {
        var relativePath = manifest.RelativeArtifactPath.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(root), Path.DirectorySeparatorChar);
        if (!path.StartsWith(prefix, PathComparison))
        {
            throw new InvalidDataException("Artifact outbox payload path escapes its storage root.");
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != manifest.Descriptor.Layout.ByteLength)
        {
            throw new InvalidDataException("Artifact outbox payload length differs from manifest v2.");
        }
        var sidecarPath = Path.ChangeExtension(path, ".json");
        RawIngressFileStore.EnsureNoSymbolicLinks(root, sidecarPath);
        if (!File.Exists(sidecarPath))
        {
            throw new InvalidDataException("Artifact outbox manifest-v2 sidecar is missing.");
        }
        // Sidecar publication already streamed and verified the immutable payload checksum.
        // Canonical equality transfers that validation without rereading every full frame here.
        var sidecarBytes = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        var parsed = CaptureContractJson.ParseManifest(sidecarBytes);
        if (!parsed.IsValid || parsed.Document?.Manifest is not { } sidecarManifest
            || !CaptureContractJson.Serialize(sidecarManifest).AsSpan().SequenceEqual(CaptureContractJson.Serialize(manifest)))
        {
            throw new InvalidDataException("Artifact outbox manifest v2 differs from its immutable sidecar.");
        }
    }

    private static async ValueTask ValidatePayloadAsync(
        string root,
        StructuredProcessingProductManifestV1 manifest,
        CancellationToken cancellationToken)
    {
        var relativePath = manifest.RelativeArtifactPath.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(root), Path.DirectorySeparatorChar);
        if (!path.StartsWith(prefix, PathComparison))
        {
            throw new InvalidDataException("Structured product outbox payload path escapes its storage root.");
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != manifest.Descriptor.ByteLength)
        {
            throw new InvalidDataException("Structured product payload length differs from its manifest.");
        }
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(checksum, manifest.Descriptor.Artifact.ChecksumSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Structured product payload checksum differs from its manifest.");
        }

        var sidecarPath = Path.ChangeExtension(path, ".manifest.json");
        RawIngressFileStore.EnsureNoSymbolicLinks(root, sidecarPath);
        if (!File.Exists(sidecarPath))
        {
            throw new InvalidDataException("Structured product durable sidecar is missing.");
        }
        var sidecarBytes = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        var sidecar = DurableProcessingProductManifestJson.Parse(sidecarBytes) as DurableTypedMetadataProductManifestV3;
        var descriptor = manifest.Descriptor;
        if (sidecar is null ||
            CanonicalJson(sidecar.Capture) != CanonicalJson(descriptor.SourceCapture.Capture) ||
            CanonicalJson(sidecar.Artifact) != CanonicalJson(descriptor.Artifact) ||
            sidecar.OutputIdentitySha256 != descriptor.OutputIdentitySha256 ||
            CanonicalJson(sidecar.Algorithms) != CanonicalJson(descriptor.Algorithms) ||
            CanonicalJson(sidecar.Compatibility) != CanonicalJson(descriptor.Compatibility) ||
            sidecar.TotalIntegrationTicks != descriptor.TotalIntegrationTicks ||
            sidecar.ByteLength != descriptor.ByteLength ||
            !string.Equals(sidecar.RelativeArtifactPath, manifest.RelativeArtifactPath, StringComparison.Ordinal) ||
            sidecar.Kind != descriptor.Kind ||
            sidecar.ProductSchemaVersion != descriptor.ProductSchemaVersion ||
            sidecar.ContentIdentitySha256 != descriptor.ContentIdentitySha256)
        {
            throw new InvalidDataException("Structured product manifest differs from its durable sidecar.");
        }
    }

    private static string CanonicalJson<T>(T value)
        => CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(value)).GetRawText();

    private static async ValueTask ValidateReplayEvidenceAsync(
        string root,
        ArtifactOutboxRecord record,
        CancellationToken cancellationToken)
    {
        if (record.Manifest is null && record.ProductManifest is null
            || string.IsNullOrWhiteSpace(record.RelativeArtifactPath)
            || string.IsNullOrWhiteSpace(record.PayloadSha256)
            || record.PayloadLength is null)
        {
            throw new InvalidOperationException("Malformed or incomplete evidence cannot be replayed.");
        }
        if (record.Manifest?.Manifest is { } manifest)
        {
            await ValidatePayloadAsync(root, manifest, cancellationToken).ConfigureAwait(false);
        }
        else if (record.ProductManifest is { } productManifest)
        {
            await ValidatePayloadAsync(root, productManifest, cancellationToken).ConfigureAwait(false);
        }
        var path = Path.GetFullPath(Path.Combine(
            root, record.RelativeArtifactPath.Replace('/', Path.DirectorySeparatorChar)));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != record.PayloadLength.Value)
        {
            throw new InvalidOperationException("Artifact evidence must be restored before replay.");
        }
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var checksum = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(checksum, record.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Artifact evidence checksum must match before replay.");
        }
    }

    private static async ValueTask<bool> InspectExistingStateAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var databasePath = DatabasePath(root);
        if (!File.Exists(databasePath))
        {
            return false;
        }

        EnsureDatabaseFilesArePhysical(root);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tables = new HashSet<string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }
        if (tables.Count == 0)
        {
            throw new InvalidDataException("Existing artifact outbox database has no canonical schema.");
        }

        string[] requiredTables =
        [
            "artifact_outbox_schema",
            "artifact_outbox_records",
            "artifact_outbox_audit",
            "artifact_outbox_conflicts",
            "artifact_outbox_operations"
        ];
        if (requiredTables.Any(table => !tables.Contains(table)))
        {
            throw new InvalidDataException("Artifact outbox database is not canonical schema 2.");
        }

        var actualSchema = await ReadSchemaObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        var canonicalSchema = CanonicalSchemaObjects.Value;
        if (actualSchema.Count != canonicalSchema.Count ||
            canonicalSchema.Any(expected =>
                !actualSchema.TryGetValue(expected.Key, out var actual) ||
                !string.Equals(actual, expected.Value, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Artifact outbox database structure is not canonical schema 2.");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT version FROM artifact_outbox_schema WHERE schema_key = 1;";
            var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (version is null || Convert.ToInt32(version, System.Globalization.CultureInfo.InvariantCulture) != 2)
            {
                throw new InvalidOperationException("Artifact outbox database is not supported canonical schema 2.");
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Artifact outbox SQLite integrity check failed.");
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT idempotency_key, manifest_kind, manifest_bytes, artifact_id, role,
                       relative_artifact_path, payload_sha256, payload_length, media_type, status,
                       attempt_count, lease_owner, lease_token, lease_expires_unix_ms,
                       completion_token, acknowledgement, acknowledged_unix_ms,
                       terminal_actor, terminal_reason, terminal_unix_ms
                FROM artifact_outbox_records;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(1), "v2", StringComparison.Ordinal) ||
                    Enumerable.Range(3, 7).Any(reader.IsDBNull) || reader.GetInt32(10) < 0)
                {
                    throw new InvalidDataException("Artifact outbox contains incomplete current rows.");
                }

                var key = reader.GetString(0);
                var bytes = await reader.GetFieldValueAsync<byte[]>(2, cancellationToken).ConfigureAwait(false);
                var parsed = CaptureContractJson.ParseManifest(bytes);
                if (parsed.IsValid && parsed.Document?.Manifest is { } manifest)
                {
                    ValidateCanonicalRow(
                        reader, key, bytes, manifest.IdempotencyKey,
                        manifest.Descriptor.Artifact, manifest.RelativeArtifactPath,
                        manifest.Descriptor.Layout.ByteLength, manifest.SchemaVersion,
                        CaptureContractJson.Serialize(manifest));
                    continue;
                }

                var product = StructuredProcessingProductManifestJson.Parse(bytes);
                if (!product.IsValid || product.Manifest is not { } productManifest)
                {
                    throw new InvalidDataException("Artifact outbox contains invalid current manifest rows.");
                }
                ValidateCanonicalRow(
                    reader, key, bytes, productManifest.IdempotencyKey,
                    productManifest.Descriptor.Artifact, productManifest.RelativeArtifactPath,
                    productManifest.Descriptor.ByteLength, productManifest.SchemaVersion,
                    StructuredProcessingProductManifestJson.Serialize(productManifest));
            }
        }
        return true;
    }

    private static void ValidateCanonicalRow(
        SqliteDataReader reader,
        string key,
        byte[] bytes,
        string expectedKey,
        ArtifactDescriptor artifact,
        string relativeArtifactPath,
        long byteLength,
        string manifestSchemaVersion,
        byte[] canonicalBytes)
    {
        if (!string.Equals(key, expectedKey, StringComparison.Ordinal) ||
            !bytes.AsSpan().SequenceEqual(canonicalBytes) ||
            !string.Equals(reader.GetString(3), artifact.ArtifactId.ToString("N"), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(4), artifact.Role.ToString(), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(5), relativeArtifactPath, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(6), artifact.ChecksumSha256.ToUpperInvariant(), StringComparison.Ordinal) ||
            reader.GetInt64(7) != byteLength ||
            !string.Equals(reader.GetString(8), artifact.MediaType, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Artifact outbox current manifest identity is inconsistent.");
        }

        var status = reader.GetString(9);
        _ = ParseStatus(status);
        var leased = string.Equals(status, "leased", StringComparison.Ordinal);
        var acknowledged = string.Equals(status, "acknowledged", StringComparison.Ordinal);
        var abandoned = string.Equals(status, "abandoned", StringComparison.Ordinal);
        if (leased != !reader.IsDBNull(11) || leased != !reader.IsDBNull(12) || leased != !reader.IsDBNull(13) ||
            acknowledged != !reader.IsDBNull(14) || acknowledged != !reader.IsDBNull(15) || acknowledged != !reader.IsDBNull(16) ||
            abandoned != !reader.IsDBNull(17) || abandoned != !reader.IsDBNull(18) || abandoned != !reader.IsDBNull(19))
        {
            throw new InvalidDataException("Artifact outbox current row state is inconsistent.");
        }
        if (!acknowledged) return;

        ArtifactUploadAcknowledgement acknowledgement;
        try
        {
            acknowledgement = JsonSerializer.Deserialize<ArtifactUploadAcknowledgement>(
                reader.GetFieldValue<byte[]>(15))
                ?? throw new InvalidDataException("Artifact outbox acknowledgement evidence is null.");
            acknowledgement.Validate();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Artifact outbox acknowledgement evidence is invalid.", exception);
        }
        if (!string.Equals(acknowledgement.SchemaVersion, ArtifactUploadAcknowledgement.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.AcceptedManifestSchemaVersion, manifestSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.IdempotencyKey, key, StringComparison.OrdinalIgnoreCase) ||
            acknowledgement.ArtifactId != artifact.ArtifactId ||
            !string.Equals(acknowledgement.ChecksumSha256, artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase) ||
            acknowledgement.ByteLength != byteLength)
        {
            throw new InvalidDataException("Artifact outbox acknowledgement evidence conflicts with its canonical manifest.");
        }
    }

    private static async ValueTask<Dictionary<string, string>> ReadSchemaObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type, name;";
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
        schema.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type, name;";
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
            if (inLiteral || !char.IsWhiteSpace(character)) result.Add(character);
        }
        return new string(result.ToArray());
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static string OutboxDirectory(string root) => Path.Combine(root, "outbox");

    private static string DatabasePath(string root) => Path.Combine(OutboxDirectory(root), "artifact-outbox.db");

    private static void EnsureDatabaseFilesArePhysical(string root)
    {
        var databasePath = DatabasePath(root);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, databasePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-wal"));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-shm"));
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
        command.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout={checked(_busyTimeoutSeconds * 1000)};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static SqliteTransaction BeginImmediate(SqliteConnection connection)
    {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        return connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("UTC time is required.", parameterName);
        }
    }

    private static string Bound(string value, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static string FormatKind(ArtifactOutboxManifestKind kind) => kind switch
    {
        ArtifactOutboxManifestKind.ManifestV2 => "v2",
        ArtifactOutboxManifestKind.StructuredProductV1 => "v2",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static ArtifactOutboxManifestKind ParseKind(string value) => value switch
    {
        "v2" => ArtifactOutboxManifestKind.ManifestV2,
        _ => throw new InvalidDataException("Artifact outbox manifest kind is invalid.")
    };

    private static ArtifactOutboxStatus ParseStatus(string value) => value switch
    {
        "pending" => ArtifactOutboxStatus.Pending,
        "leased" => ArtifactOutboxStatus.Leased,
        "retry" => ArtifactOutboxStatus.Retry,
        "acknowledged" => ArtifactOutboxStatus.Acknowledged,
        "quarantined" => ArtifactOutboxStatus.Quarantined,
        "abandoned" => ArtifactOutboxStatus.Abandoned,
        _ => throw new InvalidDataException("Artifact outbox status is invalid.")
    };

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly Lazy<Dictionary<string, string>> CanonicalSchemaObjects =
        new(CreateCanonicalSchemaObjects, LazyThreadSafetyMode.ExecutionAndPublication);

    public void Dispose()
    {
        foreach (var gate in _initializationGates.Values)
        {
            gate.Dispose();
        }
    }

    private sealed record Ownership(
        string Status,
        string? LeaseOwner,
        string? LeaseToken,
        DateTimeOffset? LeaseExpiresUtc,
        string? CompletionToken,
        byte[]? Acknowledgement);

    private sealed record CommittedOperation(
        string RecordKey,
        string Action,
        string ActorKind,
        string ReasonCode);

    private const string OperationsSelectColumns = """
        SELECT record_id, idempotency_key, manifest_kind, role, payload_length, media_type,
               status, attempt_count, next_attempt_unix_ms, created_unix_ms, updated_unix_ms, last_reason
        FROM artifact_outbox_records
        """;

    private const string SelectColumns = """
        SELECT record_id, idempotency_key, manifest_kind, manifest_bytes, artifact_id, role,
               relative_artifact_path, payload_sha256, payload_length, media_type, status,
               attempt_count, next_attempt_unix_ms, created_unix_ms, lease_owner, lease_token,
               lease_expires_unix_ms, last_reason, acknowledgement
        FROM artifact_outbox_records
        """;

    private const string SchemaSql = """
        CREATE TABLE artifact_outbox_schema(
            schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
            version INTEGER NOT NULL CHECK(version = 2)
        ) STRICT;
        INSERT INTO artifact_outbox_schema(schema_key, version) VALUES (1, 2);

        CREATE TABLE artifact_outbox_records(
            record_id INTEGER PRIMARY KEY AUTOINCREMENT,
            idempotency_key TEXT NOT NULL UNIQUE CHECK(length(idempotency_key) = 64),
            manifest_kind TEXT NOT NULL CHECK(manifest_kind = 'v2'),
            manifest_bytes BLOB NOT NULL CHECK(length(manifest_bytes) > 0),
            artifact_id TEXT NOT NULL CHECK(length(artifact_id) = 32),
            role TEXT NOT NULL CHECK(role IN ('Raw', 'Calibrated', 'Combined', 'Preview', 'AnnotatedPreview', 'Metadata')),
            relative_artifact_path TEXT NOT NULL CHECK(length(relative_artifact_path) > 0),
            payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
            payload_length INTEGER NOT NULL CHECK(payload_length >= 0),
            media_type TEXT NOT NULL CHECK(length(media_type) > 0),
            status TEXT NOT NULL CHECK(status IN ('pending', 'leased', 'retry', 'acknowledged', 'quarantined', 'abandoned')),
            attempt_count INTEGER NOT NULL CHECK(attempt_count >= 0),
            next_attempt_unix_ms INTEGER NOT NULL,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            lease_owner TEXT NULL,
            lease_token TEXT NULL,
            lease_expires_unix_ms INTEGER NULL,
            completion_token TEXT NULL,
            last_reason TEXT NULL,
            acknowledgement BLOB NULL,
            acknowledged_unix_ms INTEGER NULL,
            terminal_actor TEXT NULL,
            terminal_reason TEXT NULL,
            terminal_unix_ms INTEGER NULL,
            CHECK((status = 'leased') = (lease_owner IS NOT NULL)),
            CHECK((status = 'leased') = (lease_token IS NOT NULL)),
            CHECK((status = 'leased') = (lease_expires_unix_ms IS NOT NULL)),
            CHECK((status = 'acknowledged') = (completion_token IS NOT NULL)),
            CHECK((status = 'acknowledged') = (acknowledgement IS NOT NULL)),
            CHECK((status = 'acknowledged') = (acknowledged_unix_ms IS NOT NULL)),
            CHECK((status = 'abandoned') = (terminal_actor IS NOT NULL)),
            CHECK((status = 'abandoned') = (terminal_reason IS NOT NULL)),
            CHECK((status = 'abandoned') = (terminal_unix_ms IS NOT NULL))
        ) STRICT;

        CREATE INDEX ix_artifact_outbox_claim
            ON artifact_outbox_records(status, next_attempt_unix_ms, created_unix_ms, idempotency_key);
        CREATE INDEX ix_artifact_outbox_lease
            ON artifact_outbox_records(status, lease_expires_unix_ms, created_unix_ms, idempotency_key);
        CREATE INDEX ix_artifact_outbox_operations
            ON artifact_outbox_records(status, record_id DESC);

        CREATE TABLE artifact_outbox_audit(
            audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
            idempotency_key TEXT NOT NULL,
            action TEXT NOT NULL,
            actor TEXT NOT NULL,
            reason TEXT NOT NULL,
            occurred_unix_ms INTEGER NOT NULL,
            operation_key TEXT NULL,
            FOREIGN KEY(idempotency_key) REFERENCES artifact_outbox_records(idempotency_key)
        ) STRICT;

        CREATE UNIQUE INDEX ux_artifact_outbox_audit_operation
            ON artifact_outbox_audit(operation_key) WHERE operation_key IS NOT NULL;

        CREATE TABLE artifact_outbox_conflicts(
            conflict_id INTEGER PRIMARY KEY AUTOINCREMENT,
            idempotency_key TEXT NOT NULL,
            conflicting_manifest_bytes BLOB NOT NULL,
            reason TEXT NOT NULL,
            observed_unix_ms INTEGER NOT NULL,
            FOREIGN KEY(idempotency_key) REFERENCES artifact_outbox_records(idempotency_key)
        ) STRICT;

        CREATE TABLE artifact_outbox_operations(
            operation_key TEXT NOT NULL PRIMARY KEY CHECK(length(operation_key) BETWEEN 1 AND 128),
            idempotency_key TEXT NOT NULL,
            action TEXT NOT NULL CHECK(action IN ('replay','abandon')),
            actor_kind TEXT NOT NULL CHECK(actor_kind IN ('owner','system')),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 64),
            occurred_unix_ms INTEGER NOT NULL,
            FOREIGN KEY(idempotency_key) REFERENCES artifact_outbox_records(idempotency_key)
        ) STRICT;
        """;
}
