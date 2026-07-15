using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

/// <summary>Per-storage-root SQLite WAL outbox for immutable artifact manifests.</summary>
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Microsoft.Data.Sqlite field getters are in-memory accessors after an asynchronous row read.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Dynamically selected query fragments are internal constants; all values remain parameterized.")]
public sealed class SqliteArtifactOutbox(
    TimeProvider? timeProvider = null,
    int busyTimeoutSeconds = 5) : IArtifactOutbox, IDisposable
{
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
            Directory.CreateDirectory(directory);
            EnsureDatabaseFilesArePhysical(root);
            using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
            EnsureDatabaseFilesArePhysical(root);
            using (var existingSchema = connection.CreateCommand())
            {
                existingSchema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'artifact_outbox_schema';";
                var schemaExists = Convert.ToInt32(
                    await existingSchema.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) == 1;
                if (schemaExists)
                {
                    existingSchema.CommandText = "SELECT COALESCE((SELECT version FROM artifact_outbox_schema WHERE schema_key = 1), 0);";
                    var existingVersion = Convert.ToInt32(
                        await existingSchema.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (existingVersion > 1)
                    {
                        throw new InvalidOperationException($"Artifact outbox schema {existingVersion} is newer than supported schema 1.");
                    }
                }
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = SchemaSql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            using (var version = connection.CreateCommand())
            {
                version.CommandText = "SELECT version FROM artifact_outbox_schema WHERE schema_key = 1;";
                var value = Convert.ToInt32(
                    await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (value != 1)
                {
                    throw new InvalidOperationException($"Artifact outbox schema {value} is not supported.");
                }
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

            await ImportLegacyEvidenceAsync(root, connection, cancellationToken).ConfigureAwait(false);
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
            legacyEvidencePath: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask EnqueueAsync(
        string root,
        ArtifactUploadManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        await InsertAsync(
            root,
            manifest.IdempotencyKey,
            ArtifactOutboxManifestKind.LegacyV1,
            FileSystemArtifactOutbox.SerializeCanonical(manifest),
            manifest.ArtifactId,
            manifest.Role,
            manifest.RelativeArtifactPath,
            manifest.ChecksumSha256.ToUpperInvariant(),
            manifest.ByteLength,
            manifest.MediaType,
            manifest.CapturedAtUtc,
            legacyEvidencePath: null,
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
        var deliveredManifest = ArtifactUploadClient.CreateCompatibilityManifest(lease.Record);
        if (!string.Equals(acknowledgement.IdempotencyKey, deliveredManifest.IdempotencyKey, StringComparison.OrdinalIgnoreCase)
            || acknowledgement.ArtifactId != deliveredManifest.ArtifactId
            || !string.Equals(acknowledgement.ChecksumSha256, deliveredManifest.ChecksumSha256, StringComparison.OrdinalIgnoreCase)
            || acknowledgement.ByteLength != deliveredManifest.ByteLength
            || !string.Equals(acknowledgement.AcceptedManifestSchemaVersion, deliveredManifest.SchemaVersion, StringComparison.Ordinal))
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

    public async ValueTask<IReadOnlyList<ArtifactOutboxRetentionHold>> GetRetentionHoldsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id, relative_artifact_path, status
            FROM artifact_outbox_records
            WHERE status IN ('pending', 'leased', 'retry', 'quarantined')
              AND artifact_id IS NOT NULL AND relative_artifact_path IS NOT NULL
            ORDER BY created_unix_ms, idempotency_key;
            """;
        var holds = new List<ArtifactOutboxRetentionHold>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            holds.Add(new ArtifactOutboxRetentionHold(
                Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), ParseStatus(reader.GetString(2))));
        }
        return holds;
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
            ReadCount(reader, 6), ReadCount(reader, 7), ReadCount(reader, 8));
    }

    public async ValueTask<bool> HasUnknownRetentionHoldsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM artifact_outbox_records
                WHERE status IN ('pending', 'leased', 'retry', 'quarantined')
                  AND (manifest_kind = 'malformed-legacy' OR artifact_id IS NULL OR relative_artifact_path IS NULL));
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public IReadOnlyList<ArtifactUploadManifest> List(
        string root,
        int maximumResults,
        IReadOnlySet<string>? excludedIdempotencyKeys = null)
    {
        if (maximumResults is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }
        var records = ReadCompatibilityRecordsAsync(root, includeQuarantined: false, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        return records
            .Where(record => excludedIdempotencyKeys?.Contains(record.IdempotencyKey) != true)
            .Select(ToLegacyManifest)
            .Take(maximumResults)
            .ToArray();
    }

    public IEnumerable<ArtifactUploadManifest> EnumeratePending(string root, CancellationToken cancellationToken)
    {
        var records = ReadCompatibilityRecordsAsync(root, includeQuarantined: true, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        if (records.Any(static record => record.ManifestKind == ArtifactOutboxManifestKind.MalformedLegacy))
        {
            throw new InvalidDataException("Malformed legacy outbox evidence requires operator resolution.");
        }
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToLegacyManifest(record);
        }
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
                  WHERE idempotency_key = $key AND status = 'quarantined'
                    AND manifest_kind != 'malformed-legacy';
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
        string? legacyEvidencePath,
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
                    status, attempt_count, next_attempt_unix_ms, created_unix_ms, updated_unix_ms,
                    legacy_evidence_path)
                VALUES ($key, $kind, $bytes, $artifact, $role, $path, $sha, $length, $media,
                        'pending', 0, $next, $created, $now, $legacy)
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
            insert.Parameters.AddWithValue("$legacy", (object?)legacyEvidencePath ?? DBNull.Value);
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

    private async ValueTask ImportLegacyEvidenceAsync(
        string root,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var path in FileSystemArtifactOutbox.EnumerateManifestPaths(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] observed;
            ArtifactUploadManifest? manifest = null;
            string? failure = null;
            try
            {
                RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
                observed = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var parsed = CaptureContractJson.ParseManifest(observed);
                manifest = parsed.IsValid ? parsed.Document?.LegacyManifest : null;
                if (manifest is null)
                {
                    failure = "malformed-legacy-manifest";
                }
                else if (!string.Equals(
                    Path.GetFileNameWithoutExtension(path), manifest.IdempotencyKey, StringComparison.Ordinal))
                {
                    failure = "legacy-filename-mismatch";
                    manifest = null;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                observed = [];
                failure = "unreadable-legacy-evidence";
            }

            var evidencePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (manifest is not null)
            {
                await ImportValidLegacyAsync(
                    connection, manifest, FileSystemArtifactOutbox.SerializeCanonical(manifest), evidencePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ImportMalformedLegacyAsync(
                    connection, evidencePath, observed, failure!, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask ImportValidLegacyAsync(
        SqliteConnection connection,
        ArtifactUploadManifest manifest,
        byte[] canonicalBytes,
        string evidencePath,
        CancellationToken cancellationToken)
    {
        using var transaction = BeginImmediate(connection);
        var now = _timeProvider.GetUtcNow();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO artifact_outbox_records(
                    idempotency_key, manifest_kind, manifest_bytes, artifact_id, role,
                    relative_artifact_path, payload_sha256, payload_length, media_type,
                    status, attempt_count, next_attempt_unix_ms, created_unix_ms, updated_unix_ms,
                    legacy_evidence_path)
                VALUES ($key, 'legacy-v1', $bytes, $artifact, $role, $path, $sha, $length, $media,
                        'pending', 0, $now, $created, $now, $evidence)
                ON CONFLICT(idempotency_key) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$key", manifest.IdempotencyKey);
            insert.Parameters.AddWithValue("$bytes", canonicalBytes);
            insert.Parameters.AddWithValue("$artifact", manifest.ArtifactId.ToString("N"));
            insert.Parameters.AddWithValue("$role", manifest.Role.ToString());
            insert.Parameters.AddWithValue("$path", manifest.RelativeArtifactPath);
            insert.Parameters.AddWithValue("$sha", manifest.ChecksumSha256.ToUpperInvariant());
            insert.Parameters.AddWithValue("$length", manifest.ByteLength);
            insert.Parameters.AddWithValue("$media", manifest.MediaType);
            insert.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$created", manifest.CapturedAtUtc.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$evidence", evidencePath);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var existing = await ReadManifestBytesAsync(
            connection, transaction, manifest.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (!existing.AsSpan().SequenceEqual(canonicalBytes))
        {
            await InsertConflictAsync(
                connection, transaction, manifest.IdempotencyKey, canonicalBytes, "legacy-import-conflict", cancellationToken)
                .ConfigureAwait(false);
            if (await QuarantineByKeyAsync(
                    connection, transaction, manifest.IdempotencyKey, "legacy-import-conflict", cancellationToken)
                .ConfigureAwait(false))
            {
                await InsertAuditAsync(
                    connection,
                    transaction,
                    manifest.IdempotencyKey,
                    "quarantine",
                    "legacy-import",
                    "legacy-import-conflict",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ImportMalformedLegacyAsync(
        SqliteConnection connection,
        string evidencePath,
        byte[] observed,
        string reason,
        CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat("legacy\n", evidencePath))));
        var now = _timeProvider.GetUtcNow();
        using var transaction = BeginImmediate(connection);
        var inserted = false;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO artifact_outbox_records(
                    idempotency_key, manifest_kind, manifest_bytes, status, attempt_count,
                    next_attempt_unix_ms, created_unix_ms, updated_unix_ms, last_reason, legacy_evidence_path)
                VALUES ($key, 'malformed-legacy', $bytes, 'quarantined', 0, $now, $now, $now, $reason, $evidence)
                ON CONFLICT(idempotency_key) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$bytes", observed);
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$evidence", evidencePath);
            inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        var existing = await ReadManifestBytesAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
        if (!existing.AsSpan().SequenceEqual(observed))
        {
            await InsertConflictAsync(connection, transaction, key, observed, reason, cancellationToken).ConfigureAwait(false);
        }
        if (inserted)
        {
            await InsertAuditAsync(connection, transaction, key, "quarantine", "legacy-import", reason, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<ArtifactOutboxRecord>> ReadCompatibilityRecordsAsync(
        string root,
        bool includeQuarantined,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = string.Concat(
            SelectColumns,
            includeQuarantined
                ? " WHERE status IN ('pending', 'leased', 'retry', 'quarantined') ORDER BY next_attempt_unix_ms, created_unix_ms, idempotency_key;"
                : " WHERE status = 'pending' OR (status = 'retry' AND next_attempt_unix_ms <= $now) OR (status = 'leased' AND lease_expires_unix_ms <= $now) ORDER BY next_attempt_unix_ms, created_unix_ms, idempotency_key;");
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var records = new List<ArtifactOutboxRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(await ReadRecordAsync(reader, cancellationToken).ConfigureAwait(false));
        }
        return records;
    }

    private static ArtifactUploadManifest ToLegacyManifest(ArtifactOutboxRecord record)
    {
        if (record.Manifest?.LegacyManifest is { } legacy)
        {
            return legacy;
        }
        if (record.Manifest?.Manifest is not { } manifest)
        {
            throw new InvalidDataException("Outbox record does not contain a valid deliverable manifest.");
        }
        var descriptor = manifest.Descriptor;
        var recipe = descriptor.Artifact.Recipe;
        return new ArtifactUploadManifest(
            ArtifactUploadManifest.CurrentSchemaVersion,
            descriptor.Capture.AgentId,
            descriptor.Artifact.ArtifactId,
            descriptor.Capture.CaptureId,
            descriptor.Artifact.Role,
            descriptor.Artifact.MediaType,
            descriptor.Layout.ByteLength,
            descriptor.Artifact.ChecksumSha256,
            descriptor.Timing.ExposureStartedUtc,
            string.Concat(recipe.Name, ":", recipe.SemanticVersion, ":", recipe.OptionsSha256),
            manifest.RelativeArtifactPath,
            manifest.Scene);
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
        if (kind != ArtifactOutboxManifestKind.MalformedLegacy)
        {
            var parsed = CaptureContractJson.ParseManifest(bytes);
            if (!parsed.IsValid || parsed.Document is null)
            {
                if (status != ArtifactOutboxStatus.Quarantined)
                {
                    throw new InvalidDataException("Committed artifact outbox manifest bytes are invalid.");
                }
            }
            else
            {
                manifest = parsed.Document;
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
            reader.IsDBNull(18) ? null : await reader.GetFieldValueAsync<byte[]>(18, cancellationToken).ConfigureAwait(false),
            reader.IsDBNull(19) ? null : reader.GetString(19));
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

    private static async ValueTask<string?> ResolveCompatibilityKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string requestedKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Concat(
            SelectColumns,
            " WHERE status IN ('pending', 'leased', 'retry') ORDER BY created_unix_ms, idempotency_key;");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = await ReadRecordAsync(reader, cancellationToken).ConfigureAwait(false);
            if (string.Equals(record.IdempotencyKey, requestedKey, StringComparison.Ordinal) ||
                string.Equals(ToLegacyManifest(record).IdempotencyKey, requestedKey, StringComparison.Ordinal))
            {
                return record.IdempotencyKey;
            }
        }
        return null;
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

    private static async ValueTask ValidateReplayEvidenceAsync(
        string root,
        ArtifactOutboxRecord record,
        CancellationToken cancellationToken)
    {
        if (record.ManifestKind == ArtifactOutboxManifestKind.MalformedLegacy
            || record.Manifest is null
            || string.IsNullOrWhiteSpace(record.RelativeArtifactPath)
            || string.IsNullOrWhiteSpace(record.PayloadSha256)
            || record.PayloadLength is null)
        {
            throw new InvalidOperationException("Malformed or incomplete evidence cannot be replayed.");
        }
        if (record.Manifest.Manifest is { } manifest)
        {
            await ValidatePayloadAsync(root, manifest, cancellationToken).ConfigureAwait(false);
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
        ArtifactOutboxManifestKind.LegacyV1 => "legacy-v1",
        ArtifactOutboxManifestKind.MalformedLegacy => "malformed-legacy",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static ArtifactOutboxManifestKind ParseKind(string value) => value switch
    {
        "v2" => ArtifactOutboxManifestKind.ManifestV2,
        "legacy-v1" => ArtifactOutboxManifestKind.LegacyV1,
        "malformed-legacy" => ArtifactOutboxManifestKind.MalformedLegacy,
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

    private const string SelectColumns = """
        SELECT record_id, idempotency_key, manifest_kind, manifest_bytes, artifact_id, role,
               relative_artifact_path, payload_sha256, payload_length, media_type, status,
               attempt_count, next_attempt_unix_ms, created_unix_ms, lease_owner, lease_token,
               lease_expires_unix_ms, last_reason, acknowledgement, legacy_evidence_path
        FROM artifact_outbox_records
        """;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS artifact_outbox_schema(
            schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
            version INTEGER NOT NULL CHECK(version = 1)
        ) STRICT;
        INSERT INTO artifact_outbox_schema(schema_key, version) VALUES (1, 1)
            ON CONFLICT(schema_key) DO NOTHING;

        CREATE TABLE IF NOT EXISTS artifact_outbox_records(
            record_id INTEGER PRIMARY KEY AUTOINCREMENT,
            idempotency_key TEXT NOT NULL UNIQUE,
            manifest_kind TEXT NOT NULL CHECK(manifest_kind IN ('v2', 'legacy-v1', 'malformed-legacy')),
            manifest_bytes BLOB NOT NULL,
            artifact_id TEXT NULL CHECK(artifact_id IS NULL OR length(artifact_id) = 32),
            role TEXT NULL,
            relative_artifact_path TEXT NULL,
            payload_sha256 TEXT NULL CHECK(payload_sha256 IS NULL OR length(payload_sha256) = 64),
            payload_length INTEGER NULL CHECK(payload_length IS NULL OR payload_length >= 0),
            media_type TEXT NULL,
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
            legacy_evidence_path TEXT NULL,
            CHECK(status != 'leased' OR (lease_owner IS NOT NULL AND lease_token IS NOT NULL AND lease_expires_unix_ms IS NOT NULL))
        ) STRICT;

        CREATE INDEX IF NOT EXISTS ix_artifact_outbox_claim
            ON artifact_outbox_records(status, next_attempt_unix_ms, created_unix_ms, idempotency_key);
        CREATE INDEX IF NOT EXISTS ix_artifact_outbox_lease
            ON artifact_outbox_records(status, lease_expires_unix_ms, created_unix_ms, idempotency_key);

        CREATE TABLE IF NOT EXISTS artifact_outbox_audit(
            audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
            idempotency_key TEXT NOT NULL,
            action TEXT NOT NULL,
            actor TEXT NOT NULL,
            reason TEXT NOT NULL,
            occurred_unix_ms INTEGER NOT NULL,
            FOREIGN KEY(idempotency_key) REFERENCES artifact_outbox_records(idempotency_key)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS artifact_outbox_conflicts(
            conflict_id INTEGER PRIMARY KEY AUTOINCREMENT,
            idempotency_key TEXT NOT NULL,
            conflicting_manifest_bytes BLOB NOT NULL,
            reason TEXT NOT NULL,
            observed_unix_ms INTEGER NOT NULL,
            FOREIGN KEY(idempotency_key) REFERENCES artifact_outbox_records(idempotency_key)
        ) STRICT;
        """;
}
