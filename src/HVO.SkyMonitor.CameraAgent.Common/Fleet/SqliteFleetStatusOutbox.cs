using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Fleet;

public sealed record FleetStatusOutboxRecord(
    long RecordId,
    FleetStatusReportV1 Report,
    string PayloadSha256,
    bool IsTransition,
    int AttemptCount);

public sealed record FleetStatusOutboxLease(
    FleetStatusOutboxRecord Record,
    string Owner,
    string Token,
    DateTimeOffset ExpiresUtc);

public sealed record FleetStatusOutboxSnapshot(
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    long OverflowCount,
    long BlockedCount,
    DateTimeOffset? OldestPendingUtc,
    DateTimeOffset EvaluatedUtc);

public interface IFleetStatusOutbox
{
    ValueTask<FleetStatusReportV1> EnqueueAsync(
        string root,
        Guid agentInstanceId,
        Func<long, FleetStatusReportV1> reportFactory,
        string stateFingerprint,
        bool isTransition,
        CancellationToken cancellationToken);

    ValueTask<FleetStatusOutboxLease?> ClaimAsync(
        string root,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask AcknowledgeAsync(
        string root,
        FleetStatusOutboxLease lease,
        FleetHeartbeatAcknowledgement acknowledgement,
        CancellationToken cancellationToken);

    ValueTask RetryAsync(
        string root,
        FleetStatusOutboxLease lease,
        DateTimeOffset retryAtUtc,
        string reason,
        CancellationToken cancellationToken);

    ValueTask QuarantineAsync(
        string root,
        FleetStatusOutboxLease lease,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<FleetStatusOutboxSnapshot> GetSnapshotAsync(string root, CancellationToken cancellationToken);
}

[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Microsoft.Data.Sqlite row getters access buffered values after asynchronous reads.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Dynamic SQL inputs are private constants or validated numeric configuration; record values remain parameterized.")]
public sealed class SqliteFleetStatusOutbox(
    TimeProvider? timeProvider = null,
    int busyTimeoutSeconds = 5,
    int maximumRecords = 10_000) : IFleetStatusOutbox, IDisposable
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initializationGates = new(PathComparer);
    private readonly HashSet<string> _initializedRoots = new(PathComparer);
    private readonly object _initializedLock = new();

    public async ValueTask<FleetStatusReportV1> EnqueueAsync(
        string root,
        Guid agentInstanceId,
        Func<long, FleetStatusReportV1> reportFactory,
        string stateFingerprint,
        bool isTransition,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agentInstanceId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(reportFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFingerprint);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var boundAgent = await ReadBoundAgentAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (boundAgent is not null && !string.Equals(boundAgent, agentInstanceId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Fleet status outbox is bound to a different agent instance.");
        }
        if (boundAgent is null)
        {
            using var bind = connection.CreateCommand();
            bind.Transaction = transaction;
            bind.CommandText = "UPDATE fleet_status_metadata SET agent_instance_id = $agent WHERE metadata_key = 1;";
            bind.Parameters.AddWithValue("$agent", agentInstanceId.ToString("D"));
            await bind.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var sequence = isTransition
            ? null
            : await FindCoalescibleSequenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (sequence is null)
        {
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM fleet_status_records;";
            var records = Convert.ToInt32(
                await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (records >= maximumRecords)
            {
                using var overflow = connection.CreateCommand();
                overflow.Transaction = transaction;
                overflow.CommandText = "UPDATE fleet_status_metadata SET overflow_count = overflow_count + 1 WHERE metadata_key = 1;";
                await overflow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw new FleetStatusOutboxCapacityException($"Fleet status outbox reached its bounded limit of {maximumRecords} records.");
            }
            using var allocate = connection.CreateCommand();
            allocate.Transaction = transaction;
            allocate.CommandText = "UPDATE fleet_status_metadata SET next_sequence = next_sequence + 1 WHERE metadata_key = 1 RETURNING next_sequence - 1;";
            sequence = Convert.ToInt64(
                await allocate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var report = reportFactory(sequence.Value);
        if (report.Sequence != sequence || report.AgentInstanceId != agentInstanceId)
        {
            throw new InvalidOperationException("Fleet report factory returned a report with a different durable identity.");
        }
        var validation = FleetContractJson.Validate(report);
        if (!validation.IsValid)
        {
            throw new ArgumentException($"Fleet status report is invalid ({validation.ReasonCode}:{validation.FieldPath}).", nameof(reportFactory));
        }
        var payload = FleetContractJson.Serialize(report);
        var payloadSha256 = FleetContractJson.ComputeSha256(report);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (await UpdateCoalescedAsync(
                connection, transaction, sequence.Value, report, payload, payloadSha256, stateFingerprint, now, cancellationToken)
            .ConfigureAwait(false) == 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO fleet_status_records(
                    agent_instance_id, boot_session_id, sequence, observed_unix_ms,
                    payload, payload_sha256, state_fingerprint, is_transition,
                    status, attempt_count, next_attempt_unix_ms, created_unix_ms, updated_unix_ms)
                VALUES($agent, $boot, $sequence, $observed, $payload, $hash, $fingerprint,
                    $transition, 'pending', 0, $now, $now, $now);
                """;
            AddReportParameters(insert, report, payload, payloadSha256);
            insert.Parameters.AddWithValue("$fingerprint", Bound(stateFingerprint, 64));
            insert.Parameters.AddWithValue("$transition", isTransition ? 1 : 0);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return report;
    }

    public async ValueTask<FleetStatusOutboxLease?> ClaimAsync(
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
        var token = Guid.NewGuid().ToString("N");
        var expires = now + leaseDuration;
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var candidate = connection.CreateCommand();
        candidate.Transaction = transaction;
        candidate.CommandText = """
            SELECT record_id FROM (
                SELECT record_id, status, next_attempt_unix_ms, lease_expires_unix_ms
                FROM fleet_status_records
                WHERE status != 'quarantined'
                ORDER BY sequence
                LIMIT 1)
            WHERE status = 'pending'
               OR (status = 'retry' AND next_attempt_unix_ms <= $now)
               OR (status = 'leased' AND lease_expires_unix_ms <= $now);
            """;
        candidate.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        var value = await candidate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var recordId = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE fleet_status_records
                SET status = 'leased', attempt_count = attempt_count + 1,
                    lease_owner = $owner, lease_token = $token, lease_expires_unix_ms = $expires,
                    updated_unix_ms = $now
                WHERE record_id = $id;
                """;
            update.Parameters.AddWithValue("$owner", Bound(owner, 128));
            update.Parameters.AddWithValue("$token", token);
            update.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$id", recordId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        FleetStatusOutboxRecord record;
        try
        {
            record = await ReadRecordAsync(connection, transaction, recordId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            using var quarantine = connection.CreateCommand();
            quarantine.Transaction = transaction;
            quarantine.CommandText = """
                UPDATE fleet_status_records
                SET status = 'quarantined', last_reason = 'malformed-committed-record',
                    lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL,
                    updated_unix_ms = $now
                WHERE record_id = $id;
                """;
            quarantine.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            quarantine.Parameters.AddWithValue("$id", recordId);
            await quarantine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FleetStatusOutboxLease(record, Bound(owner, 128), token, expires);
    }

    public async ValueTask AcknowledgeAsync(
        string root,
        FleetStatusOutboxLease lease,
        FleetHeartbeatAcknowledgement acknowledgement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (acknowledgement.AgentInstanceId != lease.Record.Report.AgentInstanceId ||
            acknowledgement.BootSessionId != lease.Record.Report.BootSessionId ||
            acknowledgement.Sequence != lease.Record.Report.Sequence)
        {
            throw new InvalidDataException("Fleet heartbeat acknowledgement does not match the leased report.");
        }
        await SettleAsync(root, lease, "DELETE FROM fleet_status_records WHERE record_id = $id AND status = 'leased' AND lease_token = $token;", null, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask RetryAsync(
        string root,
        FleetStatusOutboxLease lease,
        DateTimeOffset retryAtUtc,
        string reason,
        CancellationToken cancellationToken)
        => SettleAsync(root, lease, """
            UPDATE fleet_status_records
            SET status = 'retry', next_attempt_unix_ms = $next, last_reason = $reason,
                lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL,
                updated_unix_ms = $now
            WHERE record_id = $id AND status = 'leased' AND lease_token = $token;
            """, retryAtUtc, reason, cancellationToken);

    public ValueTask QuarantineAsync(
        string root,
        FleetStatusOutboxLease lease,
        string reason,
        CancellationToken cancellationToken)
        => SettleAsync(root, lease, """
            UPDATE fleet_status_records
            SET status = 'quarantined', last_reason = $reason,
                lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL,
                updated_unix_ms = $now
            WHERE record_id = $id AND status = 'leased' AND lease_token = $token;
            """, null, reason, cancellationToken);

    public async ValueTask<FleetStatusOutboxSnapshot> GetSnapshotAsync(string root, CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN status IN ('pending','retry','leased') THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status IN ('pending','retry','leased') THEN length(payload) ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'leased' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'retry' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'quarantined' THEN 1 ELSE 0 END), 0),
                (SELECT overflow_count FROM fleet_status_metadata WHERE metadata_key = 1),
                COALESCE(SUM(CASE WHEN status = 'retry' AND last_reason = 'credentials-rejected' THEN 1 ELSE 0 END), 0),
                MIN(CASE WHEN status IN ('pending','retry','leased') THEN created_unix_ms END)
            FROM fleet_status_records;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new FleetStatusOutboxSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
            _timeProvider.GetUtcNow());
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
            using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "SELECT schema_version FROM fleet_status_metadata WHERE metadata_key = 1;";
            var schemaVersion = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (schemaVersion != 1)
            {
                throw new InvalidOperationException($"Fleet status outbox schema {schemaVersion} is not supported.");
            }
            command.CommandText = "PRAGMA integrity_check;";
            var integrity = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Fleet status outbox SQLite integrity check failed.");
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

    private async ValueTask SettleAsync(
        string root,
        FleetStatusOutboxLease lease,
        string sql,
        DateTimeOffset? next,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", lease.Record.RecordId);
        command.Parameters.AddWithValue("$token", lease.Token);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        if (next is not null)
        {
            command.Parameters.AddWithValue("$next", next.Value.ToUnixTimeMilliseconds());
        }
        if (reason is not null)
        {
            command.Parameters.AddWithValue("$reason", Bound(reason, 128));
        }
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Fleet status lease was lost before settlement.");
        }
    }

    private static async ValueTask<string?> ReadBoundAgentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT agent_instance_id FROM fleet_status_metadata WHERE metadata_key = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<long?> FindCoalescibleSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence FROM fleet_status_records
            WHERE status = 'pending' AND attempt_count = 0 AND is_transition = 0
              AND sequence = (SELECT MAX(sequence) FROM fleet_status_records)
            LIMIT 1;
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<int> UpdateCoalescedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sequence,
        FleetStatusReportV1 report,
        byte[] payload,
        string payloadSha256,
        string fingerprint,
        long now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE fleet_status_records
            SET boot_session_id = $boot, observed_unix_ms = $observed, payload = $payload,
                payload_sha256 = $hash, state_fingerprint = $fingerprint, updated_unix_ms = $now
            WHERE sequence = $sequence AND status = 'pending' AND attempt_count = 0 AND is_transition = 0;
            """;
        AddReportParameters(command, report, payload, payloadSha256);
        command.Parameters.AddWithValue("$fingerprint", Bound(fingerprint, 64));
        command.Parameters.AddWithValue("$now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<FleetStatusOutboxRecord> ReadRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long recordId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload, payload_sha256, is_transition, attempt_count FROM fleet_status_records WHERE record_id = $id;";
        command.Parameters.AddWithValue("$id", recordId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Fleet status record disappeared while being leased.");
        }
        var payload = (byte[])reader.GetValue(0);
        var report = FleetContractJson.DeserializeReport(payload)
            ?? throw new InvalidDataException("Fleet status record contains an empty payload.");
        var validation = FleetContractJson.Validate(report);
        if (!validation.IsValid || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(FleetContractJson.ComputeSha256(report)),
                Convert.FromHexString(reader.GetString(1))))
        {
            throw new InvalidDataException("Fleet status record failed validation or checksum verification.");
        }
        return new FleetStatusOutboxRecord(recordId, report, reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3));
    }

    private async ValueTask<SqliteConnection> OpenAsync(string root, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath(root),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = busyTimeoutSeconds
        };
        var connection = new SqliteConnection(builder.ToString());
        await Sqlite.SqliteConnectionConfigurationGate.OpenAndConfigureAsync(
            connection,
            async (configuredConnection, token) =>
        {
            using var command = configuredConnection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "PRAGMA synchronous=FULL;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = $"PRAGMA busy_timeout={busyTimeoutSeconds * 1000};";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "PRAGMA foreign_keys=ON;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void AddReportParameters(SqliteCommand command, FleetStatusReportV1 report, byte[] payload, string hash)
    {
        command.Parameters.AddWithValue("$agent", report.AgentInstanceId.ToString("D"));
        command.Parameters.AddWithValue("$boot", report.BootSessionId.ToString("D"));
        command.Parameters.AddWithValue("$sequence", report.Sequence);
        command.Parameters.AddWithValue("$observed", report.ObservedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$hash", hash);
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.GetFullPath(root);
    }

    private static string DatabaseDirectory(string root) => Path.Combine(root, ".fleet");
    private static string DatabasePath(string root) => Path.Combine(DatabaseDirectory(root), "fleet-status.db");
    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public void Dispose()
    {
        foreach (var gate in _initializationGates.Values)
        {
            gate.Dispose();
        }
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS fleet_status_metadata(
            metadata_key INTEGER NOT NULL PRIMARY KEY CHECK(metadata_key = 1),
            schema_version INTEGER NOT NULL,
            agent_instance_id TEXT NULL,
            next_sequence INTEGER NOT NULL CHECK(next_sequence > 0),
            overflow_count INTEGER NOT NULL DEFAULT 0 CHECK(overflow_count >= 0));
        INSERT OR IGNORE INTO fleet_status_metadata(metadata_key, schema_version, agent_instance_id, next_sequence, overflow_count)
        VALUES(1, 1, NULL, 1, 0);
        CREATE TABLE IF NOT EXISTS fleet_status_records(
            record_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            agent_instance_id TEXT NOT NULL,
            boot_session_id TEXT NOT NULL,
            sequence INTEGER NOT NULL UNIQUE CHECK(sequence > 0),
            observed_unix_ms INTEGER NOT NULL,
            payload BLOB NOT NULL,
            payload_sha256 TEXT NOT NULL,
            state_fingerprint TEXT NOT NULL,
            is_transition INTEGER NOT NULL CHECK(is_transition IN (0,1)),
            status TEXT NOT NULL CHECK(status IN ('pending','leased','retry','quarantined')),
            attempt_count INTEGER NOT NULL CHECK(attempt_count >= 0),
            next_attempt_unix_ms INTEGER NOT NULL,
            lease_owner TEXT NULL,
            lease_token TEXT NULL,
            lease_expires_unix_ms INTEGER NULL,
            last_reason TEXT NULL,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_fleet_status_claim
            ON fleet_status_records(status, next_attempt_unix_ms, sequence);
        CREATE INDEX IF NOT EXISTS ix_fleet_status_lease
            ON fleet_status_records(status, lease_expires_unix_ms, sequence);
        """;
}

public sealed class FleetStatusOutboxCapacityException : InvalidOperationException
{
    public FleetStatusOutboxCapacityException()
    {
    }

    public FleetStatusOutboxCapacityException(string message) : base(message)
    {
    }

    public FleetStatusOutboxCapacityException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
