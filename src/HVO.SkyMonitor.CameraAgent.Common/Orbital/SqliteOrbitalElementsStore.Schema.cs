using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

public sealed partial class SqliteOrbitalElementsStore
{
    internal const int CurrentSchemaVersion = 1;
    private const int BusyTimeoutSeconds = 5;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> CanonicalSchemaDefinitions =
        new(CreateCanonicalSchemaDefinitions);

    /// <summary>
    /// Schema 1. Every table is STRICT; identities are 64 uppercase hexadecimal characters; tombstones, history,
    /// commands and the installation seed are protected by triggers; durable counters and revisions never decrease.
    /// </summary>
    internal const string CanonicalSchemaSql = """
        CREATE TABLE orbital_installation(
            installation_key INTEGER PRIMARY KEY CHECK(installation_key = 1),
            seed BLOB NOT NULL CHECK(length(seed) = 32),
            created_unix_ms INTEGER NOT NULL CHECK(created_unix_ms >= 0)
        ) STRICT;
        CREATE TRIGGER orbital_installation_immutable_update BEFORE UPDATE ON orbital_installation
        BEGIN SELECT RAISE(ABORT, 'orbital installation is immutable'); END;
        CREATE TRIGGER orbital_installation_immutable_delete BEFORE DELETE ON orbital_installation
        BEGIN SELECT RAISE(ABORT, 'orbital installation is immutable'); END;

        CREATE TABLE orbital_snapshots(
            manifest_sha256 TEXT PRIMARY KEY CHECK(length(manifest_sha256) = 64 AND manifest_sha256 NOT GLOB '*[^0-9A-F]*'),
            snapshot_sequence INTEGER NOT NULL UNIQUE CHECK(snapshot_sequence > 0),
            attempt_id TEXT NOT NULL CHECK(length(attempt_id) = 32 AND attempt_id NOT GLOB '*[^0-9a-f]*'),
            manifest_file_sha256 TEXT NOT NULL CHECK(length(manifest_file_sha256) = 64 AND manifest_file_sha256 NOT GLOB '*[^0-9A-F]*'),
            manifest_length_bytes INTEGER NOT NULL CHECK(manifest_length_bytes BETWEEN 1 AND 1048576),
            payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64 AND payload_sha256 NOT GLOB '*[^0-9A-F]*'),
            payload_length_bytes INTEGER NOT NULL CHECK(payload_length_bytes BETWEEN 1 AND 134217728),
            raw_sha256 TEXT NOT NULL CHECK(length(raw_sha256) = 64 AND raw_sha256 NOT GLOB '*[^0-9A-F]*'),
            raw_length_bytes INTEGER NOT NULL CHECK(raw_length_bytes BETWEEN 1 AND 33554432),
            record_count INTEGER NOT NULL CHECK(record_count BETWEEN 0 AND 100000),
            object_count INTEGER NOT NULL CHECK(object_count BETWEEN 0 AND record_count),
            minimum_epoch_utc_ticks INTEGER,
            maximum_epoch_utc_ticks INTEGER,
            retrieved_utc_ticks INTEGER NOT NULL CHECK(retrieved_utc_ticks >= 0),
            published_unix_ms INTEGER NOT NULL CHECK(published_unix_ms >= 0),
            state TEXT NOT NULL CHECK(state IN ('published', 'retired')),
            retired_unix_ms INTEGER CHECK(retired_unix_ms IS NULL OR retired_unix_ms >= 0),
            files_pending INTEGER NOT NULL DEFAULT 0 CHECK(files_pending IN (0, 1)),
            CHECK((minimum_epoch_utc_ticks IS NULL) = (maximum_epoch_utc_ticks IS NULL)),
            CHECK(minimum_epoch_utc_ticks IS NULL OR minimum_epoch_utc_ticks <= maximum_epoch_utc_ticks),
            CHECK((state = 'retired') = (retired_unix_ms IS NOT NULL)),
            CHECK(state = 'retired' OR files_pending = 0)
        ) STRICT;
        CREATE INDEX orbital_snapshots_payload ON orbital_snapshots(payload_sha256, state);
        CREATE INDEX orbital_snapshots_raw ON orbital_snapshots(raw_sha256, state);
        CREATE INDEX orbital_snapshots_files_pending ON orbital_snapshots(snapshot_sequence) WHERE files_pending = 1;
        CREATE TRIGGER orbital_snapshots_no_delete BEFORE DELETE ON orbital_snapshots
        BEGIN SELECT RAISE(ABORT, 'orbital snapshots are tombstoned, never deleted'); END;
        CREATE TRIGGER orbital_snapshots_immutable BEFORE UPDATE ON orbital_snapshots
        WHEN NEW.manifest_sha256 IS NOT OLD.manifest_sha256 OR NEW.snapshot_sequence IS NOT OLD.snapshot_sequence
            OR NEW.attempt_id IS NOT OLD.attempt_id OR NEW.manifest_file_sha256 IS NOT OLD.manifest_file_sha256
            OR NEW.manifest_length_bytes IS NOT OLD.manifest_length_bytes OR NEW.payload_sha256 IS NOT OLD.payload_sha256
            OR NEW.payload_length_bytes IS NOT OLD.payload_length_bytes OR NEW.raw_sha256 IS NOT OLD.raw_sha256
            OR NEW.raw_length_bytes IS NOT OLD.raw_length_bytes OR NEW.record_count IS NOT OLD.record_count
            OR NEW.object_count IS NOT OLD.object_count OR NEW.minimum_epoch_utc_ticks IS NOT OLD.minimum_epoch_utc_ticks
            OR NEW.maximum_epoch_utc_ticks IS NOT OLD.maximum_epoch_utc_ticks
            OR NEW.retrieved_utc_ticks IS NOT OLD.retrieved_utc_ticks OR NEW.published_unix_ms IS NOT OLD.published_unix_ms
        BEGIN SELECT RAISE(ABORT, 'orbital snapshot identity is immutable'); END;
        CREATE TRIGGER orbital_snapshots_retirement_final BEFORE UPDATE ON orbital_snapshots
        WHEN OLD.state = 'retired' AND (NEW.state IS NOT 'retired' OR NEW.retired_unix_ms IS NOT OLD.retired_unix_ms
            OR NEW.files_pending > OLD.files_pending)
        BEGIN SELECT RAISE(ABORT, 'orbital snapshot retirement is final'); END;

        CREATE TABLE orbital_provider_state(
            provider TEXT PRIMARY KEY CHECK(provider = 'celestrak'),
            revision INTEGER NOT NULL CHECK(revision >= 0),
            attempt_sequence INTEGER NOT NULL CHECK(attempt_sequence >= 0),
            snapshot_sequence INTEGER NOT NULL CHECK(snapshot_sequence >= 0),
            active_manifest_sha256 TEXT REFERENCES orbital_snapshots(manifest_sha256),
            previous_manifest_sha256 TEXT REFERENCES orbital_snapshots(manifest_sha256),
            last_attempt_started_unix_ms INTEGER CHECK(last_attempt_started_unix_ms IS NULL OR last_attempt_started_unix_ms >= 0),
            last_retrieval_success_utc_ticks INTEGER CHECK(last_retrieval_success_utc_ticks IS NULL OR last_retrieval_success_utc_ticks >= 0),
            last_activation_unix_ms INTEGER CHECK(last_activation_unix_ms IS NULL OR last_activation_unix_ms >= 0),
            halt_since_unix_ms INTEGER CHECK(halt_since_unix_ms IS NULL OR halt_since_unix_ms >= 0),
            halt_http_status INTEGER CHECK(halt_http_status IS NULL OR (halt_http_status BETWEEN 100 AND 599 AND halt_http_status <> 200)),
            halt_reason TEXT CHECK(halt_reason IS NULL OR length(halt_reason) BETWEEN 1 AND 512),
            quarantine_observed_unix_ms INTEGER CHECK(quarantine_observed_unix_ms IS NULL OR quarantine_observed_unix_ms >= 0),
            quarantine_raw_sha256 TEXT CHECK(quarantine_raw_sha256 IS NULL OR (length(quarantine_raw_sha256) = 64 AND quarantine_raw_sha256 NOT GLOB '*[^0-9A-F]*')),
            quarantine_reason TEXT CHECK(quarantine_reason IS NULL OR length(quarantine_reason) BETWEEN 1 AND 512),
            CHECK((halt_since_unix_ms IS NULL) = (halt_http_status IS NULL) AND (halt_since_unix_ms IS NULL) = (halt_reason IS NULL)),
            CHECK((quarantine_observed_unix_ms IS NULL) = (quarantine_reason IS NULL)),
            CHECK(quarantine_raw_sha256 IS NULL OR quarantine_observed_unix_ms IS NOT NULL),
            CHECK(previous_manifest_sha256 IS NULL OR active_manifest_sha256 IS NOT NULL)
        ) STRICT;
        CREATE TRIGGER orbital_provider_state_monotonic BEFORE UPDATE ON orbital_provider_state
        WHEN NEW.provider IS NOT OLD.provider OR NEW.revision < OLD.revision
            OR NEW.attempt_sequence < OLD.attempt_sequence OR NEW.snapshot_sequence < OLD.snapshot_sequence
            OR (OLD.last_attempt_started_unix_ms IS NOT NULL AND (NEW.last_attempt_started_unix_ms IS NULL
                OR NEW.last_attempt_started_unix_ms < OLD.last_attempt_started_unix_ms))
            OR (OLD.active_manifest_sha256 IS NOT NULL AND NEW.active_manifest_sha256 IS NULL)
        BEGIN SELECT RAISE(ABORT, 'orbital provider counters never decrease'); END;
        CREATE TRIGGER orbital_provider_state_no_delete BEFORE DELETE ON orbital_provider_state
        BEGIN SELECT RAISE(ABORT, 'orbital provider state is permanent'); END;

        CREATE TABLE orbital_attempts(
            attempt_sequence INTEGER PRIMARY KEY CHECK(attempt_sequence > 0),
            attempt_id TEXT NOT NULL UNIQUE CHECK(length(attempt_id) = 32 AND attempt_id NOT GLOB '*[^0-9a-f]*'),
            run_key TEXT NOT NULL CHECK(length(run_key) BETWEEN 1 AND 128),
            trigger_kind TEXT NOT NULL CHECK(trigger_kind IN ('Scheduled', 'Manual')),
            started_unix_ms INTEGER NOT NULL CHECK(started_unix_ms >= 0),
            completed_unix_ms INTEGER CHECK(completed_unix_ms IS NULL OR completed_unix_ms >= started_unix_ms),
            outcome TEXT NOT NULL CHECK(outcome IN ('InFlight', 'Activated', 'UnchangedPayload', 'ProviderHalted',
                'TransportFailed', 'Quarantined', 'StorageFailed', 'Interrupted')),
            reason_code TEXT CHECK(reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 128),
            detail TEXT CHECK(detail IS NULL OR length(detail) <= 512),
            http_status INTEGER CHECK(http_status IS NULL OR (http_status BETWEEN 100 AND 599 AND http_status <> 200)),
            manifest_sha256 TEXT CHECK(manifest_sha256 IS NULL OR (length(manifest_sha256) = 64 AND manifest_sha256 NOT GLOB '*[^0-9A-F]*')),
            raw_sha256 TEXT CHECK(raw_sha256 IS NULL OR (length(raw_sha256) = 64 AND raw_sha256 NOT GLOB '*[^0-9A-F]*')),
            raw_length_bytes INTEGER CHECK(raw_length_bytes IS NULL OR raw_length_bytes >= 0),
            quarantine_retained INTEGER NOT NULL DEFAULT 0 CHECK(quarantine_retained IN (0, 1)),
            CHECK((outcome = 'InFlight') = (completed_unix_ms IS NULL)),
            CHECK((outcome = 'InFlight') = (reason_code IS NULL)),
            CHECK((outcome = 'ProviderHalted') = (http_status IS NOT NULL)),
            CHECK(quarantine_retained = 0 OR (outcome = 'Quarantined' AND raw_sha256 IS NOT NULL))
        ) STRICT;
        CREATE UNIQUE INDEX orbital_attempts_single_flight ON orbital_attempts(outcome) WHERE outcome = 'InFlight';
        CREATE INDEX orbital_attempts_quarantine ON orbital_attempts(raw_sha256) WHERE quarantine_retained = 1;
        CREATE TRIGGER orbital_attempts_no_delete BEFORE DELETE ON orbital_attempts
        BEGIN SELECT RAISE(ABORT, 'orbital attempts are append-only'); END;
        CREATE TRIGGER orbital_attempts_identity_immutable BEFORE UPDATE ON orbital_attempts
        WHEN NEW.attempt_sequence IS NOT OLD.attempt_sequence OR NEW.attempt_id IS NOT OLD.attempt_id
            OR NEW.run_key IS NOT OLD.run_key OR NEW.trigger_kind IS NOT OLD.trigger_kind
            OR NEW.started_unix_ms IS NOT OLD.started_unix_ms
        BEGIN SELECT RAISE(ABORT, 'orbital attempt identity is immutable'); END;
        CREATE TRIGGER orbital_attempts_terminal_immutable BEFORE UPDATE ON orbital_attempts
        WHEN OLD.outcome <> 'InFlight' AND (NEW.outcome IS NOT OLD.outcome OR NEW.completed_unix_ms IS NOT OLD.completed_unix_ms
            OR NEW.reason_code IS NOT OLD.reason_code OR NEW.detail IS NOT OLD.detail OR NEW.http_status IS NOT OLD.http_status
            OR NEW.manifest_sha256 IS NOT OLD.manifest_sha256 OR NEW.raw_sha256 IS NOT OLD.raw_sha256
            OR NEW.raw_length_bytes IS NOT OLD.raw_length_bytes OR NEW.quarantine_retained > OLD.quarantine_retained)
        BEGIN SELECT RAISE(ABORT, 'a completed orbital attempt is immutable'); END;

        CREATE TABLE orbital_activation_history(
            history_sequence INTEGER PRIMARY KEY CHECK(history_sequence > 0),
            manifest_sha256 TEXT NOT NULL REFERENCES orbital_snapshots(manifest_sha256),
            previous_manifest_sha256 TEXT REFERENCES orbital_snapshots(manifest_sha256),
            payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64 AND payload_sha256 NOT GLOB '*[^0-9A-F]*'),
            kind TEXT NOT NULL CHECK(kind IN ('activated', 'unchanged-payload')),
            attempt_id TEXT NOT NULL UNIQUE CHECK(length(attempt_id) = 32 AND attempt_id NOT GLOB '*[^0-9a-f]*'),
            activated_unix_ms INTEGER NOT NULL CHECK(activated_unix_ms >= 0),
            revision INTEGER NOT NULL CHECK(revision > 0)
        ) STRICT;
        CREATE TRIGGER orbital_activation_history_no_update BEFORE UPDATE ON orbital_activation_history
        BEGIN SELECT RAISE(ABORT, 'orbital activation history is append-only'); END;
        CREATE TRIGGER orbital_activation_history_no_delete BEFORE DELETE ON orbital_activation_history
        BEGIN SELECT RAISE(ABORT, 'orbital activation history is append-only'); END;

        CREATE TABLE orbital_snapshot_references(
            manifest_sha256 TEXT NOT NULL REFERENCES orbital_snapshots(manifest_sha256),
            holder TEXT NOT NULL CHECK(length(holder) BETWEEN 1 AND 128),
            created_unix_ms INTEGER NOT NULL CHECK(created_unix_ms >= 0),
            PRIMARY KEY(manifest_sha256, holder)
        ) STRICT;

        CREATE TABLE orbital_commands(
            idempotency_key TEXT PRIMARY KEY CHECK(length(idempotency_key) BETWEEN 1 AND 128),
            command_kind TEXT NOT NULL CHECK(command_kind = 'resume'),
            payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64 AND payload_sha256 NOT GLOB '*[^0-9A-F]*'),
            halt_since_unix_ms INTEGER NOT NULL CHECK(halt_since_unix_ms >= 0),
            halt_http_status INTEGER NOT NULL CHECK(halt_http_status BETWEEN 100 AND 599 AND halt_http_status <> 200),
            actor TEXT NOT NULL CHECK(length(actor) BETWEEN 1 AND 128),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 512),
            revision INTEGER NOT NULL CHECK(revision > 0),
            recorded_unix_ms INTEGER NOT NULL CHECK(recorded_unix_ms >= 0)
        ) STRICT;
        CREATE TRIGGER orbital_commands_no_update BEFORE UPDATE ON orbital_commands
        BEGIN SELECT RAISE(ABORT, 'orbital commands are append-only'); END;
        CREATE TRIGGER orbital_commands_no_delete BEFORE DELETE ON orbital_commands
        BEGIN SELECT RAISE(ABORT, 'orbital commands are append-only'); END;
        """;

