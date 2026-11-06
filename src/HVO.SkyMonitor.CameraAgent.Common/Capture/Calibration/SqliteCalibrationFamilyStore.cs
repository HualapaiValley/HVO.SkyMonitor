using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Sqlite;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

/// <summary>
/// The durable calibration-family store (#1475 slice C1): canonical families keyed by their identity, the one active
/// family of each agent and rig, and the log of every activation and deactivation. It is a STRICT WAL SQLite file of
/// its own under one exclusively locked root, so the fingerprinted raw-ingress schema is unchanged and a release that
/// predates it never opens it.
/// </summary>
/// <remarks>
/// Families are immutable and content-addressed: storing one again changes nothing, and reading one back proves its
/// bytes still parse canonically to the identity it is keyed by. <c>calibration_family_masters</c> lists each family's
/// master artifact IDs, written in the same transaction as the family, so a retention sweep reads the masters of the
/// active families with one query and never parses a family under its locks.
/// </remarks>
internal sealed partial class SqliteCalibrationFamilyStore : IDisposable
{
    internal const int SchemaVersion = 1;
    internal const string DirectoryName = ".calibration-families";
    internal const string DatabaseFileName = "calibration-families.db";
    internal const string LockFileName = "calibration-families.lock";
    internal const string ActivateCommand = "activate";
    internal const string DeactivateCommand = "deactivate";

    internal const string Schema = """
        CREATE TABLE calibration_families(
            identity_sha256 TEXT NOT NULL PRIMARY KEY CHECK(length(identity_sha256) = 64 AND identity_sha256 NOT GLOB '*[^0-9A-F]*'),
            family_id TEXT NOT NULL CHECK(length(family_id) BETWEEN 1 AND 128),
            agent_id TEXT NOT NULL CHECK(length(agent_id) BETWEEN 1 AND 128),
            rig_id TEXT NOT NULL CHECK(length(rig_id) BETWEEN 1 AND 128),
            canonical_json BLOB NOT NULL CHECK(length(canonical_json) BETWEEN 1 AND 2097152),
            created_unix_ms INTEGER NOT NULL,
            UNIQUE(identity_sha256, agent_id, rig_id)) STRICT;
        CREATE TABLE calibration_family_masters(
            identity_sha256 TEXT NOT NULL REFERENCES calibration_families(identity_sha256),
            artifact_id TEXT NOT NULL CHECK(length(artifact_id) = 32 AND artifact_id NOT GLOB '*[^0-9a-f]*'),
            PRIMARY KEY(identity_sha256, artifact_id)) STRICT;
        CREATE TABLE calibration_family_activations(
            idempotency_key TEXT NOT NULL PRIMARY KEY CHECK(length(idempotency_key) BETWEEN 1 AND 128),
            command_kind TEXT NOT NULL CHECK(command_kind IN ('activate', 'deactivate')),
            payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64 AND payload_sha256 NOT GLOB '*[^0-9A-F]*'),
            agent_id TEXT NOT NULL CHECK(length(agent_id) BETWEEN 1 AND 128),
            rig_id TEXT NOT NULL CHECK(length(rig_id) BETWEEN 1 AND 128),
            from_identity_sha256 TEXT REFERENCES calibration_families(identity_sha256),
            to_identity_sha256 TEXT REFERENCES calibration_families(identity_sha256),
            actor TEXT NOT NULL CHECK(length(actor) BETWEEN 1 AND 128),
            reason TEXT CHECK(reason IS NULL OR length(reason) <= 512),
            recorded_unix_ms INTEGER NOT NULL,
            CHECK((command_kind = 'activate' AND to_identity_sha256 IS NOT NULL) OR
                  (command_kind = 'deactivate' AND to_identity_sha256 IS NULL))) STRICT;
        CREATE INDEX calibration_family_activations_target ON calibration_family_activations(agent_id, rig_id, recorded_unix_ms);
        CREATE TABLE calibration_family_active(
            agent_id TEXT NOT NULL,
            rig_id TEXT NOT NULL,
            identity_sha256 TEXT NOT NULL,
            activated_unix_ms INTEGER NOT NULL,
            PRIMARY KEY(agent_id, rig_id),
            FOREIGN KEY(identity_sha256, agent_id, rig_id) REFERENCES calibration_families(identity_sha256, agent_id, rig_id)) STRICT;
        """;

