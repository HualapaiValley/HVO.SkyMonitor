using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Sqlite;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>Identity of one application-owned SQLite format: application ID, user version and format row.</summary>
internal sealed record SiloSqliteFormat(string Format, int ApplicationId, int UserVersion, string SchemaSql, string Subject)
{
    private readonly Lazy<IReadOnlyList<string>> _definitions = new(() => SiloSqlite.CanonicalDefinitions(SchemaSql));

    public IReadOnlyList<string> Definitions => _definitions.Value;
}

internal static class SiloSqlite
{
    private const int BusyTimeoutSeconds = 5;

    /// <summary>
    /// Refuse a file of another or newer format by reading its header bytes only. Nothing is opened for write, so a
    /// refused file is left byte-for-byte unchanged (#1967 acceptance 5).
    /// </summary>
    public static void RefuseUnsupportedHeader(string path, SiloSqliteFormat format)
    {
        if (!File.Exists(path))
        {
            return;
        }
        var header = new byte[100];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (stream.Length == 0)
            {
                return;
            }
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }
        if (read < header.Length || !header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
        {
            throw new SiloFormatRefusedException(format.Subject, "not an SQLite database", format.Format);
        }
        var applicationId = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(68, 4));
        var userVersion = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(60, 4));
        if ((applicationId, userVersion) is (0, 0))
        {
            // Unversioned: accepted only when it holds no schema, which the in-transaction check proves.
            return;
        }
        if (applicationId != format.ApplicationId || userVersion != format.UserVersion)
        {
            throw new SiloFormatRefusedException(format.Subject,
                string.Create(CultureInfo.InvariantCulture, $"application_id 0x{applicationId:X8}, user_version {userVersion}"),
                format.Format);
        }
    }

    private static readonly string BusyTimeoutPragma =
        string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {BusyTimeoutSeconds * 1000};");

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant PRAGMA statements with a constant numeric bound are executed.")]
    public static async ValueTask<SqliteConnection> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = BusyTimeoutSeconds
        }.ToString());
        await SqliteConnectionConfigurationGate.OpenAndConfigureAsync(connection, async (configured, token) =>
        {
            // Connection-level settings only, one statement per command: none writes the database before its format is verified.
            using var command = configured.CreateCommand();
            command.CommandText = BusyTimeoutPragma;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "PRAGMA synchronous = FULL;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Create the schema in an empty database, or prove an existing one is exactly this format and binding, then enable
    /// WAL. Verification precedes every write, so an unsupported database is refused unchanged.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant schema and PRAGMA statements are executed.")]
    public static async ValueTask EnsureSchemaAsync(
        SqliteConnection connection, SiloSqliteFormat format, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        using (var transaction = BeginImmediate(connection))
        {
            if (!await ValidateAsync(connection, transaction, format, metadata, cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAsync(connection, transaction, format.SchemaSql, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, string.Create(CultureInfo.InvariantCulture,
                    $"PRAGMA application_id = {format.ApplicationId};"), cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, string.Create(CultureInfo.InvariantCulture,
                    $"PRAGMA user_version = {format.UserVersion};"), cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, "INSERT INTO silo_meta(key, value) VALUES('format', $value);",
                    cancellationToken, ("$value", format.Format)).ConfigureAwait(false);
                foreach (var (key, value) in metadata)
                {
                    await ExecuteAsync(connection, transaction, "INSERT INTO silo_meta(key, value) VALUES($key, $value);",
                        cancellationToken, ("$key", key), ("$value", value)).ConfigureAwait(false);
                }
                if (!await ValidateAsync(connection, transaction, format, metadata, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException(format.Subject + " did not validate after creation.");
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        string? mode = null;
        await SqliteConnectionConfigurationGate.RunAsync(async () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            mode = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(format.Subject + " could not enable write-ahead logging.");
        }
    }

    /// <summary>
    /// Prove a database is exactly this format and binding. False means it is empty and may be created; any other
    /// content is refused. It reads through the connection, so committed pages still in a WAL are what it judges.
    /// </summary>
    private static async ValueTask<bool> ValidateAsync(
        SqliteConnection connection, SqliteTransaction transaction, SiloSqliteFormat format, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var applicationId = await ScalarLongAsync(connection, transaction, "PRAGMA application_id;", cancellationToken).ConfigureAwait(false);
        var userVersion = await ScalarLongAsync(connection, transaction, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
        if ((applicationId, userVersion) == (0, 0))
        {
            return await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';", cancellationToken).ConfigureAwait(false) == 0
                ? false
                : throw new SiloFormatRefusedException(format.Subject, "unversioned schema objects", format.Format);
        }
        if (applicationId != format.ApplicationId || userVersion != format.UserVersion)
        {
            throw new SiloFormatRefusedException(format.Subject,
                string.Create(CultureInfo.InvariantCulture, $"application_id 0x{applicationId:X8}, user_version {userVersion}"),
                format.Format);
        }
        var actual = await ReadDefinitionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!actual.SequenceEqual(format.Definitions, StringComparer.Ordinal))
        {
            throw new SiloFormatRefusedException(format.Subject, "a drifted schema", format.Format);
        }
        var stored = await ReadMetadataAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!stored.TryGetValue("format", out var storedFormat) || !string.Equals(storedFormat, format.Format, StringComparison.Ordinal))
        {
            throw new SiloFormatRefusedException(format.Subject, storedFormat, format.Format);
        }
        foreach (var (key, value) in metadata)
        {
            if (!stored.TryGetValue(key, out var existing) || !string.Equals(existing, value, StringComparison.Ordinal))
            {
                throw new SiloConflictException("silo.binding-mismatch", string.Create(CultureInfo.InvariantCulture,
                    $"{format.Subject} belongs to another instance or storage binding ({key}); it was left unchanged."));
            }
        }
        return true;
    }

    /// <summary>
    /// Judge an existing database without touching it: its main file and its WAL, which may hold committed pages the
    /// main file's header does not yet show, are copied into a scratch directory and the copy is validated. Returns false
    /// when there is no database or it is empty; refuses any other format or binding. The original is never opened.
    /// </summary>
    public static async ValueTask<bool> InspectAsync(
        string path, SiloSqliteFormat format, IReadOnlyDictionary<string, string> metadata, string scratchDirectory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        RefuseUnsupportedHeader(path, format);
        Directory.CreateDirectory(scratchDirectory);
        var copy = Path.Combine(scratchDirectory, "inspect-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            File.Copy(path, copy);
            if (File.Exists(path + "-wal"))
            {
                File.Copy(path + "-wal", copy + "-wal");
            }
            var connection = await OpenAsync(copy, cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                using var transaction = BeginImmediate(connection);
                return await ValidateAsync(connection, transaction, format, metadata, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                File.Delete(copy + suffix);
            }
        }
    }

    internal static IReadOnlyList<string> CanonicalDefinitions(string schemaSql)
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        using (var command = CreateCommand(connection, null, schemaSql))
        {
            command.ExecuteNonQuery();
        }
        using var definitions = CreateCommand(connection, null, DefinitionsSql);
        using var reader = definitions.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(Definition(reader));
        }
        return result;
    }

    private const string DefinitionsSql = """
        SELECT type, name, tbl_name, sql FROM sqlite_schema
        WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type, name;
        """;

    private static async ValueTask<IReadOnlyList<string>> ReadDefinitionsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, DefinitionsSql);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Definition(reader));
        }
        return result;
    }

    private static string Definition(SqliteDataReader reader) => string.Join('\u001f',
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));

    private static async ValueTask<Dictionary<string, string>> ReadMetadataAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, "SELECT key, value FROM silo_meta;");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.")]
    public static SqliteTransaction BeginImmediate(SqliteConnection connection) => connection.BeginTransaction(deferred: false);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant statement text is passed; values remain parameterized.")]
    public static SqliteCommand CreateCommand(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
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

    public static async ValueTask<int> ExecuteAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<long> ScalarLongAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static async ValueTask<string?> ScalarStringAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? NullableLong(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static long Ticks(DateTimeOffset value) => value.UtcTicks;

    public static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);
}