    /// <summary>
    /// Creates schema 1 in an empty database or proves an existing one is exactly schema 1, then settles every
    /// InFlight attempt as Interrupted. Interrupted attempts keep their start time, so they still hold the floor.
    /// </summary>
    private async ValueTask<int> EnsureSchemaAndReconcileLedgerAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var transaction = BeginImmediate(connection);
        var version = await ScalarLongAsync(connection, transaction, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
        if (version == 0)
        {
            var objects = await ScalarLongAsync(connection, transaction, """
                SELECT COUNT(*) FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%';
                """, cancellationToken).ConfigureAwait(false);
            if (objects != 0)
            {
                throw new InvalidDataException(
                    "Orbital elements storage contains unversioned schema objects; archive it before starting this CameraAgent.");
            }
            await ExecuteAsync(connection, transaction, CanonicalSchemaSql, cancellationToken).ConfigureAwait(false);
            var now = ToUnixMilliseconds(Truncate(Now()));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO orbital_installation(installation_key, seed, created_unix_ms) VALUES(1, $seed, $now);
                INSERT INTO orbital_provider_state(provider, revision, attempt_sequence, snapshot_sequence)
                VALUES('celestrak', 0, 0, 0);
                """, cancellationToken, ("$seed", RandomNumberGenerator.GetBytes(32)), ("$now", now)).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {CurrentSchemaVersion};"),
                cancellationToken).ConfigureAwait(false);
        }
        else if (version != CurrentSchemaVersion)
        {
            throw new InvalidDataException(version > CurrentSchemaVersion
                ? "Orbital elements storage was written by a newer CameraAgent; this version cannot use it."
                : "Orbital elements storage has an unknown schema version.");
        }
        await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var interrupted = await ExecuteAsync(connection, transaction, """
            UPDATE orbital_attempts
            SET outcome = 'Interrupted', completed_unix_ms = MAX(started_unix_ms, $now),
                reason_code = 'orbital.attempt-interrupted', detail = 'The process stopped before the attempt completed.'
            WHERE outcome = 'InFlight';
            """, cancellationToken, ("$now", ToUnixMilliseconds(Truncate(Now())))).ConfigureAwait(false);
        if (interrupted > 0)
        {
            await BumpRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return interrupted;
    }

    private static async ValueTask ValidateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var actual = await ReadSchemaDefinitionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var expected = CanonicalSchemaDefinitions.Value;
        if (expected.Any(pair => !actual.TryGetValue(pair.Key, out var definition) ||
                !string.Equals(definition, pair.Value, StringComparison.Ordinal)) ||
            actual.Keys.Any(name => !expected.ContainsKey(name)))
        {
            throw new InvalidDataException(
                "Orbital elements storage schema has drifted from schema 1; archive it before starting this CameraAgent.");
        }
        var integrity = await ScalarStringAsync(connection, transaction, "PRAGMA integrity_check;", cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Orbital elements SQLite integrity check failed.");
        }
        if (await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM pragma_foreign_key_check;", cancellationToken)
                .ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Orbital elements SQLite foreign-key validation failed.");
        }
        if (await ScalarLongAsync(connection, transaction, """
                SELECT (SELECT COUNT(*) FROM orbital_installation) = 1
                    AND (SELECT COUNT(*) FROM orbital_provider_state) = 1;
                """, cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("Orbital elements storage is missing its installation or provider row.");
        }
    }

    private static Dictionary<string, string> CreateCanonicalSchemaDefinitions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = CanonicalSchemaSql;
            command.ExecuteNonQuery();
        }
        using var definitions = CreateSchemaDefinitionCommand(connection, null);
        using var reader = definitions.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result.Add(reader.GetString(1), ReadSchemaDefinition(reader));
        }
        return result;
    }

    private static async ValueTask<Dictionary<string, string>> ReadSchemaDefinitionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = CreateSchemaDefinitionCommand(connection, transaction);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            definitions.Add(reader.GetString(1), ReadSchemaDefinition(reader));
        }
        return definitions;
    }

    private static SqliteCommand CreateSchemaDefinitionCommand(SqliteConnection connection, SqliteTransaction? transaction)
        => CreateCommand(connection, transaction, """
            SELECT type, name, tbl_name, sql
            FROM sqlite_schema
            WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """);

    private static string ReadSchemaDefinition(SqliteDataReader reader)
    {
        var definition = new StringBuilder();
        foreach (var ordinal in new[] { 0, 2 })
        {
            var value = reader.GetString(ordinal);
            definition.Append(value.Length).Append(':').Append(value);
        }
        var sql = SqliteRawCaptureJournal.NormalizeSchemaSql(reader.GetString(3));
        definition.Append(sql.Length).Append(':').Append(sql);
        return definition.ToString();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant PRAGMA statements with a constant numeric bound are executed.")]
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var root = _root ?? throw new InvalidOperationException("The orbital elements store root is not owned.");
        var path = root.Resolve(DatabaseFileName);
        // Re-checked on every open: a sidecar can be replaced with a link between opens, and the WAL and
        // shared-memory files carry committed data just as the database does.
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            root.Verify(candidate, "orbital-database-open");
        }
        _connectionString ??= new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = BusyTimeoutSeconds
        }.ToString();
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await Sqlite.SqliteConnectionConfigurationGate.OpenAndConfigureAsync(
                connection,
                async (configured, token) =>
                {
                    using var command = configured.CreateCommand();
                    command.CommandText = string.Create(CultureInfo.InvariantCulture,
                        $"PRAGMA busy_timeout = {BusyTimeoutSeconds * 1000};");
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    command.CommandText = "PRAGMA synchronous = FULL;";
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    command.CommandText = "PRAGMA foreign_keys = ON;";
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    command.CommandText = "PRAGMA journal_mode = WAL;";
                    var mode = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                    if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Orbital elements SQLite storage could not enable write-ahead logging.");
                    }
                },
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.")]
    private static SqliteTransaction BeginImmediate(SqliteConnection connection)
        => connection.BeginTransaction(deferred: false);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant statement text is passed; values remain parameterized.")]
    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command;
    }

    private static async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(connection, transaction, sql, parameters);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<string?> ScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(connection, transaction, sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
}