    private readonly string _rootDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SqliteCalibrationFamilyStore> _logger;

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Dispose drains the gate so late calls observe owner disposal.")]
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PhysicalRoot? _root;
    private FileStream? _ownership;
    private bool _initialized;
    private bool _disposed;

    public SqliteCalibrationFamilyStore(
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider,
        ILogger<SqliteCalibrationFamilyStore> logger)
        : this(
            Path.Combine(Path.GetFullPath((options ?? throw new ArgumentNullException(nameof(options))).Value.RawIngressRoot), DirectoryName),
            timeProvider,
            logger)
    {
    }

    internal SqliteCalibrationFamilyStore(
        string rootDirectory,
        TimeProvider timeProvider,
        ILogger<SqliteCalibrationFamilyStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The calibration-family root must be absolute.", nameof(rootDirectory));
        }
        _rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
        => _ = await ExecuteAsync(static (_, _) => 0, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Stores a family by its content identity. Storing the same family again returns the record already stored and
    /// writes nothing. An invalid family is refused by the contract serializer before anything is written.
    /// </summary>
    public ValueTask<CalibrationFamilyRecord> StoreAsync(CalibrationFamilyV1 family, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(family);
        var canonical = CalibrationFamilyContractJson.Serialize(family);
        var identity = Convert.ToHexString(SHA256.HashData(canonical));
        var parsed = Verify(identity, canonical);
        return ExecuteAsync((connection, token) =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            if (ReadFamily(connection, transaction, identity) is { } existing)
            {
                transaction.Commit();
                return existing;
            }
            var now = Now();
            Execute(connection, transaction, """
                INSERT INTO calibration_families(identity_sha256, family_id, agent_id, rig_id, canonical_json, created_unix_ms)
                VALUES($identity, $family, $agent, $rig, $json, $now);
                """,
                ("$identity", identity), ("$family", parsed.FamilyId), ("$agent", parsed.AgentId), ("$rig", parsed.RigId),
                ("$json", canonical), ("$now", now.ToUnixTimeMilliseconds()));
            foreach (var artifactId in MasterArtifactIds(parsed))
            {
                Execute(connection, transaction,
                    "INSERT INTO calibration_family_masters(identity_sha256, artifact_id) VALUES($identity, $artifact);",
                    ("$identity", identity), ("$artifact", artifactId));
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            FamilyStored(_logger, identity, parsed.AgentId, parsed.RigId);
            return new CalibrationFamilyRecord(identity, parsed, now);
        }, cancellationToken);
    }

    /// <summary>The stored family with <paramref name="identitySha256"/>, verified against its identity, or null.</summary>
    public ValueTask<CalibrationFamilyRecord?> GetAsync(string identitySha256, CancellationToken cancellationToken)
    {
        var identity = NormalizeIdentity(identitySha256, nameof(identitySha256));
        return ExecuteAsync((connection, _) =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var record = ReadFamily(connection, transaction, identity);
            transaction.Commit();
            return record;
        }, cancellationToken);
    }

    /// <summary>The family active for <paramref name="agentId"/> and <paramref name="rigId"/>, or null.</summary>
    public ValueTask<CalibrationFamilyActiveRecord?> GetActiveAsync(string agentId, string rigId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rigId);
        return ExecuteAsync((connection, _) =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            CalibrationFamilyActiveRecord? active = null;
            using (var command = Command(connection, transaction,
                       "SELECT identity_sha256, activated_unix_ms FROM calibration_family_active WHERE agent_id = $agent AND rig_id = $rig;",
                       ("$agent", agentId), ("$rig", rigId)))
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    var identity = reader.GetString(0);
                    var activated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
                    reader.Close();
                    var family = ReadFamily(connection, transaction, identity)
                        ?? throw new InvalidDataException("The active calibration family is missing from the store.");
                    active = new CalibrationFamilyActiveRecord(agentId, rigId, family, activated);
                }
            }
            transaction.Commit();
            return active;
        }, cancellationToken);
    }

    /// <summary>The recorded activation or deactivation with <paramref name="idempotencyKey"/>, or null.</summary>
    public ValueTask<CalibrationFamilyActivationRecord?> GetActivationAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return ExecuteAsync((connection, _) =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var record = ReadActivation(connection, transaction, idempotencyKey);
            transaction.Commit();
            return record;
        }, cancellationToken);
    }

    /// <summary>
    /// The master artifact IDs of every active family, distinct and ordered. One indexed query under this store's own
    /// gate; no family is parsed and no other store's gate is held.
    /// </summary>
    public ValueTask<IReadOnlyList<Guid>> GetActiveMasterArtifactIdsAsync(CancellationToken cancellationToken)
        => ExecuteAsync<IReadOnlyList<Guid>>((connection, _) =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var artifacts = new List<Guid>();
            using (var command = Command(connection, transaction, """
                       SELECT DISTINCT m.artifact_id
                       FROM calibration_family_active a
                       JOIN calibration_family_masters m ON m.identity_sha256 = a.identity_sha256
                       ORDER BY m.artifact_id;
                       """))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    artifacts.Add(Guid.ParseExact(reader.GetString(0), "N"));
                }
            }
            transaction.Commit();
            return artifacts;
        }, cancellationToken);

    /// <summary>
    /// Records one activation or deactivation in a single immediate transaction. A key already recorded returns its
    /// recorded result when the payload, which covers the command kind, is the same and is refused when it differs. An activation of a family the
    /// store does not hold, or one <paramref name="fits"/> refuses, writes nothing.
    /// </summary>
    internal ValueTask<CalibrationFamilyActivationResult> ChangeActivationAsync(
        string commandKind,
        string idempotencyKey,
        string payloadSha256,
        string agentId,
        string rigId,
        string? toIdentitySha256,
        string actor,
        string? reason,
        Func<CalibrationFamilyV1, string?> fits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fits);
        if (commandKind is not (ActivateCommand or DeactivateCommand) ||
            (commandKind == ActivateCommand) != (toIdentitySha256 is not null))
        {
            throw new ArgumentException("An activation names a family and a deactivation names none.", nameof(commandKind));
        }
        return ExecuteAsync((connection, token) =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            if (ReadActivation(connection, transaction, idempotencyKey) is { } recorded)
            {
                transaction.Commit();
                // The payload hash covers the command kind, so a key reused for the other command is refused too.
                return string.Equals(recorded.PayloadSha256, payloadSha256, StringComparison.Ordinal)
                    ? new CalibrationFamilyActivationResult(recorded, ResultReason(recorded.CommandKind), Replayed: true)
                    : CalibrationFamilyActivationResult.Refused(CalibrationFamilyActivationReasonCodes.IdempotencyConflict);
            }
            if (toIdentitySha256 is not null)
            {
                if (ReadFamily(connection, transaction, toIdentitySha256) is not { } target)
                {
                    return CalibrationFamilyActivationResult.Refused(CalibrationFamilyActivationReasonCodes.UnknownFamily);
                }
                if (fits(target.Family) is { } refusal)
                {
                    return CalibrationFamilyActivationResult.Refused(refusal);
                }
                VerifyMasters(connection, transaction, target);
            }
            var from = Scalar(connection, transaction,
                "SELECT identity_sha256 FROM calibration_family_active WHERE agent_id = $agent AND rig_id = $rig;",
                ("$agent", agentId), ("$rig", rigId)) as string;
            var now = Now();
            if (toIdentitySha256 is null)
            {
                Execute(connection, transaction,
                    "DELETE FROM calibration_family_active WHERE agent_id = $agent AND rig_id = $rig;",
                    ("$agent", agentId), ("$rig", rigId));
            }
            else
            {
                Execute(connection, transaction, """
                    INSERT INTO calibration_family_active(agent_id, rig_id, identity_sha256, activated_unix_ms)
                    VALUES($agent, $rig, $identity, $now)
                    ON CONFLICT(agent_id, rig_id) DO UPDATE SET identity_sha256 = excluded.identity_sha256,
                                                              activated_unix_ms = excluded.activated_unix_ms;
                    """,
                    ("$agent", agentId), ("$rig", rigId), ("$identity", toIdentitySha256), ("$now", now.ToUnixTimeMilliseconds()));
            }
            Execute(connection, transaction, """
                INSERT INTO calibration_family_activations(
                    idempotency_key, command_kind, payload_sha256, agent_id, rig_id,
                    from_identity_sha256, to_identity_sha256, actor, reason, recorded_unix_ms)
                VALUES($key, $command, $payload, $agent, $rig, $from, $to, $actor, $reason, $now);
                """,
                ("$key", idempotencyKey), ("$command", commandKind), ("$payload", payloadSha256), ("$agent", agentId),
                ("$rig", rigId), ("$from", from), ("$to", toIdentitySha256), ("$actor", actor), ("$reason", reason),
                ("$now", now.ToUnixTimeMilliseconds()));
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            var record = new CalibrationFamilyActivationRecord(
                idempotencyKey, commandKind, payloadSha256, agentId, rigId, from, toIdentitySha256, actor, reason, now);
            return new CalibrationFamilyActivationResult(record, ResultReason(commandKind), Replayed: false);
        }, cancellationToken);
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _ownership?.Dispose();
            _ownership = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string NormalizeIdentity(string? identitySha256, string parameterName)
        => identitySha256 is { Length: 64 } && identitySha256.All(Uri.IsHexDigit)
            ? identitySha256.ToUpperInvariant()
            : throw new ArgumentException("A calibration-family identity is a 64-character hexadecimal SHA-256.", parameterName);

    private static string ResultReason(string commandKind)
        => commandKind == ActivateCommand
            ? CalibrationFamilyActivationReasonCodes.Activated
            : CalibrationFamilyActivationReasonCodes.Deactivated;

    private static IEnumerable<string> MasterArtifactIds(CalibrationFamilyV1 family)
        => family.Members
            .SelectMany(static member => member.Masters)
            .Select(static master => master.ArtifactId.ToString("N"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Parses canonical bytes and proves they are the family <paramref name="identitySha256"/> names. Any difference
    /// means the stored bytes are not the family that was approved, so it is corruption, never a fallback.
    /// </summary>
    private static CalibrationFamilyV1 Verify(string identitySha256, byte[] canonical)
    {
        var (family, validation) = CalibrationFamilyContractJson.Parse(canonical);
        if (family is null || !validation.IsValid ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(canonical)), identitySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A stored calibration family does not parse to the identity it is keyed by.");
        }
        return family;
    }

    private static CalibrationFamilyRecord? ReadFamily(SqliteConnection connection, SqliteTransaction transaction, string identity)
    {
        using var command = Command(connection, transaction, """
            SELECT family_id, agent_id, rig_id, canonical_json, created_unix_ms
            FROM calibration_families WHERE identity_sha256 = $identity;
            """, ("$identity", identity));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        var family = Verify(identity, reader.GetFieldValue<byte[]>(3));
        if (!string.Equals(family.FamilyId, reader.GetString(0), StringComparison.Ordinal) ||
            !string.Equals(family.AgentId, reader.GetString(1), StringComparison.Ordinal) ||
            !string.Equals(family.RigId, reader.GetString(2), StringComparison.Ordinal))
        {
            throw new InvalidDataException("A stored calibration family's key columns differ from its canonical contract.");
        }
        return new CalibrationFamilyRecord(identity, family, DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)));
    }

    /// <summary>
    /// The retention hold reads <c>calibration_family_masters</c>, so before a family becomes active its rows must be
    /// exactly the family's masters. A difference is corruption: activating would leave a master unheld.
    /// </summary>
    private static void VerifyMasters(SqliteConnection connection, SqliteTransaction transaction, CalibrationFamilyRecord family)
    {
        var stored = new List<string>();
        using (var command = Command(connection, transaction,
                   "SELECT artifact_id FROM calibration_family_masters WHERE identity_sha256 = $identity ORDER BY artifact_id;",
                   ("$identity", family.IdentitySha256)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                stored.Add(reader.GetString(0));
            }
        }
        if (!stored.SequenceEqual(MasterArtifactIds(family.Family), StringComparer.Ordinal))
        {
            throw new InvalidDataException("A stored calibration family's master index differs from its canonical contract.");
        }
    }

    private static CalibrationFamilyActivationRecord? ReadActivation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey)
    {
        using var command = Command(connection, transaction, """
            SELECT command_kind, payload_sha256, agent_id, rig_id, from_identity_sha256, to_identity_sha256,
                   actor, reason, recorded_unix_ms
            FROM calibration_family_activations WHERE idempotency_key = $key;
            """, ("$key", idempotencyKey));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new CalibrationFamilyActivationRecord(
                idempotencyKey,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)))
            : null;
    }

    private async ValueTask<T> ExecuteAsync<T>(
        Func<SqliteConnection, CancellationToken, T> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ownership is null)
            {
                AcquireRoot();
            }
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                if (!_initialized)
                {
                    VerifySchema(connection);
                    _initialized = true;
                    StoreInitialized(_logger, SchemaVersion);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return action(connection, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void AcquireRoot()
    {
        Directory.CreateDirectory(_rootDirectory);
        var root = PhysicalRoot.Open(_rootDirectory);
        try
        {
            _ownership = new FileStream(root.Resolve(LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new IOException("Another calibration-family store owns the root.", exception);
        }
        _root = root;
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var root = _root ?? throw new InvalidOperationException("The calibration-family root is not owned.");
        var path = root.Resolve(DatabaseFileName);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            root.Verify(path + suffix, "calibration-family-open");
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await SqliteConnectionConfigurationGate.OpenAndConfigureAsync(connection, static (configured, _) =>
        {
            Execute(configured, null, "PRAGMA busy_timeout = 5000;");
            Execute(configured, null, "PRAGMA synchronous = FULL;");
            Execute(configured, null, "PRAGMA foreign_keys = ON;");
            if (!string.Equals(Scalar(configured, null, "PRAGMA journal_mode = WAL;") as string, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The calibration-family store could not enable WAL.");
            }
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Creates the schema in an empty file, or proves an existing one is exactly this release's: its user version,
    /// every object's definition, SQLite integrity and foreign keys. Anything else fails closed.
    /// </summary>
    private static void VerifySchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var version = Convert.ToInt64(Scalar(connection, transaction, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version == 0)
        {
            if (Convert.ToInt64(Scalar(connection, transaction,
                    "SELECT count(*) FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT GLOB 'sqlite_*';"),
                    CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidDataException("The calibration-family store contains an unversioned schema.");
            }
            Execute(connection, transaction, Schema);
            Execute(connection, transaction, FormattableString.Invariant($"PRAGMA user_version = {SchemaVersion};"));
        }
        else if (version != SchemaVersion)
        {
            throw new InvalidDataException("Unsupported calibration-family schema version; upgrade forward with a compatible binary.");
        }
        using var canonical = new SqliteConnection("Data Source=:memory:");
        canonical.Open();
        Execute(canonical, null, Schema);
        var expected = Definitions(canonical, null);
        var actual = Definitions(connection, transaction);
        if (expected.Count != actual.Count ||
            expected.Any(pair => !actual.TryGetValue(pair.Key, out var sql) || !string.Equals(sql, pair.Value, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The calibration-family schema differs from its canonical version.");
        }
        if (!Equals(Scalar(connection, transaction, "PRAGMA integrity_check;"), "ok") ||
            Convert.ToInt64(Scalar(connection, transaction, "SELECT count(*) FROM pragma_foreign_key_check;"), CultureInfo.InvariantCulture) != 0)
        {
            throw new InvalidDataException("Calibration-family SQLite integrity validation failed.");
        }
        transaction.Commit();
    }

    private static Dictionary<string, string> Definitions(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT name, type, tbl_name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT GLOB 'sqlite_*' ORDER BY name;");
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result.Add(
                reader.GetString(0),
                reader.GetString(1) + ":" + reader.GetString(2) + ":" + SqliteRawCaptureJournal.NormalizeSchemaSql(reader.GetString(3)));
        }
        return result;
    }

    private DateTimeOffset Now()
        => DateTimeOffset.FromUnixTimeMilliseconds(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only private constant SQL text is passed; values are parameterized.")]
    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command;
    }

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    [LoggerMessage(EventId = 7900, Level = LogLevel.Information,
        Message = "Calibration-family SQLite store initialized at schema {SchemaVersion}.")]
    private static partial void StoreInitialized(ILogger logger, int schemaVersion);

    [LoggerMessage(EventId = 7901, Level = LogLevel.Information,
        Message = "Calibration family {IdentitySha256} stored for agent {AgentId} rig {RigId}.")]
    private static partial void FamilyStored(ILogger logger, string identitySha256, string agentId, string rigId);
}
